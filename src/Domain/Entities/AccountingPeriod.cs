using ERPSystem.Domain.Common;
using ERPSystem.Domain.Enums;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// الفترة المحاسبية (Accounting Period) — شهر داخل سنة مالية، بحالة مفتوحة/مغلقة.
///
/// النطاق: النظام كيان مالي واحد لكل قاعدة بيانات (لا CompanyId)، فالمفتاح الطبيعي للفترة
/// هو (السنة المالية، الشهر) — وهو مفروض بفهرس فريد في قاعدة البيانات.
///
/// ملاحظات تصميمية:
///  - **غياب الصف = الفترة مفتوحة**: لا تُزرع صفوف لكل الأشهر، فيبقى السلوك السابق كما هو
///    حتى يُغلق مستخدم فترة بوعي (صفر أثر على أي بيانات قائمة).
///  - الإغلاق لا يحذف ولا يعدّل أي قيد: يمنع فقط **الترحيل الجديد** بتاريخ داخل الفترة المغلقة.
///  - إعادة الفتح مسجَّلة (من/متى/ملاحظة) للتدقيق، ولا تُنشئ قيودًا.
/// </summary>
public class AccountingPeriod : BaseEntity
{
    /// <summary>السنة المالية (السنة الميلادية للتاريخ المحاسبي).</summary>
    public int FiscalYear { get; set; }

    /// <summary>الشهر 1..12.</summary>
    public int Month { get; set; }

    /// <summary>حالة الفترة (مفتوحة/مغلقة) — غياب الصف يعني مفتوحة.</summary>
    public AccountingPeriodStatus Status { get; set; } = AccountingPeriodStatus.Open;

    /// <summary>من أغلق الفترة (للتدقيق).</summary>
    public string? ClosedBy { get; set; }

    /// <summary>متى أُغلقت الفترة (للتدقيق).</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>من أعاد فتح الفترة آخر مرة (للتدقيق).</summary>
    public string? ReopenedBy { get; set; }

    /// <summary>متى أُعيد فتح الفترة آخر مرة (للتدقيق).</summary>
    public DateTime? ReopenedAt { get; set; }

    /// <summary>ملاحظة اختيارية (سبب الإغلاق/إعادة الفتح).</summary>
    public string? Note { get; set; }
}
