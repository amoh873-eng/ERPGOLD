using System.Security.Claims;
using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة 5 — الصلاحيات والواجهات الفعلية (2026-10-25).
///
/// (أ) قواعد تصريح الفترات المحاسبية: فشل مغلق، وتستند إلى أذونات موديول المحاسبة الموجودة
///     (Accounting.View للعرض · Accounting.Edit للإغلاق/إعادة الفتح) بلا إنشاء إذن جديد.
/// (ب) عمليات الأرصدة الافتتاحية: رفض بلا صلاحية مع **صفر كتابة**.
/// (ج) عقود الواجهة المصدرية: المسارات وحرّاس الصلاحية والخدمات المستخدمة في الشاشات
///     (الأرصدة الافتتاحية · الفترات · المطابقة) + مفاتيح التعريب AR/EN.
///
/// ⚠️ لا يوجد في هذه البيئة أي مُشغّل متصفح (لا Playwright/Selenium/bUnit في المستودع)، لذلك
///     **لا يمكن** تنفيذ اختبار متصفح فعلي؛ ما يلي تحقق مصدري/عقدي مُعلن بصراحة، وليس اختبار متصفح.
///     نتيجة فحص توفّر أدوات المتصفح تُكتب في ملف الدليل <c>_p6_ui_browser.txt</c>.
/// </summary>
public class AccountingAccessAndUiContractTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public AccountingAccessAndUiContractTests(AccountingPgFixture pg) => _pg = pg;

    private const string BrowserEvidenceFile = @"D:\erpfix-pu01\_p6_ui_browser.txt";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Web")))
            dir = dir.Parent;

        Assert.True(dir is not null, "تعذر تحديد جذر المستودع من مجلد تشغيل الاختبارات");
        return dir!.FullName;
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"ملف الواجهة غير موجود: {path}");
        return File.ReadAllText(path);
    }

    private static ClaimsPrincipal Principal(string[] roles, string[] permissionClaims,
        bool authenticated = true)
    {
        var identity = new ClaimsIdentity(
            authenticated ? "Test" : null,
            ClaimTypes.Name, ClaimTypes.Role);
        foreach (var r in roles) identity.AddClaim(new Claim(ClaimTypes.Role, r));
        foreach (var p in permissionClaims) identity.AddClaim(new Claim("Permission", p));
        return new ClaimsPrincipal(identity);
    }

    // ═══════════ (أ) قواعد تصريح الفترات المحاسبية ═══════════

    [Fact]
    public void Period_rules_grant_admin_and_super_admin_everything()
    {
        foreach (var role in new[] { "Admin", "SuperAdmin", "admin", "SUPERADMIN" })
        {
            var user = Principal(new[] { role }, Array.Empty<string>());
            Assert.True(AccountingPeriodRules.IsAuthorized(user, AccountingPeriodRules.ViewAction));
            Assert.True(AccountingPeriodRules.IsAuthorized(user, AccountingPeriodRules.EditAction));
        }
    }

    [Fact]
    public void Period_rules_require_the_exact_existing_module_permission_and_never_invent_one()
    {
        // Accounting.View ⇒ عرض فقط
        var viewer = Principal(new[] { "Accountant" }, new[] { "Accounting.View" });
        Assert.True(AccountingPeriodRules.IsAuthorized(viewer, AccountingPeriodRules.ViewAction));
        Assert.False(AccountingPeriodRules.IsAuthorized(viewer, AccountingPeriodRules.EditAction));

        // Accounting.Edit ⇒ إغلاق/إعادة فتح، وليس عرضًا (القاعدة المعلنة: مطابقة دقيقة للفعل المطلوب)
        var editor = Principal(new[] { "Accountant" }, new[] { "Accounting.Edit" });
        Assert.True(AccountingPeriodRules.IsAuthorized(editor, AccountingPeriodRules.EditAction));
        Assert.False(AccountingPeriodRules.IsAuthorized(editor, AccountingPeriodRules.ViewAction));

        // لا يوجد إذن جديد باسم مختلف: Accounting.Close لا يمنح شيئًا
        var guessed = Principal(new[] { "Accountant" }, new[] { "Accounting.Close" });
        Assert.False(AccountingPeriodRules.IsAuthorized(guessed, AccountingPeriodRules.EditAction));
        Assert.False(AccountingPeriodRules.IsAuthorized(guessed, AccountingPeriodRules.ViewAction));
    }

    [Fact]
    public void Period_rules_are_fail_closed_without_identity_or_without_module_permission()
    {
        var anonymous = Principal(Array.Empty<string>(), Array.Empty<string>(), authenticated: false);
        Assert.False(AccountingPeriodRules.IsAuthorized(anonymous, AccountingPeriodRules.ViewAction));
        Assert.False(AccountingPeriodRules.IsAuthorized(anonymous, AccountingPeriodRules.EditAction));

        var salesOnly = Principal(new[] { "Sales" }, new[] { "Sales.View", "Sales.Edit" });
        Assert.False(AccountingPeriodRules.IsAuthorized(salesOnly, AccountingPeriodRules.ViewAction));
        Assert.False(AccountingPeriodRules.IsAuthorized(salesOnly, AccountingPeriodRules.EditAction));

        // المستخدم المصادَق عليه بلا أدوار وبلا أذونات ⇒ لا تصريح
        var bare = Principal(Array.Empty<string>(), Array.Empty<string>());
        Assert.False(AccountingPeriodRules.IsAuthorized(bare, AccountingPeriodRules.ViewAction));

        // الدور «Accounting» بلا أذونات صريحة للموديول ⇒ وصول كامل (السلوك الحالي الموثَّق)
        var roleBased = Principal(new[] { "Accounting" }, Array.Empty<string>());
        Assert.True(AccountingPeriodRules.IsAuthorized(roleBased, AccountingPeriodRules.ViewAction));
        Assert.True(AccountingPeriodRules.IsAuthorized(roleBased, AccountingPeriodRules.EditAction));

        // أدوار موديولات أخرى لا تمنح المحاسبة
        var otherModuleRole = Principal(new[] { "Purchasing" }, new[] { "Purchasing.View" });
        Assert.False(AccountingPeriodRules.IsAuthorized(otherModuleRole, AccountingPeriodRules.EditAction));
    }

    // ═══════════ (ب) الأرصدة الافتتاحية: فشل مغلق بلا كتابة ═══════════

    [LivePgFact]
    public async Task Opening_balance_operations_are_rejected_without_permission_and_write_nothing()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        int batchesBefore, entriesBefore;
        await using (var ctx = _pg.NewContext())
        {
            batchesBefore = await ctx.Set<OpeningBalanceBatch>().CountAsync();
            entriesBefore = await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
        }

        // تصريح رافض صريح — نفس ما يُحقن في الإنتاج عند غياب الصلاحية
        await using (var ctx = _pg.NewContext())
        {
            var service = new OpeningBalanceService(ctx, new JournalEntryService(ctx),
                new AccountingPgFixture.DenyAllOpeningBalance());

            await Assert.ThrowsAnyAsync<Exception>(() => service.CreateDraftAsync(new CreateOpeningBalanceBatchDto
            {
                FiscalYear = 2091,
                EntryDate = new DateTime(2091, 1, 1),
                Reference = "UI-DENY-1",
                Lines = new List<OpeningBalanceLineInput>
                {
                    new() { AccountCode = "1100", DebitAmount = 100m },
                    new() { AccountCode = "2100", CreditAmount = 100m }
                }
            }, "denied-user"));
        }

        await using (var ctx = _pg.NewContext())
        {
            Assert.Equal(batchesBefore, await ctx.Set<OpeningBalanceBatch>().CountAsync());
            Assert.Equal(entriesBefore, await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted));
        }
    }

    // ═══════════ (ج) عقود الواجهة المصدرية (شاشات المحاسبة الجديدة) ═══════════

    /// <summary>
    /// تحقق **مصدري/عقدي** من أن الشاشات فعلية ومربوطة: المسار، حارس الصلاحية، الخدمة المستخدمة،
    /// والعناصر التي تعتمد عليها الفحوص المحاسبية (بنود غير مُنسَبة · حقول تفصيل الرقابي).
    /// هذا ليس اختبار متصفح — راجع تعليل عدم توفّر مُشغّل المتصفح في وصف الفئة.
    /// </summary>
    [Fact]
    public void Accounting_screens_expose_routes_guards_services_and_required_fields()
    {
        var openingBalances = ReadSource("src/Web/Components/Pages/Accounting/OpeningBalances.razor");
        Assert.Contains("@page \"/accounting/opening-balances\"", openingBalances);
        Assert.Contains("[Authorize", openingBalances);
        Assert.Contains("IOpeningBalanceService", openingBalances);

        var openingForm = ReadSource("src/Web/Components/Pages/Accounting/OpeningBalanceFormDialog.razor");
        Assert.Contains("IOpeningBalanceService", openingForm);
        foreach (var field in new[] { "CustomerId", "SupplierId", "ItemId", "Quantity", "UnitCost" })
            Assert.Contains(field, openingForm);

        var openingDetails = ReadSource("src/Web/Components/Pages/Accounting/OpeningBalanceDetailsDialog.razor");
        Assert.Contains("IOpeningBalanceService", openingDetails);

        var periods = ReadSource("src/Web/Components/Pages/Accounting/AccountingPeriods.razor");
        Assert.Contains("@page \"/accounting/periods\"", periods);
        Assert.Contains("[Authorize", periods);
        Assert.Contains("IAccountingPeriodService", periods);
        Assert.Contains("CloseAsync", periods);
        Assert.Contains("ReopenAsync", periods);

        var reconciliation = ReadSource("src/Web/Components/Pages/Accounting/AccountingReconciliation.razor");
        Assert.Contains("@page \"/accounting/reconciliation\"", reconciliation);
        Assert.Contains("[Authorize", reconciliation);
        Assert.Contains("ISubLedgerReconciliationService", reconciliation);
        Assert.Contains("UnattributedControlLines", reconciliation);
        Assert.Contains("TotalUnattributedAmount", reconciliation);

        // روابط التنقّل إلى الشاشتين الجديدتين
        var nav = ReadSource("src/Web/Components/Layout/TopNav.razor");
        Assert.Contains("/accounting/periods", nav);
        Assert.Contains("/accounting/reconciliation", nav);

        // ربط المضيفين: Web (Blazor) و Api — كلٌّ بتصريح فشل مغلق
        var webProgram = ReadSource("src/Web/Program.cs");
        Assert.Contains("BlazorAccountingPeriodAuthorization", webProgram);
        Assert.Contains("BlazorOpeningBalanceAuthorization", webProgram);

        var apiProgram = ReadSource("src/Api/Program.cs");
        Assert.Contains("ApiAccountingPeriodAuthorization", apiProgram);
        Assert.Contains("ApiOpeningBalanceAuthorization", apiProgram);

        Assert.Contains("return false;",
            ReadSource("src/Web/Permissions/AccountingPeriodAuthorization.cs"));
        Assert.Contains("return false;",
            ReadSource("src/Api/Security/ApiAccountingPeriodAuthorization.cs"));

        // التسجيل الفعلي للخدمات: الفترات تحقن التصريح، والمطابقة مسجَّلة قراءةً فقط
        var di = ReadSource("src/Infrastructure/DependencyInjection.cs");
        Assert.Contains("IAccountingPeriodAuthorization", di);
        Assert.Contains("ISubLedgerReconciliationService", di);
        Assert.Contains("IOpeningBalanceAuthorization", di);
    }

    [Fact]
    public void Localization_keys_for_the_new_accounting_screens_exist_in_both_languages()
    {
        var ar = ReadSource("src/Web/Resources/SharedResources.resx");
        var en = ReadSource("src/Web/Resources/SharedResources.en.resx");

        foreach (var key in new[]
                 {
                     "AccountingPeriods", "AccountingPeriodsSubtitle", "ClosePeriod", "ReopenPeriod",
                     "PeriodStatus", "PeriodOpen", "PeriodClosed", "PeriodLockHint",
                     "AccountingReconciliation", "AccountingReconciliationSubtitle", "UnattributedLines",
                     "UnattributedNone", "UnattributedTotal",
                     "OpeningBalanceDetailKind", "OpeningBalanceCustomerDetail", "OpeningBalanceSupplierDetail",
                     "OpeningBalanceItemDetail", "OpeningBalanceWarehouseDetail", "OpeningBalanceQuantityDetail",
                     "OpeningBalanceUnitCostDetail"
                 })
        {
            Assert.Contains($"name=\"{key}\"", ar);
            Assert.Contains($"name=\"{key}\"", en);
        }
    }

    [Fact]
    public async Task Browser_runner_availability_is_measured_and_recorded()
    {
        // قياس صريح (لا ادعاء): هل توجد أدوات اختبار متصفح في المستودع؟
        var csprojs = Directory.GetFiles(RepoRoot(), "*.csproj", SearchOption.AllDirectories);
        var hits = csprojs
            .SelectMany(f => File.ReadAllLines(f)
                .Where(l => l.Contains("Playwright", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("Selenium", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("bunit", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("Puppeteer", StringComparison.OrdinalIgnoreCase))
                .Select(l => $"{Path.GetFileName(f)}: {l.Trim()}"))
            .ToList();

        var razorTestFiles = Directory.GetFiles(RepoRoot(), "*.razor", SearchOption.AllDirectories)
            .Count(f => f.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

        var line = hits.Count == 0
            ? $"لا يوجد مُشغّل متصفح: فُحص {csprojs.Length} ملف مشروع ولم يُعثر على Playwright/Selenium/bUnit/Puppeteer؛ " +
              $"ملفات .razor داخل الاختبارات = {razorTestFiles} ⇒ اختبار المتصفح غير ممكن في هذه البيئة."
            : "أدوات متصفح موجودة: " + string.Join(" | ", hits);

        try
        {
            await File.WriteAllTextAsync(BrowserEvidenceFile,
                $"utc={DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{line}{Environment.NewLine}");
        }
        catch { /* ملف الدليل اختياري */ }

        Assert.False(string.IsNullOrWhiteSpace(line));
    }
}
