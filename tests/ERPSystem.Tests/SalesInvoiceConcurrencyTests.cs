using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة 2-أ من بوابة الإصدار: تزامن **فواتير المبيعات** (لا المرتجعات).
/// سياقان مستقلان + حاجز تزامن + 20 دورة، مع التحقق من الترقيم والقيود وحركات المخزون والأرصدة
/// وعدم بقاء أي أثر جزئي عند فشل أحد الطرفين. تعمل على قاعدة اختبار مؤقتة تُنشأ وتُحذف ذاتيًا.
/// </summary>
public class SalesInvoiceConcurrencyTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public SalesInvoiceConcurrencyTests(AccountingPgFixture pg) => _pg = pg;

    private static SalesInvoiceService NewSales(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    private static async Task<decimal> StockAsync(AppDbContext ctx, Guid itemId)
        => await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => i.CurrentStock).FirstAsync();

    private static async Task<decimal> MovementSumAsync(AppDbContext ctx, Guid itemId)
        => await ctx.Set<StockMovement>().AsNoTracking().Where(m => m.ItemId == itemId && !m.IsDeleted)
            .SumAsync(m => (decimal?)m.Quantity) ?? 0m;

    private static async Task AssertBooksConsistentAsync(AppDbContext ctx, Guid itemId)
    {
        // 1) الذاكرة = مجموع الحركات (المصدر المحاسبي للكمية)
        Assert.Equal(await StockAsync(ctx, itemId), await MovementSumAsync(ctx, itemId));

        // 2) لا فاتورة مرحّلة بلا قيد
        var orphan = await ctx.Set<SalesInvoice>().AsNoTracking()
            .CountAsync(i => !i.IsDeleted && i.Lines.Any(l => l.ItemId == itemId) && i.SalesJournalEntryId == null);
        Assert.Equal(0, orphan);

        // 3) لا أرقام فواتير ولا قيود مكرّرة
        Assert.Equal(0, await ctx.Set<SalesInvoice>().AsNoTracking().Where(i => !i.IsDeleted)
            .GroupBy(i => i.InvoiceNumber).CountAsync(g => g.Count() > 1));
        Assert.Equal(0, await ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted)
            .GroupBy(e => e.EntryNumber).CountAsync(g => g.Count() > 1));

        // 4) كل قيد متوازن، وكل رصيد مخزَّن = الإشارة × مجموع سطوره
        var rows = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
            .GroupBy(l => l.JournalEntryId)
            .Select(g => new { Debit = g.Sum(x => x.DebitAmount), Credit = g.Sum(x => x.CreditAmount) })
            .ToListAsync();
        Assert.All(rows, r => Assert.True(Math.Abs(r.Debit - r.Credit) < 0.005m, "قيد غير متوازن"));

        var accounts = await ctx.Set<Account>().AsNoTracking().Select(a => new { a.Code, a.NormalBalance, a.CurrentBalance }).ToListAsync();
        var nets = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
            .Join(ctx.Set<Account>().AsNoTracking(), l => l.AccountId, a => a.Id, (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
            .GroupBy(x => x.Code).Select(g => new { Code = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToDictionaryAsync(x => x.Code, x => x.Net);
        var bad = accounts.Select(a => new
        {
            a.Code,
            a.CurrentBalance,
            Expected = a.NormalBalance == NormalBalance.Debit ? nets.GetValueOrDefault(a.Code, 0m) : -nets.GetValueOrDefault(a.Code, 0m)
        }).Where(x => Math.Abs(x.CurrentBalance - x.Expected) > 0.005m).ToList();
        Assert.True(bad.Count == 0, "أرصدة غير متسقة: " + string.Join(" ; ", bad.Select(b => b.Code)));
    }

    private CreateSalesInvoiceDto NewCashSale(Guid itemId, decimal qty) => new()
    {
        CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
        InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
        Lines = new() { new() { ItemId = itemId, Quantity = qty, UnitPrice = 15m } }
    };

    // ── 1) موارد كافية: العمليتان تنجحان، بلا تكرار ترقيم وبلا فقدان كمية/رصيد ──
    [LivePgFact]
    public async Task Concurrent_sales_with_sufficient_stock_both_commit_and_keep_books_consistent()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int iterations = 20;
        var itemId = await _pg.CreateItemAsync("GATE-SI-OK-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 1000m, taxRate: 16m);

        for (var i = 1; i <= iterations; i++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 2).Select(async _ =>
            {
                await start.Task;                        // حاجز: بدء متزامن حقيقي
                await using var ctx = _pg.NewContext();  // سياق/اتصال مستقل
                return await NewSales(ctx).CreateAsync(NewCashSale(itemId, 5m));
            }).ToList();

            start.SetResult();
            var created = await Task.WhenAll(tasks);

            await using var check = _pg.NewContext();
            Assert.Equal(2, created.Select(c => c.InvoiceNumber).Distinct().Count());   // ترقيم فريد بين العمليتين
            Assert.Equal(2, await check.Set<SalesInvoice>().AsNoTracking()
                .CountAsync(inv => created.Select(c => c.Id).Contains(inv.Id)));
            Assert.Equal(1000m - (5m * 2 * i), await StockAsync(check, itemId));        // لا فقدان كمية
            await AssertBooksConsistentAsync(check, itemId);
        }

        await using var final = _pg.NewContext();
        Assert.Equal(iterations * 2, await final.Set<SalesInvoice>().AsNoTracking()
            .CountAsync(inv => !inv.IsDeleted && inv.Lines.Any(l => l.ItemId == itemId)));
    }

    // ── 2) موارد متنافسة: واحد فقط ينجح، والآخر يُرفض بلا أي أثر جزئي ──
    [LivePgFact]
    public async Task Concurrent_sales_of_scarce_stock_commit_exactly_one_without_partial_effects()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int iterations = 20;

        for (var i = 1; i <= iterations; i++)
        {
            // صنف برصيد 5 لكل دورة ⇒ عمليتان (5 لكل واحدة) لا يمكن أن تنجحا معًا
            var itemId = await _pg.CreateItemAsync("GATE-SI-RACE-" + i + "-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 5m, taxRate: 16m);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 2).Select(async _ =>
            {
                await start.Task;
                try
                {
                    await using var ctx = _pg.NewContext();
                    var dto = await NewSales(ctx).CreateAsync(NewCashSale(itemId, 5m));
                    return (Ok: true, Number: dto.InvoiceNumber, Error: (string?)null);
                }
                catch (Exception ex)
                {
                    return (Ok: false, Number: (string)null!, Error: ex.GetType().Name);
                }
            }).ToList();

            start.SetResult();
            var results = await Task.WhenAll(tasks);

            await using var check = _pg.NewContext();
            Assert.Equal(1, results.Count(r => r.Ok));                                      // فائز واحد بالضبط
            Assert.Equal("InsufficientStockException", results.Single(r => !r.Ok).Error);   // والخاسر مرفوض بسبب واضح

            var invoices = await check.Set<SalesInvoice>().AsNoTracking()
                .Where(inv => !inv.IsDeleted && inv.Lines.Any(l => l.ItemId == itemId)).ToListAsync();
            Assert.Single(invoices);                                                        // لا فاتورة جزئية للخاسر
            Assert.NotNull(invoices[0].SalesJournalEntryId);
            Assert.Equal(0m, await StockAsync(check, itemId));                              // الرصيد صفر
            Assert.Equal(-5m, await check.Set<StockMovement>().AsNoTracking()               // حركة البيع وحدها = 5
                .Where(m => m.ItemId == itemId && !m.IsDeleted && m.MovementType == MovementType.SalesIssue)
                .SumAsync(m => (decimal?)m.Quantity) ?? 0m);
            Assert.Equal(1, await check.Set<StockMovement>().AsNoTracking()                 // حركة صادر واحدة (لا أثر للخاسر)
                .CountAsync(m => m.ItemId == itemId && !m.IsDeleted && m.MovementType == MovementType.SalesIssue));

            await AssertBooksConsistentAsync(check, itemId);
        }
    }
}
