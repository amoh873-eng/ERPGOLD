using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

public partial class ReportService
{
    /// <summary>
    /// توليد تقرير ضريبة المشتريات: يجمع بنود فواتير المشتريات في [from, to] حسب نسبة
    /// الضريبة لكل سطر (المصدر: PurchaseInvoiceLine.TaxRate / TaxAmount — لقطة تاريخية).
    ///
    /// المبادئ المعتمدة:
    /// - يعتمد على سطر الفاتورة، لا على PurchaseInvoice.TaxRate (الرأس قد يكون متوسطاً مرجّحاً
    ///   في فواتير متعددة النسب وليس نسبة فعلية).
    /// - تعديل Item.TaxRate مستقبلاً لا يغيّر فواتير المشتريات السابقة (كل سطر يحتفظ بنسبته لحظة الإنشاء).
    /// - لا يعيد احتساب ضرائب الفواتير القديمة ولا يغيّرها — يعرض البيانات المخزّنة فعلاً.
    /// - تقرير تشغيلي: لا يعتبر أي نسبة في بطاقة المادة نسبةً قانونية؛ منطق الامتثال القانوني
    ///   (قابل للخصم/غير قابل للخصم، مردودات) يُبنى لاحقاً كطبقة منفصلة فوق هذه البيانات.
    /// </summary>
    public async Task<PurchaseTaxReportDto> GetPurchaseTaxReportAsync(DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toExclusive = to.Date.AddDays(1);

        // 1) فواتير الفترة مع بنودها (فلترة في DB) — نجمّع حسب نسبة كل سطر
        var invoices = await _context.Set<PurchaseInvoice>()
            .Include(x => x.Lines)
            .Where(s => !s.IsDeleted
                        && s.Status != DocumentStatus.Cancelled
                        && s.InvoiceDate >= fromDate
                        && s.InvoiceDate < toExclusive)
            .OrderBy(s => s.InvoiceDate)
            .ThenBy(s => s.InvoiceNumber)
            .ToListAsync();

        // 2) تجميع البنود حسب نسبة الضريبة لكل سطر
        var rows = invoices
            .SelectMany(x => x.Lines.Select(l => new { Invoice = x, Line = l }))
            .GroupBy(p => Math.Round(p.Line.TaxRate, 2))
            .Select(g => new PurchaseTaxReportRow
            {
                TaxRate = g.Key,
                TaxDescription = SalesTaxTierCatalog.Describe(g.Key),
                InvoiceCount = g.Select(p => p.Invoice.Id).Distinct().Count(),
                GrossAmount = Math.Round(g.Sum(p => p.Line.LineTotal), 2),
                TaxAmount = Math.Round(g.Sum(p => p.Line.TaxAmount), 2)
            })
            .OrderByDescending(r => r.TaxRate)
            .ToList();

        // 3) إجمالي المدين والدائن من قيود المشتريات (صندوق التحقق في التذييل)
        var entryIds = invoices
            .Select(s => s.JournalEntryId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        decimal totalDebit = 0m, totalCredit = 0m;
        if (entryIds.Count > 0)
        {
            var lines = await _context.Set<JournalEntryLine>()
                .Where(l => entryIds.Contains(l.JournalEntryId))
                .ToListAsync();
            totalDebit = Math.Round(lines.Sum(l => l.DebitAmount), 2);
            totalCredit = Math.Round(lines.Sum(l => l.CreditAmount), 2);
        }

        return new PurchaseTaxReportDto
        {
            From = from,
            To = to,
            Rows = rows,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit
        };
    }

    /// <summary>
    /// سجل ضريبة المشتريات التفصيلي — صف واحد لكل فاتورة شراء مع تفاصيل أسطرها.
    /// المصدر: PurchaseInvoiceLine.TaxRate / TaxAmount (لقطة تاريخية)، لا رأس الفاتورة.
    /// - يعمل مع الفواتير متعددة النسب (نسب الفاتورة تُجمَّع في عمود «النسب»).
    /// - لا يعيد احتساب الفواتير القديمة ولا يغيّر بياناتها.
    /// - المشتريات بلا حقل خصم حالياً ← الخصم 0 والصافي = إجمالي الأصناف.
    /// </summary>
    public async Task<PurchaseTaxRegisterDto> GetPurchaseTaxRegisterAsync(DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toExclusive = to.Date.AddDays(1);

        var invoices = await _context.Set<PurchaseInvoice>()
            .Include(x => x.Supplier)
            .Include(x => x.Lines).ThenInclude(l => l.Item)
            .Where(s => !s.IsDeleted
                        && s.Status != DocumentStatus.Cancelled
                        && s.InvoiceDate >= fromDate
                        && s.InvoiceDate < toExclusive)
            .OrderBy(s => s.InvoiceDate)
            .ThenBy(s => s.InvoiceNumber)
            .ToListAsync();

        var rows = invoices.Select(inv =>
        {
            // تفاصيل السطور — كلها مبنية من السطر (لا من رأس الفاتورة)
            var lines = inv.Lines.Select(l =>
            {
                var net = Math.Round(l.LineTotal, 2);
                return new PurchaseTaxRegisterLineDto
                {
                    ItemCode = l.Item?.Code ?? "—",
                    ItemNameAr = l.Item?.NameAr ?? "—",
                    Quantity = l.Quantity,
                    UnitCost = l.UnitCost,
                    Discount = 0m, // المشتريات بلا حقل خصم حالياً
                    Net = net,
                    TaxRate = l.TaxRate,
                    TaxAmount = l.TaxAmount,
                    Total = Math.Round(net + l.TaxAmount, 2)
                };
            }).ToList();

            var itemTotal = Math.Round(lines.Sum(x => x.Net), 2);
            var taxTotal = Math.Round(lines.Sum(x => x.TaxAmount), 2);
            var rates = lines.Select(x => x.TaxRate)
                             .Distinct()
                             .OrderBy(r => r)
                             .Select(r => r.ToString("0.#") + "%")
                             .ToList();

            return new PurchaseTaxRegisterRow
            {
                InvoiceId = inv.Id,
                InvoiceDate = inv.InvoiceDate,
                InvoiceNumber = inv.InvoiceNumber,
                SupplierName = inv.Supplier?.NameAr ?? "—",
                ItemTotal = itemTotal,
                DiscountTotal = 0m,
                NetTaxable = itemTotal,
                TaxTotal = taxTotal,
                TotalInclTax = Math.Round(itemTotal + taxTotal, 2),
                TaxRates = string.Join(", ", rates),
                Lines = lines
            };
        }).ToList();

        return new PurchaseTaxRegisterDto
        {
            From = from,
            To = to,
            Rows = rows
        };
    }
}
