using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Phase A/3 — قاعدة «الحسابات المتحركة فقط» في شكل Production:
/// 1100 ورقة (Leaf) → مسموح، والجذور 1 و 5 رئيسية → مرفوضة.
/// </summary>
public class ParentAccountShapeProductionTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;
    public ParentAccountShapeProductionTests(AccountingPgFixture pg) => _pg = pg;

    [LivePgFact]
    public async Task Production_shape_allows_1100_leaf_and_rejects_parent_roots()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await using var ctx = _pg.NewContext();

        var cash = await ctx.Set<Account>().AsNoTracking().FirstAsync(a => a.Code == "1100" && !a.IsDeleted);
        var root1 = await ctx.Set<Account>().AsNoTracking().FirstAsync(a => a.Code == "1" && !a.IsDeleted);
        var root5 = await ctx.Set<Account>().AsNoTracking().FirstAsync(a => a.Code == "5" && !a.IsDeleted);

        Assert.False(await MovementAccountValidator.IsParentAsync(ctx, cash.Id));

        // 1100 ورقة → مسموح (لا يرمي)
        await MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { cash.Id });

        Assert.True(await MovementAccountValidator.IsParentAsync(ctx, root1.Id));
        Assert.True(await MovementAccountValidator.IsParentAsync(ctx, root5.Id));
        await Assert.ThrowsAsync<ParentAccountMovementException>(
            () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { root1.Id }));
        await Assert.ThrowsAsync<ParentAccountMovementException>(
            () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { root5.Id }));
    }

    [LivePgFact]
    public async Task Cash_sale_in_production_shape_posts_to_leaf_1100()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("A5-PS-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 10m);

        Guid invoiceId;
        Guid saleEntryId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await AccountingPgFixture.NewSalesService(ctx)
                .CreateAsync(_pg.NewCashSale(itemId, 1m, 2.38m));
            invoiceId = invoice.Id;
            saleEntryId = invoice.SalesJournalEntryId!.Value;
        }

        await using (var ctx = _pg.NewContext())
        {
            var entry = await ctx.Set<JournalEntry>().AsNoTracking()
                .Include(e => e.Lines).ThenInclude(l => l.Account)
                .FirstAsync(e => e.Id == saleEntryId);

            var debitLeg = entry.Lines.Single(l => l.DebitAmount > 0);
            Assert.Equal("1100", debitLeg.Account!.Code);   // الورقة نفسها (Production)
            Assert.Equal(2.38m, debitLeg.DebitAmount);

            Assert.NotNull(await ctx.Set<SalesInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId));
        }

        // القاعدة مطبَّقة فعلياً: لا حركة على أي حساب رئيسي، والأرصدة مطابقة للدفتر
        Assert.Equal(0, await LedgerInvariants.ParentPostingsAsync(_pg));
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}

/// <summary>
/// Phase A/3 — قاعدة «الحسابات المتحركة فقط» في شكل Commercial:
/// 1100 رئيسي (له ابن 11001) → مرفوض، و 11001 ورقة → مسموح، والقيود التلقائية تهبط إلى 11001.
/// </summary>
public class ParentAccountShapeCommercialTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;
    public ParentAccountShapeCommercialTests(AccountingPgFixture pg) => _pg = pg;

    [LivePgFact]
    public async Task Commercial_shape_rejects_parent_1100_and_resolves_transactions_to_11001()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // نحوّل الشجرة إلى شكل Commercial: 1100 رئيسي → 11001 ورقة
        await _pg.CreateChildAccountAsync("1100", "11001", "صندوق نقدية");

        await using (var ctx = _pg.NewContext())
        {
            var parent1100 = await ctx.Set<Account>().AsNoTracking().FirstAsync(a => a.Code == "1100" && !a.IsDeleted);
            var root1 = await ctx.Set<Account>().AsNoTracking().FirstAsync(a => a.Code == "1" && !a.IsDeleted);

            Assert.True(await MovementAccountValidator.IsParentAsync(ctx, parent1100.Id));

            // Parent → مرفوض صريحاً
            await Assert.ThrowsAsync<ParentAccountMovementException>(
                () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { parent1100.Id }));
            await Assert.ThrowsAsync<ParentAccountMovementException>(
                () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { root1.Id }));

            // الورقة 11001 → مسموح
            var leaf = await PostingAccountResolver.ResolveAsync(ctx, "1100");
            Assert.Equal("11001", leaf.Code);
            await MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { leaf.Id });
        }

        // القيود التلقائية (فاتورة بيع نقدية) تهبط إلى الورقة 11001 ولا تلمس Parent 1100
        var itemId = await _pg.CreateItemAsync("A5-CS-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 10m);

        Guid saleEntryId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await AccountingPgFixture.NewSalesService(ctx)
                .CreateAsync(_pg.NewCashSale(itemId, 1m, 2.38m));
            saleEntryId = invoice.SalesJournalEntryId!.Value;
        }

        await using (var ctx = _pg.NewContext())
        {
            var entry = await ctx.Set<JournalEntry>().AsNoTracking()
                .Include(e => e.Lines).ThenInclude(l => l.Account)
                .FirstAsync(e => e.Id == saleEntryId);

            var debitLeg = entry.Lines.Single(l => l.DebitAmount > 0);
            Assert.Equal("11001", debitLeg.Account!.Code);  // هبطت إلى الورقة
        }

        Assert.Equal(0, await LedgerInvariants.ParentPostingsAsync(_pg));
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
