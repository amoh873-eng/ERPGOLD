namespace ERPSystem.Application.DTOs.Reports;

/// <summary>
/// سجل ضريبة المشتريات التفصيلي — صف واحد لكل فاتورة شراء، مع إمكانية فتح تفاصيل أسطر الفاتورة.
///
/// المصدر: PurchaseInvoiceLine.TaxRate / TaxAmount (وليس رأس الفاتورة)، لذا:
/// - يعمل مع الفواتير متعددة النسب (تُعرض نسب الفاتورة مجمّعة في عمود «النسب»).
/// - لا يعيد احتساب الفواتير القديمة ولا يغيّر بياناتها التاريخية.
///
/// ملاحظة تصميم (بسيطة وقابلة للتوسّع): لا يوجد حقل خصم على فواتير المشتريات حالياً،
/// لذلك تُعرض أعمدة الخصم بقيمة 0 والصافي = إجمالي الأصناف. عند إضافة خصم مستقبلاً
/// يُملأ الحقلان دون تغيير البنية. لا يُضاف TaxClass/قابل للخصم في هذه المرحلة.
/// </summary>
public sealed class PurchaseTaxRegisterDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }

    /// <summary>صف لكل فاتورة شراء مرتبة بالتاريخ ثم الرقم.</summary>
    public List<PurchaseTaxRegisterRow> Rows { get; set; } = new();

    public decimal TotalItemTotal => Rows.Sum(r => r.ItemTotal);
    public decimal TotalDiscount => Rows.Sum(r => r.DiscountTotal);
    public decimal TotalNetTaxable => Rows.Sum(r => r.NetTaxable);
    public decimal TotalTax => Rows.Sum(r => r.TaxTotal);
    public decimal TotalInclTax => Rows.Sum(r => r.TotalInclTax);
}

/// <summary>صف فاتورة شراء في السجل التفصيلي.</summary>
public sealed class PurchaseTaxRegisterRow
{
    public Guid InvoiceId { get; set; }
    public DateTime InvoiceDate { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>إجمالي الأصناف = مجموع إجمالي السطور.</summary>
    public decimal ItemTotal { get; set; }

    /// <summary>إجمالي الخصم (0 في المشتريات حالياً).</summary>
    public decimal DiscountTotal { get; set; }

    /// <summary>صافي القيمة الخاضعة للضريبة = إجمالي الأصناف − الخصم.</summary>
    public decimal NetTaxable { get; set; }

    /// <summary>إجمالي الضريبة = مجموع ضريبة السطور (المصدر: PurchaseInvoiceLine.TaxAmount).</summary>
    public decimal TaxTotal { get; set; }

    /// <summary>الإجمالي شامل الضريبة = الصافي + الضريبة.</summary>
    public decimal TotalInclTax { get; set; }

    /// <summary>نسب الضريبة الموجودة في الفاتورة (مثال: "8%, 16%") — مبنية من السطور.</summary>
    public string TaxRates { get; set; } = string.Empty;

    /// <summary>تفاصيل أسطر الفاتورة (تُفتح عند النقر على الصف).</summary>
    public List<PurchaseTaxRegisterLineDto> Lines { get; set; } = new();
}

/// <summary>سطر فاتورة شراء في السجل التفصيلي.</summary>
public sealed class PurchaseTaxRegisterLineDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemNameAr { get; set; } = string.Empty;
    public decimal Quantity { get; set; }

    /// <summary>سعر الوحدة (تكلفة الوحدة عند الشراء).</summary>
    public decimal UnitCost { get; set; }

    /// <summary>الخصم على السطر (0 في المشتريات حالياً).</summary>
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
