using ERPSystem.Application.DTOs.Sales;
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
/// SUB-2b/P3 — اختبارات تكامل حية لمسار رد أموال العملاء (سند صرف بطرف عميل + مردود مرجعي).
///
/// السيناريو الأساسي: فاتورة آجلة تُحصَّل بالكامل ثم تُرد بضاعتها ⇒ رصيد دائن للعميل
/// (Customer.CurrentBalance سالب) قابل للرد نقدًا/بنكيًا. تُقاس:
///  • الصلاحية الصريحة Vouchers.Refund (فشل مغلق) ورفض الطرفين/غياب المردود.
///  • القيد: Dr 1200 / Cr 1100|1101 بنوع CustomerRefund، وتوازنه.
///  • تغيّر رصيد العميل مرة واحدة فقط + منع التكرار + السقف المزدوج.
///  • العكس الكامل: إرجاع الرصيد وتحرير السقف، ومنع إعادة التوزيع/التخصيصات.
///
/// كل الاختبارات على قاعدة معزولة تُنشأ وتُحذف ذاتيًا (AccountingPgFixture ⇒ erp_a5_*).
/// </summary>
public class Sub2bRefundVoucherTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Sub2bRefundVoucherTests(AccountingPgFixture pg) => _pg = pg;

    // ─────────────── تصاريح اختبار (تُحقن خلفيًا كما في الإنتاج) ───────────────

    private sealed class RefundAuth : IVoucherAuthorization
    {
        public bool Refund { get; init; } = true;
        public bool Reverse { get; init; } = true;

        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanRefund() => Refund;
        public bool CanReverse() => Reverse;
        public string? CurrentUserName() => "refund-tester";
    }

    private static PaymentVoucherService Payments(AppDbContext ctx, IVoucherAuthorization? auth = null)
        => new(ctx, new JournalEntryService(ctx), auth ?? new RefundAuth());

    private static ReceiptVoucherService Receipts(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new RefundAuth());

    private static SalesReturnService Returns(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx));

    private static CreatePaymentVoucherDto RefundDto(Guid customerId, Guid returnId, decimal amount, int method = 1)
        => new()
        {
            CustomerId = customerId,
            SalesReturnId = returnId,
            VoucherDate = DateTime.Today,
            PaymentMethod = method,
            Amount = amount,
            Description = "رد مبلغ عميل — اختبار"
        };

    // ─────────────── أدوات ───────────────

    private async Task<Guid> NewCustomerAsync(string label)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Code = label + "-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "عميل رد " + label,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>
    /// يهيّئ «رصيدًا دائنًا» حقيقيًا: فاتورة آجلة ⇒ تحصيل كامل ⇒ مردود جزئي/كامل.
    /// النتيجة: العميل دائن بمقدار إجمالي المردود، والمردود مرحّل ومؤهل للرد.
    /// </summary>
    private async Task<(Guid CustomerId, Guid ReturnId, decimal ReturnTotal)> SeedCreditAsync(
        decimal invoiceQty = 10m, decimal returnQty = 4m, decimal unitPrice = 15m)
    {
        var customerId = await NewCustomerAsync("RF");
        var itemId = await _pg.CreateItemAsync(
            "RF-" + Guid.NewGuid().ToString("N")[..6], unitPrice, 10m, 500m, taxRate: 16m);

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount,
                PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 16m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = invoiceQty, UnitPrice = unitPrice }
                }
            });
            invoiceId = invoice.Id;
        }

        decimal invoiceTotal;
        await using (var ctx = _pg.NewContext())
            invoiceTotal = await ctx.Set<SalesInvoice>().AsNoTracking()
                .Where(i => i.Id == invoiceId).Select(i => i.TotalAmount).FirstAsync();

        // تحصيل كامل ⇒ يصبح رصيد العميل صفرًا قبل المردود (شرط نشوء رصيد دائن حقيقي).
        await using (var ctx = _pg.NewContext())
        {
            var receipt = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = customerId,
                VoucherDate = DateTime.Today,
                PaymentMethod = 1,
                Amount = invoiceTotal,
                Description = "تحصيل قبل الرد — اختبار"
            });
            await Receipts(ctx).ApproveAsync(receipt.Id);
            await Receipts(ctx).PostAsync(receipt.Id);
        }

        Guid returnId;
        await using (var ctx = _pg.NewContext())
        {
            var salesReturn = await Returns(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto>
                {
                    new() { ItemId = itemId, Quantity = returnQty, UnitPrice = unitPrice }
                }
            });
            returnId = salesReturn.Id;
        }

        await using (var ctx = _pg.NewContext())
        {
            var ret = await ctx.Set<SalesReturn>().AsNoTracking().FirstAsync(r => r.Id == returnId);
            Assert.Equal(DocumentStatus.Posted, ret.Status);
            return (customerId, returnId, ret.TotalAmount);
        }
    }

    private async Task<decimal> CustomerBalanceAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<Guid> PostRefundAsync(Guid customerId, Guid returnId, decimal amount, int method = 1)
    {
        await using var ctx = _pg.NewContext();
        var svc = Payments(ctx);
        var draft = await svc.CreateDraftAsync(RefundDto(customerId, returnId, amount, method));
        await svc.ApproveAsync(draft.Id);
        await svc.PostAsync(draft.Id);
        return draft.Id;
    }

    private async Task<(JournalEntryType Type, Dictionary<string, decimal> Net)> EntryAsync(string reference)
    {
        await using var ctx = _pg.NewContext();
        var entry = await ctx.Set<JournalEntry>().AsNoTracking()
            .FirstAsync(e => e.ReferenceNumber == reference);

        var rows = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.JournalEntryId == entry.Id && !l.IsDeleted)
            .Join(ctx.Set<Account>().AsNoTracking(), l => l.AccountId, a => a.Id,
                (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
            .ToListAsync();

        return (entry.EntryType, rows
            .GroupBy(r => r.Code)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.DebitAmount - x.CreditAmount)));
    }

    // ═════════════════ 1) الصلاحية الصريحة والتحقق من المدخلات ═════════════════

    [LivePgFact]
    public async Task Refund_draft_requires_explicit_refund_permission()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _) = await SeedCreditAsync();

        await using var ctx = _pg.NewContext();
        var denied = new RefundAuth { Refund = false };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Payments(ctx, denied).CreateDraftAsync(RefundDto(customerId, returnId, 10m)));

        // الفشل مغلق: لم يُحفظ أي سند.
        await using var check = _pg.NewContext();
        Assert.False(await check.Set<PaymentVoucher>().AnyAsync(v => v.CustomerId == customerId));
    }

    [LivePgFact]
    public async Task Refund_draft_requires_exactly_one_party()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _) = await SeedCreditAsync();

        await using var ctx = _pg.NewContext();
        var dto = RefundDto(customerId, returnId, 10m);
        dto.SupplierId = _pg.SupplierId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Payments(ctx).CreateDraftAsync(dto));
    }

    [LivePgFact]
    public async Task Refund_draft_requires_sales_return()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, _, _) = await SeedCreditAsync();

        await using var ctx = _pg.NewContext();
        var dto = RefundDto(customerId, Guid.Empty, 10m);
        dto.SalesReturnId = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Payments(ctx).CreateDraftAsync(dto));
    }

    [LivePgFact]
    public async Task Refund_draft_rejects_return_of_another_customer()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (_, returnId, _) = await SeedCreditAsync();
        var otherCustomer = await NewCustomerAsync("OTHER");

        await using var ctx = _pg.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Payments(ctx).CreateDraftAsync(RefundDto(otherCustomer, returnId, 10m)));
    }

    // ═════════════════ 2) الترحيل: القيد والرصيد ومنع التكرار ═════════════════

    [LivePgFact]
    public async Task Cash_refund_posts_dr1200_cr1100_once_and_updates_balance_once()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _) = await SeedCreditAsync();
        var before = await CustomerBalanceAsync(customerId);
        Assert.True(before < 0m, "السيناريو يجب أن يُنشئ رصيدًا دائنًا (CurrentBalance سالب)");

        var voucherId = await PostRefundAsync(customerId, returnId, 10m);

        string number;
        await using (var ctx = _pg.NewContext())
            number = await ctx.Set<PaymentVoucher>().AsNoTracking()
                .Where(v => v.Id == voucherId).Select(v => v.VoucherNumber).FirstAsync();

        var (type, net) = await EntryAsync(number);
        Assert.Equal(JournalEntryType.CustomerRefund, type);
        Assert.Equal(10m, net.GetValueOrDefault("1200"));
        Assert.Equal(-10m, net.GetValueOrDefault("1100"));
        Assert.Equal(0m, net.GetValueOrDefault("1101"));
        Assert.Equal(0m, net.Values.Sum());

        Assert.Equal(before + 10m, await CustomerBalanceAsync(customerId));

        await using (var ctx = _pg.NewContext())
            await Payments(ctx).PostAsync(voucherId);

        await using (var ctx = _pg.NewContext())
            Assert.Equal(1, await ctx.Set<JournalEntry>().CountAsync(e => e.ReferenceNumber == number));
        Assert.Equal(before + 10m, await CustomerBalanceAsync(customerId));
    }

    [LivePgFact]
    public async Task Bank_refund_credits_bank_account_1101()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _) = await SeedCreditAsync();

        var voucherId = await PostRefundAsync(customerId, returnId, 10m, method: 2);

        string number;
        await using (var ctx = _pg.NewContext())
            number = await ctx.Set<PaymentVoucher>().AsNoTracking()
                .Where(v => v.Id == voucherId).Select(v => v.VoucherNumber).FirstAsync();

        var (type, net) = await EntryAsync(number);
        Assert.Equal(JournalEntryType.CustomerRefund, type);
        Assert.Equal(10m, net.GetValueOrDefault("1200"));
        Assert.Equal(-10m, net.GetValueOrDefault("1101"));
        Assert.Equal(0m, net.GetValueOrDefault("1100"));
    }

    [LivePgFact]
    public async Task Refund_cannot_exceed_return_remaining_amount()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, total) = await SeedCreditAsync();
        Assert.True(total > 20m, "المردود يجب أن يكون أكبر من 20 لتغطية الاختبار");

        await using (var ctx = _pg.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Payments(ctx).CreateDraftAsync(RefundDto(customerId, returnId, total + 0.01m)));

        await PostRefundAsync(customerId, returnId, 10m);

        await using (var ctx = _pg.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Payments(ctx).CreateDraftAsync(RefundDto(customerId, returnId, total - 5m)));
    }

    [LivePgFact]
    public async Task Refund_cannot_exceed_customer_credit_balance()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, total) = await SeedCreditAsync();

        // فاتورة آجلة جديدة تُقلّص الرصيد الدائن دون أن تمسّ سقف المردود.
        var itemId = await _pg.CreateItemAsync("RF2-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 100m, taxRate: 16m);
        await using (var ctx = _pg.NewContext())
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount,
                PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 16m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 1m, UnitPrice = 15m } }
            });

        var credit = -(await CustomerBalanceAsync(customerId));
        Assert.True(credit > 0m && credit < total, $"الرصيد الدائن ({credit}) يجب أن يقل عن سقف المردود ({total})");

        await using (var ctx = _pg.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Payments(ctx).CreateDraftAsync(RefundDto(customerId, returnId, credit + 1m)));

        await using (var ctx = _pg.NewContext())
        {
            var ok = await Payments(ctx).CreateDraftAsync(RefundDto(customerId, returnId, credit));
            Assert.Equal((int)VoucherStatus.Draft, ok.Status);
        }
    }

    // ═════════════════ 3) العكس الكامل · التخصيصات · التدقيق · الاستعلامات ═════════════════

    [LivePgFact]
    public async Task Full_reversal_restores_balance_and_releases_return_cap()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _) = await SeedCreditAsync();
        var before = await CustomerBalanceAsync(customerId);

        var voucherId = await PostRefundAsync(customerId, returnId, 20m);
        Assert.Equal(before + 20m, await CustomerBalanceAsync(customerId));

        await using (var ctx = _pg.NewContext())
        {
            var reversal = new PaymentVoucherReversalService(ctx, new JournalEntryService(ctx), new RefundAuth());
            await reversal.ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucherId,
                VoucherDate = DateTime.Today,
                Reason = "اختبار عكس رد عميل"
            });
        }

        Assert.Equal(before, await CustomerBalanceAsync(customerId));

        await using (var ctx = _pg.NewContext())
        {
            var original = await ctx.Set<PaymentVoucher>().AsNoTracking().FirstAsync(v => v.Id == voucherId);
            Assert.Equal(VoucherStatus.Reversed, original.Status);

            var rev = await ctx.Set<PaymentVoucherReversal>().AsNoTracking()
                .FirstAsync(r => r.OriginalPaymentVoucherId == voucherId);
            Assert.Null(rev.SupplierId);
            Assert.Equal(customerId, rev.CustomerId);
            Assert.Equal(20m, rev.Amount);
        }

        // السقف تحرر: يمكن رد المبلغ نفسه مرة أخرى (المعكوس يخرج من مجموع الردود).
        await PostRefundAsync(customerId, returnId, 20m);
        Assert.Equal(before + 20m, await CustomerBalanceAsync(customerId));
    }

    [LivePgFact]
    public async Task Refund_rejects_purchase_allocations()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _) = await SeedCreditAsync();

        await using var ctx = _pg.NewContext();
        var svc = Payments(ctx);
        var draft = await svc.CreateDraftAsync(RefundDto(customerId, returnId, 10m));
        await svc.ApproveAsync(draft.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PostAsync(
            draft.Id,
            new[] { new PaymentAllocationInput { PurchaseInvoiceId = Guid.NewGuid(), Amount = 10m } },
            null));
    }

    [LivePgFact]
    public async Task Draft_refund_creates_no_journal_entry_and_audits_creation()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _) = await SeedCreditAsync();

        await using var ctx = _pg.NewContext();
        var draft = await Payments(ctx).CreateDraftAsync(RefundDto(customerId, returnId, 10m));

        Assert.Equal((int)VoucherStatus.Draft, draft.Status);
        Assert.Equal("Customer", draft.PartyType);
        Assert.False(string.IsNullOrWhiteSpace(draft.SalesReturnNumber));
        Assert.Null(draft.JournalEntryId);

        await using var check = _pg.NewContext();
        var saved = await check.Set<PaymentVoucher>().AsNoTracking().FirstAsync(v => v.Id == draft.Id);
        Assert.Null(saved.JournalEntryId);
        Assert.Null(saved.SupplierId);
        Assert.Equal(customerId, saved.CustomerId);
        Assert.Equal(returnId, saved.SalesReturnId);
        Assert.True(await check.Set<AuditTrail>().AnyAsync(a =>
            a.EntityName == "PaymentVoucher" && a.EntityId == draft.Id && a.Action == "Created"));
    }

    [LivePgFact]
    public async Task Statement_shows_reversed_refund_with_its_reversal_without_balance_drift()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _) = await SeedCreditAsync();

        var voucherId = await PostRefundAsync(customerId, returnId, 15m);
        await using (var ctx = _pg.NewContext())
        {
            var reversal = new PaymentVoucherReversalService(ctx, new JournalEntryService(ctx), new RefundAuth());
            await reversal.ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucherId,
                VoucherDate = DateTime.Today,
                Reason = "اختبار كشف الحساب بعد العكس"
            });
        }

        await using var ctxStatement = _pg.NewContext();
        var statement = await new CustomerService(ctxStatement).GetStatementAsync(
            customerId, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));

        // العيب المُصلَح (اكتُشف بفحص واجهي فعلي): كان الكشف يُدرج **العكس** (دائن) ويُسقط **سند الرد** (مدين)
        // لأنه كان يقبل حالة Posted فقط ⇒ انحراف الرصيد الختامي عن الأستاذ. الإثبات: السطران موجودان ومتناظران.
        var refundLine = Assert.Single(statement.Lines, l => l.DocumentNumber.StartsWith("CRF-"));
        var reversalLine = Assert.Single(statement.Lines, l => l.DocumentNumber.StartsWith("PVR-"));
        Assert.Equal(15m, refundLine.Debit);
        Assert.Equal(0m, refundLine.Credit);
        Assert.Equal(0m, reversalLine.Debit);
        Assert.Equal(15m, reversalLine.Credit);

        // الأثر الصافي على الذمة = صفر، والرصيد الختامي للكشف = رصيد العميل التشغيلي (تناظر الكشف/الأستاذ).
        Assert.Equal(await CustomerBalanceAsync(customerId), statement.ClosingBalance);
        Assert.Equal(statement.TotalDebit - statement.TotalCredit, statement.ClosingBalance);
    }

    [LivePgFact]
    public async Task Refund_queries_report_remaining_and_credit_balance()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, _, total) = await SeedCreditAsync();

        await using var ctx = _pg.NewContext();
        var queries = new CustomerRefundQueries(ctx);

        var availability = await queries.GetAvailabilityAsync(customerId);
        Assert.Equal(total, availability.CreditBalance);

        var one = Assert.Single(availability.Returns);
        Assert.Equal(total, one.TotalAmount);
        Assert.Equal(0m, one.RefundedAmount);
        Assert.Equal(total, one.RemainingAmount);
    }
}
