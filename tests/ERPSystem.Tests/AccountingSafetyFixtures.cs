using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Tests;

/// <summary>
/// بيانات اختبار مشتركة لمطالب السلامة المحاسبية (ACCOUNTING SAFETY FIX).
/// تبني شجرتي حسابات مصغّرتين تحاكيان الواقع الفعلي:
///  - Production : 1100 ورقة (Leaf) → يُسمح بالترحيل إليه.
///  - Commercial : 1100 رئيسي (Parent) والحساب المتحرك الصحيح تحته 11001 ورقة → يجب الهبوط إلى 11001.
/// كل قاعدة تستخدم InMemory مستقلة (اسم فريد) فلا تتقاطع الاختبارات.
/// </summary>
internal static class AccountingSafetyFixtures
{
    /// <summary>سياق InMemory جديد ومعزول لكل اختبار.</summary>
    public static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("acct-safety-" + Guid.NewGuid())
            .Options;
        return new AppDbContext(options);
    }

    private static Account NewAccount(string code, AccountType type, Guid? parentId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "حساب " + code,
            NameEn = "Account " + code,
            AccountType = type,
            NormalBalance = type is AccountType.Asset or AccountType.Expense
                ? NormalBalance.Debit
                : NormalBalance.Credit,
            ParentAccountId = parentId,
            IsActive = true,
            IsSystem = true,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Production: الجذر «1» له أبناء (1100، 1101)، و«1100» ورقة (Leaf) بلا أبناء.
    /// الجذر «5» (مصروفات) له ابن (5300).
    /// </summary>
    public static Dictionary<string, Guid> SeedProductionTree(AppDbContext ctx)
    {
        var root1 = NewAccount("1", AccountType.Asset);
        var root5 = NewAccount("5", AccountType.Expense);
        var a1100 = NewAccount("1100", AccountType.Asset, root1.Id); // Leaf in Production
        var a1101 = NewAccount("1101", AccountType.Asset, root1.Id); // Leaf
        var a5300 = NewAccount("5300", AccountType.Expense, root5.Id); // Leaf

        ctx.Set<Account>().AddRange(root1, root5, a1100, a1101, a5300);
        ctx.SaveChanges();

        return new Dictionary<string, Guid>
        {
            ["1"] = root1.Id,
            ["5"] = root5.Id,
            ["1100"] = a1100.Id,
            ["1101"] = a1101.Id,
            ["5300"] = a5300.Id
        };
    }

    /// <summary>
    /// Commercial: «1100» رئيسي (له ابن ورقة «11001»)، الجذور «1» و«5» رئيسية، و«1101» ورقة.
    /// </summary>
    public static Dictionary<string, Guid> SeedCommercialTree(AppDbContext ctx)
    {
        var root1 = NewAccount("1", AccountType.Asset);
        var root5 = NewAccount("5", AccountType.Expense);
        var a1100 = NewAccount("1100", AccountType.Asset, root1.Id);   // Parent in Commercial
        var a11001 = NewAccount("11001", AccountType.Asset, a1100.Id); // Leaf under 1100
        var a1101 = NewAccount("1101", AccountType.Asset, root1.Id);   // Leaf
        var a5300 = NewAccount("5300", AccountType.Expense, root5.Id); // Leaf

        ctx.Set<Account>().AddRange(root1, root5, a1100, a11001, a1101, a5300);
        ctx.SaveChanges();

        return new Dictionary<string, Guid>
        {
            ["1"] = root1.Id,
            ["5"] = root5.Id,
            ["1100"] = a1100.Id,
            ["11001"] = a11001.Id,
            ["1101"] = a1101.Id,
            ["5300"] = a5300.Id
        };
    }
}
