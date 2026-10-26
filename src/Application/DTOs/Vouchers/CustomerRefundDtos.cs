namespace ERPSystem.Application.DTOs.Vouchers;

/// <summary>
/// SUB-2b/P3 — مردود مؤهل للرد النقدي/البنكي مع المتبقي القابل للرد.
/// يُستدعى للعرض فقط؛ الحارس الحاسم (السقف/الرصيد) يُعاد التحقق منه داخل معاملة الترحيل.
/// </summary>
public sealed class CustomerRefundableReturnDto
{
    public Guid ReturnId { get; set; }
    public string ReturnNumber { get; set; } = string.Empty;
    public DateTime ReturnDate { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>إجمالي المردود (SalesReturn.TotalAmount).</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>Σ سندات الرد المرحّلة المرتبطة بهذا المردود.</summary>
    public decimal RefundedAmount { get; set; }

    /// <summary>المتبقي القابل للرد = TotalAmount − RefundedAmount.</summary>
    public decimal RemainingAmount { get; set; }

    /// <summary>1 = نقدي · 2 = بنكي (PaymentMethod) — من السند المقترح.</summary>
    public int PaymentMethod { get; set; } = 1;

    /// <summary>نوع الفاتورة الأصلية (OnAccount / Cash) — للعرض.</summary>
    public string InvoiceType { get; set; } = string.Empty;

    /// <summary>وسيلة دفع الفاتورة الأصلية (Cash / Card / Check).</summary>
    public string InvoicePaymentMethod { get; set; } = string.Empty;
}

/// <summary>ملخص أهلية الرد لعميل: الرصيد الدائن + المردودات القابلة للرد.</summary>
public sealed class CustomerRefundAvailabilityDto
{
    public Guid CustomerId { get; set; }

    /// <summary>
    /// رصيد العميل التشغيلي (سالب = دائن له). سقف الرد الكلي = −CurrentBalance عند وجود رصيد دائن.
    /// </summary>
    public decimal CurrentBalance { get; set; }

    /// <summary>الرصيد الدائن المتاح (0 عندما لا يوجد رصيد دائن).</summary>
    public decimal CreditBalance { get; set; }

    public List<CustomerRefundableReturnDto> Returns { get; set; } = new();
}
