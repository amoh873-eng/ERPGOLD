using System.Globalization;
using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// ميزان المراجعة الهرمي — مفتاح التقرير في مركز التقارير: «trial-balance».
///
/// اختبارات محاسبية بقيم معروفة النتائج على أربعة مستويات من دليل الحسابات:
///   1 أصول (أب)
///     11 أصول متداولة (ابن)
///       1100 النقدية (حفيد — ورقة) : افتتاحي مدين 200 + حركة مدين 100 − حركة دائن 40 ⇒ ختامي +260
///       1101 البنك  (حفيد — ورقة)  : حركة دائن 25 ⇒ ختامي −25 (رصيد دائن)
///     12 ذمم مدينة (ابن — ورقة)   : حركة مدين 25 ⇒ ختامي +25
///     13 استثمارات (ابن)
///       1300 أسهم (حفيد)
///         13001 أسهم الشركة أ (المستوى الرابع — ورقة) : حركة مدين 500 ⇒ ختامي +500
///   2 خصوم (أب)
///     21 موردون (ابن — ورقة)          : حركة دائن 100 ⇒ ختامي −100
///     22 مصروفات مستحقة (ابن — ورقة)  : بلا حركة ⇒ صفر
///   3 حقوق ملكية (أب)
///     31 رأس المال (ابن — ورقة)       : افتتاحي دائن 200 + حركة دائن 500 ⇒ ختامي −700
///   5 مصروفات (أب)
///     5300 مصروف عام (ابن — ورقة)     : حركة مدين 40 ⇒ ختامي +40
///
/// إجمالي المدين = إجمالي الدائن = 865 (متوازن)، والفترة المحاسبية 2025-01-01 → 2025-01-31.
/// </summary>
public class TrialBalanceReportTests
{
    // ══════════════════════ أدوات البناء ══════════════════════

    private sealed record TbIds(
        Guid Root1, Guid Child11, Guid Cash1100, Guid Bank1101, Guid Child12,
        Guid Child13, Guid Grand1300, Guid Great13001,
        Guid Root2, Guid Child21, Guid Child22,
        Guid Root3, Guid Child31, Guid Root5, Guid Child5300);

    private static readonly DateTime PeriodFrom = new(2025, 1, 1);
    private static readonly DateTime PeriodTo = new(2025, 1, 31);

    /// <summary>المبالغ تُنسَّق بنفس ثقافة التقرير المالية (منزلتان عشريتان + فاصل آلاف، أرقام لاتينية).</summary>
    private static string M(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

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
    /// <summary>قيد محاسبي بأطراف معروفة (المصدر: بنود القيود غير المحذوفة).</summary>
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

    private static async Task<TbIds> SeedAsync(AppDbContext ctx)
    {
        var root1 = NewAccount("1", AccountType.Asset);
        var c11 = NewAccount("11", AccountType.Asset, root1.Id);
        var cash = NewAccount("1100", AccountType.Asset, c11.Id);
        var bank = NewAccount("1101", AccountType.Asset, c11.Id);
        var c12 = NewAccount("12", AccountType.Asset, root1.Id);
        var c13 = NewAccount("13", AccountType.Asset, root1.Id);
        var g1300 = NewAccount("1300", AccountType.Asset, c13.Id);
        var gg13001 = NewAccount("13001", AccountType.Asset, g1300.Id);
        var root2 = NewAccount("2", AccountType.Liability);
        var c21 = NewAccount("21", AccountType.Liability, root2.Id);
        var c22 = NewAccount("22", AccountType.Liability, root2.Id);
        var root3 = NewAccount("3", AccountType.Equity);
        var c31 = NewAccount("31", AccountType.Equity, root3.Id);
        var root5 = NewAccount("5", AccountType.Expense);
        var c5300 = NewAccount("5300", AccountType.Expense, root5.Id);

        ctx.Set<Account>().AddRange(root1, c11, cash, bank, c12, c13, g1300, gg13001,
            root2, c21, c22, root3, c31, root5, c5300);
        await ctx.SaveChangesAsync();

        // رصيد افتتاحي (قبل الفترة): 1100 مدين 200 / 31 دائن 200.
        await AddEntryAsync(ctx, new DateTime(2024, 12, 31), "JE-OB",
            (cash.Id, 200m, 0m), (c31.Id, 0m, 200m));

        // حركات الفترة 2025-01.
        await AddEntryAsync(ctx, new DateTime(2025, 1, 5), "JE-1",
            (cash.Id, 100m, 0m), (c21.Id, 0m, 100m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 6), "JE-2",
            (c12.Id, 25m, 0m), (bank.Id, 0m, 25m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 10), "JE-3",
            (c5300.Id, 40m, 0m), (cash.Id, 0m, 40m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 15), "JE-4",
            (gg13001.Id, 500m, 0m), (c31.Id, 0m, 500m));

        return new TbIds(root1.Id, c11.Id, cash.Id, bank.Id, c12.Id,
            c13.Id, g1300.Id, gg13001.Id, root2.Id, c21.Id, c22.Id,
            root3.Id, c31.Id, root5.Id, c5300.Id);
    }

    private static Dictionary<string, string?> PeriodParams(params (string Key, string? Value)[] extra)
    {
        var pars = new Dictionary<string, string?>
        {
            ["from"] = PeriodFrom.ToString("yyyy-MM-dd"),
            ["to"] = PeriodTo.ToString("yyyy-MM-dd")
        };
        foreach (var (k, v) in extra) pars[k] = v;
        return pars;
    }

    /// <summary>صف الحساب في التقرير (أعمدة: 0 رقم، 1 اسم، 2 مستوى، 3/4 افتتاحي، 5/6 حركة، 7/8 ختامي).</summary>
    private static List<string> Row(GenericReportTable t, string code)
        => t.Rows.Single(r => r[0].Trim() == code);

    /// <summary>صافي الرصيد من عمودي الرصيد الختامي (مدين موجب / دائن سالب).</summary>
    private static decimal CloseNet(List<string> row)
        => decimal.Parse(row[7], CultureInfo.InvariantCulture) - decimal.Parse(row[8], CultureInfo.InvariantCulture);


    // ══════════════════════ (1) المستويات والترتيب الهرمي ══════════════════════

    [Fact]
    public async Task Report_lists_every_account_in_tree_order_with_real_levels()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("trial-balance",
            PeriodParams(("includeZero", "true")));

        // ترتيب هرمي (أب ثم أبناؤه ثم أحفاده) — أربعة مستويات دون حدّ ثابت.
        Assert.Equal(
            new[] { "1", "11", "1100", "1101", "12", "13", "1300", "13001", "2", "21", "22", "3", "31", "5", "5300" },
            table.Rows.Select(r => r[0].Trim()).ToArray());

        // حُدِّثت التوقعات من التسميات الثلاثية القديمة ("أب"/"ابن"/"حفيد"/"مستوى 4")
        // إلى العقد الرباعي المعتمد من مصدر الحقيقة الوحيد AccountTreeLevels
        // (‏AccountBalanceDtos.cs:186‑189) — **دون أي تغيير في منطق احتساب الأرصدة**.
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level1Ar, Row(table, "1")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level2Ar, Row(table, "11")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level3Ar, Row(table, "1100")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level4Ar, Row(table, "13001")[2]);   // المستوى الرابع (ابن الحفيد)

        // الإزاحة تعبّر عن المستوى فعليًا في عمودي رقم الحساب والاسم.
        Assert.False(Row(table, "1")[0].StartsWith('\u00A0'));
        Assert.StartsWith(new string('\u00A0', 3), Row(table, "11")[0]);
        Assert.StartsWith(new string('\u00A0', 6), Row(table, "1100")[0]);
        Assert.StartsWith(new string('\u00A0', 9), Row(table, "13001")[0]);
        Assert.Equal(new string('\u00A0', 6) + "حساب 1100", Row(table, "1100")[1]);

        // تصنيف الصفوف: تجميعي (له أبناء) مقابل تفصيلي (قابل للترحيل).
        Assert.Equal(new[] { "1", "11", "13", "1300", "2", "3", "5" },
            table.Rows.Where((r, i) => table.RowKinds![i] == "group").Select(r => r[0].Trim()).ToArray());
        Assert.Equal(new[] { "1100", "1101", "12", "13001", "21", "22", "31", "5300" },
            table.Rows.Where((r, i) => table.RowKinds![i] == "detail").Select(r => r[0].Trim()).ToArray());

        // رأس التقرير: تسعة أعمدة في أربع مجموعات.
        Assert.Equal(9, table.Headers.Count);
        Assert.Equal(table.Headers.Count, table.HeaderGroups!.Sum(g => g.Span));
        Assert.Equal(
            new[] { "البيان", "الأرصدة الافتتاحية (قبل الفترة)", "حركة الفترة", "الأرصدة الختامية (حتى نهاية الفترة)" },
            table.HeaderGroups!.Select(g => g.Title).ToArray());
        Assert.Equal("report-tb", table.CssClass);
        Assert.True(table.ShowCurrency);   // الواجهة تعرض رمز العملة المعتمدة في الترويسة
    }

    // ══════════════════════ (2) الأب = مجموع الأبناء، والابن = مجموع الأحفاد ══════════════════════

    [Fact]
    public async Task Parent_rows_equal_the_sum_of_their_children_and_subtotals_cascade()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("trial-balance",
            PeriodParams(("includeZero", "true")));

        // 11 = 1100 + 1101 (حركة الفترة والرصيد الختامي).
        Assert.Equal(
            decimal.Parse(Row(table, "1100")[5]) + decimal.Parse(Row(table, "1101")[5]),
            decimal.Parse(Row(table, "11")[5]));
        Assert.Equal(
            decimal.Parse(Row(table, "1100")[6]) + decimal.Parse(Row(table, "1101")[6]),
            decimal.Parse(Row(table, "11")[6]));
        Assert.Equal(CloseNet(Row(table, "1100")) + CloseNet(Row(table, "1101")), CloseNet(Row(table, "11")));

        // 13001 (المستوى الرابع) يصعد إلى 1300 ثم 13.
        Assert.Equal(CloseNet(Row(table, "13001")), CloseNet(Row(table, "1300")));
        Assert.Equal(CloseNet(Row(table, "1300")), CloseNet(Row(table, "13")));

        // 1 = 11 + 12 + 13، و2 = 21 + 22، و5 = 5300.
        Assert.Equal(
            CloseNet(Row(table, "11")) + CloseNet(Row(table, "12")) + CloseNet(Row(table, "13")),
            CloseNet(Row(table, "1")));
        Assert.Equal(CloseNet(Row(table, "21")) + CloseNet(Row(table, "22")), CloseNet(Row(table, "2")));
        Assert.Equal(CloseNet(Row(table, "5300")), CloseNet(Row(table, "5")));

        // قيم ثابتة معلومة — تمنع أي انحراف صامت في الاحتساب.
        Assert.Equal(M(760m), Row(table, "1")[7]);      // 235 + 25 + 500
        Assert.Equal(M(235m), Row(table, "11")[7]);     // 260 − 25
        Assert.Equal(M(260m), Row(table, "1100")[7]);
        Assert.Equal(M(700m), Row(table, "3")[8]);      // حقوق الملكية: 200 + 500 دائن
        Assert.Equal(M(100m), Row(table, "2")[8]);
        Assert.Equal(M(40m), Row(table, "5")[7]);
    }

    // ══════════════════════ (3) الحركات المدينة والدائنة على الحسابات القابلة للترحيل ══════════════════════

    [Fact]
    public async Task Period_columns_carry_debits_and_credits_of_postable_accounts()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("trial-balance", PeriodParams());

        var cash = Row(table, "1100");
        Assert.Equal(M(200m), cash[3]);     // الرصيد الافتتاحي (JE-OB قبل الفترة)
        Assert.Equal(M(0m), cash[4]);
        Assert.Equal(M(100m), cash[5]);     // JE-1
        Assert.Equal(M(40m), cash[6]);      // JE-3
        Assert.Equal(M(260m), cash[7]);
        Assert.Equal(M(0m), cash[8]);

        Assert.Equal(M(0m), Row(table, "1101")[3]);
        Assert.Equal(M(25m), Row(table, "1101")[6]);

        // الهوية المحاسبية محفوظة في كل صف (أب/ابن/حفيد): افتتاحي + حركة الفترة = الرصيد الختامي.
        foreach (var r in table.Rows)
        {
            var opening = decimal.Parse(r[3]) - decimal.Parse(r[4]);
            var period = decimal.Parse(r[5]) - decimal.Parse(r[6]);
            Assert.Equal(opening + period, CloseNet(r));
        }
    }

    // ══════════════════════ (4) منع ازدواج الأرصدة في الإجمالي العام ══════════════════════

    [Fact]
    public async Task General_total_comes_from_the_roots_and_never_double_counts()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("trial-balance", PeriodParams());

        Assert.Equal(2, table.TotalsRows!.Count);
        var details = table.TotalsRows![0];
        var grand = table.TotalsRows![1];

        Assert.Equal("إجمالي الحسابات القابلة للترحيل (التفصيلية = إجمالي ميزان المراجعة)", details[1]);
        Assert.Equal("الإجمالي العام من الجذور (صافي كل جذر بعد تقاصّ الأرصدة المتعاكسة)", grand[1]);

        // (أ) عمودا حركة الفترة: مجموع الأوراق = مجموع الجذور تمامًا ⇒ لا حركة محتسبة مرتين.
        Assert.Equal(details[5], grand[5]);
        Assert.Equal(details[6], grand[6]);
        Assert.Equal(M(665m), grand[5]);    // 100 نقدية + 25 ذمم + 500 أسهم + 40 مصروف
        Assert.Equal(M(665m), grand[6]);    // 40 نقدية + 25 بنك + 100 موردون + 500 رأس مال

        // (ب) لو جُمعت صفوف الشجرة كلها (أب + ابن + حفيد + حفيد الحفيد) لتضاعفت الحركة — لذلك لا تُجمع.
        var naiveRowSum = table.Rows.Sum(r => decimal.Parse(r[5]));
        Assert.Equal(M(2430m), naiveRowSum.ToString("#,##0.00", CultureInfo.InvariantCulture));
        Assert.True(naiveRowSum > decimal.Parse(grand[5]));   // الازدواج المتجنّب = 2430 − 665

        // (ج) إجمالي الحسابات القابلة للترحيل = مجموع الأوراق فقط (مطابق لميزان المراجعة التقليدي).
        Assert.Equal(M(200m), details[3]);
        Assert.Equal(M(200m), details[4]);
        Assert.Equal(M(825m), details[7]);
        Assert.Equal(M(825m), details[8]);
    }

    // ══════════════════════ (5) المطابقة مع دفتر الأستاذ للفترة نفسها ══════════════════════

    [Fact]
    public async Task Balances_reconcile_with_the_general_ledger_for_the_same_period()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var service = new ReportService(ctx);
        var table = await service.GetReportAsync("trial-balance",
            PeriodParams(("includeZero", "true")));

        // كل حساب قابل للترحيل: مطابقة تامة مع دفتر الأستاذ للفترة نفسها.
        // Sign = +1 للحسابات المدينة الطبيعة، و−1 للدائنة (دفتر الأستاذ يعرض الرصيد بطبيعة الحساب).
        var postable = new (Guid Id, string Code, int Sign)[]
        {
            (ids.Cash1100, "1100", 1), (ids.Bank1101, "1101", 1), (ids.Child12, "12", 1),
            (ids.Great13001, "13001", 1), (ids.Child5300, "5300", 1),
            (ids.Child21, "21", -1), (ids.Child22, "22", -1), (ids.Child31, "31", -1)
        };

        foreach (var (id, code, sign) in postable)
        {
            var ledger = await service.GetAccountLedgerAsync(id, PeriodFrom, PeriodTo);
            var row = Row(table, code);

            Assert.Equal(ledger.Lines.Sum(l => l.Debit), decimal.Parse(row[5]));     // حركة الفترة المدينة
            Assert.Equal(ledger.Lines.Sum(l => l.Credit), decimal.Parse(row[6]));    // حركة الفترة الدائنة
            Assert.Equal(ledger.OpeningBalance * sign, decimal.Parse(row[3]) - decimal.Parse(row[4]));
            Assert.Equal(ledger.ClosingBalance * sign, CloseNet(row));
        }

        // عيّنة صريحة بقيم معروفة: النقدية 200 → 260.
        var cash = await service.GetAccountLedgerAsync(ids.Cash1100, PeriodFrom, PeriodTo);
        Assert.Equal(200m, cash.OpeningBalance);
        Assert.Equal(260m, cash.ClosingBalance);

        Assert.Contains("متوازن", table.FooterNote);
        Assert.Contains("تحقّق عدم الازدواجية", table.FooterNote);
    }


    // ══════════════════════ (6) الفترة الزمنية: من/إلى والاستثناء بعد نهاية الفترة ══════════════════════

    [Fact]
    public async Task Period_parameters_drive_opening_period_columns_and_exclude_later_movements()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var service = new ReportService(ctx);

        // حركة بعد نهاية الفترة (فبراير) — يجب ألا تدخل تقرير يناير.
        await AddEntryAsync(ctx, new DateTime(2025, 2, 10), "JE-FEB",
            (ids.Cash1100, 990m, 0m), (ids.Child21, 0m, 990m));

        // (أ) بلا فترة ⇒ كامل السجل وبلا رصيد افتتاحي (تعريف التقرير يُعلن ذلك صراحة).
        var all = await service.GetReportAsync("trial-balance", new Dictionary<string, string?>());
        Assert.Equal("كامل السجل (كل الفترات) | الحسابات ذات الحركة فقط", all.ParamSummary);
        Assert.Equal(M(0m), Row(all, "1100")[3]);
        Assert.Equal(M(1290m), Row(all, "1100")[5]);   // 200 (افتتاحي) + 100 + 990 ↑ وبلا رصيد افتتاحي مفصول
        Assert.Equal(M(40m), Row(all, "1100")[6]);

        // (ب) «إلى» فقط ⇒ حتى التاريخ شمولًا.
        var asOf = await service.GetReportAsync("trial-balance",
            new Dictionary<string, string?> { ["to"] = "2025-01-31" });
        Assert.Equal("حتى 2025-01-31 | الحسابات ذات الحركة فقط", asOf.ParamSummary);
        Assert.Equal(M(300m), Row(asOf, "1100")[5]);    // بدون حركة فبراير

        // (ج) الفترة كاملة [من، إلى] شاملة الطرفين، والرصيد الافتتاحي = ما قبل «من».
        var period = await service.GetReportAsync("trial-balance", PeriodParams());
        Assert.Equal("2025-01-01 → 2025-01-31 | الحسابات ذات الحركة فقط", period.ParamSummary);
        Assert.Equal(M(200m), Row(period, "1100")[3]);
        Assert.Equal(M(100m), Row(period, "1100")[5]);
        Assert.Equal(260m, CloseNet(Row(period, "1100")));
    }

    // ══════════════════════ (7) حسابات صفرية وأرصدة دائنة (سالبة) + فلتر إظهار الصفري ══════════════════════

    [Fact]
    public async Task Zero_balance_accounts_and_credit_balances_are_presented_correctly()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var service = new ReportService(ctx);

        // افتراضيًا تُخفى الحسابات الصفرية (سلوك ميزان المراجعة التقليدي).
        var hidden = await service.GetReportAsync("trial-balance", PeriodParams());
        Assert.DoesNotContain(hidden.Rows, r => r[0].Trim() == "22");

        var shown = await service.GetReportAsync("trial-balance", PeriodParams(("includeZero", "true")));
        var idle = Row(shown, "22");
        Assert.Equal(M(0m), idle[3]);
        Assert.Equal(M(0m), idle[5]);
        Assert.Equal(M(0m), idle[7]);
        Assert.Equal(M(0m), idle[8]);

        // الفلتر لا يغيّر الإجماليات: الحساب الصفري لا أثر له على أي إجمالي، والنتيجة تُعاد احتسابها.
        Assert.Equal(hidden.TotalsRows![0][7], shown.TotalsRows![0][7]);
        Assert.Equal(hidden.TotalsRows![1][5], shown.TotalsRows![1][5]);

        // الرصيد الدائن يُعرض في العمود الدائن موجبًا (لا أرقام سالبة في أعمدة الأرصدة).
        var bank = Row(shown, "1101");
        Assert.Equal(M(0m), bank[7]);
        Assert.Equal(M(25m), bank[8]);
        Assert.DoesNotContain("-", bank[7]);

        var capital = Row(shown, "31");
        Assert.Equal(M(0m), capital[7]);
        Assert.Equal(M(700m), capital[8]);
    }

    // ══════════════════════ (8) القيود/البنود المحذوفة لا تدخل التقرير ══════════════════════

    [Fact]
    public async Task Soft_deleted_entries_and_lines_are_excluded_from_the_report()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var service = new ReportService(ctx);

        var before = await service.GetReportAsync("trial-balance", PeriodParams());

        // (أ) قيد محذوف كاملًا (طرفاه): لا أثر على أي عمود ولا على الإجماليات.
        var deleted = await AddEntryAsync(ctx, new DateTime(2025, 1, 20), "JE-DEL",
            (ids.Cash1100, 500m, 0m), (ids.Child21, 0m, 500m));
        deleted.IsDeleted = true;
        await ctx.SaveChangesAsync();

        var afterFullDelete = await service.GetReportAsync("trial-balance", PeriodParams());
        Assert.Equal(Row(before, "1100")[7], Row(afterFullDelete, "1100")[7]);
        Assert.Equal(Row(before, "21")[8], Row(afterFullDelete, "21")[8]);
        Assert.Equal(before.TotalsRows![0], afterFullDelete.TotalsRows![0]);
        Assert.Equal(before.TotalsRows![1], afterFullDelete.TotalsRows![1]);

        // (ب) بند محذوف داخل قيد قائم: البند المحذوف وحده لا يُحتسب، ويبقى الطرف الآخر غير المحذوف محتسبًا.
        var partiallyDeleted = await AddEntryAsync(ctx, new DateTime(2025, 1, 21), "JE-PART",
            (ids.Cash1100, 300m, 0m), (ids.Child21, 0m, 300m));
        var removedLeg = await ctx.Set<JournalEntryLine>()
            .FirstAsync(l => l.JournalEntryId == partiallyDeleted.Id && l.AccountId == ids.Cash1100);
        removedLeg.IsDeleted = true;
        await ctx.SaveChangesAsync();

        var after = await service.GetReportAsync("trial-balance", PeriodParams());

        Assert.Equal(Row(afterFullDelete, "1100")[7], Row(after, "1100")[7]);   // البند المحذوف (مدين 300) مستثنى
        Assert.Equal(Row(afterFullDelete, "1100")[5], Row(after, "1100")[5]);   // ولا حركة مدينة جديدة
        Assert.Equal(M(100m), Row(after, "1100")[5]);                           // تبقى حركة JE-1 وحدها
        Assert.Equal(M(400m), Row(after, "21")[6]);                             // 100 (JE-1) + 300 (الطرف غير المحذوف)
        Assert.Equal(
            decimal.Parse(afterFullDelete.TotalsRows![0][6]) + 300m,
            decimal.Parse(after.TotalsRows![0][6]));
        Assert.Equal(
            decimal.Parse(afterFullDelete.TotalsRows![0][5]),
            decimal.Parse(after.TotalsRows![0][5]));                            // عمود المدين لم يتغيّر
    }


    // ══════════════════════ (9) التصفية على فرع حساب واحد: إعادة احتساب لا إخفاء صفوف ══════════════════════

    [Fact]
    public async Task Filter_by_account_recomputes_the_report_for_that_branch_only()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("trial-balance",
            PeriodParams(("accountId", ids.Child11.ToString())));

        // الفروع خارجه لا تظهر إطلاقًا (إعادة احتساب، وليست إخفاء صفوف).
        Assert.Equal(new[] { "11", "1100", "1101" }, table.Rows.Select(r => r[0].Trim()).ToArray());
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level2Ar, Row(table, "11")[2]);        // المستوى الحقيقي للفرع محفوظ
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level3Ar, Row(table, "1100")[2]);
        Assert.Equal("إجمالي الحسابات القابلة للترحيل (الفرع: 11)", table.TotalsRows![0][1]);
        Assert.Contains("11 - حساب 11", table.TotalsRows![1][1]);

        // الإجماليات تخص الفرع وحده (لا بيانات من بقية الشجرة).
        Assert.Equal(M(100m), table.TotalsRows![0][5]);
        Assert.Equal(M(65m), table.TotalsRows![0][6]);
        Assert.Equal(M(260m), table.TotalsRows![0][7]);
        Assert.Equal(M(25m), table.TotalsRows![0][8]);
        Assert.Equal(M(235m), table.TotalsRows![1][7]);  // تجميع جذر الفرع
        Assert.Equal(M(0m), table.TotalsRows![1][8]);

        // حركة الفترة من الجذور = من التفصيلي ⇒ لا ازدواجية حتى مع التصفية.
        Assert.Equal(table.TotalsRows![0][5], table.TotalsRows![1][5]);
        Assert.Equal(table.TotalsRows![0][6], table.TotalsRows![1][6]);
    }

    // ══════════════════════ (10) حالة عدم التوازن: يُعرض الفرق ولا يُخفى ══════════════════════

    [Fact]
    public async Task Unbalanced_data_is_reported_with_its_difference_and_not_hidden()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);

        // قيد غير متوازن (مدين 100 مقابل دائن 40) ⇒ فرق 60 يجب أن يظهر صريحًا في التقرير.
        await AddEntryAsync(ctx, new DateTime(2025, 1, 25), "JE-UNBALANCED",
            (ids.Cash1100, 100m, 0m), (ids.Child21, 0m, 40m));

        var table = await new ReportService(ctx).GetReportAsync("trial-balance", PeriodParams());

        Assert.Equal(M(925m), table.TotalsRows![0][7]);   // 825 + 100
        Assert.Equal(M(865m), table.TotalsRows![0][8]);   // 825 + 40
        Assert.Equal(M(765m), table.TotalsRows![0][5]);   // 665 + 100
        Assert.Equal(M(705m), table.TotalsRows![0][6]);   // 665 + 40

        Assert.Contains("غير متوازن", table.FooterNote);
        Assert.Contains(M(60m), table.FooterNote);        // الفرق معروض
        Assert.DoesNotContain("— متوازن.", table.FooterNote);
    }

    // ══════════════════════ تنسيق المبالغ + التسجيل في كتالوج التقارير ══════════════════════

    [Fact]
    public async Task Money_columns_use_two_decimals_thousands_separator_and_latin_digits()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // التنسيق ثابت مهما كانت ثقافة الواجهة/السيرفر.
            CultureInfo.CurrentCulture = new CultureInfo("ar-JO");

            using var ctx = AccountingSafetyFixtures.NewContext();
            var ids = await SeedAsync(ctx);
            await AddEntryAsync(ctx, new DateTime(2025, 1, 28), "JE-BIG",
                (ids.Cash1100, 1000.25m, 0m), (ids.Child21, 0m, 1000.25m));

            var table = await new ReportService(ctx).GetReportAsync("trial-balance", PeriodParams());

            var cash = Row(table, "1100");
            Assert.Equal("1,260.25", cash[7]);
            Assert.Contains(",", cash[7]);         // فاصل آلاف
            Assert.EndsWith(".25", cash[7]);       // منزلتان عشريتان
            foreach (var ch in cash[7]) Assert.True(ch < 128, "أرقام لاتينية فقط في الأعمدة المالية");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Report_is_registered_in_the_unified_catalog_with_its_filters()
    {
        var def = ReportsCatalog.All.SingleOrDefault(r => r.Key == "trial-balance");

        Assert.NotNull(def);
        Assert.Equal("ميزان المراجعة", def!.TitleAr);
        Assert.Equal(ReportCategory.Financial, def.Cat);
        Assert.Contains(ReportParamKind.DateRange, def.Params);    // من/إلى
        Assert.Contains(ReportParamKind.Account, def.Params);      // حساب رئيسي أو فرع
        Assert.Contains(ReportParamKind.IncludeZero, def.Params);  // إظهار الحسابات الصفرية
        Assert.True(def.Landscape);                                // تقرير عريض
    }
    // ══════════════════════ (11) التصفية على أي فرع (وليس الحسابات الرئيسية فقط) ══════════════════════

    [Fact]
    public async Task Filter_by_any_branch_level_keeps_the_real_level_and_only_its_subtree()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("trial-balance",
            PeriodParams(("accountId", ids.Cash1100.ToString())));

        // فرع بمستوى حفيد (3): يظهر وحده بمستواه الحقيقي، وأرقامه كما هي في الشجرة الكاملة.
        Assert.Equal(new[] { "1100" }, table.Rows.Select(r => r[0].Trim()).ToArray());
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level3Ar, Row(table, "1100")[2]);
        Assert.Equal(M(200m), Row(table, "1100")[3]);   // افتتاحي
        Assert.Equal(M(100m), Row(table, "1100")[5]);
        Assert.Equal(M(40m), Row(table, "1100")[6]);
        Assert.Equal(260m, CloseNet(Row(table, "1100")));

        // الإجماليات تُعاد احتسابها على الفرع وحده (لا صفوف من فروع أخرى).
        Assert.Equal(M(100m), table.TotalsRows![0][5]);
        Assert.Equal(M(40m), table.TotalsRows![0][6]);
        Assert.Equal(M(260m), table.TotalsRows![0][7]);
        Assert.Equal(table.TotalsRows![0][5], table.TotalsRows![1][5]);
        Assert.Equal(table.TotalsRows![0][6], table.TotalsRows![1][6]);
        Assert.Contains("الحساب: 1100", table.ParamSummary);
    }

    // ══════════════════════ (12) فلتر «مستويات التجميع فقط»: إخفاء عرض بلا تغيير محاسبي ══════════════════════

    [Fact]
    public async Task Levels_only_filter_hides_detail_rows_without_changing_any_number()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var service = new ReportService(ctx);

        var full = await service.GetReportAsync("trial-balance", PeriodParams(("includeZero", "true")));
        var aggregated = await service.GetReportAsync("trial-balance",
            PeriodParams(("includeZero", "true"), ("levelsOnly", "true")));

        // الصفوف المعروضة = الحسابات التجميعية فقط (لها أبناء) بترتيب هرمي، بلا صفوف تفصيلية.
        Assert.Equal(new[] { "1", "11", "13", "1300", "2", "3", "5" },
            aggregated.Rows.Select(r => r[0].Trim()).ToArray());
        Assert.All(aggregated.RowKinds!, kind => Assert.Equal("group", kind));

        // كل صف تجميعي مطابق تمامًا لنظيره في التقرير الكامل (لا إعادة احتساب ولا تقاصّ عرض).
        foreach (var row in aggregated.Rows) Assert.Equal(Row(full, row[0].Trim()), row);

        // الأرقام المالية للإجماليات لا تتغيّر: إخفاء العرض لا يمسّ الاحتساب (بلا ازدواج).
        for (var c = 3; c <= 8; c++)
        {
            Assert.Equal(full.TotalsRows![0][c], aggregated.TotalsRows![0][c]);
            Assert.Equal(full.TotalsRows![1][c], aggregated.TotalsRows![1][c]);
        }
        Assert.Equal("إجمالي الحسابات القابلة للترحيل (التفصيلية = إجمالي ميزان المراجعة) — محتسب من الشجرة كاملة",
            aggregated.TotalsRows![0][1]);

        // شفافية الفلتر في الوصف والملاحظة (لا يُخفى أنه وضع عرض).
        Assert.Contains("مستويات التجميع فقط", aggregated.ParamSummary);
        Assert.Contains("مستويات التجميع فقط", aggregated.FooterNote);
        Assert.DoesNotContain("مستويات التجميع فقط", full.ParamSummary);
        Assert.DoesNotContain("مستويات التجميع فقط", full.FooterNote);
    }

    // ══════════════════════ (13) تصدير Excel: الترتيب الهرمي والإجماليات بلا فقدان أو تكرار ══════════════════════

    [Fact]
    public async Task Excel_export_keeps_hierarchy_rows_header_groups_and_both_totals()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var service = new ReportService(ctx);
        var table = await service.GetReportAsync("trial-balance", PeriodParams(("includeZero", "true")));

        var bytes = service.ExportReportExcel(table);
        Assert.True(bytes.Length > 0, "ملف Excel يجب ألا يكون فارغًا");

        using var ms = new MemoryStream(bytes);
        using var wb = new ClosedXML.Excel.XLWorkbook(ms);
        var ws = wb.Worksheet(1);

        // (أ) ترويسة الملف: اسم التقرير ثم الفترة والفلاتر المطبقة.
        Assert.Equal(table.TitleAr, ws.Cell(1, 1).GetString());
        Assert.Equal(table.ParamSummary, ws.Cell(2, 1).GetString());

        // (ب) صف مجموعات الأعمدة ثم صف العناوين (تسعة أعمدة في أربع مجموعات).
        Assert.Equal("البيان", ws.Cell(4, 1).GetString());
        Assert.Equal("الأرصدة الافتتاحية (قبل الفترة)", ws.Cell(4, 4).GetString());
        Assert.Equal("حركة الفترة", ws.Cell(4, 6).GetString());
        Assert.Equal("الأرصدة الختامية (حتى نهاية الفترة)", ws.Cell(4, 8).GetString());
        Assert.Equal("رقم الحساب", ws.Cell(5, 1).GetString());
        Assert.Equal("اسم الحساب", ws.Cell(5, 2).GetString());
        Assert.Equal("المستوى", ws.Cell(5, 3).GetString());
        Assert.Equal("مدين", ws.Cell(5, 4).GetString());
        Assert.Equal("دائن", ws.Cell(5, 9).GetString());

        // (ج) كل صفوف الشجرة مُصدَّرة بنفس ترتيبها ومسافاتها البادئة — بلا فقدان وبلا تكرار.
        var exported = new List<string>();
        for (var r = 6; r < 6 + table.Rows.Count; r++) exported.Add(ws.Cell(r, 1).GetString());
        Assert.Equal(table.Rows.Select(row => row[0]).ToArray(), exported.ToArray());
        Assert.Equal(exported.Count, exported.Distinct().Count());
        Assert.Equal("1", ws.Cell(6, 1).GetString());
        Assert.Equal(new string('\u00A0', 3) + "11", ws.Cell(7, 1).GetString());
        Assert.Equal(new string('\u00A0', 9) + "13001", ws.Cell(13, 1).GetString());

        // (د) صفّا الإجماليات: التفصيلي ثم الإجمالي العام — بلا جمع صفوف التجميع مرتين.
        var firstTotalRow = 6 + table.Rows.Count;
        Assert.Equal(table.TotalsRows![0][1], ws.Cell(firstTotalRow, 2).GetString());
        Assert.Equal(table.TotalsRows![1][1], ws.Cell(firstTotalRow + 1, 2).GetString());
        Assert.Equal(M(665m), ws.Cell(firstTotalRow, 6).GetString());
        Assert.Equal(M(665m), ws.Cell(firstTotalRow + 1, 6).GetString());
        Assert.Equal(M(825m), ws.Cell(firstTotalRow, 8).GetString());
        Assert.Equal(M(825m), ws.Cell(firstTotalRow, 9).GetString());
        Assert.Equal(M(800m), ws.Cell(firstTotalRow + 1, 8).GetString());
        Assert.Equal(M(800m), ws.Cell(firstTotalRow + 1, 9).GetString());

        // (هـ) ملف بسيط (بلا مجموعات أعمدة) يبقى بتنسيقه القديم تمامًا (توافق رجعي للتصدير).
        var legacy = new GenericReportTable
        {
            TitleAr = "تقرير بسيط",
            Headers = new List<string> { "البند", "القيمة" },
            Rows = new List<List<string>> { new() { "أ", "1.00" } },
            Totals = new List<string> { "", "1.00" }
        };
        using var legacyWb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(service.ExportReportExcel(legacy)));
        var legacyWs = legacyWb.Worksheet(1);
        Assert.Equal("البند", legacyWs.Cell(1, 1).GetString());
        Assert.Equal("أ", legacyWs.Cell(2, 1).GetString());
        Assert.Equal("1.00", legacyWs.Cell(3, 2).GetString());
    }
    // ══════════════════════ (14) تدقيق فرق الإجماليين: تفسير 25.00 بالمعادلة لا بالنظرية ══════════════════════

    /// <summary>مجموع عمود (بفهرسه في صف الجدول) على مجموعة صفوف محددة بأرقام الحسابات.</summary>
    private static decimal SumCol(GenericReportTable t, int index, params string[] codes)
        => t.Rows.Where(r => codes.Contains(r[0].Trim()))
                 .Sum(r => decimal.Parse(r[index], CultureInfo.InvariantCulture));

    [Fact]
    public async Task Closing_totals_gap_is_exactly_the_intra_root_offsetting_with_no_unexplained_remainder()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var service = new ReportService(ctx);
        var table = await service.GetReportAsync("trial-balance", PeriodParams(("includeZero", "true")));
        var details = table.TotalsRows![0];
        var grand = table.TotalsRows![1];

        // (1) الافتتاحي وحركة الفترة: متطابقة تمامًا في الصفّين — لا تقاصّ ولا فرق.
        Assert.Equal(M(200m), details[3]); Assert.Equal(M(200m), grand[3]);
        Assert.Equal(M(200m), details[4]); Assert.Equal(M(200m), grand[4]);
        Assert.Equal(M(665m), details[5]); Assert.Equal(M(665m), grand[5]);
        Assert.Equal(M(665m), details[6]); Assert.Equal(M(665m), grand[6]);

        // (2) الفرق الوحيد في عمودي الرصيد الختامي = 25.00 (مدين ودائنًا معًا).
        Assert.Equal(M(825m), details[7]); Assert.Equal(M(800m), grand[7]);
        Assert.Equal(M(825m), details[8]); Assert.Equal(M(800m), grand[8]);
        Assert.Equal(25m, decimal.Parse(details[7]) - decimal.Parse(grand[7]));
        Assert.Equal(25m, decimal.Parse(details[8]) - decimal.Parse(grand[8]));

        // (3) المعادلة القابلة لإعادة الاختبار: لكل جذر (الأوراق مقابل رصيد الجذر المعروض)
        //     التقاصّ نفسه على العمودين ⇒ مجموع التقاصّ = فرق الإجماليين بالضبط.
        var rootLeaves = new Dictionary<string, string[]>
        {
            ["1"] = new[] { "1100", "1101", "12", "13001" },
            ["2"] = new[] { "21", "22" },
            ["3"] = new[] { "31" },
            ["5"] = new[] { "5300" }
        };

        decimal debitOffset = 0m, creditOffset = 0m;
        foreach (var (rootCode, leaves) in rootLeaves)
        {
            var rootRow = Row(table, rootCode);
            var grossDebit = SumCol(table, 7, leaves);
            var grossCredit = SumCol(table, 8, leaves);
            var shownDebit = decimal.Parse(rootRow[7], CultureInfo.InvariantCulture);
            var shownCredit = decimal.Parse(rootRow[8], CultureInfo.InvariantCulture);

            Assert.Equal(grossDebit - shownDebit, grossCredit - shownCredit);  // التقاصّ متساوٍ على العمودين
            debitOffset += grossDebit - shownDebit;
            creditOffset += grossCredit - shownCredit;
        }

        Assert.Equal(25m, debitOffset);
        Assert.Equal(25m, creditOffset);
        Assert.Equal(debitOffset, decimal.Parse(details[7]) - decimal.Parse(grand[7]));    // باقي غير مفسّر = صفر
        Assert.Equal(creditOffset, decimal.Parse(details[8]) - decimal.Parse(grand[8]));

        // (4) الحساب المتسبّب وحيد: 1101 (البنك) داخل الجذر «1 - أصول» برصيد دائن 25 مقابل مدين 785.
        Assert.Equal(M(25m), Row(table, "1101")[8]);
        Assert.Equal(M(0m), Row(table, "1101")[7]);
        Assert.Equal(M(785m), M(SumCol(table, 7, "1100", "1101", "12", "13001")));
        Assert.Equal(M(25m), M(SumCol(table, 8, "1100", "1101", "12", "13001")));
        Assert.Equal(M(760m), Row(table, "1")[7]);      // 785 − 25 تقاصًّا داخل الجذر
        Assert.Equal(M(0m), Row(table, "1")[8]);

        // (5) لا خلل في التوازن: الصفّان متوازنان بعد التقاصّ (لأن التقاصّ يسحب نفس المبلغ من العمودين).
        Assert.Equal(details[7], details[8]);
        Assert.Equal(grand[7], grand[8]);

        // (6) الأرقام مطابقة لدفتر الأستاذ: رصيد كل ورقة تحت الجذر «1» من دفتر الأستاذ =
        //     صفّها في التقرير، ومجموع دفاتر الأوراق = رصيد الجذر الصافي (لا فرق غير مفسر).
        decimal ledgerSum = 0m;
        foreach (var (id, code) in new[] { (ids.Cash1100, "1100"), (ids.Bank1101, "1101"), (ids.Child12, "12"), (ids.Great13001, "13001") })
        {
            var ledger = await service.GetAccountLedgerAsync(id, PeriodFrom, PeriodTo);
            Assert.Equal(ledger.ClosingBalance, CloseNet(Row(table, code)));   // أصول (طبيعة مدينة): نفس الإشارة
            ledgerSum += ledger.ClosingBalance;
        }
        Assert.Equal(760m, ledgerSum);
        Assert.Equal(ledgerSum, CloseNet(Row(table, "1")));

        // (7) الملاحظة تفسّر الفرق رقميًا وتذكر الحساب المتسبّب (يمنع بقاء فرق غير مفسّر مستقبلًا).
        Assert.Contains("تقاصّ أرصدة متعاكسة داخل الجذور", table.FooterNote);
        Assert.Contains("1101 دائن 25.00", table.FooterNote);
        Assert.Contains(M(25m), table.FooterNote);
        Assert.DoesNotContain("لا يطابق التقاصّ", table.FooterNote);
    }


    // ══════════════════════ (15) الإجماليان مشتقّان من الشجرة — لا من جمع الصفوف المعروضة ══════════════════════

    [Fact]
    public async Task Both_totals_are_traceable_to_the_tree_and_not_to_the_sum_of_displayed_rows()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var table = await new ReportService(ctx).GetReportAsync("trial-balance", PeriodParams(("includeZero", "true")));
        var details = table.TotalsRows![0];
        var grand = table.TotalsRows![1];

        // (1) التفصيلي = مجموع صفوف الأوراق المعروضة فقط (لا صفوف تجميعية معها).
        var leafCodes = new[] { "1100", "1101", "12", "13001", "21", "22", "31", "5300" };
        for (var c = 3; c <= 8; c++) Assert.Equal(M(SumCol(table, c, leafCodes)), details[c]);

        // (2) العام = مجموع صفوف الجذور المعروضة فقط (صافي كل جذر).
        var rootCodes = new[] { "1", "2", "3", "5" };
        for (var c = 3; c <= 8; c++) Assert.Equal(M(SumCol(table, c, rootCodes)), grand[c]);

        // (3) الجمع الساذج لكل الصفوف (أب + ابن + حفيد) يضاعف الحركة ⇒ لا يُستخدم في أي إجمالي.
        var naivePeriodDebit = table.Rows.Sum(r => decimal.Parse(r[5], CultureInfo.InvariantCulture));
        Assert.Equal(2430m, naivePeriodDebit);
        Assert.Equal(665m, decimal.Parse(details[5], CultureInfo.InvariantCulture));
        Assert.NotEqual(details[5], M(naivePeriodDebit));

        // (4) أي فرق بين الإجماليين مفسَّر رقميًا في الملاحظة (لا فرق صامت بلا تفسير).
        Assert.Contains("تقاصّ أرصدة متعاكسة داخل الجذور", table.FooterNote);
        Assert.Contains("إجمالي ميزان المراجعة المطابق لدفتر الأستاذ", table.FooterNote);
        Assert.DoesNotContain("لا يطابق التقاصّ", table.FooterNote);
        Assert.DoesNotContain("غير متوازن", table.FooterNote);
    }

    // ══════════════════════ (16) التوافق مع التعريف القديم عند عدم تحديد تاريخين ══════════════════════

    [Fact]
    public async Task Without_dates_the_report_keeps_the_legacy_definition_and_matches_raw_journal_lines()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);

        var table = await new ReportService(ctx).GetReportAsync("trial-balance",
            new Dictionary<string, string?> { ["includeZero"] = "true" });

        // التعريف القديم: إجمالي مدين/دائن لكل حساب من كل البنود غير المحذوفة، وبلا فصل افتتاحي.
        var raw = await ctx.Set<JournalEntryLine>()
            .Where(l => !l.IsDeleted && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.DebitAmount), Credit = g.Sum(x => x.CreditAmount) })
            .ToListAsync();

        var postable = new (Guid Id, string Code)[]
        {
            (ids.Cash1100, "1100"), (ids.Bank1101, "1101"), (ids.Child12, "12"), (ids.Great13001, "13001"),
            (ids.Child21, "21"), (ids.Child22, "22"), (ids.Child31, "31"), (ids.Child5300, "5300")
        };
        foreach (var (id, code) in postable)
        {
            var row = Row(table, code);
            var sums = raw.FirstOrDefault(x => x.AccountId == id);
            var debit = sums?.Debit ?? 0m;
            var credit = sums?.Credit ?? 0m;

            Assert.Equal(M(0m), row[3]);                     // بلا فترة ⇒ لا رصيد افتتاحي مفصول
            Assert.Equal(M(0m), row[4]);
            Assert.Equal(M(debit), row[5]);                  // = التعريف القديم حرفيًا
            Assert.Equal(M(credit), row[6]);
            Assert.Equal(M(debit >= credit ? debit - credit : 0m), row[7]);
            Assert.Equal(M(credit > debit ? credit - debit : 0m), row[8]);
            Assert.Equal(debit - credit, CloseNet(row));
        }

        // إجمالي الحسابات القابلة للترحيل = الإجمالي الخام لكل البنود (ميزان المراجعة التقليدي 865/865).
        Assert.Equal(M(865m), table.TotalsRows![0][5]);
        Assert.Equal(M(865m), table.TotalsRows![0][6]);
        Assert.Equal(M(0m), table.TotalsRows![0][3]);
        Assert.Equal(M(0m), table.TotalsRows![0][4]);
        Assert.Equal("كامل السجل (كل الفترات) | يشمل الحسابات الصفرية", table.ParamSummary);
    }


    // ══════════════════════ (17) القيود/البنود المحذوفة: التقرير مقابل دفتر الأستاذ والميزان القديم ══════════════════════

    /// <summary>
    /// أثر الحذف المنطقي على النتائج التاريخية — بالأدلة لا بالافتراض:
    /// كلٌّ من التقرير (استثناء صريح في محرّك الأرصدة) ودفتر الأستاذ والميزان القديم (عبر فلاتر
    /// الاستعلام العامة على JournalEntry/JournalEntryLine في AppDbContext) يستثني المحذوفات،
    /// فلا تتغيّر أي نتيجة تاريخية عند حذف قيد، وتبقى المطابقة بينها قائمة.
    /// </summary>
    [Fact]
    public async Task Soft_deleted_entries_are_excluded_consistently_by_report_ledger_and_legacy_trial_balance()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = await SeedAsync(ctx);
        var service = new ReportService(ctx);

        var reportBefore = await service.GetReportAsync("trial-balance", PeriodParams());
        var allBefore = await service.GetReportAsync("trial-balance", new Dictionary<string, string?> { ["includeZero"] = "true" });
        var ledgerBefore = await service.GetAccountLedgerAsync(ids.Cash1100, PeriodFrom, PeriodTo);
        var legacyBefore = await service.GetTrialBalanceAsync();

        // خط الأساس: التقرير = دفتر الأستاذ، وبلا فترة = الميزان القديم (كامل السجل: 865/865).
        Assert.Equal(260m, ledgerBefore.ClosingBalance);
        Assert.Equal(ledgerBefore.ClosingBalance, CloseNet(Row(reportBefore, "1100")));
        Assert.Equal(865m, legacyBefore.TotalDebit);
        Assert.Equal(865m, legacyBefore.TotalCredit);
        Assert.Equal(Math.Round(legacyBefore.TotalDebit, 2),
            decimal.Parse(allBefore.TotalsRows![0][5], CultureInfo.InvariantCulture));

        // قيد كامل محذوف منطقيًا (مدين 500 نقدية / دائن 500 موردون).
        var deleted = await AddEntryAsync(ctx, new DateTime(2025, 1, 20), "JE-DEL-GAP",
            (ids.Cash1100, 500m, 0m), (ids.Child21, 0m, 500m));
        deleted.IsDeleted = true;
        await ctx.SaveChangesAsync();

        var reportAfter = await service.GetReportAsync("trial-balance", PeriodParams());
        var allAfter = await service.GetReportAsync("trial-balance", new Dictionary<string, string?> { ["includeZero"] = "true" });
        var ledgerAfter = await service.GetAccountLedgerAsync(ids.Cash1100, PeriodFrom, PeriodTo);
        var legacyAfter = await service.GetTrialBalanceAsync();

        // (أ) التقرير: لا أثر للقيد المحذوف (استثناء صريح — وهو التعريف المعتمد).
        Assert.Equal(Row(reportBefore, "1100")[5], Row(reportAfter, "1100")[5]);
        Assert.Equal(Row(reportBefore, "1100")[7], Row(reportAfter, "1100")[7]);
        Assert.Equal(reportBefore.TotalsRows![0], reportAfter.TotalsRows![0]);

        // (ب) دفتر الأستاذ والميزان القديم: لا أثر أيضًا (فلاتر عامة على القيود والبنود).
        Assert.Equal(ledgerBefore.ClosingBalance, ledgerAfter.ClosingBalance);
        Assert.Equal(legacyBefore.TotalDebit, legacyAfter.TotalDebit);
        Assert.Equal(legacyBefore.TotalCredit, legacyAfter.TotalCredit);

        // (ج) المطابقة تبقى قائمة تمامًا بعد الحذف (لا فرق مخفي).
        Assert.Equal(ledgerAfter.ClosingBalance, CloseNet(Row(reportAfter, "1100")));
        Assert.Equal(Math.Round(legacyAfter.TotalDebit, 2),
            decimal.Parse(allAfter.TotalsRows![0][5], CultureInfo.InvariantCulture));
    }


}

/// <summary>
/// مطابقة تقرير «ميزان المراجعة الهرمي» على PostgreSQL حقيقي — قاعدة مؤقتة تُنشأ وتُحذف ذاتيًا
/// على خادم التطوير (لا تُلمس أي قاعدة منشورة أو بيانات عملاء). تتحقق من:
///  • كل حساب بحركة في التقرير = مجموع بنوده في القاعدة،
///  • مطابقة كل حساب بحركة مع دفتر الأستاذ لنفس الفترة (إشارة وطبيعة الحساب)،
///  • الإجماليين (التفصيلي مقابل ميزان المراجعة التقليدي، والعام من الجذور) وتطابق عمودي حركة الفترة،
///  • التوازن، وتفسير أي فرق بينهما بتقاصّ الجذور في ملاحظة التقرير.
/// يُتخطّى تلقائيًا عند غياب ERPSYSTEM_TEST_PG.
/// </summary>
public class TrialBalanceLivePgTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public TrialBalanceLivePgTests(AccountingPgFixture pg) => _pg = pg;

    /// <summary>صف الحساب في التقرير (المسافات البادئة للمستوى تُقلَّم).</summary>
    private static List<string> Row(GenericReportTable t, string code)
        => t.Rows.Single(r => r[0].Trim() == code);

    /// <summary>صافي الرصيد الختامي من عمودي الرصيد (مدين موجب / دائن سالب).</summary>
    private static decimal CloseNet(List<string> row)
        => decimal.Parse(row[7], CultureInfo.InvariantCulture) - decimal.Parse(row[8], CultureInfo.InvariantCulture);

    private static string M(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    [LivePgFact]
    public async Task Live_report_reconciles_with_the_ledger_and_the_legacy_trial_balance()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // حركات حقيقية عبر الخدمات (بيع نقدي + مشتريات آجلة) ⇒ قيود مرحّلة فعلية.
        var item = await _pg.CreateItemAsync("TB39-" + Guid.NewGuid().ToString("N")[..5], 10m, 6m, 50m);
        await using (var ctx = _pg.NewContext())
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(item, 3m, 10m));
        await using (var ctx = _pg.NewContext())
            await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx),
                new AccountingPgFixture.AllowAllManualPrice()).CreateAsync(_pg.NewPurchase(item, 2m, 6m));

        var day = DateTime.Today;
        var from = day.AddDays(-1);   // فترة [أمس، اليوم]: كل الحركات اليوم ⇒ رصيد افتتاحي = صفر

        GenericReportTable full, active, period;
        TrialBalanceDto legacy;
        Dictionary<Guid, (string Code, NormalBalance Nature)> accounts;
        Dictionary<Guid, (decimal Debit, decimal Credit)> raw;
        List<string> treeCodes;

        await using (var ctx = _pg.NewContext())
        {
            var service = new ReportService(ctx);
            // (أ) كامل السجل مع إظهار كل الحسابات ⇒ فحص الاكتمال (لا حذف صامت لأي حساب).
            full = await service.GetReportAsync("trial-balance",
                new Dictionary<string, string?> { ["includeZero"] = "true" });
            // (ب) كامل السجل بلا الحسابات الصفرية.
            active = await service.GetReportAsync("trial-balance",
                new Dictionary<string, string?> { ["includeZero"] = "false" });
            // (ج) فترة [أمس، اليوم] مع إظهار كل الحسابات ⇒ مطابقة دفتر الأستاذ.
            period = await service.GetReportAsync("trial-balance", new Dictionary<string, string?>
            {
                ["from"] = from.ToString("yyyy-MM-dd"),
                ["to"] = day.ToString("yyyy-MM-dd"),
                ["includeZero"] = "true"
            });
            legacy = await service.GetTrialBalanceAsync();

            accounts = (await ctx.Set<Account>().Where(a => !a.IsDeleted).ToListAsync())
                .ToDictionary(a => a.Id, a => (a.Code, a.NormalBalance));

            var liveEntryIds = await ctx.Set<JournalEntry>().Where(e => !e.IsDeleted).Select(e => e.Id).ToListAsync();
            var lines = await ctx.Set<JournalEntryLine>()
                .Where(l => !l.IsDeleted && liveEntryIds.Contains(l.JournalEntryId)).ToListAsync();
            raw = lines.GroupBy(l => l.AccountId)
                .ToDictionary(g => g.Key, g => (g.Sum(x => x.DebitAmount), g.Sum(x => x.CreditAmount)));

            var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();
            treeCodes = tree.Flattened.Select(n => n.Code).ToList();
        }

        Assert.True(raw.Count > 0, "يجب وجود حسابات بحركة في القاعدة المؤقتة");

        // (0) الاكتمال: بلا فترة ومع includeZero=true يجب أن يظهر **كل حساب غير محذوف** وبالترتيب
        //     الهرمي نفسه لمحرّك الأرصدة (فحص صريح يمنع الحذف الصامت لأي حساب من الميزان).
        var treeCodeSet = treeCodes.ToHashSet();
        var notInTree = accounts.Values.Where(a => !treeCodeSet.Contains(a.Code)).Select(a => a.Code).ToList();
        Assert.True(notInTree.Count == 0,
            "حسابات غير محذوفة لم تظهر في شجرة الأرصدة (أب محذوف/علاقة مكسورة): " + string.Join(", ", notInTree));
        Assert.Equal(treeCodes.ToArray(), full.Rows.Select(r => r[0].Trim()).ToArray());

        // (1) بلا فترة: كل صف = مجموع بنود الحساب (التعريف القديم) وبلا رصيد افتتاحي مفصول،
        //     والحسابات ذات الأرصدة الصفرية تظهر مع includeZero=true وتُخفى مع false.
        foreach (var (accountId, sums) in raw)
        {
            var row = Row(full, accounts[accountId].Code);
            Assert.Equal(M(sums.Debit), row[5]);
            Assert.Equal(M(sums.Credit), row[6]);
            Assert.Equal(M(0m), row[3]);
            Assert.Equal(M(0m), row[4]);
        }
        var activeCodes = active.Rows.Select(r => r[0].Trim()).ToHashSet();
        var zeroActivity = raw.Where(x => x.Value.Debit == 0m && x.Value.Credit == 0m).Select(x => accounts[x.Key].Code).ToList();
        foreach (var (accountId, sums) in raw.Where(x => x.Value.Debit != 0m || x.Value.Credit != 0m))
            Assert.Contains(accounts[accountId].Code, activeCodes);          // حساب ذو حركة: يظهر دائمًا
        foreach (var code in zeroActivity)
        {
            Assert.DoesNotContain(code, activeCodes);                        // صفر بلا أبناء: يُخفى كما هو مقصود
            Assert.Equal(M(0m), Row(full, code)[5]);                         // ويظهر صريحًا (بصفر) عند تفعيل الإظهار
            Assert.Equal(M(0m), Row(full, code)[7]);
        }

        // (2) مطابقة كل حساب بحركة مع دفتر الأستاذ لنفس الفترة (قاعدة طازجة ⇒ لا محذوفات).
        await using (var ctx = _pg.NewContext())
        {
            var service = new ReportService(ctx);
            foreach (var (accountId, _) in raw)
            {
                var (code, nature) = accounts[accountId];
                var sign = nature == NormalBalance.Debit ? 1m : -1m;
                var ledger = await service.GetAccountLedgerAsync(accountId, from, day);
                var row = Row(period, code);

                Assert.Equal(ledger.Lines.Sum(l => l.Debit), decimal.Parse(row[5], CultureInfo.InvariantCulture));
                Assert.Equal(ledger.Lines.Sum(l => l.Credit), decimal.Parse(row[6], CultureInfo.InvariantCulture));
                Assert.Equal(ledger.OpeningBalance * sign,
                    decimal.Parse(row[3], CultureInfo.InvariantCulture) - decimal.Parse(row[4], CultureInfo.InvariantCulture));
                Assert.Equal(ledger.ClosingBalance * sign, CloseNet(row));
            }
        }

        // (3) الإجماليات: عمودا حركة الفترة متطابقان بين الصفّين، والتفصيلي = ميزان المراجعة التقليدي.
        var details = period.TotalsRows![0];
        var grand = period.TotalsRows![1];
        Assert.Equal(details[5], grand[5]);
        Assert.Equal(details[6], grand[6]);
        Assert.Equal(Math.Round(legacy.TotalDebit, 2),
            decimal.Parse(active.TotalsRows![0][5], CultureInfo.InvariantCulture));
        Assert.Equal(Math.Round(legacy.TotalCredit, 2),
            decimal.Parse(active.TotalsRows![0][6], CultureInfo.InvariantCulture));

        // (4) التوازن في الصفّين، وأي فرق بينهما مفسَّر في الملاحظة (لا فرق صامت غير مفسر).
        Assert.Equal(details[7], details[8]);
        Assert.Equal(grand[7], grand[8]);
        Assert.DoesNotContain("غير متوازن", period.FooterNote);
        if (details[7] != grand[7] || details[8] != grand[8])
        {
            Assert.Contains("تقاصّ أرصدة متعاكسة داخل الجذور", period.FooterNote);
            Assert.DoesNotContain("لا يطابق التقاصّ", period.FooterNote);
        }
    }
}

