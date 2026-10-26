using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P0-2 — دلالات <c>Customer.CurrentBalance</c> مقابل دفتر الأستاذ (قياس موثَّق، بلا تغيير سلوك).
///
/// بعد اعتماد **سياسة الذمم (SUB-1/SUB-2/SUB-3 — 2026-10-24)** صار الحقل يمثل «صافي المستحق» ويطابق 1200:
///   • بيع نقدي: لا يُنشئ ذمة ⇒ الرصيد التشغيلي ثابت و1200 ثابت (متطابقان على صفر).
///   • بيع آجل: الرصيد التشغيلي وحساب الذمم يتحركان معًا بنفس المقدار (متطابقان).
///   • سند قبض: كلاهما ينقص بنفس المقدار (متطابقان).
/// قبل السياسة كان البيع النقدي يزيد الرصيد التشغيلي و1200 لا يتحرك (انحراف SUB-1) — أُصلح ومُثبت هنا.
/// </summary>
public class CustomerBalanceSemanticsProbeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public CustomerBalanceSemanticsProbeTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "p0-2-probe";
    }

    [LivePgFact]
    public async Task Customer_balance_versus_ledger_by_settlement_type_characterization()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02");
        var itemId = await _pg.CreateItemAsync("P02-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 100m, taxRate: 16m);

        // (1) بيع نقدي 5 × 15 + 16% = 87
        var customerBefore = await CustomerBalanceAsync(customer);
        Guid cashSaleInvoiceId;
        await using (var ctx = _pg.NewContext())
            cashSaleInvoiceId = (await AccountingPgFixture.NewSalesService(ctx)
                .CreateAsync(Sale(customer, itemId, SalesInvoiceType.Cash, SalesPaymentMethod.Cash))).Id;
        var cashSaleCustomerDelta = await CustomerBalanceAsync(customer) - customerBefore;
        var cashSaleLegs = await EntryNetByAccountAsync(await SalesRevenueEntryIdAsync(cashSaleInvoiceId));

        Assert.Equal(0m, cashSaleCustomerDelta);                        // SUB-1: النقدي لا يُنشئ ذمة ⇒ الرصيد ثابت
        Assert.Equal(0m, cashSaleLegs.GetValueOrDefault("1200", 0m));   // وحساب الذمم لا يتحرك (متطابقان)
        Assert.Equal(87m, cashSaleLegs.GetValueOrDefault("1100", 0m));  // القيد مدين الصندوق

        // (2) بيع آجل 5 × 15 + 16% = 87
        var beforeCreditSale = await CustomerBalanceAsync(customer);
        Guid creditSaleInvoiceId;
        await using (var ctx = _pg.NewContext())
            creditSaleInvoiceId = (await AccountingPgFixture.NewSalesService(ctx)
                .CreateAsync(Sale(customer, itemId, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount))).Id;
        var creditSaleCustomerDelta = await CustomerBalanceAsync(customer) - beforeCreditSale;
        var creditSaleLegs = await EntryNetByAccountAsync(await SalesRevenueEntryIdAsync(creditSaleInvoiceId));

        Assert.Equal(87m, creditSaleCustomerDelta);
        Assert.Equal(87m, creditSaleLegs.GetValueOrDefault("1200", 0m));   // هنا يتطابق الرصيد مع الأستاذ

        // (3) سند قبض 50
        var beforeReceipt = await CustomerBalanceAsync(customer);
        Guid? receiptEntryId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = customer, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 50m
            });
            await svc.ApproveAsync(draft.Id);
            receiptEntryId = (await svc.PostAsync(draft.Id)).JournalEntryId;
        }
        var receiptCustomerDelta = await CustomerBalanceAsync(customer) - beforeReceipt;
        var receiptLegs = await EntryNetByAccountAsync(receiptEntryId);

        Assert.Equal(-50m, receiptCustomerDelta);
        Assert.Equal(-50m, receiptLegs.GetValueOrDefault("1200", 0m));     // متطابقان
    }

    private CreateSalesInvoiceDto Sale(Guid customerId, Guid itemId, SalesInvoiceType type, SalesPaymentMethod method)
        => new()
        {
            CustomerId = customerId,
            WarehouseId = _pg.WarehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)type,
            PaymentMethod = (int)method,
            TaxRate = 16m,
            Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 5m, UnitPrice = 15m } }
        };

    private async Task<decimal> CustomerBalanceAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    /// <summary>قيد الإيراد/الذمم للفاتورة (قيد تكلفة البضاعة قيد منفصل: CogsJournalEntryId).</summary>
    private async Task<Guid?> SalesRevenueEntryIdAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<SalesInvoice>().AsNoTracking()
            .Where(i => i.Id == invoiceId).Select(i => i.SalesJournalEntryId).FirstAsync();
    }

    /// <summary>صافي (مدين − دائن) لكل كود حساب **داخل قيد واحد محدد** — لا يتأثر بحركة اختبارات أخرى.</summary>
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

    private async Task<Guid> NewCustomerAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "عميل قياس " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }
}
