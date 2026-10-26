using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// إدارة الفترات المحاسبية: عرض أشهر سنة مالية، إغلاق شهر، وإعادة فتحه — مع تسجيل تدقيقي.
///
/// المبادئ:
///  - لا حذف ولا تعديل لأي قيد عند الإغلاق: الإغلاق **حاجز ترحيل** فقط.
///  - غياب صفّ الشهر = مفتوحة (توافق خلفي كامل).
///  - التصريح من طبقة الاستضافة (<see cref="IAccountingPeriodAuthorization"/>) — لا يُمرَّر من الواجهة.
///  - الفحص المركزي للترحيل في <c>AccountingPeriodGuard</c> وليس هنا فقط.
/// </summary>
public class AccountingPeriodService : IAccountingPeriodService
{
    private static readonly string[] MonthNamesAr =
    {
        "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو",
        "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر"
    };

    private readonly DbContext _context;
    private readonly IAccountingPeriodAuthorization? _authorization;

    public AccountingPeriodService(DbContext context, IAccountingPeriodAuthorization? authorization = null)
    {
        _context = context;
        _authorization = authorization;
    }

    public async Task<AccountingPeriodYearDto> GetYearAsync(int fiscalYear)
    {
        EnsureAllowed(_authorization?.CanView() == true, AccountingPeriodRules.Permission(AccountingPeriodRules.ViewAction));
        EnsureValidYear(fiscalYear);

        var rows = await _context.Set<AccountingPeriod>()
            .AsNoTracking()
            .Where(p => p.FiscalYear == fiscalYear)
            .ToListAsync();

        var byMonth = rows
            .GroupBy(r => r.Month)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt).First());

        var result = new AccountingPeriodYearDto { FiscalYear = fiscalYear };
        for (var m = 1; m <= 12; m++)
        {
            if (byMonth.TryGetValue(m, out var row))
                result.Months.Add(MapToDto(row, hasRow: true));
            else
                result.Months.Add(new AccountingPeriodDto
                {
                    FiscalYear = fiscalYear,
                    Month = m,
                    MonthNameAr = MonthNamesAr[m - 1],
                    Status = (int)AccountingPeriodStatus.Open,
                    StatusNameAr = "مفتوحة",
                    HasRow = false
                });
        }

        return result;
    }

    public async Task<AccountingPeriodDto> CloseAsync(int fiscalYear, int month, string? note, string userId)
    {
        EnsureAllowed(_authorization?.CanClose() == true, AccountingPeriodRules.Permission(AccountingPeriodRules.EditAction));
        EnsureValidPeriod(fiscalYear, month);

        return await DbTransactionHelper.ExecuteAsync(_context, async () =>
        {
            var period = await GetOrCreateTrackedAsync(fiscalYear, month, userId);

            if (period.Status == AccountingPeriodStatus.Closed)
                throw new InvalidOperationException(
                    $"الفترة المحاسبية {fiscalYear}-{month:00} مغلقة بالفعل.");

            period.Status = AccountingPeriodStatus.Closed;
            period.ClosedAt = DateTime.UtcNow;
            period.ClosedBy = userId;
            period.Note = note;
            period.UpdatedAt = DateTime.UtcNow;
            period.UpdatedBy = userId;

            await _context.SaveChangesAsync();
            return MapToDto(period, hasRow: true);
        });
    }

    public async Task<AccountingPeriodDto> ReopenAsync(int fiscalYear, int month, string? note, string userId)
    {
        EnsureAllowed(_authorization?.CanReopen() == true, AccountingPeriodRules.Permission(AccountingPeriodRules.EditAction));
        EnsureValidPeriod(fiscalYear, month);

        return await DbTransactionHelper.ExecuteAsync(_context, async () =>
        {
            var current = await _context.Set<AccountingPeriod>()
                .FirstOrDefaultAsync(p => p.FiscalYear == fiscalYear && p.Month == month);

            if (current is null || current.Status != AccountingPeriodStatus.Closed)
                throw new InvalidOperationException(
                    $"الفترة المحاسبية {fiscalYear}-{month:00} ليست مغلقة، فلا حاجة لإعادة الفتح.");

            current.Status = AccountingPeriodStatus.Open;
            current.ReopenedAt = DateTime.UtcNow;
            current.ReopenedBy = userId;
            current.Note = note;
            current.UpdatedAt = DateTime.UtcNow;
            current.UpdatedBy = userId;

            await _context.SaveChangesAsync();
            return MapToDto(current, hasRow: true);
        });
    }

    public Task EnsurePostingAllowedAsync(DateTime entryDate)
        => AccountingPeriodGuard.EnsurePostingAllowedAsync(_context, entryDate);

    // ─────────────────────────── مساعدات ───────────────────────────

    private async Task<AccountingPeriod> GetOrCreateTrackedAsync(int fiscalYear, int month, string userId)
    {
        var existing = await _context.Set<AccountingPeriod>()
            .FirstOrDefaultAsync(p => p.FiscalYear == fiscalYear && p.Month == month);

        if (existing is not null)
            return existing;

        var created = new AccountingPeriod
        {
            Id = Guid.NewGuid(),
            FiscalYear = fiscalYear,
            Month = month,
            Status = AccountingPeriodStatus.Open,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = userId
        };

        _context.Set<AccountingPeriod>().Add(created);
        return created;
    }

    private static AccountingPeriodDto MapToDto(AccountingPeriod p, bool hasRow) => new()
    {
        FiscalYear = p.FiscalYear,
        Month = p.Month,
        MonthNameAr = MonthNamesAr[p.Month - 1],
        Status = (int)p.Status,
        StatusNameAr = p.Status == AccountingPeriodStatus.Closed ? "مغلقة" : "مفتوحة",
        HasRow = hasRow,
        ClosedBy = p.ClosedBy,
        ClosedAt = p.ClosedAt,
        ReopenedBy = p.ReopenedBy,
        ReopenedAt = p.ReopenedAt,
        Note = p.Note
    };

    private static void EnsureValidYear(int fiscalYear)
    {
        if (fiscalYear < 1900 || fiscalYear > 2100)
            throw new InvalidOperationException("السنة المالية خارج النطاق المسموح (1900-2100).");
    }

    private static void EnsureValidPeriod(int fiscalYear, int month)
    {
        EnsureValidYear(fiscalYear);
        if (month < 1 || month > 12)
            throw new InvalidOperationException("الشهر يجب أن يكون بين 1 و 12.");
    }

    private static void EnsureAllowed(bool allowed, string permission)
    {
        if (!allowed)
            throw new UnauthorizedAccessException(
                $"لا تملك صلاحية تنفيذ عملية الفترات المحاسبية ({permission}).");
    }
}
