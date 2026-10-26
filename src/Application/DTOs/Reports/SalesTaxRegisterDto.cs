namespace ERPSystem.Application.DTOs.Reports;

/// <summary>
/// سجل ضريبة المبيعات التفصيلي — صف واحد لكل فاتورة مبيعات، مع إمكانية فتح تفاصيل أسطرها.
///
/// المصدر: SalesInvoiceLine.TaxRate / TaxAmount (وليس رأس الفاتورة)، لذا:
/// - يعمل مع الفواتير متعددة النسب (تُعرض نسب الفاتورة مجمّعة في عمود «النسب»).
/// - لا يعيد احتساب الفواتير القديمة ولا يغيّر بياناتها التاريخية.
///
/// فواتير المبيعات تملك خصم على مستوى الرأس؛ يُوزَّع تناسبياً على السطور (نفس منطق الإنشاء)
/// فيظهر «صافي» كل سطر = إجمالي السطر − حصته من الخصم. لا يُضاف TaxClass/Input VAT هنا.
/// </summary>
public sealed class SalesTaxRegisterDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }

    /// <summary>صف لكل فاتورة مبيعات مرتبة بالتاريخ ثم الرقم.</summary>
    public List<SalesTaxRegisterRow> Rows { get; set; } = new();

    public decimal TotalItemTotal => Rows.Sum(r => r.ItemTotal);
    public decimal TotalDiscount => Rows.Sum(r => r.DiscountTotal);
    public decimal TotalNetTaxable => Rows.Sum(r => r.NetTaxable);
    public decimal TotalTax => Rows.Sum(r => r.TaxTotal);
    public decimal TotalInclTax => Rows.Sum(r => r.TotalInclTax);
}

/// <summary>صف فاتورة مبيعات في السجل التفصيلي.</summary>
public sealed class SalesTaxRegisterRow
{
    public Guid InvoiceId { get; set; }
    public DateTime InvoiceDate { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>إجمالي الأصناف = مجموع إجمالي السطور.</summary>
    public decimal ItemTotal { get; set; }

    /// <summary>إجمالي الخصم = مجموع حصص الخصم على السطور.</summary>
    public decimal DiscountTotal { get; set; }

    /// <summary>صافي القيمة الخاضعة للضريبة = إجمالي الأصناف − الخصم.</summary>
    public decimal NetTaxable { get; set; }

    /// <summary>إجمالي الضريبة = مجموع ضريبة السطور (المصدر: SalesInvoiceLine.TaxAmount).</summary>
    public decimal TaxTotal { get; set; }

    /// <summary>الإجمالي شامل الضريبة = الصافي + الضريبة.</summary>
    public decimal TotalInclTax { get; set; }

    /// <summary>نسب الضريبة الموجودة في الفاتورة (مثال: "8%, 16%") — مبنية من السطور.</summary>
    public string TaxRates { get; set; } = string.Empty;

    /// <summary>تفاصيل أسطر الفاتورة (تُفتح عند النقر على الصف).</summary>
    public List<SalesTaxRegisterLineDto> Lines { get; set; } = new();
}

/// <summary>سطر فاتورة مبيعات في السجل التفصيلي.</summary>
public sealed class SalesTaxRegisterLineDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemNameAr { get; set; } = string.Empty;
    public decimal Quantity { get; set; }

    /// <summary>سعر الوحدة (سعر البيع).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>الخصم على السطر (حصته من خصم الفاتورة).</summary>
    public decimal Discount { get; set; }

    /// <summary>صافي السطر = إجمالي السطر − الخصم.</summary>
    public decimal Net { get; set; }

    /// <summary>نسبة ضريبة السطر (لقطة).</summary>
    public decimal TaxRate { get; set; }

    /// <summary>مبلغ ضريبة السطر.</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>الإجمالي شامل الضريبة للسطر = الصافي + الضريبة.</summary>
    public decimal Total { get; set; }
}
