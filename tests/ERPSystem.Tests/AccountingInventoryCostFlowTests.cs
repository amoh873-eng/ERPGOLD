using ERPSystem.Application.DTOs.Accounting;
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
/// استكمال فحوص المخزون والتكلفة (2026-10-25): الرصيد الافتتاحي المخزني ⇄ الشراء ⇄ البيع ⇄ المردود.
///
/// الثوابت المُثبتة بالأرقام:
///   • الكمية والتكلفة: المتوسط المرجّح بعد الوارد يُحتسب بنفس دالة التقييم الرسمية.
///   • تكلفة البضاعة المباعة = الكمية × متوسط التكلفة لحظة البيع.
///   • مردود البيع يعكس تكلفة البضاعة بنفس متوسط التكلفة (تناظر COGS/المخزون).
///   • **الثابت الثلاثي**: أثر هذا الصنف على حساب 1300 = مجموع قيمة حركاته المخزنية
///     (كمية × تكلفة الوحدة) ⇒ لا ازدواج أثر ولا انفصال بين الأستاذ وحركات المخزون.
///   • المخزَّن = المستنتج لكل الحسابات في كل خطوة (LedgerInvariants).
/// </summary>
public class AccountingInventoryCostFlowTests : IClassFixture<AccountingPgFixture>
{
    private const string Inventory = "1300";
    private const string Cogs = "5100";
    private const string Equity = "2100";

    private readonly AccountingPgFixture _pg;

    public AccountingInventoryCostFlowTests(AccountingPgFixture pg) => _pg = pg;

    // ───────────────────────── أدوات ─────────────────────────

    private async Task<Guid> NewItemAsync(string suffix, decimal salePrice = 30m)
    {
        await using var ctx = _pg.NewContext();
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "COST-" + suffix,
            NameAr = "صنف تكلفة " + suffix,
            CategoryId = _pg.CategoryId,
            UnitId = _pg.UnitId,
            SalePrice = salePrice,
            CostPrice = 0m,
            TaxRate = 0m,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private async Task PostOpeningStockAsync(string itemCode, decimal quantity, decimal unitCost, int year)
    {
        await using var ctx = _pg.NewContext();
        var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
        var value = Math.Round(quantity * unitCost, 2);

        var draft = await svc.CreateDraftAsync(new CreateOpeningBalanceBatchDto
        {
            FiscalYear = year,
            EntryDate = new DateTime(year, 1, 10),
            Reference = $"COST-OB-{year}",
            Lines = new List<OpeningBalanceLineInput>
            {
                new()
                {
                    AccountCode = Inventory,
                    ItemCode = itemCode,
                    WarehouseCode = AccountingPgFixture.WarehouseCode,
                    Quantity = quantity,
                    UnitCost = unitCost
                },
                new() { AccountCode = Equity, CreditAmount = value }
            }
        }, "tester");

        await svc.PostAsync(draft.Id, "tester");
    }

    private async Task<Guid> PurchaseOnAccountAsync(Guid itemId, decimal quantity, decimal unitCost, int year)
    {
        await using var ctx = _pg.NewContext();
        var invoice = await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx),
                new AccountingPgFixture.AllowAllManualPrice())
            .CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = _pg.SupplierId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = new DateTime(year, 2, 10),
                InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                PaymentMethod = CreatePurchaseInvoiceDto.PaymentOnAccount,
                Lines = new List<CreatePurchaseInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = quantity, UnitCost = unitCost }
                }
            });
        return invoice.Id;
    }

    private async Task<Guid> SaleAsync(Guid itemId, decimal quantity, decimal unitPrice, int year)
    {
        await using var ctx = _pg.NewContext();
        var invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = _pg.CustomerId,
            WarehouseId = _pg.WarehouseId,
            InvoiceDate = new DateTime(year, 3, 10),
            InvoiceType = (int)SalesInvoiceType.Cash,
            PaymentMethod = (int)SalesPaymentMethod.Cash,
            TaxRate = 0m,
            Lines = new List<CreateSalesInvoiceLineDto>
            {
                new() { ItemId = itemId, Quantity = quantity, UnitPrice = unitPrice }
            }
        });
        return invoice.Id;
    }

    private async Task<Guid> ReturnAsync(Guid invoiceId, Guid itemId, decimal quantity, decimal unitPrice, int year)
    {
        await using var ctx = _pg.NewContext();
        var ret = await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(
            new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = new DateTime(year, 4, 10),
                Lines = new List<CreateSalesReturnLineDto>
                {
                    new() { ItemId = itemId, Quantity = quantity, UnitPrice = unitPrice }
                }
            });
        return ret.Id;
    }

    private async Task<decimal> AccountEffectAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && !l.IsDeleted
                        && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .Select(l => new { l.DebitAmount, l.CreditAmount })
            .ToListAsync();
        return Math.Round(lines.Sum(l => l.DebitAmount - l.CreditAmount), 2);
    }

    private async Task<(decimal Stock, decimal Cost)> ItemStateAsync(Guid itemId)
    {
        await using var ctx = _pg.NewContext();
        var item = await ctx.Set<Item>().AsNoTracking()
            .Where(i => i.Id == itemId).Select(i => new { i.CurrentStock, i.CostPrice }).FirstAsync();
        return (item.CurrentStock, item.CostPrice);
    }

    /// <summary>مجموع قيمة حركات المخزون للصنف (كمية × تكلفة الوحدة) — بكل أنواع الحركات.</summary>
    private async Task<decimal> MovementValueAsync(Guid itemId)
    {
        await using var ctx = _pg.NewContext();
        var rows = await ctx.Set<StockMovement>().AsNoTracking()
            .Where(m => m.ItemId == itemId && !m.IsDeleted)
            .Select(m => new { m.Quantity, m.UnitCost })
            .ToListAsync();
        return Math.Round(rows.Sum(r => r.Quantity * r.UnitCost), 2);
    }

    private async Task<int> InvoiceCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<SalesInvoice>().CountAsync(i => !i.IsDeleted);
    }

    // ═══════════ 1) افتتاحي ⇄ شراء (متوسط مرجّح) ⇄ بيع (COGS مطابق) ═══════════

    [LivePgFact]
    public async Task Opening_then_purchase_then_sale_keeps_cost_and_control_in_sync()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2081;

        var itemId = await NewItemAsync("A");
        var inventoryBefore = await AccountEffectAsync(Inventory);
        var cogsBefore = await AccountEffectAsync(Cogs);

        // (1) افتتاحي: 100 × 12 = 1200 (حركة مخزون افتتاحية + قيد Dr 1300)
        await PostOpeningStockAsync("COST-A", 100m, 12m, year);
        Assert.Equal(100m, (await ItemStateAsync(itemId)).Stock);
        Assert.Equal(12m, (await ItemStateAsync(itemId)).Cost);
        Assert.Equal(inventoryBefore + 1200m, await AccountEffectAsync(Inventory));

        // (2) شراء آجل 100 × 18 = 1800 ⇒ متوسط مرجّح = (100×12 + 100×18) / 200 = 15
        await PurchaseOnAccountAsync(itemId, 100m, 18m, year);
        var afterPurchase = await ItemStateAsync(itemId);
        Assert.Equal(200m, afterPurchase.Stock);
        Assert.Equal(15m, afterPurchase.Cost);
        Assert.Equal(inventoryBefore + 3000m, await AccountEffectAsync(Inventory));

        // (3) بيع نقدي 50 × 30 ⇒ COGS = 50 × 15 = 750
        await SaleAsync(itemId, 50m, 30m, year);
        var afterSale = await ItemStateAsync(itemId);
        Assert.Equal(150m, afterSale.Stock);
        Assert.Equal(750m, await AccountEffectAsync(Cogs) - cogsBefore);
        Assert.Equal(inventoryBefore + 2250m, await AccountEffectAsync(Inventory));   // 1200 + 1800 − 750

        // (4) الثابت الثلاثي: أثر 1300 لهذا الصنف = مجموع قيمة حركاته المخزنية
        Assert.Equal(2250m, await MovementValueAsync(itemId));
        Assert.Equal(2250m, await AccountEffectAsync(Inventory) - inventoryBefore);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ═══════════ 2) مردود بيع يعكس COGS بنفس متوسط التكلفة ═══════════

    [LivePgFact]
    public async Task Sales_return_reverses_cogs_at_the_same_average_cost()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2082;

        var itemId = await NewItemAsync("B");
        var inventoryBefore = await AccountEffectAsync(Inventory);
        var cogsBefore = await AccountEffectAsync(Cogs);

        await PostOpeningStockAsync("COST-B", 100m, 12m, year);
        await PurchaseOnAccountAsync(itemId, 100m, 18m, year);          // متوسط 15
        var invoiceId = await SaleAsync(itemId, 50m, 30m, year);        // COGS 750

        // مردود 20 وحدة ⇒ عكس COGS = 20 × 15 = 300
        await ReturnAsync(invoiceId, itemId, 20m, 30m, year);

        var state = await ItemStateAsync(itemId);
        Assert.Equal(170m, state.Stock);                                  // 100 + 100 − 50 + 20
        Assert.Equal(15m, state.Cost);                                    // المردود لا يغيّر متوسط التكلفة
        Assert.Equal(450m, await AccountEffectAsync(Cogs) - cogsBefore);   // 750 − 300
        Assert.Equal(inventoryBefore + 2550m, await AccountEffectAsync(Inventory));  // 1200+1800−750+300

        // الثابت الثلاثي يبقى صحيحًا بعد المردود
        Assert.Equal(2550m, await MovementValueAsync(itemId));
        Assert.Equal(2550m, await AccountEffectAsync(Inventory) - inventoryBefore);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ═══════════ 3) تزامن: بيعان متوازيان لا يبيعان أكثر من المتاح ولا يضاعفان الأثر ═══════════

    [LivePgFact]
    public async Task Concurrent_sales_against_opened_stock_never_oversell_nor_double_count()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2083;

        var itemId = await NewItemAsync("C");
        var inventoryBefore = await AccountEffectAsync(Inventory);
        var invoicesBefore = await InvoiceCountAsync();

        await PostOpeningStockAsync("COST-C", 100m, 10m, year);

        async Task<string> SellAsync()
        {
            try
            {
                await SaleAsync(itemId, 60m, 20m, year);
                return "ok";
            }
            catch (ERPSystem.Application.Exceptions.InsufficientStockException)
            {
                return "insufficient";
            }
        }

        var results = await Task.WhenAll(SellAsync(), SellAsync());

        // واحد فقط ينجح (المتاح 100، والمطلوب 60 لكل بيع بعد القفل التسلسلي على الصنف)
        Assert.Contains("ok", results);
        Assert.Contains("insufficient", results);
        Assert.Equal(invoicesBefore + 1, await InvoiceCountAsync());

        // الكمية النهائية = 40 والمخزون لا يصبح سالبًا، والثابت الثلاثي سليم
        var state = await ItemStateAsync(itemId);
        Assert.Equal(40m, state.Stock);
        Assert.True(state.Stock >= 0m);
        Assert.Equal(400m, await MovementValueAsync(itemId));            // 1000 افتتاحي − 600 COGS
        Assert.Equal(400m, await AccountEffectAsync(Inventory) - inventoryBefore);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
