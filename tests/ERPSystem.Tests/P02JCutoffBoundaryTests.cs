using System.Text;
using ERPSystem.Application.DTOs.BankChecks;
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
/// P0-2J — اختبارات حدود تاريخ القطع (إصلاح M1) على قاعدة اختبار معزولة وبيانات اصطناعية.
///
/// القاعدة المُختبَرة (بدون Migration):
///   «حتى asOf» = كل مستندات يوم `asOf` كاملةً + ما قبله، ولا شيء بعده —
///   تُنفَّذ بحد أعلى **حصري** `X &lt; asOf.Date.AddDays(1)` (تعمل صحيحًا للأعمدة date وtimestamp معًا).
///
/// لا تُعدَّل أي دورة شيك، ولا تُكتب أي أرصدة/قيود، ولا تُنشأ تخصيصات.
/// المخرَج المقيس: D:\erpfix-pu01\_p02j_cutoff_log.txt
/// </summary>
public class P02JCutoffBoundaryTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;
    private static readonly List<string> Log = new();

    public P02JCutoffBoundaryTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "p02j";
    }

    private static string Code(string tag) => tag + "-" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Guid> NewCustomerAsync(string tag)
    {
        await using var ctx = _pg.NewContext();
        var c = new Customer
        {
            Id = Guid.NewGuid(), Code = Code(tag), NameAr = "عميل قطع " + tag,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<Guid> NewItemAsync(string tag, decimal unitPrice = 10m)
        => await _pg.CreateItemAsync(Code(tag), unitPrice, 5m, 100_000m, taxRate: 0m);

    private async Task<(Guid InvoiceId, decimal Total)> NewInvoiceAsync(
        Guid customerId, Guid itemId, DateTime? invoiceDate = null, decimal quantity = 10m)
    {
        await using var ctx = _pg.NewContext();
        var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = customerId,
            WarehouseId = _pg.WarehouseId,
            InvoiceDate = invoiceDate ?? DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.OnAccount,
            PaymentMethod = (int)SalesPaymentMethod.OnAccount,
            TaxRate = 0m,
            Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = quantity, UnitPrice = 10m } }
        });
        return (inv.Id, inv.TotalAmount);
    }

    private async Task<Guid> NewReturnAsync(Guid invoiceId, Guid itemId, decimal quantity, DateTime? returnDate = null)
    {
        await using var ctx = _pg.NewContext();
        var r = await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
        {
            SalesInvoiceId = invoiceId,
            WarehouseId = _pg.WarehouseId,
            ReturnDate = returnDate ?? DateTime.Today,
            Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = itemId, Quantity = quantity, UnitPrice = 10m } }
        });
        return r.Id;
    }

    private async Task<Guid> NewReceiptAsync(Guid customerId, decimal amount, DateTime? voucherDate = null,
        bool post = true)
    {
        await using var ctx = _pg.NewContext();
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = voucherDate ?? DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        if (!post) return draft.Id;                     // يبقى «مسودة» (غير مرحَّل)
        await svc.ApproveAsync(draft.Id);
        await svc.PostAsync(draft.Id);
        return draft.Id;
    }

    private async Task<Guid> NewCancelledReceiptAsync(Guid customerId, decimal amount, DateTime? voucherDate = null)
    {
        await using var ctx = _pg.NewContext();
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = voucherDate ?? DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        await svc.ApproveAsync(draft.Id);
        await svc.CancelAsync(draft.Id, "إلغاء اختبار P0-2J");
        return draft.Id;
    }

    private async Task<Guid> NewCheckAsync(Guid customerId, decimal amount, DateTime? receivedOrIssuedDate = null)
    {
        await using var ctx = _pg.NewContext();
        var d = receivedOrIssuedDate ?? DateTime.Today;
        var created = await new BankCheckService(ctx, new JournalEntryService(ctx)).RegisterAsync(new CreateBankCheckDto
        {
            CheckNumber = Code("CHK"), BankName = "بنك قطع P0-2J",
            Direction = BankCheckDirection.ReceivedFromCustomer,
            CustomerId = customerId, Amount = amount,
            IssueDate = d, DueDate = d.AddDays(30), ReceivedOrIssuedDate = d
        });
        return created.Id;
    }

    // تغيير «حالة»/حذف منطقي لمستند اختبار فقط (لا مسار رسمي للإلغاء/الحذف) — موثَّق في التقرير
    private async Task SetInvoiceStatusAsync(Guid invoiceId, DocumentStatus status, bool deleted = false)
    {
        await using var ctx = _pg.NewContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SalesInvoices\" SET \"Status\" = {(int)status}, \"IsDeleted\" = {deleted} WHERE \"Id\" = {invoiceId}");
    }

    private async Task SetReturnStatusAsync(Guid returnId, DocumentStatus status)
    {
        await using var ctx = _pg.NewContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SalesReturns\" SET \"Status\" = {(int)status} WHERE \"Id\" = {returnId}");
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

    private async Task<string> LedgerSnapshotAsync()
    {
        await using var ctx = _pg.NewContext();
        var entries = await ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted)
            .Select(e => e.Id).ToListAsync();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
            .Select(l => new { l.AccountId, l.DebitAmount, l.CreditAmount }).ToListAsync();
        var accounts = await ctx.Set<Account>().AsNoTracking().Where(a => !a.IsDeleted)
            .Select(a => new { a.Code, a.CurrentBalance }).ToListAsync();
        var customers = await ctx.Set<Customer>().AsNoTracking().Where(c => !c.IsDeleted)
            .Select(c => new { c.Id, c.CurrentBalance }).ToListAsync();

        var sb = new StringBuilder();
        sb.Append($"entries={entries.Count};lines={lines.Count};debit={lines.Sum(l => l.DebitAmount):F2};credit={lines.Sum(l => l.CreditAmount):F2}");
        sb.Append(";accounts=").Append(string.Join("|", accounts.OrderBy(a => a.Code).Select(a => $"{a.Code}={a.CurrentBalance:F2}")));
        sb.Append(";customers=").Append(string.Join("|", customers.OrderBy(c => c.Id).Select(c => $"{c.Id:N}={c.CurrentBalance:F2}")));
        return sb.ToString();
    }

    private static string F(decimal v) => v.ToString("F2");

    private static async Task FlushLogAsync()
        => await File.WriteAllLinesAsync(@"D:\erpfix-pu01\_p02j_cutoff_log.txt", Log, Encoding.UTF8);

    // ═════ (1) فاتورة بتاريخ القطع + (2) مردود مرحَّل في يوم القطع بوقت متأخر ═════
    [LivePgFact]
    public async Task T1_T2_Cutoff_day_invoice_and_late_time_posted_return_are_included()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02J-T12");
        var item = await NewItemAsync("P02J-T12-IT");

        var (invoiceId, total) = await NewInvoiceAsync(cust, item);                       // 100 بتاريخ اليوم
        var lateNight = DateTime.Today.AddHours(23).AddMinutes(59).AddSeconds(59);        // 23:59:59 نفس اليوم
        await NewReturnAsync(invoiceId, item, 2m, lateNight);                             // مردود 20 وقت متأخر

        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, cust)!;

        Log.Add($"[T1/T2] فاتورة بتاريخ القطع ({F(total)}) + مردود مرحَّل 20 بوقت {lateNight:HH:mm:ss} ⇒ المتبقي المحسوب={F(row.Total)} (متوقع 80)");
        Assert.Equal(100m, total);
        Assert.Equal(80m, row.Total);
        Assert.Equal(80m, row.Invoices.Single().Balance);
        await FlushLogAsync();
    }

    // ═════ (3) سند قبض في يوم القطع بوقت متأخر + (4) شيك في يوم القطع بوقت متأخر ═════
    [LivePgFact]
    public async Task T3_T4_Cutoff_day_late_time_receipt_and_check_are_included()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02J-T34");
        var item = await NewItemAsync("P02J-T34-IT");
        await NewInvoiceAsync(cust, item);

        var lateNight = DateTime.Today.AddHours(23).AddMinutes(30);
        await NewReceiptAsync(cust, 25m, lateNight);                          // سند قبض 25
        await NewCheckAsync(cust, 15m, lateNight);                            // شيك 15 (بلا رابط فاتورة)

        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, cust)!;

        Log.Add($"[T3/T4] قبض 25 وشيك 15 بوقت {lateNight:HH:mm} في يوم القطع ⇒ مقبوضات={F(row.UnallocatedReceipts)} · شيكات={F(row.UnallocatedChecks)} · رصيد الفاتورة ثابت={F(row.Total)}");
        Assert.Equal(25m, row.UnallocatedReceipts);
        Assert.Equal(15m, row.UnallocatedChecks);
        Assert.Equal(100m, row.Total);                                        // لا يُخصم أي منهما من الفاتورة
        await FlushLogAsync();
    }

    // ═════ (5) مستند بعد تاريخ القطع بيوم + (8) اليوم التالي لتاريخ القطع ═════
    [LivePgFact]
    public async Task T5_T8_Documents_after_the_cutoff_are_excluded_then_included_next_day()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02J-T58");
        var item = await NewItemAsync("P02J-T58-IT");

        var tomorrow = DateTime.Today.AddDays(1);
        var (invoiceId, _) = await NewInvoiceAsync(cust, item);                            // فاتورة اليوم
        await NewReturnAsync(invoiceId, item, 2m, tomorrow);                               // مردود الغد
        await NewReceiptAsync(cust, 40m, tomorrow);                                        // قبض الغد
        await NewCheckAsync(cust, 50m, tomorrow);                                          // شيك الغد

        var today = await AgingAsync(DateTime.Today);
        var nextDay = await AgingAsync(tomorrow);
        var todayRow = Row(today, cust)!;
        var nextRow = Row(nextDay, cust)!;

        Log.Add($"[T5] عند asOf=اليوم: المتبقي={F(todayRow.Total)} (مستندات الغد مستبعدة) · مقبوضات={F(todayRow.UnallocatedReceipts)} · شيكات={F(todayRow.UnallocatedChecks)}");
        Log.Add($"[T8] عند asOf=الغد: المتبقي={F(nextRow.Total)} · مقبوضات={F(nextRow.UnallocatedReceipts)} · شيكات={F(nextRow.UnallocatedChecks)}");

        Assert.Equal(100m, todayRow.Total);                 // مردود الغد غير مخصوم
        Assert.Equal(0m, todayRow.UnallocatedReceipts);     // قبض الغد مستبعد
        Assert.Equal(0m, todayRow.UnallocatedChecks);       // شيك الغد مستبعد
        Assert.Equal(80m, nextRow.Total);                   // الغد: المردود مشمول
        Assert.Equal(40m, nextRow.UnallocatedReceipts);
        Assert.Equal(50m, nextRow.UnallocatedChecks);
        await FlushLogAsync();
    }

    // ═════ (6) مستند غير مرحَّل + (7) مستند ملغى ═════
    [LivePgFact]
    public async Task T6_T7_Non_posted_and_cancelled_documents_are_excluded()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02J-T67");
        var item = await NewItemAsync("P02J-T67-IT");

        var (invoiceId, _) = await NewInvoiceAsync(cust, item);
        var postedReturn = await NewReturnAsync(invoiceId, item, 2m, DateTime.Today);      // مردود مرحَّل 20

        // (6) غير مرحَّل: سند قبض «مسودة» (بلا ترحيل) + مردود محوَّل إلى «مسودة» (لا مسار رسمي للتراجع)
        await NewReceiptAsync(cust, 33m, DateTime.Today, post: false);
        await SetReturnStatusAsync(postedReturn, DocumentStatus.Draft);
        var draftState = await AgingAsync(DateTime.Today);
        var draftRow = Row(draftState, cust)!;

        // (7) ملغى: سند قبض ملغى رسميًا + مردود بحالة «ملغى» (لا مسار رسمي للمردود)
        await NewCancelledReceiptAsync(cust, 44m, DateTime.Today);
        await SetReturnStatusAsync(postedReturn, DocumentStatus.Cancelled);
        var cancelledState = await AgingAsync(DateTime.Today);
        var cancelledRow = Row(cancelledState, cust)!;

        // إعادة المردود إلى «مرحَّل» (حالة نهائية سليمة)
        await SetReturnStatusAsync(postedReturn, DocumentStatus.Posted);
        var restored = Row(await AgingAsync(DateTime.Today), cust)!;

        Log.Add($"[T6] غير مرحَّل (سند مسودة 33 + مردود مسودة 20): المتبقي={F(draftRow.Total)} (100) · مقبوضات={F(draftRow.UnallocatedReceipts)} (0)");
        Log.Add($"[T7] ملغى (سند ملغى 44 + مردود ملغى): المتبقي={F(cancelledRow.Total)} (100) · مقبوضات={F(cancelledRow.UnallocatedReceipts)} (0)");
        Log.Add($"[T6/T7] بعد إعادة المردود إلى «مرحَّل»: المتبقي={F(restored.Total)} (80)");

        Assert.Equal(100m, draftRow.Total);
        Assert.Equal(0m, draftRow.UnallocatedReceipts);
        Assert.Equal(100m, cancelledRow.Total);
        Assert.Equal(0m, cancelledRow.UnallocatedReceipts);
        Assert.Equal(80m, restored.Total);
        await FlushLogAsync();
    }

    // ═════ دقة الحد الأعلى الحصري: بلا فجوة ولا تداخل عند منتصف الليل ═════
    [LivePgFact]
    public async Task T9_Exclusive_boundary_has_no_gap_and_no_overlap_at_midnight()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02J-T9");
        var item = await NewItemAsync("P02J-T9-IT");
        await NewInvoiceAsync(cust, item);                       // فاتورة اليوم (100)

        // مردود عند آخر جزء من يوم القطع (23:59:59.9999999) ⇒ مشمول
        var lastTick = DateTime.Today.AddDays(1).AddTicks(-1);
        await NewReturnAsync(invoiceId: await LatestInvoiceIdAsync(cust), itemId: item,
            quantity: 2m, returnDate: lastTick);                 // مردود 20

        var cutoffDay = await AgingAsync(DateTime.Today);
        var nextDay = await AgingAsync(DateTime.Today.AddDays(1));

        Log.Add($"[T9] مردود عند آخر جزء من يوم القطع ({lastTick:O}) ⇒ المتبقي عند asOf=اليوم={F(Row(cutoffDay, cust)!.Total)} (80) · عند asOf=الغد={F(Row(nextDay, cust)!.Total)} (80) — بلا فجوة ولا تداخل");

        Assert.Equal(80m, Row(cutoffDay, cust)!.Total);
        Assert.Equal(80m, Row(nextDay, cust)!.Total);            // لا يُخصم مرتين ولا يُفقد في الغد
        await FlushLogAsync();
    }

    private async Task<Guid> LatestInvoiceIdAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<SalesInvoice>().AsNoTracking()
            .Where(i => i.CustomerId == customerId && !i.IsDeleted)
            .OrderByDescending(i => i.CreatedAt).Select(i => i.Id).FirstAsync();
    }

    // ═════ تشغيل التقارير لا يغيّر الأستاذ العام ولا أرصدة العملاء ═════
    [LivePgFact]
    public async Task T10_Running_cutoff_reports_does_not_change_ledger_or_balances()
    {
        if (!_pg.Available) return;
        var cust = await NewCustomerAsync("P02J-T10");
        var item = await NewItemAsync("P02J-T10-IT");
        var (invoiceId, _) = await NewInvoiceAsync(cust, item);
        await NewReturnAsync(invoiceId, item, 2m, DateTime.Today.AddHours(22));
        await NewReceiptAsync(cust, 12m, DateTime.Today.AddHours(23));
        await NewCheckAsync(cust, 8m, DateTime.Today.AddHours(23).AddMinutes(59));

        var before = await LedgerSnapshotAsync();
        for (var i = 0; i < 3; i++)
        {
            await AgingAsync(DateTime.Today);
            await AgingAsync(DateTime.Today.AddDays(-1));
            await AgingAsync(DateTime.Today.AddDays(1));
            await AgingAsync(DateTime.Today, receivable: false);
        }
        var after = await LedgerSnapshotAsync();

        Log.Add($"[T10] لقطة الأستاذ قبل/بعد 12 تشغيل تقرير ⇒ متطابقة={before == after} · {before.Substring(0, Math.Min(90, before.Length))}");

        Assert.Equal(before, after);
        await FlushLogAsync();
    }
}
