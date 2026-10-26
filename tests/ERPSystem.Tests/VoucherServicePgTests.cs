using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة B — اختبارات تكامل حية لسندات القبض والصرف على PostgreSQL معزول
/// (قاعدة مؤقتة تُنشأ وتُحذف ذاتيًا عبر <see cref="AccountingPgFixture"/>، وبها شجرة الحسابات المزروعة).
///
/// تغطّي: عدم إنشاء قيد قبل الترحيل · توازن القيد وربطه بالسند · منع الترحيل المزدوج وإعادة المحاولة ·
/// التزامن · ذرية الفشل · الإلغاء مع حفظ الأثر · فحص الصلاحيات الخلفي (فشل مغلق) ·
/// ثبات ميزان المراجعة بعد الترحيل · إعادة الترحيل بلا تكرار · الاختلاف الصحيح لسند الصرف.
/// </summary>
public class VoucherServicePgTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public VoucherServicePgTests(AccountingPgFixture pg) => _pg = pg;

    // ───────────────────────── تصاريح اختبار (تُحقن خلفيًا كما في الإنتاج) ─────────────────────────

    private sealed class AllowAllVouchers : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public string? CurrentUserName() => "tester";
    }

    private sealed class DenyAllVouchers : IVoucherAuthorization
    {
        public bool CanView() => false;
        public bool CanCreate() => false;
        public bool CanEdit() => false;
        public bool CanApprove() => false;
        public bool CanPost() => false;
        public bool CanCancel() => false;
        public string? CurrentUserName() => "denied-user";
    }

    private sealed class AllowCreateOnlyVouchers : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => false;
        public bool CanPost() => false;
        public bool CanCancel() => false;
        public string? CurrentUserName() => "creator-only";
    }

    private static ReceiptVoucherService NewReceiptService(AppDbContext ctx, IVoucherAuthorization? auth = null)
        => new(ctx, new JournalEntryService(ctx), auth ?? new AllowAllVouchers());

    private static PaymentVoucherService NewPaymentService(AppDbContext ctx, IVoucherAuthorization? auth = null)
        => new(ctx, new JournalEntryService(ctx), auth ?? new AllowAllVouchers());

    // ───────────────────────── أدوات ─────────────────────────

    private CreateReceiptVoucherDto NewReceiptDto(Guid customerId, decimal amount, int paymentMethod = 1)
        => new()
        {
            CustomerId = customerId,
            VoucherDate = DateTime.Today,
            PaymentMethod = paymentMethod,
            Amount = amount,
            Description = "سند قبض اختبار"
        };

    private CreatePaymentVoucherDto NewPaymentDto(Guid supplierId, decimal amount, int paymentMethod = 1)
        => new()
        {
            SupplierId = supplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = paymentMethod,
            Amount = amount,
            Description = "سند صرف اختبار"
        };

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Account>().Where(a => a.Id == accountId).Select(a => a.CurrentBalance).FirstAsync();
    }

    private async Task<int> JournalCountForVoucherAsync(string voucherNumber)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => e.ReferenceNumber == voucherNumber);
    }

    private async Task<(decimal Debit, decimal Credit)> GrandTotalsAsync()
    {
        await using var ctx = _pg.NewContext();
        var debit = await ctx.Set<JournalEntryLine>().SumAsync(l => (decimal?)l.DebitAmount) ?? 0m;
        var credit = await ctx.Set<JournalEntryLine>().SumAsync(l => (decimal?)l.CreditAmount) ?? 0m;
        return (debit, credit);
    }

    // ═════════════ 1) لا قيد عند حفظ المسودة + سجل تدقيق الإنشاء ═════════════

    [LivePgFact]
    public async Task Draft_receipt_creates_no_journal_entry_and_audits_creation()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        ReceiptVoucherDto dto;
        await using (var ctx = _pg.NewContext())
        {
            dto = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 250.75m));
        }

        Assert.Equal((int)VoucherStatus.Draft, dto.Status);
        Assert.Null(dto.JournalEntryId);
        Assert.StartsWith("RCV-", dto.VoucherNumber);
        Assert.Equal(250.75m, dto.Amount);
        Assert.Equal("مسودة", dto.StatusName);

        // لا قيد محاسبي إطلاقًا للمسودة
        Assert.Equal(0, await JournalCountForVoucherAsync(dto.VoucherNumber));

        await using (var ctx = _pg.NewContext())
        {
            var audit = await NewReceiptService(ctx).GetAuditAsync(dto.Id);
            Assert.Single(audit);
            Assert.Equal("Created", audit[0].Action);
            Assert.Equal("tester", audit[0].PerformedBy);
            Assert.Null(audit[0].JournalEntryId);
        }
    }

    // ═════════════ 2) تحقق المدخلات + الحلّ الديناميكي للحسابات الورقية ═════════════

    [LivePgFact]
    public async Task Draft_receipt_rejects_invalid_input_and_resolves_leaf_accounts()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var service = NewReceiptService(ctx);

        // مبلغ غير موجب
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 0m)));

        // طريقة دفع غير صالحة (لا نقدي ولا بنكي)
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 10m, paymentMethod: 7)));

        // عميل غير موجود
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateDraftAsync(NewReceiptDto(Guid.NewGuid(), 10m)));

        // تاريخ غير صالح (DateTime.MinValue → -infinity في PostgreSQL)
        var badDate = NewReceiptDto(_pg.CustomerId, 10m);
        badDate.VoucherDate = default;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDraftAsync(badDate));

        // سند صحيح: الحسابات المحلولة ورقة متحركة (لا حساب تجميعي/جذري)
        var ok = await service.CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 10m, paymentMethod: 2));
        Assert.Equal(await _pg.FindAccountIdAsync("1101"), ok.CashOrBankAccountId); // بنكي ⇒ 1101
        Assert.Equal(await _pg.FindAccountIdAsync("1200"), ok.ReceivableAccountId); // ذمم العملاء
    }

    // ═════════════ 3) دورة الحياة: ترحيل بلا اعتماد مرفوض + الاعتماد بلا قيد ═════════════

    [LivePgFact]
    public async Task Post_requires_approval_and_approval_itself_creates_no_entry()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        Guid id;
        string number;
        await using (var ctx = _pg.NewContext())
        {
            var created = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 120m));
            id = created.Id;
            number = created.VoucherNumber;

            // ترحيل مباشر من المسودة ⇒ مرفوض (لا اختصار للاعتماد)
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewReceiptService(ctx).PostAsync(id));
            Assert.Contains("معتمد", ex.Message);
        }

        Assert.Equal(0, await JournalCountForVoucherAsync(number));

        await using (var ctx = _pg.NewContext())
        {
            var approved = await NewReceiptService(ctx).ApproveAsync(id);
            Assert.Equal((int)VoucherStatus.Approved, approved.Status);
            Assert.Equal("معتمد", approved.StatusName);
            Assert.Null(approved.JournalEntryId);
            Assert.NotNull(approved.ApprovedAt);
            Assert.Equal("tester", approved.ApprovedBy);
        }

        // الاعتماد نفسه لا يُنشئ قيدًا (اختبار رقم 8 في خريطة الاختبارات)
        Assert.Equal(0, await JournalCountForVoucherAsync(number));
    }

    // ═════════════ 4) الترحيل: قيد متوازن + ربط بالسند + تحديث الأرصدة ═════════════

    [LivePgFact]
    public async Task Post_creates_balanced_entry_links_voucher_and_updates_balances()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var cashId = await _pg.FindAccountIdAsync("1100");
        var receivableId = await _pg.FindAccountIdAsync("1200");
        var cashBefore = await BalanceAsync(cashId);
        var receivableBefore = await BalanceAsync(receivableId);

        Guid id;
        string number;
        await using (var ctx = _pg.NewContext())
        {
            var created = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 400m));
            id = created.Id;
            number = created.VoucherNumber;
            await NewReceiptService(ctx).ApproveAsync(id);
            var posted = await NewReceiptService(ctx).PostAsync(id);

            Assert.Equal((int)VoucherStatus.Posted, posted.Status);
            Assert.Equal("مرحّل", posted.StatusName);
            Assert.NotNull(posted.JournalEntryId);
            Assert.NotNull(posted.JournalEntryNumber);
            Assert.NotNull(posted.PostedAt);
            Assert.Equal("tester", posted.PostedBy);
        }

        await using (var ctx = _pg.NewContext())
        {
            var entry = await ctx.Set<JournalEntry>()
                .Include(e => e.Lines)
                .FirstAsync(e => e.ReferenceNumber == number);

            Assert.Equal(JournalEntryType.ReceiptVoucher, entry.EntryType);
            Assert.Equal(2, entry.Lines.Count);
            Assert.Equal(entry.Lines.Sum(l => l.DebitAmount), entry.Lines.Sum(l => l.CreditAmount));
            Assert.Equal(400m, entry.Lines.Sum(l => l.DebitAmount));

            // مدين الصندوق — دائن ذمم العميل
            Assert.Equal(400m, entry.Lines.Single(l => l.AccountId == cashId).DebitAmount);
            Assert.Equal(400m, entry.Lines.Single(l => l.AccountId == receivableId).CreditAmount);

            var audit = await NewReceiptService(ctx).GetAuditAsync(id);
            Assert.Contains(audit, a => a.Action == "Approved");
            Assert.Contains(audit, a => a.Action == "Posted" && a.JournalEntryId == entry.Id);
        }

        Assert.Equal(cashBefore + 400m, await BalanceAsync(cashId));
        Assert.Equal(receivableBefore - 400m, await BalanceAsync(receivableId));
    }

    // ═════════════ 5) إعادة الترحيل: لا قيد مكرر ولا رصيد مزدوج ═════════════

    [LivePgFact]
    public async Task Post_is_idempotent_and_never_duplicates_entry_or_balance()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var cashId = await _pg.FindAccountIdAsync("1100");
        var cashBefore = await BalanceAsync(cashId);

        Guid id;
        string number;
        Guid? firstEntryId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 250m));
            id = created.Id;
            number = created.VoucherNumber;
            await NewReceiptService(ctx).ApproveAsync(id);
            firstEntryId = (await NewReceiptService(ctx).PostAsync(id)).JournalEntryId;
        }

        // إعادة الترحيل على سياق جديد ⇒ تُعيد نفس السند بلا أي قيد جديد
        await using (var ctx = _pg.NewContext())
        {
            var second = await NewReceiptService(ctx).PostAsync(id);
            Assert.Equal(firstEntryId, second.JournalEntryId);
            Assert.Equal((int)VoucherStatus.Posted, second.Status);
        }

        Assert.Equal(1, await JournalCountForVoucherAsync(number));
        Assert.Equal(cashBefore + 250m, await BalanceAsync(cashId));
    }

    // ═════════════ 6) الصلاحيات: فشل مغلق ولا تغيير في الحالة ═════════════

    [LivePgFact]
    public async Task Missing_permission_fails_closed_and_changes_nothing()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // (أ) إنشاء بلا صلاحية ⇒ مرفوض ولا يُنشأ سند
        await using (var ctx = _pg.NewContext())
        {
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => NewReceiptService(ctx, new DenyAllVouchers()).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 50m)));
            Assert.Contains("Vouchers.Add", ex.Message);
        }
        await using (var ctx = _pg.NewContext())
            Assert.False(await ctx.Set<ReceiptVoucher>().AnyAsync(v => v.Amount == 50m));

        // (ب) منشئ بلا صلاحية اعتماد/ترحيل ⇒ مرفوض في الخدمة نفسها (لا حماية بالواجهة فقط)
        Guid id;
        string number;
        await using (var ctx = _pg.NewContext())
        {
            var auth = new AllowCreateOnlyVouchers();
            var dto = await NewReceiptService(ctx, auth).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 60m));
            id = dto.Id;
            number = dto.VoucherNumber;

            var approveEx = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => NewReceiptService(ctx, auth).ApproveAsync(id));
            Assert.Contains("Vouchers.Approve", approveEx.Message);

            var postEx = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => NewReceiptService(ctx, auth).PostAsync(id));
            Assert.Contains("Vouchers.Post", postEx.Message);

            var cancelEx = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => NewReceiptService(ctx, auth).CancelAsync(id, "محاولة"));
            Assert.Contains("Vouchers.Cancel", cancelEx.Message);
        }

        // السند باقٍ مسودة بلا قيد، ولا سطور تدقيق لعمليات مرفوضة
        await using (var ctx = _pg.NewContext())
        {
            var voucher = await ctx.Set<ReceiptVoucher>().FirstAsync(v => v.Id == id);
            Assert.Equal(VoucherStatus.Draft, voucher.Status);
            Assert.Null(voucher.JournalEntryId);

            var audit = await NewReceiptService(ctx).GetAuditAsync(id);
            Assert.Single(audit);
            Assert.Equal("Created", audit[0].Action);
        }
        Assert.Equal(0, await JournalCountForVoucherAsync(number));
    }

    // ═════════════ 7) الإلغاء: حفظ الأثر التاريخي + منع إلغاء المُرحَّل ═════════════

    [LivePgFact]
    public async Task Cancel_preserves_history_and_posted_voucher_cannot_be_cancelled()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // (أ) إلغاء مسودة: لا حذف، سجل تدقيق كامل، ولا قيد
        Guid draftId;
        string draftNumber;
        await using (var ctx = _pg.NewContext())
        {
            var dto = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 75m));
            draftId = dto.Id;
            draftNumber = dto.VoucherNumber;

            var cancelled = await NewReceiptService(ctx).CancelAsync(draftId, "خطأ في المبلغ");
            Assert.Equal((int)VoucherStatus.Cancelled, cancelled.Status);
            Assert.Equal("ملغى", cancelled.StatusName);
            Assert.Equal("خطأ في المبلغ", cancelled.CancelReason);
            Assert.NotNull(cancelled.CancelledAt);
            Assert.Equal("tester", cancelled.CancelledBy);
        }

        // الملغى لا يُعتمد ولا يُرحَّل، والسند نفسه ما زال موجودًا (لم يُحذف)
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => NewReceiptService(ctx).ApproveAsync(draftId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => NewReceiptService(ctx).PostAsync(draftId));

            Assert.True(await ctx.Set<ReceiptVoucher>().AnyAsync(v => v.Id == draftId && !v.IsDeleted));

            var audit = await NewReceiptService(ctx).GetAuditAsync(draftId);
            Assert.Equal(2, audit.Count);
            Assert.Contains(audit, a => a.Action == "Cancelled" && a.Details!.Contains("خطأ في المبلغ"));
        }
        Assert.Equal(0, await JournalCountForVoucherAsync(draftNumber));

        // (ب) سند مُرحَّل: الإلغاء مرفوض — الأثر المحاسبي محفوظ
        Guid postedId;
        await using (var ctx = _pg.NewContext())
        {
            var dto = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 90m));
            postedId = dto.Id;
            await NewReceiptService(ctx).ApproveAsync(postedId);
            await NewReceiptService(ctx).PostAsync(postedId);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewReceiptService(ctx).CancelAsync(postedId, "محاولة إلغاء بعد الترحيل"));
            Assert.Contains("مُرحَّل", ex.Message);
        }

        await using (var ctx = _pg.NewContext())
        {
            var voucher = await ctx.Set<ReceiptVoucher>().FirstAsync(v => v.Id == postedId);
            Assert.Equal(VoucherStatus.Posted, voucher.Status);
            Assert.Null(voucher.CancelledAt);
        }
    }

    // ═════════════ 8) ذرية الفشل: لا سند ولا قيد ولا رصيد عند فشل الترحيل ═════════════

    [LivePgFact]
    public async Task Posting_is_atomic_when_target_account_becomes_unmovable()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var cashId = await _pg.FindAccountIdAsync("1100");
        var cashBefore = await BalanceAsync(cashId);

        Guid id;
        string number;
        await using (var ctx = _pg.NewContext())
        {
            var dto = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 310m));
            id = dto.Id;
            number = dto.VoucherNumber;
            await NewReceiptService(ctx).ApproveAsync(id);
        }

        // تعطيل حساب الصندوق (تغيير بيانات محلي في قاعدة الاختبار) ⇒ الترحيل يفشل
        try
        {
            await SetAccountActiveAsync(cashId, false);

            await using (var ctx = _pg.NewContext())
            {
                await Assert.ThrowsAsync<InactiveAccountMovementException>(() => NewReceiptService(ctx).PostAsync(id));
            }
        }
        finally
        {
            await SetAccountActiveAsync(cashId, true);
        }

        // تراجع كامل: لا قيد، لا رصيد متغير، والسند باقٍ معتمدًا بلا ربط
        Assert.Equal(0, await JournalCountForVoucherAsync(number));
        Assert.Equal(cashBefore, await BalanceAsync(cashId));

        await using (var ctx = _pg.NewContext())
        {
            var voucher = await ctx.Set<ReceiptVoucher>().FirstAsync(v => v.Id == id);
            Assert.Equal(VoucherStatus.Approved, voucher.Status);
            Assert.Null(voucher.JournalEntryId);
        }
    }

    private async Task SetAccountActiveAsync(Guid accountId, bool active)
    {
        await using var ctx = _pg.NewContext();
        var account = await ctx.Set<Account>().FirstAsync(a => a.Id == accountId);
        account.IsActive = active;
        await ctx.SaveChangesAsync();
    }

    // ═════════════ 9) التزامن: محاولتان على نفس السند ⇒ قيد واحد ورصيد واحد ═════════════

    [LivePgFact]
    public async Task Concurrent_post_yields_single_entry_and_single_balance_update()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var cashId = await _pg.FindAccountIdAsync("1100");
        var cashBefore = await BalanceAsync(cashId);

        Guid id;
        string number;
        await using (var ctx = _pg.NewContext())
        {
            var dto = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 175m));
            id = dto.Id;
            number = dto.VoucherNumber;
            await NewReceiptService(ctx).ApproveAsync(id);
        }

        // سياقان مستقلان يمثلان مستخدمين متزامنين: الأول ينجح، والثاني يجب ألا يُنتج قيدًا ثانيًا
        // (قيد الحالة في الخدمة + فهرس فريد على JournalEntryId + RowVersion).
        await using var ctxA = _pg.NewContext();
        await using var ctxB = _pg.NewContext();

        Guid? entryA = null;
        string? errorB = null;

        try
        {
            entryA = (await NewReceiptService(ctxA).PostAsync(id)).JournalEntryId;
        }
        finally
        {
            try
            {
                var postedB = await NewReceiptService(ctxB).PostAsync(id);
                // مسار الأمان في الخدمة: تُعيد السند نفسه بلا قيد جديد
                Assert.Equal(entryA, postedB.JournalEntryId);
            }
            catch (InvalidOperationException ex)
            {
                errorB = ex.Message; // رسالة تزامن عربية واضحة بدل استثناء تقني
            }
        }

        // الثوابت المهمة: قيد واحد فقط وأثر رصيد واحد مهما كان ترتيب التنفيذ
        Assert.NotNull(entryA);
        Assert.Equal(1, await JournalCountForVoucherAsync(number));
        Assert.Equal(cashBefore + 175m, await BalanceAsync(cashId));

        await using (var ctx = _pg.NewContext())
        {
            var voucher = await ctx.Set<ReceiptVoucher>().FirstAsync(v => v.Id == id);
            Assert.Equal(VoucherStatus.Posted, voucher.Status);
            Assert.Equal(entryA, voucher.JournalEntryId);
        }

        // إن رُفضت المحاولة الثانية فيجب أن تكون برسالة واضحة (لا استثناء تقني غامض)
        if (errorB is not null)
            Assert.Contains("تعارض", errorB);
    }

    // ═════════════ 10) سند الصرف: مدين ذمم المورد — دائن الصندوق ═════════════

    [LivePgFact]
    public async Task Payment_voucher_posts_payable_debit_and_cash_credit()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var payableId = await _pg.FindAccountIdAsync("2200");
        var cashId = await _pg.FindAccountIdAsync("1100");
        // PU-03B: Arrange صريح — ذمّة كافية لمبلغ السند (660) قبل قياس خط الأساس.
        await using (var seedCtx = _pg.NewContext())
            await AccountingPgFixture.SeedPayableForAsync(seedCtx, _pg.SupplierId, 660m);
        var cashBefore = await BalanceAsync(cashId);
        var payableBefore = await BalanceAsync(payableId);

        Guid id;
        string number;
        await using (var ctx = _pg.NewContext())
        {
            var dto = await NewPaymentService(ctx).CreateDraftAsync(NewPaymentDto(_pg.SupplierId, 660m));
            id = dto.Id;
            number = dto.VoucherNumber;

            Assert.StartsWith("PV-", dto.VoucherNumber);
            Assert.Equal((int)VoucherStatus.Draft, dto.Status);
            Assert.Equal(payableId, dto.PayableAccountId);
            Assert.Equal(cashId, dto.CashOrBankAccountId);

            await NewPaymentService(ctx).ApproveAsync(id);
            var posted = await NewPaymentService(ctx).PostAsync(id);
            Assert.Equal((int)VoucherStatus.Posted, posted.Status);
            Assert.NotNull(posted.JournalEntryId);
        }

        await using (var ctx = _pg.NewContext())
        {
            var entry = await ctx.Set<JournalEntry>()
                .Include(e => e.Lines)
                .FirstAsync(e => e.ReferenceNumber == number);

            Assert.Equal(JournalEntryType.PaymentVoucher, entry.EntryType);
            Assert.Equal(660m, entry.Lines.Single(l => l.AccountId == payableId).DebitAmount);
            Assert.Equal(660m, entry.Lines.Single(l => l.AccountId == cashId).CreditAmount);
            Assert.Equal(entry.Lines.Sum(l => l.DebitAmount), entry.Lines.Sum(l => l.CreditAmount));

            var audit = await NewPaymentService(ctx).GetAuditAsync(id);
            Assert.Equal(3, audit.Count); // Created + Approved + Posted
        }

        // الرصيد المخزَّن يُعبَّر عنه باتجاه طبيعة الحساب:
        // ذمم الموردين (دائن الطبيعة) ⇒ المدين 660 يُنقص رصيدها، والصندوق (مدين الطبيعة) يُنقصه الدائن.
        Assert.Equal(payableBefore - 660m, await BalanceAsync(payableId));
        Assert.Equal(cashBefore - 660m, await BalanceAsync(cashId));
    }

    // ═════════════ 11) ميزان المراجعة يبقى متوازنًا بعد الترحيل ═════════════

    [LivePgFact]
    public async Task Trial_balance_stays_balanced_after_posting_vouchers()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // PU-03B: Arrange صريح — ذمّة كافية للسند (222) قبل قياس خط الأساس.
        await using (var seedCtx = _pg.NewContext())
            await AccountingPgFixture.SeedPayableForAsync(seedCtx, _pg.SupplierId, 222m);

        var before = await GrandTotalsAsync();
        Assert.Equal(before.Debit, before.Credit);

        await using (var ctx = _pg.NewContext())
        {
            var receipt = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 111m));
            await NewReceiptService(ctx).ApproveAsync(receipt.Id);
            await NewReceiptService(ctx).PostAsync(receipt.Id);

            var payment = await NewPaymentService(ctx).CreateDraftAsync(NewPaymentDto(_pg.SupplierId, 222m));
            await NewPaymentService(ctx).ApproveAsync(payment.Id);
            await NewPaymentService(ctx).PostAsync(payment.Id);
        }

        var after = await GrandTotalsAsync();

        // الطرفان يزيدان بنفس المقدار (قيود متوازنة) ⇒ لا خلل في الميزان
        Assert.Equal(before.Debit + 333m, after.Debit);
        Assert.Equal(before.Credit + 333m, after.Credit);
        Assert.Equal(after.Debit, after.Credit);
    }

    // ═════════════ 12) تعديل المسودة + سجل التدقيق ═════════════

    [LivePgFact]
    public async Task Update_draft_persists_changes_and_audits_the_update()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        Guid id;
        string number;
        await using (var ctx = _pg.NewContext())
        {
            var dto = await NewReceiptService(ctx).CreateDraftAsync(NewReceiptDto(_pg.CustomerId, 100m));
            id = dto.Id;
            number = dto.VoucherNumber;

            var updated = await NewReceiptService(ctx).UpdateDraftAsync(id, new UpdateReceiptVoucherDto
            {
                CustomerId = _pg.CustomerId,
                VoucherDate = DateTime.Today,
                PaymentMethod = 2, // نقدي ← بنكي
                Amount = 333.5m,
                Description = "بعد التعديل",
                ExternalReference = "REF-1"
            });

            Assert.Equal(333.5m, updated.Amount);
            Assert.Equal(2, updated.PaymentMethod);
            Assert.Equal(await _pg.FindAccountIdAsync("1101"), updated.CashOrBankAccountId);
            Assert.Equal("بعد التعديل", updated.Description);
            Assert.Equal(number, updated.VoucherNumber); // الرقم لا يتغير بالتعديل
        }

        await using (var ctx = _pg.NewContext())
        {
            var audit = await NewReceiptService(ctx).GetAuditAsync(id);
            Assert.Equal(2, audit.Count);
            Assert.Equal("Updated", audit[1].Action);
            Assert.Contains("100.00", audit[1].Details!); // القيمة قبل التعديل محفوظة في الأثر
        }

        Assert.Equal(0, await JournalCountForVoucherAsync(number)); // التعديل لا يُنشئ قيدًا

        // التعديل بعد الاعتماد مرفوض
        await using (var ctx = _pg.NewContext())
        {
            await NewReceiptService(ctx).ApproveAsync(id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => NewReceiptService(ctx).UpdateDraftAsync(id,
                new UpdateReceiptVoucherDto
                {
                    CustomerId = _pg.CustomerId,
                    VoucherDate = DateTime.Today,
                    PaymentMethod = 1,
                    Amount = 10m
                }));
        }
    }
}
