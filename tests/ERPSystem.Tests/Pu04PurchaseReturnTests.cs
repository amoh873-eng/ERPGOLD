using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// PU-04 (N-3 / G1) — تدقيق مردودات المشتريات:
///  • لا حركة نقدية بلا إثبات رد نقد فعلي.
///  • مردود يخفض الالتزام (OnAccountCredit) لا يُعامل كمردود نقدي، والعكس.
///  • Suppliers.CurrentBalance يتغير وفق نوع التسوية فقط.
///  • كل قيد متوازن ويُنشأ مرة واحدة.
/// </summary>
public class Pu04PurchaseReturnTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Pu04PurchaseReturnTests(AccountingPgFixture pg) => _pg = pg;

    private async Task<Guid> NewSupplierAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "مورد مردود " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    private async Task<(Guid InvoiceId, Guid ItemId)> NewInvoiceAsync(Guid supplierId, decimal amount, bool cash)
    {
        await using var ctx = _pg.NewContext();
        // مخزن الاختبار نفسه المستخدم في المردود (وإلا فحص المخزون يفشل لمخزن مختلف).
        var warehouseId = _pg.WarehouseId;
        var categoryId = await ctx.Set<Category>().Where(c => !c.IsDeleted).Select(c => c.Id).FirstAsync();
        var unitId = await ctx.Set<Unit>().Where(u => !u.IsDeleted).Select(u => u.Id).FirstAsync();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "RET-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "صنف مردود",
            CategoryId = categoryId,
            UnitId = unitId,
            SalePrice = amount,
            CostPrice = amount,
            TaxRate = 0m,
            TracksBatches = false,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        var service = new PurchaseInvoiceService(
            ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());
        var invoice = await service.CreateAsync(new CreatePurchaseInvoiceDto
        {
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)(cash ? PurchaseInvoiceType.Cash : PurchaseInvoiceType.OnAccount),
            PaymentMethod = cash ? CreatePurchaseInvoiceDto.PaymentCash : CreatePurchaseInvoiceDto.PaymentOnAccount,
            Lines = new List<CreatePurchaseInvoiceLineDto>
            {
                new() { ItemId = item.Id, Quantity = 1m, UnitCost = amount }
            }
        });

        return (invoice.Id, item.Id);
    }

    private async Task<(decimal SupplierBalance, decimal Payable, decimal Cash, int Journals)> SnapAsync(Guid supplierId)
    {
        await using var ctx = _pg.NewContext();
        var payableId = (await PostingAccountResolver.ResolveAsync(ctx, "2200")).Id;
        var cashId = (await PostingAccountResolver.ResolveAsync(ctx, "1100")).Id;

        return (
            await ctx.Set<Supplier>().AsNoTracking().Where(s => s.Id == supplierId).Select(s => s.CurrentBalance).FirstAsync(),
            await ctx.Set<Account>().AsNoTracking().Where(a => a.Id == payableId).Select(a => a.CurrentBalance).FirstAsync(),
            await ctx.Set<Account>().AsNoTracking().Where(a => a.Id == cashId).Select(a => a.CurrentBalance).FirstAsync(),
            await ctx.Set<JournalEntry>().AsNoTracking().CountAsync());
    }

    // ── 1) مردود آجل: يخفض الالتزام (2200) ورصيد المورد، ولا يمس الصندوق ──
    [LivePgFact]
    public async Task On_account_return_reduces_payable_and_supplier_balance()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("AP");
        var (invoiceId, itemId) = await NewInvoiceAsync(supplier, 200m, cash: false);
        var before = await SnapAsync(supplier);

        await using (var ctx = _pg.NewContext())
        {
            await new PurchaseReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = invoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                SettlementType = (int)PurchaseReturnSettlementType.OnAccountCredit,
                Lines = new List<CreatePurchaseReturnLineDto>
                {
                    new() { ItemId = itemId, Quantity = 1m, UnitCost = 200m }
                }
            });
        }

        var after = await SnapAsync(supplier);
        Assert.Equal(before.Journals + 1, after.Journals);
        Assert.Equal(before.SupplierBalance - 200m, after.SupplierBalance);
        Assert.Equal(before.Payable - 200m, after.Payable);
        Assert.Equal(before.Cash, after.Cash);
    }

    // ── 2) مردود نقدي بإثبات: لا يغيّر رصيد المورد ولا 2200 ──
    [LivePgFact]
    public async Task Cash_return_with_evidence_does_not_change_supplier_or_payable()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("CASH");
        var (invoiceId, itemId) = await NewInvoiceAsync(supplier, 200m, cash: true);
        var before = await SnapAsync(supplier);

        await using (var ctx = _pg.NewContext())
        {
            await new PurchaseReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = invoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                SettlementType = (int)PurchaseReturnSettlementType.CashRefund,
                RefundReference = "REF-1001",
                Lines = new List<CreatePurchaseReturnLineDto>
                {
                    new() { ItemId = itemId, Quantity = 1m, UnitCost = 200m }
                }
            });
        }

        var after = await SnapAsync(supplier);
        Assert.Equal(before.Journals + 1, after.Journals);
        Assert.Equal(before.SupplierBalance, after.SupplierBalance);   // G1: بلا تغيير
        Assert.Equal(before.Payable, after.Payable);
        Assert.Equal(before.Cash + 200m, after.Cash);
    }

    // ── 3) مردود نقدي بلا إثبات ⇒ مرفوض ──
    [LivePgFact]
    public async Task Cash_return_without_evidence_is_rejected()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("NOEV");
        var (invoiceId, itemId) = await NewInvoiceAsync(supplier, 200m, cash: true);

        await using var ctx = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PurchaseReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = invoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                SettlementType = (int)PurchaseReturnSettlementType.CashRefund,
                Lines = new List<CreatePurchaseReturnLineDto>
                {
                    new() { ItemId = itemId, Quantity = 1m, UnitCost = 200m }
                }
            }));
        Assert.Contains("مرجع إثبات رد النقد", ex.Message);
    }

    // ── 4) فاتورة نقدية + تسوية OnAccountCredit ⇒ مرفوض ──
    [LivePgFact]
    public async Task Cash_invoice_with_on_account_settlement_is_rejected()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("MIX1");
        var (invoiceId, itemId) = await NewInvoiceAsync(supplier, 200m, cash: true);

        await using var ctx = _pg.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PurchaseReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = invoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                SettlementType = (int)PurchaseReturnSettlementType.OnAccountCredit,
                Lines = new List<CreatePurchaseReturnLineDto>
                {
                    new() { ItemId = itemId, Quantity = 1m, UnitCost = 200m }
                }
            }));
    }

    // ── 5) فاتورة آجلة + تسوية CashRefund ⇒ مرفوض ──
    [LivePgFact]
    public async Task On_account_invoice_with_cash_settlement_is_rejected()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("MIX2");
        var (invoiceId, itemId) = await NewInvoiceAsync(supplier, 200m, cash: false);

        await using var ctx = _pg.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PurchaseReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = invoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                SettlementType = (int)PurchaseReturnSettlementType.CashRefund,
                RefundReference = "REF-X",
                Lines = new List<CreatePurchaseReturnLineDto>
                {
                    new() { ItemId = itemId, Quantity = 1m, UnitCost = 200m }
                }
            }));
    }
}
