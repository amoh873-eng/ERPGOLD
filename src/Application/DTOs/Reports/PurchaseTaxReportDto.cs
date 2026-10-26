namespace ERPSystem.Application.DTOs.Reports;

/// <summary>
/// تقرير ضريبة المشتريات (Purchase Tax Report) — يجمع بنود فواتير المشتريات في الفترة
/// حسب نسبة الضريبة لكل سطر (المصدر: PurchaseInvoiceLine.TaxRate / TaxAmount).
/// يُعرض إجمالي المشتريات الخاضعة للضريبة وإجمالي الضريبة لكل شريحة، مع إمكانية العرض حسب النسبة.
///
/// ⚠️ ملاحظة تصميم (قابل للتوسّع لاحقاً):
/// - هذا تقرير «تشغيلي» يعرض البيانات الفعلية المخزّنة على مستوى السطر، ولا يعتبر أي نسبة
///   في بطاقة المادة نسبةً قانونية. منطق الامتثال القانوني (نسب/مبالغ قابلة للخصم، ضريبة
///   مدخلات قابلة/غير قابلة للخصم، مردودات المشتريات) يُبنى لاحقاً كطبقة منفصلة فوق هذه
///   البيانات دون تغيير مصدرها.
/// - لإضافة «ضريبة مدخلات قابلة للخصم / غير قابلة للخصم» أو «مردودات مشتريات»، يُضاف
///   حقل تصنيف (مثل TaxClass / SourceKind) إلى الصف ويكون افتراضياً (Invoice) بلا تغيير
///   في سلوك التقرير الحالي.
/// </summary>
public sealed class PurchaseTaxReportDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }

    /// <summary>صفوف الشرائح الضريبية مرتبة تنازلياً حسب النسبة.</summary>
    public List<PurchaseTaxReportRow> Rows { get; set; } = new();

    /// <summary>إجمالي عدد فواتير المشتريات في الفترة.</summary>
    public int TotalInvoices => Rows.Sum(r => r.InvoiceCount);

    /// <summary>إجمالي قيمة المشتريات الخاضعة للضريبة (بدون الضريبة).</summary>
    public decimal TotalGross => Rows.Sum(r => r.GrossAmount);

    /// <summary>إجمالي مبلغ ضريبة المشتريات.</summary>
    public decimal TotalTax => Rows.Sum(r => r.TaxAmount);

    /// <summary>إجمالي المشتريات شامل الضريبة (Gross + Tax).</summary>
    public decimal TotalPurchasesInclTax => TotalGross + TotalTax;

    /// <summary>إجمالي المدين من قيود المشتريات (المخزون + الضريبة المرسملة).</summary>
    public decimal TotalDebit { get; set; }

    /// <summary>إجمالي الدائن من قيود المشتريات (الصندوق/الموردون).</summary>
    public decimal TotalCredit { get; set; }

    /// <summary>التحقق من توازن القيود (Debit == Credit) للتأكيد المحاسبي.</summary>
    public bool IsBalanced => Math.Abs(TotalDebit - TotalCredit) < 0.01m;
}

/// <summary>صف شريحة ضريبية واحدة في تقرير ضريبة المشتريات.</summary>
public sealed class PurchaseTaxReportRow
{
    /// <summary>نسبة الضريبة (0، 5، 10، 16، 32 ...).</summary>
    public decimal TaxRate { get; set; }

    /// <summary>وصف الشريحة الضريبية (مثال: "ضريبة مبيعات 16% مستحقة").</summary>
    public string TaxDescription { get; set; } = string.Empty;

    /// <summary>عدد فواتير المشتريات ضمن هذه الشريحة (فاتورة بنسب متعددة تُحسب في كل شريحة).</summary>
    public int InvoiceCount { get; set; }

    /// <summary>إجمالي المشتريات الخاضعة للضريبة (أساس الضريبة = إجمالي البنود).</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>إجمالي مبلغ ضريبة المشتريات لهذه الشريحة.</summary>
    public decimal TaxAmount { get; set; }
}
