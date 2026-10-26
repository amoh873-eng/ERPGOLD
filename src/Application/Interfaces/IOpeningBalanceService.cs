using ERPSystem.Application.DTOs.Accounting;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// عمليات الأرصدة الافتتاحية الرسمية (Opening Balances):
/// دفعة واحدة لكل سنة مالية، تُدخل على الحسابات المتحركة (Leaf) فقط، وتُرحَّل عبر خدمة القيود
/// المركزية ضمن معاملة ذرية واحدة (الدفعة + القيد + بنوده + تحديث الأرصدة معاً أو لا شيء).
/// لا كتابة مباشرة على CurrentBalance، ولا حساب مقابل مفترض.
/// </summary>
public interface IOpeningBalanceService
{
    /// <summary>ينشئ مسودة دفعة افتتاحية (يتحقق: السنة المالية، الحسابات المتحركة، التوازن، ومنع التكرار).</summary>
    Task<OpeningBalanceBatchDto> CreateDraftAsync(CreateOpeningBalanceBatchDto dto, string userId);

    /// <summary>يحدّث مسودة قائمة (مسموح في حالة Draft فقط).</summary>
    Task<OpeningBalanceBatchDto> UpdateDraftAsync(Guid batchId, UpdateOpeningBalanceBatchDto dto, string userId);

    /// <summary>يحذف (حذفاً ناعماً) مسودة قائمة (مسموح في حالة Draft فقط).</summary>
    Task DeleteDraftAsync(Guid batchId, string userId);

    /// <summary>يرحّل المسودة: يُنشئ القيد المحاسبي المتوازن ويحدّث الأرصدة ذرياً ثم يثبّت الحالة Posted.</summary>
    Task<OpeningBalanceBatchDto> PostAsync(Guid batchId, string userId);

    /// <summary>يعكس دفعة مُرحَّلة بقيد عكسي مرتبط بالأصل (لا حذف ولا تعديل للأصل).</summary>
    Task<OpeningBalanceBatchDto> ReverseAsync(Guid batchId, DateTime entryDate, string userId);

    /// <summary>كل الدفعات (الأحدث أولاً) مع بنودها.</summary>
    Task<List<OpeningBalanceBatchDto>> GetAllAsync();

    /// <summary>دفعة واحدة بمعرّفها مع بنودها.</summary>
    Task<OpeningBalanceBatchDto?> GetByIdAsync(Guid id);

    /// <summary>الدفعة الفعّالة (Draft/Posted) لسنة مالية — أو null.</summary>
    Task<OpeningBalanceBatchDto?> GetActiveForYearAsync(int fiscalYear);
}
