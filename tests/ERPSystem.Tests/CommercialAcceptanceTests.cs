using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// اختبارات القبول التجارية (القسم 8 من المهمة) — تُنفَّذ على قاعدة اختبار مؤقتة تُنشأ وتُحذف ذاتياً،
/// ولا تلمس أي قاعدة قائمة أو إنتاجية.
/// التغطية: بنود 1، 2، 3، 5، 6، 7، 8 (إنشاء وترحيل مبيعات بكل أساليب السداد، مردود جزئي ثم كامل،
/// فاتورة مشتريات مع مردود جزئي ثم كامل، توازن القيود وعدم تكرار الأرقام، وفشل متعمد في منتصف العملية).
/// </summary>
public class CommercialAcceptanceTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public CommercialAcceptanceTests(AccountingPgFixture pg) => _pg = pg;

    private static SalesInvoiceService NewSales(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    private static PurchaseInvoiceService NewPurchase(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    private static SalesReturnService NewSalesReturn(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx));

    private static PurchaseReturnService NewPurchaseReturn(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx));

    private static async Task<decimal> StockAsync(AppDbContext ctx, Guid itemId)
        => await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => i.CurrentStock).FirstAsync();

    private static async Task<decimal> MovementSumAsync(AppDbContext ctx, Guid itemId)
        => await ctx.Set<StockMovement>().AsNoTracking().Where(m => m.ItemId == itemId && !m.IsDeleted)
            .SumAsync(m => (decimal?)m.Quantity) ?? 0m;

    /// <summary>كل قيد مرحّل: مدين == دائن، ولا رقم قيد مكرر في القاعدة كلها.</summary>
    private static async Task AssertEntriesBalancedAndUniqueAsync(AppDbContext ctx)
    {
        var rows = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted)
            .GroupBy(l => l.JournalEntryId)
            .Select(g => new { g.Key, Debit = g.Sum(x => x.DebitAmount), Credit = g.Sum(x => x.CreditAmount) })
            .ToListAsync();
        var unbalanced = rows.Where(r => Math.Abs(r.Debit - r.Credit) > 0.004m).ToList();
        Assert.True(unbalanced.Count == 0, "قيود غير متوازنة: " + string.Join(" ; ", unbalanced.Select(r => r.Key + " D=" + r.Debit + " C=" + r.Credit)));

        var dupes = await ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted)
            .GroupBy(e => e.EntryNumber).Where(g => g.Count() > 1).CountAsync();
        Assert.Equal(0, dupes);
    }

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

        var bad = accounts.Select(a => new
        {
            a.Code,
            Stored = a.CurrentBalance,
            Expected = a.NormalBalance == NormalBalance.Debit ? nets.GetValueOrDefault(a.Code, 0m) : -nets.GetValueOrDefault(a.Code, 0m)
        }).Where(x => Math.Abs(x.Stored - x.Expected) > 0.004m).ToList();

        Assert.True(bad.Count == 0, "أرصدة غير متسقة: " + string.Join(" ; ", bad.Select(m => $"{m.Code} {m.Stored} != {m.Expected}")));
    }

    private static async Task SetAccountActiveAsync(AppDbContext ctx, string code, bool active)
    {
        var account = await ctx.Set<Account>().FirstAsync(a => a.Code == code);
        account.IsActive = active;
        await ctx.SaveChangesAsync();
    }

    // ─────────── بند 1 + 5 + 6 + 7: فاتورة مبيعات نقدية/آجلة/بطاقة/شيك ───────────

    [LivePgFact]
    public async Task Sales_invoice_posts_and_balances_for_cash_credit_card_and_check()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("ACC-METHODS-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 100m, taxRate: 16m);

        var cases = new (string Name, int Type, int Method, bool Card, bool Check)[]
        {
            ("cash", (int)SalesInvoiceType.Cash, (int)SalesPaymentMethod.Cash, false, false),
            ("credit", (int)SalesInvoiceType.OnAccount, (int)SalesPaymentMethod.OnAccount, false, false),
            ("card", (int)SalesInvoiceType.Cash, (int)SalesPaymentMethod.Card, true, false),
            ("check", (int)SalesInvoiceType.Cash, (int)SalesPaymentMethod.Check, false, true),
        };

        var ids = new List<Guid>();
        foreach (var c in cases)
        {
            await using var ctx = _pg.NewContext();
            var dto = new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = c.Type, PaymentMethod = c.Method, TaxRate = 16m,
                CardApprovalCode = c.Card ? "APR-" + c.Name : null,
                CardLast4 = c.Card ? "4242" : null,
                CheckNumber = c.Check ? "CHK-" + Guid.NewGuid().ToString("N")[..6] : null,
                CheckBankName = c.Check ? "Test Bank" : null,
                CheckDueDate = c.Check ? DateTime.Today.AddDays(30) : null,
                Lines = new() { new() { ItemId = itemId, Quantity = 2m, UnitPrice = 15m } }
            };
            var inv = await NewSales(ctx).CreateAsync(dto);
            ids.Add(inv.Id);
            Assert.StartsWith("SI-", inv.InvoiceNumber);
            Assert.True(inv.TotalAmount > 0m);
        }

        await using var check = _pg.NewContext();
        var invoices = await check.Set<SalesInvoice>().AsNoTracking()
            .Where(i => ids.Contains(i.Id)).ToListAsync();
        Assert.Equal(4, invoices.Count);
        Assert.All(invoices, i => Assert.NotNull(i.SalesJournalEntryId));            // بند 7: لا فاتورة مرحّلة بلا قيد
        Assert.Equal(4, invoices.Select(i => i.InvoiceNumber).Distinct().Count());   // بند 7: لا أرقام مكررة

        // بند 5: السداد بشيك يُنشئ سجل شيك مستلم مرتبطًا بالفاتورة
        var checkRows = await check.Set<BankCheck>().AsNoTracking()
            .Where(b => ids.Contains(b.RelatedInvoiceId!.Value)).ToListAsync();
        Assert.Single(checkRows);
        Assert.Equal(BankCheckDirection.ReceivedFromCustomer, checkRows[0].Direction);

        // بند 6: 8 وحدات خرجت من المخزون، ومجموع الحركات = الرصيد
        Assert.Equal(92m, await StockAsync(check, itemId));
        Assert.Equal(await StockAsync(check, itemId), await MovementSumAsync(check, itemId));

        await AssertEntriesBalancedAndUniqueAsync(check);
        await AssertAccountBalancesConsistentAsync(check);
    }

    // ─────────── بند 2: مردود مبيعات جزئي ثم كامل (مع منع تجاوز الكمية) ───────────

    [LivePgFact]
    public async Task Sales_return_partial_then_full_is_guarded_and_consistent()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("ACC-SRET-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 30m, taxRate: 16m);

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var inv = await NewSales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 15m } }
            });
            invoiceId = inv.Id;
        }
        Assert.Equal(20m, await Stock0Async(itemId));

        // مردود جزئي 4 من 10
        await using (var ctx = _pg.NewContext())
        {
            var r = await NewSalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new() { new() { ItemId = itemId, Quantity = 4m, UnitPrice = 15m } }
            });
            Assert.False(string.IsNullOrWhiteSpace(r.ReturnNumber));
        }
        await using (var ctx = _pg.NewContext())
        {
            Assert.Equal(24m, await StockAsync(ctx, itemId));
            Assert.Equal(await StockAsync(ctx, itemId), await MovementSumAsync(ctx, itemId));
            Assert.Equal(1, await ctx.Set<SalesReturn>().AsNoTracking().CountAsync(r => r.SalesInvoiceId == invoiceId));
            await AssertEntriesBalancedAndUniqueAsync(ctx);
        }

        // إكمال المردود (6 المتبقية ⇒ مردود كامل)
        await using (var ctx = _pg.NewContext())
        {
            await NewSalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new() { new() { ItemId = itemId, Quantity = 6m, UnitPrice = 15m } }
            });
        }

        await using var after = _pg.NewContext();
        Assert.Equal(30m, await StockAsync(after, itemId));                          // عاد المخزون لأصله
        var returns = await after.Set<SalesReturn>().AsNoTracking()
            .Where(r => r.SalesInvoiceId == invoiceId)
            .Select(r => new { r.SalesJournalEntryId, r.CogsJournalEntryId }).ToListAsync();
        Assert.Equal(2, returns.Count);
        Assert.All(returns, r => Assert.NotNull(r.SalesJournalEntryId));
        Assert.All(returns, r => Assert.NotNull(r.CogsJournalEntryId));
        Assert.Equal(4, returns.SelectMany(r => new[] { r.SalesJournalEntryId, r.CogsJournalEntryId }).Distinct().Count());

        // محاولة رد أكثر من المباع ⇒ مرفوضة بلا أي أثر جزئي
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => NewSalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new() { new() { ItemId = itemId, Quantity = 1m, UnitPrice = 15m } }
            }));
        }

        await using var final = _pg.NewContext();
        Assert.Equal(2, await final.Set<SalesReturn>().AsNoTracking().CountAsync(r => r.SalesInvoiceId == invoiceId));
        Assert.Equal(30m, await StockAsync(final, itemId));
        Assert.Equal(await StockAsync(final, itemId), await MovementSumAsync(final, itemId));
        await AssertEntriesBalancedAndUniqueAsync(final);
        await AssertAccountBalancesConsistentAsync(final);
    }

    private async Task<decimal> Stock0Async(Guid itemId)
    {
        await using var ctx = _pg.NewContext();
        return await StockAsync(ctx, itemId);
    }

    // ─────────── بند 3: فاتورة مشتريات + مردود جزئي ثم كامل ───────────

    [LivePgFact]
    public async Task Purchase_invoice_with_partial_then_full_return_is_consistent()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("ACC-PUR-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 0m, taxRate: 16m);

        Guid purchaseId;
        await using (var ctx = _pg.NewContext())
        {
            var pi = await NewPurchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = _pg.SupplierId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)PurchaseInvoiceType.Cash,
                Lines = new() { new() { ItemId = itemId, Quantity = 5m, UnitCost = 10m } }
            });
            purchaseId = pi.Id;
            Assert.StartsWith("PI-", pi.InvoiceNumber);
        }
        Assert.Equal(5m, await Stock0Async(itemId));

        // مردود جزئي 2 من 5
        await using (var ctx = _pg.NewContext())
        {
            var pr = await NewPurchaseReturn(ctx).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = purchaseId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today, SettlementType = 2, RefundReference = "PU04-TEST-REF",
                Lines = new() { new() { ItemId = itemId, Quantity = 2m, UnitCost = 10m } }
            });
            Assert.False(string.IsNullOrWhiteSpace(pr.ReturnNumber));
        }
        await using (var ctx = _pg.NewContext())
        {
            Assert.Equal(3m, await StockAsync(ctx, itemId));
            Assert.Equal(await StockAsync(ctx, itemId), await MovementSumAsync(ctx, itemId));
            Assert.Equal(1, await ctx.Set<PurchaseReturn>().AsNoTracking().CountAsync(r => r.PurchaseInvoiceId == purchaseId));
            await AssertEntriesBalancedAndUniqueAsync(ctx);
        }

        // إكمال المردود (3 المتبقية ⇒ مردود كامل)
        await using (var ctx = _pg.NewContext())
        {
            await NewPurchaseReturn(ctx).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = purchaseId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today, SettlementType = 2, RefundReference = "PU04-TEST-REF",
                Lines = new() { new() { ItemId = itemId, Quantity = 3m, UnitCost = 10m } }
            });
        }

        await using var final = _pg.NewContext();
        Assert.Equal(0m, await StockAsync(final, itemId));                             // عاد المخزون إلى صفر
        Assert.Equal(await StockAsync(final, itemId), await MovementSumAsync(final, itemId));
        Assert.Equal(2, await final.Set<PurchaseReturn>().AsNoTracking().CountAsync(r => r.PurchaseInvoiceId == purchaseId));
        var returnRows = await final.Set<PurchaseReturn>().AsNoTracking().Where(r => r.PurchaseInvoiceId == purchaseId).ToListAsync();
        Assert.All(returnRows, r => Assert.NotNull(r.JournalEntryId));
        await AssertEntriesBalancedAndUniqueAsync(final);
        await AssertAccountBalancesConsistentAsync(final);
    }

    // ─────────── بند 8: فشل متعمد في منتصف العملية ⇒ تراجع كامل ───────────

    [LivePgFact]
    public async Task Deliberate_mid_operation_failure_rolls_back_the_entire_sale()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("ACC-FAIL-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 20m, taxRate: 16m);

        // إعطال حساب المخزون (1300): يقع الفشل في منتصف العملية (بعد حركة المخزون وقيد الإيراد)
        await using (var ctx = _pg.NewContext()) await SetAccountActiveAsync(ctx, "1300", false);
        try
        {
            await using var ctx = _pg.NewContext();
            await Assert.ThrowsAnyAsync<Exception>(() => NewSales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemId, Quantity = 3m, UnitPrice = 15m } }
            }));
        }
        finally
        {
            await using var ctx = _pg.NewContext();
            await SetAccountActiveAsync(ctx, "1300", true);
        }

        await using var check = _pg.NewContext();
        Assert.Equal(0, await check.Set<SalesInvoice>().AsNoTracking()
            .CountAsync(i => !i.IsDeleted && i.Lines.Any(l => l.ItemId == itemId)));   // لا فاتورة شبحية
        Assert.Equal(20m, await StockAsync(check, itemId));                           // لا حركة مخزون باقية
        Assert.Equal(await StockAsync(check, itemId), await MovementSumAsync(check, itemId));
        await AssertAccountBalancesConsistentAsync(check);                            // لا أثر على أي رصيد

        // العملية الصحيحة بعد زوال سبب الفشل تنجح
        await using (var ctx = _pg.NewContext())
        {
            var ok = await NewSales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemId, Quantity = 2m, UnitPrice = 15m } }
            });
            Assert.StartsWith("SI-", ok.InvoiceNumber);
        }

        await using var after = _pg.NewContext();
        Assert.Equal(18m, await StockAsync(after, itemId));
        Assert.Equal(1, await after.Set<SalesInvoice>().AsNoTracking()
            .CountAsync(i => !i.IsDeleted && i.Lines.Any(l => l.ItemId == itemId)));
        await AssertEntriesBalancedAndUniqueAsync(after);
        await AssertAccountBalancesConsistentAsync(after);
    }
}
