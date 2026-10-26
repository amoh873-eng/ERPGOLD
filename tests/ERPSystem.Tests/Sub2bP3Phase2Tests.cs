using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.DTOs.Shifts;
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
/// SUB-2b/P3 — المرحلة الثانية: تكامل رد أموال العملاء مع تقرير الأعمار (R13/R16)،
/// التزامن الحقيقي على PostgreSQL، منع تكرار الطلب (Idempotency)، وردية الكاشير، والتدقيق.
///
/// كل الاختبارات حيّة على قاعدة معزولة تُنشأ وتُحذف ذاتيًا (AccountingPgFixture ⇒ erp_a5_*).
/// لا اتصال بأي قاعدة منشورة، ولا تعديل لأي رصيد تاريخي.
/// </summary>
public class Sub2bP3Phase2Tests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Sub2bP3Phase2Tests(AccountingPgFixture pg) => _pg = pg;

    // ─────────────── تصاريح اختبار (تُحقن خلفيًا كما في الإنتاج) ───────────────

    private sealed class TestAuth : IVoucherAuthorization
    {
        public bool Refund { get; init; } = true;
        public bool Reverse { get; init; } = true;
        public bool Allocate { get; init; } = true;

        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanRefund() => Refund;
        public bool CanReverse() => Reverse;
        public bool CanAllocate() => Allocate;
        public string? CurrentUserName() => "p3-phase2-tester";
    }

    private static PaymentVoucherService Payments(AppDbContext ctx, IVoucherAuthorization? auth = null)
        => new(ctx, new JournalEntryService(ctx), auth ?? new TestAuth());

    private static ReceiptVoucherService Receipts(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new TestAuth());

    private static SalesReturnService Returns(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx));

    private static PaymentVoucherReversalService Reversals(AppDbContext ctx, IVoucherAuthorization? auth = null)
        => new(ctx, new JournalEntryService(ctx), auth ?? new TestAuth());

    private static CreatePaymentVoucherDto RefundDto(
        Guid customerId, Guid returnId, decimal amount, int method = 1, Guid? key = null)
        => new()
        {
            CustomerId = customerId,
            SalesReturnId = returnId,
            VoucherDate = DateTime.Today,
            PaymentMethod = method,
            Amount = amount,
            Description = "رد مبلغ عميل — المرحلة الثانية",
            IdempotencyKey = key
        };

    // ─────────────── أدوات تهيئة ───────────────

    private async Task<Guid> NewCustomerAsync(string label)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Code = label + "-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "عميل " + label,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<Guid> CreateReturnAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        var line = await ctx.Set<SalesInvoiceLine>().AsNoTracking()
            .Where(l => l.SalesInvoiceId == invoiceId)
            .Select(l => new { l.ItemId, l.Quantity, l.UnitPrice })
            .FirstAsync();

        var salesReturn = await Returns(ctx).CreateAsync(new CreateSalesReturnDto
        {
            SalesInvoiceId = invoiceId,
            WarehouseId = _pg.WarehouseId,
            ReturnDate = DateTime.Today,
            Lines = new List<CreateSalesReturnLineDto>
            {
                // 40% من الكمية المباعة — مردود جزئي حقيقي عبر الخدمة (قيود + رصيد)
                new() { ItemId = line.ItemId, Quantity = Math.Round(line.Quantity * 0.4m, 2), UnitPrice = line.UnitPrice }
            }
        });
        return salesReturn.Id;
    }

    private async Task<decimal> CustomerBalanceAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<(Guid Id, PaymentVoucherDto Dto)> CreateApprovedRefundAsync(
        Guid customerId, Guid returnId, decimal amount, Guid? key = null, int method = 1)
    {
        await using var ctx = _pg.NewContext();
        var svc = Payments(ctx);
        var draft = await svc.CreateDraftAsync(RefundDto(customerId, returnId, amount, method, key));
        await svc.ApproveAsync(draft.Id);
        return (draft.Id, draft);
    }

    private async Task<Guid> PostRefundAsync(
        Guid customerId, Guid returnId, decimal amount, Guid? postKey = null, int method = 1)
    {
        var (id, _) = await CreateApprovedRefundAsync(customerId, returnId, amount);
        await using var ctx = _pg.NewContext();
        await Payments(ctx).PostAsync(id, null, postKey);
        return id;
    }

    private async Task<AgingReportDto> AgingAsync()
    {
        await using var ctx = _pg.NewContext();
        return await AgingReportQueries.GetAgingAsync(ctx, true, DateTime.Today);
    }

    private static AgingPartyRow Party(AgingReportDto report, Guid customerId)
        => Assert.Single(report.Rows.Where(r => r.PartyId == customerId));

    /// <summary>
    /// فاتورة آجلة + تحصيل كامل + **تخصيص موثق** للتحصيل على الفاتورة + مردود جزئي.
    /// النتيجة: الفاتورة تحمل رصيدًا دائنًا حقيقيًا في تقرير الأعمار = إجمالي المردود،
    /// ورصيد العميل سالب بنفس المبلغ (قابل للرد).
    /// </summary>
    private async Task<(Guid CustomerId, Guid ReturnId, decimal ReturnTotal, Guid InvoiceId)>
        SeedOnAccountCreditAsync(decimal invoiceQty = 10m, decimal unitPrice = 15m)
    {
        var customerId = await NewCustomerAsync("OA");
        var itemId = await _pg.CreateItemAsync(
            "OA-" + Guid.NewGuid().ToString("N")[..6], unitPrice, 10m, 500m, taxRate: 16m);

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

        Guid receiptId;
        await using (var ctx = _pg.NewContext())
        {
            var receipt = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = customerId,
                VoucherDate = DateTime.Today,
                PaymentMethod = 1,
                Amount = invoiceTotal,
                Description = "تحصيل كامل قبل المردود"
            });
            receiptId = receipt.Id;
            await Receipts(ctx).ApproveAsync(receiptId);
            await Receipts(ctx).PostAsync(receiptId);
        }

        // تخصيص موثق: يجعل «المتبقي المحسوب» للفاتورة = 0 ثم سالبًا بعد المردود.
        await using (var ctx = _pg.NewContext())
        {
            await Receipts(ctx).SetAllocationsAsync(receiptId, new SetReceiptAllocationsRequest
            {
                Allocations = new List<CustomerAllocationInput>
                {
                    new() { SalesInvoiceId = invoiceId, Amount = invoiceTotal }
                },
                Note = "تخصيص تحصيل كامل — اختبار المرحلة الثانية"
            });
        }

        var returnId = await CreateReturnAsync(invoiceId);
        await using var check = _pg.NewContext();
        var ret = await check.Set<SalesReturn>().AsNoTracking().FirstAsync(r => r.Id == returnId);
        return (customerId, returnId, ret.TotalAmount, invoiceId);
    }

    /// <summary>
    /// فاتورة **مسدَّدة بشيك** (R16) ثم مردود جزئي: مردودها يُقيَّد دائن 1200 فعلًا
    /// ⇒ يجب أن يظهر أثره في تقرير الأعمار (كان مهملًا قبل الإصلاح).
    /// </summary>
    private async Task<(Guid CustomerId, Guid ReturnId, decimal ReturnTotal, Guid InvoiceId)>
        SeedCheckCreditAsync(decimal invoiceQty = 10m, decimal unitPrice = 15m)
    {
        var customerId = await NewCustomerAsync("CK");
        var itemId = await _pg.CreateItemAsync(
            "CK-" + Guid.NewGuid().ToString("N")[..6], unitPrice, 10m, 500m, taxRate: 16m);

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Check,
                TaxRate = 16m,
                CheckNumber = "CK-" + Guid.NewGuid().ToString("N")[..8],
                CheckBankName = "بنك الاختبار",
                CheckDueDate = DateTime.Today.AddDays(30),
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = invoiceQty, UnitPrice = unitPrice }
                }
            });
            invoiceId = invoice.Id;
        }

        var returnId = await CreateReturnAsync(invoiceId);
        await using var check = _pg.NewContext();
        var ret = await check.Set<SalesReturn>().AsNoTracking().FirstAsync(r => r.Id == returnId);
        return (customerId, returnId, ret.TotalAmount, invoiceId);
    }

    /// <summary>فاتورة نقدية + مردود جزئي (مردود النقد لا يمسّ 1200 ⇒ لا يجب أن يظهر في الأعمار).</summary>
    private async Task<Guid> SeedCashReturnAsync(decimal invoiceQty = 10m, decimal unitPrice = 15m)
    {
        var customerId = await NewCustomerAsync("CS");
        var itemId = await _pg.CreateItemAsync(
            "CS-" + Guid.NewGuid().ToString("N")[..6], unitPrice, 10m, 500m, taxRate: 16m);

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                TaxRate = 16m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = invoiceQty, UnitPrice = unitPrice }
                }
            });
            invoiceId = invoice.Id;
        }

        await CreateReturnAsync(invoiceId);
        return customerId;
    }

    // ═══════════════════════ R16 — مردود فاتورة سُدِّدت بشيك ═══════════════════════

    [LivePgFact]
    public async Task Check_paid_invoice_return_is_reflected_in_ar_aging()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, _, returnTotal, _) = await SeedCheckCreditAsync();
        Assert.True(returnTotal > 0m);

        var report = await AgingAsync();
        var row = Party(report, customerId);

        // مردود فاتورة الشيك يُقيَّد دائن 1200 ⇒ يظهر مرة واحدة، ويولّد رصيدًا دائنًا (لا مدينًا).
        Assert.Equal(0m, row.Total);
        Assert.Equal(returnTotal, row.EffectiveReturnsTotal);
        Assert.Equal(-returnTotal, row.CreditBalance);
        Assert.Equal(returnTotal, row.CreditBeforeRefunds);
        Assert.Equal(0m, row.RefundsPaid);
        Assert.Equal(returnTotal, row.RemainingRefundableCredit);

        // ملاحظة: صفوف الرصيد الدائن لا تُضاف إلى تفصيل الفواتير (سلوك P0-2 القائم) — الدليل على R16
        // هو ظهور الرصيد الدائن نفسه بمقدار المردود؛ قبل الإصلاح كان يساوي صفرًا فيغيب الصف تمامًا.
        Assert.True(row.IsComputedResidual);
        Assert.True(row.InvoicedTotal > 0m);
    }

    [LivePgFact]
    public async Task Cash_invoice_return_is_not_reflected_in_ar_aging()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var customerId = await SeedCashReturnAsync();

        var report = await AgingAsync();

        // مردود النقد يعكس 1100/1105 لا 1200 ⇒ لا صف ولا رصيد دائن، والعميل رصيده صفر.
        Assert.DoesNotContain(report.Rows, r => r.PartyId == customerId);
        Assert.Equal(0m, await CustomerBalanceAsync(customerId));
    }

    // ═════════════════ R13 — الرد المرحَّل يخفض الرصيد الدائن مرة واحدة ═════════════════

    [LivePgFact]
    public async Task Posted_refund_reduces_aging_credit_exactly_once()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, returnTotal, _) = await SeedOnAccountCreditAsync();

        var reportBefore = await AgingAsync();
        var before = Party(reportBefore, customerId);
        Assert.Equal(returnTotal, before.CreditBeforeRefunds);
        Assert.Equal(-returnTotal, before.CreditBalance);
        Assert.Equal(0m, before.Total);

        const decimal refundAmount = 20m;
        await PostRefundAsync(customerId, returnId, refundAmount);

        var report = await AgingAsync();
        var after = Party(report, customerId);

        // (ب) مستحق للعميل قبل الرد · (ج) تم رده · (د) لا يزال قابلًا للرد — مفصولة بلا ازدواج.
        Assert.Equal(returnTotal, after.CreditBeforeRefunds);
        Assert.Equal(refundAmount, after.RefundsPaid);
        Assert.Equal(returnTotal - refundAmount, after.RemainingRefundableCredit);
        Assert.Equal(-(returnTotal - refundAmount), after.CreditBalance);
        Assert.Equal(0m, after.RefundsBeyondInvoiceCredit);

        // (أ) الرصيد المستحق على العميل لهذا الطرف لم يتغيّر بأي رد، ولم تدخل الردود في فئاته.
        Assert.Equal(0m, after.Total);

        // إجماليات التقرير تتغيّر بالمقدار نفسه مرة واحدة (القاعدة مشتركة مع بيانات اختبار أخرى).
        Assert.Equal(reportBefore.TotalRefundsPaid + refundAmount, report.TotalRefundsPaid);
        Assert.Equal(returnTotal - refundAmount, after.RemainingRefundableCredit);

        // الرصيد التشغيلي للعميل تحرّك مرة واحدة فقط.
        Assert.Equal(-(returnTotal - refundAmount), await CustomerBalanceAsync(customerId));
    }

    [LivePgFact]
    public async Task Reversed_refund_restores_aging_credit()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, returnTotal, _) = await SeedOnAccountCreditAsync();

        const decimal refundAmount = 20m;
        var refundId = await PostRefundAsync(customerId, returnId, refundAmount);
        Assert.Equal(refundAmount, Party(await AgingAsync(), customerId).RefundsPaid);

        await using (var ctx = _pg.NewContext())
        {
            await Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = refundId,
                VoucherDate = DateTime.Today,
                Reason = "اختبار عكس رد — تقرير الأعمار"
            });
        }

        var after = Party(await AgingAsync(), customerId);
        Assert.Equal(0m, after.RefundsPaid);
        Assert.Equal(returnTotal, after.CreditBeforeRefunds);
        Assert.Equal(returnTotal, after.RemainingRefundableCredit);
        Assert.Equal(-returnTotal, after.CreditBalance);
        Assert.Equal(-returnTotal, await CustomerBalanceAsync(customerId));
    }

    // ═══════════════════════ التزامن الحقيقي (PostgreSQL معزول) ═══════════════════════

    [LivePgFact]
    public async Task Concurrent_refunds_cannot_exceed_return_cap_in_either_order()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // ترتيبان مختلفان للوصول (A أولًا ثم B، والعكس) — النتيجة يجب أن تكون صحيحة في الحالتين.
        foreach (var delayFirst in new[] { true, false })
        {
            var (customerId, returnId, returnTotal, _) = await SeedOnAccountCreditAsync();
            var each = Math.Round(returnTotal * 0.6m, 2);
            Assert.True(each * 2m > returnTotal, "السؤال يتطلب أن يتجاوز مجموع الطلبين السقف");

            var (a, _) = await CreateApprovedRefundAsync(customerId, returnId, each);
            var (b, _) = await CreateApprovedRefundAsync(customerId, returnId, each);

            async Task<Exception?> TryPostAsync(Guid id, bool delay)
            {
                try
                {
                    if (delay) await Task.Delay(120);
                    await using var ctx = _pg.NewContext();
                    await Payments(ctx).PostAsync(id);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            var results = await Task.WhenAll(
                TryPostAsync(a, delayFirst),
                TryPostAsync(b, delayFirst ? false : true));

            var failures = results.Where(x => x is not null).ToList();
            Assert.Single(failures);
            Assert.IsType<InvalidOperationException>(failures[0]);

            await using var check = _pg.NewContext();
            var postedSum = await check.Set<PaymentVoucher>().AsNoTracking()
                .Where(v => v.SalesReturnId == returnId && v.Status == VoucherStatus.Posted && !v.IsDeleted)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;

            // (1) مجموع الردود المرحَّلة لا يتجاوز السقف، والناجح لم يُقتطع.
            Assert.True(postedSum <= returnTotal + 0.001m);
            Assert.Equal(each, postedSum);

            // (2) رصيد العميل خُصم مرة واحدة فقط بمقدار الناجح.
            Assert.Equal(-returnTotal + postedSum, await CustomerBalanceAsync(customerId));

            // (3) قيد مرحَّل واحد فقط — لا قيدان متزامنان.
            var journaled = await check.Set<PaymentVoucher>().AsNoTracking()
                .CountAsync(v => (v.Id == a || v.Id == b) && v.JournalEntryId != null);
            Assert.Equal(1, journaled);

            // (4) لا بيانات جزئية: الفاشل بقي معتمدًا بلا قيد ولا تغيير رصيد، وسُجّل رفضه.
            var failedId = results[0] is null ? b : a;
            var failed = await check.Set<PaymentVoucher>().AsNoTracking().FirstAsync(v => v.Id == failedId);
            Assert.Equal(VoucherStatus.Approved, failed.Status);
            Assert.Null(failed.JournalEntryId);
            Assert.True(await check.Set<AuditTrail>().AnyAsync(x =>
                x.EntityId == failedId && x.Action == "RefundRejected"));
        }
    }

    [LivePgFact]
    public async Task Concurrent_reversal_and_new_refund_preserve_invariants()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        foreach (var delayRefund in new[] { true, false })
        {
            var (customerId, returnId, returnTotal, _) = await SeedOnAccountCreditAsync();

            var firstRefundId = await PostRefundAsync(customerId, returnId, 30m);
            var (secondRefundId, _) = await CreateApprovedRefundAsync(customerId, returnId, 30m);

            async Task ReverseAsync()
            {
                if (delayRefund) await Task.Delay(150);
                await using var ctx = _pg.NewContext();
                await Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
                {
                    OriginalPaymentVoucherId = firstRefundId,
                    VoucherDate = DateTime.Today,
                    Reason = "عكس متزامن مع محاولة رد"
                });
            }

            async Task TryPostNewRefundAsync()
            {
                if (!delayRefund) await Task.Delay(150);
                try
                {
                    await using var ctx = _pg.NewContext();
                    await Payments(ctx).PostAsync(secondRefundId);
                }
                catch (InvalidOperationException)
                {
                    // مسموح قانونيًا: العكس قد لا يكون قد حرّر السقف/الرصيد بعد.
                }
            }

            await Task.WhenAll(ReverseAsync(), TryPostNewRefundAsync());

            await using var check = _pg.NewContext();
            var netPosted = await check.Set<PaymentVoucher>().AsNoTracking()
                .Where(v => v.SalesReturnId == returnId && v.Status == VoucherStatus.Posted && !v.IsDeleted)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;

            // الثوابت في كل ترتيب: لا تجاوز للسقف، رصيد العميل = −السقف + صافي المرحَّل،
            // ومستند عكس واحد فقط للأصل، ولا سند مرحَّل بلا قيد.
            Assert.True(netPosted <= returnTotal + 0.001m);
            Assert.True(netPosted >= 0m);
            Assert.Equal(-returnTotal + netPosted, await CustomerBalanceAsync(customerId));

            Assert.Equal(1, await check.Set<PaymentVoucherReversal>().AsNoTracking()
                .CountAsync(r => r.OriginalPaymentVoucherId == firstRefundId && !r.IsDeleted));

            var postedJournalIds = await check.Set<PaymentVoucher>().AsNoTracking()
                .Where(v => v.SalesReturnId == returnId && v.Status == VoucherStatus.Posted && !v.IsDeleted)
                .Select(v => v.JournalEntryId).ToListAsync();
            Assert.All(postedJournalIds, je => Assert.NotNull(je));

            var reversedOriginal = await check.Set<PaymentVoucher>().AsNoTracking()
                .FirstAsync(v => v.Id == firstRefundId);
            Assert.Equal(VoucherStatus.Reversed, reversedOriginal.Status);
        }
    }

    // ═══════════════════════════ Idempotency ═══════════════════════════

    [LivePgFact]
    public async Task Idempotent_create_returns_same_voucher_without_second_document()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _, _) = await SeedOnAccountCreditAsync();
        var key = Guid.NewGuid();

        Guid firstId, secondId;
        await using (var ctx = _pg.NewContext())
            firstId = (await Payments(ctx).CreateDraftAsync(RefundDto(customerId, returnId, 10m, key: key))).Id;
        await using (var ctx = _pg.NewContext())
            secondId = (await Payments(ctx).CreateDraftAsync(RefundDto(customerId, returnId, 10m, key: key))).Id;

        Assert.Equal(firstId, secondId);

        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.Set<PaymentVoucher>().CountAsync(v => v.IdempotencyKey == key));
        // إعادة الإرسال لا تُنشئ سطر تدقيق «Created» ثانيًا (لا مستند ثانٍ أصلًا).
        Assert.Equal(1, await check.Set<AuditTrail>()
            .CountAsync(a => a.EntityId == firstId && a.Action == "Created"));
    }

    [LivePgFact]
    public async Task Idempotent_post_creates_one_journal_and_one_balance_change()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, returnTotal, _) = await SeedOnAccountCreditAsync();

        // مفتاح واحد لسلسلة الطلب (إنشاء + ترحيل): إعادة استخدامه مع سند آخر مرفوضة بالتصميم.
        var key = Guid.NewGuid();

        PaymentVoucherDto created;
        await using (var ctx = _pg.NewContext())
            created = await Payments(ctx).CreateDraftAsync(RefundDto(customerId, returnId, 25m, key: key));
        await using (var ctx = _pg.NewContext()) await Payments(ctx).ApproveAsync(created.Id);
        await using (var ctx = _pg.NewContext()) await Payments(ctx).PostAsync(created.Id, null, key);

        var balanceAfterFirst = await CustomerBalanceAsync(customerId);
        Assert.Equal(-returnTotal + 25m, balanceAfterFirst);

        // إعادة الطلب نفسه (انقطاع استجابة بعد نجاح المعاملة) بمفتاح الترحيل نفسه ⇒ نفس النتيجة.
        PaymentVoucherDto replay;
        await using (var ctx = _pg.NewContext())
            replay = await Payments(ctx).PostAsync(created.Id, null, key);

        Assert.Equal(created.Id, replay.Id);
        Assert.Equal((int)VoucherStatus.Posted, replay.Status);

        await using (var check = _pg.NewContext())
        {
            var voucher = await check.Set<PaymentVoucher>().AsNoTracking().FirstAsync(v => v.Id == created.Id);
            Assert.NotNull(voucher.JournalEntryId);
            Assert.Equal(1, await check.Set<JournalEntry>()
                .CountAsync(e => e.ReferenceNumber == voucher.VoucherNumber));
            Assert.Equal(1, await check.Set<PaymentVoucher>().CountAsync(v =>
                v.SalesReturnId == returnId && v.Status == VoucherStatus.Posted && !v.IsDeleted));
        }

        Assert.Equal(balanceAfterFirst, await CustomerBalanceAsync(customerId));

        // مفتاح مختلف على السند المُرحَّل نفسه: لا صرف ثانٍ ولا قيد ثانٍ (السند مُرحَّل يُعاد كما هو).
        await using (var ctx = _pg.NewContext())
            await Payments(ctx).PostAsync(created.Id, null, Guid.NewGuid());

        await using (var check2 = _pg.NewContext())
            Assert.Equal(1, await check2.Set<JournalEntry>()
                .CountAsync(e => e.ReferenceNumber == created.VoucherNumber));
        Assert.Equal(balanceAfterFirst, await CustomerBalanceAsync(customerId));
    }

    [LivePgFact]
    public async Task New_refund_with_new_key_is_a_distinct_legitimate_document()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, returnTotal, _) = await SeedOnAccountCreditAsync();

        var first = await PostRefundAsync(customerId, returnId, 20m, Guid.NewGuid());
        var second = await PostRefundAsync(customerId, returnId, 15m, Guid.NewGuid());

        Assert.NotEqual(first, second);

        await using var check = _pg.NewContext();
        var posted = await check.Set<PaymentVoucher>().AsNoTracking()
            .Where(v => v.SalesReturnId == returnId && v.Status == VoucherStatus.Posted && !v.IsDeleted)
            .ToListAsync();
        Assert.Equal(2, posted.Count);
        Assert.Equal(35m, posted.Sum(v => v.Amount));
        Assert.True(posted.Sum(v => v.Amount) <= returnTotal);
        Assert.All(posted, v => Assert.NotNull(v.JournalEntryId));
        Assert.Equal(-returnTotal + 35m, await CustomerBalanceAsync(customerId));
    }

    // ═══════════════════════ التدقيق والصلاحيات ═══════════════════════

    [LivePgFact]
    public async Task Rejected_refund_attempts_are_audited_without_secrets()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _, _) = await SeedOnAccountCreditAsync();

        // (1) رفض الصلاحية: يُسجَّل بالهوية والوقت UTC والمرجع والمبلغ والسبب والنتيجة.
        await using (var ctx = _pg.NewContext())
        {
            var denied = Payments(ctx, new TestAuth { Refund = false });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                denied.CreateDraftAsync(RefundDto(customerId, returnId, 10m)));
        }

        await using (var check = _pg.NewContext())
        {
            var audit = await check.Set<AuditTrail>().AsNoTracking()
                .Where(a => a.Action == "RefundRejected")
                .OrderByDescending(a => a.PerformedAt)
                .FirstOrDefaultAsync();

            Assert.NotNull(audit);
            Assert.Equal("PaymentVoucher", audit!.EntityName);
            Assert.Equal("p3-phase2-tester", audit.PerformedBy);
            Assert.Contains("المبلغ=10.00", audit.Details);
            Assert.Contains("العميل=" + customerId, audit.Details);
            Assert.Contains("المردود=" + returnId, audit.Details);
            Assert.Contains("Rejected", audit.Details);
            Assert.DoesNotContain("password", audit.Details, StringComparison.OrdinalIgnoreCase);
            Assert.True((DateTime.UtcNow - audit.PerformedAt).Duration() < TimeSpan.FromMinutes(5),
                "PerformedAt يجب أن يكون UTC قريبًا من الآن");
        }

        // (2) تجاوز السقف أثناء الترحيل: تدقيق مرتبط بسند الرد نفسه (EntityId = السند).
        var (firstId, _) = await CreateApprovedRefundAsync(customerId, returnId, 60m);
        var (secondId, _) = await CreateApprovedRefundAsync(customerId, returnId, 30m);

        await using (var ctx = _pg.NewContext()) await Payments(ctx).PostAsync(firstId);
        await using (var ctx = _pg.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Payments(ctx).PostAsync(secondId));

        await using (var check2 = _pg.NewContext())
        {
            var rejected = await check2.Set<AuditTrail>().AsNoTracking()
                .FirstOrDefaultAsync(x => x.EntityId == secondId && x.Action == "RefundRejected");
            Assert.NotNull(rejected);
            Assert.Contains("المبلغ=30.00", rejected!.Details);
            Assert.Contains("المردود=" + returnId, rejected.Details);
            // السند الفاشل لم يُرحَّل ولم يُنشأ له قيد.
            var failed = await check2.Set<PaymentVoucher>().AsNoTracking().FirstAsync(v => v.Id == secondId);
            Assert.Equal(VoucherStatus.Approved, failed.Status);
            Assert.Null(failed.JournalEntryId);
        }
    }

    // ═══════════════════════ وردية الكاشير ═══════════════════════

    [LivePgFact]
    public async Task Cash_refund_appears_in_shift_summary_without_changing_expected_cash()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, returnId, _, _) = await SeedOnAccountCreditAsync();

        Guid shiftId;
        await using (var ctx = _pg.NewContext())
            shiftId = (await new CashierShiftService(ctx, new JournalEntryService(ctx))
                .StartAsync(100m, "cashier-p3")).Id;

        ShiftSummaryDto before;
        await using (var ctx = _pg.NewContext())
            before = await new CashierShiftService(ctx, new JournalEntryService(ctx))
                .ComputeSummaryAsync(shiftId);

        await PostRefundAsync(customerId, returnId, 20m, method: 1);

        ShiftSummaryDto after;
        await using (var ctx = _pg.NewContext())
            after = await new CashierShiftService(ctx, new JournalEntryService(ctx))
                .ComputeSummaryAsync(shiftId);

        // الرد النقدي يُدفع من الخزنة 1100: يظهر بندًا مستقلًا، ولا يغيّر متوقَّع الدرج 1105.
        Assert.Equal(20m, after.CustomerCashRefundsFromSafe - before.CustomerCashRefundsFromSafe);
        Assert.Equal(before.Expected, after.Expected);
        Assert.Equal(before.CashRefunds, after.CashRefunds);
        Assert.Equal(before.CashSales, after.CashSales);
    }
}
