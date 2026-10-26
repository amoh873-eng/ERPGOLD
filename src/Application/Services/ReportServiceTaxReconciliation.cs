using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

public partial class ReportService
{
    /// <summary>
    /// لوحة التسوية الضريبية الموحّدة: تجمع ضريبة المبيعات (المخرجات) وضريبة المشتريات (المدخلات)
    /// لحساب الصافي المستحق (Payable) أو المسترد (Refundable) خلال الفترة.
    ///
    /// المصدر: سطور الفاتورة (SalesInvoiceLine.TaxAmount / PurchaseInvoiceLine.TaxAmount) —
    /// لقطة تاريخية، لا رأس الفاتورة، ولا إعادة احتساب للفواتير القديمة.
    /// </summary>
    public async Task<TaxReconciliationDto> GetTaxReconciliationAsync(DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toExclusive = to.Date.AddDays(1);

        var salesInvoices = await _context.Set<SalesInvoice>()
            .Where(s => !s.IsDeleted
                        && s.Status != DocumentStatus.Cancelled
                        && s.InvoiceDate >= fromDate
                        && s.InvoiceDate < toExclusive)
            .ToListAsync();

        var purchInvoices = await _context.Set<PurchaseInvoice>()
            .Where(s => !s.IsDeleted
                        && s.Status != DocumentStatus.Cancelled
                        && s.InvoiceDate >= fromDate
                        && s.InvoiceDate < toExclusive)
            .ToListAsync();

        // ضريبة المخرجات/«المسجّلة» من سطور الفاتورة (لقطة تاريخية)
        var salesIds = salesInvoices.Select(i => i.Id).ToList();
        var purchIds = purchInvoices.Select(i => i.Id).ToList();

        var salesLines = salesIds.Count > 0
            ? await _context.Set<SalesInvoiceLine>().Where(l => salesIds.Contains(l.SalesInvoiceId)).ToListAsync()
            : new List<SalesInvoiceLine>();
        var purchLines = purchIds.Count > 0
            ? await _context.Set<PurchaseInvoiceLine>().Where(l => purchIds.Contains(l.PurchaseInvoiceId)).ToListAsync()
            : new List<PurchaseInvoiceLine>();

        var salesTax = salesLines.Sum(l => l.TaxAmount);
        var purchTax = purchLines.Sum(l => l.TaxAmount);

        // تفصيل التسوية حسب نسبة الضريبة (بلا TaxClass)
        var rates = salesLines.Select(l => Math.Round(l.TaxRate, 2))
                              .Concat(purchLines.Select(l => Math.Round(l.TaxRate, 2)))
                              .Distinct()
                              .OrderBy(r => r)
                              .ToList();

        var breakdown = rates.Select(rate => new TaxRateBreakdownRow
        {
            TaxRate = rate,
            TaxDescription = SalesTaxTierCatalog.Describe(rate),
            SalesOutputTax = Math.Round(salesLines.Where(l => Math.Round(l.TaxRate, 2) == rate).Sum(l => l.TaxAmount), 2),
            RecordedPurchaseTax = Math.Round(purchLines.Where(l => Math.Round(l.TaxRate, 2) == rate).Sum(l => l.TaxAmount), 2)
        }).ToList();

        var salesGross = salesInvoices.Sum(i => i.SubTotal - i.DiscountAmount);
        var purchGross = purchInvoices.Sum(i => i.SubTotal);

        return new TaxReconciliationDto
        {
            From = from,
            To = to,
            TotalSalesGross = Math.Round(salesGross, 2),
            SalesTaxOutput = Math.Round(salesTax, 2),
            TotalPurchasesGross = Math.Round(purchGross, 2),
            RecordedPurchaseTax = Math.Round(purchTax, 2),
            DeductibleInputVat = null, // غير معروفة حتى يُضاف التصنيف المحاسبي/الضريبي
            Breakdown = breakdown
        };
    }
}
