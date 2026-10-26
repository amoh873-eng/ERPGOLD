using ERPSystem.Application.DTOs.Accounting;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// مطابقة الحسابات الرقابية مع السجلات المساعدة — **قراءة فقط**.
/// لا يسوّي أي فرق ولا يعدّل أي رصيد: يعرض الرقابي والتفصيلي والفرق ومصدره.
/// </summary>
public interface ISubLedgerReconciliationService
{
    /// <summary>تقرير المطابقة (1200 العملاء · 2200 الموردين · 1300 المخزون الافتتاحي).</summary>
    Task<SubLedgerReconciliationDto> GetReportAsync();
}
