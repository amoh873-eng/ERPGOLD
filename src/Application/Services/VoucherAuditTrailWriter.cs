using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// D3-A — كاتب سجل التدقيق لسندات القبض والصرف.
///
/// القواعد:
///   • الجدول مُلحَق فقط (append-only): لا تعديل ولا حذف من أي مسار.
///   • لا تُسجَّل أسرار أو كلمات مرور أو محتوى مرفقات — وصف مختصر وقيم محاسبية أساسية فقط.
///   • يُضاف السجل إلى نفس سياق/معاملة المستند ⇒ يُحفظ في نفس SaveChanges (ذرّي).
/// </summary>
internal static class VoucherAuditTrailWriter
{
    /// <summary>اسم الكيان كما يُخزَّن في AuditTrails.EntityName لسندات القبض.</summary>
    public const string EntityReceipt = "ReceiptVoucher";

    /// <summary>اسم الكيان كما يُخزَّن في AuditTrails.EntityName لسندات الصرف.</summary>
    public const string EntityPayment = "PaymentVoucher";

    public const string ActionCreated = "Created";
    public const string ActionUpdated = "Updated";
    public const string ActionApproved = "Approved";
    public const string ActionPosted = "Posted";
    public const string ActionCancelled = "Cancelled";

    /// <summary>عكس مستند (سند أصلي صار Reversed) — ثابت بدل نص حر (R20).</summary>
    public const string ActionReversed = "Reversed";

    // ═══ P0-2 (المرحلة B) — أفعال تخصيص المقبوضات على فواتير العملاء ═══
    /// <summary>إنشاء صف/صفوف تخصيص (توزيع تحليلي — بلا قيد محاسبي).</summary>
    public const string ActionAllocated = "Allocated";

    /// <summary>تطبيق اقتراح FIFO بعد تأكيد المستخدم (يُسجَّل الترتيب والمبالغ).</summary>
    public const string ActionFifoApplied = "FifoApplied";

    /// <summary>عكس صف تخصيص (كلي/جزئي) — لا يُحذف الأصل.</summary>
    public const string ActionAllocationReversed = "AllocationReversed";

    /// <summary>استبدال مجموعة تخصيصات السند (عكس القائم ثم إضافة الجديد) — بلا حذف تاريخي.</summary>
    public const string ActionAllocationsReplaced = "AllocationsReplaced";

    /// <summary>يضيف سطر تدقيق إلى السياق (لا يحفظ) — يُحفظ مع السند في نفس المعاملة.</summary>
    public static void Write(
        DbContext context,
        string entityName,
        Guid entityId,
        string? documentNumber,
        string action,
        string? details,
        string? performedBy,
        Guid? journalEntryId = null)
    {
        var now = DateTime.UtcNow;
        context.Set<AuditTrail>().Add(new AuditTrail
        {
            Id = Guid.NewGuid(),
            EntityName = entityName,
            EntityId = entityId,
            DocumentNumber = documentNumber,
            Action = action,
            Details = details,
            PerformedBy = string.IsNullOrWhiteSpace(performedBy) ? "غير معروف" : performedBy,
            PerformedAt = now,
            JournalEntryId = journalEntryId,
            CreatedAt = now,
            CreatedBy = performedBy
        });
    }
}
