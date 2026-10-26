using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة D — تقارير سندات القبض/الصرف وقيود المستندات الأربعة على PostgreSQL معزول.
///
/// يغطّي: ظهور كل نوع في تقريره فقط (لا تسرّب بين الأنواع) · عمل الفلاتر (المستند/القيد/الطرف/الحالة) ·
/// الإجماليات على مستوى البند (لا تضخيم لمبلغ الرأس) · عدم إنشاء أي قيد أو تغيير أي رصيد عند فتح التقرير ·
/// بقاء التقارير القائمة تعمل (لا انحدار).
/// </summary>
public class VoucherReportPgTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public VoucherReportPgTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAllVouchers : ERPSystem.Application.Security.IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public string? CurrentUserName() => "report-tester";
    }

    private static ReceiptVoucherService Receipt(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    private static PaymentVoucherService Payment(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    private static Dictionary<string, string?> P(params (string K, string? V)[] items)
        => items.ToDictionary(i => i.K, i => i.V);

    private async Task<string> PostReceiptAsync(decimal amount, string? description = null)
    {
        await using var ctx = _pg.NewContext();
        var dto = await Receipt(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = _pg.CustomerId,
            VoucherDate = DateTime.Today,
            PaymentMethod = 1,
            Amount = amount,
            Description = description
        });
        await Receipt(ctx).ApproveAsync(dto.Id);
        await Receipt(ctx).PostAsync(dto.Id);
        return dto.VoucherNumber;
    }

    private async Task<string> PostPaymentAsync(decimal amount)
    {
        await using var ctx = _pg.NewContext();
        var dto = await Payment(ctx).CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = _pg.SupplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = 1,
            Amount = amount
        });
        await Payment(ctx).ApproveAsync(dto.Id);
        await Payment(ctx).PostAsync(dto.Id);
        return dto.VoucherNumber;
    }

    private static string Col(ERPSystem.Application.DTOs.Reports.GenericReportTable t, string header)
        => t.Headers.First(h => h == header);

    private static List<string> Values(ERPSystem.Application.DTOs.Reports.GenericReportTable t, string header)
    {
        var idx = t.Headers.IndexOf(header);
        return t.Rows.Select(r => r[idx]).ToList();
    }

    // ═════════════ 12) كل نوع في تقريره فقط — لا تسرّب بين الأنواع ═════════════

    [LivePgFact]
    public async Task Reports_are_type_scoped_with_no_cross_leakage()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        // PU-03B: Arrange صريح — ذمّة كافية لسند الصرف (90) قبل أي ترحيل.
        await using (var seedCtx = _pg.NewContext())
            await AccountingPgFixture.SeedPayableForAsync(seedCtx, _pg.SupplierId, 90m);

        var receiptNumber = await PostReceiptAsync(150m, "تقرير قبض");
        var paymentNumber = await PostPaymentAsync(90m);

        await using var ctx = _pg.NewContext();
        var reports = new ReportService(ctx);
        var from = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.Today.ToString("yyyy-MM-dd");

        var receiptList = await reports.GetReportAsync("receipt-vouchers", P(("from", from), ("to", to)));
        var paymentList = await reports.GetReportAsync("payment-vouchers", P(("from", from), ("to", to)));
        var receiptEntries = await reports.GetReportAsync("receipt-voucher-entries", P(("from", from), ("to", to), ("docNumber", receiptNumber)));
        var paymentEntries = await reports.GetReportAsync("payment-voucher-entries", P(("from", from), ("to", to), ("docNumber", paymentNumber)));
        var salesEntries = await reports.GetReportAsync("sales-invoice-entries", P(("from", from), ("to", to)));

        // قائمة سندات القبض: سندات القبض فقط (لا أي سند صرف)
        var receiptDocs = Values(receiptList, "رقم السند");
        Assert.Contains(receiptNumber, receiptDocs);
        Assert.DoesNotContain(paymentNumber, receiptDocs);

        // قائمة سندات الصرف: سندات الصرف فقط
        var paymentDocs = Values(paymentList, "رقم السند");
        Assert.Contains(paymentNumber, paymentDocs);
        Assert.DoesNotContain(receiptNumber, paymentDocs);

        // قيود سندات القبض: كل بند يخص سند قبض، والعكس لسندات الصرف
        Assert.NotEmpty(receiptEntries.Rows);
        Assert.All(Values(receiptEntries, "رقم المستند"), d => Assert.Equal(receiptNumber, d));
        Assert.All(Values(receiptEntries, "نوع القيد"), k => Assert.Contains("سند قبض", k));

        Assert.NotEmpty(paymentEntries.Rows);
        Assert.All(Values(paymentEntries, "رقم المستند"), d => Assert.Equal(paymentNumber, d));
        Assert.All(Values(paymentEntries, "نوع القيد"), k => Assert.Contains("سند صرف", k));

        // تقرير قيود فواتير المبيعات لا يحتوي أي بند من السندات (لا تسرّب أنواع)
        var salesDocs = Values(salesEntries, "رقم المستند");
        Assert.DoesNotContain(receiptNumber, salesDocs);
        Assert.DoesNotContain(paymentNumber, salesDocs);
    }

    // ═════════════ 13) عمل الفلاتر (المستند/الطرف/الحالة/القيد/المدة) ═════════════

    [LivePgFact]
    public async Task Filters_narrow_the_result_set()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var first = await PostReceiptAsync(111m, "فلتر أ");
        var second = await PostReceiptAsync(222m, "فلتر ب");

        await using var ctx = _pg.NewContext();
        var reports = new ReportService(ctx);
        var from = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.Today.ToString("yyyy-MM-dd");

        // فلتر رقم المستند
        var byDoc = await reports.GetReportAsync("receipt-vouchers", P(("from", from), ("to", to), ("docNumber", first)));
        Assert.Single(byDoc.Rows);
        Assert.Equal(first, Values(byDoc, "رقم السند").Single());

        // فلتر الطرف (العميل المزروع في بيئة الاختبار)
        var byParty = await reports.GetReportAsync("receipt-vouchers", P(("from", from), ("to", to), ("party", "عميل A5")));
        Assert.Contains(first, Values(byParty, "رقم السند"));

        // فلتر الحالة: 3 = مرحّل (يظهر الاثنان) · 1 = مسودة (لا نتائج — لا مسودات في هذه البيئة)
        var posted = await reports.GetReportAsync("receipt-vouchers", P(("from", from), ("to", to), ("status", "3")));
        Assert.Contains(first, Values(posted, "رقم السند"));
        Assert.Contains(second, Values(posted, "رقم السند"));

        var drafts = await reports.GetReportAsync("receipt-vouchers", P(("from", from), ("to", to), ("status", "1")));
        Assert.Equal(0, drafts.Rows.Count);

        // فلتر رقم القيد على تقرير القيود
        var entryNumber = await ctx.Set<JournalEntry>()
            .Where(e => e.ReferenceNumber == first).Select(e => e.EntryNumber).FirstAsync();
        var byEntry = await reports.GetReportAsync("receipt-voucher-entries",
            P(("from", from), ("to", to), ("entryNumber", entryNumber)));
        Assert.NotEmpty(byEntry.Rows);
        Assert.All(Values(byEntry, "رقم القيد"), e => Assert.Equal(entryNumber, e));

        // فلتر المدة: فترة خارج النطاق ⇒ لا نتائج
        var old = await reports.GetReportAsync("receipt-vouchers", P(("from", "2020-01-01"), ("to", "2020-01-31")));
        Assert.Equal(0, old.Rows.Count);
    }

    // ═════════════ 14) الإجماليات على مستوى البند (لا تضخيم لمبلغ الرأس) ═════════════

    [LivePgFact]
    public async Task Totals_are_line_level_and_not_inflated_for_multi_line_documents()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var number = await PostReceiptAsync(500m, "إجماليات");

        await using var ctx = _pg.NewContext();
        var reports = new ReportService(ctx);
        var from = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.Today.ToString("yyyy-MM-dd");

        // على مستوى السند: المبلغ مرة واحدة فقط
        var list = await reports.GetReportAsync("receipt-vouchers", P(("from", from), ("to", to), ("docNumber", number)));
        Assert.Single(list.Rows);
        Assert.Equal("عدد=1", list.Totals![1]);
        Assert.Equal("500.00", list.Totals![4]);

        // على مستوى البند: بندان (صندوق/ذمم) ومجموعاهما 500 لا 1000
        var entries = await reports.GetReportAsync("receipt-voucher-entries", P(("from", from), ("to", to), ("docNumber", number)));
        Assert.Equal(2, entries.Rows.Count);

        var debit = entries.Rows.Sum(r => decimal.Parse(r[7], System.Globalization.CultureInfo.InvariantCulture));
        var credit = entries.Rows.Sum(r => decimal.Parse(r[8], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(500m, debit);
        Assert.Equal(500m, credit);
        Assert.Contains("فرق=0.00", entries.Totals![9]);
    }

    // ═════════════ 15) فتح التقارير لا يُنشئ أي قيد ولا يغيّر أي رصيد ═════════════

    [LivePgFact]
    public async Task Opening_reports_creates_no_entries_and_keeps_ledger_unchanged()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await PostReceiptAsync(77m, "قراءة فقط");

        int entriesBefore, linesBefore;
        decimal debitBefore, creditBefore;

        await using (var ctx = _pg.NewContext())
        {
            entriesBefore = await ctx.Set<JournalEntry>().CountAsync();
            linesBefore = await ctx.Set<JournalEntryLine>().CountAsync();
            debitBefore = await ctx.Set<JournalEntryLine>().SumAsync(l => (decimal?)l.DebitAmount) ?? 0m;
            creditBefore = await ctx.Set<JournalEntryLine>().SumAsync(l => (decimal?)l.CreditAmount) ?? 0m;
        }

        // فتح التقارير الثمانية جميعًا
        await using (var ctx = _pg.NewContext())
        {
            var reports = new ReportService(ctx);
            var pars = P(("from", DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd")),
                         ("to", DateTime.Today.ToString("yyyy-MM-dd")));

            foreach (var key in new[]
            {
                "receipt-vouchers", "receipt-voucher-entries", "payment-vouchers", "payment-voucher-entries",
                "sales-invoice-entries", "sales-return-entries", "purchase-invoice-entries", "purchase-return-entries"
            })
            {
                var table = await reports.GetReportAsync(key, pars);
                Assert.False(string.IsNullOrWhiteSpace(table.TitleAr));
                Assert.NotEmpty(table.Headers);
            }
        }

        await using (var ctx = _pg.NewContext())
        {
            Assert.Equal(entriesBefore, await ctx.Set<JournalEntry>().CountAsync());
            Assert.Equal(linesBefore, await ctx.Set<JournalEntryLine>().CountAsync());
            Assert.Equal(debitBefore, await ctx.Set<JournalEntryLine>().SumAsync(l => (decimal?)l.DebitAmount) ?? 0m);
            Assert.Equal(creditBefore, await ctx.Set<JournalEntryLine>().SumAsync(l => (decimal?)l.CreditAmount) ?? 0m);
        }
    }

    // ═════════════ 16) التقارير القائمة ما زالت تعمل (لا انحدار) ═════════════

    [LivePgFact]
    public async Task Existing_reports_still_work_after_phase_d()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        await using var ctx = _pg.NewContext();
        var reports = new ReportService(ctx);
        var pars = P(("from", DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd")),
                     ("to", DateTime.Today.ToString("yyyy-MM-dd")));

        foreach (var key in new[] { "journal-register", "general-ledger", "expenses-by-category", "sales-by-customer", "trial-balance" })
        {
            var table = await reports.GetReportAsync(key, pars);
            Assert.False(string.IsNullOrWhiteSpace(table.TitleAr));
            Assert.NotEmpty(table.Headers);
        }
    }

    // ═════════════ 17) تصدير Excel للتقارير الثمانية ببيانات حقيقية ═════════════

    [LivePgFact]
    public async Task Voucher_reports_export_to_excel_with_real_rows()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var number = await PostReceiptAsync(321m, "تصدير إكسل");

        await using var ctx = _pg.NewContext();
        var reports = new ReportService(ctx);
        var from = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.Today.ToString("yyyy-MM-dd");

        foreach (var key in new[]
        {
            "receipt-vouchers", "payment-vouchers", "receipt-voucher-entries", "payment-voucher-entries",
            "sales-invoice-entries", "sales-return-entries", "purchase-invoice-entries", "purchase-return-entries"
        })
        {
            var table = await reports.GetReportAsync(key, P(("from", from), ("to", to)));
            var bytes = reports.ExportReportExcel(table);

            Assert.True(bytes.Length > 1000, $"{key}: ملف Excel غير صالح الحجم");
            Assert.Equal((byte)'P', bytes[0]);
            Assert.Equal((byte)'K', bytes[1]);
        }

        // تحقق من المحتوى الفعلي: تقرير سندات القبض المُصدَّر يحمل رقم السند الحقيقي (قراءة الملف نفسه)
        var receiptTable = await reports.GetReportAsync("receipt-vouchers", P(("from", from), ("to", to), ("docNumber", number)));
        var xlsx = reports.ExportReportExcel(receiptTable);

        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx));
        var sheet = workbook.Worksheets.First();
        var cellText = string.Join(" | ", sheet.CellsUsed().Select(c => c.GetString()));

        Assert.Contains(number, cellText);
        Assert.Contains("321.00", cellText);
    }
}
