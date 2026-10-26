using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// إصلاحات R1/R2/R3/R5/R6 (جلسة 2026-10-21) — اختبارات انحدار تستهدف العيوب الأصلية:
///  • D-05-03: مستند «مرحّل» بلا قيد ينتج من كيانات معلّقة تُحفظ لاحقاً في سياق طويل العمر.
///  • D-03-02: فقدان تحديثات متزامنة في مردود المبيعات (بلا معاملة ⇒ القفل غير فعّال).
/// تعمل على قاعدة اختبار مؤقتة تُنشأ وتُحذف ذاتياً (AccountingPgFixture) — لا تلمس أي قاعدة قائمة.
/// </summary>
public class DocumentIntegrityRepairTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public DocumentIntegrityRepairTests(AccountingPgFixture pg) => _pg = pg;

    private static SalesInvoiceService NewSales(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    private static PurchaseInvoiceService NewPurchase(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    private static SalesReturnService NewReturn(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx));

    /// <summary>قراءة آخر قيمة عدّاد الترقيم للبادئة (لمقارنة استهلاك الأرقام).</summary>
    private static async Task<long> SequenceValueAsync(AppDbContext ctx, string prefix)
    {
        var key = $"{prefix}-{DateTime.Now:yyyyMMdd}";
        var conn = ctx.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"LastValue\" FROM \"NumberSequences\" WHERE \"SequenceKey\" = @k";
            var p = cmd.CreateParameter(); p.ParameterName = "k"; p.Value = key; cmd.Parameters.Add(p);
            var r = await cmd.ExecuteScalarAsync();
            return r is null || r is DBNull ? 0 : Convert.ToInt64(r);
        }
        finally { if (wasClosed && conn.State == System.Data.ConnectionState.Open) await conn.CloseAsync(); }
    }

    private static async Task<decimal> StockAsync(AppDbContext ctx, Guid itemId)
        => await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => i.CurrentStock).FirstAsync();

    private static async Task<decimal> MovementSumAsync(AppDbContext ctx, Guid itemId)
        => await ctx.Set<StockMovement>().AsNoTracking().Where(m => m.ItemId == itemId && !m.IsDeleted)
            .SumAsync(m => (decimal?)m.Quantity) ?? 0m;

    private static async Task<int> OrphanSalesInvoicesAsync(AppDbContext ctx)
        => await ctx.Set<SalesInvoice>().AsNoTracking().CountAsync(i => !i.IsDeleted && i.SalesJournalEntryId == null);

    private static async Task<int> OrphanPurchaseInvoicesAsync(AppDbContext ctx)
        => await ctx.Set<PurchaseInvoice>().AsNoTracking().CountAsync(i => !i.IsDeleted && i.JournalEntryId == null);

    /// <summary>لكل حساب: الرصيد المخزَّن == الإشارة × مجموع سطوره (المعادلة المعتمدة).</summary>
    private static async Task AssertAccountBalancesConsistentAsync(AppDbContext ctx)
    {
        var accounts = await ctx.Set<Account>().AsNoTracking()
            .Select(a => new { a.Code, a.NormalBalance, a.CurrentBalance }).ToListAsync();
        var nets = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted)
            .Join(ctx.Set<Account>().AsNoTracking(), l => l.AccountId, a => a.Id, (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
            .GroupBy(x => x.Code)
            .Select(g => new { Code = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToDictionaryAsync(x => x.Code, x => x.Net);

        var mismatched = accounts
            .Select(a => new
            {
                a.Code,
                Stored = a.CurrentBalance,
                Expected = a.NormalBalance == NormalBalance.Debit ? nets.GetValueOrDefault(a.Code, 0m) : -nets.GetValueOrDefault(a.Code, 0m)
            })
            .Where(x => Math.Abs(x.Stored - x.Expected) > 0.004m)
            .ToList();

        Assert.True(mismatched.Count == 0,
            "أرصدة حسابات غير متسقة: " + string.Join(" ; ", mismatched.Select(m => $"{m.Code} stored={m.Stored} expected={m.Expected}")));
    }

    // ─────────────────────────── R1 / D-05-03: منع المستند الشبح ───────────────────────────

    [LivePgFact]
    public async Task Rejected_sale_does_not_survive_a_later_save_on_the_same_context()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("R1-GHOST-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 50m, taxRate: 16m);

        long seqBefore;
        await using (var probe = _pg.NewContext()) seqBefore = await SequenceValueAsync(probe, "SI");

        await using var ctx = _pg.NewContext();
        var svc = NewSales(ctx);

        // (1) محاولة بيع مرفوضة (كمية أكبر من الرصيد) على السياق نفسه
        await Assert.ThrowsAnyAsync<Exception>(() => svc.CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
            Lines = new() { new() { ItemId = itemId, Quantity = 9_999m, UnitPrice = 15m } }
        }));

        // (2) عملية صحيحة لاحقة على **نفس** السياق — سابقاً كانت تحفظ الكيانات المعلّقة معها (مستند شبح)
        var ok = await svc.CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
            Lines = new() { new() { ItemId = itemId, Quantity = 1m, UnitPrice = 15m } }
        });
        Assert.StartsWith("SI-", ok.InvoiceNumber);

        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.Set<SalesInvoice>().AsNoTracking().CountAsync(i => !i.IsDeleted && i.Id == ok.Id));
        Assert.Equal(0, await OrphanSalesInvoicesAsync(check));                       // لا فاتورة بلا قيد
        Assert.Equal(49m, await StockAsync(check, itemId));                           // المخزون تأثر بالعملية الصحيحة فقط
        Assert.Equal(await StockAsync(check, itemId), await MovementSumAsync(check, itemId));
        Assert.Equal(seqBefore + 1, await SequenceValueAsync(check, "SI"));           // الرقم المرفوض لم يُستهلك
        await AssertAccountBalancesConsistentAsync(check);
    }

    // ─────────────────────────── R2 / D-05-03: ذرّية دورة المشتريات ───────────────────────────

    [LivePgFact]
    public async Task Purchase_failure_is_atomic_leaves_no_ghost_and_next_document_is_clean()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // صنف يتتبّع الدُفعات مع رصيد افتتاحي كحركة كمّية (بلا دُفعة) ⇒ يفشل فحص تكامل الدُفعات عند الاستلام
        var batchItemId = await _pg.CreateItemAsync("R2-BATCH-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 5m, taxRate: 16m, tracksBatches: true);
        // صنف عادي لإثبات أن السياق نظيف بعد الفشل
        var plainItemId = await _pg.CreateItemAsync("R2-PLAIN-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 0m, taxRate: 16m);

        await using var ctx = _pg.NewContext();
        var svc = NewPurchase(ctx);

        var seqBefore = await SequenceValueAsync(ctx, "PI");
        var stockBefore = await StockAsync(ctx, batchItemId);
        var movementsBefore = await ctx.Set<StockMovement>().AsNoTracking().CountAsync(m => m.ItemId == batchItemId && !m.IsDeleted);
        var supplierBefore = await ctx.Set<Supplier>().AsNoTracking().Where(s => s.Id == _pg.SupplierId).Select(s => s.CurrentBalance).FirstAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => svc.CreateAsync(new CreatePurchaseInvoiceDto
        {
            SupplierId = _pg.SupplierId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
            InvoiceType = (int)PurchaseInvoiceType.Cash,
            Lines = new()
            {
                new() { ItemId = batchItemId, Quantity = 3m, UnitCost = 10m,
                        ExpiryDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(6)), BatchNumber = "R2-B1" }
            }
        }));

        await using var check = _pg.NewContext();
        Assert.Equal(0, await OrphanPurchaseInvoicesAsync(check));                    // لا فاتورة بلا قيد
        Assert.Equal(stockBefore, await StockAsync(check, batchItemId));              // لا زيادة مخزون
        Assert.Equal(movementsBefore, await check.Set<StockMovement>().AsNoTracking().CountAsync(m => m.ItemId == batchItemId && !m.IsDeleted));
        Assert.Equal(supplierBefore, await check.Set<Supplier>().AsNoTracking().Where(s => s.Id == _pg.SupplierId).Select(s => s.CurrentBalance).FirstAsync());
        Assert.Equal(seqBefore, await SequenceValueAsync(check, "PI"));               // لا استهلاك رقم

        // عملية صحيحة لاحقة على نفس السياق ⇒ تُحفظ هي فقط
        var ok = await svc.CreateAsync(new CreatePurchaseInvoiceDto
        {
            SupplierId = _pg.SupplierId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
            InvoiceType = (int)PurchaseInvoiceType.Cash,
            Lines = new() { new() { ItemId = plainItemId, Quantity = 4m, UnitCost = 10m } }
        });
        Assert.StartsWith("PI-", ok.InvoiceNumber);

        await using var after = _pg.NewContext();
        Assert.Equal(1, await after.Set<PurchaseInvoice>().AsNoTracking().CountAsync(i => !i.IsDeleted && i.Id == ok.Id));
        Assert.Equal(0, await OrphanPurchaseInvoicesAsync(after));
        Assert.Equal(4m, await StockAsync(after, plainItemId));
        Assert.Equal(await StockAsync(after, plainItemId), await MovementSumAsync(after, plainItemId));
        await AssertAccountBalancesConsistentAsync(after);
    }

    // ─────────────────── R3 / D-03-02: تزامن مردود المبيعات (20 تكراراً + ضابط) ───────────────────

    [LivePgFact]
    public async Task Concurrent_sales_returns_never_lose_updates_across_20_iterations()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int iterations = 20;

        var itemId = await _pg.CreateItemAsync("R3-CONC-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 200m, taxRate: 16m);

        // فاتورة بيع مرحّلة تكفي لمردودي كل تكرار + مردودَي الضابط المتتالي
        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var inv = await NewSales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemId, Quantity = iterations * 2m + 2m, UnitPrice = 15m } }
            });
            invoiceId = inv.Id;
        }

        for (var i = 1; i <= iterations; i++)
        {
            // حاجز تزامن: الطلبان يبدآن في اللحظة نفسها على سياقين مستقلين (يحاكي دائرتين)
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 2).Select(async _ =>
            {
                await start.Task;
                await using var c = _pg.NewContext();
                await NewReturn(c).CreateAsync(new CreateSalesReturnDto
                {
                    SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                    Lines = new() { new() { ItemId = itemId, Quantity = 1m, UnitPrice = 15m } }
                });
            }).ToList();

            start.SetResult();
            await Task.WhenAll(tasks);

            await using var check = _pg.NewContext();

            // القاعدة هي مرجع الحقيقة — لا فقدان تحديث في الكمية ولا في الأرصدة
            Assert.Equal(await StockAsync(check, itemId), await MovementSumAsync(check, itemId));

            var returns = await check.Set<SalesReturn>().AsNoTracking()
                .CountAsync(r => !r.IsDeleted && r.SalesInvoiceId == invoiceId);
            Assert.Equal(i * 2, returns);

            var returnEntryIds = await check.Set<SalesReturn>().AsNoTracking()
                .Where(r => !r.IsDeleted && r.SalesInvoiceId == invoiceId)
                .Select(r => new { r.SalesJournalEntryId, r.CogsJournalEntryId })
                .ToListAsync();
            var entryIds = returnEntryIds
                .SelectMany(r => new[] { r.SalesJournalEntryId, r.CogsJournalEntryId })
                .Where(id => id.HasValue).Select(id => id!.Value).ToList();
            Assert.Equal(i * 4, entryIds.Count);                       // قيدان لكل مردود
            Assert.Equal(entryIds.Count, entryIds.Distinct().Count()); // لا تكرار للقيود

            var duplicates = await check.Set<JournalEntry>().AsNoTracking()
                .Where(e => !e.IsDeleted)
                .GroupBy(e => e.EntryNumber).Where(g => g.Count() > 1).CountAsync();
            Assert.Equal(0, duplicates);                               // لا أرقام قيود مكررة

            await AssertAccountBalancesConsistentAsync(check);
        }

        // الضابط: مردودان متتاليان (بلا حاجز) يجب أن ينجحا دائماً
        for (var k = 0; k < 2; k++)
        {
            await using var c = _pg.NewContext();
            await NewReturn(c).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new() { new() { ItemId = itemId, Quantity = 1m, UnitPrice = 15m } }
            });
        }

        await using var final = _pg.NewContext();
        Assert.Equal(await StockAsync(final, itemId), await MovementSumAsync(final, itemId));
        Assert.Equal(iterations * 2 + 2,
            await final.Set<SalesReturn>().AsNoTracking().CountAsync(r => !r.IsDeleted && r.SalesInvoiceId == invoiceId));
        await AssertAccountBalancesConsistentAsync(final);
    }
}


