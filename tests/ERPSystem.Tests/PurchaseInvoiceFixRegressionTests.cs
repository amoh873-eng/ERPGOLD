using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.Services;
using ERPSystem.Infrastructure.Data;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// PU-01 — انحدار D-01…D-04/D-06 على قاعدة PostgreSQL مؤقتة معزولة (AccountingPgFixture)،
/// تُنشأ وتُحذف ذاتيًا عبر ERPSYSTEM_TEST_PG. لا تلمس أي قاعدة منشورة.
/// </summary>
public sealed class PurchaseInvoiceFixRegressionTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _fx;
    public PurchaseInvoiceFixRegressionTests(AccountingPgFixture fx) => _fx = fx;

    private static PurchaseInvoiceService Svc(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    private async Task<decimal> CashAsync()
    {
        await using var ctx = _fx.NewContext();
        return (await PostingAccountResolver.ResolveAsync(ctx, "1100")).CurrentBalance;
    }

    private async Task<(int pi, int je, int sm, decimal sup, decimal cash)> SnapAsync()
    {
        await using var ctx = _fx.NewContext();
        var cashId = (await PostingAccountResolver.ResolveAsync(ctx, "1100")).Id;
        return (await ctx.Set<PurchaseInvoice>().CountAsync(),
                await ctx.Set<JournalEntry>().CountAsync(),
                await ctx.Set<StockMovement>().CountAsync(),
                await ctx.Set<Supplier>().SumAsync(s => s.CurrentBalance),
                await ctx.Set<Account>().Where(a => a.Id == cashId).SumAsync(a => a.CurrentBalance));
    }

    private async Task<decimal> SupplierBalAsync()
    {
        await using var ctx = _fx.NewContext();
        return (await ctx.Set<Supplier>().AsNoTracking().FirstAsync(s => s.Id == _fx.SupplierId)).CurrentBalance;
    }

    // T01 — نفس المفتاح مرتين ⇒ مستند واحد فقط، ويُعاد الرقم الأصلي
    [LivePgFact]
    public async Task T01_SameKey_Twice_OneDocument()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var item = await _fx.CreateItemAsync("A5-FIX-01", 5m, 2m, 0m);
        var dto = _fx.NewPurchase(item, 10m, 2m, cash: true);
        dto.IdempotencyKey = Guid.NewGuid();

        string no1; await using (var c = _fx.NewContext()) no1 = (await Svc(c).CreateAsync(dto)).InvoiceNumber;
        await using (var c = _fx.NewContext())
            Assert.Equal(no1, (await Svc(c).CreateAsync(dto)).InvoiceNumber);

        await using var chk = _fx.NewContext();
        Assert.Equal(1, await chk.Set<PurchaseInvoice>().CountAsync(i => i.IdempotencyKey == dto.IdempotencyKey));
        Assert.Equal(1, await chk.Set<StockMovement>().CountAsync(m => m.ReferenceNumber == no1));
    }

    // T02 — 4 طلبات متزامنة بنفس المفتاح ⇒ لا تكرار
    [LivePgFact]
    public async Task T02_Concurrent_SameKey_NoDuplicates()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var item = await _fx.CreateItemAsync("A5-FIX-02", 5m, 2m, 0m);
        var dto = _fx.NewPurchase(item, 4m, 2m, cash: true);
        dto.IdempotencyKey = Guid.NewGuid();

        async Task Fire()
        {
            try { await using var c = _fx.NewContext(); await Svc(c).CreateAsync(dto); }
            catch { /* الطلب الخاسر يُرفض — المهم ألا يُنشئ مستندًا */ }
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Fire()));

        await using var chk = _fx.NewContext();
        Assert.Equal(1, await chk.Set<PurchaseInvoice>().CountAsync(i => i.IdempotencyKey == dto.IdempotencyKey));
    }

    // T03 — فشل في المنتصف ⇒ صفر آثار جزئية
    [LivePgFact]
    public async Task T03_MidFailure_NoPartialArtifacts()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var before = await SnapAsync();
        var dto = _fx.NewPurchase(Guid.NewGuid(), 3m, 1m, cash: true);   // صنف غير موجود
        dto.IdempotencyKey = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var c = _fx.NewContext();
            await Svc(c).CreateAsync(dto);
        });
        Assert.Equal(before, await SnapAsync());
    }

    // T04 — شراء نقدي مدفوع بالكامل ⇒ لا ذمة على المورد
    [LivePgFact]
    public async Task T04_FullyPaidCash_NoSupplierLiability()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var item = await _fx.CreateItemAsync("A5-FIX-04", 5m, 3m, 0m);
        var dto = _fx.NewPurchase(item, 10m, 2m, cash: true);
        dto.IdempotencyKey = Guid.NewGuid();

        var before = await SupplierBalAsync();
        await using (var c = _fx.NewContext()) await Svc(c).CreateAsync(dto);
        Assert.Equal(before, await SupplierBalAsync());

        await using var chk = _fx.NewContext();
        var inv = await chk.Set<PurchaseInvoice>().AsNoTracking().FirstAsync(i => i.IdempotencyKey == dto.IdempotencyKey);
        Assert.Equal(inv.TotalAmount, inv.PaidAmount);
    }
    // T05 — شراء آجل ⇒ الالتزام الصحيح (7 × 4 = 28)
    [LivePgFact]
    public async Task T05_CreditPurchase_CorrectLiability()
    {
        if (!_fx.Available) return;
        var item = await _fx.CreateItemAsync("A5-FIX-05", 5m, 3m, 0m);
        var dto = _fx.NewPurchase(item, 7m, 4m, cash: false);
        dto.IdempotencyKey = Guid.NewGuid();

        var before = await SupplierBalAsync();
        decimal total; await using (var c = _fx.NewContext()) total = (await Svc(c).CreateAsync(dto)).TotalAmount;
        Assert.Equal(28m, total);
        Assert.Equal(before + total, await SupplierBalAsync());
    }

    // T06 — دفع نقدي غير مغطى ⇒ رفض بلا آثار جانبية
    [LivePgFact]
    public async Task T06_UncoveredCash_Rejected_NoSideEffects()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var item = await _fx.CreateItemAsync("A5-FIX-06", 5m, 1m, 0m);
        var avail = await CashAsync();
        var dto = _fx.NewPurchase(item, avail + 100m, 1m, cash: true);
        dto.IdempotencyKey = Guid.NewGuid();

        var before = await SnapAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var c = _fx.NewContext();
            await Svc(c).CreateAsync(dto);
        });
        // الرسالة عربية و«الكسر» التشكيلية قد تختلف في التطبيع ⇒ نتحقق من رسالة واضحة
        // خاصة بالفاتورة (مرتكز ASCII مستقر)، والإثبات الجوهري هو انعدام الآثار الجانبية أدناه.
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Contains("PI-", ex.Message);
        Assert.Equal(before, await SnapAsync());
    }

    // T07 — الشيك المؤجل لا يُعامل كنقدية متاحة
    [LivePgFact]
    public async Task T07_DeferredCheck_NotAvailableCash()
    {
        if (!_fx.Available) return;
        await _fx.SeedCashAsync();
        var item = await _fx.CreateItemAsync("A5-FIX-07", 5m, 1m, 0m);
        var cashBefore = await CashAsync();
        var dto = _fx.NewPurchase(item, 5m, 2m, cash: false);
        dto.InvoiceType = 1; dto.PaymentMethod = 2;
        dto.CheckNumber = "CHK-A5-001"; dto.CheckBankName = "بنك اختباري";
        dto.CheckDueDate = DateTime.Today.AddMonths(1);
        dto.IdempotencyKey = Guid.NewGuid();

        await using (var c = _fx.NewContext()) await Svc(c).CreateAsync(dto);
        Assert.Equal(cashBefore, await CashAsync());   // الصندوق لم يُمس
    }

    // T08 — توازن القيود + مطابقة المخزون + الأستاذ
    [LivePgFact]
    public async Task T08_LedgersBalance_StockMatches()
    {
        if (!_fx.Available) return;
        var item = await _fx.CreateItemAsync("A5-FIX-08", 5m, 2m, 0m);
        var dto = _fx.NewPurchase(item, 10m, 2m, cash: false);
        dto.IdempotencyKey = Guid.NewGuid();

        await using var ctx = _fx.NewContext();
        var inv = await Svc(ctx).CreateAsync(dto);

        var entity = await ctx.Set<PurchaseInvoice>().AsNoTracking()
            .FirstAsync(i => i.IdempotencyKey == dto.IdempotencyKey);
        var e = await ctx.Set<JournalEntry>().AsNoTracking().Include(x => x.Lines)
            .FirstAsync(x => x.Id == entity.JournalEntryId);
        Assert.Equal(e.Lines.Sum(l => l.DebitAmount), e.Lines.Sum(l => l.CreditAmount));
        Assert.Equal(20m, e.Lines.Sum(l => l.DebitAmount));
        Assert.Equal(10m, await ctx.Set<StockMovement>().AsNoTracking()
            .Where(m => m.ReferenceNumber == inv.InvoiceNumber).SumAsync(m => m.Quantity));

        var inventory = await PostingAccountResolver.ResolveAsync(ctx, "1300");
        Assert.True(inventory.CurrentBalance >= 20m);
    }

    // T09 — مصفوفة التوافق بين نوع الفاتورة وطريقة الدفع
    [LivePgFact]
    public async Task T09_TypePayment_Compatibility()
    {
        if (!_fx.Available) return;
        var item = await _fx.CreateItemAsync("A5-FIX-09", 5m, 1m, 0m);

        var bad1 = _fx.NewPurchase(item, 1m, 1m, cash: false);
        bad1.InvoiceType = 1; bad1.PaymentMethod = 1;          // نقدي + آجل
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        { await using var c = _fx.NewContext(); await Svc(c).CreateAsync(bad1); });

        var bad2 = _fx.NewPurchase(item, 1m, 1m, cash: false);
        bad2.InvoiceType = 2; bad2.PaymentMethod = 3;          // آجل + نقدي
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        { await using var c = _fx.NewContext(); await Svc(c).CreateAsync(bad2); });

        var ok = _fx.NewPurchase(item, 1m, 1m, cash: false);
        ok.InvoiceType = 2; ok.PaymentMethod = 1; ok.IdempotencyKey = Guid.NewGuid();
        await using (var c = _fx.NewContext()) await Svc(c).CreateAsync(ok);   // مقبول
    }

}
