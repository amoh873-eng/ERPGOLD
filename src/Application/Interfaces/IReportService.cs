using ERPSystem.Application.DTOs.Reports;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// Contract for financial reports (التقارير المالية):
/// trial balance, income statement, and balance sheet.
/// </summary>
public interface IReportService
{
    /// <summary>ميزان المراجعة: أرصدة كل الحسابات مع إجمالي المدين والدائن.</summary>
    Task<TrialBalanceDto> GetTrialBalanceAsync();

    /// <summary>قائمة الدخل لفترة محددة (from/to).</summary>
    Task<IncomeStatementDto> GetIncomeStatementAsync(DateTime from, DateTime to);

    /// <summary>الميزانية العمومية كما في تاريخ محدد (asOf).</summary>
    Task<BalanceSheetDto> GetBalanceSheetAsync(DateTime asOf);

    /// <summary>دفتر أستاذ حساب (الرصيد الافتتاحي والحركات والرصيد الختامي) لفترة محددة.</summary>
    Task<AccountLedgerDto> GetAccountLedgerAsync(Guid accountId, DateTime from, DateTime to);

    Task<BillingStageReportDto> GetBillingStageReportAsync(DateTime from, DateTime to);
    byte[] ExportBillingStageExcel(BillingStageReportDto dto);
    Task<GenericReportTable> GetReportAsync(string key, Dictionary<string,string?> pars);
    byte[] ExportReportExcel(GenericReportTable t);

    /// <summary>
    /// ميزان المراجعة الهرمي حسب مستويات دليل الحسابات كجدول تقرير موحّد.
    /// مصدر عرض واحد لمركز التقارير ولصفحة ميزان المراجعة القديمة ولمسارات التصدير (Excel/PDF).
    /// </summary>
    Task<GenericReportTable> GetTrialBalanceReportAsync(DateTime? from = null, DateTime? to = null,
        Guid? rootAccountId = null, bool includeZero = false, bool levelsOnly = false);

    /// <summary>تقرير ضريبة المبيعات المجمَّع حسب شرائح الضريبة لفترة محددة (مع إجمالي مدين/دائن).</summary>
    Task<SalesTaxReportDto> GetSalesTaxReportAsync(DateTime from, DateTime to);

    /// <summary>
    /// تقرير ضريبة المشتريات المجمَّع حسب نسبة الضريبة لكل سطر (المصدر: PurchaseInvoiceLine).
    /// تقرير تشغيلي يعرض البيانات المخزّنة؛ لا يعتمد على رأس الفاتورة ولا يعيد احتساب الفواتير القديمة.
    /// </summary>
    Task<PurchaseTaxReportDto> GetPurchaseTaxReportAsync(DateTime from, DateTime to);

    /// <summary>
    /// سجل ضريبة المشتريات التفصيلي: صف واحد لكل فاتورة شراء مع تفاصيل أسطرها.
    /// المصدر: PurchaseInvoiceLine.TaxRate / TaxAmount (لقطة تاريخية)؛ لا يعتمد على رأس الفاتورة.
    /// </summary>
    Task<PurchaseTaxRegisterDto> GetPurchaseTaxRegisterAsync(DateTime from, DateTime to);

    /// <summary>
    /// سجل ضريبة المبيعات التفصيلي: صف واحد لكل فاتورة مبيعات مع تفاصيل أسطرها.
    /// المصدر: SalesInvoiceLine.TaxRate / TaxAmount (لقطة تاريخية)؛ لا يعتمد على رأس الفاتورة.
    /// </summary>
    Task<SalesTaxRegisterDto> GetSalesTaxRegisterAsync(DateTime from, DateTime to);

    /// <summary>
    /// لوحة التسوية الضريبية الموحّدة: تجمع ضريبة المبيعات (المخرجات) وضريبة المشتريات (المدخلات)
    /// لحساب الصافي المستحق أو المسترد خلال الفترة. المصدر: سطور الفاتورة (TaxAmount).
    /// </summary>
    Task<TaxReconciliationDto> GetTaxReconciliationAsync(DateTime from, DateTime to);

    /// <summary>تقرير أعمار الذمم (AR/AP) حسب العميل/المورد كما في تاريخ asOf.</summary>
    Task<AgingReportDto> GetAgingReportAsync(bool isReceivable, DateTime asOf);

    /// <summary>أعلى N عملاء بنسبة من المبيعات + تحذير تركيز &gt;70%.</summary>
    Task<(List<ERPSystem.Application.Services.AgingReportQueries.TopCustomerRow> Top, bool Warning)>
        GetTopCustomersAsync(DateTime asOf, int topN = 5);

    /// <summary>المؤشرات المالية للفترة [from,to] مع مقارنة الفترة السابقة.</summary>
    Task<FinancialRatiosDto> GetFinancialRatiosAsync(DateTime from, DateTime to);

    /// <summary>قائمة التدفق النقدي (بصيغة غير مباشرة) للفترة [from,to].</summary>
    Task<CashFlowStatementDto> GetCashFlowStatementAsync(DateTime from, DateTime to);

    /// <summary>ربحية الأصناف/الفئات (هامش مساهمة) لفترة محددة.</summary>
    Task<ItemProfitabilityDto> GetItemProfitabilityAsync(DateTime from, DateTime to);

    /// <summary>الأصناف البطيئة/الميتة (لا مبيع خلال آخر N يوم).</summary>
    Task<SlowMovingDto> GetSlowMovingAsync(int daysThreshold);

    /// <summary>تصنيف ABC على أساس قيمة المخزون.</summary>
    Task<AbcAnalysisDto> GetAbcAnalysisAsync();

    /// <summary>الموسمية: مبيعات كل سنة × 12 شهراً (فواتير غير ملغاة).</summary>
    Task<SeasonalityDto> GetSeasonalityAsync();
}