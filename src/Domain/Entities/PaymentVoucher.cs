using ERPSystem.Domain.Common;
using ERPSystem.Domain.Enums;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// سند صرف (Payment Voucher) — مستند مستقل بدورة حياة (مسودة ← اعتماد ← ترحيل ← إلغاء).
///
/// النطاق المعتمد (D5-A): صرف لتسوية ذمم الموردين ⇒ عند الترحيل:
///   مدين: حساب ذمم المورد (PayableAccountId) — دائن: الصندوق/البنك (CashOrBankAccountId).
///
/// القواعد الملزمة: نفس قواعد سند القبض (لا قيد قبل الترحيل، محرك مركزي واحد، لا ترحيل مزدوج).
/// </summary>
public class PaymentVoucher : BaseEntity
{
    /// <summary>رقم السند المتسلسل (مثال: PV-20261020-0001).</summary>
    public string VoucherNumber { get; set; } = string.Empty;

    /// <summary>تاريخ السند المحاسبي.</summary>
    public DateTime VoucherDate { get; set; }

    /// <summary>حالة السند وفق دورة الحياة الجديدة.</summary>
    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    /// <summary>
    /// المورد المسدَّد له (تسوية ذمم الموردين — D5-A).
    /// اختياري منذ SUB-2b/P3: سند رد العميل لا مورد له، والقيد ck_pv_party_xor يفرض طرفًا واحدًا.
    /// </summary>
    public Guid? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>العميل المستفيد من الرد (SUB-2b/P3) — بديل SupplierId في وضع الرد.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>
    /// المردود المرتبط (SUB-2b/P3) — إلزامي لسند رد العميل، وهو مصدر سقف الرد.
    /// ck_pv_customer_requires_return يمنع سند عميل بلا مردود.
    /// </summary>
    public Guid? SalesReturnId { get; set; }
    public SalesReturn? SalesReturn { get; set; }

    /// <summary>اسم المستفيد عند السداد النقدي بلا مورد مسجّل (اختياري — للطبع فقط).</summary>
    public string? BeneficiaryName { get; set; }

    /// <summary>طريقة الدفع: نقدي أو بنكي.</summary>
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    /// <summary>حساب الصندوق أو البنك الذي خرج منه المبلغ (دائن).</summary>
    public Guid CashOrBankAccountId { get; set; }
    public Account? CashOrBankAccount { get; set; }

    /// <summary>
    /// الحساب المقابل للطرف (مدين في الترحيل): 2200 للمورد · 1200 للعميل (ذمم مدينة).
    /// الاسم تاريخي؛ لا يتغيّر المخطط — التفسير بحسب الطرف.
    /// </summary>
    public Guid PayableAccountId { get; set; }
    public Account? PayableAccount { get; set; }

    /// <summary>المبلغ (دائمًا أكبر من صفر).</summary>
    public decimal Amount { get; set; }

    /// <summary>سبب الصرف أو البيان.</summary>
    public string? Description { get; set; }

    /// <summary>رقم مرجع خارجي (تحويل/شيك/مرجع مورد).</summary>
    public string? ExternalReference { get; set; }

    /// <summary>مسار مرفق اختياري.</summary>
    public string? AttachmentPath { get; set; }

    /// <summary>القيد المحاسبي الناتج — يُضبط مرة واحدة عند الترحيل فقط.</summary>
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    public DateTime? PostedAt { get; set; }
    public string? PostedBy { get; set; }

    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>رمز التزامن (منع الترحيل المتزامن المزدوج).</summary>
    public byte[]? RowVersion { get; set; }

    /// <summary>
    /// PU-04 (N-1) — مفتاح منع تكرار الإنشاء: يُولَّد مرة واحدة لكل «نية إنشاء» في الواجهة،
    /// ويُعاد إرساله عند إعادة المحاولة. فريد جزئيًا: NULL مسموح (توافق خلفي).
    /// </summary>
    public Guid? IdempotencyKey { get; set; }

    /// <summary>صفوف تخصيص السند (تُنشأ عند الترحيل فقط — D5-و/و-1).</summary>
    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();

    /// <summary>مستند العكس إن وُجد (علاقة عكسية واحدة).</summary>
    public PaymentVoucherReversal? Reversal { get; set; }
}
