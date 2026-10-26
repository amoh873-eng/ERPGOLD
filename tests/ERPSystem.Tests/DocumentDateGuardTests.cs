using ERPSystem.Application.DTOs.Inventory;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Phase A/1 — حماية التاريخ (جذر -infinity): لا يمكن إنشاء حركة مخزون أو سند أو قيد محاسبي
/// بتاريخ DateTime.MinValue أو تاريخ خارج النطاق المعقول، ولا تُترك بيانات جزئية عند الرفض.
/// </summary>
public class DocumentDateGuardTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public DocumentDateGuardTests(AccountingPgFixture pg) => _pg = pg;

    // ───────── الحارس المركزي في JournalEntryService.PrepareEntryAsync (InMemory) ─────────

    private static List<JournalEntryLegInput> BalancedLegs(Guid debit, Guid credit) => new()
    {
        new JournalEntryLegInput { AccountId = debit, DebitAmount = 100m },
        new JournalEntryLegInput { AccountId = credit, CreditAmount = 100m }
    };

    [LivePgTheory]
    [InlineData(1900, 1, 1)]    // الحد الأدنى المسموح
    [InlineData(2026, 10, 9)]   // تاريخ عمل طبيعي
    [InlineData(2100, 1, 1)]    // الحد الأعلى المسموح
    public async Task Journal_guard_accepts_valid_dates_on_live_pg(int y, int m, int d)
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var cash = await PostingAccountResolver.ResolveAsync(ctx, "1100");
        var revenue = await PostingAccountResolver.ResolveAsync(ctx, "4100");
        var journal = new JournalEntryService(ctx);

        var entry = await journal.PrepareEntryAsync(
            JournalEntryType.Manual, new DateTime(y, m, d), null, null,
            new List<JournalEntryLegInput>
            {
                new() { AccountId = cash.Id, DebitAmount = 100m },
                new() { AccountId = revenue.Id, CreditAmount = 100m }
            });

        Assert.Equal(new DateTime(y, m, d), entry.EntryDate);
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(100m, entry.Lines.Sum(l => l.DebitAmount));
        Assert.Equal(100m, entry.Lines.Sum(l => l.CreditAmount));
    }

    [Theory]
    [InlineData(1, 1, 1)]        // DateTime.MinValue — كان يُخزَّن -infinity في PostgreSQL
    [InlineData(1899, 12, 31)]   // قبل الحد الأدنى
    [InlineData(2100, 1, 2)]     // بعد الحد الأعلى
    public async Task Journal_guard_rejects_unset_or_out_of_range_dates(int y, int m, int d)
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedProductionTree(ctx);
        var service = new JournalEntryService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareEntryAsync(
            JournalEntryType.Manual, new DateTime(y, m, d), null, null,
            BalancedLegs(ids["1100"], ids["1101"])));

        Assert.Contains("تاريخ القيد غير صالح", ex.Message);
        // الرفض قبل أي أثر: لا قيد جزئي، ولا تحديث أرصدة
        Assert.False(ctx.ChangeTracker.Entries<JournalEntry>().Any());
        Assert.Equal(0m, ctx.Set<Account>().First(a => a.Id == ids["1100"]).CurrentBalance);
        Assert.Equal(0m, ctx.Set<Account>().First(a => a.Id == ids["1101"]).CurrentBalance);
    }

    // ───────── سند الإتلاف على PostgreSQL حقيقي ─────────

    private static CreateStockWriteOffDto WriteOffDto(Guid itemId, Guid warehouseId, DateTime date) => new()
    {
        ItemId = itemId,
        WarehouseId = warehouseId,
        Quantity = 3m,
        Reason = (int)StockWriteOffReason.Damaged,
        Date = date
    };

    /// <summary>لقطة مقيّدة بالصنف: حركاته، سنداته، قيوده، رقم WO المتسلسل، ورصيده.</summary>
    private async Task<(int Movements, int WriteOffs, int Entries, long WoSequence, decimal Stock)> SnapshotAsync(Guid itemId)
    {
        await using var ctx = _pg.NewContext();
        var key = $"WO-{DateTime.Now:yyyyMMdd}";

        var writeOffEntryIds = await ctx.Set<StockWriteOff>().AsNoTracking()
            .Where(w => w.ItemId == itemId)
            .Select(w => w.JournalEntryId)
            .ToListAsync();

        return (
            await ctx.Set<StockMovement>().CountAsync(m => m.ItemId == itemId && !m.IsDeleted),
            await ctx.Set<StockWriteOff>().CountAsync(w => w.ItemId == itemId && !w.IsDeleted),
            await ctx.Set<JournalEntry>().CountAsync(e => writeOffEntryIds.Contains(e.Id)),
            (await ctx.Set<NumberSequence>().AsNoTracking().FirstOrDefaultAsync(n => n.SequenceKey == key))?.LastValue ?? 0L,
            (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId)).CurrentStock);
    }

    private static StockWriteOffService NewWriteOffService(ERPSystem.Infrastructure.Data.AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx));

    [LivePgFact]
    public async Task Write_off_rejects_unset_date_without_any_write()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("A5-DT-UNSET-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 10m);
        var before = await SnapshotAsync(itemId);

        await using (var ctx = _pg.NewContext())
        {
            var service = NewWriteOffService(ctx);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CreateAsync(WriteOffDto(itemId, _pg.WarehouseId, default(DateTime)), "tester"));

            Assert.Contains("تاريخ سند الإتلاف غير صالح", ex.Message);
        }

        var after = await SnapshotAsync(itemId);
        Assert.Equal(before, after);   // لا سند، لا حركة، لا قيد، لا رقم محروق، ولا تغيّر رصيد
    }

    [LivePgFact]
    public async Task Write_off_rejects_absurd_date_without_any_write()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("A5-DT-ABS-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 10m);
        var before = await SnapshotAsync(itemId);

        await using (var ctx = _pg.NewContext())
        {
            var service = NewWriteOffService(ctx);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CreateAsync(WriteOffDto(itemId, _pg.WarehouseId, new DateTime(1899, 12, 31)), "tester"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CreateAsync(WriteOffDto(itemId, _pg.WarehouseId, new DateTime(2100, 1, 2)), "tester"));
        }

        var after = await SnapshotAsync(itemId);
        Assert.Equal(before, after);
    }

    [LivePgFact]
    public async Task Central_journal_guard_rejects_unset_date_on_live_pg()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var cash = await PostingAccountResolver.ResolveAsync(ctx, "1100");
        var revenue = await PostingAccountResolver.ResolveAsync(ctx, "4100");
        var journal = new JournalEntryService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareEntryAsync(
            JournalEntryType.Manual, default(DateTime), null, null,
            new List<JournalEntryLegInput>
            {
                new() { AccountId = cash.Id, DebitAmount = 10m },
                new() { AccountId = revenue.Id, CreditAmount = 10m }
            }));

        Assert.Contains("تاريخ القيد غير صالح", ex.Message);
        Assert.False(ctx.ChangeTracker.Entries<JournalEntry>().Any());
    }

    [LivePgFact]
    public async Task Write_off_accepts_valid_date_and_keeps_ledger_consistent()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemAsync("A5-DT-OK-" + Guid.NewGuid().ToString("N")[..5], 2.38m, 1.75m, 10m);
        var date = DateTime.Today;

        StockWriteOffDto dto;
        await using (var ctx = _pg.NewContext())
        {
            dto = await NewWriteOffService(ctx).CreateAsync(WriteOffDto(itemId, _pg.WarehouseId, date), "tester");
        }

        Assert.Equal(date, dto.Date);

        await using (var ctx = _pg.NewContext())
        {
            // السند + حركة المخزون + القيد كلها بالتاريخ الصالح نفسه
            var document = await ctx.Set<StockWriteOff>().AsNoTracking().FirstAsync(w => w.Id == dto.Id);
            Assert.Equal(date, document.Date);

            var movement = await ctx.Set<StockMovement>().AsNoTracking()
                .FirstAsync(m => m.ItemId == itemId && m.MovementType == MovementType.WriteOff && !m.IsDeleted);
            Assert.Equal(date, movement.MovementDate);
            Assert.Equal(-3m, movement.Quantity);

            var entry = await ctx.Set<JournalEntry>().AsNoTracking()
                .Include(e => e.Lines).ThenInclude(l => l.Account)
                .FirstAsync(e => e.Id == dto.JournalEntryId);
            Assert.Equal(date, entry.EntryDate);
            Assert.Equal(5.25m, entry.Lines.Where(l => l.Account!.Code == "5300").Sum(l => l.DebitAmount));
            Assert.Equal(5.25m, entry.Lines.Where(l => l.Account!.Code == "1300").Sum(l => l.CreditAmount));
            Assert.Equal(entry.Lines.Sum(l => l.DebitAmount), entry.Lines.Sum(l => l.CreditAmount));

            Assert.Equal(7m, (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId)).CurrentStock);
        }

        // لا تواريخ غير معقولة، ولا حركة على Parent، وكل الأرصدة المخزَّنة == دفتر القيود
        Assert.Equal(0, await LedgerInvariants.AbsurdDateRowsAsync(_pg));
        Assert.Equal(0, await LedgerInvariants.ParentPostingsAsync(_pg));
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

}
