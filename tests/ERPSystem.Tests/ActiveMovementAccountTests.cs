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
/// Phase B.4 — بوابة الحساب المتحرك النشط على PostgreSQL معزول.
///
/// الحساب الصالح للحركة = موجود + IsActive=true + غير محذوف + ليس جذريًا + ليس Parent تجميعيًا.
/// كل حالات الرفض (محذوف/جذري/Parent/غير نشط) تُطبَّق مركزيًا في MovementAccountValidator،
/// أي على كل مسارات القيود (اليدوي + التلقائي) وفئات المصاريف والأرصدة الافتتاحية.
/// </summary>
public class ActiveMovementAccountTests : IClassFixture<AccountingPgFixture>
{
    /// <summary>أكواد الحسابات الافتراضية التي تستخدمها القيود التلقائية.</summary>
    private static readonly string[] DefaultCodes =
    {
        "1100", "1101", "1102", "1105", "1110", "1200", "1205", "1300",
        "2100", "2200", "2300", "2400", "4100", "4110", "5100", "5110", "5200"
    };

    private readonly AccountingPgFixture _pg;

    public ActiveMovementAccountTests(AccountingPgFixture pg) => _pg = pg;

    /// <summary>ينشئ حسابًا فرعيًا (Leaf) تحت الجذر «1» بحالة النشاط المطلوبة.</summary>
    private static async Task<Guid> CreateChildLeafAsync(AccountingPgFixture pg, string code, bool isActive, bool isDeleted = false)
    {
        await using var ctx = pg.NewContext();
        var rootId = await ctx.Set<Account>().Where(a => a.Code == "1").Select(a => a.Id).FirstAsync();

        var account = new Account
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "حساب اختبار " + code,
            NameEn = "Test account " + code,
            AccountType = AccountType.Asset,
            NormalBalance = NormalBalance.Debit,
            ParentAccountId = rootId,
            IsActive = isActive,
            IsDeleted = isDeleted,
            CreatedAt = DateTime.UtcNow
        };

        ctx.Set<Account>().Add(account);
        await ctx.SaveChangesAsync();
        return account.Id;
    }

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

    // ───────── حساب Leaf نشط → قبول ─────────

    [LivePgFact]
    public async Task Active_leaf_account_is_accepted()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var id = await CreateChildLeafAsync(_pg, "T4-ACT", isActive: true);

        await using var ctx = _pg.NewContext();
        await MovementAccountValidator.EnsureMovableAccountAsync(ctx, id); // لا يرمي
    }

    // ───────── حساب Leaf غير نشط → رفض ─────────

    [LivePgFact]
    public async Task Inactive_leaf_account_is_rejected()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var id = await CreateChildLeafAsync(_pg, "T4-INACT", isActive: false);

        await using var ctx = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InactiveAccountMovementException>(
            () => MovementAccountValidator.EnsureMovableAccountAsync(ctx, id));

        Assert.Equal(InactiveAccountMovementException.Key, ex.ResourceKey);
    }

    // ───────── حساب محذوف → رفض ─────────

    [LivePgFact]
    public async Task Deleted_account_is_rejected()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var id = await CreateChildLeafAsync(_pg, "T4-DEL", isActive: true, isDeleted: true);

        await using var ctx = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MovementAccountValidator.EnsureMovableAccountAsync(ctx, id));

        Assert.Contains("محذوف", ex.Message);
    }

    // ───────── Parent تجميعي → رفض ─────────

    [LivePgFact]
    public async Task Parent_account_is_rejected()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var root1 = await _pg.FindAccountIdAsync("1"); // له أبناء في الشجرة المزروعة

        await using var ctx = _pg.NewContext();
        await Assert.ThrowsAsync<ParentAccountMovementException>(
            () => MovementAccountValidator.EnsureMovableAccountAsync(ctx, root1));
    }

    // ───────── Root جذري/تصنيفي → رفض ─────────

    [LivePgFact]
    public async Task Root_account_is_rejected()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var root3 = await _pg.FindAccountIdAsync("3"); // جذري بلا أبناء

        await using var ctx = _pg.NewContext();
        await Assert.ThrowsAsync<RootAccountMovementException>(
            () => MovementAccountValidator.EnsureMovableAccountAsync(ctx, root3));
    }

    // ───────── الحسابات الافتراضية لكل نسخة → تُحلّ إلى Leaf نشط صالح ─────────

    [LivePgFact]
    public async Task Default_accounts_resolve_to_an_active_leaf()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();

        var existing = await ctx.Set<Account>().AsNoTracking()
            .Where(a => DefaultCodes.Contains(a.Code))
            .Select(a => a.Code)
            .ToListAsync();

        Assert.NotEmpty(existing);

        foreach (var code in existing)
        {
            var resolved = await PostingAccountResolver.ResolveAsync(ctx, code);

            Assert.True(resolved.IsActive, $"الحساب الافتراضي '{code}' محلول إلى حساب غير نشط.");
            Assert.NotNull(resolved.ParentAccountId);
            Assert.False(
                await MovementAccountValidator.IsParentAsync(ctx, resolved.Id),
                $"الحساب الافتراضي '{code}' محلول إلى حساب رئيسي تجميعي.");

            // يمرّ البوابة المركزية بلا استثناء (Leaf نشط صالح)
            await MovementAccountValidator.EnsureMovableAccountAsync(ctx, resolved.Id);
        }
    }

    // ───────── المنسدلة تستبعد الحسابات غير النشطة ─────────

    [LivePgFact]
    public async Task Inactive_leaf_is_excluded_from_the_picker()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await CreateChildLeafAsync(_pg, "T4-PICK", isActive: false);

        await using var ctx = _pg.NewContext();
        var leaves = await new AccountService(ctx).GetLeafAccountsAsync();

        Assert.DoesNotContain(leaves, a => a.Code == "T4-PICK"); // غير نشط
        Assert.Contains(leaves, a => a.Code == "1100");          // نشط
    }

    // ───────── الرفض لا ينشئ قيدًا ولا يعدّل رصيدًا جزئيًا ─────────

    [LivePgFact]
    public async Task Posting_to_inactive_account_is_rejected_without_any_partial_write()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var inactive = await CreateChildLeafAsync(_pg, "T4-POST", isActive: false);
        var cash = await _pg.FindAccountIdAsync("1100");

        var entriesBefore = await CountEntriesAsync(_pg);
        var cashBefore = await BalanceAsync(_pg, "1100");

        await using (var ctx = _pg.NewContext())
        {
            var journal = new JournalEntryService(ctx);

            var ex = await Assert.ThrowsAsync<InactiveAccountMovementException>(() =>
                journal.PrepareEntryAsync(
                    JournalEntryType.Manual, DateTime.Today, "B4-INACT", "محاولة ترحيل إلى حساب غير نشط",
                    new List<JournalEntryLegInput>
                    {
                        new() { AccountId = cash, DebitAmount = 250m },
                        new() { AccountId = inactive, CreditAmount = 250m }   // حساب غير نشط
                    }));

            Assert.Equal(InactiveAccountMovementException.Key, ex.ResourceKey);

            // لا قيد جزئي في السياق
            Assert.False(ctx.ChangeTracker.Entries<JournalEntry>().Any());
        }

        // لا أثر في قاعدة البيانات
        Assert.Equal(entriesBefore, await CountEntriesAsync(_pg));
        Assert.Equal(cashBefore, await BalanceAsync(_pg, "1100"));

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
