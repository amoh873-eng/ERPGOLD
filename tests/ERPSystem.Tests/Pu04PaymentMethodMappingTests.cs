using ERPSystem.Application.DTOs;
using ERPSystem.Application.DTOs.Purchases;
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
/// PU-04 (N-7) — خريطة صريحة لدلالة PaymentMethod ومنع الخلط بين فضاءين مختلفين:
///  • فاتورة المشتريات: 1=آجل · 2=شيك · 3=نقدي
///  • سند الصرف:        1=نقدي · 2=بنكي
/// الاختبارات تثبت أن الأرقام **لم تتغيّر دلالتها** (توافق خلفي) وأن الترحيل يفسّرها صحيحًا.
/// </summary>
public class Pu04PaymentMethodMappingTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Pu04PaymentMethodMappingTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "pu04-pm";
    }

    [Fact]
    public void Map_pins_invoice_and_voucher_value_spaces()
    {
        Assert.Equal(1, PaymentMethodMap.InvoiceOnAccount);
        Assert.Equal(2, PaymentMethodMap.InvoiceCheck);
        Assert.Equal(3, PaymentMethodMap.InvoiceCash);

        Assert.Equal(1, PaymentMethodMap.VoucherCash);
        Assert.Equal(2, PaymentMethodMap.VoucherBank);

        Assert.Equal(1, CreatePurchaseInvoiceDto.PaymentOnAccount);
        Assert.Equal(2, CreatePurchaseInvoiceDto.PaymentCheck);
        Assert.Equal(3, CreatePurchaseInvoiceDto.PaymentCash);

        Assert.Equal((int)Domain.Enums.PaymentMethod.Cash, PaymentMethodMap.VoucherCash);
        Assert.Equal((int)Domain.Enums.PaymentMethod.Bank, PaymentMethodMap.VoucherBank);

        Assert.True(PaymentMethodMap.IsValidInvoice(null));
        Assert.True(PaymentMethodMap.IsValidInvoice(3));
        Assert.False(PaymentMethodMap.IsValidInvoice(4));
        Assert.True(PaymentMethodMap.IsValidVoucher(2));
        Assert.False(PaymentMethodMap.IsValidVoucher(3));
    }

    [LivePgFact]
    public async Task Invoice_payment_method_one_is_on_account_not_cash()
    {
        if (!_pg.Available) return;
        await using var ctx = _pg.NewContext();
        var invoice = await NewInvoiceAsync(ctx, PaymentMethodMap.InvoiceOnAccount);

        var saved = await ctx.Set<PurchaseInvoice>().AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(PurchaseInvoiceType.OnAccount, saved.InvoiceType);
        Assert.Equal(0m, saved.PaidAmount);
    }

    [LivePgFact]
    public async Task Invoice_payment_method_three_is_cash_and_fully_paid()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        await using var ctx = _pg.NewContext();
        var invoice = await NewInvoiceAsync(ctx, PaymentMethodMap.InvoiceCash);

        var saved = await ctx.Set<PurchaseInvoice>().AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(PurchaseInvoiceType.Cash, saved.InvoiceType);
        Assert.Equal(saved.TotalAmount, saved.PaidAmount);
    }

    [LivePgFact]
    public async Task Invoice_payment_method_two_registers_issued_check()
    {
        if (!_pg.Available) return;
        await using var ctx = _pg.NewContext();
        var invoice = await NewInvoiceAsync(ctx, PaymentMethodMap.InvoiceCheck);

        var checks = await ctx.Set<BankCheck>().AsNoTracking()
            .Where(c => c.RelatedInvoiceId == invoice.Id).ToListAsync();
        Assert.Single(checks);
        Assert.Equal(BankCheckDirection.IssuedToSupplier, checks[0].Direction);
    }

    [LivePgFact]
    public async Task Invoice_rejects_unknown_payment_method()
    {
        if (!_pg.Available) return;
        await using var ctx = _pg.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewInvoiceAsync(ctx, 4));
    }

    [LivePgFact]
    public async Task Voucher_payment_method_maps_to_cash_or_bank_account()
    {
        if (!_pg.Available) return;
        var supplierId = await SeedSupplierAsync();

        Guid cashId, bankId;
        await using (var ctx0 = _pg.NewContext())
            cashId = (await PostingAccountResolver.ResolveAsync(ctx0, "1100")).Id;
        await using (var ctx1 = _pg.NewContext())
            bankId = (await PostingAccountResolver.ResolveAsync(ctx1, "1101")).Id;

        await using var ctx = _pg.NewContext();
        var service = new PaymentVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());

        var cashVoucher = await service.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = supplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = PaymentMethodMap.VoucherCash,
            Amount = 10m
        });
        var bankVoucher = await service.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = supplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = PaymentMethodMap.VoucherBank,
            Amount = 10m
        });

        Assert.Equal(cashId, cashVoucher.CashOrBankAccountId);
        Assert.Equal(bankId, bankVoucher.CashOrBankAccountId);
    }

    private async Task<Guid> SeedSupplierAsync()
    {
        await using var ctx = _pg.NewContext();
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = "PM-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "مورد خريطة الدفع",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    private async Task<PurchaseInvoiceDto> NewInvoiceAsync(AppDbContext ctx, int paymentMethod)
    {
        var supplierId = await SeedSupplierAsync();
        var warehouseId = await ctx.Set<Warehouse>().Where(w => !w.IsDeleted).Select(w => w.Id).FirstAsync();
        var categoryId = await ctx.Set<Category>().Where(c => !c.IsDeleted).Select(c => c.Id).FirstAsync();
        var unitId = await ctx.Set<Unit>().Where(u => !u.IsDeleted).Select(u => u.Id).FirstAsync();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "PM-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "صنف خريطة الدفع",
            CategoryId = categoryId,
            UnitId = unitId,
            SalePrice = 100m,
            CostPrice = 100m,
            TaxRate = 0m,
            TracksBatches = false,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        var service = new PurchaseInvoiceService(
            ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

        return await service.CreateAsync(new CreatePurchaseInvoiceDto
        {
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)(paymentMethod == PaymentMethodMap.InvoiceOnAccount
                ? PurchaseInvoiceType.OnAccount
                : PurchaseInvoiceType.Cash),
            PaymentMethod = paymentMethod,
            CheckNumber = paymentMethod == PaymentMethodMap.InvoiceCheck ? "PM-CHK-1" : null,
            CheckBankName = paymentMethod == PaymentMethodMap.InvoiceCheck ? "بنك الاختبار" : null,
            CheckDueDate = paymentMethod == PaymentMethodMap.InvoiceCheck ? DateTime.Today.AddDays(30) : null,
            Lines = new List<CreatePurchaseInvoiceLineDto>
            {
                new() { ItemId = item.Id, Quantity = 1m, UnitCost = 100m }
            }
        });
    }
}
