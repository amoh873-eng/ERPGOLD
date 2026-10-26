using ERPSystem.Domain.Enums;

namespace ERPSystem.Application.DTOs.Shifts;

/// <summary>بدء وردية: رصيد الفكة الافتتاحي (يُنشأ تلقائياً Float-In مرتبط).</summary>
public class StartShiftDto
{
    /// <summary>رصيد الفكة الافتتاحي (أكبر من صفر).</summary>
    public decimal OpeningFloatAmount { get; set; }
}

/// <summary>صف وردية في الشاشات والتاريخ.</summary>
public class CashierShiftDto
{
    public Guid Id;
    public string CashierUserId;
    public DateTime StartTime;
    public DateTime? EndTime;
    public int Status;
    public decimal OpeningFloatAmount;
    public Guid OpeningTransferId;
    public decimal? ExpectedCashAmount;
    public decimal? CountedCashAmount;
    public decimal? VarianceAmount;
    public string? ClosedByUserId;
    public string? Notes;
    public Guid? JournalEntryId;
    public string EntryNumber;
}

/// <summary>ملخّص مصادر المعادلة لوردية محددة (يُعرض قبل الإغلاق ويدخل في حساب المتوقَّع).</summary>
public class ShiftSummaryDto
{
    public decimal OpeningFloat = 0m;
    public decimal CashSales = 0m;
    public decimal CashRefunds = 0m;
    public decimal FloatsIn = 0m;
    public decimal FloatsOut = 0m;
    public decimal Expected { get; set; } = 0m;

    /// <summary>
    /// SUB-2b/P3 — ردود عملاء **نقدية من الخزنة الرئيسية (1100)** داخل نافذة الوردية.
    ///
    /// ═══ لماذا لا تدخل <see cref="Expected"/>؟ ═══
    /// معادلة تسوية الوردية تقيس **درج الكاش 1105** فقط: مردودات POS النقدية تُقيَّد ضد 1105
    /// (تُخصم في <see cref="CashRefunds"/>). أما سند رد العميل فيُقيَّد دائن **1100** (خزنة)،
    /// فلا يُنقص الدرج ⇒ إضافته إلى Expected كانت ستُنتج عجزًا وهميًا مساويًا لمبلغ الرد.
    /// يُعرض هذا الحقل بندًا مستقلًا للتفسير والتدقيق فقط، ومجموع الردود النقدية الكلي
    /// = <see cref="CashRefunds"/> (من الدرج) + هذا الحقل (من الخزنة).
    /// ⚠️ توحيد مسار الدفع ليكون من الدرج قرار مالي/معماري مفتوح (انظر تقرير المرحلة).
    /// </summary>
    public decimal CustomerCashRefundsFromSafe = 0m;
}

/// <summary>فلترة سجل الورديات (التاريخ + الكاشير).</summary>
public class CashierShiftFilterDto
{
    public string? Cashier;
    public DateTime? From;
    public DateTime? To;
}