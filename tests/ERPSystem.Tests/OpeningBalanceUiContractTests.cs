using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Phase B.1 — عقد الواجهة على PostgreSQL حقيقي معزول.
///
/// لا يوجد متصفح/Playwright في هذه البيئة، لذلك نختبر المسار «من مدخلات الشاشة إلى قاعدة البيانات»
/// بنفس المدخلات والمخرجات التي تستخدمها الشاشة فعليًا:
///   - مصدر قائمة الحسابات في النموذج هو IAccountService.GetLeafAccountsAsync (حسابات متحركة فقط)،
///     والشاشة لا تعرض أي حساب رئيسي ⇒ لا يمكن إدخال Parent من الواجهة.
///   - القراءة/الكتابة عبر IOpeningBalanceService حصراً (نفس ما تستدعيه الأزرار والحوارات).
///   - العرض بعد الترحيل عبر GetAllAsync/GetByIdAsync (نفس ما تحمّله الصفحة)، مع رقم القيد.
/// </summary>
public class OpeningBalanceUiContractTests : IClassFixture<AccountingPgFixture>
{
    private const string Cash = "1100";
    private const string Payable = "2200";

    private readonly AccountingPgFixture _pg;

    public OpeningBalanceUiContractTests(AccountingPgFixture pg) => _pg = pg;

    private static async Task<List<AccountDto>> LeafPickerAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        return await new AccountService(ctx).GetLeafAccountsAsync();
    }

    private static async Task<int> CountEntriesAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        return await ctx.Set<Domain.Entities.JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    private static CreateOpeningBalanceBatchDto Draft(int year, string debitCode, decimal debit,
        string creditCode, decimal credit) => new()
    {
        FiscalYear = year,
        EntryDate = new DateTime(year, 1, 1),
        Reference = $"UI-{year}",
        Description = $"مدخلات شاشة {year}",
        Lines = new()
        {
            new() { AccountCode = debitCode, DebitAmount = debit, SupplierCode = debitCode == Payable ? AccountingPgFixture.SupplierCode : null, CustomerCode = debitCode == "1200" ? AccountingPgFixture.CustomerCode : null },
            new() { AccountCode = creditCode, CreditAmount = credit, SupplierCode = creditCode == Payable ? AccountingPgFixture.SupplierCode : null, CustomerCode = creditCode == "1200" ? AccountingPgFixture.CustomerCode : null }
        }
    };

    [LivePgFact]
    public async Task Ui_picker_offers_leaves_only_and_posted_batch_reopens_with_entry_number()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2051;

        // (1) قائمة الحسابات في النموذج = الحسابات المتحركة فقط.
        // التعريف المعتمد في النظام: الحساب «رئيسي/متحرك» يُشتق من وجود أبناء فعليين، لا من مستواه.
        // لذلك نتحقق من عدم ظهور أي حساب له أبناء (Parent)، بدل افتراض أن كل جذر رئيسي
        // (فهناك جذر بلا أبناء مثل "3 حقوق الملكية" ويُعدّ ورقةً بحكم التعريف).
        var leaves = await LeafPickerAsync(_pg);
        Assert.NotEmpty(leaves);
        Assert.Contains(leaves, a => a.Code == Cash);
        Assert.Contains(leaves, a => a.Code == Payable);

        List<string> parentCodes;
        await using (var ctx = _pg.NewContext())
        {
            var parentIds = await ctx.Set<Account>().AsNoTracking()
                .Where(a => a.ParentAccountId != null && !a.IsDeleted)
                .Select(a => a.ParentAccountId!.Value)
                .Distinct()
                .ToListAsync();

            parentCodes = await ctx.Set<Account>().AsNoTracking()
                .Where(a => parentIds.Contains(a.Id) && !a.IsDeleted)
                .Select(a => a.Code)
                .ToListAsync();
        }

        Assert.Contains("1", parentCodes); // الأصول: جذر له أبناء
        Assert.Contains("5", parentCodes); // المصروفات: جذر له أبناء
        foreach (var parent in parentCodes)
            Assert.DoesNotContain(leaves, a => a.Code == parent);

        // (2) إدخال مسودة متوازنة بأكواد مختارة من القائمة نفسها + الترحيل
        Guid batchId;
        string entryNumber;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(Draft(year, Cash, 750m, Payable, 750m), "ui-user");
            Assert.Equal((int)OpeningBalanceBatchStatus.Draft, draft.Status);
            Assert.Equal(750m, draft.TotalDebit);
            Assert.Equal(750m, draft.TotalCredit);

            var posted = await svc.PostAsync(draft.Id, "ui-user");
            batchId = posted.Id;
            entryNumber = posted.EntryNumber ?? "";
            Assert.Equal((int)OpeningBalanceBatchStatus.Posted, posted.Status);
            Assert.False(string.IsNullOrWhiteSpace(entryNumber)); // رقم القيد يظهر بعد الترحيل
        }

        // (3) إعادة فتح الدفعة كما تعيدها الشاشة (قائمة + تفاصيل)
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);

            var row = (await svc.GetAllAsync()).Single(b => b.FiscalYear == year);
            Assert.Equal((int)OpeningBalanceBatchStatus.Posted, row.Status);
            Assert.Equal(entryNumber, row.EntryNumber);
            Assert.NotNull(row.JournalEntryId);
            Assert.Equal(2, row.Lines.Count);
            Assert.Equal(entryNumber, (await svc.GetByIdAsync(row.Id))!.EntryNumber);

            // زر الإنشاء يرى الدفعة الفعّالة لنفس السنة ⇒ يمنع التكرار
            Assert.NotNull(await svc.GetActiveForYearAsync(year));

            // منع التعديل والحذف بعد الترحيل (الخدمة هي الحارس، والشاشة تخفي الأزرار)
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteDraftAsync(row.Id, "ui-user"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateDraftAsync(row.Id,
                new UpdateOpeningBalanceBatchDto
                {
                    EntryDate = row.EntryDate,
                    Lines = new()
                    {
                        new() { AccountCode = Cash, DebitAmount = 1m },
                        new() { AccountCode = Payable, CreditAmount = 1m }
                    }
                }, "ui-user"));
        }

        // (4) ظهور القيد في ميزان المراجعة
        await using (var ctx = _pg.NewContext())
        {
            var tb = await new ReportService(ctx).GetTrialBalanceAsync();
            Assert.Equal(tb.TotalDebit, tb.TotalCredit);
            Assert.True(tb.Lines.Single(l => l.AccountCode == Cash).TotalDebit >= 750m);
            Assert.True(tb.Lines.Single(l => l.AccountCode == Payable).TotalCredit >= 750m);
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ───────── رفض مسودة غير متوازنة قبل السماح بالحفظ/الترحيل ─────────

    [LivePgFact]
    public async Task Ui_unbalanced_draft_is_blocked_with_the_difference()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2052;
        var entriesBefore = await CountEntriesAsync(_pg);

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateDraftAsync(Draft(year, Cash, 100m, Payable, 60m), "ui-user"));

            Assert.Contains("غير متوازن", ex.Message);
            Assert.Contains("الفرق", ex.Message); // رسالة الشاشة تُظهر الفرق
        }

        Assert.Equal(entriesBefore, await CountEntriesAsync(_pg)); // لا دفعة ولا قيد
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            Assert.Null(await svc.GetActiveForYearAsync(year));
        }
    }

    // ───────── الحساب الرئيسي لا يظهر في القائمة، ويُرفض إن أُرسل ─────────

    [LivePgFact]
    public async Task Ui_parent_account_is_absent_from_picker_and_rejected_when_submitted()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2053;

        var leaves = await LeafPickerAsync(_pg);
        Assert.DoesNotContain(leaves, a => a.Code == "1");

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            await Assert.ThrowsAsync<ParentAccountMovementException>(() =>
                svc.CreateDraftAsync(Draft(year, "1", 100m, Payable, 100m), "ui-user"));
            Assert.Null(await svc.GetActiveForYearAsync(year));
        }
    }

    // ───────── منع تكرار الترحيل عند الضغط المزدوج/الطلبات المتزامنة ─────────

    [LivePgFact]
    public async Task Ui_double_click_and_concurrent_post_create_only_one_entry()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2054;

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            batchId = (await AccountingPgFixture.NewOpeningBalanceService(ctx)
                .CreateDraftAsync(Draft(year, Cash, 320m, Payable, 320m), "ui-user")).Id;
        }

        var entriesBefore = await CountEntriesAsync(_pg);

        // (أ) ضغط مزدوج متتابع (كما في الواجهة): الثاني يُرفض
        await using (var ctx = _pg.NewContext())
            await AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "ui-user");
        await using (var ctx = _pg.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "ui-user"));

        // (ب) طلبات متزامنة على نفس المسودة: قيد واحد فقط
        const int parallel = 4;
        var successes = 0;
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(async _ =>
        {
            try
            {
                await using var ctx = _pg.NewContext();
                await AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "ui-user");
                Interlocked.Increment(ref successes);
            }
            catch (InvalidOperationException) { /* الحالة Posted تمنع التكرار */ }
        }));

        Assert.Equal(0, successes);                                    // كلها مرفوضة (مُرحَّلة سلفاً)
        Assert.Equal(entriesBefore + 1, await CountEntriesAsync(_pg)); // قيد واحد فقط
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ───────── الصلاحية: المستخدم غير المصرّح يُمنع خلفياً ─────────

    [LivePgFact]
    public async Task Ui_unauthorized_user_is_blocked_by_the_backend()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2055;

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(
                ctx, new AccountingPgFixture.DenyAllOpeningBalance());

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                svc.CreateDraftAsync(Draft(year, Cash, 10m, Payable, 10m), "ui-user"));

            Assert.Null(await svc.GetActiveForYearAsync(year));
        }
    }
}
