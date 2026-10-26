using ERPSystem.Domain.Common;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// PU-04 — تخصيص مبلغ من سند صرف مورد على فاتورة مشتريات.
///
/// القواعد المحاسبية:
///  • صف التخصيص **لا يُنشئ قيدًا محاسبيًا** — القيد الوحيد هو قيد سند الصرف (أو قيد العكس).
///  • <see cref="Direction"/> = +1 تخصيص · −1 عكس (مرتبط بالأصل عبر <see cref="ReversalOfAllocationId"/>).
///  • العكس لا يعدّل الصف الأصلي ولا يحذفه؛ الأثر التاريخي محفوظ دائمًا.
///  • لا يُستخدم الحذف المنطقي للإبطال؛ الإبطال بالعكس فقط.
/// </summary>
public class PaymentAllocation : BaseEntity
{
    /// <summary>سند الصرف الأصلي (حتى في صفوف العكس يبقى مرجعًا للسند الأصلي).</summary>
    public Guid PaymentVoucherId { get; set; }
    public PaymentVoucher? PaymentVoucher { get; set; }

    /// <summary>الفاتورة المخصَّصة.</summary>
    public Guid PurchaseInvoiceId { get; set; }
    public PurchaseInvoice? PurchaseInvoice { get; set; }

    /// <summary>المورد (مكرر للتحقق والتقارير — يجب أن يطابق السند والفاتورة).</summary>
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>مبلغ التخصيص، دائمًا أكبر من صفر.</summary>
    public decimal Amount { get; set; }

    /// <summary>+1 تخصيص · −1 عكس.</summary>
    public short Direction { get; set; } = 1;

    /// <summary>مرجع الصف الأصلي عند العكس (لا يُشير إلا إلى صف Direction = +1).</summary>
    public Guid? ReversalOfAllocationId { get; set; }
    public PaymentAllocation? ReversalOfAllocation { get; set; }

    /// <summary>مستند العكس الذي أنشأ هذا الصف (فقط عندما Direction = −1).</summary>
    public Guid? ReversalDocumentId { get; set; }
    public PaymentVoucherReversal? ReversalDocument { get; set; }

    /// <summary>توقيت التخصيص/العكس (داخل نفس معاملة المستند).</summary>
    public DateTime AllocatedAt { get; set; }

    /// <summary>سبب العكس أو ملاحظة التخصيص.</summary>
    public string? Note { get; set; }
}
