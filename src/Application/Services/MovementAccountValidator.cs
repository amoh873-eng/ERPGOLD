using ERPSystem.Application.Exceptions;
using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// القاعدة المحاسبية المركزية: الحسابات المتحركة (Movable) النشطة فقط تقبل الحركات المالية.
///
/// تعريف الحساب الصالح للحركة (Movable) المعتمد:
///  1) موجود (وإلا رُفض في المتصل).
///  2) نشط — <c>IsActive == true</c>.
///  3) غير محذوف — <c>IsDeleted == false</c>.
///  4) ليس حساباً جذرياً/تصنيفياً — أي <c>ParentAccountId != null</c>.
///     (الحسابات الجذرية هي رؤوس التصنيفات: 1، 2، 3، 4، 5 — ولا تُرحَّل إليها حركات مباشرة،
///      حتى لو لم يكن لها أبناء بعد، مثل «3 حقوق الملكية».)
///  5) لا يحتوي على أبناء غير محذوفين (ليس Parent تجميعياً).
///
/// التعريف مشتق من بنية الشجرة الفعلية + حالة الحساب — لا يوجد عمود IsParent/IsRoot مخزَّن.
///
/// هذه النقطة تُستخدم في JournalEntryService.PrepareEntryAsync (نقطة الاختناق الواحدة
/// لكل الترحيلات: اليدوي + كل القيود التلقائية)، وكذلك في الأرصدة الافتتاحية وفئات المصاريف.
/// </summary>
public static class MovementAccountValidator
{
    /// <summary>هل الحساب جذري/تصنيفي؟ (بلا أب — رأس تصنيف ولا يقبل الحركة المباشرة)</summary>
    public static bool IsRootAccount(Account account) => account.ParentAccountId is null;

    /// <summary>
    /// يعيد مُعرّفات الحسابات (من القائمة المعطاة) التي هي حسابات رئيسية (لها أبناء غير محذوفين).
    /// قائمة فارغة = لا يوجد آباء بين الحسابات المعطاة.
    /// </summary>
    public static async Task<List<Guid>> FindParentAccountIdsAsync(
        DbContext context, IReadOnlyCollection<Guid> accountIds)
    {
        if (accountIds is null || accountIds.Count == 0)
            return new List<Guid>();

        var ids = accountIds.ToList();
        return await context.Set<Account>()
            .Where(a => a.ParentAccountId != null
                        && ids.Contains(a.ParentAccountId.Value)
                        && !a.IsDeleted)
            .Select(a => a.ParentAccountId!.Value)
            .Distinct()
            .ToListAsync();
    }

    /// <summary>
    /// يتحقق أن كل الحسابات المعطاة صالحة للحركة (Movable نشطة)؛ وإلا:
    ///  - حساب محذوف (خارج الفلتر العام) ⇒ رفض صريح
    ///  - حساب له أبناء (Parent تجميعي) ⇒ <see cref="ParentAccountMovementException"/>
    ///  - حساب جذري/تصنيفي (بلا أب) ⇒ <see cref="RootAccountMovementException"/>
    ///  - حساب غير نشط (IsActive = false) ⇒ <see cref="InactiveAccountMovementException"/>
    /// </summary>
    public static async Task EnsureMovementAccountsAsync(
        DbContext context, IReadOnlyCollection<Guid> accountIds)
    {
        if (accountIds is null || accountIds.Count == 0)
            return;

        var ids = accountIds.ToList();

        // (0) حساب محذوف ناعماً — خارج الفلتر العام، لذا نكشفه صراحةً.
        if (await context.Set<Account>().IgnoreQueryFilters()
                .AnyAsync(a => a.IsDeleted && ids.Contains(a.Id)))
            throw new InvalidOperationException(
                "أحد الحسابات في الحركة محذوف — لا يمكن استخدامه في حركة مالية.");

        // (1) حساب رئيسي تجميعي (له أبناء) — السلوك القائم.
        var parents = await FindParentAccountIdsAsync(context, ids);
        if (parents.Count > 0)
            throw new ParentAccountMovementException();

        // (2) حساب جذري/تصنيفي (بلا أب) — مرفوض للحركة المباشرة حتى لو لم تكن له أبناء.
        if (await context.Set<Account>().AnyAsync(a => a.ParentAccountId == null && ids.Contains(a.Id)))
            throw new RootAccountMovementException();

        // (3) حساب غير نشط — مرفوض للحركة الجديدة (لا تبديل صامت إلى حساب آخر).
        if (await context.Set<Account>().AnyAsync(a => !a.IsActive && ids.Contains(a.Id)))
            throw new InactiveAccountMovementException();
    }

    /// <summary>
    /// نفس التحقق لحساب واحد — نقطة موحّدة لشاشات/خدمات تختار حساباً واحداً
    /// (فئات المصاريف، الأرصدة الافتتاحية، أي مسار يربط مستنداً بحساب).
    /// </summary>
    public static Task EnsureMovableAccountAsync(DbContext context, Guid accountId)
        => EnsureMovementAccountsAsync(context, new[] { accountId });

    /// <summary>هل الحساب رئيسي (له أبناء غير محذوفين)؟</summary>
    public static async Task<bool> IsParentAsync(DbContext context, Guid accountId)
        => await context.Set<Account>()
            .AnyAsync(a => a.ParentAccountId == accountId && !a.IsDeleted);
}

