using ERPSystem.Domain.Common;
using ERPSystem.Domain.Enums;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// سند قبض (Receipt Voucher) — مستند مستقل بدورة حياة (مسودة ← اعتماد ← ترحيل ← إلغاء).
///
/// النطاق المعتمد (D5-A): قبض لتسوية ذمم العملاء ⇒ عند الترحيل:
///   مدين: الصندوق/البنك (CashOrBankAccountId) — دائن: حساب ذمم العميل (ReceivableAccountId).
///
/// القواعد الملزمة:
///   • لا يُنشأ أي قيد إلا عند الترحيل، وعبر محرك القيود المركزي PrepareEntryAsync حصرًا.
///   • الربط بالقيد عبر JournalEntryId (علاقة موثوقة على جهة المستند — D4-A، بلا تغيير في رأس القيد).
///   • لا ترحيل مزدوج: JournalEntryId لا يُكتب إلا مرة، ويحميه فهرس فريد + قيد الحالة في الخدمة.
/// </summary>
public class ReceiptVoucher : BaseEntity
{
    /// <summary>رقم السند المتسلسل (مثال: RCV-20261020-0001) — من NumberSequenceHelper.</summary>
    public string VoucherNumber { get; set; } = string.Empty;

    /// <summary>تاريخ السند المحاسبي.</summary>
    public DateTime VoucherDate { get; set; }

    /// <summary>حالة السند وفق دورة الحياة الجديدة (سندات القبض والصرف فقط).</summary>
    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    /// <summary>العميل المُحصَّل منه (تسوية ذمم العملاء — D5-A).</summary>
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>طريقة القبض: نقدي أو بنكي (وفق PaymentMethod الحالي).</summary>
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    /// <summary>حساب الصندوق أو البنك الذي دخل إليه المبلغ (مدين) — حساب ورقي محسوم بـPostingAccountResolver.</summary>
    public Guid CashOrBankAccountId { get; set; }
    public Account? CashOrBankAccount { get; set; }

    /// <summary>حساب ذمم العميل المقابل (دائن) — يُحفظ للتحقق والتتبع (لا يُختار يدويًا).</summary>
    public Guid ReceivableAccountId { get; set; }
    public Account? ReceivableAccount { get; set; }

    /// <summary>المبلغ (دائمًا أكبر من صفر).</summary>
    public decimal Amount { get; set; }

    /// <summary>البيان أو وصف العملية.</summary>
    public string? Description { get; set; }

    /// <summary>رقم مرجع خارجي (تحويل بنكي/شيك/مرجع عميل) عند الحاجة.</summary>
    public string? ExternalReference { get; set; }

    /// <summary>مسار مرفق اختياري تحت wwwroot/uploads.</summary>
    public string? AttachmentPath { get; set; }

    /// <summary>القيد المحاسبي الناتج — يُضبط مرة واحدة عند الترحيل فقط.</summary>
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    // ── تواريخ وأطراف دورة الحياة ──
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    public DateTime? PostedAt { get; set; }
    public string? PostedBy { get; set; }

    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>رمز التزامن (منع الترحيل المتزامن المزدوج على مستوى قاعدة البيانات).</summary>
    public byte[]? RowVersion { get; set; }

    /// <summary>
    /// P0-2 (المرحلة B) — صفوف تخصيص هذا السند على فواتير العميل.
    /// التخصيص **لا يُنشئ أي قيد محاسبي**؛ القيد الوحيد هو قيد السند نفسه.
    /// </summary>
    public ICollection<CustomerPaymentAllocation> Allocations { get; set; } = new List<CustomerPaymentAllocation>();
}
