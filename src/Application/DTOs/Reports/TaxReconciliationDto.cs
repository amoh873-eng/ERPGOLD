namespace ERPSystem.Application.DTOs.Reports;

/// <summary>
/// لوحة التسوية الضريبية الموحّدة — تجمع ضريبة المبيعات (المخرجات) وضريبة المشتريات «المسجّلة»
/// مع تفصيل حسب نسبة الضريبة. لا تفترض تساوي ضريبة المشتريات المسجّلة مع «ضريبة مدخلات قابلة للخصم».
///
/// ⚠️ دلالة مهمة:
/// - «ضريبة المشتريات المسجّلة» (Recorded Purchase Tax) = مجموع TaxAmount لسطور المشتريات — قيمة
///   مقيسة، ليست بالضرورة ضريبة مدخلات قابلة للخصم (قد تشمل نسباً غير قابلة للخصم).
/// - «ضريبة المدخلات القابلة للخصم» (DeductibleInputVat) مفهوم منفصل ومؤجّل حتى نطبّق تصنيفاً
///   محاسبياً/ضريبياً واضحاً. تُترك غير معروفة (null) الآن.
/// - لذلك لا يُعرض الصافي كتسوية ضريبية نهائية أو مبلغ مستحق قانوني؛ بل كـ«صافي ضريبة المبيعات
///   قبل تسوية ضريبة المدخلات القابلة للخصم».
/// - المصدر: سطور الفاتورة (SalesInvoiceLine.TaxAmount/TaxRate، PurchaseInvoiceLine.TaxAmount/TaxRate)
///   — لقطة تاريخية، لا رأس الفاتورة، ولا إعادة احتساب للفواتير القديمة.
/// </summary>
public sealed class TaxReconciliationDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }

    /// <summary>إجمالي المبيعات الخاضعة للضريبة (صافي = SubTotal − خصم).</summary>
    public decimal TotalSalesGross { get; set; }

    /// <summary>ضريبة المبيعات (المخرجات) = مجموع TaxAmount لسطور المبيعات.</summary>
    public decimal SalesTaxOutput { get; set; }

    /// <summary>إجمالي المشتريات الخاضعة للضريبة.</summary>
    public decimal TotalPurchasesGross { get; set; }

    /// <summary>«ضريبة المشتريات المسجّلة» = مجموع TaxAmount لسطور المشتريات (ليست بالضرورة قابلة للخصم).</summary>
    public decimal RecordedPurchaseTax { get; set; }

    /// <summary>ضريبة المدخلات القابلة للخصم — غير معروفة حتى يُضاف التصنيف؛ تُترك null الآن.</summary>
    public decimal? DeductibleInputVat { get; set; }

    /// <summary>هل التسوية «نهائية»؟ لا، حتى تُعرف ضريبة المدخلات القابلة للخصم.</summary>
    public bool IsFinal => DeductibleInputVat.HasValue;

    /// <summary>
    /// صافي ضريبة المبيعات = مخرجات − مدخلات قابلة للخصم.
    /// حيث إن المدخلات القابلة للخصم غير معروفة، يُحسب على أساس صفر مؤقتاً، ويجب عرضه بوصفه
    /// «قبل تسوية ضريبة المدخلات القابلة للخصم».
    /// </summary>
    public decimal NetSalesTaxBeforeVat => Math.Round(SalesTaxOutput - (DeductibleInputVat ?? 0m), 2);

    /// <summary>تفصيل التسوية حسب نسبة الضريبة (Sales Output Tax و Recorded Purchase Tax لكل شريحة) — بلا TaxClass.</summary>
    public List<TaxRateBreakdownRow> Breakdown { get; set; } = new();

    public decimal TotalBreakdownSalesTax => Rows(Breakdown.Sum(r => r.SalesOutputTax));
    public decimal TotalBreakdownPurchaseTax => Rows(Breakdown.Sum(r => r.RecordedPurchaseTax));

    private static decimal Rows(decimal v) => Math.Round(v, 2);
}

/// <summary>صف تفصيل التسوية حسب نسبة الضريبة.</summary>
public sealed class TaxRateBreakdownRow
{
    public decimal TaxRate { get; set; }
    public string TaxDescription { get; set; } = string.Empty;

    /// <summary>ضريبة المبيعات (المخرجات) لهذه الشريحة.</summary>
    public decimal SalesOutputTax { get; set; }

    /// <summary>ضريبة المشتريات المسجّلة لهذه الشريحة.</summary>
    public decimal RecordedPurchaseTax { get; set; }
}
