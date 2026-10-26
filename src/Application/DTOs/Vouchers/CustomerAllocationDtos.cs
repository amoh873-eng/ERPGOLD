namespace ERPSystem.Application.DTOs.Vouchers;

/// <summary>P0-2 — مدخل تخصيص واحد (فاتورة + مبلغ) لسند قبض عميل.</summary>
public sealed class CustomerAllocationInput
{
    public Guid SalesInvoiceId { get; set; }

    /// <summary>المبلغ المخصَّص (أكبر من صفر، منزلتان عشريتان).</summary>
    public decimal Amount { get; set; }
}

/// <summary>P0-2 — طلب ترحيل سند قبض مع نية توزيع اختيارية (تُمرَّر مع طلب الترحيل فقط).</summary>
public sealed class PostReceiptVoucherRequest
{
    /// <summary>تخصيصات صريحة يؤكدها المستخدم. فارغة ⇒ يُرحَّل السند بلا تخصيص (يبقى «بلا تخصيص موثق»).</summary>
    public List<CustomerAllocationInput> Allocations { get; set; } = new();

    /// <summary>مفتاح منع التكرار لطلب الترحيل — يُعاد كما هو عند إعادة المحاولة.</summary>
    public Guid? IdempotencyKey { get; set; }
}

/// <summary>P0-2 — طلب ضبط تخصيصات سند قبض مُرحَّل (استبدال المجموعة الحالية: عكس ثم إضافة، بلا حذف).</summary>
public sealed class SetReceiptAllocationsRequest
{
    public List<CustomerAllocationInput> Allocations { get; set; } = new();

    public Guid? IdempotencyKey { get; set; }

    /// <summary>ملاحظة تُسجَّل في صفوف التخصيص وسجل التدقيق (سبب التعديل).</summary>
    public string? Note { get; set; }
}

/// <summary>P0-2 — طلب عكس صف تخصيص (كلي أو جزئي) — لا يحذف التاريخ.</summary>
public sealed class ReverseCustomerAllocationRequest
{
    public Guid AllocationId { get; set; }

    /// <summary>المبلغ المعكوس؛ NULL أو 0 ⇒ عكس ما تبقى من الصف بالكامل.</summary>
    public decimal? Amount { get; set; }

    public string? Note { get; set; }

    public Guid? IdempotencyKey { get; set; }
}

/// <summary>P0-2 — صف تخصيص للعرض (مع الحالة المشتقّة — لا عمود حالة مخزَّن).</summary>
public sealed class CustomerAllocationDto
{
    public Guid Id { get; set; }
    public Guid ReceiptVoucherId { get; set; }
    public Guid SalesInvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public short Direction { get; set; }
    public Guid? ReversalOfAllocationId { get; set; }
    public DateTime AllocatedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// P0-2 — فاتورة عميل قابلة للتخصيص: كل مكوّن في بند واضح
/// (الأصلي · المدفوع المسجَّل · المردودات المؤثرة على الذمم · المخصَّص · المتبقي · المتاح).
/// </summary>
public sealed class CustomerOpenInvoiceDto
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public DateTime DueDate { get; set; }

    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }

    /// <summary>مردودات مبيعات **مرحَّلة مرتبطة** بالمبلغ **والمؤثرة على حساب الذمم** فقط (مرة واحدة).</summary>
    public decimal EffectiveReturns { get; set; }

    /// <summary>صافي المخصَّص: Σ(+1) − Σ(−1) حتى تاريخ القطع.</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>المتبقي المحسوب = الإجمالي − المدفوع − المردودات المؤثرة − المخصَّص.</summary>
    public decimal Residual { get; set; }

    /// <summary>المتاح للتخصيص = max(0, Residual).</summary>
    public decimal Available { get; set; }

    public bool IsEligible { get; set; }
    public string? IneligibilityReason { get; set; }
}

/// <summary>P0-2 — حالة تخصيص سند قبض: المبلغ · المخصَّص · غير المخصص · الصفوف · الفواتير المؤهَّلة.</summary>
public sealed class ReceiptAllocationStateDto
{
    public Guid ReceiptVoucherId { get; set; }
    public string VoucherNumber { get; set; } = string.Empty;
    public int Status { get; set; }
    public Guid CustomerId { get; set; }
    public DateTime VoucherDate { get; set; }

    public decimal Amount { get; set; }
    public decimal AllocatedAmount { get; set; }

    /// <summary>المتبقي غير المخصص من السند = Amount − AllocatedAmount.</summary>
    public decimal UnallocatedAmount { get; set; }

    public List<CustomerAllocationDto> Allocations { get; set; } = new();
    public List<CustomerOpenInvoiceDto> OpenInvoices { get; set; } = new();
}

/// <summary>P0-2 — اقتراح FIFO لسند قبض (اقتراح صريح فقط · لا كتابة ولا تنفيذ تلقائي).</summary>
public sealed class CustomerFifoSuggestionDto
{
    public Guid ReceiptVoucherId { get; set; }
    public decimal Amount { get; set; }
    public decimal SuggestedTotal { get; set; }
    public decimal RemainingUnallocated { get; set; }
    public List<CustomerAllocationInput> Lines { get; set; } = new();
}
