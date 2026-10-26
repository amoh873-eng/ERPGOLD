using System.Text;
using ERPSystem.Application.DTOs.BankChecks;
using ERPSystem.Application.DTOs.RepCustody;
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
/// P0-2I — اختبارات مراجعة مستقلة (review-only) لحالات حرجة لم تغطها P0-2H.
///
/// ⚠️ هذا الملف **مراجعة فقط**: لا يُعدّل أي ملف إنتاج، ولا يُنشئ تخصيصات، ولا يغيّر قيودًا/أرصدة تاريخية.
/// • قاعدة اختبار معزولة (تُنشأ وتُحذف ذاتيًا) — لا إنتاج/لا VPS.
/// • كل الكتابات عبر الخدمات الرسمية، والاستثناء الوحيد الموثَّق: تغيير «حالة»/«حذف منطقي»
///   لمستند اختبار عبر SQL (لأنه لا يوجد مسار رسمي للإلغاء/الحذف) ثم قياس الاستبعاد.
/// • المخرَج المقيس يُكتب في D:\erpfix-pu01\_p02i_review_log.txt
/// </summary>
public class P02IIndependentReviewTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;
    private static readonly List<string> Log = new();

    public P02IIndependentReviewTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "p02i";
    }

    private static string Code(string tag) => tag + "-" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Guid> NewCustomerAsync(string tag)
    {
        await using var ctx = _pg.NewContext();
        var c = new Customer
        {
            Id = Guid.NewGuid(), Code = Code(tag), NameAr = "عميل مراجعة " + tag,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<Guid> NewItemAsync(string tag, decimal unitPrice = 10m)
        => await _pg.CreateItemAsync(Code(tag), unitPrice, 5m, 100_000m, taxRate: 0m);

    /// <summary>فاتورة بيع مرحَّلة بنوع/أسلوب سداد محدَّدين (بلا ضريبة) — تُعيد (الفاتورة، الإجمالي).</summary>
    private async Task<(Guid InvoiceId, decimal Total)> NewInvoiceAsync(
        Guid customerId, Guid itemId, SalesInvoiceType type, SalesPaymentMethod method,
        decimal quantity = 10m, decimal unitPrice = 10m, DateTime? invoiceDate = null)
    {
        await using var ctx = _pg.NewContext();
        var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = customerId,
            WarehouseId = _pg.WarehouseId,
            InvoiceDate = invoiceDate ?? DateTime.Today,
            InvoiceType = (int)type,
            PaymentMethod = (int)method,
            TaxRate = 0m,
            Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = quantity, UnitPrice = unitPrice } }
        });
        return (inv.Id, inv.TotalAmount);
    }

    private async Task<decimal> InvoicePaidAmountAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<SalesInvoice>().AsNoTracking()
            .Where(i => i.Id == invoiceId).Select(i => i.PaidAmount).FirstAsync();
    }

    private async Task<decimal> CustomerCurrentBalanceAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<Guid> NewReturnAsync(Guid invoiceId, Guid itemId, decimal quantity, decimal unitPrice,
        DateTime? returnDate = null)
    {
        await using var ctx = _pg.NewContext();
        var r = await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
        {
            SalesInvoiceId = invoiceId,
            WarehouseId = _pg.WarehouseId,
            ReturnDate = returnDate ?? DateTime.Today,
            Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = itemId, Quantity = quantity, UnitPrice = unitPrice } }
        });
        return r.Id;
    }

    private async Task<Guid> NewReceiptAsync(Guid customerId, decimal amount, DateTime? voucherDate = null)
    {
        await using var ctx = _pg.NewContext();
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = voucherDate ?? DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        await svc.ApproveAsync(draft.Id);
        await svc.PostAsync(draft.Id);
        return draft.Id;
    }

    private async Task<Guid> NewCheckAsync(Guid customerId, decimal amount, Guid? relatedInvoiceId = null,
        DateTime? receivedOrIssuedDate = null)
    {
        await using var ctx = _pg.NewContext();
        var d = receivedOrIssuedDate ?? DateTime.Today;
        var created = await new BankCheckService(ctx, new JournalEntryService(ctx)).RegisterAsync(new CreateBankCheckDto
        {
            CheckNumber = Code("CHK"), BankName = "بنك مراجعة P0-2I",
            Direction = BankCheckDirection.ReceivedFromCustomer,
            CustomerId = customerId, RelatedInvoiceId = relatedInvoiceId,
            Amount = amount, IssueDate = d, DueDate = d.AddDays(30), ReceivedOrIssuedDate = d
        });
        return created.Id;
    }

    private async Task ChangeCheckStateAsync(Guid checkId, string transition)
    {
        await using var ctx = _pg.NewContext();
        var svc = new BankCheckService(ctx, new JournalEntryService(ctx));
        switch (transition)
        {
            case "deposit": await svc.DepositAsync(checkId, DateTime.Today); break;
            case "clear": await svc.ClearAsync(checkId, DateTime.Today); break;
            case "bounce": await svc.BounceAsync(checkId, DateTime.Today); break;
        }
    }

    /// <summary>تحصيل ميداني ضمن عهدة مندوب: مدين 1110 / دائن 1200 (يخفّض ذمم العميل).</summary>
    private async Task<Guid> NewRepCustodyCollectAsync(Guid customerId, decimal amount, DateTime? date = null)
    {
        await using var ctx = _pg.NewContext();
        var dto = await new RepCustodyService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateRepCustodyDto
        {
            Effect = RepCustodyEffect.Collect, RepUserId = "REP-P02I", CustomerId = customerId,
            Amount = amount, Timestamp = date ?? DateTime.Today
        }, "p02i");
        return dto.Id;
    }

    // ── تغييرات حالة/حذف لمستند اختبار (لا مسار رسمي للإلغاء/الحذف) — تُوثَّق في التقرير ──
    private async Task SetInvoiceStatusAsync(Guid invoiceId, DocumentStatus status, bool deleted = false)
    {
        await using var ctx = _pg.NewContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SalesInvoices\" SET \"Status\" = {(int)status}, \"IsDeleted\" = {deleted} WHERE \"Id\" = {invoiceId}");
    }

    private async Task SetReturnStatusAsync(Guid returnId, DocumentStatus status, bool deleted = false)
    {
        await using var ctx = _pg.NewContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SalesReturns\" SET \"Status\" = {(int)status}, \"IsDeleted\" = {deleted} WHERE \"Id\" = {returnId}");
    }

    private async Task<AgingReportDto> AgingAsync(DateTime asOf, bool receivable = true)
    {
        await using var ctx = _pg.NewContext();
        return await AgingReportQueries.GetAgingAsync(ctx, receivable, asOf);
    }

    private static AgingPartyRow? Row(AgingReportDto dto, Guid customerId)
        => dto.Rows.FirstOrDefault(r => r.PartyId == customerId);

    private static decimal PartyValue(AgingReportDto dto, Guid customerId)
        => dto.Rows.Where(r => r.PartyId == customerId).Sum(r => r.Total + r.CreditBalance);

    private async Task<decimal> AccountNetAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.AccountId == accountId)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }

    private static string F(decimal v) => v.ToString("F2");

    private static async Task FlushLogAsync()
    {
        await File.WriteAllLinesAsync(@"D:\erpfix-pu01\_p02i_review_log.txt", Log, Encoding.UTF8);
    }

    // ════════════ R1: فاتورة نقدية + مردود ⇒ الائتمان غير ظاهر في تقرير العملاء ════════════
    [LivePgFact]
    public async Task R1_Cash_invoice_and_its_return_are_invisible_in_AR_aging()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02I-R1");
        var item = await NewItemAsync("P02I-R1-IT");

        var (invoiceId, total) = await NewInvoiceAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Cash);
        var paid = await InvoicePaidAmountAsync(invoiceId);
        var ar0 = await AccountNetAsync("1200");
        var cash0 = await AccountNetAsync("1100");

        var beforeReturn = await AgingAsync(DateTime.Today);
        await NewReturnAsync(invoiceId, item, 2m, 10m);          // مردود 20 على فاتورة نقدية
        var afterReturn = await AgingAsync(DateTime.Today);

        var balance = await CustomerCurrentBalanceAsync(cust);
        var ar1 = await AccountNetAsync("1200");
        var cash1 = await AccountNetAsync("1100");

        Log.Add($"[R1] فاتورة نقدية: Total={F(total)} · PaidAmount(مخزَّن)={F(paid)} ⇒ الرصيد مستبعد من أعمار العملاء");
        Log.Add($"[R1] قبل المردود: صف العميل={Row(beforeReturn, cust)?.Total.ToString("F2") ?? "غير موجود"} (الفاتورة النقدية لا تُدين 1200 ولا تظهر في الأعمار)");
        Log.Add($"[R1] بعد مردود 20: صف العميل={Row(afterReturn, cust)?.Total.ToString("F2") ?? "غير موجود"} · Δ1200={F(ar1 - ar0)} · Δ1100={F(cash1 - cash0)}");
        Log.Add($"[R1] رصيد العميل التشغيلي بعد المردود={F(balance)} (ائتمان 20 لا يظهر في تقرير الأعمار)");

        Assert.Equal(total, paid);                                  // PaidAmount = الإجمالي (مثبت)
        Assert.Null(Row(beforeReturn, cust));                       // الفاتورة النقدية مستبعدة
        Assert.Null(Row(afterReturn, cust));                        // المتبقي سالب ⇒ مستبعد (الائتمان مخفي)
        Assert.Equal(0m, ar1 - ar0);                                // مردود النقدي لا يمسّ 1200
        Assert.NotEqual(0m, cash1 - cash0);                         // أثره في 1100
        await FlushLogAsync();
    }

    // ════════════ R2: فاتورة آجلة + مردود + قبض ⇒ لا خصم مزدوج ════════════
    [LivePgFact]
    public async Task R2_On_account_invoice_with_return_and_receipt_is_not_double_deducted()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02I-R2");
        var item = await NewItemAsync("P02I-R2-IT");

        var ar0 = await AccountNetAsync("1200");
        var (invoiceId, total) = await NewInvoiceAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);
        await NewReturnAsync(invoiceId, item, 2m, 10m);          // 20
        await NewReceiptAsync(cust, 30m);                        // 30 قبض
        var ar1 = await AccountNetAsync("1200");

        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, cust)!;
        var residual = row.Total + row.CreditBalance;
        var deltaAr = ar1 - ar0;

        Log.Add($"[R2] فاتورة آجلة 100 + مردود 20 + قبض 30: المتبقي المحسوب={F(residual)} (100−20 مرة واحدة) · بند المقبوضات={F(row.UnallocatedReceipts)}");
        Log.Add($"[R2] Δ1200={F(deltaAr)} = المتبقي({F(residual)}) − المقبوضات({F(row.UnallocatedReceipts)}) = {F(residual - row.UnallocatedReceipts)}");

        Assert.Equal(100m, total);
        Assert.Equal(80m, residual);
        Assert.Equal(80m, row.Invoices.Single().Balance);
        Assert.Equal(30m, row.UnallocatedReceipts);
        Assert.Equal(deltaAr, residual - row.UnallocatedReceipts);   // المعادلة القابلة للإثبات (سيناريو مضبوط)
        await FlushLogAsync();
    }

    // ════════════ R3: مردود بتاريخ بعد تاريخ القطع ════════════
    [LivePgFact]
    public async Task R3_Return_dated_after_asOf_is_not_deducted_before_its_date()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02I-R3");
        var item = await NewItemAsync("P02I-R3-IT");

        var (invoiceId, _) = await NewInvoiceAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);
        await NewReturnAsync(invoiceId, item, 2m, 10m, DateTime.Today.AddDays(1));   // مردود بتاريخ الغد

        var today = PartyValue(await AgingAsync(DateTime.Today), cust);
        var tomorrow = PartyValue(await AgingAsync(DateTime.Today.AddDays(1)), cust);

        Log.Add($"[R3] مردود بتاريخ الغد: عند asOf=اليوم={F(today)} · عند asOf=الغد={F(tomorrow)}");
        Assert.Equal(100m, today);
        Assert.Equal(80m, tomorrow);
        await FlushLogAsync();
    }

    // ════════════ R4: تماثل تاريخ القطع (أعمدة date مقابل timestamp) — دليل مقيس بعد إصلاح P0-2J ════════════
    [LivePgFact]
    public async Task R4_Same_day_documents_with_a_time_component_are_included_by_the_cutoff()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02I-R4");
        var item = await NewItemAsync("P02I-R4-IT");

        // الفاتورة: عمود InvoiceDate نوعه date ⇒ تُقارن بتاريخ فقط.
        var arBefore = await AccountNetAsync("1200");
        var (invoiceId, _) = await NewInvoiceAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);

        // ثلاثة مستندات «اليوم» لكن مع جزء وقت (نفس ما يحدث فعليًا في شاشة الشيكات: الافتراضي DateTime.Now)
        var withTime = DateTime.Today.AddHours(23);
        await NewReturnAsync(invoiceId, item, 0.5m, 10m, withTime);          // مردود 5
        await NewReceiptAsync(cust, 10m, withTime);                          // قبض 10
        await NewCheckAsync(cust, 7m, receivedOrIssuedDate: withTime);       // شيك 7 (بلا رابط فاتورة)
        var arAfter = await AccountNetAsync("1200");

        var sameDay = await AgingAsync(DateTime.Today);                      // asOf = اليوم 00:00
        var nextDay = await AgingAsync(DateTime.Today.AddDays(1));
        var sameRow = Row(sameDay, cust);
        var nextRow = Row(nextDay, cust);

        var deltaAr = arAfter - arBefore;
        var reportAttributedSameDay = (sameRow?.Total ?? 0m) + (sameRow?.UnallocatedReceipts ?? 0m)
                                      + (sameRow?.UnallocatedChecks ?? 0m);

        Log.Add($"[R4] مستندات نفس اليوم مع جزء وقت (23:00): مردود 5 + قبض 10 + شيك 7 · Δ1200={F(deltaAr)} (100−5−10−7=78)");
        Log.Add($"[R4] asOf=اليوم (بعد إصلاح P0-2J): الفاتورة={F(sameRow?.Total ?? 0m)} · مقبوضات={F(sameRow?.UnallocatedReceipts ?? 0m)} · شيكات={F(sameRow?.UnallocatedChecks ?? 0m)} ⇒ ما يفسّره التقرير={F(reportAttributedSameDay)} مقابل 1200={F(deltaAr)}");
        Log.Add($"[R4] asOf=الغد (00:00): الفاتورة={F(nextRow?.Total ?? 0m)} · مقبوضات={F(nextRow?.UnallocatedReceipts ?? 0m)} · شيكات={F(nextRow?.UnallocatedChecks ?? 0m)}");

        // P0-2J: بعد إصلاح M1 تشمل مستندات يوم القطع كاملًا (حتى ذات الوقت 23:00) وتتّسق مع الأستاذ.
        Assert.Equal(95m, sameRow!.Total);                 // المردود 5 يُخصم في يومه
        Assert.Equal(10m, sameRow.UnallocatedReceipts);
        Assert.Equal(7m, sameRow.UnallocatedChecks);
        Assert.Equal(78m, deltaAr);                        // الأثر المحاسبي الفعلي على 1200 (مدين−دائن)
        Assert.Equal(112m, reportAttributedSameDay);       // 95 (متبقٍّ) + 10 (قبض) + 7 (شيك)
        Assert.Equal(95m, nextRow!.Total);
        Assert.Equal(10m, nextRow.UnallocatedReceipts);
        Assert.Equal(7m, nextRow.UnallocatedChecks);
        // المعادلة مُتّسقة في يوم القطع وفي الغد: (متبقٍّ − مقبوضات − شيكات) = 1200
        Assert.Equal(deltaAr, sameRow.Total - sameRow.UnallocatedReceipts - sameRow.UnallocatedChecks);
        Assert.Equal(deltaAr, nextRow.Total - nextRow.UnallocatedReceipts - nextRow.UnallocatedChecks);
        await FlushLogAsync();
    }

    // ════════════ R5: مستندات ملغاة/محذوفة منطقيًا (لا مسار رسمي ⇒ تغيير حالة اختباري) ════════════
    [LivePgFact]
    public async Task R5_Cancelled_or_soft_deleted_documents_are_excluded()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02I-R5");
        var item = await NewItemAsync("P02I-R5-IT");

        var (invoiceId, _) = await NewInvoiceAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);
        var returnId = await NewReturnAsync(invoiceId, item, 2m, 10m);
        var baseline = PartyValue(await AgingAsync(DateTime.Today), cust);   // 80

        await SetReturnStatusAsync(returnId, DocumentStatus.Cancelled);
        var cancelledReturn = PartyValue(await AgingAsync(DateTime.Today), cust);
        await SetReturnStatusAsync(returnId, DocumentStatus.Posted, deleted: true);
        var deletedReturn = PartyValue(await AgingAsync(DateTime.Today), cust);
        await SetReturnStatusAsync(returnId, DocumentStatus.Posted);

        await SetInvoiceStatusAsync(invoiceId, DocumentStatus.Cancelled);
        var cancelledInvoice = PartyValue(await AgingAsync(DateTime.Today), cust);
        await SetInvoiceStatusAsync(invoiceId, DocumentStatus.Posted, deleted: true);
        var deletedInvoice = PartyValue(await AgingAsync(DateTime.Today), cust);
        await SetInvoiceStatusAsync(invoiceId, DocumentStatus.Posted);

        var restored = PartyValue(await AgingAsync(DateTime.Today), cust);

        Log.Add($"[R5] الأساس (مردود مرحَّل 20)={F(baseline)} · مردود ملغى={F(cancelledReturn)} · مردود محذوف={F(deletedReturn)} · فاتورة ملغاة={F(cancelledInvoice)} · فاتورة محذوفة={F(deletedInvoice)} · بعد الإعادة={F(restored)}");

        Assert.Equal(80m, baseline);
        Assert.Equal(100m, cancelledReturn);      // الملغى لا يُخصم
        Assert.Equal(100m, deletedReturn);        // المحذوف لا يُخصم
        Assert.Equal(0m, cancelledInvoice);
        Assert.Equal(0m, deletedInvoice);
        Assert.Equal(80m, restored);
        await FlushLogAsync();
    }

    // ════════════ R6: تحصيل عهدة مندوب يخفّض 1200 ولا يظهر في تقرير الأعمار (فجوة نطاق) ════════════
    [LivePgFact]
    public async Task R6_Rep_custody_collection_reduces_1200_but_is_absent_from_AR_aging()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02I-R6");
        var item = await NewItemAsync("P02I-R6-IT");

        var ar0 = await AccountNetAsync("1200");
        await NewInvoiceAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);  // 100
        var arAfterInvoice = await AccountNetAsync("1200");
        var before = await AgingAsync(DateTime.Today);
        var custodyId = await NewRepCustodyCollectAsync(cust, 40m);
        var arAfterCustody = await AccountNetAsync("1200");
        var after = await AgingAsync(DateTime.Today);

        var beforeRow = Row(before, cust)!;
        var afterRow = Row(after, cust)!;
        var invoiceAr = arAfterInvoice - ar0;
        var custodyAr = arAfterCustody - arAfterInvoice;
        var ledgerForCustomer = invoiceAr + custodyAr;

        Log.Add($"[R6] تحصيل عهدة مندوب 40 (مدين 1110/دائن 1200): Δ1200 للفاتورة={F(invoiceAr)} · Δ1200 للعهدة={F(custodyAr)} · صافي أثر المستندَين={F(ledgerForCustomer)}");
        Log.Add($"[R6] تقرير الأعمار قبل/بعد: المتبقي={F(beforeRow.Total)}/{F(afterRow.Total)} · مقبوضات={F(afterRow.UnallocatedReceipts)} · شيكات={F(afterRow.UnallocatedChecks)} ⇒ لا تغيّر رغم خفض 1200 بـ40");
        Log.Add($"[R6] الفرق المُفسَّر: التقرير يشرح {F(afterRow.Total)} بينما صافي أثر المستندَين على 1200 = {F(ledgerForCustomer)} ⇒ فرق {F(afterRow.Total - ledgerForCustomer)} (تحصيل العهدة) غير مُفسَّر بالتقرير");

        Assert.NotEqual(Guid.Empty, custodyId);
        Assert.Equal(100m, invoiceAr);
        Assert.Equal(-40m, custodyAr);
        Assert.Equal(60m, ledgerForCustomer);
        Assert.Equal(100m, beforeRow.Total);
        Assert.Equal(100m, afterRow.Total);
        Assert.Equal(0m, afterRow.UnallocatedReceipts);
        Assert.Equal(0m, afterRow.UnallocatedChecks);
        Assert.Equal(40m, afterRow.Total - ledgerForCustomer);
        await FlushLogAsync();
    }

    // ════════════ R7: قبض يتجاوز المتبقي (دفعة مقدَّمة) ⇒ لا مقاصّة ولا عرض ائتمان ════════════
    [LivePgFact]
    public async Task R7_Advance_receipt_above_the_residual_is_not_netted()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02I-R7");
        var item = await NewItemAsync("P02I-R7-IT");

        var ar0 = await AccountNetAsync("1200");
        await NewInvoiceAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);   // 100
        await NewReceiptAsync(cust, 150m);                                                            // 150 قبض
        var ar1 = await AccountNetAsync("1200");

        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, cust)!;
        var residual = row.Total + row.CreditBalance;

        Log.Add($"[R7] فاتورة 100 + قبض 150: المتبقي المعروض={F(residual)} · بند المقبوضات={F(row.UnallocatedReceipts)} · صافي 1200={F(ar1 - ar0)}");
        Log.Add($"[R7] الائتمان (−50) لا يُقاصّ مقابل الفاتورة ولا يُعرض كرصيد سالب/دائن في فرع العملاء");

        Assert.Equal(100m, residual);
        Assert.Equal(150m, row.UnallocatedReceipts);
        Assert.Equal(-50m, ar1 - ar0);          // 1200 فعلًا دائن 50
        Assert.Equal(0m, row.CreditBalance);     // لا يُعرض أي ائتمان في فرع العملاء
        await FlushLogAsync();
    }

    // ════════════ R8: بنود الشيكات — احتساب واحد فقط و1102 غير مضاف ════════════
    [LivePgFact]
    public async Task R8_Check_buckets_do_not_double_count_and_1102_is_not_added_to_AR()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02I-R8");
        var item = await NewItemAsync("P02I-R8-IT");

        var (invoiceId, _) = await NewInvoiceAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);
        var base1102 = await AccountNetAsync("1102");

        var unlinked = await NewCheckAsync(cust, 30m);                 // بلا رابط فاتورة
        var registered = await AgingAsync(DateTime.Today);
        var afterRegister1102 = await AccountNetAsync("1102");
        await ChangeCheckStateAsync(unlinked, "deposit");
        var deposited = await AgingAsync(DateTime.Today);
        await ChangeCheckStateAsync(unlinked, "clear");
        var cleared = await AgingAsync(DateTime.Today);
        var afterClear1102 = await AccountNetAsync("1102");

        await NewCheckAsync(cust, 40m, relatedInvoiceId: invoiceId);    // مرتبط بفاتورة
        var linked = await AgingAsync(DateTime.Today);
        var afterLinked1102 = await AccountNetAsync("1102");

        var bouncedCheck = await NewCheckAsync(cust, 25m);              // بلا رابط ⇒ مرتجع
        await ChangeCheckStateAsync(bouncedCheck, "bounce");
        var bounced = await AgingAsync(DateTime.Today);

        var r = Row(registered, cust)!;
        Log.Add($"[R8] شيك 30 بلا رابط: مسجَّل={F(r.UnallocatedChecks)} · مودع={F(Row(deposited, cust)!.UnallocatedChecks)} · محصَّل={F(Row(cleared, cust)!.UnallocatedChecks)} · 1102 عند التسجيل={F(afterRegister1102)} وبعد التحصيل={F(afterClear1102)}");
        Log.Add($"[R8] شيك 40 مرتبط: بند «مرتبط بفاتورة»={F(Row(linked, cust)!.InvoiceLinkedChecks)} · «بلا رابط»={F(Row(linked, cust)!.UnallocatedChecks)} · 1102={F(afterLinked1102)}");
        Log.Add($"[R8] شيك 25 مرتجع: «بلا رابط»={F(Row(bounced, cust)!.UnallocatedChecks)} · رصيد الفاتورة ثابت={F(Row(bounced, cust)!.Total)} · 1102(base)={F(base1102)}");

        Assert.Equal(30m, r.UnallocatedChecks);
        Assert.Equal(30m, Row(deposited, cust)!.UnallocatedChecks);
        Assert.Equal(30m, Row(cleared, cust)!.UnallocatedChecks);       // لا تكرار بعد التحصيل
        Assert.Equal(base1102 + 30m, afterRegister1102);
        Assert.Equal(base1102, afterClear1102);                          // التحصيل نقل 1102→1101
        Assert.Equal(40m, Row(linked, cust)!.InvoiceLinkedChecks);
        Assert.Equal(30m, Row(linked, cust)!.UnallocatedChecks);         // المرتبط لم يدخل «بلا رابط»
        Assert.Equal(100m, Row(linked, cust)!.Total);                    // ولم يُخصم من الفاتورة
        Assert.Equal(30m, Row(bounced, cust)!.UnallocatedChecks);        // المرتجع مستبعد
        await FlushLogAsync();
    }
}
