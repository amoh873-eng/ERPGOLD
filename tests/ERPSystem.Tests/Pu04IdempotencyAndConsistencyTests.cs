using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Vouchers;
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
/// PU-04 — اختبارات إصلاح العيوب المؤكدة (جلسة 2026-10-23):
///  • N-1/R-2: منع تكرار **إنشاء** السند (مفتاح ثابت + إرجاع السند الأصلي + سباق متزامن).
///  • P0-3: معالجة تعارض الفهرس الفريد (`23505`) في الترحيل والعكس دون إخفاء أخطاء أخرى.
///  • R-03: سلامة العكس الكامل ومنع التكرار والتراجع الذري عند الفشل.
///  • M-3: فحص اتساق التخصيصات (R3 §9.1) — قراءة فقط بأربعة أنواع.
/// تعمل على قاعدة اختبار مؤقتة تُنشأ وتُحذف ذاتيًا (AccountingPgFixture) وتُطبَّق عليها الهجرات.
/// </summary>
public class Pu04IdempotencyAndConsistencyTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Pu04IdempotencyAndConsistencyTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "pu04-fix-tester";
    }

    private static PaymentVoucherService Vouchers(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAll());

    private static PaymentVoucherReversalService Reversals(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAll());

    // ───────────────────────── بُناة البيانات ─────────────────────────

    private async Task<Guid> NewSupplierAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "مورد إصلاح " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    /// <summary>فاتورة مشتريات آجلة بمبلغ محدد (بضريبة 0 وكمية 1).</summary>
    private async Task<Guid> NewInvoiceAsync(Guid supplierId, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        var categoryId = await ctx.Set<Category>().Where(c => !c.IsDeleted).Select(c => c.Id).FirstAsync();
        var unitId = await ctx.Set<Unit>().Where(u => !u.IsDeleted).Select(u => u.Id).FirstAsync();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "FX-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "صنف إصلاح",
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

        var invoice = await new PurchaseInvoiceService(
                ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice())
            .CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = supplierId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                PaymentMethod = CreatePurchaseInvoiceDto.PaymentOnAccount,
                Lines = new List<CreatePurchaseInvoiceLineDto>
                {
                    new() { ItemId = item.Id, Quantity = 1m, UnitCost = amount }
                }
            });
        return invoice.Id;
    }

    private static CreatePaymentVoucherDto NewDraftDto(Guid supplierId, decimal amount, Guid? key)
        => new()
        {
            SupplierId = supplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = 1,
            Amount = amount,
            Description = "PU-04 fix",
            IdempotencyKey = key
        };

    /// <summary>مسودة معتمدة (بلا مفتاح تكرار) — للاختبارات التي لا تخص الإنشاء.</summary>
    private async Task<Guid> DraftApprovedAsync(Guid supplierId, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        var svc = Vouchers(ctx);
        var draft = await svc.CreateDraftAsync(NewDraftDto(supplierId, amount, null));
        await svc.ApproveAsync(draft.Id);
        return draft.Id;
    }

    private async Task<(decimal SupplierBalance, int Journals)> SnapAsync(Guid supplierId)
    {
        await using var ctx = _pg.NewContext();
        return (
            await ctx.Set<Supplier>().AsNoTracking().Where(s => s.Id == supplierId)
                .Select(s => s.CurrentBalance).FirstAsync(),
            await ctx.Set<JournalEntry>().AsNoTracking().CountAsync());
    }

    private async Task<int> VoucherCountByKeyAsync(Guid key)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<PaymentVoucher>().AsNoTracking()
            .CountAsync(v => v.IdempotencyKey == key);
    }

    private async Task<int> AllocationRowCountAsync(Guid voucherId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<PaymentAllocation>().AsNoTracking()
            .CountAsync(a => a.PaymentVoucherId == voucherId);
    }

    private async Task<VoucherStatus?> StatusAsync(Guid voucherId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<PaymentVoucher>().AsNoTracking()
            .Where(v => v.Id == voucherId).Select(v => (VoucherStatus?)v.Status).FirstOrDefaultAsync();
    }

    // ═══════════════════════ 1) منع تكرار الإنشاء (N-1 / R-2) ═══════════════════════

    [LivePgFact]
    public async Task CreateDraft_same_key_returns_same_voucher_without_duplicate()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("IDEM1");
        var key = Guid.NewGuid();

        PaymentVoucherDto first, second;
        await using (var ctx = _pg.NewContext())
            first = await Vouchers(ctx).CreateDraftAsync(NewDraftDto(supplier, 100m, key));
        await using (var ctx = _pg.NewContext())
            second = await Vouchers(ctx).CreateDraftAsync(NewDraftDto(supplier, 100m, key));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.VoucherNumber, second.VoucherNumber);
        Assert.Equal(1, await VoucherCountByKeyAsync(key));
        Assert.Equal(VoucherStatus.Draft, await StatusAsync(first.Id));
        Assert.Equal(0m, (await SnapAsync(supplier)).SupplierBalance); // المسودة بلا أثر محاسبي
    }

    [LivePgFact]
    public async Task CreateDraft_different_keys_create_two_intentional_vouchers()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("IDEM2");

        PaymentVoucherDto a, b;
        await using (var ctx = _pg.NewContext())
            a = await Vouchers(ctx).CreateDraftAsync(NewDraftDto(supplier, 100m, Guid.NewGuid()));
        await using (var ctx = _pg.NewContext())
            b = await Vouchers(ctx).CreateDraftAsync(NewDraftDto(supplier, 100m, Guid.NewGuid()));

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.VoucherNumber, b.VoucherNumber);
    }

    [LivePgFact]
    public async Task CreateDraft_concurrent_same_key_creates_one_voucher()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("IDEM3");
        var key = Guid.NewGuid();

        async Task<PaymentVoucherDto> CreateOnceAsync()
        {
            await using var ctx = _pg.NewContext();
            return await Vouchers(ctx).CreateDraftAsync(NewDraftDto(supplier, 120m, key));
        }

        var results = await Task.WhenAll(CreateOnceAsync(), CreateOnceAsync());

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(1, await VoucherCountByKeyAsync(key));
    }

    // ═══════════════════════ 2) وحدة الترحيل والتراجع ═══════════════════════

    [LivePgFact]
    public async Task Post_with_same_key_twice_returns_same_voucher_and_single_entry()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("POST1");
        var invoice = await NewInvoiceAsync(supplier, 100m);
        var key = Guid.NewGuid();

        Guid voucherId;
        Guid? entryId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Vouchers(ctx);
            voucherId = (await svc.CreateDraftAsync(NewDraftDto(supplier, 100m, key))).Id;
            await svc.ApproveAsync(voucherId);
            var posted = await svc.PostAsync(voucherId, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 100m }
            }, key);
            entryId = posted.JournalEntryId;
        }

        var before = await SnapAsync(supplier);
        await using (var ctx = _pg.NewContext())
        {
            var again = await Vouchers(ctx).PostAsync(voucherId, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 100m }
            }, key);
            Assert.Equal(voucherId, again.Id);
            Assert.Equal(entryId, again.JournalEntryId);
            Assert.Equal((int)VoucherStatus.Posted, again.Status);
        }
        var after = await SnapAsync(supplier);

        Assert.Equal(before.Journals, after.Journals);               // لا قيد ثانٍ
        Assert.Equal(before.SupplierBalance, after.SupplierBalance);  // لا خصم مزدوج
        Assert.Equal(1, await AllocationRowCountAsync(voucherId));    // لا صف تخصيص مكرر
    }

    [LivePgFact]
    public async Task Post_with_key_of_another_voucher_is_rejected_with_clear_error()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("POST2");
        var invA = await NewInvoiceAsync(supplier, 50m);
        var invB = await NewInvoiceAsync(supplier, 50m);
        var key = Guid.NewGuid();

        Guid voucherB;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Vouchers(ctx);
            var a = (await svc.CreateDraftAsync(NewDraftDto(supplier, 50m, key))).Id;
            await svc.ApproveAsync(a);
            await svc.PostAsync(a, new[] { new PaymentAllocationInput { PurchaseInvoiceId = invA, Amount = 50m } }, key);

            // سند B **بلا مفتاح** ⇒ محاولة ترحيله بمفتاح A تُصطدم بالفهرس الفريد ⇒ يجب ألا تُبتلع.
            voucherB = (await svc.CreateDraftAsync(NewDraftDto(supplier, 50m, null))).Id;
            await svc.ApproveAsync(voucherB);
        }

        var before = await SnapAsync(supplier);
        await using (var ctx = _pg.NewContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Vouchers(ctx).PostAsync(voucherB, new[]
                {
                    new PaymentAllocationInput { PurchaseInvoiceId = invB, Amount = 50m }
                }, key));
            Assert.Contains("سند آخر", ex.Message);
        }
        var after = await SnapAsync(supplier);

        Assert.Equal(before.Journals, after.Journals);
        Assert.Equal(before.SupplierBalance, after.SupplierBalance);
        Assert.Equal(VoucherStatus.Approved, await StatusAsync(voucherB));
        Assert.Equal(0, await AllocationRowCountAsync(voucherB));
    }

    [LivePgFact]
    public async Task Post_with_conflicting_key_on_same_voucher_is_rejected()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("POST4");
        var invoice = await NewInvoiceAsync(supplier, 30m);
        var createKey = Guid.NewGuid();

        Guid voucher;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Vouchers(ctx);
            // المفتاح مُثبَّت عند الإنشاء ⇒ أي ترحيل بمفتاح مختلف يُرفض (لا تبديل للمفتاح).
            voucher = (await svc.CreateDraftAsync(NewDraftDto(supplier, 30m, createKey))).Id;
            await svc.ApproveAsync(voucher);
        }

        var before = await SnapAsync(supplier);
        await using (var ctx = _pg.NewContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Vouchers(ctx).PostAsync(voucher, new[]
                {
                    new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 30m }
                }, Guid.NewGuid()));
            Assert.Contains("لا يطابق", ex.Message);
        }
        var after = await SnapAsync(supplier);

        Assert.Equal(before, after);                        // لا قيد ولا تغيير رصيد
        Assert.Equal(0, await AllocationRowCountAsync(voucher));
        Assert.Equal(VoucherStatus.Approved, await StatusAsync(voucher));

        // الترحيل بالمفتاح الأصلي (نفس طلب الإنشاء) ينجح وينشئ قيدًا واحدًا.
        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 30m }
            }, createKey);

        Assert.Equal(VoucherStatus.Posted, await StatusAsync(voucher));
        Assert.Equal(1, await AllocationRowCountAsync(voucher));
        Assert.Equal(0m, (await SnapAsync(supplier)).SupplierBalance);
    }

    [LivePgFact]
    public async Task Post_rejection_after_checks_leaves_no_partial_changes()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("POST3");
        var bigInvoice = await NewInvoiceAsync(supplier, 200m);   // يرفع رصيد المورد إلى 200
        var smallInvoice = await NewInvoiceAsync(supplier, 50m);  // متاحه 50 فقط
        var voucher = await DraftApprovedAsync(supplier, 100m);   // ΣA = Amount، وحارس المورد يمرّ (100 ≤ 200)

        var before = await SnapAsync(supplier);
        await using (var ctx = _pg.NewContext())
        {
            // يمرّ فحص المجموع وحارس المورد، ويُرفض على حارس **الفاتورة** داخل المعاملة ⇒ تراجع كامل.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Vouchers(ctx).PostAsync(voucher, new[]
                {
                    new PaymentAllocationInput { PurchaseInvoiceId = smallInvoice, Amount = 100m }
                }));
        }
        var after = await SnapAsync(supplier);

        Assert.Equal(before, after);                     // لا قيد ولا تغيير رصيد
        Assert.Equal(0, await AllocationRowCountAsync(voucher));
        Assert.Equal(VoucherStatus.Approved, await StatusAsync(voucher));
        await using var check = _pg.NewContext();
        Assert.Null(await check.Set<PaymentVoucher>().AsNoTracking()
            .Where(v => v.Id == voucher).Select(v => v.JournalEntryId).FirstAsync());

        // الفاتورتان لم يمسّهما الترحيل المرفوض.
        var residualQueries = new SupplierAllocationQueries(check);
        Assert.Equal(200m, (await residualQueries.GetResidualAsync(bigInvoice))!.Residual);
        Assert.Equal(50m, (await residualQueries.GetResidualAsync(smallInvoice))!.Residual);
    }

    // ═══════════════════════ 3) العكس الكامل ومنع التكرار (R-03) ═══════════════════════

    private async Task<Guid> FirstAllocationIdAsync(Guid voucherId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<PaymentAllocation>().AsNoTracking()
            .Where(a => a.PaymentVoucherId == voucherId && a.Direction == 1)
            .Select(a => a.Id).FirstAsync();
    }

    [LivePgFact]
    public async Task Reverse_concurrent_same_key_creates_one_reversal()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REV1");
        var invoice = await NewInvoiceAsync(supplier, 80m);
        var voucher = await DraftApprovedAsync(supplier, 80m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 80m }
            });

        var allocationId = await FirstAllocationIdAsync(voucher);
        var key = Guid.NewGuid();

        CreatePaymentVoucherReversalDto NewReversalDto() => new()
        {
            OriginalPaymentVoucherId = voucher,
            VoucherDate = DateTime.Today,
            IdempotencyKey = key,
            Allocations = new List<PaymentAllocationReversalInput>
            {
                new() { AllocationId = allocationId, Amount = 80m }
            }
        };

        async Task<PaymentVoucherReversalDto> ReverseOnceAsync()
        {
            await using var ctx = _pg.NewContext();
            return await Reversals(ctx).ReverseAsync(NewReversalDto());
        }

        var results = await Task.WhenAll(ReverseOnceAsync(), ReverseOnceAsync());

        Assert.Equal(results[0].Id, results[1].Id);

        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.Set<PaymentVoucherReversal>().AsNoTracking()
            .CountAsync(r => r.OriginalPaymentVoucherId == voucher));
        Assert.Equal(1, await check.Set<PaymentAllocation>().AsNoTracking()
            .CountAsync(a => a.PaymentVoucherId == voucher && a.Direction == -1));
        Assert.Equal(VoucherStatus.Reversed, await StatusAsync(voucher));
        Assert.Equal(80m, (await SnapAsync(supplier)).SupplierBalance);   // الرصيد عاد مرة واحدة فقط
    }

    [LivePgFact]
    public async Task Reverse_failure_leaves_no_partial_changes()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("REV2");
        var invA = await NewInvoiceAsync(supplier, 30m);
        var invB = await NewInvoiceAsync(supplier, 40m);
        var voucherA = await DraftApprovedAsync(supplier, 30m);
        var voucherB = await DraftApprovedAsync(supplier, 40m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucherA, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invA, Amount = 30m }
            });
        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucherB, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invB, Amount = 40m }
            });

        var allocationOfA = await FirstAllocationIdAsync(voucherA);

        var before = await SnapAsync(supplier);
        int allocationsBefore, reversalsBefore;
        await using (var ctx = _pg.NewContext())
        {
            allocationsBefore = await ctx.Set<PaymentAllocation>().AsNoTracking().CountAsync();
            reversalsBefore = await ctx.Set<PaymentVoucherReversal>().AsNoTracking().CountAsync();
        }

        await using (var ctx = _pg.NewContext())
        {
            // عكس سند B بمبلغ صف يتبع سند A ⇒ يجب الرفض قبل أي تغيير.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
                {
                    OriginalPaymentVoucherId = voucherB,
                    VoucherDate = DateTime.Today,
                    Allocations = new List<PaymentAllocationReversalInput>
                    {
                        new() { AllocationId = allocationOfA, Amount = 30m }
                    }
                }));
            Assert.Contains("لا يتبع السند الأصلي", ex.Message);
        }

        var after = await SnapAsync(supplier);
        Assert.Equal(before, after);                                     // لا قيد ولا تغيير رصيد

        await using var check = _pg.NewContext();
        Assert.Equal(allocationsBefore, await check.Set<PaymentAllocation>().AsNoTracking().CountAsync());
        Assert.Equal(reversalsBefore, await check.Set<PaymentVoucherReversal>().AsNoTracking().CountAsync());
        Assert.Equal(VoucherStatus.Posted, await StatusAsync(voucherB));
    }

    // ═══════════════════════ 4) فحص اتساق التخصيص (M-3) ═══════════════════════

    private async Task<List<AllocationAnomalyDto>> AnomaliesAsync()
    {
        await using var ctx = _pg.NewContext();
        return await new SupplierAllocationQueries(ctx).GetConsistencyAnomaliesAsync();
    }

    private async Task<Guid> PostFullAsync(Guid supplierId, decimal amount)
    {
        var invoice = await NewInvoiceAsync(supplierId, amount);
        var voucher = await DraftApprovedAsync(supplierId, amount);
        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = amount }
            });
        return voucher;
    }

    [LivePgFact]
    public async Task Consistency_reports_clean_state_after_normal_post()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("M3OK");
        var invoice = await NewInvoiceAsync(supplier, 60m);
        var voucher = await DraftApprovedAsync(supplier, 60m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 60m }
            });

        var anomalies = await AnomaliesAsync();

        Assert.DoesNotContain(anomalies, a => a.PaymentVoucherId == voucher);
        Assert.DoesNotContain(anomalies, a => a.PurchaseInvoiceId == invoice);
    }

    [LivePgFact]
    public async Task Consistency_detects_sum_mismatch()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("M3SUM");
        var voucher = await PostFullAsync(supplier, 60m);

        // محاكاة بيانات شاذة (صف تخصيص زائد أُدخل خارج الخدمة) — لا يُنشئ قيدًا محاسبيًا.
        await using (var ctx = _pg.NewContext())
        {
            var existing = await ctx.Set<PaymentAllocation>().AsNoTracking()
                .FirstAsync(a => a.PaymentVoucherId == voucher && a.Direction == 1);
            ctx.Add(new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                PaymentVoucherId = voucher,
                PurchaseInvoiceId = existing.PurchaseInvoiceId,
                SupplierId = supplier,
                Amount = 5m,
                Direction = 1,
                AllocatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var rows = (await AnomaliesAsync())
            .Where(a => a.PaymentVoucherId == voucher && a.Kind == AllocationAnomalyKinds.SumMismatch)
            .ToList();

        Assert.Single(rows);
        Assert.Equal(65m, rows[0].Actual);
        Assert.Equal(60m, rows[0].Expected);
    }

    [LivePgFact]
    public async Task Consistency_detects_exceeds_available()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("M3EXC");
        var invoice = await NewInvoiceAsync(supplier, 100m);
        var voucher = await DraftApprovedAsync(supplier, 60m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 60m }
            });

        await using (var ctx = _pg.NewContext())
        {
            ctx.Add(new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                PaymentVoucherId = voucher,
                PurchaseInvoiceId = invoice,
                SupplierId = supplier,
                Amount = 100m,
                Direction = 1,
                AllocatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var rows = (await AnomaliesAsync())
            .Where(a => a.PurchaseInvoiceId == invoice && a.Kind == AllocationAnomalyKinds.ExceedsAvailable)
            .ToList();

        Assert.Single(rows);
        Assert.Equal(160m, rows[0].Actual);   // 60 (مخصَّص صحيحًا) + 100 (صف شاذ)
        Assert.Equal(100m, rows[0].Expected); // المتاح قبل التخصيص = 100 (لا مردودات)
    }

    [LivePgFact]
    public async Task Consistency_detects_soft_deleted_invoice()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("M3DELI");
        var invoice = await NewInvoiceAsync(supplier, 25m);
        var voucher = await DraftApprovedAsync(supplier, 25m);

        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucher, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 25m }
            });

        await using (var ctx = _pg.NewContext())
        {
            var inv = await ctx.Set<PurchaseInvoice>().FirstAsync(i => i.Id == invoice);
            inv.IsDeleted = true;                      // محاكاة «فاتورة محذوفة منطقيًا»
            await ctx.SaveChangesAsync();
        }

        var rows = (await AnomaliesAsync())
            .Where(a => a.PurchaseInvoiceId == invoice && a.Kind == AllocationAnomalyKinds.MissingInvoice)
            .ToList();

        Assert.NotEmpty(rows);
    }

    [LivePgFact]
    public async Task Consistency_detects_soft_deleted_voucher()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("M3DELV");
        var voucher = await PostFullAsync(supplier, 35m);

        await using (var ctx = _pg.NewContext())
        {
            var v = await ctx.Set<PaymentVoucher>().FirstAsync(x => x.Id == voucher);
            v.IsDeleted = true;                        // محاكاة «سند محذوف منطقيًا»
            await ctx.SaveChangesAsync();
        }

        var rows = (await AnomaliesAsync())
            .Where(a => a.PaymentVoucherId == voucher && a.Kind == AllocationAnomalyKinds.MissingVoucher)
            .ToList();

        Assert.NotEmpty(rows);
    }

    [LivePgFact]
    public async Task Consistency_check_is_read_only()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("M3RO");
        await PostFullAsync(supplier, 45m);

        var before = await SnapAsync(supplier);
        int allocationsBefore, reversalsBefore;
        await using (var ctx = _pg.NewContext())
        {
            allocationsBefore = await ctx.Set<PaymentAllocation>().AsNoTracking().CountAsync();
            reversalsBefore = await ctx.Set<PaymentVoucherReversal>().AsNoTracking().CountAsync();
        }

        await AnomaliesAsync();
        await AnomaliesAsync();   // مرتان: للتأكد من عدم وجود أثر تراكمي

        var after = await SnapAsync(supplier);
        Assert.Equal(before, after);

        await using var check = _pg.NewContext();
        Assert.Equal(allocationsBefore, await check.Set<PaymentAllocation>().AsNoTracking().CountAsync());
        Assert.Equal(reversalsBefore, await check.Set<PaymentVoucherReversal>().AsNoTracking().CountAsync());
    }

    [LivePgFact]
    public async Task Concurrent_post_on_same_invoice_keeps_consistency()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("CONC1");
        var invoice = await NewInvoiceAsync(supplier, 100m);
        var voucherA = await DraftApprovedAsync(supplier, 80m);
        var voucherB = await DraftApprovedAsync(supplier, 80m);

        async Task<bool> TryPostAsync(Guid voucher)
        {
            await using var ctx = _pg.NewContext();
            try
            {
                await Vouchers(ctx).PostAsync(voucher, new[]
                {
                    new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 80m }
                });
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;   // تجاوز المتاح ⇒ رفض نظيف بلا أثر
            }
        }

        var results = await Task.WhenAll(TryPostAsync(voucherA), TryPostAsync(voucherB));

        Assert.Equal(1, results.Count(ok => ok));   // 80 + 80 > 100 ⇒ واحد فقط ينجح

        await using (var ctx = _pg.NewContext())
        {
            var residual = await new SupplierAllocationQueries(ctx).GetResidualAsync(invoice);
            Assert.NotNull(residual);
            Assert.True(residual!.Residual >= 0m, "المتبقي يجب ألا يصبح سالبًا بعد التزامن");
        }

        var anomalies = await AnomaliesAsync();
        Assert.DoesNotContain(anomalies, a => a.PurchaseInvoiceId == invoice);
        Assert.DoesNotContain(anomalies, a => a.PaymentVoucherId == voucherA);
        Assert.DoesNotContain(anomalies, a => a.PaymentVoucherId == voucherB);
    }

    private async Task<decimal> ResidualAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        return (await new SupplierAllocationQueries(ctx).GetResidualAsync(invoiceId))?.Residual ?? 0m;
    }

    /// <summary>
    /// انحدار 2026-10-23: ثلاثة ترحيلات متزامنة لنفس السند (بلا مفتاح ⇒ الحماية بالقفل لا بالمفتاح)
    /// يجب أن تُطبَّق **مرة واحدة**: قيد واحد، خصم واحد، صف تخصيص واحد.
    /// (كان هذا السباق قائمًا: قراءة الحالة قبل قفل الصف تسمح بترحيل مزدوج.)
    /// </summary>
    [LivePgFact]
    public async Task Concurrent_posts_of_same_voucher_apply_once()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("CONC2");
        var invoice = await NewInvoiceAsync(supplier, 70m);
        var voucher = await DraftApprovedAsync(supplier, 70m);

        var before = await SnapAsync(supplier);          // رصيد المورد وعدد القيود قبل الترحيل

        async Task PostOnceAsync()
        {
            await using var ctx = _pg.NewContext();
            try
            {
                await Vouchers(ctx).PostAsync(voucher, new[]
                {
                    new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 70m }
                });
            }
            catch (InvalidOperationException)
            {
                // الطلب الخاسر مسموح أن يُرفض — المهم ألا يتكرر الأثر.
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PostOnceAsync()));

        var after = await SnapAsync(supplier);
        Assert.Equal(70m, before.SupplierBalance - after.SupplierBalance);   // خصم واحد فقط
        Assert.Equal(before.Journals + 1, after.Journals);                    // قيد واحد فقط
        Assert.Equal(1, await AllocationRowCountAsync(voucher));              // صف تخصيص واحد فقط
        Assert.Equal(0m, await ResidualAsync(invoice));                       // المتاح استُهلك مرة واحدة
        Assert.Equal(VoucherStatus.Posted, await StatusAsync(voucher));
    }

    // ═══════════════════════ 5) إثبات القفل ودورة القبول ═══════════════════════

    /// <summary>
    /// إثبات أن قفل صف السند (`FOR UPDATE`) يُنفَّذ على **نفس اتصال ومعاملة** الترحيل:
    /// جلسة A تفتح معاملة وتنفّذ نفس جملة القفل المستخدمة في `PostCoreAsync`، ثم تحاول جلسة B
    /// قفل نفس الصف بـ`NOWAIT` ⇒ يجب أن تفشل بـ`55P03` (lock_not_available)، وبعد تحرير المعاملة
    /// تنجح ⇒ القفل مرتبط بالمعاملة/الاتصال لا بالجملة وحدها.
    /// </summary>
    [LivePgFact]
    public async Task Voucher_row_lock_is_held_on_same_connection_transaction()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("LOCK1");
        var voucher = await DraftApprovedAsync(supplier, 10m);

        await using var sessionA = _pg.NewContext();
        await using var tx = await sessionA.Database.BeginTransactionAsync();
        await sessionA.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PaymentVouchers\" WHERE \"Id\" = {voucher} FOR UPDATE");

        await using (var sessionB = _pg.NewContext())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                sessionB.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"PaymentVouchers\" WHERE \"Id\" = {voucher} FOR UPDATE NOWAIT"));
            Assert.Equal("55P03", ex.SqlState);   // lock_not_available ⇒ الصف مقفول من جلسة أخرى
        }

        await tx.RollbackAsync();

        // بعد تحرير المعاملة: القفل يُمنح لجلسة جديدة (تُثبته بحجب جلسة رابعة) ⇒ ما مُنع سابقًا كان القفل لا الجملة.
        await using var sessionC = _pg.NewContext();
        await using var txC = await sessionC.Database.BeginTransactionAsync();
        await sessionC.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PaymentVouchers\" WHERE \"Id\" = {voucher} FOR UPDATE");

        await using (var sessionD = _pg.NewContext())
        {
            var ex2 = await Assert.ThrowsAsync<PostgresException>(() =>
                sessionD.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"PaymentVouchers\" WHERE \"Id\" = {voucher} FOR UPDATE NOWAIT"));
            Assert.Equal("55P03", ex2.SqlState);
        }

        await txC.RollbackAsync();
    }

    /// <summary>
    /// دورة قبول كاملة على بيانات اختبار (بديل E2E لعدم توفر متصفح/سلسلة اتصال تطبيق):
    /// إنشاء بمفتاح ← اعتماد ← تخصيص كامل ← ترحيل ← إعادة إرسال الإنشاء والترحيل ←
    /// عكس كامل مرة واحدة ← رفض العكس الثاني ← فحص M-3.
    /// وتُثبت عند كل خطوة: عدد القيود، ورصيد المورد، ومتبقي الفاتورة، وصفوف التخصيص.
    /// </summary>
    [LivePgFact]
    public async Task Acceptance_cycle_create_allocate_post_resend_reverse()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("ACC1");
        var invoice = await NewInvoiceAsync(supplier, 150m);
        var key = Guid.NewGuid();

        var before = await SnapAsync(supplier);          // رصيد المورد = 150 · عدد القيود = N

        // (1) إنشاء بمفتاح + اعتماد ⇒ لا أثر محاسبي
        Guid voucherId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Vouchers(ctx);
            voucherId = (await svc.CreateDraftAsync(NewDraftDto(supplier, 150m, key))).Id;
            await svc.ApproveAsync(voucherId);
        }
        Assert.Equal(1, await VoucherCountByKeyAsync(key));
        Assert.Equal(before, await SnapAsync(supplier));

        // (2) تخصيص كامل + ترحيل ⇒ قيد واحد وخصم واحد
        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucherId, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 150m }
            });

        var afterPost = await SnapAsync(supplier);
        Assert.Equal(150m, before.SupplierBalance - afterPost.SupplierBalance);
        Assert.Equal(before.Journals + 1, afterPost.Journals);
        Assert.Equal(0m, await ResidualAsync(invoice));
        Assert.Equal(1, await AllocationRowCountAsync(voucherId));
        Assert.Equal(VoucherStatus.Posted, await StatusAsync(voucherId));

        // (3) إعادة إرسال طلب الإنشاء نفسه (نفس المفتاح) ⇒ نفس السند بلا سند ثانٍ
        await using (var ctx = _pg.NewContext())
        {
            var resent = await Vouchers(ctx).CreateDraftAsync(NewDraftDto(supplier, 150m, key));
            Assert.Equal(voucherId, resent.Id);
        }
        Assert.Equal(1, await VoucherCountByKeyAsync(key));

        // (4) إعادة إرسال طلب الترحيل ⇒ لا قيد ولا تخصيص إضافي
        await using (var ctx = _pg.NewContext())
            await Vouchers(ctx).PostAsync(voucherId, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 150m }
            });
        Assert.Equal(afterPost, await SnapAsync(supplier));
        Assert.Equal(1, await AllocationRowCountAsync(voucherId));

        // (5) عكس كامل مرة واحدة ⇒ إرجاع الأرصدة + قيد عكسي واحد
        var allocationId = await FirstAllocationIdAsync(voucherId);
        await using (var ctx = _pg.NewContext())
            await Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucherId,
                VoucherDate = DateTime.Today,
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 150m }
                }
            });

        var afterReverse = await SnapAsync(supplier);
        Assert.Equal(before.SupplierBalance, afterReverse.SupplierBalance);   // عاد كاملًا
        Assert.Equal(before.Journals + 2, afterReverse.Journals);             // قيد السند + القيد العكسي
        Assert.Equal(150m, await ResidualAsync(invoice));                     // المتاح عاد
        Assert.Equal(VoucherStatus.Reversed, await StatusAsync(voucherId));
        await using (var ctx = _pg.NewContext())
        {
            Assert.Equal(1, await ctx.Set<PaymentVoucherReversal>().AsNoTracking()
                .CountAsync(r => r.OriginalPaymentVoucherId == voucherId));
            Assert.Equal(1, await ctx.Set<PaymentAllocation>().AsNoTracking()
                .CountAsync(a => a.PaymentVoucherId == voucherId && a.Direction == -1));
        }

        // (6) عكس ثانٍ ⇒ مرفوض بلا أثر
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
                {
                    OriginalPaymentVoucherId = voucherId,
                    VoucherDate = DateTime.Today,
                    Allocations = new List<PaymentAllocationReversalInput>
                    {
                        new() { AllocationId = allocationId, Amount = 150m }
                    }
                }));
        }
        Assert.Equal(afterReverse, await SnapAsync(supplier));

        // (7) M-3: لا شذوذ على مستندات هذه الدورة
        var anomalies = await AnomaliesAsync();
        Assert.DoesNotContain(anomalies, a => a.PaymentVoucherId == voucherId);
        Assert.DoesNotContain(anomalies, a => a.PurchaseInvoiceId == invoice);
    }
}

