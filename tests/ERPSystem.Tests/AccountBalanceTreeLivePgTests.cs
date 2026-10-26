using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;
using Xunit.Abstractions;

namespace ERPSystem.Tests;

/// <summary>
/// اختبار تكامل حي (PostgreSQL معزول) لأرصدة شجرة الحسابات حسب المستويات:
///  - مطابقة أرصدة الشجرة مع دفتر القيود وميزان المراجعة لنفس البيانات.
///  - مطابقة رصيد الأب مع مجموع أبنائه على حركات حقيقية مُرحّلة عبر الخدمات.
///  - ثبات عدد استعلامات SQL مع نمو عدد الحسابات (منع N+1) — اختبار الأداء.
/// </summary>
public class AccountBalanceTreeLivePgTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;
    private readonly ITestOutputHelper _out;

    public AccountBalanceTreeLivePgTests(AccountingPgFixture pg, ITestOutputHelper output)
    {
        _pg = pg;
        _out = output;
    }

    /// <summary>عدّاد أوامر SQL — يثبت أن الأرصدة تُبنى بعدد ثابت من الاستعلامات.</summary>
    private sealed class CountingInterceptor : DbCommandInterceptor
    {
        private int _commands;
        public int Commands => Volatile.Read(ref _commands);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _commands);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }

    // ═════════ (11) المطابقة مع دفتر القيود وميزان المراجعة + تجميع الأب/الابن ═════════

    [LivePgFact]
    public async Task Live_tree_matches_ledger_trial_balance_and_parent_aggregation()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // حركات حقيقية عبر الخدمات (بيع نقدي + مشتريات آجلة) ⇒ قيود مرحّلة فعلية بأرقام حقيقية.
        var item = await _pg.CreateItemAsync("A5-BAL-" + Guid.NewGuid().ToString("N")[..5], 10m, 6m, 50m);
        await using (var ctx = _pg.NewContext())
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(item, 3m, 10m));
        await using (var ctx = _pg.NewContext())
            await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx),
                new AccountingPgFixture.AllowAllManualPrice()).CreateAsync(_pg.NewPurchase(item, 2m, 6m));

        AccountBalanceTreeDto tree;
        await using (var ctx = _pg.NewContext())
            tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        // (أ) كل حساب تجميعي = مجموع أبنائه تمامًا (ابن = أحفاده، وأب = أبناؤه).
        foreach (var node in tree.Flattened.Where(n => n.Children.Count > 0))
        {
            Assert.Equal(node.Children.Sum(c => c.TotalDebit), node.TotalDebit);
            Assert.Equal(node.Children.Sum(c => c.TotalCredit), node.TotalCredit);
            Assert.Equal(node.Children.Sum(c => c.NetBalance), node.NetBalance);
        }

        // (ب) كل حساب تفصيلي: رصيده = صافي بنوده في دفتر القيود.
        await using (var ctx = _pg.NewContext())
        {
            var liveEntryIds = await ctx.Set<JournalEntry>().Where(e => !e.IsDeleted).Select(e => e.Id).ToListAsync();
            var lines = await ctx.Set<JournalEntryLine>().Where(l => !l.IsDeleted).ToListAsync();
            var valid = lines.Where(l => liveEntryIds.Contains(l.JournalEntryId)).ToList();
            var byAccount = valid.GroupBy(l => l.AccountId)
                .ToDictionary(g => g.Key, g => (D: g.Sum(x => x.DebitAmount), C: g.Sum(x => x.CreditAmount)));

            foreach (var node in tree.Flattened.Where(n => !n.IsAggregate))
            {
                var sums = byAccount.TryGetValue(node.Id, out var s) ? s : (D: 0m, C: 0m);
                Assert.Equal(Math.Round(sums.D - sums.C, 2), node.NetBalance);
            }

            // (ج) إجماليات الشجرة (من الجذور) = إجمالي الدفتر (مدين = دائن).
            var ledgerDebit = Math.Round(valid.Sum(l => l.DebitAmount), 2);
            var ledgerCredit = Math.Round(valid.Sum(l => l.CreditAmount), 2);
            Assert.Equal(ledgerDebit, ledgerCredit);
            Assert.Equal(ledgerDebit, tree.TotalDebit);
            Assert.Equal(ledgerCredit, tree.TotalCredit);
        }

        // (د) مطابقة مع ميزان المراجعة (نفس المصدر: بنود القيود).
        await using (var ctx = _pg.NewContext())
        {
            var trial = await new ReportService(ctx).GetTrialBalanceAsync();
            var leafDebits = tree.Flattened.Where(n => !n.IsAggregate).Sum(n => n.TotalDebit);
            var leafCredits = tree.Flattened.Where(n => !n.IsAggregate).Sum(n => n.TotalCredit);

            Assert.Equal(Math.Round(leafDebits, 2), Math.Round(trial.TotalDebit, 2));
            Assert.Equal(Math.Round(leafCredits, 2), Math.Round(trial.TotalCredit, 2));
        }

        // (هـ) توثيق سلوك النظام الحالي: لا حركات مباشرة على الحسابات التجميعية.
        Assert.Empty(tree.AggregateAccountsWithDirectMovements);
        _out.WriteLine($"شجرة مباشرة (بلا فترة): حساب={tree.Flattened.Count}، مدين={tree.TotalDebit:N2}، دائن={tree.TotalCredit:N2}");
    }

    // ═════════ (13) الأداء: 300 ورقة ⇒ عدد استعلامات ثابت (لا N+1) ═════════

    [LivePgFact]
    public async Task Live_large_tree_is_aggregated_with_a_constant_number_of_queries()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        const int childCount = 20;
        const int grandChildCount = 15;               // 20 × 15 = 300 ورقة

        await using (var ctx = _pg.NewContext())
        {
            // حساب مقابل ثابت (ورقة موجودة في الشجرة المزروعة) لقيد متوازن لكل ورقة جديدة.
            var contraAccountId = await ctx.Set<Account>().Where(a => a.Code == "1100").Select(a => a.Id).FirstAsync();

            var root = new Account
            {
                Id = Guid.NewGuid(), Code = "9", NameAr = "أصول اختبار الأداء",
                AccountType = AccountType.Asset, NormalBalance = NormalBalance.Debit,
                IsActive = true, CreatedAt = DateTime.UtcNow
            };
            ctx.Set<Account>().Add(root);

            for (var c = 0; c < childCount; c++)
            {
                var child = new Account
                {
                    Id = Guid.NewGuid(), Code = $"9{c:00}", NameAr = $"ابن أداء {c}",
                    AccountType = AccountType.Asset, NormalBalance = NormalBalance.Debit,
                    ParentAccountId = root.Id, IsActive = true, CreatedAt = DateTime.UtcNow
                };
                ctx.Set<Account>().Add(child);

                for (var g = 0; g < grandChildCount; g++)
                {
                    var leaf = new Account
                    {
                        Id = Guid.NewGuid(), Code = $"9{c:00}{g:00}", NameAr = $"حفيد أداء {c}-{g}",
                        AccountType = AccountType.Asset, NormalBalance = NormalBalance.Debit,
                        ParentAccountId = child.Id, IsActive = true, CreatedAt = DateTime.UtcNow
                    };
                    ctx.Set<Account>().Add(leaf);

                    var entry = new JournalEntry
                    {
                        Id = Guid.NewGuid(), EntryNumber = $"JE-PERF-{c:00}{g:00}",
                        EntryType = JournalEntryType.Manual, EntryDate = DateTime.Today,
                        Description = "perf", CreatedAt = DateTime.UtcNow
                    };
                    ctx.Set<JournalEntry>().Add(entry);
                    ctx.Set<JournalEntryLine>().Add(new JournalEntryLine
                    {
                        Id = Guid.NewGuid(), JournalEntryId = entry.Id, AccountId = leaf.Id,
                        DebitAmount = 10m, CreatedAt = DateTime.UtcNow
                    });
                    ctx.Set<JournalEntryLine>().Add(new JournalEntryLine
                    {
                        Id = Guid.NewGuid(), JournalEntryId = entry.Id, AccountId = contraAccountId,
                        CreditAmount = 10m, CreatedAt = DateTime.UtcNow
                    });
                }
            }

            await ctx.SaveChangesAsync();
        }

        var counter = new CountingInterceptor();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        AccountBalanceTreeDto tree;
        await using (var ctx = _pg.NewContextWithInterceptor(counter))
            tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync(
                new AccountBalanceQuery { From = DateTime.Today, To = DateTime.Today });

        watch.Stop();

        var perfRoot = tree.Flattened.Single(n => n.Code == "9");
        _out.WriteLine($"عدد الحسابات={tree.Flattened.Count} أوامر SQL={counter.Commands} الزمن={watch.ElapsedMilliseconds}ms");

        // (أ) عدد استعلامات ثابت بصرف النظر عن عدد الحسابات (300 ورقة) ⇒ لا N+1.
        Assert.True(counter.Commands <= 4, $"عدد أوامر SQL غير ثابت (احتمال N+1): {counter.Commands}");
        Assert.Equal(childCount, perfRoot.Children.Count);
        Assert.All(perfRoot.Children, c => Assert.Equal(grandChildCount, c.Children.Count));

        // (ب) التجميع الهرمي صحيح على بيانات كبيرة: الابن = 15 × 10، والجذر = 20 × 150.
        Assert.All(perfRoot.Children, c => Assert.Equal(grandChildCount * 10m, c.TotalDebit));
        Assert.Equal(childCount * grandChildCount * 10m, perfRoot.TotalDebit);
        Assert.Equal(grandChildCount * 10m, perfRoot.Children[0].NetBalance);
        Assert.Equal(1, perfRoot.Level);
        Assert.All(perfRoot.Children, c => Assert.Equal(2, c.Level));
        Assert.All(perfRoot.Children, c => Assert.All(c.Children, g => Assert.Equal(3, g.Level)));
    }
}
