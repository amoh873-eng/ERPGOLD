using System.Data.Common;
using System.Globalization;
using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace ERPSystem.Tests;

/// <summary>
/// Phase B — الأرصدة الافتتاحية الرسمية على PostgreSQL حقيقي معزول
/// (قاعدة erp_a5_* تُنشأ وتُحذف ذاتيًا؛ لا تُلمس أي قاعدة منشورة).
///
/// تغطي الاختبارات المطلوبة: الترحيل المتوازن + الظهور في ميزان المراجعة، رفض غير المتوازن مع
/// بيان الفرق، رفض الحساب الرئيسي/غير الموجود، منع التكرار، التزامن (إنشاءً وترحيلاً)،
/// التراجع الذري بلا أثر جزئي، تطابق CurrentBalance مع دفتر القيود وفق NormalBalance،
/// ومنع التعديل/الحذف بعد الترحيل مع القيد العكسي المرتبط بالأصل.
/// </summary>
public class OpeningBalanceTests : IClassFixture<AccountingPgFixture>
{
    private const string Cash = "1100";    // أصل — طبيعته مدين
    private const string Payable = "2200"; // خصم — طبيعته دائن
    private const string ParentRoot = "1"; // حساب رئيسي (له أبناء) — مرفوض

    private readonly AccountingPgFixture _pg;

    public OpeningBalanceTests(AccountingPgFixture pg) => _pg = pg;

    // ───────────────────────── أدوات ─────────────────────────

    private static CreateOpeningBalanceBatchDto Batch(int year, DateTime? date = null,
        params (string Code, decimal Debit, decimal Credit)[] lines) => new()
    {
        FiscalYear = year,
        EntryDate = date ?? new DateTime(year, 1, 1),
        Reference = $"OPEN-{year}-TEST",
        Description = $"أرصدة افتتاحية تجريبية {year}",
        Lines = lines.Select(l => new OpeningBalanceLineInput
        {
            AccountCode = l.Code,
            DebitAmount = l.Debit,
            CreditAmount = l.Credit,
            // تفصيل الحسابات الرقابية أصبح إلزاميًا (2026-10-25) لمنع انفصال الرقابي عن السجل المساعد:
            // 2200 ⇒ مورد · 1200 ⇒ عميل. (بقية الحسابات تبقى بلا تفصيل كما هي.)
            SupplierCode = l.Code == Payable ? AccountingPgFixture.SupplierCode : null,
            CustomerCode = l.Code == "1200" ? AccountingPgFixture.CustomerCode : null
        }).ToList()
    };

    private static async Task<decimal> StoredBalanceAsync(AccountingPgFixture pg, string code)
    {
        await using var ctx = pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code).Select(a => a.CurrentBalance).FirstAsync();
    }

    private static async Task<(decimal Debit, decimal Credit)> LedgerAsync(AccountingPgFixture pg, string code)
    {
        await using var ctx = pg.NewContext();
        var id = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code).Select(a => a.Id).FirstAsync();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == id && !l.IsDeleted).ToListAsync();
        return (lines.Sum(l => l.DebitAmount), lines.Sum(l => l.CreditAmount));
    }

    private static async Task<int> CountEntriesAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    private static async Task<int> CountActiveBatchesAsync(AccountingPgFixture pg, int year)
    {
        await using var ctx = pg.NewContext();
        return await ctx.Set<OpeningBalanceBatch>().CountAsync(b =>
            b.FiscalYear == year && b.ReversalOfBatchId == null
            && b.Status != OpeningBalanceBatchStatus.Reversed);
    }

    /// <summary>يعترض أول أمر مطابق ويرميه مرة واحدة (يحاكي فشلاً عابراً وسط المعاملة).</summary>
    private sealed class FailOnceInterceptor : DbCommandInterceptor
    {
        private readonly string _match;
        private int _hit;

        public FailOnceInterceptor(string match) => _match = match;

        private void MaybeFail(DbCommand command)
        {
            if (!command.CommandText.Contains(_match, StringComparison.Ordinal)) return;
            if (Interlocked.Increment(ref _hit) > 1) return;
            throw new PostgresException("simulated transient failure", "ERROR", "ERROR",
                PostgresErrorCodes.SerializationFailure);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            MaybeFail(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return new ValueTask<InterceptionResult<int>>(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MaybeFail(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }

    // ───────────────────────── 1) ترحيل متوازن + ميزان المراجعة + اتساق الأرصدة ─────────────────────────

    [LivePgFact]
    public async Task Balanced_batch_posts_and_appears_in_trial_balance_with_consistent_balances()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2031;

        var cashBefore = await StoredBalanceAsync(_pg, Cash);
        var payableBefore = await StoredBalanceAsync(_pg, Payable);

        Guid batchId;
        string entryNumber;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);

            var draft = await svc.CreateDraftAsync(
                Batch(year, null, (Cash, 5000m, 0m), (Payable, 0m, 5000m)), "tester");
            Assert.Equal((int)OpeningBalanceBatchStatus.Draft, draft.Status);
            Assert.Equal(5000m, draft.TotalDebit);
            Assert.Equal(5000m, draft.TotalCredit);
            Assert.Equal("tester", draft.CreatedBy);

            var posted = await svc.PostAsync(draft.Id, "tester");
            batchId = posted.Id;
            entryNumber = posted.EntryNumber ?? "";

            Assert.Equal((int)OpeningBalanceBatchStatus.Posted, posted.Status);
            Assert.NotNull(posted.JournalEntryId);
            Assert.False(string.IsNullOrWhiteSpace(entryNumber));
            Assert.Equal("tester", posted.PostedBy);
            Assert.NotNull(posted.PostedAt);
        }

        // التحقق من قاعدة البيانات مباشرة (لا من استجابة الواجهة)
        await using (var ctx = _pg.NewContext())
        {
            var batch = await ctx.Set<OpeningBalanceBatch>().AsNoTracking().FirstAsync(b => b.Id == batchId);
            Assert.Equal(OpeningBalanceBatchStatus.Posted, batch.Status);
            Assert.NotNull(batch.JournalEntryId);

            var entry = await ctx.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
                .FirstAsync(e => e.Id == batch.JournalEntryId);
            Assert.Equal(JournalEntryType.OpeningBalance, entry.EntryType);
            Assert.Equal(entryNumber, entry.EntryNumber);
            Assert.Equal(2, entry.Lines.Count);
            Assert.Equal(entry.Lines.Sum(l => l.DebitAmount), entry.Lines.Sum(l => l.CreditAmount));
            Assert.Equal(5000m, entry.Lines.Sum(l => l.DebitAmount));
        }

        // الأرصدة المخزَّنة = السابق + المتوقع، ومطابقة لدفتر القيود وفق NormalBalance
        Assert.Equal(cashBefore + 5000m, await StoredBalanceAsync(_pg, Cash));       // أصل مدين
        Assert.Equal(payableBefore + 5000m, await StoredBalanceAsync(_pg, Payable)); // خصم دائن

        var (cashD, cashC) = await LedgerAsync(_pg, Cash);
        Assert.Equal(cashD - cashC, await StoredBalanceAsync(_pg, Cash));
        var (apD, apC) = await LedgerAsync(_pg, Payable);
        Assert.Equal(apC - apD, await StoredBalanceAsync(_pg, Payable));

        // ميزان المراجعة يعكس القيد الافتتاحي (يُبنى من بنود القيود لا من الرصيد المخزَّن)
        await using (var ctx = _pg.NewContext())
        {
            var tb = await new ReportService(ctx).GetTrialBalanceAsync();
            Assert.Equal(tb.TotalDebit, tb.TotalCredit);
            Assert.True(tb.Lines.Single(l => l.AccountCode == Cash).TotalDebit >= 5000m);
            Assert.True(tb.Lines.Single(l => l.AccountCode == Payable).TotalCredit >= 5000m);
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ───────────────────────── 2) رفض غير المتوازن مع بيان الفرق ─────────────────────────

    [LivePgFact]
    public async Task Unbalanced_batch_is_rejected_with_the_difference()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2032;
        var entriesBefore = await CountEntriesAsync(_pg);

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateDraftAsync(
                Batch(year, null, (Cash, 100m, 0m), (Payable, 0m, 60m)), "tester"));

            Assert.Contains("غير متوازن", ex.Message);
            Assert.Contains("الفرق", ex.Message);
            Assert.Contains(40m.ToString("N2", CultureInfo.CurrentCulture), ex.Message); // الفرق 100 - 60
        }

        // لا دفعة ولا قيد جزئي
        Assert.Equal(0, await CountActiveBatchesAsync(_pg, year));
        Assert.Equal(entriesBefore, await CountEntriesAsync(_pg));
    }


    // ───────────────────────── 3) رفض الحساب الرئيسي وقبول الورقة ─────────────────────────

    [LivePgFact]
    public async Task Parent_account_is_rejected_and_leaf_is_accepted()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2033;

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            await Assert.ThrowsAsync<ParentAccountMovementException>(() => svc.CreateDraftAsync(
                Batch(year, null, (ParentRoot, 100m, 0m), (Payable, 0m, 100m)), "tester"));
        }

        Assert.Equal(0, await CountActiveBatchesAsync(_pg, year));

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(
                Batch(year, null, (Cash, 100m, 0m), (Payable, 0m, 100m)), "tester");
            Assert.Equal((int)OpeningBalanceBatchStatus.Draft, draft.Status);
        }

        Assert.Equal(1, await CountActiveBatchesAsync(_pg, year));
    }

    // ───────────────────────── 4) رفض الحساب غير الموجود ─────────────────────────

    [LivePgFact]
    public async Task Missing_account_is_rejected()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2034;

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateDraftAsync(
                Batch(year, null, ("9999", 100m, 0m), (Payable, 0m, 100m)), "tester"));

            Assert.Contains("غير موجود", ex.Message);
        }

        Assert.Equal(0, await CountActiveBatchesAsync(_pg, year));
    }


    // ───────────────────────── 5) منع تكرار الدفعة لنفس السنة المالية ─────────────────────────

    [LivePgFact]
    public async Task Duplicate_batch_for_same_fiscal_year_is_rejected()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2035;

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(Batch(year, null, (Cash, 100m, 0m), (Payable, 0m, 100m)), "tester");
            await svc.PostAsync(draft.Id, "tester");
        }

        Assert.Equal(1, await CountActiveBatchesAsync(_pg, year));

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateDraftAsync(
                Batch(year, null, (Cash, 200m, 0m), (Payable, 0m, 200m)), "tester"));
            Assert.Contains("بالفعل", ex.Message);
        }

        Assert.Equal(1, await CountActiveBatchesAsync(_pg, year));
    }

    // ───────────────────────── 6) إنشاء متزامن ⇒ دفعة واحدة فقط ─────────────────────────

    [LivePgFact]
    public async Task Concurrent_creation_creates_only_one_batch()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2036;
        const int parallel = 4;

        var successes = 0;
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(async _ =>
        {
            try
            {
                await using var ctx = _pg.NewContext();
                var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
                await svc.CreateDraftAsync(Batch(year, null, (Cash, 100m, 0m), (Payable, 0m, 100m)), "tester");
                Interlocked.Increment(ref successes);
            }
            catch (InvalidOperationException)
            {
                // منع التكرار المتزامن (انتهاك الفهرس الفريد) — متوقع للخاسر.
            }
        }));

        Assert.Equal(1, successes);
        Assert.Equal(1, await CountActiveBatchesAsync(_pg, year));
    }

    // ───────────────────────── 7) ترحيل متزامن ⇒ قيد واحد فقط ─────────────────────────

    [LivePgFact]
    public async Task Concurrent_posting_creates_only_one_journal_entry()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2037;
        const int parallel = 4;

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            batchId = (await AccountingPgFixture.NewOpeningBalanceService(ctx)
                .CreateDraftAsync(Batch(year, null, (Cash, 700m, 0m), (Payable, 0m, 700m)), "tester")).Id;
        }

        var cashBefore = await StoredBalanceAsync(_pg, Cash);
        var entriesBefore = await CountEntriesAsync(_pg);

        var successes = 0;
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(async _ =>
        {
            try
            {
                await using var ctx = _pg.NewContext();
                await AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "tester");
                Interlocked.Increment(ref successes);
            }
            catch (InvalidOperationException)
            {
                // قفل صف الدفعة + الحالة Posted ⇒ الخاسرون يُرفضون.
            }
        }));

        Assert.Equal(1, successes);
        Assert.Equal(entriesBefore + 1, await CountEntriesAsync(_pg));      // قيد واحد فقط
        Assert.Equal(cashBefore + 700m, await StoredBalanceAsync(_pg, Cash)); // طُبّق مرة واحدة

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ───────────────────────── 8) التراجع الذري بلا أثر جزئي ─────────────────────────

    [LivePgFact]
    public async Task Failed_posting_rolls_back_without_partial_rows()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2038;

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            batchId = (await AccountingPgFixture.NewOpeningBalanceService(ctx)
                .CreateDraftAsync(Batch(year, null, (Cash, 900m, 0m), (Payable, 0m, 900m)), "tester")).Id;
        }

        var cashBefore = await StoredBalanceAsync(_pg, Cash);
        var payableBefore = await StoredBalanceAsync(_pg, Payable);
        var entriesBefore = await CountEntriesAsync(_pg);

        // فشل عابر عند تحديث صف الدفعة (بعد إدراج القيد وبنوده وتحديث الأرصدة) ⇒ تراجع كامل.
        var interceptor = new FailOnceInterceptor("UPDATE \"OpeningBalanceBatches\"");
        await using (var ctx = _pg.NewContextWithInterceptor(interceptor, retryOnFailure: false))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "tester"));
        }

        await using (var ctx = _pg.NewContext())
        {
            var batch = await ctx.Set<OpeningBalanceBatch>().AsNoTracking().FirstAsync(b => b.Id == batchId);
            Assert.Equal(OpeningBalanceBatchStatus.Draft, batch.Status); // لم تترحّل
            Assert.Null(batch.JournalEntryId);
        }

        Assert.Equal(entriesBefore, await CountEntriesAsync(_pg));          // لا قيد جزئي
        Assert.Equal(cashBefore, await StoredBalanceAsync(_pg, Cash));      // لا تحديث رصيد منفصل
        Assert.Equal(payableBefore, await StoredBalanceAsync(_pg, Payable));

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }


    // ───────────────────────── 9) صحة المدين/الدائن وفق NormalBalance ─────────────────────────

    [LivePgFact]
    public async Task Debit_credit_follow_normal_balance_after_posting()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2039;

        var cashBefore = await StoredBalanceAsync(_pg, Cash);
        var payableBefore = await StoredBalanceAsync(_pg, Payable);

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(
                Batch(year, null, (Cash, 1250m, 0m), (Payable, 0m, 1250m)), "tester");
            await svc.PostAsync(draft.Id, "tester");
        }

        // أصل طبيعته مدين: يزيد بالمدين (مدين − دائن)
        var (cashD, cashC) = await LedgerAsync(_pg, Cash);
        Assert.Equal(cashBefore + 1250m, await StoredBalanceAsync(_pg, Cash));
        Assert.Equal(cashD - cashC, await StoredBalanceAsync(_pg, Cash));

        // خصم طبيعته دائن: يزيد بالدائن (دائن − مدين)
        var (apD, apC) = await LedgerAsync(_pg, Payable);
        Assert.Equal(payableBefore + 1250m, await StoredBalanceAsync(_pg, Payable));
        Assert.Equal(apC - apD, await StoredBalanceAsync(_pg, Payable));
    }

    // ───────────────────────── 10) منع التعديل/الحذف بعد الترحيل ─────────────────────────

    [LivePgFact]
    public async Task Posted_batch_cannot_be_edited_or_deleted()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2040;

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            batchId = (await svc.CreateDraftAsync(
                Batch(year, null, (Cash, 300m, 0m), (Payable, 0m, 300m)), "tester")).Id;
            await svc.PostAsync(batchId, "tester");

            var update = new UpdateOpeningBalanceBatchDto
            {
                EntryDate = new DateTime(year, 6, 1),
                Lines = new List<OpeningBalanceLineInput>
                {
                    new() { AccountCode = Cash, DebitAmount = 1m },
                    new() { AccountCode = Payable, CreditAmount = 1m }
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateDraftAsync(batchId, update, "tester"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteDraftAsync(batchId, "tester"));
        }

        Assert.Equal(1, await CountActiveBatchesAsync(_pg, year));
    }

    // ───────────────────────── 11) العكس: ارتباط بالأصل + الأثر الصافي صفر ─────────────────────────

    [LivePgFact]
    public async Task Reversal_links_to_original_and_nets_to_zero()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2041;

        var cashBefore = await StoredBalanceAsync(_pg, Cash);
        var payableBefore = await StoredBalanceAsync(_pg, Payable);

        Guid originalId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(
                Batch(year, null, (Cash, 400m, 0m), (Payable, 0m, 400m)), "tester");
            originalId = (await svc.PostAsync(draft.Id, "tester")).Id;
        }

        Assert.Equal(cashBefore + 400m, await StoredBalanceAsync(_pg, Cash));

        Guid reversalId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var reversal = await svc.ReverseAsync(originalId, new DateTime(year, 12, 31), "reverser");
            reversalId = reversal.Id;

            Assert.Equal((int)OpeningBalanceBatchStatus.Posted, reversal.Status);
            Assert.Equal(originalId, reversal.ReversalOfBatchId);
            Assert.NotNull(reversal.JournalEntryId);
            Assert.Equal("reverser", reversal.PostedBy);
        }

        await using (var ctx = _pg.NewContext())
        {
            var original = await ctx.Set<OpeningBalanceBatch>().AsNoTracking().FirstAsync(b => b.Id == originalId);
            Assert.Equal(OpeningBalanceBatchStatus.Reversed, original.Status);
            Assert.Equal(reversalId, original.ReversedByBatchId);

            var reversal = await ctx.Set<OpeningBalanceBatch>().AsNoTracking()
                .Include(b => b.JournalEntry).ThenInclude(e => e!.Lines)
                .FirstAsync(b => b.Id == reversalId);
            Assert.Equal(400m, reversal.JournalEntry!.Lines.Sum(l => l.DebitAmount));
            Assert.Equal(400m, reversal.JournalEntry!.Lines.Sum(l => l.CreditAmount));
        }

        // الأثر الصافي = صفر: عاد الرصيد إلى ما كان قبل الترحيل
        Assert.Equal(cashBefore, await StoredBalanceAsync(_pg, Cash));
        Assert.Equal(payableBefore, await StoredBalanceAsync(_pg, Payable));

        // بعد العكس يمكن إنشاء دفعة افتتاحية جديدة لنفس السنة (المعكوسة مستثناة من التفرد)
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var again = await svc.CreateDraftAsync(
                Batch(year, null, (Cash, 150m, 0m), (Payable, 0m, 150m)), "tester");
            Assert.Equal((int)OpeningBalanceBatchStatus.Draft, again.Status);
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }


    // ───────────────────────── 12) الصلاحية: الفشل المغلق عند غياب الإذن ─────────────────────────

    [LivePgFact]
    public async Task Operations_are_rejected_without_permission()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2042;

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(
                ctx, new AccountingPgFixture.DenyAllOpeningBalance());

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateDraftAsync(
                Batch(year, null, (Cash, 10m, 0m), (Payable, 0m, 10m)), "tester"));
        }

        Assert.Equal(0, await CountActiveBatchesAsync(_pg, year));
    }

    // ───────────────────────── 13) الفترة المالية: تاريخ خارج السنة ⇒ رفض ─────────────────────────

    [LivePgFact]
    public async Task Fiscal_year_mismatch_is_rejected()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2043;

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateDraftAsync(
                Batch(year, new DateTime(year + 1, 1, 1), (Cash, 10m, 0m), (Payable, 0m, 10m)), "tester"));

            Assert.Contains("خارج السنة المالية", ex.Message);
        }

        Assert.Equal(0, await CountActiveBatchesAsync(_pg, year));
    }

    // ───────────────────────── 14) قواعد الصلاحية النقية (بلا قاعدة بيانات) ─────────────────────────

    [Fact]
    public void OpeningBalanceRules_use_existing_accounting_permissions()
    {
        // مدير النظام → مصرّح دائماً
        Assert.True(OpeningBalanceRules.IsAuthorized(
            new[] { "Admin" }, Array.Empty<string>(), OpeningBalanceRules.AddAction));

        // دور «محاسبة» بلا أذونات صريحة → وصول كامل للموديول (السلوك الحالي للنظام)
        Assert.True(OpeningBalanceRules.IsAuthorized(
            new[] { "Accounting" }, Array.Empty<string>(), OpeningBalanceRules.EditAction));

        // أذونات صريحة للموديول → يلزم الفعل المطابق تحديداً
        Assert.True(OpeningBalanceRules.IsAuthorized(
            Array.Empty<string>(), new[] { "Accounting.Add" }, OpeningBalanceRules.AddAction));
        Assert.False(OpeningBalanceRules.IsAuthorized(
            Array.Empty<string>(), new[] { "Accounting.Add" }, OpeningBalanceRules.EditAction));
        Assert.False(OpeningBalanceRules.IsAuthorized(
            Array.Empty<string>(), new[] { "Accounting.Add" }, OpeningBalanceRules.DeleteAction));

        // بلا أي دور/إذن محاسبي → مرفوض
        Assert.False(OpeningBalanceRules.IsAuthorized(
            new[] { "Cashier" }, new[] { "Pos.View" }, OpeningBalanceRules.AddAction));
    }
}

