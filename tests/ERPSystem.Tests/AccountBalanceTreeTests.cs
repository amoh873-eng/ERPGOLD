using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// أرصدة شجرة الحسابات حسب المستويات (أب/ابن/حفيد) — اختبارات محاسبية بقيم معروفة النتائج
/// على محرّك الأرصدة الموحّد <see cref="AccountBalanceService"/> وعلى تقرير
/// «شجرة الحسابات والأرصدة حسب المستويات» في مركز التقارير الموحّد.
///
/// الشجرة المستخدمة (قيم ثابتة):
///   1 أصول (أب)
///     11 أصول متداولة (ابن)
///       1100 النقدية (حفيد)  → مدين 300 / دائن 40  ⇒ صافي +260
///       1101 البنك  (حفيد)   → دائن 25             ⇒ صافي −25  (رصيد دائن)
///     12 ذمم مدينة (ابن)     → مدين 25             ⇒ صافي +25
///   2 خصوم (أب)
///     21 موردون (ابن)        → دائن 100            ⇒ صافي −100
///     22 مصروفات مستحقة (ابن) → بلا حركة            ⇒ صفر
///   3 حقوق ملكية (أب بلا أبناء) → دائن 200 (رصيد افتتاحي)
///   5 مصروفات (أب) → 5300 مصروف عام (ابن) → مدين 40
/// إجمالي المدين = إجمالي الدائن = 365 (متوازن).
/// </summary>
public class AccountBalanceTreeTests
{
    // ══════════════════════ أدوات البناء ══════════════════════

    private sealed record TreeIds(
        Guid Root1, Guid Child11, Guid Grand1100, Guid Grand1101, Guid Child12,
        Guid Root2, Guid Child21, Guid Child22, Guid Root3, Guid Root5, Guid Child5300);

    private static Account NewAccount(string code, AccountType type, Guid? parentId = null) => new()
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
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>قيد متوازن بقيم معروفة (مدين/دائن لكل طرف).</summary>
    private static async Task<JournalEntry> AddEntryAsync(
        AppDbContext ctx, DateTime date, string number,
        params (Guid AccountId, decimal Debit, decimal Credit)[] legs)
    {
        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            EntryNumber = number,
            EntryType = JournalEntryType.Manual,
            EntryDate = date,
            Description = number,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Set<JournalEntry>().Add(entry);
        foreach (var leg in legs)
        {
            ctx.Set<JournalEntryLine>().Add(new JournalEntryLine
            {
                Id = Guid.NewGuid(),
                JournalEntryId = entry.Id,
                AccountId = leg.AccountId,
                DebitAmount = leg.Debit,
                CreditAmount = leg.Credit,
                CreatedAt = DateTime.UtcNow
            });
        }
        await ctx.SaveChangesAsync();
        return entry;
    }

    /// <summary>ينشئ الشجرة والقيود المعتمدة ويُرجع مُعرّفات الحسابات.</summary>
    private static async Task<TreeIds> SeedAsync(AppDbContext ctx)
    {
        var root1 = NewAccount("1", AccountType.Asset);
        var c11 = NewAccount("11", AccountType.Asset, root1.Id);
        var g1100 = NewAccount("1100", AccountType.Asset, c11.Id);
        var g1101 = NewAccount("1101", AccountType.Asset, c11.Id);
        var c12 = NewAccount("12", AccountType.Asset, root1.Id);
        var root2 = NewAccount("2", AccountType.Liability);
        var c21 = NewAccount("21", AccountType.Liability, root2.Id);
        var c22 = NewAccount("22", AccountType.Liability, root2.Id);
        var root3 = NewAccount("3", AccountType.Equity);
        var root5 = NewAccount("5", AccountType.Expense);
        var c5300 = NewAccount("5300", AccountType.Expense, root5.Id);

        ctx.Set<Account>().AddRange(root1, c11, g1100, g1101, c12, root2, c21, c22, root3, root5, c5300);
        await ctx.SaveChangesAsync();

        // رصيد افتتاحي (قبل 2025) — يُحتسب رصيداً افتتاحياً للفترة 2025-01.
        await AddEntryAsync(ctx, new DateTime(2024, 12, 31), "JE-OB",
            (g1100.Id, 200m, 0m), (root3.Id, 0m, 200m));

        // حركات داخل الفترة 2025-01.
        await AddEntryAsync(ctx, new DateTime(2025, 1, 5), "JE-1",
            (g1100.Id, 100m, 0m), (c21.Id, 0m, 100m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 6), "JE-2",
            (c12.Id, 25m, 0m), (g1101.Id, 0m, 25m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 10), "JE-3",
            (c5300.Id, 40m, 0m), (g1100.Id, 0m, 40m));

        return new TreeIds(root1.Id, c11.Id, g1100.Id, g1101.Id, c12.Id,
            root2.Id, c21.Id, c22.Id, root3.Id, root5.Id, c5300.Id);
    }

    private static AccountBalanceNodeDto Node(AccountBalanceTreeDto tree, string code)
        => tree.Flattened.Single(n => n.Code == code);

    private static readonly DateTime PeriodFrom = new(2025, 1, 1);
    private static readonly DateTime PeriodTo = new(2025, 1, 31);

    private static AccountBalanceQuery PeriodQuery() => new() { From = PeriodFrom, To = PeriodTo };

    // ══════════════════════ (1) الأب له عدة أبناء ══════════════════════

    [Fact]
    public async Task Parent_balance_equals_the_sum_of_its_children()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        var root1 = Node(tree, "1");
        Assert.Equal(Node(tree, "11").NetBalance + Node(tree, "12").NetBalance, root1.NetBalance);
        Assert.Equal(260m, root1.NetBalance);   // 235 (11) + 25 (12)

        var root2 = Node(tree, "2");
        Assert.Equal(Node(tree, "21").NetBalance + Node(tree, "22").NetBalance, root2.NetBalance);
        Assert.Equal(-100m, root2.NetBalance);
    }

    // ══════════════════════ (2) الابن له عدة أحفاد ══════════════════════

    [Fact]
    public async Task Child_balance_equals_the_sum_of_its_grandchildren()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        var child11 = Node(tree, "11");
        Assert.Equal(2, child11.Children.Count);
        Assert.Equal(Node(tree, "1100").NetBalance + Node(tree, "1101").NetBalance, child11.NetBalance);
        Assert.Equal(235m, child11.NetBalance);   // 260 − 25
    }

    // ══════════════════════ المستوى والحساب الأب (أب/ابن/حفيد) ══════════════════════

    [Fact]
    public async Task Levels_and_parent_references_are_reported_for_all_three_levels()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        var root = Node(tree, "1");
        Assert.Equal(1, root.Level);
        Assert.Equal(AccountTreeLevels.Level1Ar, root.LevelNameAr);      // المستوى الأول (الجد)
        Assert.Null(root.ParentAccountCode);
        Assert.True(root.IsAggregate);

        var child = Node(tree, "11");
        Assert.Equal(2, child.Level);
        Assert.Equal(AccountTreeLevels.Level2Ar, child.LevelNameAr);      // المستوى الثاني (الأب)
        Assert.Equal("1", child.ParentAccountCode);
        Assert.Equal("حساب 1", child.ParentAccountNameAr);

        var grand = Node(tree, "1100");
        Assert.Equal(3, grand.Level);
        Assert.Equal(AccountTreeLevels.Level3Ar, grand.LevelNameAr);      // المستوى الثالث (الحفيد)
        Assert.Equal("11", grand.ParentAccountCode);
        Assert.Equal("حساب 11", grand.ParentAccountNameAr);
        Assert.False(grand.IsAggregate);
        Assert.Equal(4, tree.Roots.Count);                               // 1، 2، 3، 5
    }

    // ══════════════════════ (3) حساب تفصيلي برصيد مدين ══════════════════════

    [Fact]
    public async Task Detailed_account_with_debit_balance_is_reported_as_debit()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        var cash = Node(tree, "1100");
        Assert.Equal(300m, cash.TotalDebit);       // إجمالي الحركة المدينة (مباشر: حفيد بلا أبناء)
        Assert.Equal(40m, cash.TotalCredit);       // إجمالي الحركة الدائنة
        Assert.Equal(300m, cash.DirectDebit);
        Assert.Equal(40m, cash.DirectCredit);
        Assert.Equal(260m, cash.NetBalance);       // صافي الرصيد = 300 − 40
        Assert.Equal(260m, cash.DebitBalance);     // الرصيد المدين
        Assert.Equal(0m, cash.CreditBalance);      // لا جزء دائن
        Assert.Equal(AccountBalanceNature.Debit, cash.NetNatureAr);
    }

    // ══════════════════════ (4) حساب تفصيلي برصيد دائن ══════════════════════

    [Fact]
    public async Task Detailed_account_with_credit_balance_is_reported_as_credit()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        var bank = Node(tree, "1101");
        Assert.Equal(0m, bank.TotalDebit);
        Assert.Equal(25m, bank.TotalCredit);
        Assert.Equal(-25m, bank.NetBalance);
        Assert.Equal(0m, bank.DebitBalance);
        Assert.Equal(25m, bank.CreditBalance);     // الرصيد الدائن موجب الطبيعة
        Assert.Equal(AccountBalanceNature.Credit, bank.NetNatureAr);
    }

    // ══════════════════════ (5) حساب بلا حركات مالية ══════════════════════

    [Fact]
    public async Task Account_without_movements_is_shown_with_zero_balance()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        var idle = Node(tree, "22");
        Assert.Equal(0m, idle.TotalDebit);
        Assert.Equal(0m, idle.TotalCredit);
        Assert.Equal(0m, idle.NetBalance);
        Assert.Equal(0m, idle.OpeningBalance);
        Assert.Equal(AccountBalanceNature.Zero, idle.NetNatureAr);
        // يبقى ظاهراً في الشجرة (مستوى ابن) دون التأثير على رصيد أبيه.
        Assert.Equal(2, idle.Level);
        Assert.Equal(Node(tree, "21").NetBalance, Node(tree, "2").NetBalance);
    }

    // ══════════════════════ (6) رصيد افتتاحي + حركات خلال الفترة ══════════════════════

    [Fact]
    public async Task Opening_balance_plus_period_movements_equals_the_net_balance()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync(PeriodQuery());

        var cash = Node(tree, "1100");
        Assert.Equal(200m, cash.OpeningBalance);   // من قيد 2024-12-31 (قبل الفترة)
        Assert.Equal(100m, cash.PeriodDebit);      // JE-1
        Assert.Equal(40m, cash.PeriodCredit);      // JE-3
        Assert.Equal(260m, cash.NetBalance);       // 200 + 100 − 40
        Assert.Equal(cash.OpeningBalance + cash.PeriodDebit - cash.PeriodCredit, cash.NetBalance);

        var root1 = Node(tree, "1");
        Assert.Equal(200m, root1.OpeningBalance);
        Assert.Equal(125m, root1.PeriodDebit);     // 100 نقدية + 25 ذمم
        Assert.Equal(65m, root1.PeriodCredit);     // 40 نقدية + 25 بنك
        Assert.Equal(260m, root1.NetBalance);

        // الهوية محفوظة في كل عقدة (أب/ابن/حفيد): افتتاحي + حركة الفترة = الصافي.
        Assert.All(tree.Flattened, n =>
            Assert.Equal(n.OpeningBalance + n.PeriodDebit - n.PeriodCredit, n.NetBalance));
    }

    // ══════════════════════ حدود الفترة المحاسبية ══════════════════════

    [Fact]
    public async Task Period_boundaries_include_from_and_to_dates_and_exclude_what_follows()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);

        await AddEntryAsync(ctx, new DateTime(2025, 2, 28), "JE-END", (ids.Grand1100, 7m, 0m), (ids.Child21, 0m, 7m));
        await AddEntryAsync(ctx, new DateTime(2025, 3, 1), "JE-AFTER", (ids.Grand1100, 990m, 0m), (ids.Child21, 0m, 990m));

        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync(
            new AccountBalanceQuery { From = new DateTime(2025, 2, 1), To = new DateTime(2025, 2, 28) });

        var cash = Node(tree, "1100");
        Assert.Equal(7m, cash.PeriodDebit);        // حركة تاريخ النهاية 2025-02-28 مشمولة
        Assert.Equal(0m, cash.PeriodCredit);
        Assert.Equal(307m, cash.TotalDebit);       // 300 (حتى نهاية فبراير) — الحركة التالية مستثناة
        Assert.Equal(267m, cash.NetBalance);       // 260 (افتتاحي) + 7
    }

    // ══════════════════════ (7) قيود محذوفة + قيد عكسي ══════════════════════

    [Fact]
    public async Task Soft_deleted_entries_and_lines_do_not_affect_balances()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);

        var before = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        // قيد محذوف كاملاً + بند محذوف داخل قيد قائم: لا أثر على الأرصدة.
        var deleted = await AddEntryAsync(ctx, new DateTime(2025, 1, 20), "JE-DEL",
            (ids.Grand1100, 500m, 0m), (ids.Child21, 0m, 500m));
        deleted.IsDeleted = true;

        var partiallyDeleted = await AddEntryAsync(ctx, new DateTime(2025, 1, 21), "JE-PART",
            (ids.Grand1100, 300m, 0m), (ids.Child21, 0m, 300m));
        var removedLeg = await ctx.Set<JournalEntryLine>()
            .FirstAsync(l => l.JournalEntryId == partiallyDeleted.Id && l.AccountId == ids.Grand1100);
        removedLeg.IsDeleted = true;
        await ctx.SaveChangesAsync();

        var after = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        Assert.Equal(Node(before, "1100").NetBalance, Node(after, "1100").NetBalance);
        Assert.Equal(Node(before, "1").TotalDebit, Node(after, "1").TotalDebit);
        Assert.Equal(Node(before, "1").TotalCredit, Node(after, "1").TotalCredit);
        Assert.Equal(Node(before, "1").NetBalance, Node(after, "1").NetBalance);
    }

    [Fact]
    public async Task Reversing_entry_offsets_the_original_movement()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);

        // سياسة النظام: لا حذف للقيود المرحّلة — بل قيد عكسي بعكس المدين/الدائن.
        // عكس JE-3 (مدين 5300 / دائن 1100) ⇒ دائن 5300 / مدين 1100.
        await AddEntryAsync(ctx, new DateTime(2025, 1, 11), "JE-3-REV",
            (ids.Child5300, 0m, 40m), (ids.Grand1100, 40m, 0m));

        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        var expense = Node(tree, "5300");
        Assert.Equal(40m, expense.TotalDebit);                 // الحركة الأصلية
        Assert.Equal(40m, expense.TotalCredit);                // والحركة العكسية
        Assert.Equal(0m, expense.NetBalance);                  // الأثر الصافي = صفر
        Assert.Equal(AccountBalanceNature.Zero, expense.NetNatureAr);
        Assert.Equal(0m, Node(tree, "5").NetBalance);          // جذر المصروفات عاد إلى الصفر

        Assert.Equal(300m, Node(tree, "1100").NetBalance);     // 260 + 40 (عكس أثر الصرف)
        Assert.Equal(300m, Node(tree, "1").NetBalance);        // 300 − 25 + 25
    }

    // ══════════════════════ (8)(9)(10) منع ازدواجية الأرصدة والتجميع الهرمي ══════════════════════

    [Fact]
    public async Task Parent_and_child_balances_never_double_count_the_same_line()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        // (أ) رصيد الجذر = مجموع الحركات المباشرة لورقاته فقط (كل بند يُحتسب مرة واحدة).
        var directUnderRoot1 = tree.Flattened
            .Where(n => n.Code is "1100" or "1101" or "12")
            .Sum(n => n.DirectDebit);
        Assert.Equal(325m, directUnderRoot1);                      // 300 + 0 + 25
        Assert.Equal(directUnderRoot1, Node(tree, "1").TotalDebit);

        // (ب) الإجماليات من الجذور فقط.
        Assert.Equal(tree.Roots.Sum(n => n.TotalDebit), tree.TotalDebit);
        Assert.Equal(tree.Roots.Sum(n => n.TotalCredit), tree.TotalCredit);
        Assert.Equal(tree.Roots.Sum(n => n.NetBalance), tree.NetBalance);

        // (ج) لو جُمعت كل الصفوف (أب + ابن + حفيد) لتضاعف الرصيد ⇒ دليل عددي على الازدواجية المُتجنَّبة.
        var naiveSum = tree.Flattened.Sum(n => n.TotalDebit);
        Assert.Equal(1030m, naiveSum);
        Assert.NotEqual(tree.TotalDebit, naiveSum);
    }

    // ══════════════════════ (11) المطابقة مع دفتر القيود ══════════════════════

    [Fact]
    public async Task Tree_totals_match_the_journal_ledger_exactly()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        var lines = await ctx.Set<JournalEntryLine>().Where(l => !l.IsDeleted).ToListAsync();
        var liveEntryIds = await ctx.Set<JournalEntry>().Where(e => !e.IsDeleted).Select(e => e.Id).ToListAsync();
        var valid = lines.Where(l => liveEntryIds.Contains(l.JournalEntryId)).ToList();

        Assert.Equal(valid.Sum(l => l.DebitAmount), tree.TotalDebit);      // 365
        Assert.Equal(valid.Sum(l => l.CreditAmount), tree.TotalCredit);    // 365
        Assert.Equal(365m, tree.TotalDebit);
        Assert.True(tree.IsBalanced);

        // رصيد كل حساب تفصيلي = صافي بنوده في الدفتر.
        foreach (var node in tree.Flattened.Where(n => !n.IsAggregate))
        {
            var debit = valid.Where(l => l.AccountId == node.Id).Sum(l => l.DebitAmount);
            var credit = valid.Where(l => l.AccountId == node.Id).Sum(l => l.CreditAmount);
            Assert.Equal(debit - credit, node.NetBalance);
        }
    }

    // ══════════════════════ (12) التصفية حسب الحساب الرئيسي ══════════════════════

    [Fact]
    public async Task Filter_by_root_account_returns_that_branch_only_with_real_levels()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync(
            new AccountBalanceQuery { RootAccountId = ids.Child11 });

        Assert.Single(tree.Roots);
        Assert.Equal("11", tree.Roots[0].Code);
        Assert.Equal(2, tree.Roots[0].Level);        // المستوى الحقيقي (ابن) محفوظ بعد التصفية
        Assert.Equal(new[] { "11", "1100", "1101" }, tree.Flattened.Select(n => n.Code).ToArray());
        Assert.Equal(300m, tree.TotalDebit);
        Assert.Equal(65m, tree.TotalCredit);

        // حساب غير موجود ⇒ شجرة فارغة بلا استثناء.
        var empty = await new AccountBalanceService(ctx).GetBalanceTreeAsync(
            new AccountBalanceQuery { RootAccountId = Guid.NewGuid() });
        Assert.Empty(empty.Roots);
        Assert.Empty(empty.Flattened);
    }

    [Fact]
    public async Task Include_zero_balance_false_prunes_accounts_without_movement()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);

        var full = await new AccountBalanceService(ctx).GetBalanceTreeAsync();
        Assert.Contains(full.Flattened, n => n.Code == "22");

        var pruned = await new AccountBalanceService(ctx).GetBalanceTreeAsync(
            new AccountBalanceQuery { IncludeZeroBalance = false });

        Assert.DoesNotContain(pruned.Flattened, n => n.Code == "22");
        Assert.Contains(pruned.Flattened, n => n.Code == "21");
        Assert.Contains(pruned.Flattened, n => n.Code == "3");     // جذري له رصيد دائن
        Assert.Equal(full.TotalDebit, pruned.TotalDebit);
        Assert.Equal(full.TotalCredit, pruned.TotalCredit);
    }

    // ══════════════════════ (13) التقرير الموحّد: نفس الأرقام + العرض الهرمي ══════════════════════

    [Fact]
    public async Task Unified_report_returns_the_same_numbers_as_the_balance_tree()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync(PeriodQuery());
        var table = await new ReportService(ctx).GetReportAsync("accounts-tree-balances",
            new Dictionary<string, string?>
            {
                ["from"] = PeriodFrom.ToString("yyyy-MM-dd"),
                ["to"] = PeriodTo.ToString("yyyy-MM-dd")
            });

        static string Fmt(decimal v) => v.ToString("N2");

        Assert.Equal("تقرير شجرة الحسابات والأرصدة حسب المستويات", table.TitleAr);
        Assert.Equal(11, table.Headers.Count);
        Assert.Equal(tree.Flattened.Count, table.Rows.Count);
        Assert.True(table.Landscape);

        // صف الحفيد 1100 مطابق تمامًا لأرقام الخدمة (نفس المصدر — بلا منطق مكرر).
        var index = tree.Flattened.FindIndex(n => n.Code == "1100");
        var cash = tree.Flattened[index];
        var row = table.Rows[index];
        Assert.Equal("1100", row[0].Trim());
        Assert.Equal("حساب 1100", row[1].Trim());
        Assert.Equal(AccountTreeLevels.Level3Ar, row[2]);
        Assert.Equal("11 - حساب 11", row[3]);
        Assert.Equal(Fmt(cash.OpeningBalance), row[4]);
        Assert.Equal(Fmt(cash.PeriodDebit), row[5]);
        Assert.Equal(Fmt(cash.PeriodCredit), row[6]);
        Assert.Equal(Fmt(cash.DebitBalance), row[7]);
        Assert.Equal(Fmt(cash.CreditBalance), row[8]);
        Assert.Equal(Fmt(cash.NetBalance), row[9]);
        Assert.Equal(cash.NetNatureAr, row[10]);

        // صف الإجماليات من الجذور فقط (بلا ازدواجية) + إشعار التوازن في تذييل التقرير.
        Assert.NotNull(table.Totals);
        Assert.Equal("الإجمالي (الجذور)", table.Totals![1]);

        // (1) مجموع الأرصدة المدينة = مجموع الأرصدة الدائنة (دليل توازن القيد المزدوج).
        Assert.Equal(table.Totals[7], table.Totals[8]);
        Assert.NotEqual(Fmt(0m), table.Totals[7]);
        // (2) مجموع صافي الأرصدة = صفر دائمًا (كل رصيد مدين يقابله رصيد دائن).
        Assert.Equal(Fmt(0m), table.Totals[9]);
        // (3) حركة الفترة مُجمَّعة من الجذور: 125 + 40 = 165 مدين، و65 + 100 = 165 دائن.
        Assert.Equal(Fmt(165m), table.Totals[5]);
        Assert.Equal(Fmt(165m), table.Totals[6]);

        Assert.Contains("متوازن", table.FooterNote);
        Assert.Contains("مرة واحدة", table.FooterNote);
    }

    [Fact]
    public async Task Report_rows_follow_the_hierarchical_order_and_are_indented_by_level()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("accounts-tree-balances",
            new Dictionary<string, string?>());

        // ترتيب هرمي: الأب ثم أبناؤه ثم أحفاده، ثم الجذر التالي.
        Assert.Equal(new[] { "1", "11", "1100", "1101", "12", "2", "21", "22", "3", "5", "5300" },
            table.Rows.Select(r => r[0].Trim()).ToArray());

        // الإزاحة تعبّر عن المستوى: أب بلا إزاحة، ابن 3 مسافات، حفيد 6 (RTL: \u00A0).
        Assert.StartsWith("1", table.Rows[0][0]);
        Assert.StartsWith(new string('\u00A0', 3), table.Rows[1][0]);
        Assert.StartsWith(new string('\u00A0', 6), table.Rows[2][0]);
        Assert.Equal(AccountTreeLevels.Level1Ar, table.Rows[0][2]);
        Assert.Equal(AccountTreeLevels.Level2Ar, table.Rows[1][2]);
        Assert.Equal(AccountTreeLevels.Level3Ar, table.Rows[2][2]);
        Assert.Equal("11 - حساب 11", table.Rows[2][3]);      // علاقة الحفيد بأبيه
    }

    [Fact]
    public async Task Report_period_parameters_drive_the_opening_and_period_columns()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var service = new ReportService(ctx);

        // بلا from/to ⇒ كامل السجل وبلا رصيد افتتاحي.
        var all = await service.GetReportAsync("accounts-tree-balances", new Dictionary<string, string?>());
        Assert.Equal("كامل السجل", all.ParamSummary);
        var cashAll = all.Rows.Single(r => r[0].Trim() == "1100");
        Assert.Equal((0m).ToString("N2"), cashAll[4]);
        Assert.Equal((260m).ToString("N2"), cashAll[9]);

        // to فقط ⇒ حتى التاريخ.
        var asOf = await service.GetReportAsync("accounts-tree-balances",
            new Dictionary<string, string?> { ["to"] = "2025-01-31" });
        Assert.Equal("حتى 2025-01-31", asOf.ParamSummary);

        // from + to ⇒ الفترة مع الرصيد الافتتاحي.
        var period = await service.GetReportAsync("accounts-tree-balances",
            new Dictionary<string, string?> { ["from"] = "2025-01-01", ["to"] = "2025-01-31" });
        Assert.Equal("2025-01-01 → 2025-01-31", period.ParamSummary);
        var cashPeriod = period.Rows.Single(r => r[0].Trim() == "1100");
        Assert.Equal((200m).ToString("N2"), cashPeriod[4]);
    }

    [Fact]
    public async Task Report_can_be_filtered_by_the_main_account()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("accounts-tree-balances",
            new Dictionary<string, string?> { ["accountId"] = ids.Child11.ToString() });

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new[] { "11", "1100", "1101" }, table.Rows.Select(r => r[0].Trim()).ToArray());
        Assert.Equal(AccountTreeLevels.Level2Ar, table.Rows[0][2]);       // المستوى الحقيقي للفرع المحدد
    }

    [Fact]
    public void Report_is_registered_in_the_unified_reports_catalog()
    {
        var def = ReportsCatalog.All.SingleOrDefault(r => r.Key == "accounts-tree-balances");

        Assert.NotNull(def);
        Assert.Equal("شجرة الحسابات والأرصدة حسب المستويات", def!.TitleAr);
        Assert.Equal(ReportCategory.Financial, def.Cat);
        Assert.Contains(ReportParamKind.DateRange, def.Params);
        Assert.Contains(ReportParamKind.Account, def.Params);
    }

    [Fact]
    public async Task Report_numbers_use_two_decimals_and_arabic_nature_labels()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            using var ctx = AccountingSafetyFixtures.NewContext();
            await SeedAsync(ctx);
            var table = await new ReportService(ctx).GetReportAsync("accounts-tree-balances",
                new Dictionary<string, string?>());

            var cash = table.Rows.Single(r => r[0].Trim() == "1100");
            Assert.Equal("260.00", cash[9]);                 // منزلتان عشريتان دائمًا
            Assert.Equal("مدين", cash[10]);

            var bank = table.Rows.Single(r => r[0].Trim() == "1101");
            Assert.Equal("-25.00", bank[9]);                 // صافي الرصيد الدائن سالب
            Assert.Equal("0.00", bank[7]);                   // ولا رصيد مدين
            Assert.Equal("25.00", bank[8]);                  // والرصيد الدائن موجب الطبيعة
            Assert.Equal("دائن", bank[10]);

            var idle = table.Rows.Single(r => r[0].Trim() == "22");
            Assert.Equal("صفر", idle[10]);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}

