using ERPSystem.Application.DTOs.Accounting;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// إدارة الفترات المحاسبية (قفل/فتح) + الفحص المركزي الذي يمنع الترحيل داخل فترة مغلقة.
///
/// الفحص المركزي مُطبَّق أيضًا داخل <c>JournalEntryService.PrepareEntryAsync</c> عبر
/// <c>AccountingPeriodGuard</c> — فلا يملك أي مسار ترحيل أن يلتفّ على القفل.
/// </summary>
public interface IAccountingPeriodService
{
    /// <summary>الأشهر الاثنا عشر لسنة مالية مع حالتها (الغياب = مفتوحة).</summary>
    Task<AccountingPeriodYearDto> GetYearAsync(int fiscalYear);

    /// <summary>يغلق شهرًا (يمنع أي ترحيل جديد بتاريخ داخله). لا يعدّل أي قيد قائم.</summary>
    Task<AccountingPeriodDto> CloseAsync(int fiscalYear, int month, string? note, string userId);

    /// <summary>يعيد فتح شهر مغلق بقرار مُوثَّق (يمنع الترحيل بعده فقط ما دام مغلقًا).</summary>
    Task<AccountingPeriodDto> ReopenAsync(int fiscalYear, int month, string? note, string userId);

    /// <summary>فحص عام: هل يسمح بترحيل بتاريخ معيّن؟ (يُرمى PeriodClosedException إذا مغلقة).</summary>
    Task EnsurePostingAllowedAsync(DateTime entryDate);
}
