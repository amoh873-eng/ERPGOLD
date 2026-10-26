using ERPSystem.Domain.Common;
using ERPSystem.Domain.Enums;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// دفعة الأرصدة الافتتاحية الرسمية (Opening Balance Batch).
///
/// مستند واحد لكل نطاق مالي (سنة مالية) يحمل بنوداً مدينة/دائنة صريحة، ويُرحّل عبر خدمة
/// القيود المركزية (JournalEntryService.PrepareEntryAsync) إلى قيد افتتاحي متوازن.
/// ملاحظات التصميم:
///  - النظام شركة/كيان مالي واحد لكل قاعدة بيانات (لا CompanyId)، فالنطاق المالي = السنة المالية.
///  - لا كتابة مباشرة على Account.CurrentBalance؛ الرصيد يتغيّر فقط كنتيجة ترحيل القيد المركزي.
///  - التصحيح بعد الترحيل بقيد عكسي مرتبط بالأصل (ReversalOfBatchId / ReversedByBatchId) لا بالحذف.
/// </summary>
public class OpeningBalanceBatch : BaseEntity
{
    /// <summary>السنة المالية — نطاق التفرد (دفعة افتتاحية فعّالة واحدة لكل سنة مالية).</summary>
    public int FiscalYear { get; set; }

    /// <summary>تاريخ الترحيل المحاسبي — يجب أن يقع داخل السنة المالية نفسها.</summary>
    public DateTime EntryDate { get; set; }

    /// <summary>حالة الدفعة (Draft → Posted → Reversed).</summary>
    public OpeningBalanceBatchStatus Status { get; set; } = OpeningBalanceBatchStatus.Draft;

    /// <summary>مرجع حر للتدقيق (يظهر في القيد المحاسبي).</summary>
    public string? Reference { get; set; }

    /// <summary>وصف الدفعة (يظهر في القيد المحاسبي).</summary>
    public string? Description { get; set; }

    /// <summary>القيد المحاسبي الناتج — يُملأ عند الترحيل (لا حذف متتالٍ: تدقيق كامل).</summary>
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    /// <summary>في دفعة العكس فقط: معرّف الدفعة الأصلية التي تُعكس.</summary>
    public Guid? ReversalOfBatchId { get; set; }

    /// <summary>على الدفعة الأصلية عند عكسها: معرّف دفعة العكس المرتبطة.</summary>
    public Guid? ReversedByBatchId { get; set; }

    /// <summary>مستخدم الترحيل (للتدقيق) — يُملأ عند الانتقال Draft → Posted.</summary>
    public string? PostedBy { get; set; }

    /// <summary>توقيت الترحيل (للتدقيق).</summary>
    public DateTime? PostedAt { get; set; }

    /// <summary>بنود الدفعة (أرصدة الحسابات المتحركة).</summary>
    public ICollection<OpeningBalanceLine> Lines { get; set; } = new List<OpeningBalanceLine>();
}
