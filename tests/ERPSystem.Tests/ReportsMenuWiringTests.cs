using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Application.Services;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// إصلاح قائمة التقارير (REPORTS-MENU-FIX) — اختبار ربط طبقة التطبيق:
///   • الأقسام المعروضة في اللوحة `/reports` تُبنى من فهرس التقارير نفسه عبر ReportKeys/ByKeys.
///   • كل مفتاح معروض في اللوحة **يفتح فعليًا** في محرّك التقارير (GetReportAsync) بعنوان وأعمدة.
///   • لا مفاتيح مكررة/مجهولة، والعناوين العربية والإنجليزية موجودة في الفهرس (لا أسماء في الواجهة).
/// (إثبات تسجيل المسارات الحقيقي في HTTP يتم بسكربت التحقق `tb-reports-menu-verify.ps1`.)
/// </summary>
public class ReportsMenuWiringTests
{
    /// <summary>المفاتيح الثمانية الفعلية كما وثّقها التنفيذ (لا تخمين).</summary>
    private static readonly string[] DocumentedEight =
    {
        "receipt-vouchers",
        "payment-vouchers",
        "receipt-voucher-entries",
        "payment-voucher-entries",
        "sales-invoice-entries",
        "sales-return-entries",
        "purchase-invoice-entries",
        "purchase-return-entries"
    };

    [Fact]
    public void Dashboard_groups_cover_exactly_the_eight_documented_keys()
    {
        var grouped = ReportsCatalog.VoucherReportKeys
            .Concat(ReportsCatalog.DocumentEntryReportKeys)
            .ToList();

        Assert.Equal(DocumentedEight.Length, grouped.Count);
        Assert.Equal(DocumentedEight, grouped);
        Assert.Equal(DocumentedEight.Length, grouped.Distinct().Count());
    }

    [Fact]
    public void Every_grouped_key_resolves_with_bilingual_titles()
    {
        foreach (var key in DocumentedEight)
        {
            var defs = ReportsCatalog.ByKeys(new[] { key });

            Assert.Single(defs);
            var def = defs[0];
            Assert.Equal(key, def.Key);
            Assert.False(string.IsNullOrWhiteSpace(def.TitleAr), $"{key}: عنوان عربي مفقود");
            Assert.False(string.IsNullOrWhiteSpace(def.TitleEn), $"{key}: عنوان إنجليزي مفقود");
        }
    }

    [Fact]
    public void ByKeys_ignores_unknown_keys_and_preserves_requested_order()
    {
        var defs = ReportsCatalog.ByKeys(new[] { "payment-vouchers", "not-a-real-report", "receipt-vouchers" });

        Assert.Equal(2, defs.Count);
        Assert.Equal("payment-vouchers", defs[0].Key);
        Assert.Equal("receipt-vouchers", defs[1].Key);
    }

    [Fact]
    public void Catalog_has_no_duplicate_keys_and_groups_reference_existing_keys()
    {
        var allKeys = ReportsCatalog.All.Select(d => d.Key).ToList();
        Assert.Equal(allKeys.Count, allKeys.Distinct().Count());

        foreach (var key in ReportsCatalog.VoucherAndDocumentEntryKeys())
            Assert.Contains(key, allKeys);
    }

    [Fact]
    public async Task Every_dashboard_key_opens_through_the_report_engine()
    {
        // نفس محرّك التقارير الذي تستدعيه اللوحة والصفحة العامة ومسارات التصدير.
        using var ctx = AccountingSafetyFixtures.NewContext();
        var reports = new ReportService(ctx);
        var pars = new Dictionary<string, string?>
        {
            ["from"] = DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd"),
            ["to"] = DateTime.Today.ToString("yyyy-MM-dd")
        };

        foreach (var key in DocumentedEight)
        {
            var table = await reports.GetReportAsync(key, pars);

            Assert.False(string.IsNullOrWhiteSpace(table.TitleAr), $"{key}: التقرير لا يعيد عنوانًا");
            Assert.NotEmpty(table.Headers);
            Assert.NotNull(table.Rows);
        }
    }

    [Fact]
    public async Task Every_dashboard_key_exports_to_excel_bytes()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var reports = new ReportService(ctx);
        var pars = new Dictionary<string, string?>
        {
            ["from"] = DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd"),
            ["to"] = DateTime.Today.ToString("yyyy-MM-dd")
        };

        foreach (var key in DocumentedEight)
        {
            var table = await reports.GetReportAsync(key, pars);
            var bytes = reports.ExportReportExcel(table);

            // ملف xlsx صالح = أرشيف ZIP (يبدأ بـ PK) وبحجم معقول (لا ملف فارغ/تالف).
            Assert.True(bytes.Length > 1000, $"{key}: ملف Excel أصغر من المتوقع ({bytes.Length} بايت)");
            Assert.Equal((byte)'P', bytes[0]);
            Assert.Equal((byte)'K', bytes[1]);
        }
    }

    [Fact]
    public async Task Existing_reports_keys_still_open_after_the_menu_fix()
    {
        // نفس محرّك التقارير — تقارير قائمة لا تتطلب بيانات حسابات (تغطية trial-balance/general-ledger
        // على بيئة PostgreSQL الحقيقية موجودة في VoucherReportPgTests.Existing_reports_still_work_after_phase_d).
        using var ctx = AccountingSafetyFixtures.NewContext();
        var reports = new ReportService(ctx);
        var pars = new Dictionary<string, string?>
        {
            ["from"] = DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd"),
            ["to"] = DateTime.Today.ToString("yyyy-MM-dd")
        };

        foreach (var key in new[] { "journal-register", "expenses-by-category", "sales-by-customer", "sales-tax-register", "ar-aging", "ap-aging" })
        {
            var table = await reports.GetReportAsync(key, pars);
            Assert.False(string.IsNullOrWhiteSpace(table.TitleAr), $"{key}: انحدار في تقرير قائم");
            Assert.NotEmpty(table.Headers);
        }
    }
}
