namespace ERPSystem.Application.DTOs.Reports;
public enum ReportCategory { Financial, Sales, Purchases, Inventory, Hr, Expenses, Pos, Crm, Executive }
public enum ReportParamKind { None, DateRange, Date, Account, IncludeZero, LevelsOnly, DocNumber, EntryNumber, Status, Party }
public record ReportDef(string Key, ReportCategory Cat, string TitleAr, string TitleEn, ReportParamKind[] Params, bool Landscape = true);
public static class ReportsCatalog
{
    public static IReadOnlyList<ReportDef> All = new List<ReportDef>
    {
        new("trial-balance", ReportCategory.Financial, "ميزان المراجعة", "Trial Balance", new[] { ReportParamKind.Account, ReportParamKind.DateRange, ReportParamKind.IncludeZero, ReportParamKind.LevelsOnly }),
        new("accounts-tree-balances", ReportCategory.Financial, "شجرة الحسابات والأرصدة حسب المستويات", "Chart of Accounts Balances by Level", new[] { ReportParamKind.Account, ReportParamKind.DateRange }),
        new("income-statement", ReportCategory.Financial, "قائمة الدخل", "Income Statement", new[] { ReportParamKind.DateRange }),
        new("balance-sheet", ReportCategory.Financial, "الميزانية", "Balance Sheet", new[] { ReportParamKind.DateRange }),
        new("general-ledger", ReportCategory.Financial, "دفتر الأستاذ", "General Ledger", new[] { ReportParamKind.Account, ReportParamKind.DateRange }),
        new("journal-register", ReportCategory.Financial, "سجل القيود", "Journal Register", new[] { ReportParamKind.DateRange }),
        new("vat-summary", ReportCategory.Financial, "ملخص الضريبة", "VAT Summary", new[] { ReportParamKind.DateRange }),
        new("sales-tax", ReportCategory.Financial, "تقرير ضريبة المبيعات", "Sales Tax Report", new[] { ReportParamKind.DateRange }),
        new("sales-tax-register", ReportCategory.Sales, "سجل ضريبة المبيعات", "Sales Tax Register", new[] { ReportParamKind.DateRange }),
        new("purchase-tax", ReportCategory.Purchases, "تقرير ضريبة المشتريات", "Purchase Tax Report", new[] { ReportParamKind.DateRange }),
        new("tax-reconciliation", ReportCategory.Financial, "تسوية الضريبة الموحّدة", "Tax Reconciliation", new[] { ReportParamKind.DateRange }),
        new("purchase-tax-register", ReportCategory.Purchases, "سجل ضريبة المشتريات", "Purchase Tax Register", new[] { ReportParamKind.DateRange }),
        new("financial-ratios", ReportCategory.Financial, "المؤشرات المالية", "Financial Ratios", new[] { ReportParamKind.DateRange }),
        new("cash-flow", ReportCategory.Financial, "قائمة التدفق النقدي", "Cash Flow Statement", new[] { ReportParamKind.DateRange }),
        new("item-profitability", ReportCategory.Inventory, "ربحية الأصناف", "Item Profitability", new[] { ReportParamKind.DateRange }),
        new("slow-moving-stock", ReportCategory.Inventory, "بطيء الحركة / خامل", "Slow Moving", new[] { ReportParamKind.DateRange }),
        new("abc-analysis", ReportCategory.Inventory, "تصنيف ABC", "ABC Analysis", new[] { ReportParamKind.DateRange }),
        new("seasonality", ReportCategory.Financial, "الموسمية", "Seasonality", new[] { ReportParamKind.DateRange }),
        new("sales-by-customer", ReportCategory.Sales, "المبيعات حسب العميل", "Sales by Customer", new[] { ReportParamKind.DateRange }),
        new("sales-by-item", ReportCategory.Sales, "المبيعات حسب الصنف", "Sales by Item", new[] { ReportParamKind.DateRange }),
        new("stock-valuation", ReportCategory.Inventory, "تقييم المخزون", "Stock Valuation", new[] { ReportParamKind.DateRange }),
        new("ar-aging", ReportCategory.Financial, "أعمار الذمم - عملاء", "AR Aging", new[] { ReportParamKind.DateRange }),
        new("ap-aging", ReportCategory.Financial, "أعمار الذمم - موردون", "AP Aging", new[] { ReportParamKind.DateRange }),
        new("low-stock", ReportCategory.Inventory, "نقص المخزون", "Low Stock", new[] { ReportParamKind.DateRange }),
        new("employee-directory", ReportCategory.Hr, "دليل الموظفين", "Employees", new[] { ReportParamKind.DateRange }),
        new("expenses-by-category", ReportCategory.Expenses, "المصاريف حسب الفئة", "Expenses by Category", new[] { ReportParamKind.DateRange }),
        new("pos-daily", ReportCategory.Pos, "ملخص المبيعات", "POS Summary", new[] { ReportParamKind.DateRange }),
        new("leads-pipeline", ReportCategory.Crm, "مسار العملاء", "Leads Pipeline", new[] { ReportParamKind.DateRange }),
        new("kpi-summary", ReportCategory.Executive, "ملخص المؤشرات", "KPI Summary", new[] { ReportParamKind.DateRange }),

        // ── المرحلة D: تقارير سندات القبض/الصرف + قيود المستندات الأربعة ──
        // كل تقرير يشترط نوعه: لا يظهر نوع في تقرير نوع آخر، والإجماليات على مستوى البند
        // (لتفادي تضخيم مبلغ الرأس عند تعدد البنود)، مع فلاتر: المدة/الطرف/الحالة/رقم المستند/رقم القيد.
        new("receipt-vouchers", ReportCategory.Expenses, "سندات القبض", "Receipt Vouchers",
            new[] { ReportParamKind.DateRange, ReportParamKind.Party, ReportParamKind.Status, ReportParamKind.DocNumber, ReportParamKind.EntryNumber }),
        new("receipt-voucher-entries", ReportCategory.Expenses, "قيود سندات القبض", "Receipt Voucher Entries",
            new[] { ReportParamKind.DateRange, ReportParamKind.Party, ReportParamKind.Status, ReportParamKind.DocNumber, ReportParamKind.EntryNumber }),
        new("payment-vouchers", ReportCategory.Expenses, "سندات الصرف", "Payment Vouchers",
            new[] { ReportParamKind.DateRange, ReportParamKind.Party, ReportParamKind.Status, ReportParamKind.DocNumber, ReportParamKind.EntryNumber }),
        new("payment-voucher-entries", ReportCategory.Expenses, "قيود سندات الصرف", "Payment Voucher Entries",
            new[] { ReportParamKind.DateRange, ReportParamKind.Party, ReportParamKind.Status, ReportParamKind.DocNumber, ReportParamKind.EntryNumber }),
        new("sales-invoice-entries", ReportCategory.Sales, "قيود فواتير المبيعات", "Sales Invoice Entries",
            new[] { ReportParamKind.DateRange, ReportParamKind.Party, ReportParamKind.Status, ReportParamKind.DocNumber, ReportParamKind.EntryNumber }),
        new("sales-return-entries", ReportCategory.Sales, "قيود مردودات المبيعات", "Sales Return Entries",
            new[] { ReportParamKind.DateRange, ReportParamKind.Party, ReportParamKind.Status, ReportParamKind.DocNumber, ReportParamKind.EntryNumber }),
        new("purchase-invoice-entries", ReportCategory.Purchases, "قيود فواتير المشتريات", "Purchase Invoice Entries",
            new[] { ReportParamKind.DateRange, ReportParamKind.Party, ReportParamKind.Status, ReportParamKind.DocNumber, ReportParamKind.EntryNumber }),
        new("purchase-return-entries", ReportCategory.Purchases, "قيود مردودات المشتريات", "Purchase Return Entries",
            new[] { ReportParamKind.DateRange, ReportParamKind.Party, ReportParamKind.Status, ReportParamKind.DocNumber, ReportParamKind.EntryNumber }),
    };

    /// <summary>
    /// المرحلة D — تقارير سندات القبض/الصرف (مستوى المستند): مفاتيح مصدرها هذا الفهرس نفسه،
    /// وتُستخدم في اللوحة الرئيسية `/reports` لبناء قسمها من السجل ⇒ لا قائمة ثابتة في الواجهة
    /// ولا فرق ممكن بين ما هو مسجَّل وما هو معروض.
    /// </summary>
    public static readonly string[] VoucherReportKeys = { "receipt-vouchers", "payment-vouchers" };

    /// <summary>المرحلة D — تقارير قيود المستندات (مستوى البند): سندات القبض/الصرف + الفواتير والمردودات.</summary>
    public static readonly string[] DocumentEntryReportKeys =
    {
        "receipt-voucher-entries", "payment-voucher-entries",
        "sales-invoice-entries", "sales-return-entries",
        "purchase-invoice-entries", "purchase-return-entries"
    };

    /// <summary>كل مفاتيح المرحلة D الثمانية (بالترتيب المعتمد للعرض).</summary>
    public static IEnumerable<string> VoucherAndDocumentEntryKeys()
        => VoucherReportKeys.Concat(DocumentEntryReportKeys);

    /// <summary>تعريفات التقارير بترتيب المفاتيح المعطى (تتخطى أي مفتاح غير معرَّف بدل الفشل).</summary>
    public static IReadOnlyList<ReportDef> ByKeys(IEnumerable<string> keys)
    {
        var defs = new List<ReportDef>();
        foreach (var key in keys)
        {
            var def = All.FirstOrDefault(d => d.Key == key);
            if (def is not null) defs.Add(def);
        }
        return defs;
    }
}
