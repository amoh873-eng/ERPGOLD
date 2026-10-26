using ERPSystem.Application.DTOs.Reports;
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
/// SUB-2b/P3 — المرحلة الثالثة: D‑A (مصادر الرصيد الدائن غير المخصص)، D‑B (نقد الرد من الخزنة 1100
/// لا الدرج 1105)، D‑C (كشف تجاوز السقف خارج الخدمة)، D‑D (إظهار الرصيد الدائن للفاتورة في التفصيل
/// بلا تكرار الرصيد العام وبلا تغيير معنى الأعمار).
///
/// كل الاختبارات **حيّة** على قاعدة معزولة تُنشأ وتُحذف ذاتيًا (<see cref="AccountingPgFixture"/> ⇒ erp_a5_*).
/// لا اتصال بأي قاعدة منشورة، ولا تعديل لأي رصيد تاريخي، ولا Migration.
/// </summary>
public class Sub2bP3Phase3Tests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Sub2bP3Phase3Tests(AccountingPgFixture pg) => _pg = pg;

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
        public string? CurrentUserName() => "p3-phase3-tester";
    }

    private static PaymentVoucherService Payments(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new TestAuth());

    private static ReceiptVoucherService Receipts(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new TestAuth());

    private static SalesReturnService Returns(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx));

    private static PaymentVoucherReversalService Reversals(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new TestAuth());

    // ───────────────────────────── أدوات التهيئة ─────────────────────────────

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

    /// <summary>فاتورة بيع آجلة (OnAccount) 10 × 15 + 16% ⇒ 174.00.</summary>
    private async Task<Guid> NewOnAccountInvoiceAsync(Guid customerId, decimal qty = 10m, decimal price = 15m)
    {
        var itemId = await _pg.CreateItemAsync(
            "P3-" + Guid.NewGuid().ToString("N")[..6], price, 10m, 500m, taxRate: 16m);

        await using var ctx = _pg.NewContext();
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
                new() { ItemId = itemId, Quantity = qty, UnitPrice = price }
            }
        });
        return invoice.Id;
    }

    private async Task<Guid> NewReceiptAsync(Guid customerId, decimal amount, Guid? allocateToInvoice = null)
    {
        Guid receiptId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Receipts(ctx);
            var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = customerId,
                VoucherDate = DateTime.Today,
                PaymentMethod = 1,
                Amount = amount,
                Description = "تحصيل اختبار المرحلة الثالثة"
            });
            receiptId = draft.Id;
            await svc.ApproveAsync(receiptId);
            await svc.PostAsync(receiptId);
        }

        if (allocateToInvoice is { } invoiceId)
        {
            await using var ctx = _pg.NewContext();
            await Receipts(ctx).SetAllocationsAsync(receiptId, new SetReceiptAllocationsRequest
            {
                Allocations = new List<CustomerAllocationInput>
                {
                    new() { SalesInvoiceId = invoiceId, Amount = amount }
                },
                Note = "تخصيص موثق — اختبار المرحلة الثالثة"
            });
        }

        return receiptId;
    }

    /// <summary>مردود جزئي حقيقي عبر الخدمة (40% من الكمية ⇒ 4 وحدات = 69.60 للفاتورة 174).</summary>
    private async Task<(Guid ReturnId, decimal Total)> NewReturnAsync(Guid invoiceId, decimal fraction = 0.4m)
    {
        await using var ctx = _pg.NewContext();
        var line = await ctx.Set<SalesInvoiceLine>().AsNoTracking()
            .Where(l => l.SalesInvoiceId == invoiceId)
            .Select(l => new { l.ItemId, l.Quantity, l.UnitPrice })
            .FirstAsync();

        var ret = await Returns(ctx).CreateAsync(new CreateSalesReturnDto
        {
            SalesInvoiceId = invoiceId,
            WarehouseId = _pg.WarehouseId,
            ReturnDate = DateTime.Today,
            Lines = new List<CreateSalesReturnLineDto>
            {
                new()
                {
                    ItemId = line.ItemId,
                    Quantity = Math.Round(line.Quantity * fraction, 2),
                    UnitPrice = line.UnitPrice
                }
            }
        });

        await using var check = _pg.NewContext();
        var total = await check.Set<SalesReturn>().AsNoTracking()
            .Where(r => r.Id == ret.Id).Select(r => r.TotalAmount).FirstAsync();
        return (ret.Id, total);
    }

    /// <summary>فاتورة 174 + تحصيل موثق 150 + مردود 69.60 ⇒ رصيد فاتورة دائن 45.60 ورصيد عميل −45.60.</summary>
    private async Task<(Guid CustomerId, Guid InvoiceId, Guid ReturnId, decimal ReturnTotal, decimal InvoiceCredit)>
        SeedInvoiceCreditAsync()
    {
        var customerId = await NewCustomerAsync("D3");
        var invoiceId = await NewOnAccountInvoiceAsync(customerId);
        await NewReceiptAsync(customerId, 150m, invoiceId);
        var (returnId, returnTotal) = await NewReturnAsync(invoiceId);
        return (customerId, invoiceId, returnId, returnTotal, 45.60m);
    }

    private async Task<PaymentVoucherDto> PostRefundAsync(
        Guid customerId, Guid returnId, decimal amount, int method = 1, Guid? key = null)
    {
        await using var ctx = _pg.NewContext();
        var svc = Payments(ctx);
        var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            CustomerId = customerId,
            SalesReturnId = returnId,
            VoucherDate = DateTime.Today,
            PaymentMethod = method,
            Amount = amount,
            Description = "رد عميل — المرحلة الثالثة",
            IdempotencyKey = key
        });
        await svc.ApproveAsync(draft.Id);
        return await svc.PostAsync(draft.Id);
    }

    private async Task<AgingReportDto> AgingAsync()
    {
        await using var ctx = _pg.NewContext();
        return await AgingReportQueries.GetAgingAsync(ctx, true, DateTime.Today);
    }

    private static AgingPartyRow Party(AgingReportDto report, Guid customerId)
        => Assert.Single(report.Rows.Where(r => r.PartyId == customerId));

    private async Task<decimal> CustomerBalanceAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<List<(string Code, decimal Debit, decimal Credit)>> JournalLinesAsync(Guid? journalId)
    {
        Assert.NotNull(journalId);
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.JournalEntryId == journalId)
            .Join(ctx.Set<Account>().AsNoTracking(), l => l.AccountId, a => a.Id,
                (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
            .Select(x => new ValueTuple<string, decimal, decimal>(x.Code, x.DebitAmount, x.CreditAmount))
            .ToListAsync();
    }

    // ═══════════════════ D‑D: الرصيد الدائن للفاتورة في التفصيل ═══════════════════

    [LivePgFact]
    public async Task Credit_invoice_line_appears_in_drilldown_without_changing_customer_total()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, _, _, returnTotal, invoiceCredit) = await SeedInvoiceCreditAsync();
        Assert.Equal(69.60m, returnTotal);

        var report = await AgingAsync();
        var row = Party(report, customerId);

        // (أ) المستحق على العميل لا يتأثر بالرصيد الدائن: الفئات = 0 و Total = 0.
        Assert.Equal(0m, row.Total);
        Assert.Equal(0m, row.Bucket0_30 + row.Bucket31_60 + row.Bucket61_90 + row.Bucket90Plus);

        // (ب/د) الرصيد الدائن ظاهر مرة واحدة على مستوى الطرف.
        Assert.Equal(-invoiceCredit, row.CreditBalance);
        Assert.Equal(invoiceCredit, row.CreditBeforeRefunds);
        Assert.Equal(invoiceCredit, row.RemainingRefundableCredit);

        // (D‑D) سطر الفاتورة يحمل **رصيدها الدائن الخاص** ويُوسم كسطر دائن.
        var line = Assert.Single(row.Invoices);
        Assert.True(line.IsCreditLine);
        Assert.Equal(-invoiceCredit, line.Balance);
        Assert.Equal(invoiceCredit, line.CreditBeforeRefunds);
        Assert.Equal(0m, line.RefundsApplied);

        // لا تكرار: سطر دائن لا يساوي مستحق العميل (يُعرض ولا يدخل Total).
        Assert.NotEqual(row.Total, row.Invoices.Sum(i => i.Balance));

        // صافي المركز الموثق = −45.60 ويطابق رصيد العميل التشغيلي في هذه الحالة.
        Assert.Equal(-invoiceCredit, row.NetDocumentedPosition);
        Assert.Equal(-invoiceCredit, await CustomerBalanceAsync(customerId));
        Assert.Equal(0m, row.UnallocatedCreditRemaining);
        Assert.False(row.HasCreditConflict);
    }

    // ═══════════════════ D‑A: الرصيد غير المخصص كمصدر ثانٍ ═══════════════════

    [LivePgFact]
    public async Task Refund_funded_by_unallocated_receipt_consumes_only_that_bucket()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, invoiceId, returnId, _, invoiceCredit) = await SeedInvoiceCreditAsync();

        // مقبوض إضافي **بلا تخصيص موثق** (50) ⇒ رصيد العميل −95.60، والتقرير يعرضه بندًا مستقلًا.
        await NewReceiptAsync(customerId, 50m);

        var before = Party(await AgingAsync(), customerId);
        Assert.Equal(50m, before.UnallocatedReceipts);
        Assert.Equal(50m, before.UnallocatedCreditTotal);
        Assert.Equal(invoiceCredit, before.RemainingRefundableCredit);
        Assert.Equal(invoiceCredit + 50m, before.CreditPoolRemaining);

        // رد 60 (≤ سقف المردود 69.60 و ≤ الرصيد الدائن 95.60) ⇒ 45.60 على رصيد الفاتورة و14.40 من غير المخصص.
        await PostRefundAsync(customerId, returnId, 60m);

        var after = Party(await AgingAsync(), customerId);

        Assert.Equal(60m, after.RefundsPaid);
        Assert.Equal(invoiceCredit, after.RefundsAppliedToInvoiceCredit);
        Assert.Equal(14.40m, after.RefundsBeyondInvoiceCredit);

        // الفاتورة: سطر دائن صفّره الرد بالكامل، وما زال ظاهرًا للتفسير.
        var line = Assert.Single(after.Invoices.Where(i => i.IsCreditLine));
        Assert.Equal(0m, line.Balance);
        Assert.Equal(invoiceCredit, line.RefundsApplied);
        Assert.Equal(invoiceCredit, line.CreditBeforeRefunds);

        // (أ) الفئات المدينة لم تتغيّر بأي رد.
        Assert.Equal(0m, after.Total);
        Assert.Equal(0m, after.CreditBalance);

        // D‑A: غير المخصص بقي **بلا أي ربط بفاتورة** — استُهلك منه فقط ما موّل الرد.
        Assert.Equal(50m, after.UnallocatedReceipts);
        Assert.Equal(50m, after.UnallocatedCreditTotal);
        Assert.Equal(14.40m, after.UnallocatedCreditConsumedByRefunds);
        Assert.Equal(35.60m, after.UnallocatedCreditRemaining);
        Assert.Equal(0m, after.RefundsWithNoDocumentedCredit);
        Assert.Equal(0m, after.RefundsBeyondReturnCap);
        Assert.False(after.HasCreditConflict);

        // المعادلة: صافي المركز الموثق = −35.60 = رصيد العميل بعد رد واحد فقط.
        Assert.Equal(-35.60m, after.NetDocumentedPosition);
        Assert.Equal(-35.60m, await CustomerBalanceAsync(customerId));

        // لا ازدواج: الرد لا يغيّر متبقي الفاتورة المحسوب من المستندات.
        await using var ctx = _pg.NewContext();
        var residual = await new CustomerAllocationQueries(ctx).GetComputedResidualsAsync(DateTime.Today);
        var inv = Assert.Single(residual.Where(r => r.InvoiceId == invoiceId));
        Assert.Equal(-invoiceCredit, inv.ComputedResidual);
    }

    [LivePgFact]
    public async Task Refund_without_documented_credit_is_flagged_as_conflict()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, _, returnId, _, invoiceCredit) = await SeedInvoiceCreditAsync();

        // محاكاة رصيد دائن **بلا مصدر موثق** (عهدة/قيد يدوي/بيانات تاريخية): كتابة مباشرة على الحقل
        // في قاعدة الاختبار فقط. الخدمة تسمح بالرد لأنها تفحص CurrentBalance لا مصدره.
        await using (var ctx = _pg.NewContext())
        {
            var customer = await ctx.Set<Customer>().FirstAsync(c => c.Id == customerId);
            customer.CurrentBalance = -100m;
            await ctx.SaveChangesAsync();
        }

        await PostRefundAsync(customerId, returnId, 60m);

        var row = Party(await AgingAsync(), customerId);

        Assert.Equal(invoiceCredit, row.RefundsAppliedToInvoiceCredit);
        Assert.Equal(14.40m, row.RefundsBeyondInvoiceCredit);
        Assert.Equal(0m, row.UnallocatedCreditTotal);

        // لا مصدر موثق يغطي 14.40 ⇒ تعارض معروض لا يُسوَّى تلقائيًا.
        Assert.Equal(14.40m, row.RefundsWithNoDocumentedCredit);
        Assert.True(row.HasCreditConflict);
        Assert.Equal(0m, row.RefundsBeyondReturnCap);   // السقف المرجعي لم يُتجاوز

        // الأثر: الرصيد التشغيلي −40 بعد رد مشروع واحد؛ والتقرير لا يجد له مصدرًا موثقًا ⇒ فرق 40
        // (المصدر غير الموثق كله) يظهر كتعارض على الأقل بقدر ما استهلكه الرد (14.40).
        Assert.Equal(-40m, await CustomerBalanceAsync(customerId));
        Assert.Equal(0m, row.NetDocumentedPosition);
        Assert.Equal(
            MoneyRounding.Round(-(await CustomerBalanceAsync(customerId)) + row.NetDocumentedPosition),
            40m);
    }

    // ═══════════════════ D‑C: كشف تجاوز سقف الرد خارج الخدمة ═══════════════════

    [LivePgFact]
    public async Task Direct_write_beyond_return_cap_is_detected_by_audit_query()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, _, returnId, returnTotal, invoiceCredit) = await SeedInvoiceCreditAsync();

        // رد مشروع عبر الخدمة: 40 (≤ 69.60).
        await PostRefundAsync(customerId, returnId, 40m);
        Assert.Equal(-invoiceCredit + 40m, await CustomerBalanceAsync(customerId));

        // كتابة **خارج الخدمة** (محاكاة SQL مباشر/إصلاح بيانات): سند مرحَّل بمبلغ السقف كاملًا
        // ⇒ مجموع الردود 109.60 على مردود سقفه 69.60. قاعدة البيانات تقبله: لا Trigger/CHECK يعبر الصفوف (G1).
        Guid forgedId;
        await using (var ctx = _pg.NewContext())
        {
            var cash = await ctx.Set<Account>().AsNoTracking().FirstAsync(a => a.Code == "1100");
            var receivable = await ctx.Set<Account>().AsNoTracking().FirstAsync(a => a.Code == "1200");

            var forged = new PaymentVoucher
            {
                Id = Guid.NewGuid(),
                VoucherNumber = "CAP-" + Guid.NewGuid().ToString("N")[..10],
                VoucherDate = DateTime.Today,
                Status = VoucherStatus.Posted,
                PaymentMethod = PaymentMethod.Cash,
                Amount = returnTotal,
                SupplierId = null,
                CustomerId = customerId,
                SalesReturnId = returnId,
                BeneficiaryName = "كتابة مباشرة — اختبار D-C",
                Description = "سند خارج الخدمة لتجاوز السقف",
                CashOrBankAccountId = cash.Id,
                PayableAccountId = receivable.Id,
                CreatedAt = DateTime.UtcNow,
                PostedAt = DateTime.UtcNow
            };
            ctx.Add(forged);
            await ctx.SaveChangesAsync();       // لا استثناء ⇒ السقف ليس مفروضًا في القاعدة
            forgedId = forged.Id;
        }

        await using (var ctx = _pg.NewContext())
        {
            var violations = await new CustomerAllocationQueries(ctx)
                .GetRefundCapViolationsAsync(DateTime.Today);
            Assert.Equal(40m, violations[customerId]);      // 109.60 − 69.60
        }

        var row = Party(await AgingAsync(), customerId);
        Assert.Equal(109.60m, row.RefundsPaid);
        Assert.Equal(40m, row.RefundsBeyondReturnCap);
        Assert.True(row.HasCreditConflict);

        // الكتابة المباشرة لا تحدّث رصيد العميل ⇒ العميل يبقى على أثر الرد المشروع وحده (دليل على الثغرة).
        Assert.Equal(-invoiceCredit + 40m, await CustomerBalanceAsync(customerId));

        // التنظيف داخل قاعدة الاختبار فقط: إزالة السند المزوَّر (لا حذف أي بيانات أخرى).
        await using (var ctx = _pg.NewContext())
        {
            var forged = await ctx.Set<PaymentVoucher>().FirstAsync(v => v.Id == forgedId);
            ctx.Remove(forged);
            await ctx.SaveChangesAsync();
        }
    }

    // ═══════════════════ D‑B: مسار النقد (الخزنة 1100 لا الدرج 1105) ═══════════════════

    [LivePgFact]
    public async Task Cash_refund_credits_safe_1100_and_never_drawer_1105()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var (customerId, _, returnId, _, _) = await SeedInvoiceCreditAsync();

        var voucher = await PostRefundAsync(customerId, returnId, 25m, method: 1);

        await using var ctx = _pg.NewContext();
        var stored = await ctx.Set<PaymentVoucher>().AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        var cashId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == "1100").Select(a => a.Id).FirstAsync();
        Assert.Equal(cashId, stored.CashOrBankAccountId);

        var lines = await JournalLinesAsync(stored.JournalEntryId);
        Assert.Equal(2, lines.Count);

        var debit = Assert.Single(lines.Where(l => l.Debit > 0m));
        var credit = Assert.Single(lines.Where(l => l.Credit > 0m));
        Assert.Equal("1200", debit.Code);
        Assert.Equal(25m, debit.Debit);
        Assert.Equal("1100", credit.Code);
        Assert.Equal(25m, credit.Credit);

        // لا مساس بدرج الكاشير 1105 ولا بالبنك/الشيك/البطاقة.
        foreach (var code in new[] { "1105", "1101", "1102", "1205" })
            Assert.DoesNotContain(lines, l => l.Code == code);
    }

    // ═══════ سيناريو مركّب: عدة فواتير + جزئي + غير مخصص + رد + عكس ═══════

    [LivePgFact]
    public async Task Multi_invoice_partial_returns_unallocated_receipt_refund_and_reversal_keep_invariants()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var customerId = await NewCustomerAsync("MIX");

        // فاتورة A: 174 (10×15+16%) — تُحصَّل 174 كاملة وتُخصَّص.
        var invoiceA = await NewOnAccountInvoiceAsync(customerId);
        await NewReceiptAsync(customerId, 174m, invoiceA);

        // فاتورة B: 34.80 (2×15+16%) — تبقى مدينة (فئة عمرية).
        await NewOnAccountInvoiceAsync(customerId, qty: 2m);

        // مردود جزئي على A: 4 وحدات = 69.60 ⇒ رصيد دائن للفاتورة 69.60.
        var (returnA, returnTotal) = await NewReturnAsync(invoiceA);
        Assert.Equal(69.60m, returnTotal);

        // مقبوض بلا تخصيص 30 ⇒ بند مستقل.
        await NewReceiptAsync(customerId, 30m);

        var before = Party(await AgingAsync(), customerId);
        Assert.Equal(34.80m, before.Total);                   // فاتورة B فقط
        Assert.Equal(-69.60m, before.CreditBalance);           // فاتورة A دائنة
        Assert.Equal(69.60m, before.CreditBeforeRefunds);
        Assert.Equal(30m, before.UnallocatedReceipts);
        Assert.Equal(2, before.Invoices.Count);
        var creditLine = Assert.Single(before.Invoices.Where(i => i.IsCreditLine));
        Assert.Equal(69.60m, creditLine.CreditBeforeRefunds);
        Assert.Equal(-69.60m, creditLine.Balance);

        // رد جزئي 50 على مردود A.
        var refund = await PostRefundAsync(customerId, returnA, 50m);

        var afterRefund = Party(await AgingAsync(), customerId);
        Assert.Equal(50m, afterRefund.RefundsPaid);
        Assert.Equal(50m, afterRefund.RefundsAppliedToInvoiceCredit);
        Assert.Equal(19.60m, afterRefund.RemainingRefundableCredit);
        Assert.Equal(-19.60m, afterRefund.CreditBalance);
        Assert.Equal(34.80m, afterRefund.Total);               // B لم يتغيّر
        Assert.Equal(30m, afterRefund.UnallocatedCreditTotal);
        Assert.Equal(30m, afterRefund.UnallocatedCreditRemaining);   // الرد لم يتجاوز رصيد الفواتير
        Assert.Equal(0m, afterRefund.UnallocatedCreditConsumedByRefunds);
        Assert.False(afterRefund.HasCreditConflict);

        // الرصيد التشغيلي: A −19.60 + B +34.80 + غير مخصص −30 ⇒ −14.80 (له عندنا).
        Assert.Equal(-14.80m, await CustomerBalanceAsync(customerId));
        Assert.Equal(-14.80m, afterRefund.NetDocumentedPosition);

        // عكس الرد الكامل ⇒ يعود الرصيد الدائن ويُفكّ السقف.
        await using (var ctx = _pg.NewContext())
        {
            await Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = refund.Id,
                VoucherDate = DateTime.Today,
                Reason = "اختبار عكس رد — المرحلة الثالثة"
            });
        }

        var afterReversal = Party(await AgingAsync(), customerId);
        Assert.Equal(0m, afterReversal.RefundsPaid);
        Assert.Equal(0m, afterReversal.RefundsAppliedToInvoiceCredit);
        Assert.Equal(69.60m, afterReversal.RemainingRefundableCredit);
        Assert.Equal(-69.60m, afterReversal.CreditBalance);
        Assert.Equal(34.80m, afterReversal.Total);
        Assert.Equal(30m, afterReversal.UnallocatedCreditRemaining);
        Assert.Equal(-64.80m, await CustomerBalanceAsync(customerId));
        Assert.Equal(-64.80m, afterReversal.NetDocumentedPosition);

        // إجماليات التقرير = مجموع الأطراف (اتساق البنود الجديدة).
        var finalReport = await AgingAsync();
        Assert.Equal(finalReport.TotalRefundsPaid, Math.Round(finalReport.Rows.Sum(r => r.RefundsPaid), 2));
        Assert.Equal(finalReport.TotalUnallocatedCreditRemaining,
            Math.Round(finalReport.Rows.Sum(r => r.UnallocatedCreditRemaining), 2));
        Assert.Equal(finalReport.TotalRefundsBeyondReturnCap,
            Math.Round(finalReport.Rows.Sum(r => r.RefundsBeyondReturnCap), 2));
    }
}
