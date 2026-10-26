using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// ميزان المراجعة الهرمي — اختبارات المستويات والترتيب والإجماليات والتصدير
/// (مفتاح التقرير: trial-balance، ونفس الجدول تستهلكه شاشة مركز التقارير والصفحة القديمة ومسارات التصدير).
///
/// دليل الحسابات المستخدم يُثبت أن المستوى مشتق من علاقة الأب/الابن المحفوظة لا من طول الكود:
///   100 (أب — 3 محارف) → 2 (ابن — محرف واحد) → 33 (حفيد — محرفان) → 4 (المستوى الرابع — محرف واحد)
///   100 → 5 (ابن/ورقة — له حركة)
///   100 → 6 (ابن/ورقة — بلا حركة ⇒ رصيد صفري)
///   7   (أب) → 70 (ابن/ورقة — دائن)
/// الفترة: 2025-01-01 → 2025-01-31، والرصيد الافتتاحي 2024-12-31.
/// </summary>
public class TrialBalanceHierarchyTests
{
    private static readonly DateTime PeriodFrom = new(2025, 1, 1);
    private static readonly DateTime PeriodTo = new(2025, 1, 31);
    private const string Nbsp = "\u00A0";

    private static Account NewAccount(string code, AccountType type, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(), Code = code, NameAr = "حساب " + code, NameEn = "Account " + code,
        AccountType = type,
        NormalBalance = type is AccountType.Asset or AccountType.Expense ? NormalBalance.Debit : NormalBalance.Credit,
        ParentAccountId = parentId, IsActive = true, CreatedAt = DateTime.UtcNow
    };

    private static async Task<JournalEntry> AddEntryAsync(AppDbContext ctx, DateTime date, string number,
        params (Guid AccountId, decimal Debit, decimal Credit)[] legs)
    {
        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(), EntryNumber = number, EntryType = JournalEntryType.Manual,
            EntryDate = date, Description = number, CreatedAt = DateTime.UtcNow
        };
        ctx.Set<JournalEntry>().Add(entry);
        foreach (var leg in legs)
            ctx.Set<JournalEntryLine>().Add(new JournalEntryLine
            {
                Id = Guid.NewGuid(), JournalEntryId = entry.Id, AccountId = leg.AccountId,
                DebitAmount = leg.Debit, CreditAmount = leg.Credit, CreatedAt = DateTime.UtcNow
            });
        await ctx.SaveChangesAsync();
        return entry;
    }

    private static async Task SeedAsync(AppDbContext ctx)
    {
        var a100 = NewAccount("100", AccountType.Asset);
        var a2 = NewAccount("2", AccountType.Asset, a100.Id);
        var a33 = NewAccount("33", AccountType.Asset, a2.Id);
        var a4 = NewAccount("4", AccountType.Asset, a33.Id);
        var a5 = NewAccount("5", AccountType.Asset, a100.Id);
        var a6 = NewAccount("6", AccountType.Asset, a100.Id);   // بلا حركة
        var a7 = NewAccount("7", AccountType.Liability);
        var a70 = NewAccount("70", AccountType.Liability, a7.Id);

        ctx.Set<Account>().AddRange(a100, a2, a33, a4, a5, a6, a7, a70);
        await ctx.SaveChangesAsync();

        await AddEntryAsync(ctx, new DateTime(2024, 12, 31), "JE-OB", (a4.Id, 200m, 0m), (a70.Id, 0m, 200m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 5), "JE-1", (a4.Id, 100m, 0m), (a70.Id, 0m, 100m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 6), "JE-2", (a5.Id, 40m, 0m), (a70.Id, 0m, 40m));
    }

    private static Dictionary<string, string?> Pars(params (string Key, string? Value)[] extra)
    {
        var d = new Dictionary<string, string?>
        {
            ["from"] = PeriodFrom.ToString("yyyy-MM-dd"),
            ["to"] = PeriodTo.ToString("yyyy-MM-dd")
        };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    private static List<string> Row(GenericReportTable t, string code)
        => t.Rows.Single(r => r[0].Trim() == code);

    // ═════════ (1) المستوى من علاقة الأب/الابن لا من طول الكود ═════════

    [Fact]
    public async Task Levels_come_from_parent_child_relation_not_code_length()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var t = await new ReportService(ctx).GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        // «100» أب بمستوى 1 رغم أن كوده أطول من كود الحفيد «33» (مستوى 3) والحفيد الأعمق «4» (مستوى 4).
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level1Ar, Row(t, "100")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level2Ar, Row(t, "2")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level3Ar, Row(t, "33")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level4Ar, Row(t, "4")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level2Ar, Row(t, "70")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level1Ar, Row(t, "7")[2]);
    }

    // ═════════ (2) الترتيب الهرمي والمسافات البادئة ═════════

    [Fact]
    public async Task Four_levels_render_in_pre_order_with_indentation_and_level_names()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var t = await new ReportService(ctx).GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        Assert.Equal(new[] { "100", "2", "33", "4", "5", "7", "70" },
            t.Rows.Select(r => r[0].Trim()).ToArray());   // «6» بلا حركة ⇒ مخفي افتراضيًا

        Assert.Equal("100", t.Rows[0][0]);                          // أب: بلا إزاحة
        Assert.Equal(Nbsp + Nbsp + Nbsp + "2", Row(t, "2")[0]);     // ابن: 3 مسافات
        Assert.Equal(new string('\u00A0', 6) + "33", Row(t, "33")[0]);   // حفيد: 6
        Assert.Equal(new string('\u00A0', 9) + "4", Row(t, "4")[0]);     // مستوى 4: 9
        Assert.Equal(Nbsp + Nbsp + Nbsp + "70", Row(t, "70")[0]);

        // الاسم يحمل نفس الإزاحة أيضًا (لقراءة هرمية واضحة في الشاشة والتصدير).
        Assert.Equal(Nbsp + Nbsp + Nbsp + "حساب 2", Row(t, "2")[1]);
    }

    // ═════════ (3) تمييز التجميعي/التفصيلي (بما فيه الحسابات بلا أبناء) ═════════

    [Fact]
    public async Task Parent_rows_are_group_and_childless_rows_are_detail()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var t = await new ReportService(ctx).GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        var kinds = t.Rows.Zip(t.RowKinds!, (row, kind) => (Code: row[0].Trim(), Kind: kind)).ToDictionary(x => x.Code, x => x.Kind);
        Assert.Equal("group", kinds["100"]);
        Assert.Equal("group", kinds["2"]);
        Assert.Equal("group", kinds["33"]);
        Assert.Equal("detail", kinds["4"]);   // أعمق ورقة
        Assert.Equal("detail", kinds["5"]);   // ابن بلا أبناء
        Assert.Equal("detail", kinds["70"]);  // ابن بلا أبناء
        Assert.Equal("group", kinds["7"]);
    }

    // ═════════ (4) الحسابات ذات الرصيد الصفري ═════════

    [Fact]
    public async Task Zero_balance_childless_account_is_hidden_by_default_and_shown_when_requested()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var service = new ReportService(ctx);

        var hidden = await service.GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);
        Assert.DoesNotContain(hidden.Rows, r => r[0].Trim() == "6");
        Assert.Contains("الحسابات ذات الحركة فقط", hidden.ParamSummary);

        var shown = await service.GetTrialBalanceReportAsync(PeriodFrom, PeriodTo, null, includeZero: true);
        var row6 = Row(shown, "6");
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level2Ar, row6[2]);
        Assert.Equal("0.00", row6[7]);   // لا رصيد ختامي مدين
        Assert.Equal("0.00", row6[8]);   // ولا دائن — ولا أرقام سالبة في أعمدة الأرصدة
        Assert.Contains("يشمل الحسابات الصفرية", shown.ParamSummary);

        // الإجماليات لا تتأثر بإظهار الصفري (رقم ثابت).
        Assert.Equal(shown.TotalsRows![0][7], hidden.TotalsRows![0][7]);
    }

    // ═════════ (5) نفس الجدول لكل الأسطح (مركز التقارير + الصفحة القديمة + التصدير) ═════════

    [Fact]
    public async Task Legacy_surface_returns_the_same_table_as_the_report_center()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var service = new ReportService(ctx);

        // المسار الذي تستخدمه الصفحة القديمة ومسارا /export/trial-balance/{excel,pdf}
        var legacy = await service.GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);
        // نفس المفتاح في مركز التقارير
        var center = await service.GetReportAsync("trial-balance", Pars());

        Assert.Equal(center.TitleAr, legacy.TitleAr);
        Assert.Equal(center.Headers, legacy.Headers);
        Assert.Equal(center.HeaderGroups!.Select(g => (g.Title, g.Span)), legacy.HeaderGroups!.Select(g => (g.Title, g.Span)));
        Assert.Equal(center.Rows.Count, legacy.Rows.Count);
        for (var r = 0; r < center.Rows.Count; r++)
            Assert.Equal(center.Rows[r], legacy.Rows[r]);
        Assert.Equal(center.TotalsRows![1], legacy.TotalsRows![1]);
        Assert.Equal(center.FooterNote, legacy.FooterNote);

        // اسم موحّد لملف التصدير (Excel/PDF) مشتق من نهاية الفترة.
        Assert.Equal($"trial-balance-{PeriodTo:yyyyMMdd}", legacy.ExportFileName);
        Assert.Equal(legacy.ExportFileName, center.ExportFileName);
    }

    // ═════════ (6) الإجماليات: من الجذور بلا ازدواجية ═════════

    [Fact]
    public async Task Totals_come_from_roots_without_double_counting()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var t = await new ReportService(ctx).GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        // إجمالي حركة الفترة: 100 (من 4) + 40 (من 5) = 140 مدينًا و140 دائنًا.
        Assert.Equal("140.00", t.TotalsRows![0][5]);
        Assert.Equal("140.00", t.TotalsRows![0][6]);
        Assert.Equal("140.00", t.TotalsRows![1][5]);
        Assert.Equal("140.00", t.TotalsRows![1][6]);

        // الجمع الساذج لكل الصفوف يعطي 480 (تضاعف) — وهو ما يجب تجنّبه.
        var naive = t.Rows.Sum(r => decimal.Parse(r[5].Replace(",", "")));
        Assert.Equal(480m, naive);
        Assert.NotEqual(naive, decimal.Parse(t.TotalsRows![1][5].Replace(",", "")));

        // التوازن: مدين = دائن في الصفّين.
        Assert.Equal(t.TotalsRows![0][7], t.TotalsRows![0][8]);
        Assert.Equal(t.TotalsRows![1][7], t.TotalsRows![1][8]);
        Assert.Contains("متوازن", t.FooterNote);
    }

    // ═════════ (7) تصدير Excel يحفظ الهرمية والمستويات والإجماليات ═════════

    [Fact]
    public async Task Excel_export_keeps_levels_indentation_and_both_totals()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var service = new ReportService(ctx);
        var t = await service.GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        var bytes = service.ExportReportExcel(t);
        Assert.True(bytes.Length > 0);

        using var ms = new MemoryStream(bytes);
        using var wb = new ClosedXML.Excel.XLWorkbook(ms);
        var ws = wb.Worksheet(1);

        // ترويسة + مجموعات الأعمدة + تسعة عناوين
        Assert.Equal(t.TitleAr, ws.Cell(1, 1).GetString());
        Assert.Equal(t.ParamSummary, ws.Cell(2, 1).GetString());
        Assert.Equal("البيان", ws.Cell(4, 1).GetString());
        Assert.Equal("الأرصدة الختامية (حتى نهاية الفترة)", ws.Cell(4, 8).GetString());
        Assert.Equal(9, t.Headers.Count);

        // الصف الأول = الجذر بلا إزاحة، وصف الابن يحمل الإزاحة والمستوى الصحيح
        Assert.Equal("100", ws.Cell(6, 1).GetString());
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level1Ar, ws.Cell(6, 3).GetString());
        Assert.Equal(Nbsp + Nbsp + Nbsp + "2", ws.Cell(7, 1).GetString());
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level2Ar, ws.Cell(7, 3).GetString());

        // صفّا الإجماليات في نهاية الملف (بلا جمع صفوف التجميع مرتين)
        var totalsStart = 6 + t.Rows.Count;
        Assert.Contains("إجمالي الحسابات القابلة للترحيل", ws.Cell(totalsStart, 2).GetString());
        Assert.Contains("الإجمالي العام", ws.Cell(totalsStart + 1, 2).GetString());
        Assert.Equal(t.TotalsRows![1][7], ws.Cell(totalsStart + 1, 8).GetString());
        Assert.Equal(t.TotalsRows![1][8], ws.Cell(totalsStart + 1, 9).GetString());
    }

    // ═════════ (8) شجرة عريضة: أب له ابنان ولكل ابن حفيدان ═════════

    [Fact]
    public async Task Wide_tree_two_children_each_with_two_grandchildren_keeps_order_indent_and_totals()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var p = NewAccount("900", AccountType.Asset);
        var c1 = NewAccount("910", AccountType.Asset, p.Id);
        var c2 = NewAccount("920", AccountType.Asset, p.Id);
        var g1 = NewAccount("9110", AccountType.Asset, c1.Id);
        var g2 = NewAccount("9120", AccountType.Asset, c1.Id);
        var g3 = NewAccount("9210", AccountType.Asset, c2.Id);
        var g4 = NewAccount("9220", AccountType.Asset, c2.Id);
        var rev = NewAccount("4900", AccountType.Revenue);
        ctx.Set<Account>().AddRange(p, c1, c2, g1, g2, g3, g4, rev);
        await ctx.SaveChangesAsync();
        await AddEntryAsync(ctx, new DateTime(2025, 1, 10), "JE-W1", (g1.Id, 100m, 0m), (rev.Id, 0m, 100m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 11), "JE-W2", (g2.Id, 50m, 0m), (rev.Id, 0m, 50m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 12), "JE-W3", (g3.Id, 25m, 0m), (rev.Id, 0m, 25m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 13), "JE-W4", (g4.Id, 75m, 0m), (rev.Id, 0m, 75m));

        var t = await new ReportService(ctx).GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        var order = t.Rows.Select(r => r[0].Trim()).ToList();
        Assert.True(order.IndexOf("900") < order.IndexOf("910"));
        Assert.True(order.IndexOf("910") < order.IndexOf("9110"));
        Assert.True(order.IndexOf("9110") < order.IndexOf("9120"));
        Assert.True(order.IndexOf("9120") < order.IndexOf("920"));
        Assert.True(order.IndexOf("920") < order.IndexOf("9210"));
        Assert.True(order.IndexOf("9210") < order.IndexOf("9220"));

        Assert.Equal(Nbsp + Nbsp + Nbsp + "910", Row(t, "910")[0]);
        Assert.Equal(Nbsp + Nbsp + Nbsp + Nbsp + Nbsp + Nbsp + "9110", Row(t, "9110")[0]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level1Ar, Row(t, "900")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level2Ar, Row(t, "910")[2]);
        Assert.Equal(ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level3Ar, Row(t, "9110")[2]);

        Assert.Equal("150.00", Row(t, "910")[5]);
        Assert.Equal("100.00", Row(t, "920")[5]);
        Assert.Equal("250.00", Row(t, "900")[5]);
        Assert.Equal("250.00", t.TotalsRows![0][5]);
        Assert.Equal(t.TotalsRows![0][5], t.TotalsRows![1][5]);
    }

    // ═════════ (9) حركة تاريخية مباشرة على حساب تجميعي: تُعرض وتُنبه ولا تُعدَّل ═════════

    [Fact]
    public async Task Aggregate_account_with_direct_historical_movement_is_shown_and_flagged_without_data_change()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var p = NewAccount("800", AccountType.Asset);
        var c = NewAccount("810", AccountType.Asset, p.Id);
        var rev = NewAccount("4800", AccountType.Revenue);
        ctx.Set<Account>().AddRange(p, c, rev);
        await ctx.SaveChangesAsync();
        await AddEntryAsync(ctx, new DateTime(2025, 1, 5), "JE-L1", (p.Id, 30m, 0m), (rev.Id, 0m, 30m));
        await AddEntryAsync(ctx, new DateTime(2025, 1, 6), "JE-L2", (c.Id, 70m, 0m), (rev.Id, 0m, 70m));
        var linesBefore = await ctx.Set<JournalEntryLine>().CountAsync();

        var t = await new ReportService(ctx).GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        Assert.Equal("100.00", Row(t, "800")[5]);
        Assert.Equal("70.00", Row(t, "810")[5]);
        Assert.Contains("حركة مباشرة على حسابات تجميعية", t.FooterNote);
        Assert.Contains("800", t.FooterNote);
        Assert.Equal(linesBefore, await ctx.Set<JournalEntryLine>().CountAsync());
        Assert.Equal(30m, await ctx.Set<JournalEntryLine>().Where(l => l.AccountId == p.Id).SumAsync(l => l.DebitAmount));

        // التحذير نفسه مكتوب في ملف Excel (اتساق الشاشة ↔ PDF ↔ Excel)
        var svc = new ReportService(ctx);
        using var ms = new MemoryStream(svc.ExportReportExcel(t));
        using var wb = new ClosedXML.Excel.XLWorkbook(ms);
        var all = string.Join("|", wb.Worksheet(1).CellsUsed().Select(c => c.GetString()));
        Assert.Contains("حركة مباشرة على حسابات تجميعية", all);
    }

    // ═════════ (10) توحيد «التقارير المخصصة» مع التقرير الهرمي الرئيسي ═════════

    [Fact]
    public async Task Custom_report_trial_balance_variant_returns_the_same_hierarchical_table()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var def = new CustomReportDefinition
        {
            Id = Guid.NewGuid(), Name = "TB variant", IsEnabled = true,
            ReportType = CustomReportType.ExistingReportVariant,
            ExistingReportKey = "TrialBalance", CreatedAt = DateTime.UtcNow
        };
        ctx.Set<CustomReportDefinition>().Add(def);
        await ctx.SaveChangesAsync();

        var svc = new ERPSystem.Infrastructure.Services.CustomReportService(
            ctx, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new ReportService(ctx));

        var pars = Pars().ToDictionary(k => k.Key, k => (object?)k.Value);
        var result = await svc.ExecuteAsync(def.Id, pars);
        var expected = await new ReportService(ctx).GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        Assert.Equal(9, result.Columns.Count);
        Assert.Contains(result.Columns, c => c.StartsWith("مدين ("));
        Assert.Equal(expected.Rows.Count + (expected.TotalsRows?.Count ?? 0), result.Rows.Count);
        Assert.Equal(expected.Rows[0][0], result.Rows[0][result.Columns[0]]?.ToString());
        Assert.Equal(expected.Rows[0][2], result.Rows[0]["المستوى"]?.ToString());
    }

    // ═════════ (11) مستويات الصفوف مُصدَّرة للعرض الموحّد (نفس القيم في الشاشة وPDF) ═════════

    [Fact]
    public async Task Row_levels_are_exposed_and_match_the_real_tree_depth()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        await SeedAsync(ctx);
        var t = await new ReportService(ctx).GetTrialBalanceReportAsync(PeriodFrom, PeriodTo);

        Assert.NotNull(t.RowLevels);
        Assert.Equal(t.Rows.Count, t.RowLevels!.Count);

        var index = t.Rows.Select((r, i) => (Code: r[0].Trim(), Index: i)).ToDictionary(x => x.Code, x => x.Index);

        Assert.Equal(1, t.RowLevels[index["100"]]);   // أب
        Assert.Equal(2, t.RowLevels[index["2"]]);     // ابن
        Assert.Equal(3, t.RowLevels[index["33"]]);    // حفيد
        Assert.Equal(4, t.RowLevels[index["4"]]);     // أعمق من حفيد (لا يُصنَّف كأحد الثلاثة)
        Assert.Equal(1, t.RowLevels[index["7"]]);     // أب آخر
        Assert.Equal(2, t.RowLevels[index["70"]]);    // ابن

        // عمود «المستوى» المعروض يطابق المستوى المُصدَّر لكل صف (لا تناقض بين العمود والتنسيق)
        for (var i = 0; i < t.Rows.Count; i++)
        {
            var expected = t.RowLevels[i] switch
            {
                1 => ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level1Ar,
                2 => ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level2Ar,
                3 => ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level3Ar,
                4 => ERPSystem.Application.DTOs.Accounts.AccountTreeLevels.Level4Ar,
                var l => "مستوى " + l
            };
            Assert.Equal(expected, t.Rows[i][2]);
        }
    }
}
