using ERPSystem.Domain.Common;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// P0-2 (المرحلة B) — تخصيص مبلغ من سند قبض عميل على فاتورة مبيعات.
///
/// القواعد المحاسبية الملزمة:
///  • صف التخصيص **لا يُنشئ أي قيد محاسبي** — القيد الوحيد هو قيد سند القبض نفسه
///    (التخصيص توزيع تحليلي لتسديد موجود في الأستاذ، ولا يكرر القيد الأصلي).
///  • <see cref="Direction"/> = +1 تخصيص · −1 عكس (مرتبط بالأصل عبر <see cref="ReversalOfAllocationId"/>).
///  • العكس لا يعدّل الصف الأصلي ولا يحذفه؛ الأثر التاريخي محفوظ دائمًا (لا حذف ولا تعديل مبلغ).
///  • لا يُستخدم الحذف المنطقي للإبطال؛ الإبطال بالعكس فقط (نمط PU-04 المرجعي).
///  • لا يُخصَّص إلا سند قبض **مُرحَّل** وفاتورة بيع **مُرحَّلة آجلة** لنفس العميل.
/// </summary>
public class CustomerPaymentAllocation : BaseEntity
{
    /// <summary>العميل (مكرر للتحقق والتقارير — يجب أن يطابق العميل في السند والفاتورة).</summary>
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>سند القبض مصدر المال (يجب أن يكون مُرحَّلًا).</summary>
    public Guid ReceiptVoucherId { get; set; }
    public ReceiptVoucher? ReceiptVoucher { get; set; }

    /// <summary>فاتورة المبيعات المخصَّص عليها (يجب أن تكون مُرحَّلة وآجلة).</summary>
    public Guid SalesInvoiceId { get; set; }
    public SalesInvoice? SalesInvoice { get; set; }

    /// <summary>المبلغ المخصَّص، دائمًا أكبر من صفر.</summary>
    public decimal Amount { get; set; }

    /// <summary>+1 تخصيص · −1 عكس.</summary>
    public short Direction { get; set; } = 1;

    /// <summary>مرجع الصف الأصلي عند العكس (لا يُشير إلا إلى صف Direction = +1).</summary>
    public Guid? ReversalOfAllocationId { get; set; }
    public CustomerPaymentAllocation? ReversalOfAllocation { get; set; }

    /// <summary>توقيت التخصيص/العكس (داخل نفس معاملة المستند) — أساس سياسة تاريخ القطع.</summary>
    public DateTime AllocatedAt { get; set; }

    /// <summary>سبب العكس أو ملاحظة التخصيص.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// مفتاح منع تكرار «نية» التخصيص/العكس عند إعادة الطلب (فريد جزئيًا: NULL مسموح).
    /// </summary>
    public Guid? IdempotencyKey { get; set; }
}
