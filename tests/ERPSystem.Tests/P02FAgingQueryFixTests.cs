using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P0-2F — إثبات عيب شروط استعلام أعمار ذمم العملاء (قبل الإصلاح) وإثبات الإصلاح (بعده).
/// البيانات تُنشأ عبر الخدمات الرسمية؛ لا تعديل قيود أو أرصدة.
/// </summary>
public class P02FAgingQueryFixTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P02FAgingQueryFixTests(AccountingPgFixture pg) => _pg = pg;

    private async Task<(Guid CustomerId, Guid InvoiceId)> SeedCreditInvoiceAsync()
    {
        await _pg.SeedCashAsync();
        Guid customerId;
        await using (var ctx = _pg.NewContext())
        {
            var c = new Customer
            {
                Id = Guid.NewGuid(), Code = "P02F-" + Guid.NewGuid().ToString("N")[..5],
                NameAr = "عميل شروط", IsActive = true, CreatedAt = DateTime.UtcNow
            };
            ctx.Add(c); await ctx.SaveChangesAsync();
            customerId = c.Id;
        }
        var itemId = await _pg.CreateItemAsync("P02F-" + Guid.NewGuid().ToString("N")[..5], 10m, 5m, 50m, taxRate: 0m);
        await using (var ctx = _pg.NewContext())
        {
            var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount, PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 10m } }
            });
            return (customerId, inv.Id);
        }
    }

    private async Task<decimal> AgingValueAsync(Guid customerId, DateTime asOf, bool receivable = true)
    {
        await using var ctx = _pg.NewContext();
        var aging = await AgingReportQueries.GetAgingAsync(ctx, isReceivable: receivable, asOf: asOf);
        return aging.Rows.Where(r => r.PartyId == customerId).Sum(r => r.Total + r.CreditBalance);
    }

    /// <summary>(1) تاريخ القطع: فاتورة اليوم تظهر عند asOf=اليوم (حد شامل) ولا تظهر عند asOf=أمس.</summary>
    [LivePgFact]
    public async Task AR_aging_respects_asOf_cutoff()
    {
        if (!_pg.Available) return;
        var (customerId, _) = await SeedCreditInvoiceAsync();

        Assert.Equal(100m, await AgingValueAsync(customerId, DateTime.Today));
        Assert.Equal(0m, await AgingValueAsync(customerId, DateTime.Today.AddDays(-1)));
    }

    /// <summary>(2) الفواتير غير المرحَّلة لا تدخل تقرير ذمم العملاء.</summary>
    [LivePgFact]
    public async Task AR_aging_excludes_non_posted_invoices()
    {
        if (!_pg.Available) return;
        var (customerId, invoiceId) = await SeedCreditInvoiceAsync();
        Assert.Equal(100m, await AgingValueAsync(customerId, DateTime.Today));

        // حالة اختبارية: تحويل حالة مستند الاختبار إلى «مسودة» (لا يمسّ قيودًا أو أرصدة) ثم إعادتها
        await using (var ctx = _pg.NewContext())
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"SalesInvoices\" SET \"Status\" = {(int)DocumentStatus.Draft} WHERE \"Id\" = {invoiceId}");
        var afterDraft = await AgingValueAsync(customerId, DateTime.Today);
        await using (var ctx = _pg.NewContext())
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"SalesInvoices\" SET \"Status\" = {(int)DocumentStatus.Posted} WHERE \"Id\" = {invoiceId}");

        Assert.Equal(0m, afterDraft);
    }

    /// <summary>(3) تشغيل التقارير لا يغيّر أي قيد أو رصيد في الأستاذ.</summary>
    [LivePgFact]
    public async Task Running_reports_does_not_change_ledger()
    {
        if (!_pg.Available) return;
        await SeedCreditInvoiceAsync();

        async Task<(int Count, decimal Debit, decimal Credit)> LedgerAsync()
        {
            await using var ctx = _pg.NewContext();
            var lines = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
                .Select(l => new { l.DebitAmount, l.CreditAmount }).ToListAsync();
            return (lines.Count, lines.Sum(l => l.DebitAmount), lines.Sum(l => l.CreditAmount));
        }

        var before = await LedgerAsync();
        await using (var ctx = _pg.NewContext())
        {
            await AgingReportQueries.GetAgingAsync(ctx, isReceivable: true, asOf: DateTime.Today);
            await AgingReportQueries.GetAgingAsync(ctx, isReceivable: false, asOf: DateTime.Today);
        }
        Assert.Equal(before, await LedgerAsync());
    }

    /// <summary>(4) فرع الموردين يحترم تاريخ القطع أصلًا (لم يتغير بعد الإصلاح).</summary>
    [LivePgFact]
    public async Task AP_aging_cutoff_still_works()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var supplierId = Guid.NewGuid();
        Guid itemId;
        await using (var ctx = _pg.NewContext())
        {
            ctx.Add(new Supplier
            {
                Id = supplierId, Code = "P02F-S-" + Guid.NewGuid().ToString("N")[..5],
                NameAr = "مورد شروط", IsActive = true, CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }
        itemId = await _pg.CreateItemAsync("P02F-AP-" + Guid.NewGuid().ToString("N")[..5], 10m, 5m, 50m, taxRate: 0m);
        await using (var ctx = _pg.NewContext())
        {
            await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new ERPSystem.Application.DTOs.Purchases.CreatePurchaseInvoiceDto
                {
                    SupplierId = supplierId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                    InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                    PaymentMethod = ERPSystem.Application.DTOs.Purchases.CreatePurchaseInvoiceDto.PaymentOnAccount,
                    Lines = new List<ERPSystem.Application.DTOs.Purchases.CreatePurchaseInvoiceLineDto>
                    {
                        new() { ItemId = itemId, Quantity = 10m, UnitCost = 10m }
                    }
                });
        }

        Assert.True(await AgingValueAsync(supplierId, DateTime.Today, receivable: false) >= 100m);
        Assert.Equal(0m, await AgingValueAsync(supplierId, DateTime.Today.AddDays(-1), receivable: false));
    }
}
