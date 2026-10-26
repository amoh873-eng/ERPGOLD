using ERPSystem.Domain.Common;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// سجل تدقيق (Audit Trail) — D3-A: يُسجّل عمليات سندات القبض والصرف (إنشاء/تعديل/اعتماد/ترحيل/إلغاء)
/// مع المستخدم والتوقيت والتفاصيل.
///
/// قواعد السلامة:
///   • لا تُسجَّل كلمات المرور أو الأسرار أو محتوى المرفقات — فقط الوصف والحالة والقيم المحاسبية الأساسية.
///   • الجدول مُلحَق فقط (append-only) ولا يُعدَّل منه شيء من مسار الواجهة.
/// </summary>
public class AuditTrail : BaseEntity
{
    /// <summary>اسم الكيان المتأثر (مثال: ReceiptVoucher / PaymentVoucher).</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>معرّف المستند المتأثر.</summary>
    public Guid EntityId { get; set; }

    /// <summary>رقم المستند وقت العملية (للقراءة السريعة).</summary>
    public string? DocumentNumber { get; set; }

    /// <summary>العملية: Created / Updated / Approved / Posted / Cancelled / ReadReport …</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>تفاصيل مختصرة (بلا أي بيانات حساسة).</summary>
    public string? Details { get; set; }

    /// <summary>من نفّذ العملية (بريد/اسم المستخدم من هوية الجلسة).</summary>
    public string? PerformedBy { get; set; }

    /// <summary>توقيت العملية (UTC).</summary>
    public DateTime PerformedAt { get; set; }

    /// <summary>معرّف القيد المرتبط إن وُجد (للترحيل).</summary>
    public Guid? JournalEntryId { get; set; }
}
