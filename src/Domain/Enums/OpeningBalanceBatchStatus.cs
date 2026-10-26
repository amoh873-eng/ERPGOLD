namespace ERPSystem.Domain.Enums;

/// <summary>
/// حالة دفعة الأرصدة الافتتاحية (Opening Balance Batch) — دورة حياة واضحة.
///
/// Draft    → مسودة: قابلة للتعديل/الحذف، ولا أثر محاسبي بعد (لا قيد ولا أرصدة).
/// Posted   → مُرحَّلة: أُنشئ القيد الافتتاحي وطُبّقت الأرصدة ذرياً؛ لا تُعدَّل ولا تُحذف.
/// Reversed → معكوسة: رُحّل لها قيد عكسي مرتبط بالأصل؛ التصحيح بالقيد العكسي لا بالحذف.
/// </summary>
public enum OpeningBalanceBatchStatus
{
    /// <summary>مسودة — بلا أثر محاسبي.</summary>
    Draft = 1,

    /// <summary>مُرحَّلة — قيد افتتاحي متوازن مطبَّق على الأرصدة.</summary>
    Posted = 2,

    /// <summary>معكوسة — رُحّل قيد عكسي مرتبط بالأصل.</summary>
    Reversed = 3
}
