using System.Data.Common;
using System.Threading;
using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة الأولى (فحص الفجوات) — اختبارات فحص إضافية للأرصدة الافتتاحية، تغطي نقاطًا
/// **غير مغطاة** باختبارات Phase B القائمة. لا تُعدَّل أي شيفرة إنتاج.
///
/// تُشغَّل على قاعدة PostgreSQL معزولة تُنشأ وتُحذف ذاتيًا (AccountingPgFixture ⇒ erp_a5_*).
///
/// البنود المفحوصة:
///  1) رفض الحساب **غير النشط** (IsActive=false) عند الإدخال + عدم ظهوره في منتَقي الشاشة.
///  2) مطابقة مجموع بنود الدفعة لمجموع بنود القيد الناتج + ارتباط القيد بالدفعة (Reference/Id).
///  3) إعادة المحاولة بعد فشل عابر ⇒ ترحيل مرة واحدة فقط (لا قيد ثانٍ ولا رصيد مضاعف).
///  4) أثر رصيد افتتاحي على حساب رقابي (1200 العملاء) على **دفتر الأستاذ فقط** دون تحديث
///     الرصيد المساعد للعميل (`Customer.CurrentBalance`) ⇒ إثبات فجوة «الأرصدة الافتتاحية
///     للمساعدين/الأطراف غير مدعومة عبر الدفعة الرسمية».
///  5) منع **العكس المزدوج** لنفس الدفعة + عدم إنشاء قيد عكسي ثانٍ.
/// </summary>
public class OpeningBalanceGapProbeTests : IClassFixture<AccountingPgFixture>
{
    private const string Cash = "1100";        // أصل — مدين
    private const string Bank = "1101";        // أصل — مدين (نُستخدم لفحص الحساب غير النشط)
    private const string Receivable = "1200";  // أصل — ذمم العملاء (حساب رقابي)
    private const string Payable = "2200";     // خصم — ذمم الموردين

    private readonly AccountingPgFixture _pg;

    public OpeningBalanceGapProbeTests(AccountingPgFixture pg) => _pg = pg;

    // ───────────────────────── أدوات ─────────────────────────

    private static CreateOpeningBalanceBatchDto Batch(int year, string reference,
        params (string Code, decimal Debit, decimal Credit)[] lines) => new()
    {
        FiscalYear = year,
        EntryDate = new DateTime(year, 1, 5),
        Reference = reference,
        Description = $"فحص فجوات {year}",
        Lines = lines.Select(l => new OpeningBalanceLineInput
        {
            AccountCode = l.Code,
            DebitAmount = l.Debit,
            CreditAmount = l.Credit,
            // تفصيل الحسابات الرقابية إلزامي بعد إصلاح 2026-10-25 (منع انفصال الرقابي عن السجل المساعد):
            // 2200 ⇒ مورد · 1200 ⇒ عميل. وبنود 1300 تلزم بالصنف والمخزن والكمية والتكلفة.
            SupplierCode = l.Code == Payable ? AccountingPgFixture.SupplierCode : null,
            CustomerCode = l.Code == Receivable ? AccountingPgFixture.CustomerCode : null
        }).ToList()
    };

    private async Task<decimal> StoredAccountBalanceAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code).Select(a => a.CurrentBalance).FirstAsync();
    }

    private async Task<decimal> CustomerBalanceAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<int> CountEntriesAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    /// <summary>
    /// يحدد كود الحساب المتحرك (Leaf) الخاص بحساب ذمم العملاء الرقابي 1200:
    /// إن كان 1200 نفسه ورقةً متحركة يُستخدم، وإلا يُستخدم أول ورقة تحته (1200x).
    /// </summary>
    private async Task<string> ResolveReceivableLeafCodeAsync()
    {
        await using var ctx = _pg.NewContext();
        var leaves = await new AccountService(ctx).GetLeafAccountsAsync();
        if (leaves.Any(a => a.Code == Receivable)) return Receivable;
        var under = leaves.Where(a => a.Code.StartsWith(Receivable, StringComparison.Ordinal))
            .OrderBy(a => a.Code).FirstOrDefault();
        return under?.Code
               ?? throw new InvalidOperationException("لا توجد ورقة متحركة تحت حساب الذمم 1200 في الشجرة المزروعة.");
    }

    /// <summary>يعترض أول أمر مطابق ويرميه مرة واحدة (فشل عابر داخل المعاملة).</summary>
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
        { MaybeFail(command); return result; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        { MaybeFail(command); return new ValueTask<InterceptionResult<int>>(result); }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { MaybeFail(command); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { MaybeFail(command); return new ValueTask<InterceptionResult<DbDataReader>>(result); }
    }

    // ─────────── 1) الحساب غير النشط: مرفوض في الخدمة وغائب عن منتَقي الشاشة ───────────

    [LivePgFact]
    public async Task Probe1_Inactive_leaf_is_rejected_and_hidden_from_picker()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2061;

        // تعطيل حساب البنك **داخل قاعدة الاختبار المعزولة فقط**
        await using (var ctx = _pg.NewContext())
        {
            var bank = await ctx.Set<Account>().FirstAsync(a => a.Code == Bank);
            bank.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        try
        {
            // (أ) منتَقي الشاشة (نفس مصدر نموذج الإدخال) لا يعرض الحساب المعطَّل
            await using (var ctx = _pg.NewContext())
            {
                var leaves = await new AccountService(ctx).GetLeafAccountsAsync();
                Assert.DoesNotContain(leaves, a => a.Code == Bank);
                Assert.Contains(leaves, a => a.Code == Payable); // الحسابات النشطة تبقى ظاهرة
            }

            // (ب) إدخال الحساب المعطَّل عبر الخدمة ⇒ رفض صريح دون إنشاء دفعة
            await using (var ctx = _pg.NewContext())
            {
                var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
                await Assert.ThrowsAsync<InactiveAccountMovementException>(() => svc.CreateDraftAsync(
                    Batch(year, $"PROBE-INACTIVE-{year}", (Bank, 100m, 0m), (Payable, 0m, 100m)), "probe"));
            }

            await using (var ctx = _pg.NewContext())
            {
                var batches = await ctx.Set<OpeningBalanceBatch>()
                    .CountAsync(b => b.FiscalYear == year && b.ReversalOfBatchId == null);
                Assert.Equal(0, batches);
            }
        }
        finally
        {
            // إعادة التنشيط (النظافة داخل قاعدة الاختبار — لا أثر على أي قاعدة أخرى)
            await using var ctx = _pg.NewContext();
            var bank = await ctx.Set<Account>().FirstAsync(a => a.Code == Bank);
            bank.IsActive = true;
            await ctx.SaveChangesAsync();
        }
    }

    // ─────────── 2) مجموع بنود الدفعة = مجموع بنود القيد + الارتباط ───────────

    [LivePgFact]
    public async Task Probe2_Batch_lines_equal_entry_totals_and_entry_is_linked()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2062;
        const string reference = "PROBE-LINK-2062";

        var receivableLeaf = await ResolveReceivableLeafCodeAsync();
        var cashBefore = await StoredAccountBalanceAsync(Cash);
        var receivableBefore = await StoredAccountBalanceAsync(receivableLeaf);
        var payableBefore = await StoredAccountBalanceAsync(Payable);

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(Batch(year, reference,
                (Cash, 1000m, 0m), (receivableLeaf, 500m, 0m), (Payable, 0m, 1500m)), "probe");
            batchId = draft.Id;
            Assert.Equal(1500m, draft.TotalDebit);
            Assert.Equal(1500m, draft.TotalCredit);
            await svc.PostAsync(batchId, "probe");
        }

        await using (var ctx = _pg.NewContext())
        {
            var batch = await ctx.Set<OpeningBalanceBatch>().AsNoTracking()
                .Include(b => b.Lines).FirstAsync(b => b.Id == batchId);
            var entry = await ctx.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
                .FirstAsync(e => e.Id == batch.JournalEntryId);

            // (1) الارتباط: القيد الناتج مرتبط بالدفعة بنوعه ومرجعه
            Assert.Equal(JournalEntryType.OpeningBalance, entry.EntryType);
            Assert.Equal(reference, entry.ReferenceNumber);
            Assert.Equal(batch.JournalEntryId, entry.Id);

            // (2) التطابق: مجموع بنود الدفعة = مجموع بنود القيد (مدين/دائن + العدد)
            Assert.Equal(batch.Lines.Count, entry.Lines.Count);
            Assert.Equal(batch.Lines.Sum(l => l.DebitAmount), entry.Lines.Sum(l => l.DebitAmount));
            Assert.Equal(batch.Lines.Sum(l => l.CreditAmount), entry.Lines.Sum(l => l.CreditAmount));

            // (3) بنود القيد على نفس الحسابات بلا تبديل
            var batchAccounts = batch.Lines.Select(l => l.AccountId).OrderBy(x => x).ToList();
            var entryAccounts = entry.Lines.Select(l => l.AccountId).OrderBy(x => x).ToList();
            Assert.Equal(batchAccounts, entryAccounts);
        }

        // (4) الأرصدة المخزَّنة تحرّكت بمقدار البنود (مدين/دائن حسب الطبيعة)
        Assert.Equal(cashBefore + 1000m, await StoredAccountBalanceAsync(Cash));
        Assert.Equal(receivableBefore + 500m, await StoredAccountBalanceAsync(receivableLeaf));
        Assert.Equal(payableBefore + 1500m, await StoredAccountBalanceAsync(Payable));
    }

    // ─────────── 3) إعادة المحاولة بعد فشل عابر ⇒ ترحيل واحد فقط ───────────

    [LivePgFact]
    public async Task Probe3_Retry_after_transient_failure_posts_exactly_once()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2063;

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            batchId = (await AccountingPgFixture.NewOpeningBalanceService(ctx).CreateDraftAsync(
                Batch(year, $"PROBE-RETRY-{year}", (Cash, 900m, 0m), (Payable, 0m, 900m)), "probe")).Id;
        }

        var cashBefore = await StoredAccountBalanceAsync(Cash);
        var entriesBefore = await CountEntriesAsync();

        // محاولة أولى: فشل عابر عند تحديث صف الدفعة (بعد إدراج القيد والبنود) ⇒ تراجع كامل
        var interceptor = new FailOnceInterceptor("UPDATE \"OpeningBalanceBatches\"");
        await using (var ctx = _pg.NewContextWithInterceptor(interceptor, retryOnFailure: false))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "probe"));
        }

        await using (var ctx = _pg.NewContext())
        {
            var afterFailure = await ctx.Set<OpeningBalanceBatch>().AsNoTracking().FirstAsync(b => b.Id == batchId);
            Assert.Equal(OpeningBalanceBatchStatus.Draft, afterFailure.Status);
            Assert.Null(afterFailure.JournalEntryId);
        }

        // إعادة المحاولة (نفس الدفعة، بلا تغيير) ⇒ نجاح واحد فقط
        await using (var ctx = _pg.NewContext())
        {
            var posted = await AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "probe");
            Assert.Equal((int)OpeningBalanceBatchStatus.Posted, posted.Status);
            Assert.NotNull(posted.JournalEntryId);
        }

        Assert.Equal(entriesBefore + 1, await CountEntriesAsync());          // قيد واحد فقط
        Assert.Equal(cashBefore + 900m, await StoredAccountBalanceAsync(Cash)); // تطبيق واحد للرصيد

        // إعادة إرسال طلب الترحيل على دفعة مُرحَّلة ⇒ رفض (لا قيد ثانٍ ولا رصيد إضافي)
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "probe"));
        }

        Assert.Equal(entriesBefore + 1, await CountEntriesAsync());
        Assert.Equal(cashBefore + 900m, await StoredAccountBalanceAsync(Cash));
    }

    // ─────────── 4) الإصلاح مُثبت (محدَّث 2026-10-25): بند الرقابي يلزم بطرف ويُزامن السجل المساعد ───────────
    // ملاحظة تحديث: كانت هذه الفحص في المرحلة الأولى تُثبت الفجوة (الرصيد على 1200 يتحرك بينما
    // رصيد العميل لا يتحرك). بعد الإصلاح صار الإدخال **مرفوضًا** بلا طرف، ومع الطرف صار الرقابي
    // والسجل المساعد يتحركان **معًا** ⇒ حُدِّث الفحص ليقيس السلوك الجديد الصحيح (لا العكس).

    [LivePgFact]
    public async Task Probe4_Opening_balance_on_receivable_requires_customer_and_updates_subledger()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2064;

        var receivableLeaf = await ResolveReceivableLeafCodeAsync();
        Assert.Equal(Receivable, receivableLeaf); // الشجرة المعتمدة: 1200 ورقة متحركة (بلا أبناء)

        // (أ) بند على 1200 **بلا كود عميل** ⇒ رفض صريح قبل أي كتابة (منع عودة الفجوة).
        var entriesBefore = await CountEntriesAsync();
        await using (var ctx = _pg.NewContext())
        {
            var dto = new CreateOpeningBalanceBatchDto
            {
                FiscalYear = 2063,
                EntryDate = new DateTime(2063, 1, 5),
                Reference = "PROBE-PARTY-NOPARTY-2063",
                Lines = new List<OpeningBalanceLineInput>
                {
                    new() { AccountCode = receivableLeaf, DebitAmount = 100m },
                    new() { AccountCode = Payable, CreditAmount = 100m, SupplierCode = AccountingPgFixture.SupplierCode }
                }
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx).CreateDraftAsync(dto, "probe"));
            Assert.Contains("كود العميل", ex.Message);
        }
        Assert.Equal(entriesBefore, await CountEntriesAsync());

        // (ب) بند على 1200 **بكود عميل** ⇒ ترحيل ناجح + مزامنة رصيد العميل (مطابقة بالبناء).
        var customerBefore = await CustomerBalanceAsync(_pg.CustomerId);
        var receivableBefore = await StoredAccountBalanceAsync(receivableLeaf);

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(Batch(year, $"PROBE-PARTY-{year}",
                (receivableLeaf, 1200m, 0m), (Payable, 0m, 1200m)), "probe");
            var posted = await svc.PostAsync(draft.Id, "probe");

            // البند يحمل هوية الطرف في العرض (تدقيق)
            var partyLine = posted.Lines.Single(l => l.AccountCode == receivableLeaf);
            Assert.Equal(AccountingPgFixture.CustomerCode, partyLine.CustomerCode);
            Assert.Equal("عميل", partyLine.DetailKindAr);
        }

        // الرقابي والرصيد المساعد تحرّكا بنفس المقدار ⇒ لا فرق رقابي جديد
        Assert.Equal(receivableBefore + 1200m, await StoredAccountBalanceAsync(receivableLeaf));
        Assert.Equal(customerBefore + 1200m, await CustomerBalanceAsync(_pg.CustomerId));
    }

    // ─────────── 5) منع العكس المزدوج + قيد عكسي واحد ───────────

    [LivePgFact]
    public async Task Probe5_Double_reversal_is_rejected_and_creates_single_reversal_entry()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2065;

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            batchId = (await svc.CreateDraftAsync(
                Batch(year, $"PROBE-REV-{year}", (Cash, 500m, 0m), (Payable, 0m, 500m)), "probe")).Id;
            await svc.PostAsync(batchId, "probe");
        }

        var cashBeforeReversal = await StoredAccountBalanceAsync(Cash);
        var entriesBefore = await CountEntriesAsync();

        await using (var ctx = _pg.NewContext())
        {
            var reversal = await AccountingPgFixture.NewOpeningBalanceService(ctx)
                .ReverseAsync(batchId, new DateTime(year, 12, 31), "probe");
            Assert.Equal((int)OpeningBalanceBatchStatus.Posted, reversal.Status);
            Assert.Equal(batchId, reversal.ReversalOfBatchId);
            Assert.NotNull(reversal.JournalEntryId);
        }

        Assert.Equal(entriesBefore + 1, await CountEntriesAsync()); // قيد عكسي واحد فقط

        // الأثر الصافي: الرصيد يعود إلى قيمته قبل ترحيل الدفعة الأصلية (أي − 500 عن قيمته بعد الترحيل)
        Assert.Equal(cashBeforeReversal - 500m, await StoredAccountBalanceAsync(Cash));

        // عكس ثانٍ لنفس الدفعة ⇒ رفض صريح بلا أثر جديد.
        // ملاحظة فحص: الرفض يقع على فحص **الحالة** (الأصل أصبح Reversed) قبل فحص ReversedByBatchId،
        // أي أن فرع «معكوسة مسبقاً» في الخدمة غير قابل للوصول في التدفق الطبيعي (كود دفاعي).
        await using (var ctx = _pg.NewContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx)
                    .ReverseAsync(batchId, new DateTime(year, 12, 31), "probe"));
            Assert.Contains("لا يمكن عكس", ex.Message);
        }

        Assert.Equal(entriesBefore + 1, await CountEntriesAsync());
    }
}
