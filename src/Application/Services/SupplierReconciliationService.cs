using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// PU-04 (D12) — تقرير المطابقة الثلاثي: ΣR · F2 · F1 المنسوب + غير المنسوب.
///
/// قواعد ملزمة:
///  • لا مساواة مفروضة بين الأرصدة، ولا قيد تسوية تلقائي.
///  • F1 المنسوب يُشتق من مستندات المورد (فاتورة آجلة/مردود M1/سند صرف/عكس).
///  • الشيكات والقيود اليدوية والافتتاحية تظهر في «غير المنسوب» لأن بنود القيود لا تحمل بُعد طرف.
/// </summary>
public sealed class SupplierReconciliationService
{
    private readonly DbContext _context;

    public SupplierReconciliationService(DbContext context) => _context = context;

    public async Task<SupplierReconciliationReportDto> GetAsync(DateTime asOf, CancellationToken ct = default)
    {
        var report = new SupplierReconciliationReportDto { AsOf = asOf.Date };
        var query = new SupplierAllocationQueries(_context);

        var suppliers = await _context.Set<Supplier>().AsNoTracking()
            .Where(s => !s.IsDeleted)
            .OrderBy(s => s.Code)
            .ToListAsync(ct);

        var payableAccountIds = await _context.Set<Account>().AsNoTracking()
            .Where(a => a.Code == "2200")
            .Select(a => a.Id)
            .ToListAsync(ct);

        var ledgerLines = await _context.Set<JournalEntryLine>().AsNoTracking()
            .Include(l => l.JournalEntry)
            .Where(l => !l.IsDeleted
                        && payableAccountIds.Contains(l.AccountId)
                        && l.JournalEntry != null
                        && l.JournalEntry.EntryDate <= asOf.Date)
            .Select(l => new { l.DebitAmount, l.CreditAmount })
            .ToListAsync(ct);

        report.LedgerAccountBalance = MoneyRounding.Round(
            ledgerLines.Sum(l => l.CreditAmount) - ledgerLines.Sum(l => l.DebitAmount));

        foreach (var supplier in suppliers)
        {
            var invoices = await query.GetInvoicesAsync(supplier.Id, asOf, onlyEligible: false, ct);
            var onAccountInvoices = invoices
                .Where(i => i.InvoiceType == (int)PurchaseInvoiceType.OnAccount)
                .ToList();

            var sumResidual = onAccountInvoices.Sum(i => i.Residual);
            var invoiceLedger = onAccountInvoices.Sum(i => i.TotalAmount);

            var returnsLedger = await _context.Set<PurchaseReturn>().AsNoTracking()
                .Where(r => r.SupplierId == supplier.Id
                            && !r.IsDeleted
                            && r.Status == DocumentStatus.Posted
                            && r.SettlementType == PurchaseReturnSettlementType.OnAccountCredit
                            && r.ReturnDate <= asOf.Date)
                .SumAsync(r => (decimal?)r.TotalAmount, ct) ?? 0m;

            // ملاحظة محاسبية: السند المعكوس كليًا (Reversed) **ما زال** يمثل حركة نقدية حدثت،
            // فيُحتسب −Amount هنا ويُقابله +Amount في مستند العكس ⇒ الصافي صفر بلا ازدواج.
            var voucherLedger = await _context.Set<PaymentVoucher>().AsNoTracking()
                .Where(v => v.SupplierId == supplier.Id
                            && !v.IsDeleted
                            && (v.Status == VoucherStatus.Posted || v.Status == VoucherStatus.Reversed)
                            && v.VoucherDate <= asOf.Date)
                .SumAsync(v => (decimal?)v.Amount, ct) ?? 0m;

            var reversalLedger = await _context.Set<PaymentVoucherReversal>().AsNoTracking()
                .Where(r => r.SupplierId == supplier.Id
                            && !r.IsDeleted
                            && r.Status == VoucherStatus.Posted
                            && r.VoucherDate <= asOf.Date)
                .SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;

            var attributed = MoneyRounding.Round(invoiceLedger - returnsLedger - voucherLedger + reversalLedger);
            var balance = MoneyRounding.Round(supplier.CurrentBalance);

            report.Rows.Add(new SupplierReconciliationDto
            {
                SupplierId = supplier.Id,
                SupplierCode = supplier.Code,
                SupplierName = supplier.NameAr,
                SumResidual = MoneyRounding.Round(sumResidual),
                SupplierBalance = balance,
                AttributedLedger = attributed,
                ResidualVsBalance = MoneyRounding.Round(sumResidual - balance),
                BalanceVsLedger = MoneyRounding.Round(balance - attributed),
                NegativeInvoices = onAccountInvoices.Count(i => i.Residual < 0m),
                Notes = "الفروق لا تُسوّى تلقائيًا. غير المنسوب يشمل القيود اليدوية والافتتاحية والشيكات."
            });
        }

        report.TotalSumResidual = MoneyRounding.Round(report.Rows.Sum(r => r.SumResidual));
        report.TotalSupplierBalance = MoneyRounding.Round(report.Rows.Sum(r => r.SupplierBalance));
        report.TotalAttributedLedger = MoneyRounding.Round(report.Rows.Sum(r => r.AttributedLedger));
        report.UnattributedLedger = MoneyRounding.Round(report.LedgerAccountBalance - report.TotalAttributedLedger);
        return report;
    }
}
