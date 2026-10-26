using ERPSystem.Application.Exceptions;
using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// يحل الحسابات الافتراضية للترحيل حسب بنية شجرة كل قاعدة بيانات (ديناميكيًا).
///
/// لا يوجد Hard-code عالمي (مثل 1100 → 11001):
///  - الحساب الجذري/التصنيفي (ParentAccountId = null) لا يُستخدم ولا يُهبط منه — يُرفض صريحًا.
///  - إذا كان كود الحساب الافتراضي ورقةً نشطة (Leaf بلا أبناء) → يُستخدم كما هو.
///  - إذا كان رئيسيًا (له أبناء) → نهبط إلى أول حساب متحرك (Leaf) <b>نشط</b> تحته
///    (أصغر كود، بشكل حتمي) حتى لا تقع الحركة على Parent.
///  - إن لم يوجد أي حساب متحرك نشط صالح ⇒ رفض العملية برسالة واضحة
///    (لا اختيار صامت لحساب آخر، ولا استخدام حساب غير نشط).
///
/// أمثلة فعلية:
///  - Production: 1100 ورقة نشطة (تحت الجذر 1) → 1100.
///  - Commercial: 1100 رئيسي (تحته 11001 ورقة نشطة) → 11001.
/// </summary>
public static class PostingAccountResolver
{
    /// <summary>
    /// يعيد الحساب المتحرك (Leaf) النشط المرتبط بالكود النظامي، أو يرمي إن لم يوجد،
    /// أو كان جذريًا، أو كان غير نشط، أو لم يوجد حساب متحرك نشط تحته.
    /// </summary>
    public static async Task<Account> ResolveAsync(DbContext context, string code)
    {
        // شجرة الحسابات صغيرة بطبيعتها؛ نجلب الكل مرة واحدة (احترامًا للفلتر العام !IsDeleted).
        var accounts = await context.Set<Account>()
            .Where(a => !a.IsDeleted)
            .ToListAsync();

        var parentIds = accounts
            .Where(a => a.ParentAccountId != null)
            .Select(a => a.ParentAccountId!.Value)
            .ToHashSet();

        var account = accounts.FirstOrDefault(a => a.Code == code)
            ?? throw new InvalidOperationException($"الحساب النظامي '{code}' غير موجود في شجرة الحسابات.");

        // حساب جذري/تصنيفي (بلا أب) ليس هدفًا للترحيل إطلاقًا — لا استخدامًا مباشرًا ولا هبوطًا منه.
        if (MovementAccountValidator.IsRootAccount(account))
            throw new RootAccountMovementException();

        // ورقة؟ → تُستخدم فقط إن كانت نشطة (لا تبديل صامت إلى حساب آخر).
        if (!parentIds.Contains(account.Id))
        {
            if (!account.IsActive)
                throw new InactiveAccountMovementException();
            return account;
        }

        // رئيسي؟ → الهبوط إلى أول ورقة (Leaf) نشطة بحسب ترتيب الكود.
        return FindFirstActiveLeaf(accounts, account.Id, parentIds)
            ?? throw new InvalidOperationException(
                $"الحساب النظامي '{code}' حساب رئيسي بدون حساب متحرك (Leaf) نشط تحته. " +
                "يرجى تفعيل حساب فرعي صالح أو إضافته.");
    }

    /// <summary>أول حساب متحرك (Leaf) نشط تحت الحساب المعطى — بحسب ترتيب الكود (حتمي).</summary>
    private static Account? FindFirstActiveLeaf(List<Account> all, Guid parentId, HashSet<Guid> parentIds)
    {
        var children = all
            .Where(a => a.ParentAccountId == parentId && a.IsActive)
            .OrderBy(a => a.Code, StringComparer.Ordinal)
            .ToList();

        foreach (var child in children)
        {
            if (!parentIds.Contains(child.Id))
                return child; // ورقة نشطة

            var deeper = FindFirstActiveLeaf(all, child.Id, parentIds);
            if (deeper is not null)
                return deeper;
        }

        return null;
    }
}
