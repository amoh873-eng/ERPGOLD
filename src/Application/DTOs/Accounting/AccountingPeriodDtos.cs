namespace ERPSystem.Application.DTOs.Accounting;

/// <summary>فترة محاسبية (شهر) مع حالتها — تُعاد كصفوف الأشهر الاثني عشر للسنة المطلوبة.</summary>
public class AccountingPeriodDto
{
    public int FiscalYear { get; set; }

    /// <summary>الشهر 1..12.</summary>
    public int Month { get; set; }

    public string MonthNameAr { get; set; } = string.Empty;

    /// <summary>حالة الفترة (1=مفتوحة · 2=مغلقة).</summary>
    public int Status { get; set; }

    public string StatusNameAr { get; set; } = string.Empty;

    /// <summary>هل يوجد صفّ فعلي في قاعدة البيانات (false = مفتوحة افتراضيًا بلا صف).</summary>
    public bool HasRow { get; set; }

    public string? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? ReopenedBy { get; set; }
    public DateTime? ReopenedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>ملخّص حالة سنة مالية: عدد الأشهر المغلقة والمفتوحة.</summary>
public class AccountingPeriodYearDto
{
    public int FiscalYear { get; set; }
    public List<AccountingPeriodDto> Months { get; set; } = new();
    public int ClosedCount => Months.Count(m => m.Status == 2);
    public int OpenCount => Months.Count(m => m.Status != 2);
}
