using System.Data.Common;
using ERPSystem.Application.DTOs.Expenses;
using ERPSystem.Application.DTOs.Inventory;
using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace ERPSystem.Tests;

/// <summary>
/// Phase A/2 + A/4 — سلامة الدورة المحاسبية على PostgreSQL حقيقي معزول:
/// دورة مستند كاملة (بيع نقدي/بطاقة، شراء آجل/نقدي، مردود بيع/شراء، مصروف، إتلاف)،
/// واتساق كل رصيد مخزَّن مع دفتر القيود، وعدم وجود قيود جزئية/مكررة، وسلامة الترقيم عند التزامن،
/// وسلوك الفشل/التراجع، وسلامة إعادة المحاولة (Retry) كما هي مفعّلة في الإنتاج.
/// </summary>
public class AccountingCycleIntegrityTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;
    private readonly ITestOutputHelper _out;

    public AccountingCycleIntegrityTests(AccountingPgFixture pg, ITestOutputHelper output)
    {
        _pg = pg;
        _out = output;
    }

    // ───────── أدوات ─────────

    /// <summary>انحراف ذاكرة المخزون (Item.CurrentStock) عن مجموع حركات المخزون (لأصناف محددة أو للكل).</summary>
    private static async Task<List<string>> StockCacheDriftAsync(AccountingPgFixture pg, params Guid[] itemIds)
    {
        await using var ctx = pg.NewContext();
        var query = ctx.Set<Item>().AsNoTracking().Where(i => !i.IsDeleted);
        if (itemIds.Length > 0) query = query.Where(i => itemIds.Contains(i.Id));
        var items = await query.ToListAsync();

        var sums = await ctx.Set<StockMovement>().AsNoTracking()
            .Where(m => !m.IsDeleted)
            .GroupBy(m => m.ItemId)
            .Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToListAsync();
        var map = sums.ToDictionary(x => x.ItemId, x => x.Qty);

        var drift = new List<string>();
        foreach (var item in items)
        {
            var qty = map.TryGetValue(item.Id, out var q) ? q : 0m;
            if (Math.Round(item.CurrentStock, 2) != Math.Round(qty, 2))
                drift.Add($"{item.Code}: cache={item.CurrentStock} movements={qty}");
        }
        return drift;
    }

    /// <summary>فواتير البيع التي لا يساوي فيها قيد COGS مجموع (كمية × لقطة التكلفة).</summary>
    private static async Task<List<string>> CogsMismatchAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        var rows = await ctx.Set<SalesInvoice>().AsNoTracking()
            .Where(i => !i.IsDeleted && i.CogsJournalEntryId != null)
            .Select(i => new
            {
                i.InvoiceNumber,
                Expected = i.Lines.Sum(l => l.Quantity * l.UnitCost),
                Posted = ctx.Set<JournalEntryLine>()
                    .Where(l => l.JournalEntryId == i.CogsJournalEntryId && !l.IsDeleted)
                    .Sum(l => l.DebitAmount)
            })
            .ToListAsync();

        return rows
            .Where(r => Math.Round(r.Expected, 2) != Math.Round(r.Posted, 2))
            .Select(r => $"{r.InvoiceNumber}: expected={r.Expected:N2} posted={r.Posted:N2}")
            .ToList();
    }

    private static async Task<long> SequenceValueAsync(AccountingPgFixture pg, string prefix)
    {
        await using var ctx = pg.NewContext();
        var key = $"{prefix}-{DateTime.Now:yyyyMMdd}";
        return (await ctx.Set<NumberSequence>().AsNoTracking()
            .FirstOrDefaultAsync(n => n.SequenceKey == key))?.LastValue ?? 0L;
    }

    /// <summary>يعترض أول أمر مطابق: يرمي خطأ عابراً مرة واحدة ثم يسمح بالمرور (لمحاكاة إعادة المحاولة).</summary>
    private sealed class TransientFailureOnceInterceptor : DbCommandInterceptor
    {
        private readonly string _match;
        private int _attempts;
        private int _thrown;

        public TransientFailureOnceInterceptor(string match) => _match = match;

        /// <summary>عدد الأوامر المطابقة التي نُفّذت (محاولات الحفظ).</summary>
        public int Attempts => _attempts;

        /// <summary>عدد الأخطاء العابرة التي أُلقيت فعلاً.</summary>
        public int Thrown => _thrown;

        private void MaybeFail(DbCommand command)
        {
            if (!command.CommandText.Contains(_match, StringComparison.Ordinal)) return;
            if (Interlocked.Increment(ref _attempts) > 1) return;
            Interlocked.Increment(ref _thrown);
            throw new PostgresException("simulated transient failure", "ERROR", "ERROR",
                PostgresErrorCodes.SerializationFailure);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            MaybeFail(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return new ValueTask<InterceptionResult<int>>(result);
        }

        // Npgsql ينفّذ INSERT مع RETURNING عبر ExecuteReader، لذا نعترض القراءة أيضاً.
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MaybeFail(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }

    // ───────── A/4: دورة مستند كاملة بقيم معروفة النتائج ─────────

    [LivePgFact]
    public async Task Full_document_cycle_is_balanced_consistent_with_correct_cogs_and_stock()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var itemA = await _pg.CreateItemAsync("A5-CYC-A-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 100m, taxRate: 16m);
        var itemB = await _pg.CreateItemAsync("A5-CYC-B-" + Guid.NewGuid().ToString("N")[..5], 5.00m, 2.00m, 100m, taxRate: 0m);
        var expenseCategory = await _pg.CreateExpenseCategoryAsync("A5-EXP", "5200");

        // (أ) بيع نقدي: A × 2 بسعر 2.38 (ضريبة 16%)
        SalesInvoiceDto cashSale;
        await using (var ctx = _pg.NewContext())
        {
            cashSale = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(itemA, 2m, 2.38m));
        }
        Assert.Equal(4.76m, cashSale.SubTotal);
        Assert.Equal(0.76m, cashSale.TaxAmount);
        Assert.Equal(5.52m, cashSale.TotalAmount);

        // (ب) بيع بالبطاقة: B × 1 بسعر 5.00 (ضريبة 0%) → تُدين ذمم البطاقات 1205
        SalesInvoiceDto cardSale;
        await using (var ctx = _pg.NewContext())
        {
            cardSale = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCardSale(itemB, 1m, 5.00m));
        }
        Assert.Equal(5.00m, cardSale.TotalAmount);

        // (ج) شراء آجل: A × 10 بتكلفة 1.80 → مدين المخزون 1300 / دائن المورد 2200
        PurchaseInvoiceDto creditPurchase;
        await using (var ctx = _pg.NewContext())
        {
            var svc = new PurchaseInvoiceService(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());
            creditPurchase = await svc.CreateAsync(_pg.NewPurchase(itemA, 10m, 1.80m));
        }
        Assert.Equal(20.88m, creditPurchase.TotalAmount);   // 18.00 صافي + ضريبة الصنف 16% (2.88)
        // (د) شراء نقدي: B × 5 بتكلفة 2.20 → مدين المخزون / دائن الصندوق 1100
        await using (var ctx = _pg.NewContext())
        {
            var svc = new PurchaseInvoiceService(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());
            await _pg.SeedCashAsync();   // الشراء النقدي يحتاج رصيدًا نقديًا (حارس D-03)
            await svc.CreateAsync(_pg.NewPurchase(itemB, 5m, 2.20m, cash: true));
        }

        // (هـ) مصروف نقدي 30.00 على الحساب 5200
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ExpenseService(ctx, new JournalEntryService(ctx));
            var expense = await svc.CreateAsync(new CreateExpenseEntryDto
            {
                ExpenseCategoryId = expenseCategory,
                ExpenseDate = DateTime.Today,
                Amount = 30m,
                PaymentMethod = 1,
                Description = "مصروف دورة A5"
            });
            Assert.Equal(30m, expense.Amount);
        }

        // (و) سند إتلاف: A × 3 بتكلفة 1.75 → مدين 5300 / دائن 1300
        await using (var ctx = _pg.NewContext())
        {
            var svc = new StockWriteOffService(ctx, new JournalEntryService(ctx));
            var writeOff = await svc.CreateAsync(new CreateStockWriteOffDto
            {
                ItemId = itemA,
                WarehouseId = _pg.WarehouseId,
                Quantity = 3m,
                Reason = (int)StockWriteOffReason.Damaged,
                Date = DateTime.Today
            }, "tester");
            Assert.Equal(5.26m, writeOff.TotalCost);   // 3 × متوسط التكلفة المرجّح بعد الشراء (1.7545)
        }

        // (ز) مردود مبيعات على الفاتورة النقدية: 1 × 2.38
        await using (var ctx = _pg.NewContext())
        {
            var svc = new SalesReturnService(ctx, new JournalEntryService(ctx));
            var salesReturn = await svc.CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = cashSale.Id,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = itemA, Quantity = 1m, UnitPrice = 2.38m } }
            });
            Assert.True(salesReturn.TotalAmount > 0m);
        }

        // (ح) مردود مشتريات على الفاتورة الآجلة: 2 × 1.80
        await using (var ctx = _pg.NewContext())
        {
            var svc = new PurchaseReturnService(ctx, new JournalEntryService(ctx));
            var purchaseReturn = await svc.CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = creditPurchase.Id,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                Lines = new List<CreatePurchaseReturnLineDto> { new() { ItemId = itemA, Quantity = 2m, UnitCost = 1.80m } }
            });
            Assert.True(purchaseReturn.TotalAmount > 0m);
        }

        // ── التحقق المحاسبي الشامل ──
        var report = await LedgerInvariants.AssertConsistentAsync(_pg);
        _out.WriteLine(report);

        Assert.Equal(0, await LedgerInvariants.ParentPostingsAsync(_pg));
        Assert.Equal(0, await LedgerInvariants.AbsurdDateRowsAsync(_pg));
        Assert.Empty(await LedgerInvariants.DuplicateNumbersAsync(_pg));
        Assert.Empty(await StockCacheDriftAsync(_pg, itemA, itemB));
        Assert.Empty(await CogsMismatchAsync(_pg));

    }


    // ───────── A/2 + A/4: الفشل/التراجع، التزامن، وسلامة إعادة المحاولة ─────────

    [LivePgFact]
    public async Task Failed_sale_rolls_back_atomically_without_partial_rows()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("A5-RB-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 5m);

        var jeBefore = await SequenceValueAsync(_pg, "JE");
        long invoicesBefore, linesBefore, movementsBefore, entriesBefore;
        await using (var ctx = _pg.NewContext())
        {
            invoicesBefore = await ctx.Set<SalesInvoice>().CountAsync();
            linesBefore = await ctx.Set<SalesInvoiceLine>().CountAsync();
            movementsBefore = await ctx.Set<StockMovement>().CountAsync();
            entriesBefore = await ctx.Set<JournalEntry>().CountAsync();
        }

        // طلب أكبر من الرصيد المتاح (5) → يجب الفشل والتراجع بلا أي أثر
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(itemId, 50m, 2.38m)));
        }

        await using (var ctx = _pg.NewContext())
        {
            Assert.Equal(invoicesBefore, await ctx.Set<SalesInvoice>().CountAsync());
            Assert.Equal(linesBefore, await ctx.Set<SalesInvoiceLine>().CountAsync());
            Assert.Equal(movementsBefore, await ctx.Set<StockMovement>().CountAsync());
            Assert.Equal(entriesBefore, await ctx.Set<JournalEntry>().CountAsync());
            Assert.Equal(5m, (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId)).CurrentStock);
        }

        // أرقام القيود لا تُحرق (تُصرف داخل المعاملة) — أما SI فيُصرف قبل المعاملة (فجوة موثّقة، لا تكرار)
        Assert.Equal(jeBefore, await SequenceValueAsync(_pg, "JE"));

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    [LivePgFact]
    public async Task Concurrent_sales_get_unique_document_and_entry_numbers()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("A5-CC-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 100m);
        const int parallel = 6;

        var results = await Task.WhenAll(Enumerable.Range(0, parallel).Select(async _ =>
        {
            await using var ctx = _pg.NewContext();
            var invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(itemId, 1m, 2.38m));
            return (invoice.InvoiceNumber, SaleEntry: invoice.SalesJournalEntryId!.Value, CogsEntry: invoice.CogsJournalEntryId!.Value);
        }));

        Assert.Equal(parallel, results.Select(r => r.InvoiceNumber).Distinct().Count());
        Assert.Equal(parallel, results.Select(r => r.SaleEntry).Distinct().Count());
        Assert.Equal(parallel, results.Select(r => r.CogsEntry).Distinct().Count());
        Assert.Empty(await LedgerInvariants.DuplicateNumbersAsync(_pg));

        var entryIds = results.Select(r => r.SaleEntry).Concat(results.Select(r => r.CogsEntry)).ToList();
        await using (var ctx = _pg.NewContext())
        {
            Assert.Equal(parallel * 2, await ctx.Set<JournalEntry>().CountAsync(e => entryIds.Contains(e.Id) && !e.IsDeleted));
        }

        // الأهم: أرصدة الحسابات المخزَّنة == دفتر القيود (لا فقدان تحديثات عند التزامن)
        await LedgerInvariants.AssertConsistentAsync(_pg);

        var drift = await StockCacheDriftAsync(_pg, itemId);
        _out.WriteLine("[A5] انحراف ذاكرة المخزون بعد التزامن: " + (drift.Count == 0 ? "لا انحراف" : string.Join(" | ", drift)));
        Assert.Empty(drift);

        // حالة ثانية: فواتير متزامنة على أصناف مختلفة → كشف تسابق تحديثات أرصدة الحسابات
        var otherItems = new List<Guid>();
        for (var i = 0; i < parallel; i++)
            otherItems.Add(await _pg.CreateItemAsync($"A5-CD{i}-" + Guid.NewGuid().ToString("N")[..4], 2.38m, 1.75m, 10m));

        await Task.WhenAll(otherItems.Select(async id =>
        {
            await using var ctx = _pg.NewContext();
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(id, 1m, 2.38m));
        }));

        // إن فُقد تحديث رصيد حساب (read-modify-write) سيظهر هنا فوراً
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    /// <summary>
    /// فشل عابر أثناء حفظ الفاتورة: لا إعادة محاولة صامتة (الكتابة غير idempotent) — نفشل بوضوح
    /// بدل إنتاج قيود/أرقام مكررة أو كتابة جزئية، ولا يبقى أي أثر جزئي في قاعدة البيانات.
    /// </summary>
    [LivePgFact]
    public async Task Transient_failure_leaves_no_partial_state_and_never_duplicates()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("A5-RT-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 100m);

        var interceptor = new TransientFailureOnceInterceptor("INSERT INTO \"SalesInvoices\"");
        await using (var ctx = _pg.NewContextWithInterceptor(interceptor))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(itemId, 1m, 2.38m)));
        }

        Assert.Equal(1, interceptor.Thrown);     // فشل عابر وقع فعلاً
        Assert.Equal(1, interceptor.Attempts);   // ولا إعادة محاولة صامتة (محاولة واحدة)

        await using (var ctx = _pg.NewContext())
        {
            Assert.Equal(0, await ctx.Set<SalesInvoice>().CountAsync(i => i.Lines.Any(l => l.ItemId == itemId)));
            Assert.Equal(0, await ctx.Set<SalesInvoiceLine>().CountAsync(l => l.ItemId == itemId));
            Assert.Equal(1, await ctx.Set<StockMovement>().CountAsync(m => m.ItemId == itemId && !m.IsDeleted));
            Assert.Equal(100m, (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId)).CurrentStock);
        }

        Assert.Empty(await LedgerInvariants.DuplicateNumbersAsync(_pg));
        Assert.Empty(await StockCacheDriftAsync(_pg, itemId));
        Assert.Empty(await CogsMismatchAsync(_pg));
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

}
