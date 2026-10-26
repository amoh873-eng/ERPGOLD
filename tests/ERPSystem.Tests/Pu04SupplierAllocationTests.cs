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
/// PU-04 — اختبارات تكامل حية لتخصيص دفعات الموردين (D1 · D5 · D4 · D13-A · D13-B · H-1 · N-1 · N-3).
/// تعمل على قاعدة اختبار مؤقتة تُنشأ وتُحذف ذاتيًا (AccountingPgFixture) وتُطبَّق عليها الهجرات.
/// </summary>
public class Pu04SupplierAllocationTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Pu04SupplierAllocationTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "pu04-tester";
    }

    private sealed class DenyReverseAuth : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => false;
        public string? CurrentUserName() => "pu04-no-reverse";
    }

    private static PaymentVoucherService Vouchers(AppDbContext ctx, IVoucherAuthorization? auth = null)
        => new(ctx, new JournalEntryService(ctx), auth ?? new AllowAll());

    private static PaymentVoucherReversalService Reversals(AppDbContext ctx, IVoucherAuthorization? auth = null)
        => new(ctx, new JournalEntryService(ctx), auth ?? new AllowAll());

    private static PurchaseInvoiceService Purchases(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    private static PurchaseReturnService Returns(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx));

    private async Task<Guid> NewSupplierAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "مورد PU-04 " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    /// <summary>يُنشئ فاتورة مشتريات مرحّلة (آجلة افتراضيًا) بمبلغ محدد وبضريبة 0.</summary>
    private async Task<Guid> NewInvoiceAsync(Guid supplierId, decimal amount, bool cash = false)
    {
        await using var ctx = _pg.NewContext();
        var warehouseId = await ctx.Set<Warehouse>().Where(w => !w.IsDeleted).Select(w => w.Id).FirstAsync();
        var categoryId = await ctx.Set<Category>().Where(c => !c.IsDeleted).Select(c => c.Id).FirstAsync();
        var unitId = await ctx.Set<Unit>().Where(u => !u.IsDeleted).Select(u => u.Id).FirstAsync();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "PU04-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "صنف PU-04",
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

        var invoice = await Purchases(ctx).CreateAsync(new CreatePurchaseInvoiceDto
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
        return invoice.Id;
    }

    private async Task<Guid> DraftApprovedAsync(Guid supplierId, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        var svc = Vouchers(ctx);
        var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = supplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = 1,
            Amount = amount,
            Description = "PU-04"
        });
        await svc.ApproveAsync(draft.Id);
        return draft.Id;
    }

    private async Task<(decimal SupplierBalance, int Journals)> SnapAsync(Guid supplierId)
    {
        await using var ctx = _pg.NewContext();
        return (
            await ctx.Set<Supplier>().AsNoTracking().Where(s => s.Id == supplierId).Select(s => s.CurrentBalance).FirstAsync(),
            await ctx.Set<JournalEntry>().AsNoTracking().CountAsync());
    }

    private async Task<decimal> ResidualAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        var dto = await new SupplierAllocationQueries(ctx).GetResidualAsync(invoiceId);
        return dto?.Residual ?? 0m;
    }

    // ═══════════ 1) تخصيص كامل لفاتورة واحدة: قيد واحد + صف تخصيص واحد ═══════════
    [LivePgFact]
    public async Task Post_full_allocation_single_invoice_creates_allocation_without_extra_entry()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("FULL");
        var invoice = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);
        var before = await SnapAsync(supplier);

        await using (var ctx = _pg.NewContext())
        {
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 200m }
            });
        }

        var after = await SnapAsync(supplier);
        Assert.Equal(before.Journals + 1, after.Journals);
        Assert.Equal(0m, await ResidualAsync(invoice));
        Assert.Equal(0m, after.SupplierBalance);

        await using var check = _pg.NewContext();
        var rows = await check.Set<PaymentAllocation>().AsNoTracking()
            .Where(a => a.PaymentVoucherId == voucher).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Direction);
        Assert.Equal(200m, rows[0].Amount);
    }

    // ═══════════ 2) سند واحد لعدة فواتير لنفس المورد ═══════════
    [LivePgFact]
    public async Task Post_multi_invoice_distributes_exactly_and_keeps_remaining_residual()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("MULTI");
        var a = await NewInvoiceAsync(supplier, 100m);
        var b = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 150m);

        await using (var ctx = _pg.NewContext())
        {
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = a, Amount = 100m },
                new PaymentAllocationInput { PurchaseInvoiceId = b, Amount = 50m }
            });
        }

        Assert.Equal(0m, await ResidualAsync(a));
        Assert.Equal(150m, await ResidualAsync(b));
        Assert.Equal(150m, (await SnapAsync(supplier)).SupplierBalance);
    }

    // ═══════════ 3) تخصيص جزئي يترك رصيدًا مستحقًا ═══════════
    [LivePgFact]
    public async Task Post_partial_allocation_leaves_residual()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("PART");
        var invoice = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 60m);

        await using (var ctx = _pg.NewContext())
        {
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 60m }
            });
        }

        Assert.Equal(140m, await ResidualAsync(invoice));
        Assert.Equal(140m, (await SnapAsync(supplier)).SupplierBalance);
    }

    // ═══════════ 4) رفض مبلغ غير موزع (ΣA ≠ Amount) — D5 ═══════════
    [LivePgFact]
    public async Task Post_rejects_unallocated_amount()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("U");
        var invoice = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);

        await using var ctx = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 150m }
            }));
        Assert.Contains("غير موزع", ex.Message);
        Assert.Equal(200m, await ResidualAsync(invoice));
    }

    // ═══════════ 5) رفض فاتورة مورد آخر ═══════════
    [LivePgFact]
    public async Task Post_rejects_invoice_of_another_supplier()
    {
        if (!_pg.Available) return;
        var supplierA = await NewSupplierAsync("SA");
        var supplierB = await NewSupplierAsync("SB");
        await NewInvoiceAsync(supplierA, 200m);          // ذمّة كافية للمورد A ⇒ يعبر حارس PU-03
        var invoiceB = await NewInvoiceAsync(supplierB, 200m);
        var voucher = await DraftApprovedAsync(supplierA, 200m);

        await using var ctx = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoiceB, Amount = 200m }
            }));
        Assert.Contains("لا تخص المورد نفسه", ex.Message);
    }

    // ═══════════ 6) رفض فاتورة غير مؤهلة (مسودة) ═══════════
    [LivePgFact]
    public async Task Post_rejects_ineligible_draft_invoice()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("DRAFT");
        var invoice = await NewInvoiceAsync(supplier, 200m);

        await using (var seed = _pg.NewContext())
        {
            var inv = await seed.Set<PurchaseInvoice>().FirstAsync(i => i.Id == invoice);
            inv.Status = DocumentStatus.Draft;
            await seed.SaveChangesAsync();
        }

        var voucher = await DraftApprovedAsync(supplier, 200m);
        await using var ctx = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 200m }
            }));
        Assert.Contains("غير مؤهلة", ex.Message);
    }

    // ═══════════ 7) رفض تجاوز المتاح للفاتورة رغم كفاية رصيد المورد ═══════════
    [LivePgFact]
    public async Task Post_rejects_exceeding_invoice_available_even_if_supplier_balance_is_enough()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("CAP");
        var a = await NewInvoiceAsync(supplier, 100m);
        await NewInvoiceAsync(supplier, 100m);
        var voucher = await DraftApprovedAsync(supplier, 150m);

        await using var ctx = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = a, Amount = 150m }
            }));
        Assert.Contains("يتجاوز الرصيد المتاح", ex.Message);
    }

    // ═══════════ 8) D13-A: صفوف موجبة متعددة لنفس الزوج ═══════════
    [LivePgFact]
    public async Task Post_allows_multiple_positive_rows_for_same_pair()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("D13A");
        var invoice = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 100m);

        await using (var ctx = _pg.NewContext())
        {
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 40m },
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 60m }
            });
        }

        await using var check = _pg.NewContext();
        var rows = await check.Set<PaymentAllocation>().AsNoTracking()
            .Where(a => a.PaymentVoucherId == voucher && a.Direction == 1).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(100m, rows.Sum(r => r.Amount));
        Assert.Equal(100m, await ResidualAsync(invoice));
    }

    // ═══════════ 9) دقة التقريب: مجموع الأجزاء = مبلغ السند بالضبط (D8) ═══════════
    [LivePgFact]
    public async Task Post_rounding_third_split_sums_exactly()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("ROUND");
        var a = await NewInvoiceAsync(supplier, 100m);
        var b = await NewInvoiceAsync(supplier, 100m);
        var c = await NewInvoiceAsync(supplier, 100m);
        var voucher = await DraftApprovedAsync(supplier, 100m);

        await using (var ctx = _pg.NewContext())
        {
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = a, Amount = 33.33m },
                new PaymentAllocationInput { PurchaseInvoiceId = b, Amount = 33.33m },
                new PaymentAllocationInput { PurchaseInvoiceId = c, Amount = 33.34m }
            });
        }

        await using var check = _pg.NewContext();
        var total = await check.Set<PaymentAllocation>().AsNoTracking()
            .Where(x => x.PaymentVoucherId == voucher).SumAsync(x => (decimal?)x.Amount) ?? 0m;
        Assert.Equal(100m, total);
    }

    // ═══════════ 10) رفض الفاتورة النقدية (لا رصيد مستحق) ═══════════
    [LivePgFact]
    public async Task Post_rejects_cash_invoice()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();                       // الفاتورة النقدية تتطلب رصيد صندوق كافيًا
        var supplier = await NewSupplierAsync("CASH");
        await NewInvoiceAsync(supplier, 200m);           // ذمّة آجلة ⇒ يعبر حارس PU-03
        var cashInvoice = await NewInvoiceAsync(supplier, 200m, cash: true);
        var voucher = await DraftApprovedAsync(supplier, 200m);

        await using var ctx = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = cashInvoice, Amount = 200m }
            }));
        Assert.True(ex.Message.Contains("غير مؤهلة") || ex.Message.Contains("نقدية"));
    }

    // ═══════════ 11) إعادة الترحيل لا تُنشئ قيدًا أو تخصيصًا ثانيًا (N-1) ═══════════
    [LivePgFact]
    public async Task Post_twice_does_not_duplicate_entry_or_allocation()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("IDEM");
        var invoice = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);
        var allocations = new[] { new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 200m } };

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, allocations);

        var afterFirst = await SnapAsync(supplier);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, allocations);

        var afterSecond = await SnapAsync(supplier);
        Assert.Equal(afterFirst.Journals, afterSecond.Journals);
        Assert.Equal(afterFirst.SupplierBalance, afterSecond.SupplierBalance);

        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.Set<PaymentAllocation>().AsNoTracking()
            .CountAsync(a => a.PaymentVoucherId == voucher && a.Direction == 1));
    }

    private async Task<Guid> FirstAllocationIdAsync(Guid voucherId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<PaymentAllocation>().AsNoTracking()
            .Where(a => a.PaymentVoucherId == voucherId && a.Direction == 1)
            .Select(a => a.Id).FirstAsync();
    }

    // ═══════════ 12) العكس الكلي: قيد عكسي + استرجاع الأرصدة + حالة Reversed ═══════════
    [LivePgFact]
    public async Task Reverse_full_restores_balances_and_marks_voucher_reversed()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REV");
        var invoice = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 200m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);
        var beforeReverse = await SnapAsync(supplier);

        await using (var ctx = _pg.NewContext())
            await Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucher,
                VoucherDate = DateTime.Today,
                Reason = "اختبار عكس كلي",
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 200m }
                }
            });

        var afterReverse = await SnapAsync(supplier);
        Assert.Equal(beforeReverse.Journals + 1, afterReverse.Journals);
        Assert.Equal(200m, afterReverse.SupplierBalance);
        Assert.Equal(200m, await ResidualAsync(invoice));

        await using var check = _pg.NewContext();
        var v = await check.Set<PaymentVoucher>().AsNoTracking().FirstAsync(x => x.Id == voucher);
        Assert.Equal(VoucherStatus.Reversed, v.Status);
        Assert.Equal(1, await check.Set<PaymentVoucherReversal>().AsNoTracking()
            .CountAsync(r => r.OriginalPaymentVoucherId == voucher));
        Assert.Equal(1, await check.Set<PaymentAllocation>().AsNoTracking()
            .CountAsync(a => a.PaymentVoucherId == voucher && a.Direction == -1));
    }

    // ═══════════ 13) رفض العكس الجزئي (D5) ═══════════
    [LivePgFact]
    public async Task Reverse_rejects_partial_amount()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REVPART");
        var invoice = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 200m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);

        await using var ctx2 = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reversals(ctx2).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucher,
                VoucherDate = DateTime.Today,
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 60m }
                }
            }));
        Assert.Contains("العكس الجزئي", ex.Message);
    }

    // ═══════════ 14) إعادة التوزيع: تصحيح بحت بلا قيد مع ثبات ΣA ═══════════
    [LivePgFact]
    public async Task Reallocate_moves_amount_between_invoices_without_new_entry()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REALLOC");
        var a = await NewInvoiceAsync(supplier, 200m);
        var b = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = a, Amount = 200m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);
        var before = await SnapAsync(supplier);

        await using (var ctx = _pg.NewContext())
            await Reversals(ctx).ReallocateAsync(new ReallocateAllocationsDto
            {
                PaymentVoucherId = voucher,
                Reason = "تصحيح توزيع",
                FromAllocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 60m }
                },
                ToInvoices = new List<PaymentAllocationInput>
                {
                    new() { PurchaseInvoiceId = b, Amount = 60m }
                }
            });

        var after = await SnapAsync(supplier);
        Assert.Equal(before.Journals, after.Journals);
        Assert.Equal(before.SupplierBalance, after.SupplierBalance);
        Assert.Equal(60m, await ResidualAsync(a));
        Assert.Equal(140m, await ResidualAsync(b));
    }

    // ═══════════ 15) رفض السحب بأكثر من أصل الصف ═══════════
    [LivePgFact]
    public async Task Reallocate_rejects_amount_over_original()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("OVER");
        var a = await NewInvoiceAsync(supplier, 200m);
        var b = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = a, Amount = 200m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);

        await using (var ctx2 = _pg.NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Reversals(ctx2).ReallocateAsync(new ReallocateAllocationsDto
                {
                    PaymentVoucherId = voucher,
                    FromAllocations = new List<PaymentAllocationReversalInput>
                    {
                        new() { AllocationId = allocationId, Amount = 260m }
                    },
                    ToInvoices = new List<PaymentAllocationInput>
                    {
                        new() { PurchaseInvoiceId = b, Amount = 260m }
                    }
                }));
        }

        // لا صفوف جديدة (لا سحب ولا إضافة) بعد المحاولة المرفوضة، والتخصيص الأصلي كما هو.
        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.Set<PaymentAllocation>().AsNoTracking()
            .CountAsync(x => x.PaymentVoucherId == voucher));
        Assert.Equal(0m, await ResidualAsync(a));
    }

    // ═══════════ 16) صلاحية العكس المنفصلة (N-2) ═══════════
    [LivePgFact]
    public async Task Reverse_requires_reverse_permission()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("PERM");
        var invoice = await NewInvoiceAsync(supplier, 100m);
        var voucher = await DraftApprovedAsync(supplier, 100m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 100m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);

        await using var ctx2 = _pg.NewContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Reversals(ctx2, new DenyReverseAuth()).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucher,
                VoucherDate = DateTime.Today,
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 100m }
                }
            }));
    }

    // ═══════════ 17) إعادة التوزيع: رفض اختلاف المجموع (يحفظ ΣA) ═══════════
    [LivePgFact]
    public async Task Reallocate_rejects_mismatched_totals()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("MISMATCH");
        var a = await NewInvoiceAsync(supplier, 200m);
        var b = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = a, Amount = 200m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);

        await using var ctx2 = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reversals(ctx2).ReallocateAsync(new ReallocateAllocationsDto
            {
                PaymentVoucherId = voucher,
                FromAllocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 60m }
                },
                ToInvoices = new List<PaymentAllocationInput>
                {
                    new() { PurchaseInvoiceId = b, Amount = 50m }
                }
            }));
        Assert.Contains("يجب أن يساوي المبلغ المسحوب", ex.Message);
    }

    // ═══════════ 18) إعادة التوزيع تتطلب صلاحية العكس (N-2) ═══════════
    [LivePgFact]
    public async Task Reallocate_requires_reverse_permission()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REALLOCPERM");
        var a = await NewInvoiceAsync(supplier, 200m);
        var b = await NewInvoiceAsync(supplier, 200m);
        var voucher = await DraftApprovedAsync(supplier, 200m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = a, Amount = 200m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);

        await using var ctx2 = _pg.NewContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Reversals(ctx2, new DenyReverseAuth()).ReallocateAsync(new ReallocateAllocationsDto
            {
                PaymentVoucherId = voucher,
                FromAllocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 60m }
                },
                ToInvoices = new List<PaymentAllocationInput>
                {
                    new() { PurchaseInvoiceId = b, Amount = 60m }
                }
            }));
    }

    // ═══════════ 19) عكس كامل بنفس مفتاح الطلب مرتين ⇒ مستند عكس واحد (N-1) ═══════════
    [LivePgFact]
    public async Task Reverse_is_idempotent_with_same_key()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REVIDEM");
        var invoice = await NewInvoiceAsync(supplier, 120m);
        var voucher = await DraftApprovedAsync(supplier, 120m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 120m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);
        var key = Guid.NewGuid();

        CreatePaymentVoucherReversalDto Request() => new()
        {
            OriginalPaymentVoucherId = voucher,
            VoucherDate = DateTime.Today,
            IdempotencyKey = key,
            Allocations = new List<PaymentAllocationReversalInput>
            {
                new() { AllocationId = allocationId, Amount = 120m }
            }
        };

        await using (var ctx = _pg.NewContext())
            await Reversals(ctx).ReverseAsync(Request());

        var afterFirst = await SnapAsync(supplier);

        await using (var ctx = _pg.NewContext())
            await Reversals(ctx).ReverseAsync(Request());

        var afterSecond = await SnapAsync(supplier);
        Assert.Equal(afterFirst.Journals, afterSecond.Journals);
        Assert.Equal(afterFirst.SupplierBalance, afterSecond.SupplierBalance);

        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.Set<PaymentVoucherReversal>().AsNoTracking()
            .CountAsync(r => r.OriginalPaymentVoucherId == voucher));
        Assert.Equal(1, await check.Set<PaymentAllocation>().AsNoTracking()
            .CountAsync(a => a.PaymentVoucherId == voucher && a.Direction == -1));
    }

    // ═══════════ 20) عكس سند سبق عكسه (بمفتاح آخر) ⇒ مرفوض ═══════════
    [LivePgFact]
    public async Task Reverse_twice_without_key_is_rejected()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REVTWICE");
        var invoice = await NewInvoiceAsync(supplier, 90m);
        var voucher = await DraftApprovedAsync(supplier, 90m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 90m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);

        await using (var ctx = _pg.NewContext())
            await Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucher,
                VoucherDate = DateTime.Today,
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 90m }
                }
            });

        await using (var ctx2 = _pg.NewContext())
        {
            // المحاولة الثانية مرفوضة (السند صار Reversed) — الرسالة قد تكون «غير معكوس» أو «مستند عكس قائم».
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Reversals(ctx2).ReverseAsync(new CreatePaymentVoucherReversalDto
                {
                    OriginalPaymentVoucherId = voucher,
                    VoucherDate = DateTime.Today,
                    Allocations = new List<PaymentAllocationReversalInput>
                    {
                        new() { AllocationId = allocationId, Amount = 90m }
                    }
                }));
        }

        // ولا يوجد سوى مستند عكس واحد ولا صفوف عكس مكررة.
        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.Set<PaymentVoucherReversal>().AsNoTracking()
            .CountAsync(r => r.OriginalPaymentVoucherId == voucher));
        Assert.Equal(1, await check.Set<PaymentAllocation>().AsNoTracking()
            .CountAsync(a => a.PaymentVoucherId == voucher && a.Direction == -1));
    }

    // ═══════════ 21) رفض عكس تخصيص يتبع سندًا آخر ═══════════
    [LivePgFact]
    public async Task Reverse_rejects_allocation_from_another_voucher()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("FOREIGN");
        var a = await NewInvoiceAsync(supplier, 100m);
        var b = await NewInvoiceAsync(supplier, 100m);
        var voucherA = await DraftApprovedAsync(supplier, 100m);
        var voucherB = await DraftApprovedAsync(supplier, 100m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucherA, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = a, Amount = 100m }
            });
        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucherB, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = b, Amount = 100m }
            });

        var allocationOfA = await FirstAllocationIdAsync(voucherA);

        await using var ctx2 = _pg.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reversals(ctx2).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucherB,
                VoucherDate = DateTime.Today,
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationOfA, Amount = 100m }
                }
            }));
        Assert.Contains("لا يتبع السند الأصلي", ex.Message);
    }
}
