namespace ERPSystem.Application.Exceptions;

/// <summary>
/// يُرمى عند محاولة ترحيل قيد بتاريخ يقع داخل فترة محاسبية **مغلقة**.
/// القاعدة: الفترة المغلقة لا تقبل ترحيلًا جديدًا؛ والتصحيح يكون بإعادة فتح الفترة بقرار
/// مُوثَّق ثم الترحيل، أو بترحيل التصحيح في فترة مفتوحة (لا تعديل تاريخي تلقائي).
/// الفحص مركزي في <c>JournalEntryService.PrepareEntryAsync</c> ⇒ يغطي كل المسارات
/// (الفواتير والمردودات والسندات والمصاريف والأرصدة الافتتاحية والقيود اليدوية...).
/// </summary>
public sealed class PeriodClosedException : InvalidOperationException
{
    public int FiscalYear { get; }
    public int Month { get; }

    public PeriodClosedException(int fiscalYear, int month)
        : base($"الفترة المحاسبية {fiscalYear}-{month:00} مغلقة. لا يمكن ترحيل قيد بهذا التاريخ — " +
               "أعد فتح الفترة بقرار موثَّق أو رحّل التصحيح في فترة مفتوحة.")
    {
        FiscalYear = fiscalYear;
        Month = month;
    }
}
