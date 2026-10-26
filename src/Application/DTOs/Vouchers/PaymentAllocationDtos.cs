using System.ComponentModel.DataAnnotations;

namespace ERPSystem.Application.DTOs.Vouchers;

/// <summary>PU-04 — مدخل تخصيص واحد (فاتورة + مبلغ).</summary>
public sealed class PaymentAllocationInput
{
    public Guid PurchaseInvoiceId { get; set; }

    /// <summary>المبلغ المخصَّص (أكبر من صفر، منزلتان عشريتان).</summary>
    public decimal Amount { get; set; }
}

/// <summary>PU-04 — طلب ترحيل سند مع نية التوزيع (D5-و: تُمرَّر مع طلب الترحيل فقط).</summary>
public sealed class PostPaymentVoucherRequest
{
    /// <summary>تخصيصات صريحة يؤكدها المستخدم. فارغة ⇒ اقتراح FIFO داخلي (مسار توافق).</summary>
    public List<PaymentAllocationInput> Allocations { get; set; } = new();

    /// <summary>مفتاح منع التكرار (N-1) لطلب الترحيل — يُعاد كما هو عند إعادة المحاولة.</summary>
    public Guid? IdempotencyKey { get; set; }
}

/// <summary>PU-04 — رصيد فاتورة مشتريات لأغراض التخصيص.</summary>
public sealed class SupplierInvoiceResidualDto
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }
    public int InvoiceType { get; set; }

    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal ReturnsOnAccount { get; set; }
    public decimal NetAllocated { get; set; }

    /// <summary>المتبقي المحسوب: T − P − RA − N.</summary>
    public decimal Residual { get; set; }

    /// <summary>المتاح للتخصيص = max(0, Residual).</summary>
    public decimal Available { get; set; }

    public bool IsEligible { get; set; }
    public string? IneligibilityReason { get; set; }
}

/// <summary>PU-04 — صف تخصيص للعرض.</summary>
public sealed class PaymentAllocationDto
{
    public Guid Id { get; set; }
    public Guid PaymentVoucherId { get; set; }
    public Guid PurchaseInvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public short Direction { get; set; }
    public Guid? ReversalOfAllocationId { get; set; }
    public Guid? ReversalDocumentId { get; set; }
    public DateTime AllocatedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>PU-04 — طلب عكس سند دفع (كلي أو جزئي) مع إعادة توزيع إلزامية عند الجزئي.</summary>
public sealed class CreatePaymentVoucherReversalDto
{
    [Required]
    public Guid OriginalPaymentVoucherId { get; set; }

    public DateTime VoucherDate { get; set; } = DateTime.Today;

    [StringLength(500)]
    public string? Reason { get; set; }

    /// <summary>
    /// الصفوف الأصلية المعكوسة ومبالغها — **إلزامية لسندات المورد فقط**.
    /// سند رد العميل لا يملك صفوف تخصيص أصلًا؛ خدمة العكس تتجاهلها في مساره.
    /// </summary>
    public List<PaymentAllocationReversalInput> Allocations { get; set; } = new();

    /// <summary>إعادة توزيع المبلغ المعكوس على فواتير أخرى (إلزامية عند العكس الجزئي).</summary>
    public List<PaymentAllocationInput> Reallocations { get; set; } = new();

    public Guid? IdempotencyKey { get; set; }
}

public sealed class PaymentAllocationReversalInput
{
    public Guid AllocationId { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>
/// PU-04 (D13-B/H-1) — إعادة توزيع مبلغ تخصيص بين الفواتير (تصحيح بحت، بلا قيد محاسبي).
/// المجموع المُعاد توزيعه يجب أن يساوي مجموع المبالغ المسحوبة من الصفوف الأصلية.
/// </summary>
public sealed class ReallocateAllocationsDto
{
    [Required]
    public Guid PaymentVoucherId { get; set; }

    [MinLength(1)]
    public List<PaymentAllocationReversalInput> FromAllocations { get; set; } = new();

    [MinLength(1)]
    public List<PaymentAllocationInput> ToInvoices { get; set; } = new();

    [StringLength(500)]
    public string? Reason { get; set; }
}

/// <summary>PU-04 — نتيجة عكس سند الدفع.</summary>
public sealed class PaymentVoucherReversalDto
{
    public Guid Id { get; set; }
    public string ReversalNumber { get; set; } = string.Empty;
    public Guid OriginalPaymentVoucherId { get; set; }
    /// <summary>المورد — null في عكس سند رد العميل.</summary>
    public Guid? SupplierId { get; set; }
    /// <summary>العميل — يُملأ في عكس سند رد العميل.</summary>
    public Guid? CustomerId { get; set; }
    public DateTime VoucherDate { get; set; }
    public decimal Amount { get; set; }
    public int Status { get; set; }
    public Guid? JournalEntryId { get; set; }
    public bool OriginalFullyReversed { get; set; }
    public List<PaymentAllocationDto> Allocations { get; set; } = new();
}

/// <summary>PU-04 — صف مطابقة ثلاثية لمورد (ΣR · F2 · F1 المنسوب).</summary>
public sealed class SupplierReconciliationDto
{
    public Guid SupplierId { get; set; }
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>مجموع متبقيات الفواتير (قد يكون سالبًا — لا يُخفى).</summary>
    public decimal SumResidual { get; set; }

    /// <summary>رصيد المورد التشغيلي (Suppliers.CurrentBalance).</summary>
    public decimal SupplierBalance { get; set; }

    /// <summary>رصيد 2200 المنسوب للمورد من المستندات (فاتورة/مردود/سند/عكس/شيك).</summary>
    public decimal AttributedLedger { get; set; }

    /// <summary>الفرق ΣR − F2 (لا يُسوّى تلقائيًا).</summary>
    public decimal ResidualVsBalance { get; set; }

    /// <summary>الفرق F2 − F1 المنسوب (لا يُسوّى تلقائيًا).</summary>
    public decimal BalanceVsLedger { get; set; }

    /// <summary>عدد الفواتير ذات رصيد سالب (تحتاج مراجعة).</summary>
    public int NegativeInvoices { get; set; }

    public string? Notes { get; set; }
}

/// <summary>PU-04 — نتيجة المطابقة الكلية (بما فيها غير المنسوب).</summary>
public sealed class SupplierReconciliationReportDto
{
    public DateTime AsOf { get; set; }

    /// <summary>رصيد حساب 2200 الكلي من الأستاذ حتى التاريخ.</summary>
    public decimal LedgerAccountBalance { get; set; }

    public decimal TotalSumResidual { get; set; }
    public decimal TotalSupplierBalance { get; set; }
    public decimal TotalAttributedLedger { get; set; }

    /// <summary>القيود اليدوية/الافتتاحية والقيود غير المرتبطة بمستند مورد.</summary>
    public decimal UnattributedLedger { get; set; }

    public List<SupplierReconciliationDto> Rows { get; set; } = new();
}

/// <summary>
/// PU-04 (M-3) — صف شاذ في سجل التخصيصات. فحص **قراءة فقط**؛ لا يقترح تصحيحًا ولا يُسوّي شيئًا.
/// الأنواع الأربعة منقولة حرفيًا من R3 §9.1:
/// «صفوف شاذة: `ΣA ≠ Amount` · `A > R` · صف بلا سند/فاتورة · فاتورة محذوفة».
/// </summary>
public sealed class AllocationAnomalyDto
{
    /// <summary>نوع الشذوذ — انظر <see cref="AllocationAnomalyKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>وصف عربي موجز للعرض/التقرير.</summary>
    public string Description { get; set; } = string.Empty;

    public Guid? PaymentVoucherId { get; set; }
    public string? VoucherNumber { get; set; }

    public Guid? PurchaseInvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }

    /// <summary>القيمة المرصودة (المجموع الفعلي).</summary>
    public decimal? Actual { get; set; }

    /// <summary>القيمة المتوقعة (مبلغ السند أو المتاح) — إن وُجدت.</summary>
    public decimal? Expected { get; set; }
}

/// <summary>أنواع الصفوف الشاذة في فحص اتساق التخصيص (M-3) — وفق R3 §9.1.</summary>
public static class AllocationAnomalyKinds
{
    /// <summary>`ΣA ≠ Amount` لسند مُرحَّل (مقتضى D5-أ·1).</summary>
    public const string SumMismatch = "SumMismatch";

    /// <summary>مجموع التخصيصات الموجبة لفاتورة يتجاوز متاحها `Available = max(0, R)`.</summary>
    public const string ExceedsAvailable = "ExceedsAvailable";

    /// <summary>صف تخصيص يشير إلى سند غير موجود أو محذوف منطقيًا.</summary>
    public const string MissingVoucher = "MissingVoucher";

    /// <summary>صف تخصيص يشير إلى فاتورة غير موجودة أو محذوفة منطقيًا.</summary>
    public const string MissingInvoice = "MissingInvoice";
}
