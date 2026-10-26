using ERPSystem.Application.DTOs.Journal;
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
/// PU-04 (D12) — مطابقة ΣR · F2 · F1:
///  • لا احتساب مزدوج للمبلغ نفسه.
///  • لا تسوية تلقائية للفروقات.
///  • القيود اليدوية غير المنسوبة تظهر بوضوح في «غير المنسوب».
///  • الرصيد السالب يظهر ولا يُصفَّر.
/// </summary>
public class Pu04ReconciliationTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Pu04ReconciliationTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "pu04-recon";
    }

    private async Task<Guid> NewSupplierAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "مورد مطابقة " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    private async Task<(Guid InvoiceId, Guid ItemId)> NewInvoiceAsync(Guid supplierId, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        // مخزن الاختبار نفسه المستخدم في المردود (وإلا فحص المخزون يفشل لمخزن مختلف).
        var warehouseId = _pg.WarehouseId;
        var categoryId = await ctx.Set<Category>().Where(c => !c.IsDeleted).Select(c => c.Id).FirstAsync();
        var unitId = await ctx.Set<Unit>().Where(u => !u.IsDeleted).Select(u => u.Id).FirstAsync();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "RC-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "صنف مطابقة",
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
            InvoiceType = (int)PurchaseInvoiceType.OnAccount,
            PaymentMethod = CreatePurchaseInvoiceDto.PaymentOnAccount,
            Lines = new List<CreatePurchaseInvoiceLineDto>
            {
                new() { ItemId = item.Id, Quantity = 1m, UnitCost = amount }
            }
        });

        return (invoice.Id, item.Id);
    }

    private async Task<Guid> PostVoucherAsync(Guid supplierId, decimal amount, params (Guid InvoiceId, decimal Amount)[] allocations)
    {
        await using var ctx = _pg.NewContext();
        var service = new PaymentVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await service.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = supplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = 1,
            Amount = amount
        });
        await service.ApproveAsync(draft.Id);
        await service.PostAsync(draft.Id, allocations
            .Select(a => new PaymentAllocationInput { PurchaseInvoiceId = a.InvoiceId, Amount = a.Amount })
            .ToList());
        return draft.Id;
    }

    private async Task<SupplierReconciliationDto> RowAsync(Guid supplierId)
    {
        await using var ctx = _pg.NewContext();
        var report = await new SupplierReconciliationService(ctx).GetAsync(DateTime.Today);
        return report.Rows.Single(r => r.SupplierId == supplierId);
    }

    // ── 1) فاتورة آجلة مسددة جزئيًا: الأرصدة الثلاثة متساوية في سيناريو نظيف ──
    [LivePgFact]
    public async Task Partial_payment_matches_all_three_balances()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("PART");
        var (invoiceId, _) = await NewInvoiceAsync(supplier, 200m);
        await PostVoucherAsync(supplier, 60m, (invoiceId, 60m));

        var row = await RowAsync(supplier);
        Assert.Equal(140m, row.SumResidual);
        Assert.Equal(140m, row.SupplierBalance);
        Assert.Equal(140m, row.AttributedLedger);
        Assert.Equal(0m, row.ResidualVsBalance);
        Assert.Equal(0m, row.BalanceVsLedger);
    }

    // ── 2) عدة فواتير + تسديدات ──
    [LivePgFact]
    public async Task Multiple_invoices_payments_stay_consistent()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("MULTI");
        var (a, _) = await NewInvoiceAsync(supplier, 200m);
        var (b, _) = await NewInvoiceAsync(supplier, 100m);
        await PostVoucherAsync(supplier, 150m, (a, 100m), (b, 50m));

        var row = await RowAsync(supplier);
        Assert.Equal(150m, row.SumResidual);          // A:100 + B:50
        Assert.Equal(150m, row.SupplierBalance);
        Assert.Equal(150m, row.AttributedLedger);
    }

    // ── 3) عكس سند دفع كامل: الأرصدة تعود بلا ازدواج ──
    [LivePgFact]
    public async Task Full_voucher_reversal_restores_balances_without_double_counting()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REV");
        var (invoiceId, _) = await NewInvoiceAsync(supplier, 200m);
        var voucherId = await PostVoucherAsync(supplier, 200m, (invoiceId, 200m));

        Guid allocationId;
        await using (var ctx = _pg.NewContext())
        {
            allocationId = await ctx.Set<PaymentAllocation>().AsNoTracking()
                .Where(x => x.PaymentVoucherId == voucherId && x.Direction == 1)
                .Select(x => x.Id).FirstAsync();
        }

        await using (var ctx = _pg.NewContext())
        {
            var service = new PaymentVoucherReversalService(ctx, new JournalEntryService(ctx), new AllowAll());
            await service.ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucherId,
                VoucherDate = DateTime.Today,
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 200m }
                }
            });
        }

        var row = await RowAsync(supplier);
        Assert.Equal(200m, row.SumResidual);
        Assert.Equal(200m, row.SupplierBalance);
        Assert.Equal(200m, row.AttributedLedger);
    }

    // ── 4) رصيد فاتورة سالب يظهر كحالة تحتاج مراجعة ──
    [LivePgFact]
    public async Task Negative_invoice_residual_is_reported_not_zeroed()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("NEG");
        var (invoiceId, itemId) = await NewInvoiceAsync(supplier, 200m);
        await PostVoucherAsync(supplier, 200m, (invoiceId, 200m));

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
                    // الكمية 0.25 × تكلفة الفاتورة 200 = مردود 50 (التكلفة تُؤخذ من سطر الفاتورة).
                    new() { ItemId = itemId, Quantity = 0.25m, UnitCost = 200m }
                }
            });
        }

        var row = await RowAsync(supplier);
        Assert.True(row.NegativeInvoices >= 1, "يجب الإبلاغ عن فاتورة ذات رصيد سالب");
        Assert.Equal(-50m, row.SumResidual);
        Assert.Equal(-50m, row.SupplierBalance);
    }

    // ── 5) قيد يدوي على 2200: «غير منسوب» ظاهر ولا تسوية تلقائية ──
    [LivePgFact]
    public async Task Manual_entry_on_payable_is_reported_as_unattributed()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("MANUAL");
        await NewInvoiceAsync(supplier, 100m);

        await using (var ctx = _pg.NewContext())
        {
            var inventoryId = (await PostingAccountResolver.ResolveAsync(ctx, "1300")).Id;
            var payableId = (await PostingAccountResolver.ResolveAsync(ctx, "2200")).Id;
            await new JournalEntryService(ctx).CreateManualEntryAsync(
                DateTime.Today, "MANUAL-2200", "قيد يدوي للاختبار",
                new List<JournalEntryLegInput>
                {
                    new() { AccountId = inventoryId, DebitAmount = 40m },
                    new() { AccountId = payableId, CreditAmount = 40m }
                });
        }

        int journalsBefore;
        SupplierReconciliationReportDto report;
        await using (var ctx = _pg.NewContext())
        {
            journalsBefore = await ctx.Set<JournalEntry>().AsNoTracking().CountAsync();
            report = await new SupplierReconciliationService(ctx).GetAsync(DateTime.Today);
        }

        int journalsAfter;
        await using (var ctx = _pg.NewContext())
            journalsAfter = await ctx.Set<JournalEntry>().AsNoTracking().CountAsync();

        Assert.Equal(journalsBefore, journalsAfter);
        Assert.NotEqual(0m, report.UnattributedLedger);

        var row = report.Rows.Single(r => r.SupplierId == supplier);
        Assert.Equal(100m, row.SumResidual);       // متبقيات الفواتير لا تتأثر بالقيد اليدوي
        Assert.Equal(100m, row.SupplierBalance);
        Assert.Equal(0m, row.BalanceVsLedger);     // القيد اليدوي لا يمس F2
        Assert.Equal(40m, report.LedgerAccountBalance - report.TotalAttributedLedger);
        Assert.Equal(40m, report.UnattributedLedger);
    }
}
