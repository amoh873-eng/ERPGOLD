using ERPSystem.Domain.Common;
using ERPSystem.Domain.Enums;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// PU-04 — مستند عكس سند صرف مورد (D7-ب): مستند مستقل بدورة حياة كاملة.
///
/// لماذا مستند مستقل؟
///  • سند القبض الحالي مقيّد بالعملاء (1200 + Customer.CurrentBalance) ولا يصلح لعكس سند صرف.
///  • الفصل الدلالي يمنع الالتباس مع سند قبض عميل أو سند صرف عادي.
///  • كل عكس يُنشئ قيدًا معاكسًا متوازنًا مرة واحدة، ويُربط بالأصل عبر <see cref="OriginalPaymentVoucherId"/>.
/// </summary>
public class PaymentVoucherReversal : BaseEntity
{
    /// <summary>رقم المستند (بادئة PVR).</summary>
    public string ReversalNumber { get; set; } = string.Empty;

    /// <summary>السند الأصلي المُعكَس — فريد: لا يُعكس السند أكثر من مستند عكس واحد.</summary>
    public Guid OriginalPaymentVoucherId { get; set; }
    public PaymentVoucher? OriginalPaymentVoucher { get; set; }

    /// <summary>المورد (سند مورد) — اختياري في عكس سند رد العميل (SUB-2b/P3).</summary>
    public Guid? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>العميل (عكس سند رد) — بديل SupplierId؛ ck_pvr_party_xor يفرض طرفًا واحدًا.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public DateTime VoucherDate { get; set; }

    /// <summary>حالة المستند (Draft/Approved/Posted/Cancelled) — نفس دورة حياة السندات.</summary>
    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    /// <summary>إجمالي مبلغ العكس (= مجموع صفوف التخصيص العكسية).</summary>
    public decimal Amount { get; set; }

    /// <summary>حساب الصندوق/البنك الذي يُعاد إليه المبلغ (نفس حساب السند الأصلي).</summary>
    public Guid CashOrBankAccountId { get; set; }
    public Account? CashOrBankAccount { get; set; }

    /// <summary>حساب ذمم المورد (2200) — دائن في قيد العكس.</summary>
    public Guid PayableAccountId { get; set; }
    public Account? PayableAccount { get; set; }

    public string? Reason { get; set; }

    /// <summary>القيد العكسي — يُضبط مرة واحدة عند الترحيل.</summary>
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    /// <summary>مفتاح منع التكرار (N-1) — فريد جزئيًا عند وجوده.</summary>
    public Guid? IdempotencyKey { get; set; }

    /// <summary>رمز التزامن (منع الترحيل المتزامن المزدوج).</summary>
    public byte[]? RowVersion { get; set; }

    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}
