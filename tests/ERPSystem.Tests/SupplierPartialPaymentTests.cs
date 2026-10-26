using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// PU-02 — تدقيق سند الصرف الجزئي للمورد بأرقام صريحة.
/// المسار الفعلي: CreateDraftAsync → ApproveAsync → PostAsync
/// (‏PostAsync: قيد مدين 2200 المورد / دائن الصندوق 1100 أو البنك 1101، ثم Supplier.CurrentBalance -= Amount).
/// قاعدة معزولة فقط (AccountingPgFixture).
/// </summary>
public sealed class SupplierPartialPaymentTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _fx;
    public SupplierPartialPaymentTests(AccountingPgFixture fx) => _fx = fx;

    private sealed class AllowAllVouchers : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public string CurrentUserName() => "pu02-tester";
    }

    private static PaymentVoucherService Vouchers(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    private static PurchaseInvoiceService Purchases(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    /// <summary>(رصيد المورد المخزَّن، رصيد 2200 من الأستاذ، رصيد الصندوق 1100 من الأستاذ، عدد سندات الصرف المُرحَّلة)</summary>
    private async Task<(decimal sup, decimal payable, decimal cash, int posted)> SnapAsync()
    {
        await using var ctx = _fx.NewContext();
        var cash = await PostingAccountResolver.ResolveAsync(ctx, "1100");
        var payable = await PostingAccountResolver.ResolveAsync(ctx, "2200");
        return (
            await ctx.Set<Supplier>().AsNoTracking().Where(s => s.Id == _fx.SupplierId).Select(s => s.CurrentBalance).FirstAsync(),
            await ctx.Set<Account>().AsNoTracking().Where(a => a.Id == payable.Id).Select(a => a.CurrentBalance).FirstAsync(),
            await ctx.Set<Account>().AsNoTracking().Where(a => a.Id == cash.Id).Select(a => a.CurrentBalance).FirstAsync(),
            await ctx.Set<PaymentVoucher>().AsNoTracking().CountAsync(v => v.Status == ERPSystem.Domain.Enums.VoucherStatus.Posted));
    }

    private async Task<(Guid id, string number)> NewApprovedVoucherAsync(decimal amount, int method = 1)
    {
        await using var ctx = _fx.NewContext();
        var vs = Vouchers(ctx);
        var d = await vs.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = _fx.SupplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = method,
            Amount = amount,
            Description = "PU-02 سند صرف اختباري"
        });
        await vs.ApproveAsync(d.Id);
        return (d.Id, d.VoucherNumber);
    }

    // ═══ الاختبار الأساسي: فاتورة 200 آجلة + سند جزئي 60 ⇒ مستحق 140 ═══
    [LivePgFact]
    public async Task PU02_Partial_60_of_200_leaves_140()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var item = await _fx.CreateItemAsync("A5-PU02-A", 5m, 20m, 0m);

        // (١) فاتورة آجلة 10 × 20.00 = 200.00 ودون أي سداد
        decimal total, paid;
        await using (var ctx = _fx.NewContext())
        {
            var dto = _fx.NewPurchase(item, 10m, 20m, cash: false);
            dto.IdempotencyKey = Guid.NewGuid();
            var inv = await Purchases(ctx).CreateAsync(dto);
            total = inv.TotalAmount;
            paid = inv.PaidAmount;
        }
        Assert.Equal(200m, total);
        Assert.Equal(0m, paid);

        var before = await SnapAsync();
        var supBefore = before.sup;   // القاعدة مشتركة بين اختبارات الفئة ⇒ نقيس بالفروق لا بالقيم المطلقة

        // (٢) سند صرف جزئي 60.00 نقدًا
        Guid jeId;
        await using (var ctx = _fx.NewContext())
        {
            var vs = Vouchers(ctx);
            var d = await vs.CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = _fx.SupplierId,
                VoucherDate = DateTime.Today,
                PaymentMethod = 1,      // نقدي ⇒ الصندوق 1100
                Amount = 60m,
                Description = "PU-02 جزئي 60"
            });
            await vs.ApproveAsync(d.Id);
            var posted = await vs.PostAsync(d.Id);
            Assert.Equal((int)ERPSystem.Domain.Enums.VoucherStatus.Posted, posted.Status);
            jeId = posted.JournalEntryId!.Value;
        }

        var after = await SnapAsync();

        Assert.Equal(supBefore - 60m, after.sup);               // (٢) انخفض 60 فقط
        Assert.Equal(60m, before.sup - after.sup);              // (٣) المسدَّد 60.00 فقط
        Assert.Equal(before.cash - 60m, after.cash);             // (٤) الصندوق انخفض 60 مرة واحدة
        Assert.Equal(before.payable - 60m, after.payable);       // (٥) الرقابي 2200 انخفض 60
        Assert.Equal(after.sup, after.payable);                  // (٧) دفتر المورد == الأستاذ (كلاهما 140)
        Assert.Equal(before.posted + 1, after.posted);           // سند مُرحَّل واحد فقط

        // (٦) القيد: مدين 2200 = 60 · دائن الصندوق = 60 · متوازن
        await using (var ctx = _fx.NewContext())
        {
            var e = await ctx.Set<JournalEntry>().AsNoTracking()
                .Include(x => x.Lines).ThenInclude(l => l.Account)
                .FirstAsync(x => x.Id == jeId);

            Assert.Equal(60m, e.Lines.Sum(l => l.DebitAmount));
            Assert.Equal(e.Lines.Sum(l => l.DebitAmount), e.Lines.Sum(l => l.CreditAmount));
            Assert.Equal("2200", e.Lines.Single(l => l.DebitAmount > 0).Account!.Code);
            Assert.Equal(2, e.Lines.Count);
        }
    }
    // ═══ إعادة ترحيل السند نفسه ⇒ لا قيد إضافي ولا خصم ثانٍ ═══
    [LivePgFact]
    public async Task PU02_Reposting_same_voucher_has_no_extra_effect()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var item = await _fx.CreateItemAsync("A5-PU02-B", 5m, 30m, 0m);
        await using (var ctx = _fx.NewContext())
        {
            var dto = _fx.NewPurchase(item, 10m, 30m, cash: false);   // 300 آجلة
            dto.IdempotencyKey = Guid.NewGuid();
            await Purchases(ctx).CreateAsync(dto);
        }

        var (id, _) = await NewApprovedVoucherAsync(50m);
        var before = await SnapAsync();

        await using (var ctx = _fx.NewContext()) await Vouchers(ctx).PostAsync(id);
        var afterFirst = await SnapAsync();
        Assert.Equal(50m, before.sup - afterFirst.sup);

        // إعادة إرسال نفس الطلب (الترحيل نفسه) — السند مُرحَّل ⇒ يُعاد كما هو
        await using (var ctx = _fx.NewContext())
        {
            var again = await Vouchers(ctx).PostAsync(id);
            Assert.Equal((int)ERPSystem.Domain.Enums.VoucherStatus.Posted, again.Status);
        }

        var afterSecond = await SnapAsync();
        Assert.Equal(afterFirst.sup, afterSecond.sup);       // لا خصم ثانٍ
        Assert.Equal(afterFirst.cash, afterSecond.cash);      // لا خصم نقدي ثانٍ
        Assert.Equal(afterFirst.posted, afterSecond.posted);  // لا سند إضافي
    }

    // ═══ مبلغ صفري أو سالب ⇒ مرفوض بلا آثار ═══
    [LivePgFact]
    public async Task PU02_Zero_or_negative_amount_is_rejected_without_side_effects()
    {
        if (!_fx.Available) return;
        var before = await SnapAsync();

        await using (var ctx = _fx.NewContext())
        {
            var vs = Vouchers(ctx);
            var ex0 = await Assert.ThrowsAsync<InvalidOperationException>(() => vs.CreateDraftAsync(
                new CreatePaymentVoucherDto { SupplierId = _fx.SupplierId, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 0m }));
            Assert.NotNull(ex0.Message);

            var exNeg = await Assert.ThrowsAsync<InvalidOperationException>(() => vs.CreateDraftAsync(
                new CreatePaymentVoucherDto { SupplierId = _fx.SupplierId, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = -5m }));
            Assert.NotNull(exNeg.Message);
        }

        var after = await SnapAsync();
        Assert.Equal(before.posted, after.posted);   // لم يُرحَّل أي سند جديد
        Assert.Equal(before.sup, after.sup);
        Assert.Equal(before.cash, after.cash);
    }

    // ═══ ترحيلان متزامنان للسند نفسه ⇒ أثر واحد فقط ═══
    [LivePgFact]
    public async Task PU02_Concurrent_posts_of_same_voucher_apply_once()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var item = await _fx.CreateItemAsync("A5-PU02-C", 5m, 40m, 0m);
        await using (var ctx = _fx.NewContext())
        {
            var dto = _fx.NewPurchase(item, 10m, 40m, cash: false);   // 400 آجلة
            dto.IdempotencyKey = Guid.NewGuid();
            await Purchases(ctx).CreateAsync(dto);
        }

        var (id, _) = await NewApprovedVoucherAsync(70m);
        var before = await SnapAsync();

        async Task Try()
        {
            try { await using var c = _fx.NewContext(); await Vouchers(c).PostAsync(id); }
            catch { /* الطلب الخاسر مسموح أن يُرفض — المهم ألا يُطبّق الأثر مرتين */ }
        }
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Try()));

        var after = await SnapAsync();
        Assert.Equal(70m, before.sup - after.sup);          // خصم واحد فقط
        Assert.Equal(70m, before.cash - after.cash);         // خصم نقدي واحد فقط
        await using var chk = _fx.NewContext();
        Assert.Equal(1, await chk.Set<JournalEntry>()
            .CountAsync(e => e.EntryType == ERPSystem.Domain.Enums.JournalEntryType.PaymentVoucher
                             && e.ReferenceNumber == chk.Set<PaymentVoucher>().Where(v => v.Id == id).Select(v => v.VoucherNumber).First()));
    }

}
