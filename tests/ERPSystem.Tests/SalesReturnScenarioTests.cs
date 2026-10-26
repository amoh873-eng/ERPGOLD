using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// AUDIT-R2 — سيناريو مردود المبيعات من البداية إلى النهاية (لم يكن مُختبرًا قبل هذه الجولة).
/// يقيس: أثر المردود على الكمية، وعلى دفتر الأستاذ (قيد عكس البيع + قيد عكس تكلفة البضاعة)،
/// وعلى <c>Customer.CurrentBalance</c>، ثم يتحقق من الثوابت العامة (المخزَّن = المستنتج، توازن الأستاذ،
/// الكمية = مجموع الحركات). لا يُصلح ولا يُغيّر أي سياسة — يقيس فقط.
/// </summary>
public class SalesReturnScenarioTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public SalesReturnScenarioTests(AccountingPgFixture pg) => _pg = pg;

    private static SalesReturnService Returns(AppDbContext ctx) => new(ctx, new JournalEntryService(ctx));

    [LivePgFact]
    public async Task Credit_sale_then_return_keeps_ledger_subledger_and_stock_consistent()
    {
        if (!_pg.Available) return;
        var customer = await NewCustomerAsync("SR");
        var itemId = await _pg.CreateItemAsync("SR-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 100m, taxRate: 16m);

        // (1) بيع آجل 4 × 15 + 16% = 69.60
        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
            invoiceId = (await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customer,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount,
                PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 16m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 4m, UnitPrice = 15m } }
            })).Id;

        var customerAfterSale = await CustomerBalanceAsync(customer);

        // (2) مردود بيع 2 × 15 (يُعاد للمخزون ويُعكس القيدان)
        Guid returnId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await Returns(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = itemId, Quantity = 2m, UnitPrice = 15m } }
            });
            returnId = created.Id;
        }

        Guid? revenueEntryId, cogsEntryId;
        decimal returnTotal;
        await using (var ctx = _pg.NewContext())
        {
            var ret = await ctx.Set<SalesReturn>().AsNoTracking().FirstAsync(r => r.Id == returnId);
            revenueEntryId = ret.SalesJournalEntryId;
            cogsEntryId = ret.CogsJournalEntryId;
            returnTotal = ret.TotalAmount;
        }

        // (أ) المردود أُنشئ بقيود عكسية (بيع + تكلفة بضاعة)
        Assert.NotNull(revenueEntryId);
        Assert.NotNull(cogsEntryId);

        // (ب) الأستاذ: قيد العكس ينقص الذمم بمقدار إجمالي المردود، ورصيد العميل يطابقه
        var revenueLegs = await EntryNetByAccountAsync(revenueEntryId);
        var arLeg = revenueLegs.GetValueOrDefault("1200", 0m);
        var customerDelta = await CustomerBalanceAsync(customer) - customerAfterSale;

        Assert.True(Math.Abs(arLeg + returnTotal) <= 0.005m,
            $"قيد المردود لا يطابق إجمالي المردود: AR={arLeg:F2} total={returnTotal:F2}");
        Assert.True(Math.Abs(customerDelta + returnTotal) <= 0.005m,
            $"رصيد العميل لا يطابق إجمالي المردود: delta={customerDelta:F2} total={returnTotal:F2}");

        // (ج) قيد تكلفة البضاعة المعكوس متوازن وبمقدار 2 × 10 = 20
        var cogs = await EntryTotalsAsync(cogsEntryId);
        Assert.True(Math.Abs(cogs.Debit - cogs.Credit) <= 0.005m, "قيد عكس تكلفة البضاعة غير متوازن");
        Assert.Equal(20m, cogs.Debit);
        Assert.Equal(20m, cogs.Credit);

        await AssertGlobalInvariantsAsync(itemId, expectedStock: 100m - 4m + 2m);
    }

    /// <summary>الثوابت العامة: المخزَّن = المستنتج لكل الحسابات · توازن الأستاذ · الكمية = مجموع الحركات.</summary>
    private async Task AssertGlobalInvariantsAsync(Guid itemId, decimal expectedStock)
    {
        await using var ctx = _pg.NewContext();

        var accounts = await ctx.Set<Account>().AsNoTracking().Where(a => !a.IsDeleted)
            .Select(a => new { a.Id, a.Code, a.NormalBalance, a.CurrentBalance }).ToListAsync();
        var nets = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
            .Join(ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted),
                  l => l.JournalEntryId, e => e.Id, (l, e) => new { l.AccountId, l.DebitAmount, l.CreditAmount })
            .GroupBy(x => x.AccountId)
            .Select(g => new { AccountId = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Net);

        var bad = accounts.Where(a =>
        {
            var derived = a.NormalBalance == NormalBalance.Debit
                ? nets.GetValueOrDefault(a.Id, 0m) : -nets.GetValueOrDefault(a.Id, 0m);
            return Math.Abs(a.CurrentBalance - derived) > 0.005m;
        }).Select(a => a.Code).ToList();
        Assert.True(bad.Count == 0, "أرصدة مخزَّنة ≠ مستنتجة بعد المردود: " + string.Join(",", bad));

        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
            .Select(l => new { l.DebitAmount, l.CreditAmount }).ToListAsync();
        Assert.True(Math.Abs(lines.Sum(l => l.DebitAmount) - lines.Sum(l => l.CreditAmount)) <= 0.005m,
            "الأستاذ غير متوازن بعد المردود");

        var stock = await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => i.CurrentStock).FirstAsync();
        var movements = await ctx.Set<StockMovement>().AsNoTracking()
            .Where(m => m.ItemId == itemId && !m.IsDeleted).SumAsync(m => (decimal?)m.Quantity) ?? 0m;
        Assert.Equal(movements, stock);
        Assert.Equal(expectedStock, stock);
    }

    private async Task<decimal> CustomerBalanceAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<Dictionary<string, decimal>> EntryNetByAccountAsync(Guid? entryId)
    {
        if (entryId is null) return new Dictionary<string, decimal>();
        await using var ctx = _pg.NewContext();
        var rows = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.JournalEntryId == entryId.Value)
            .Join(ctx.Set<Account>().AsNoTracking(), l => l.AccountId, a => a.Id,
                  (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
            .GroupBy(x => x.Code)
            .Select(g => new { Code = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToListAsync();
        return rows.ToDictionary(r => r.Code, r => r.Net);
    }

    private async Task<(decimal Debit, decimal Credit)> EntryTotalsAsync(Guid? entryId)
    {
        if (entryId is null) return (0m, 0m);
        await using var ctx = _pg.NewContext();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.JournalEntryId == entryId.Value)
            .Select(l => new { l.DebitAmount, l.CreditAmount }).ToListAsync();
        return (lines.Sum(l => l.DebitAmount), lines.Sum(l => l.CreditAmount));
    }

    private async Task<Guid> NewCustomerAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "عميل مردود " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }
}
