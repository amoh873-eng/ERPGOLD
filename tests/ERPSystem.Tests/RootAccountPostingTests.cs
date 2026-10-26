using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Phase B.3 — منع الحركة المباشرة على الحسابات الجذرية/التصنيفية (ParentAccountId = null)
/// على PostgreSQL حقيقي معزول (شكل Production: الجذور 1،2،3،4،5).
///
/// الحساب المتحرك (Movable) = ليس جذريًا + لا أبناء له. الحساب «3 حقوق الملكية» جذري بلا أبناء
/// ⇒ كان يُقبل قبل الإصلاح (Leaf بالتعريف القديم) واليوم يُرفض مركزيًا في الـBackend.
/// </summary>
public class RootAccountPostingTests : IClassFixture<AccountingPgFixture>
{
    /// <summary>رؤوس التصنيفات الجذرية في شجرة الحسابات المزروعة (لا أب لها).</summary>
    private static readonly string[] RootCodes = { "1", "2", "3", "4", "5" };

    private readonly AccountingPgFixture _pg;

    public RootAccountPostingTests(AccountingPgFixture pg) => _pg = pg;

    private static async Task<int> CountEntriesAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    private static async Task<decimal> BalanceAsync(AccountingPgFixture pg, string code)
    {
        await using var ctx = pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code).Select(a => a.CurrentBalance).FirstAsync();
    }

    // ───────── الحساب 3 (جذري بلا أبناء) → رفض مركزي ─────────

    [LivePgFact]
    public async Task Root_account_3_is_rejected_even_without_children()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var id = await _pg.FindAccountIdAsync("3");

        await using var ctx = _pg.NewContext();

        // الحساب 3 بلا أبناء فعلاً (تأكيد الشكل قبل الاستنتاج)
        Assert.False(await MovementAccountValidator.IsParentAsync(ctx, id));

        var ex = await Assert.ThrowsAsync<RootAccountMovementException>(
            () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { id }));

        Assert.Equal(RootAccountMovementException.Key, ex.ResourceKey);
    }

    // ───────── الجذور 1،2،4،5 → رفض ─────────

    [LivePgFact]
    public async Task Root_accounts_1_2_4_5_are_rejected()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        foreach (var code in new[] { "1", "2", "4", "5" })
        {
            var id = await _pg.FindAccountIdAsync(code);
            await using var ctx = _pg.NewContext();

            // هذه الجذور لها أبناء في الشجرة الحالية ⇒ تُرفض كحسابات رئيسية تجميعية.
            await Assert.ThrowsAsync<ParentAccountMovementException>(
                () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { id }));
        }
    }

    // ───────── Production: 1100 ورقة (تحت الجذر 1) → قبول ─────────

    [LivePgFact]
    public async Task Production_leaf_1100_is_accepted()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var id = await _pg.FindAccountIdAsync("1100");

        await using var ctx = _pg.NewContext();
        Assert.False(await MovementAccountValidator.IsParentAsync(ctx, id));
        await MovementAccountValidator.EnsureMovableAccountAsync(ctx, id); // لا يرمي
    }

    // ───────── حساب متحرك عادي 1101 → قبول ─────────

    [LivePgFact]
    public async Task Movable_account_1101_is_accepted()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var id = await _pg.FindAccountIdAsync("1101");

        await using var ctx = _pg.NewContext();
        await MovementAccountValidator.EnsureMovableAccountAsync(ctx, id); // لا يرمي
    }

    // ───────── إرسال حساب جذري مباشرة إلى خدمة الترحيل → رفض بلا أي أثر جزئي ─────────

    [LivePgFact]
    public async Task Posting_a_root_account_is_rejected_without_any_partial_write()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var root3 = await _pg.FindAccountIdAsync("3");
        var cash = await _pg.FindAccountIdAsync("1100");

        var entriesBefore = await CountEntriesAsync(_pg);
        var cashBefore = await BalanceAsync(_pg, "1100");
        var equityBefore = await BalanceAsync(_pg, "3");

        await using (var ctx = _pg.NewContext())
        {
            var journal = new JournalEntryService(ctx);

            var ex = await Assert.ThrowsAsync<RootAccountMovementException>(() =>
                journal.PrepareEntryAsync(
                    JournalEntryType.Manual, DateTime.Today, "B3-ROOT", "محاولة ترحيل إلى حساب جذري",
                    new List<JournalEntryLegInput>
                    {
                        new() { AccountId = cash, DebitAmount = 500m },
                        new() { AccountId = root3, CreditAmount = 500m }   // حساب جذري/تصنيفي
                    }));

            Assert.Equal(RootAccountMovementException.Key, ex.ResourceKey);

            // لا قيد جزئي في السياق ولا تغيير رصيد
            Assert.False(ctx.ChangeTracker.Entries<JournalEntry>().Any());
        }

        // لا أثر في قاعدة البيانات
        Assert.Equal(entriesBefore, await CountEntriesAsync(_pg));
        Assert.Equal(cashBefore, await BalanceAsync(_pg, "1100"));
        Assert.Equal(equityBefore, await BalanceAsync(_pg, "3"));

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ───────── الشكل الفعلي للشجرة: المرفوض = الجذور فقط (لا يُحجب أي حساب شرعي) ─────────

    [LivePgFact]
    public async Task Rejected_accounts_are_exactly_the_root_accounts()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        List<Account> accounts;
        await using (var ctx = _pg.NewContext())
            accounts = await ctx.Set<Account>().AsNoTracking().Where(a => !a.IsDeleted).ToListAsync();

        var parentIds = accounts.Where(a => a.ParentAccountId != null)
            .Select(a => a.ParentAccountId!.Value).ToHashSet();

        var rejectedAccounts = accounts
            .Where(a => a.ParentAccountId is null || parentIds.Contains(a.Id))
            .ToList();

        // 1) كل حساب مرفوض هو جذري (ParentAccountId == null) — أي لا حساب شرعي محجوب
        Assert.All(rejectedAccounts, a => Assert.Null(a.ParentAccountId));

        // 2) المرفوض = الجذور الخمسة بالضبط
        Assert.Equal(
            RootCodes.OrderBy(c => c, StringComparer.Ordinal),
            rejectedAccounts.Select(a => a.Code).OrderBy(c => c, StringComparer.Ordinal));
    }

    // ───────── منسدلة الحركات لا تعرض أي حساب جذري ─────────

    [LivePgFact]
    public async Task Leaf_picker_excludes_all_root_accounts()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        List<AccountDto> leaves;
        await using (var ctx = _pg.NewContext())
            leaves = await new AccountService(ctx).GetLeafAccountsAsync();

        Assert.NotEmpty(leaves);
        foreach (var root in RootCodes)
            Assert.DoesNotContain(leaves, a => a.Code == root);

        Assert.Contains(leaves, a => a.Code == "1100");
        Assert.Contains(leaves, a => a.Code == "1101");
    }

    // ───────── (InMemory) جذري بلا أبناء: نفس الرفض المركزي ─────────

    [Fact]
    public async Task InMemory_root_without_children_is_rejected()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();

        // رأس تصنيف بلا أبناء إطلاقاً (مثل «3» في الشجرة المزروعة).
        var classificationRoot = new Account
        {
            Id = Guid.NewGuid(),
            Code = "9",
            NameAr = "تصنيف بلا أبناء",
            NameEn = "Class without children",
            AccountType = AccountType.Equity,
            NormalBalance = NormalBalance.Credit,
            ParentAccountId = null,   // جذري/تصنيفي
            IsActive = true,
            IsSystem = true,
            CreatedAt = DateTime.UtcNow
        };

        // تصنيف آخر له ابن متحرك — لإثبات أن الابن المتحرك يُقبل.
        var otherRoot = new Account
        {
            Id = Guid.NewGuid(),
            Code = "8",
            NameAr = "تصنيف آخر",
            NameEn = "Another class",
            AccountType = AccountType.Equity,
            NormalBalance = NormalBalance.Credit,
            ParentAccountId = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var leaf = new Account
        {
            Id = Guid.NewGuid(),
            Code = "81",
            NameAr = "حساب فرعي متحرك",
            NameEn = "Movable sub account",
            AccountType = AccountType.Equity,
            NormalBalance = NormalBalance.Credit,
            ParentAccountId = otherRoot.Id, // متحرك
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        ctx.Set<Account>().AddRange(classificationRoot, otherRoot, leaf);
        await ctx.SaveChangesAsync();

        // جذري بلا أبناء → رفض مركزي (RootAccountMovementException)
        await Assert.ThrowsAsync<RootAccountMovementException>(
            () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { classificationRoot.Id }));

        // الورقة المتحركة تحت تصنيف آخر → مقبولة
        await MovementAccountValidator.EnsureMovableAccountAsync(ctx, leaf.Id);
    }
}
