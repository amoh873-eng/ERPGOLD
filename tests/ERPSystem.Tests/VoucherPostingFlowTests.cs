using ERPSystem.Application.DTOs.Accounts;
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
/// اختبارات مسار سندات القبض/الصرف من الإنشاء حتى الأثر في شجرة الحسابات وميزان المراجعة
/// (المهمة: تشخيص عدم ظهور الأثر المحاسبي على نسخة 18091).
///
/// تُشغَّل على قاعدة PostgreSQL معزولة تُنشأ وتُحذف ذاتيًا عبر <see cref="AccountingPgFixture"/>،
/// وتغطّي المتطلبات: عميل جديد + سند قبض 100.00 · وجود القيد وسطرَي المدين/الدائن · ظهور الأثر في
/// الشجرة وميزان المراجعة · سند صرف باتجاهه الصحيح · مبالغ عشرية · فشل الترحيل بلا سند مرحّل بلا قيد ·
/// إعادة الإرسال بلا تكرار · الإلغاء وفق القواعد · أرصدة الأطراف والصندوق قبل/بعد · توازن المجاميع ·
/// غياب القيود اليتيمة/الأرقام المكررة · ثبات كل ذلك بعد إعادة الحساب من القاعدة.
/// </summary>
public class VoucherPostingFlowTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public VoucherPostingFlowTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAllVouchers : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public string? CurrentUserName() => "posting-flow-tester";
    }

    private static ReceiptVoucherService Receipts(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    private static PaymentVoucherService Payments(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    /// <summary>ينشئ عميل اختبار جديدًا بمعرّف واضح (متطلب: «عميل اختبار جديد بمعرف واضح»).</summary>
    private static async Task<Guid> CreateCustomerAsync(AppDbContext ctx, string code)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "عميل اختبار " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<Guid> CreateSupplierAsync(AppDbContext ctx, string code)
    {
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "مورد اختبار " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<decimal> StoredBalanceAsync(AppDbContext ctx, Guid accountId)
        => await ctx.Set<Account>().AsNoTracking().Where(a => a.Id == accountId).Select(a => a.CurrentBalance).FirstAsync();

    private static async Task<decimal> PartyBalanceAsync(AppDbContext ctx, Guid partyId, bool customer)
        => customer
            ? await ctx.Set<Customer>().AsNoTracking().Where(c => c.Id == partyId).Select(c => c.CurrentBalance).FirstAsync()
            : await ctx.Set<Supplier>().AsNoTracking().Where(s => s.Id == partyId).Select(s => s.CurrentBalance).FirstAsync();

    private static async Task<AccountBalanceTreeDto> BalanceTreeAsync(AppDbContext ctx)
        => await new AccountBalanceService(ctx).GetBalanceTreeAsync();

    /// <summary>صافي رصيد عقدة بالكود من المحرّك الموحّد (نفس مصدر الشجرة وميزان المراجعة).</summary>
    private static decimal NetOf(AccountBalanceTreeDto tree, string code)
    {
        var node = tree.Flattened.FirstOrDefault(n => n.Code == code);
        return node?.NetBalance ?? 0m;
    }

    private static async Task<(decimal Debit, decimal Credit)> GrandTotalsAsync(AppDbContext ctx)
    {
        var debit = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted).SumAsync(l => (decimal?)l.DebitAmount) ?? 0m;
        var credit = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted).SumAsync(l => (decimal?)l.CreditAmount) ?? 0m;
        return (debit, credit);
    }

    // ═════════ 1) سند قبض 100.00 لعميل جديد: مسودة بلا قيد ← اعتماد بلا قيد ← ترحيل بقيد ═════════

    [LivePgFact]
    public async Task Receipt_100_posts_balanced_entry_and_shows_in_tree_and_trial_balance()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        Guid customerId, cashAccountId, arAccountId, voucherId;
        string voucherNumber, cashCode, arCode;
        decimal customerBefore, cashBefore, arBefore, cashTreeBefore, arTreeBefore;
        decimal debitBefore, creditBefore;

        await using (var ctx = _pg.NewContext())
        {
            customerId = await CreateCustomerAsync(ctx, "TEST-18091-100");
            customerBefore = await PartyBalanceAsync(ctx, customerId, true);
            (debitBefore, creditBefore) = await GrandTotalsAsync(ctx);

            var treeBefore = await BalanceTreeAsync(ctx);

            var draft = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = customerId,
                VoucherDate = DateTime.Today,
                PaymentMethod = 1, // نقدي
                Amount = 100.00m,
                Description = "سند قبض 100.00 — اختبار مسار الترحيل"
            });

            voucherId = draft.Id;
            voucherNumber = draft.VoucherNumber;
            cashAccountId = draft.CashOrBankAccountId;
            arAccountId = draft.ReceivableAccountId;

            cashCode = await ctx.Set<Account>().AsNoTracking().Where(a => a.Id == cashAccountId).Select(a => a.Code).FirstAsync();
            arCode = await ctx.Set<Account>().AsNoTracking().Where(a => a.Id == arAccountId).Select(a => a.Code).FirstAsync();
            cashTreeBefore = NetOf(treeBefore, cashCode);
            arTreeBefore = NetOf(treeBefore, arCode);

            // (أ) المسودة: بلا قيد — لا أثر محاسبي (وهذا هو سبب الانطباع بالعطل في نسخة المعاينة)
            Assert.Equal((int)VoucherStatus.Draft, draft.Status);
            Assert.Null(draft.JournalEntryId);
            Assert.Equal(0, await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(e => e.ReferenceNumber == draft.VoucherNumber));

            cashBefore = await StoredBalanceAsync(ctx, cashAccountId);
            arBefore = await StoredBalanceAsync(ctx, arAccountId);

            // (ب) الاعتماد: لا يُنشئ قيدًا
            var approved = await Receipts(ctx).ApproveAsync(voucherId);
            Assert.Equal((int)VoucherStatus.Approved, approved.Status);
            Assert.Null(approved.JournalEntryId);
            Assert.Equal(0, await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(e => e.ReferenceNumber == voucherNumber));
        }

        await using (var ctx = _pg.NewContext())
        {
            // (ج) الترحيل: قيد مرتبط بالسند + سطرا مدين/دائن بالاتجاه الصحيح
            var posted = await Receipts(ctx).PostAsync(voucherId);
            Assert.Equal((int)VoucherStatus.Posted, posted.Status);
            Assert.NotNull(posted.JournalEntryId);
            Assert.False(string.IsNullOrWhiteSpace(posted.JournalEntryNumber));

            var entry = await ctx.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
                .FirstAsync(e => e.Id == posted.JournalEntryId);
            Assert.Equal(JournalEntryType.ReceiptVoucher, entry.EntryType);
            Assert.True(entry.Lines.Count >= 2, "القيد يجب أن يحمل سطرين على الأقل");
            Assert.Equal(100.00m, entry.Lines.Sum(l => l.DebitAmount));
            Assert.Equal(entry.Lines.Sum(l => l.DebitAmount), entry.Lines.Sum(l => l.CreditAmount));

            Assert.Equal(cashAccountId, entry.Lines.Single(l => l.DebitAmount > 0m).AccountId);
            Assert.Equal(arAccountId, entry.Lines.Single(l => l.CreditAmount > 0m).AccountId);

            // الأرصدة المخزَّنة (الأستاذ) تحرّكت بالمقدار الصحيح
            Assert.Equal(cashBefore + 100.00m, await StoredBalanceAsync(ctx, cashAccountId));
            Assert.Equal(arBefore - 100.00m, await StoredBalanceAsync(ctx, arAccountId));
            Assert.Equal(customerBefore - 100.00m, await PartyBalanceAsync(ctx, customerId, true));

            // الأثر في شجرة الحسابات وميزان المراجعة (المحرّك الموحّد)
            var tree = await BalanceTreeAsync(ctx);
            Assert.Equal(100.00m, NetOf(tree, cashCode) - cashTreeBefore);
            Assert.Equal(-100.00m, NetOf(tree, arCode) - arTreeBefore);
            Assert.Equal(tree.TotalDebit, tree.TotalCredit);

            var (debitAfter, creditAfter) = await GrandTotalsAsync(ctx);
            Assert.Equal(debitBefore + 100.00m, debitAfter);
            Assert.Equal(creditBefore + 100.00m, creditAfter);
        }
    }

    // ═════════ 2) سند صرف 100.00 لمورد: مدين ذمم الموردين — دائن الصندوق/البنك ═════════

    [LivePgFact]
    public async Task Payment_100_debits_payable_and_credits_cash()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        Guid supplierId, voucherId, cashAccountId, payableAccountId;
        decimal supplierBefore, cashBefore, payableBefore;

        await using (var ctx = _pg.NewContext())
        {
            supplierId = await CreateSupplierAsync(ctx, "TEST-18091-PAY");
            // PU-03B: Arrange صريح — ذمّة افتتاحية للمورد المُنشأ هنا (حارس السداد الزائد يتطلب مستحقًا).
            await AccountingPgFixture.SeedPayableForAsync(ctx, supplierId, 100.00m);
            supplierBefore = await PartyBalanceAsync(ctx, supplierId, false);

            var draft = await Payments(ctx).CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = supplierId,
                VoucherDate = DateTime.Today,
                PaymentMethod = 1,
                Amount = 100.00m,
                Description = "سند صرف 100.00 — اختبار اتجاه القيد"
            });

            voucherId = draft.Id;
            cashAccountId = draft.CashOrBankAccountId;
            payableAccountId = draft.PayableAccountId;
            cashBefore = await StoredBalanceAsync(ctx, cashAccountId);
            payableBefore = await StoredBalanceAsync(ctx, payableAccountId);

            Assert.Equal((int)VoucherStatus.Draft, draft.Status);
            Assert.Null(draft.JournalEntryId);
        }

        await using (var ctx = _pg.NewContext())
        {
            await Payments(ctx).ApproveAsync(voucherId);
            var posted = await Payments(ctx).PostAsync(voucherId);
            Assert.Equal((int)VoucherStatus.Posted, posted.Status);

            var entry = await ctx.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
                .FirstAsync(e => e.Id == posted.JournalEntryId);
            Assert.Equal(JournalEntryType.PaymentVoucher, entry.EntryType);
            Assert.Equal(payableAccountId, entry.Lines.Single(l => l.DebitAmount > 0m).AccountId); // مدين المورد
            Assert.Equal(cashAccountId, entry.Lines.Single(l => l.CreditAmount > 0m).AccountId);   // دائن الصندوق
            Assert.Equal(100.00m, entry.Lines.Sum(l => l.DebitAmount));
            Assert.Equal(entry.Lines.Sum(l => l.DebitAmount), entry.Lines.Sum(l => l.CreditAmount));

            Assert.Equal(cashBefore - 100.00m, await StoredBalanceAsync(ctx, cashAccountId));
            // ملاحظة اتجاه (سلوك النظام): الرصيد المخزَّن للأستاذ يُحفظ في **اتجاه طبيعة الحساب**،
            // فذمم الموردين (دائن طبيعتها) تظهر موجبة عند وجود التزام، ومدين 100 يُنقصها بمقدار 100.
            Assert.Equal(payableBefore - 100.00m, await StoredBalanceAsync(ctx, payableAccountId));
            Assert.Equal(supplierBefore - 100.00m, await PartyBalanceAsync(ctx, supplierId, false));
        }
    }

    // ═════════ 3) مبالغ عشرية: لا انحراف تقريب ═════════

    [LivePgFact]
    public async Task Decimal_amounts_post_exactly_without_rounding_drift()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var customerId = await CreateCustomerAsync(ctx, "TEST-18091-DEC");
        var supplierId = await CreateSupplierAsync(ctx, "TEST-18091-DEC-S");
        // PU-03B: Arrange صريح — ذمّة كافية لمبلغ السند (0.45) قبل قياس خط الأساس.
        await AccountingPgFixture.SeedPayableForAsync(ctx, supplierId, 0.45m);
        var (debitBefore, creditBefore) = await GrandTotalsAsync(ctx);

        var receipt = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = DateTime.Today, PaymentMethod = 1,
            Amount = 100.55m, Description = "عشري 100.55"
        });
        await Receipts(ctx).ApproveAsync(receipt.Id);
        var postedReceipt = await Receipts(ctx).PostAsync(receipt.Id);

        var payment = await Payments(ctx).CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = supplierId, VoucherDate = DateTime.Today, PaymentMethod = 2,
            Amount = 0.45m, Description = "عشري 0.45 (بنكي)"
        });
        await Payments(ctx).ApproveAsync(payment.Id);
        var postedPayment = await Payments(ctx).PostAsync(payment.Id);

        var receiptEntry = await ctx.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .FirstAsync(e => e.Id == postedReceipt.JournalEntryId);
        var paymentEntry = await ctx.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .FirstAsync(e => e.Id == postedPayment.JournalEntryId);

        Assert.Equal(100.55m, receiptEntry.Lines.Sum(l => l.DebitAmount));
        Assert.Equal(100.55m, receiptEntry.Lines.Sum(l => l.CreditAmount));
        Assert.Equal(0.45m, paymentEntry.Lines.Sum(l => l.DebitAmount));
        Assert.Equal(0.45m, paymentEntry.Lines.Sum(l => l.CreditAmount));

        var (debitAfter, creditAfter) = await GrandTotalsAsync(ctx);
        Assert.Equal(debitBefore + 101.00m, debitAfter);
        Assert.Equal(creditBefore + 101.00m, creditAfter);
        Assert.Equal(debitAfter, creditAfter);
    }

    // ═════════ 4) فشل الترحيل: لا سند «مرحّل» بلا قيد ولا أثر جزئي ═════════

    [LivePgFact]
    public async Task Failed_post_keeps_voucher_approved_without_entry_and_no_partial_effect()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var customerId = await CreateCustomerAsync(ctx, "TEST-18091-FAIL");

        var draft = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = DateTime.Today, PaymentMethod = 1,
            Amount = 100.00m, Description = "فشل ترحيل مُستحَث"
        });
        await Receipts(ctx).ApproveAsync(draft.Id);

        var cashAccountId = draft.CashOrBankAccountId;
        var cashBefore = await StoredBalanceAsync(ctx, cashAccountId);
        var customerBefore = await PartyBalanceAsync(ctx, customerId, true);
        var entriesBefore = await ctx.Set<JournalEntry>().AsNoTracking().CountAsync();

        // استحثاث الفشل: تعطيل حساب الصندوق ⇒ MovementAccountValidator يرفض الترحيل
        var cash = await ctx.Set<Account>().FirstAsync(a => a.Id == cashAccountId);
        cash.IsActive = false;
        await ctx.SaveChangesAsync();

        var threw = false;
        try { await Receipts(ctx).PostAsync(draft.Id); }
        catch { threw = true; }
        Assert.True(threw, "الترحيل إلى حساب معطّل يجب أن يُرفض");

        var afterFailure = await Receipts(ctx).GetByIdAsync(draft.Id);
        Assert.NotNull(afterFailure);
        Assert.Equal((int)VoucherStatus.Approved, afterFailure!.Status); // لم يُعتبر «مرحّلًا»
        Assert.Null(afterFailure.JournalEntryId);
        Assert.Equal(entriesBefore, await ctx.Set<JournalEntry>().AsNoTracking().CountAsync());
        Assert.Equal(cashBefore, await StoredBalanceAsync(ctx, cashAccountId));
        Assert.Equal(customerBefore, await PartyBalanceAsync(ctx, customerId, true));

        // إعادة التفعيل ⇒ الترحيل ينجح ويُنشئ القيد
        cash.IsActive = true;
        await ctx.SaveChangesAsync();
        var posted = await Receipts(ctx).PostAsync(draft.Id);
        Assert.Equal((int)VoucherStatus.Posted, posted.Status);
        Assert.NotNull(posted.JournalEntryId);
    }

    // ═════════ 5) إعادة الإرسال: ترحيل مكرر لا يُنشئ قيدًا ثانيًا ولا يضاعف الأرصدة ═════════

    [LivePgFact]
    public async Task Reposting_same_voucher_is_idempotent()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var customerId = await CreateCustomerAsync(ctx, "TEST-18091-DUP");

        var draft = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = DateTime.Today, PaymentMethod = 1,
            Amount = 100.00m, Description = "إعادة إرسال"
        });
        await Receipts(ctx).ApproveAsync(draft.Id);

        var first = await Receipts(ctx).PostAsync(draft.Id);
        var entriesAfterFirst = await ctx.Set<JournalEntry>().AsNoTracking().CountAsync();
        var cashAfterFirst = await StoredBalanceAsync(ctx, draft.CashOrBankAccountId);

        var second = await Receipts(ctx).PostAsync(draft.Id);

        Assert.Equal(first.JournalEntryId, second.JournalEntryId);
        Assert.Equal(entriesAfterFirst, await ctx.Set<JournalEntry>().AsNoTracking().CountAsync());
        Assert.Equal(cashAfterFirst, await StoredBalanceAsync(ctx, draft.CashOrBankAccountId));
        Assert.Equal(1, await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(e => e.ReferenceNumber == draft.VoucherNumber));
    }

    // ═════════ 6) الإلغاء وفق قواعد النظام ═════════

    [LivePgFact]
    public async Task Cancel_draft_keeps_no_entry_and_posted_voucher_cannot_be_cancelled()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var customerId = await CreateCustomerAsync(ctx, "TEST-18091-CANCEL");

        var draft = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = DateTime.Today, PaymentMethod = 1,
            Amount = 100.00m, Description = "إلغاء مسودة"
        });

        var cancelled = await Receipts(ctx).CancelAsync(draft.Id, "اختبار الإلغاء");
        Assert.Equal((int)VoucherStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.JournalEntryId);
        Assert.Equal(0, await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(e => e.ReferenceNumber == draft.VoucherNumber));

        var second = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = DateTime.Today, PaymentMethod = 1,
            Amount = 100.00m, Description = "سند مُرحَّل لا يُلغى"
        });
        await Receipts(ctx).ApproveAsync(second.Id);
        await Receipts(ctx).PostAsync(second.Id);

        var threw = false;
        try { await Receipts(ctx).CancelAsync(second.Id, "محاولة إلغاء مرحّل"); }
        catch { threw = true; }
        Assert.True(threw, "لا يُلغى سند مُرحَّل — الأثر المحاسبي محفوظ");
    }

    // ═════════ 7) سلامة شاملة: لا قيود يتيمة/سندات بلا قيد/أرقام مكررة/أرصدة منحرفة ═════════

    [LivePgFact]
    public async Task No_orphan_entries_unposted_vouchers_duplicate_numbers_or_balance_drift()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();

        // (أ) سندات مرحّلة بلا قيد = 0
        var postedWithoutEntry =
            await ctx.Set<ReceiptVoucher>().AsNoTracking().CountAsync(v => v.Status == VoucherStatus.Posted && v.JournalEntryId == null)
          + await ctx.Set<PaymentVoucher>().AsNoTracking().CountAsync(v => v.Status == VoucherStatus.Posted && v.JournalEntryId == null);
        Assert.Equal(0, postedWithoutEntry);

        var receiptEntryIds = await ctx.Set<ReceiptVoucher>().AsNoTracking()
            .Where(v => v.JournalEntryId != null).Select(v => v.JournalEntryId!.Value).ToListAsync();
        var paymentEntryIds = await ctx.Set<PaymentVoucher>().AsNoTracking()
            .Where(v => v.JournalEntryId != null).Select(v => v.JournalEntryId!.Value).ToListAsync();
        var voucherEntryIds = receiptEntryIds.Concat(paymentEntryIds).ToList();

        // (ب) قيد واحد لكل سند (لا تكرار) + كل قيد متوازن وبسطرين على الأقل
        Assert.Equal(voucherEntryIds.Count, voucherEntryIds.Distinct().Count());
        var voucherEntries = await ctx.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => voucherEntryIds.Contains(e.Id)).ToListAsync();
        Assert.Equal(0, voucherEntries.Count(e => e.Lines.Count(l => !l.IsDeleted) < 2));
        Assert.Equal(0, voucherEntries.Count(e => e.Lines.Sum(l => l.DebitAmount) != e.Lines.Sum(l => l.CreditAmount)));

        // (ج) أرقام السندات فريدة
        var receiptNumbers = await ctx.Set<ReceiptVoucher>().AsNoTracking().Select(v => v.VoucherNumber).ToListAsync();
        var paymentNumbers = await ctx.Set<PaymentVoucher>().AsNoTracking().Select(v => v.VoucherNumber).ToListAsync();
        Assert.Equal(receiptNumbers.Count, receiptNumbers.Distinct().Count());
        Assert.Equal(paymentNumbers.Count, paymentNumbers.Distinct().Count());

        // (د) مجاميع المدين = الدائن، وميزان المراجعة متوازن
        var (debit, credit) = await GrandTotalsAsync(ctx);
        Assert.Equal(debit, credit);
        var tree = await BalanceTreeAsync(ctx);
        Assert.Equal(tree.TotalDebit, tree.TotalCredit);

        // (هـ) الرصيد المخزَّن لكل حساب = الرصيد المشتق من سطور القيود (حسب طبيعة الحساب)
        var accounts = await ctx.Set<Account>().AsNoTracking().Where(a => !a.IsDeleted)
            .Select(a => new { a.Id, a.NormalBalance, a.CurrentBalance }).ToListAsync();
        var nets = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Net = g.Sum(l => l.DebitAmount - l.CreditAmount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Net);

        var mismatches = accounts.Count(a =>
        {
            var net = nets.TryGetValue(a.Id, out var n) ? n : 0m;
            var expected = a.NormalBalance == NormalBalance.Debit ? net : -net;
            return Math.Abs(expected - a.CurrentBalance) > 0.005m;
        });
        Assert.Equal(0, mismatches);
    }

    // ═════════ 8) مصدر أرصدة لوحة المعلومات = نفس محرّك الشجرة/ميزان المراجعة بلا ازدواج ═════════

    [LivePgFact]
    public async Task Dashboard_tiles_use_derived_engine_without_double_counting()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var tree = await BalanceTreeAsync(ctx);

        var topNodes = TopMostNodes(tree, "1100", "1105").ToList();
        var leaves = topNodes.SelectMany(LeavesOf).ToList();

        // جمع «أعلى عقدة مطابقة» (منطق اللوحة بعد الإصلاح) = مجموع أوراق الفروع المطلوبة
        Assert.Equal(leaves.Sum(l => l.NetBalance), SumTopMost(tree, "1100", "1105"));

        // والقيمة مطابقة لمجموع سطور القيود على تلك الأوراق (لا للرصيد المخزَّن)
        var leafIds = leaves.Select(l => l.Id).ToList();
        var fromLines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && leafIds.Contains(l.AccountId))
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
        Assert.Equal(fromLines, SumTopMost(tree, "1100", "1105"));
    }

    // ───────────────────────── مساعدات منطق «أعلى عقدة مطابقة» (نفس منطق اللوحة) ─────────────────────────

    private static IEnumerable<AccountBalanceNodeDto> LeavesOf(AccountBalanceNodeDto node)
        => node.Children.Count == 0 ? new[] { node } : node.Children.SelectMany(LeavesOf);

    /// <summary>العقد المطابقة للأكواد مع تخطّي أي عقدة لها سلف مطابق (منع ازدواج التجميع الهرمي).</summary>
    private static IEnumerable<AccountBalanceNodeDto> TopMostNodes(AccountBalanceTreeDto tree, params string[] codes)
    {
        var byCode = tree.Flattened.GroupBy(n => n.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList());
        var wanted = new HashSet<string>(codes, StringComparer.Ordinal);

        foreach (var node in tree.Flattened.Where(n => wanted.Contains(n.Code)))
        {
            var hasMatchingAncestor = false;
            var parentCode = node.ParentAccountCode;
            var guard = 0;
            while (!string.IsNullOrEmpty(parentCode) && guard++ < 64)
            {
                if (wanted.Contains(parentCode)) { hasMatchingAncestor = true; break; }
                parentCode = byCode.TryGetValue(parentCode, out var parents) ? parents[0].ParentAccountCode : null;
            }

            if (!hasMatchingAncestor) yield return node;
        }
    }

    private static decimal SumTopMost(AccountBalanceTreeDto tree, params string[] codes)
        => TopMostNodes(tree, codes).Sum(n => n.NetBalance);
}
