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
/// PU-03C — اختبارات قبول لحارس منع السداد الزائد.
/// كل سيناريو يستخدم **موردًا جديدًا بذمّة 200.00 بالضبط** (عبر SeedPayableForAsync) ⇒ عزل تام
/// وقياسات مطلقة صحيحة، بلا تأثر ببيانات الاختبارات الأخرى.
/// المسار الفعلي: CreateDraftAsync → ApproveAsync → PostAsync.
/// </summary>
public sealed class OverpaymentGuardTests : IClassFixture<AccountingPgFixture>
{
    private const decimal Payable = 200.00m;

    private readonly AccountingPgFixture _fx;
    public OverpaymentGuardTests(AccountingPgFixture fx) => _fx = fx;

    private sealed class AllowAllVouchers : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public string CurrentUserName() => "pu03c-tester";
    }

    private static PaymentVoucherService Vouchers(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    /// <summary>مورد جديد بذمّة ابتدائية محددة (قيد متوازن: مدين 1300 / دائن 2200).</summary>
    private async Task<Guid> NewSupplierAsync(string code)
    {
        await using var ctx = _fx.NewContext();
        var s = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "مورد PU-03C " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(s);
        await ctx.SaveChangesAsync();
        await AccountingPgFixture.SeedPayableForAsync(ctx, s.Id, Payable);
        return s.Id;
    }

    private async Task<(decimal sup, decimal cash, int entries)> SnapAsync(Guid supplierId, Guid? cashIdOverride = null)
    {
        await using var ctx = _fx.NewContext();
        var cid = cashIdOverride ?? (await PostingAccountResolver.ResolveAsync(ctx, "1100")).Id;
        return (
            await ctx.Set<Supplier>().AsNoTracking().Where(s => s.Id == supplierId).Select(s => s.CurrentBalance).FirstAsync(),
            await ctx.Set<Account>().AsNoTracking().Where(a => a.Id == cid).Select(a => a.CurrentBalance).FirstAsync(),
            await ctx.Set<JournalEntry>().AsNoTracking().CountAsync());
    }

    /// <summary>ينشئ سندًا ويعتمده (بلا ترحيل) ويُرجع معرّفه.</summary>
    private async Task<Guid> DraftApprovedAsync(Guid supplierId, decimal amount)
    {
        await using var ctx = _fx.NewContext();
        var svc = Vouchers(ctx);
        var d = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = supplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = 1,      // نقدي ⇒ الصندوق 1100
            Amount = amount,
            Description = "PU-03C"
        });
        await svc.ApproveAsync(d.Id);
        return d.Id;
    }

    private async Task PostAsync(Guid voucherId)
    {
        await using var ctx = _fx.NewContext();
        await Vouchers(ctx).PostAsync(voucherId);
    }

    private async Task<(VoucherStatus status, Guid? entry)> VoucherStateAsync(Guid voucherId)
    {
        await using var ctx = _fx.NewContext();
        var v = await ctx.Set<PaymentVoucher>().AsNoTracking().FirstAsync(x => x.Id == voucherId);
        return (v.Status, v.JournalEntryId);
    }

    // ═══ A: ذمة 200 · سند 201 ⇒ رفض بلا أي أثر ═══
    [LivePgFact]
    public async Task PU03C_A_Overpay_201_of_200_is_rejected_without_effect()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var sup = await NewSupplierAsync("A5-C-A");

        var before = await SnapAsync(sup);
        Assert.Equal(Payable, before.sup);           // ذمّة 200 بالضبط

        var v = await DraftApprovedAsync(sup, 201.00m);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PostAsync(v));
        Assert.Contains("يتجاوز المستحق", ex.Message);

        var after = await SnapAsync(sup);
        Assert.Equal(before.sup, after.sup);         // رصيد المورد لم يتغيّر
        Assert.Equal(before.cash, after.cash);        // الصندوق لم يتغيّر
        Assert.Equal(before.entries, after.entries);  // لا قيد جديد
        var st = await VoucherStateAsync(v);
        Assert.Equal(VoucherStatus.Approved, st.status);  // لم يُعدّ مرحّلًا
        Assert.Null(st.entry);
    }

    // ═══ B: 200 − 60 ⇒ 140 · ثم 141 ⇒ رفض · المتاح يبقى 140 ═══
    [LivePgFact]
    public async Task PU03C_B_Second_141_after_60_is_rejected_leaving_140()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var sup = await NewSupplierAsync("A5-C-B");
        var before = await SnapAsync(sup);

        var v60 = await DraftApprovedAsync(sup, 60.00m);
        await PostAsync(v60);
        var mid = await SnapAsync(sup);
        Assert.Equal(140.00m, mid.sup);                              // المتاح 140
        Assert.Equal(before.cash - 60.00m, mid.cash);                // خصم نقدي 60 مرة واحدة
        Assert.Equal(before.entries + 1, mid.entries);

        var v141 = await DraftApprovedAsync(sup, 141.00m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostAsync(v141));

        var after = await SnapAsync(sup);
        Assert.Equal(140.00m, after.sup);        // بقي 140
        Assert.Equal(mid.cash, after.cash);      // لا خصم إضافي
        Assert.Equal(mid.entries, after.entries);// لا قيد جديد
        Assert.Equal(VoucherStatus.Approved, (await VoucherStateAsync(v141)).status);
    }
    // ═══ C: 200 − 60 − 140 ⇒ صفر ═══
    [LivePgFact]
    public async Task PU03C_C_60_then_140_clears_the_payable()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var sup = await NewSupplierAsync("A5-C-C");
        var before = await SnapAsync(sup);

        await PostAsync(await DraftApprovedAsync(sup, 60.00m));
        var mid = await SnapAsync(sup);
        Assert.Equal(140.00m, mid.sup);

        await PostAsync(await DraftApprovedAsync(sup, 140.00m));
        var after = await SnapAsync(sup);

        Assert.Equal(0.00m, after.sup);                            // المتاح صفر
        Assert.Equal(before.cash - 200.00m, after.cash);            // خصم نقدي 200 إجمالًا
        Assert.Equal(before.entries + 2, after.entries);            // قيدان فقط

        // لا يمكن أي سند موجب إضافي
        var extra = await DraftApprovedAsync(sup, 1.00m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostAsync(extra));
        Assert.Equal(0.00m, (await SnapAsync(sup)).sup);
    }

    // ═══ D: سداد كامل 200 ⇒ صفر ثم رفض أي موجب ═══
    [LivePgFact]
    public async Task PU03C_D_Full_200_clears_then_rejects_any_positive()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var sup = await NewSupplierAsync("A5-C-D");
        var before = await SnapAsync(sup);

        await PostAsync(await DraftApprovedAsync(sup, 200.00m));
        var after = await SnapAsync(sup);
        Assert.Equal(0.00m, after.sup);
        Assert.Equal(before.cash - 200.00m, after.cash);
        Assert.Equal(before.entries + 1, after.entries);

        foreach (var amt in new[] { 0.01m, 1.00m, 200.00m })
        {
            var v = await DraftApprovedAsync(sup, amt);
            await Assert.ThrowsAsync<InvalidOperationException>(() => PostAsync(v));
        }
        var final = await SnapAsync(sup);
        Assert.Equal(0.00m, final.sup);
        Assert.Equal(after.cash, final.cash);
        Assert.Equal(after.entries, final.entries);
    }

    // ═══ التزامن: سندان مستقلان 120+120 على ذمّة 200 ⇒ لا يُقبلان معًا ═══
    [LivePgFact]
    public async Task PU03C_CONCURRENCY_two_independent_120s_never_exceed_200()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var sup = await NewSupplierAsync("A5-C-CC");
        var before = await SnapAsync(sup);

        // سندان **مستقلان** (ليس ترحيلًا مكررًا لنفس السند)
        var v1 = await DraftApprovedAsync(sup, 120.00m);
        var v2 = await DraftApprovedAsync(sup, 120.00m);

        async Task Try(Guid id)
        {
            try { await PostAsync(id); } catch { /* الخاسر مسموح أن يُرفض */ }
        }
        await Task.WhenAll(Try(v1), Try(v2));

        var s1 = await VoucherStateAsync(v1);
        var s2 = await VoucherStateAsync(v2);
        var postedCount = (s1.status == VoucherStatus.Posted ? 1 : 0) + (s2.status == VoucherStatus.Posted ? 1 : 0);

        Assert.Equal(1, postedCount);                     // واحد فقط
        var after = await SnapAsync(sup);
        Assert.Equal(80.00m, after.sup);                  // 200 − 120
        Assert.Equal(before.cash - 120.00m, after.cash);   // خصم نقدي واحد
        Assert.Equal(before.entries + 1, after.entries);   // قيد واحد
        Assert.True(200.00m - after.sup <= 200.00m, "مجموع المقبول لا يتجاوز الذمّة");
    }

    // ═══ الذرّية: فشل أثناء الترحيل ⇒ لا آثار جزئية، وإعادة المحاولة ممكنة ═══
    [LivePgFact]
    public async Task PU03C_ATOMICITY_failed_post_leaves_no_partial_effect()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var sup = await NewSupplierAsync("A5-C-AT");
        var v = await DraftApprovedAsync(sup, 50.00m);
        var before = await SnapAsync(sup);

        // استحثاث فشل آمن: تعطيل حساب الصندوق ⇒ MovementAccountValidator يرفض (بلا تعديل كود إنتاج)
        Guid cashId;
        await using (var ctx = _fx.NewContext())
        {
            var cash = await PostingAccountResolver.ResolveAsync(ctx, "1100");
            cashId = cash.Id;
            cash.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => PostAsync(v));

            var afterFail = await SnapAsync(sup, cashId);
            Assert.Equal(before.sup, afterFail.sup);        // رصيد المورد ثابت
            Assert.Equal(before.cash, afterFail.cash);       // الصندوق ثابت
            Assert.Equal(before.entries, afterFail.entries);  // لا قيد جزئي
            var st = await VoucherStateAsync(v);
            Assert.Equal(VoucherStatus.Approved, st.status);  // حالة متوافقة (لم يُعدّ مرحّلًا)
            Assert.Null(st.entry);
        }
        finally
        {
            // إعادة الحساب لتفعيله حتى لا تتأثر بقية اختبارات الفئة
            await using var ctx = _fx.NewContext();
            var cash = await ctx.Set<Account>().FirstAsync(a => a.Id == cashId);
            cash.IsActive = true;
            await ctx.SaveChangesAsync();
        }

        // إعادة المحاولة بعد إزالة سبب الفشل ⇒ تنجح مرة واحدة فقط
        await PostAsync(v);
        var ok = await SnapAsync(sup);
        Assert.Equal(150.00m, ok.sup);                     // 200 − 50
        Assert.Equal(before.cash - 50.00m, ok.cash);
        Assert.Equal(before.entries + 1, ok.entries);
    }

}
