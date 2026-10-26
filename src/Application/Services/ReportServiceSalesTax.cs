using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

public partial class ReportService
{
    /// <summary>
    /// توليد تقرير ضريبة المبيعات: يجمع فواتير المبيعات في [from, to] حسب نسبة
    /// الضريبة (شريحة ضريبية) ويعيد لكل شريحة عدد الفواتير وإجمالي المبيعات
    /// ومبلغ الضريبة، مع إجمالي مدين/دائن من القيود المحاسبية المرتبطة.
    ///
    /// الاستعلام مصمم للكفاءة:
    ///   - الفلترة (فترة + عدم الحذف + غير ملغاة) تُنفَّذ في قاعدة البيانات
    ///     عبر IQueryable قبل السحب إلى الذاكرة (ToListAsync).
    ///   - للتجميع حسب النسبة تُسحب الفواتير لفترة واحدة فقط مرة واحدة ثم تُجمَّع
    ///     في الذاكرة (عدد الفواتير في فترة واحدة صغير نسبياً، والتجميع بسيط).
    ///   - قيود البيع تُقرأ بجملة IN واحدة (Contains) بدل استعلام لكل فاتورة.
    /// </summary>
    public async Task<SalesTaxReportDto> GetSalesTaxReportAsync(DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toExclusive = to.Date.AddDays(1);

        // 1) فواتير الفترة مع بنودها (IQueryable → فلترة في DB) — نجمّع حسب نسبة كل سطر
        //    ليعكس التقرير الشرائح الضريبية الفعلية لكل صنف (ضريبة لكل سطر وليس نسبة واحدة على الرأس).
        var invoices = await _context.Set<SalesInvoice>()
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .Where(s => !s.IsDeleted
                        && s.Status != DocumentStatus.Cancelled
                        && s.InvoiceDate >= fromDate
                        && s.InvoiceDate < toExclusive)
            .OrderBy(s => s.InvoiceDate)
            .ThenBy(s => s.InvoiceNumber)
            .ToListAsync();

        // 2) تجميع بنود الفواتير حسب نسبة الضريبة لكل سطر
        //    - الأساس الخاضع للضريبة لكل سطر = سطر السطر بعد توزيع الخصم تناسبياً (نفس منطق الإنشاء)
        //    - فاتورة بنسب مختلفة تظهر في أكثر من شريحة (تفصيل صحيح للمحاسب/المدقق)
        var rows = invoices
            .SelectMany(x => x.Lines.Select(l => new { Invoice = x, Line = l }))
            .GroupBy(p => Math.Round(p.Line.TaxRate, 2))
            .Select(g => new SalesTaxReportRow
            {
                TaxRate = g.Key,
                TaxDescription = SalesTaxTierCatalog.Describe(g.Key),
                InvoiceCount = g.Select(p => p.Invoice.Id).Distinct().Count(),
                GrossAmount = Math.Round(g.Sum(p =>
                {
                    var inv = p.Invoice;
                    if (inv.SubTotal <= 0m) return 0m;
                    return p.Line.LineTotal - (p.Line.LineTotal * inv.DiscountAmount / inv.SubTotal);
                }), 2),
                TaxAmount = Math.Round(g.Sum(p => p.Line.TaxAmount), 2)
            })
            .OrderByDescending(r => r.TaxRate)
            .ToList();

        // 3) إجمالي المدين والدائن من القيود المحاسبية المرتبطة بفواتير البيع (صندوق التحقق في التذييل)
        var entryIds = invoices
            .Select(s => s.SalesJournalEntryId)
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

        return new SalesTaxReportDto
        {
            From = from,
            To = to,
            Rows = rows,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit
        };
    }

    /// <summary>
    /// سجل ضريبة المبيعات التفصيلي — صف واحد لكل فاتورة مبيعات مع تفاصيل أسطرها.
    /// المصدر: SalesInvoiceLine.TaxRate / TaxAmount (لقطة تاريخية)، لا رأس الفاتورة.
    /// - الخصم يُوزَّع تناسبياً على السطور (نفس منطق الإنشاء).
    /// - يعمل مع الفواتير متعددة النسب (نسب الفاتورة تُجمَّع في عمود «النسب»).
    /// - لا يعيد احتساب الفواتير القديمة ولا يغيّر بياناتها.
    /// </summary>
    public async Task<SalesTaxRegisterDto> GetSalesTaxRegisterAsync(DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toExclusive = to.Date.AddDays(1);

        var invoices = await _context.Set<SalesInvoice>()
            .Include(x => x.Customer)
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
            var subTotal = inv.SubTotal;
            // تفاصيل السطور — كلها مبنية من السطر (لا من رأس الفاتورة)
            var lines = inv.Lines.Select(l =>
            {
                var gross = Math.Round(l.LineTotal, 2);
                // توزيع الخصم تناسبياً على السطر (نفس منطق الإنشاء)
                var discount = subTotal > 0m ? Math.Round(gross * inv.DiscountAmount / subTotal, 2) : 0m;
                var net = Math.Round(gross - discount, 2);
                return new SalesTaxRegisterLineDto
                {
                    ItemCode = l.Item?.Code ?? "—",
                    ItemNameAr = l.Item?.NameAr ?? "—",
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    Discount = discount,
                    Net = net,
                    TaxRate = l.TaxRate,
                    TaxAmount = l.TaxAmount,
                    Total = Math.Round(net + l.TaxAmount, 2)
                };
            }).ToList();

            var itemTotal = Math.Round(lines.Sum(x => x.Net + x.Discount), 2);
            var discountTotal = Math.Round(lines.Sum(x => x.Discount), 2);
            var netTaxable = Math.Round(lines.Sum(x => x.Net), 2);
            var taxTotal = Math.Round(lines.Sum(x => x.TaxAmount), 2);
            var totalIncl = Math.Round(lines.Sum(x => x.Total), 2);
            var rates = lines.Select(x => x.TaxRate)
                             .Distinct()
                             .OrderBy(r => r)
                             .Select(r => r.ToString("0.#") + "%")
                             .ToList();

            return new SalesTaxRegisterRow
            {
                InvoiceId = inv.Id,
                InvoiceDate = inv.InvoiceDate,
                InvoiceNumber = inv.InvoiceNumber,
                CustomerName = inv.Customer?.NameAr ?? "زبون نقدي",
                ItemTotal = itemTotal,
                DiscountTotal = discountTotal,
                NetTaxable = netTaxable,
                TaxTotal = taxTotal,
                TotalInclTax = totalIncl,
                TaxRates = string.Join(", ", rates),
                Lines = lines
            };
        }).ToList();

        return new SalesTaxRegisterDto
        {
            From = from,
            To = to,
            Rows = rows
        };
    }
}

/// <summary>
/// كتالوج شرائح الضريبة: يصف نسبة الضريبة باسم عربي موحد (وصف الضريبة المستحق).
/// النسبة 0% تُعرض كـ "معفاة / نسبة صفرية". النسب غير المعروفة تُولّد وصفاً ديناميكياً.
/// </summary>
public static class SalesTaxTierCatalog
{
    private static readonly (decimal Rate, string Description)[] Tiers =
    {
        (16m, "ضريبة مبيعات 16% مستحقة"),
        (10m, "ضريبة مبيعات 10% مستحقة"),
        (5m,  "ضريبة مبيعات 5% مستحقة"),
        (4m,  "ضريبة مبيعات 4% مستحقة"),
        (2m,  "ضريبة مبيعات 2% مستحقة"),
        (0m,  "معفاة من الضريبة / نسبة صفرية"),
    };

    /// <summary>يعيد الوصف الموحد لشريحة الضريبة (بالعربية — يُترجم في الواجهة عند الحاجة).</summary>
    public static string Describe(decimal taxRate)
    {
        foreach (var tier in Tiers)
            if (Math.Abs(tier.Rate - taxRate) < 0.005m)
                return tier.Description;

        return $"ضريبة مبيعات {taxRate:0.#}%";
    }
}