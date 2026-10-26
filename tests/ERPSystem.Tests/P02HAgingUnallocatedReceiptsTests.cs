using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P0-2H (المرحلة A) — اختبارات محدَّدة لتحسين عرض ذمم العملاء والمقبوضات بلا تخصيص موثق.
///
/// القواعد:
///   • قاعدة اختبار معزولة (PostgreSQL مؤقتة تُنشأ وتُحذف ذاتيًا) — لا إنتاج/لا VPS.
///   • كل الكتابات عبر الخدمات الرسمية؛ الاستثناء الوحيد: تغيير حقل «حالة» مستند اختبار
///     (فاتورة/مردود) عبر SQL لجعل الحالة «مسودة» وقياس الاستبعاد، ثم إعادته — كما في P0-2F.
///   • **لا تخصيص مقبوضات** ولا كيان تخصيص ولا Migration ولا تعديل مخطط.
///   • لم يُعدَّل شيء في `BankCheckService` ولا في أرصدة/قيود تاريخية (اختبار الانحدار يثبت ذلك).
/// </summary>
public class P02HAgingUnallocatedReceiptsTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P02HAgingUnallocatedReceiptsTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "p02h";
    }

    private static string Code(string tag) => tag + "-" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Guid> NewCustomerAsync(string tag)
    {
        await using var ctx = _pg.NewContext();
        var c = new Customer
        {
            Id = Guid.NewGuid(), Code = Code(tag), NameAr = "عميل P0-2H " + tag,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>فاتورة بيع آجلة مرحَّلة (بلا ضريبة) — تُعيد (العميل، الفاتورة، الإجمالي، الصنف).</summary>
    private async Task<(Guid CustomerId, Guid InvoiceId, decimal Total, Guid ItemId)> NewCreditInvoiceAsync(
        string tag, decimal quantity = 10m, decimal unitPrice = 10m, Guid? customerId = null, Guid? itemId = null)
    {
        await _pg.SeedCashAsync();
        var cust = customerId ?? await NewCustomerAsync(tag);
        // رصيد افتتاحي كبير ليسمح بمردودات متعددة في نفس العميل.
        var item = itemId ?? await _pg.CreateItemAsync(Code(tag + "-IT"), unitPrice, 5m, 100_000m, taxRate: 0m);

        await using var ctx = _pg.NewContext();
        var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = cust,
            WarehouseId = _pg.WarehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.OnAccount,
            PaymentMethod = (int)SalesPaymentMethod.OnAccount,
            TaxRate = 0m,
            Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = item, Quantity = quantity, UnitPrice = unitPrice } }
        });
        return (cust, inv.Id, inv.TotalAmount, item);
    }

    private async Task<Guid> NewPostedReceiptAsync(Guid customerId, decimal amount, DateTime? date = null)
    {
        await using var ctx = _pg.NewContext();
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = date ?? DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        await svc.ApproveAsync(draft.Id);
        await svc.PostAsync(draft.Id);
        return draft.Id;
    }

    private async Task<Guid> NewCheckAsync(Guid customerId, decimal amount, Guid? relatedInvoiceId = null,
        DateTime? date = null)
    {
        await using var ctx = _pg.NewContext();
        var created = await new BankCheckService(ctx, new JournalEntryService(ctx)).RegisterAsync(new CreateBankCheckDto
        {
            CheckNumber = Code("CHK"), BankName = "بنك اختبار P0-2H",
            Direction = BankCheckDirection.ReceivedFromCustomer,
            CustomerId = customerId, RelatedInvoiceId = relatedInvoiceId,
            Amount = amount,
            IssueDate = date ?? DateTime.Today,
            DueDate = (date ?? DateTime.Today).AddDays(30),
            ReceivedOrIssuedDate = date ?? DateTime.Today
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

    /// <summary>تغيير «حالة» مستند اختبار فقط (لا يمسّ قيودًا/أرصدة) — كما في P0-2F.</summary>
    private async Task SetInvoiceStatusAsync(Guid invoiceId, DocumentStatus status)
    {
        await using var ctx = _pg.NewContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SalesInvoices\" SET \"Status\" = {(int)status} WHERE \"Id\" = {invoiceId}");
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

    /// <summary>رصيد صف طرف في التقرير (الفئات + الرصيد الدائن غير المُخفى) — نفس تعريف P0-2A/P0-2F.</summary>
    private static decimal PartyValue(AgingReportDto dto, Guid customerId)
        => dto.Rows.Where(r => r.PartyId == customerId).Sum(r => r.Total + r.CreditBalance);

    private static AgingPartyRow? PartyRow(AgingReportDto dto, Guid customerId)
        => dto.Rows.FirstOrDefault(r => r.PartyId == customerId);

    /// <summary>رصيد حساب بالكود من سطور القيود (مدين − دائن) — قراءة فقط.</summary>
    private async Task<decimal> AccountNetAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.AccountId == accountId)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }

    /// <summary>لقطة قراءة فقط شاملة: القيود وسطورها + أرصدة كل الحسابات + أرصدة كل العملاء.</summary>
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
        sb.Append(";accounts=").Append(string.Join("|", accounts.OrderBy(a => a.Code)
            .Select(a => $"{a.Code}={a.CurrentBalance:F2}")));
        sb.Append(";customers=").Append(string.Join("|", customers.OrderBy(c => c.Id)
            .Select(c => $"{c.Id:N}={c.CurrentBalance:F2}")));
        return sb.ToString();
    }

    /// <summary>مُعترِض يعدّ أوامر القراءة المنفَّذة (لإثبات غياب N+1).</summary>
    private sealed class CountingReaderInterceptor : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Reads++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<int> CountAgingReadsAsync(DateTime asOf)
    {
        var interceptor = new CountingReaderInterceptor();
        await using var ctx = _pg.NewContextWithInterceptor(interceptor, retryOnFailure: false);
        await AgingReportQueries.GetAgingAsync(ctx, isReceivable: true, asOf: asOf);
        return interceptor.Reads;
    }

    private async Task<Guid> NewPostedReturnAsync(Guid invoiceId, Guid itemId, decimal quantity, decimal unitPrice)
    {
        await using var ctx = _pg.NewContext();
        var r = await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
        {
            SalesInvoiceId = invoiceId,
            WarehouseId = _pg.WarehouseId,
            ReturnDate = DateTime.Today,
            Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = itemId, Quantity = quantity, UnitPrice = unitPrice } }
        });
        return r.Id;
    }

    // ════════════════════ (1) فاتورة آجلة مرحَّلة بلا قبض ════════════════════
    [LivePgFact]
    public async Task Posted_on_account_invoice_without_receipt_is_fully_open()
    {
        if (!_pg.Available) return;
        var (customerId, _, total, _) = await NewCreditInvoiceAsync("P02H-T1");

        var dto = await AgingAsync(DateTime.Today);
        var row = PartyRow(dto, customerId);

        Assert.Equal(100m, total);
        Assert.NotNull(row);
        Assert.Equal(100m, row!.Total);
        Assert.True(row.IsComputedResidual, "يجب وسم الصف بأنه «متبقٍّ محسوب» لا رصيد نهائي");
        Assert.Equal(0m, row.UnallocatedReceipts);
        Assert.Equal(0m, row.UnallocatedChecks);
        Assert.Equal(0m, row.InvoiceLinkedChecks);
        Assert.Single(row.Invoices);
        Assert.Equal(100m, row.Invoices[0].Balance);
    }

    // ════════════════════ (2) فاتورة غير مرحَّلة لا تدخل تقرير الذمم ════════════════════
    [LivePgFact]
    public async Task Non_posted_invoice_is_excluded_from_AR_aging()
    {
        if (!_pg.Available) return;
        var (customerId, invoiceId, _, _) = await NewCreditInvoiceAsync("P02H-T2");
        Assert.Equal(100m, PartyValue(await AgingAsync(DateTime.Today), customerId));

        // حالة اختبارية فقط (لا تمسّ القيود/الأرصدة) ثم إعادتها كما في P0-2F.
        await SetInvoiceStatusAsync(invoiceId, DocumentStatus.Draft);
        var asDraft = PartyValue(await AgingAsync(DateTime.Today), customerId);
        await SetInvoiceStatusAsync(invoiceId, DocumentStatus.Posted);
        var restored = PartyValue(await AgingAsync(DateTime.Today), customerId);

        Assert.Equal(0m, asDraft);
        Assert.Equal(100m, restored);
    }

    // ════════════════════ (3) احترام تاريخ القطع asOf ════════════════════
    [LivePgFact]
    public async Task AR_aging_respects_asOf_cutoff_for_invoices_and_receipts()
    {
        if (!_pg.Available) return;
        var (customerId, _, _, _) = await NewCreditInvoiceAsync("P02H-T3");
        await NewPostedReceiptAsync(customerId, 25m);   // سند قبض بتاريخ اليوم

        var today = await AgingAsync(DateTime.Today);
        var yesterday = await AgingAsync(DateTime.Today.AddDays(-1));

        // الفاتورة: مشمولة عند asOf = تاريخ الفاتورة (حد شامل) ومستبعدة عند asOf = أمس.
        Assert.Equal(100m, PartyValue(today, customerId));
        Assert.Equal(0m, PartyValue(yesterday, customerId));

        // السند: حصته تظهر في تقرير اليوم وتغيب كليًا في تقرير أمس (تاريخ القطع على تاريخ السند).
        // قاعدة الاختبار معزولة وكل مستنداتها بتاريخ اليوم ⇒ لا يظهر أي سند في تقرير «أمس».
        Assert.Equal(25m, PartyRow(today, customerId)!.UnallocatedReceipts);
        Assert.Equal(0m, yesterday.UnallocatedReceiptsTotal);
        Assert.Equal(0m, yesterday.UnallocatedChecksTotal);
    }

    // ════════════════════ (4) فاتورة لها مردود مرحَّل مرتبط بها ════════════════════
    [LivePgFact]
    public async Task Posted_linked_return_reduces_the_computed_residual_once()
    {
        if (!_pg.Available) return;
        var (customerId, invoiceId, _, itemId) = await NewCreditInvoiceAsync("P02H-T4");
        var returnId = await NewPostedReturnAsync(invoiceId, itemId, 2m, 10m);   // مردود 20

        var dto = await AgingAsync(DateTime.Today);
        var row = PartyRow(dto, customerId)!;

        Assert.NotEqual(Guid.Empty, returnId);
        Assert.Equal(80m, row.Total);
        Assert.Single(row.Invoices);
        Assert.Equal(80m, row.Invoices[0].Balance);
        // المردود مرتبط بالفاتورة ⇒ لا يظهر في بند المقبوضات بلا تخصيص موثق.
        Assert.Equal(0m, row.UnallocatedReceipts);
    }

    // ════════════════════ (5) استبعاد المردود غير المرحَّل ════════════════════
    [LivePgFact]
    public async Task Draft_return_is_not_deducted_from_the_residual()
    {
        if (!_pg.Available) return;
        var (customerId, invoiceId, _, itemId) = await NewCreditInvoiceAsync("P02H-T5");
        var returnId = await NewPostedReturnAsync(invoiceId, itemId, 2m, 10m);
        Assert.Equal(80m, PartyValue(await AgingAsync(DateTime.Today), customerId));

        // حالة اختبارية فقط: تحويل المردود إلى «مسودة» ⇒ يجب ألا يُخصم.
        await SetReturnStatusAsync(returnId, DocumentStatus.Draft);
        var asDraft = PartyValue(await AgingAsync(DateTime.Today), customerId);
        await SetReturnStatusAsync(returnId, DocumentStatus.Posted);
        var restored = PartyValue(await AgingAsync(DateTime.Today), customerId);

        Assert.Equal(100m, asDraft);
        Assert.Equal(80m, restored);
    }

    // ════════════════════ (6) سند قبض بلا تخصيص موثق ════════════════════
    [LivePgFact]
    public async Task Receipt_without_documented_allocation_is_shown_as_independent_item()
    {
        if (!_pg.Available) return;
        var (customerId, _, _, _) = await NewCreditInvoiceAsync("P02H-T6");

        var before = (await AgingAsync(DateTime.Today)).UnallocatedReceiptsTotal;
        await NewPostedReceiptAsync(customerId, 20m);
        var dto = await AgingAsync(DateTime.Today);
        var row = PartyRow(dto, customerId)!;

        // البند مستقل: مبلغه 20، ورصيد الفاتورة يبقى 100 (لا خصم).
        Assert.Equal(20m, row.UnallocatedReceipts);
        Assert.Equal(100m, row.Total);
        Assert.Equal(100m, row.Invoices.Single().Balance);
        Assert.Equal(20m, dto.UnallocatedReceiptsTotal - before);
        Assert.Equal(0m, row.UnallocatedChecks);
    }

    // ════════════════════ (7) لا خصم لقبض من فاتورة بلا علاقة موثقة ════════════════════
    [LivePgFact]
    public async Task Receipt_is_not_deducted_from_any_invoice_without_documented_relation()
    {
        if (!_pg.Available) return;
        var (customerId, _, _, itemId) = await NewCreditInvoiceAsync("P02H-T7", 10m, 10m);           // 100
        var (_, _, _, _) = await NewCreditInvoiceAsync("P02H-T7b", 5m, 10m, customerId, itemId);      // 50

        await NewPostedReceiptAsync(customerId, 120m);

        var dto = await AgingAsync(DateTime.Today);
        var row = PartyRow(dto, customerId)!;

        // الفاتورتان بلا علاقة تخصيص موثقة ⇒ لا تُخصم منهما أي حصة، والأقدم لا يُستنزف أولًا.
        Assert.Equal(2, row.Invoices.Count);
        Assert.Equal(100m, row.Invoices.Single(i => i.Balance == 100m).Balance);
        Assert.Equal(50m, row.Invoices.Single(i => i.Balance == 50m).Balance);
        Assert.Equal(150m, row.Total);
        Assert.Equal(120m, row.UnallocatedReceipts);
    }

    // ════════════════════ (8) الشيك تحت التحصيل لا يُحتسب مرتين ════════════════════
    [LivePgFact]
    public async Task Check_under_collection_is_counted_exactly_once_and_not_added_to_AR()
    {
        if (!_pg.Available) return;
        var (customerId, invoiceId, _, _) = await NewCreditInvoiceAsync("P02H-T8");

        var start = await AgingAsync(DateTime.Today);
        var baseUnallocatedChecks = start.UnallocatedChecksTotal;
        var base1102 = await AccountNetAsync("1102");

        // (أ) شيك 30 بلا رابط فاتورة: مسجَّل → مودع → محصَّل
        var checkId = await NewCheckAsync(customerId, 30m);

        var afterRegister = await AgingAsync(DateTime.Today);
        var registerRow = PartyRow(afterRegister, customerId)!;
        Assert.Equal(30m, registerRow.UnallocatedChecks);
        Assert.Equal(30m, afterRegister.UnallocatedChecksTotal - baseUnallocatedChecks);
        Assert.Equal(100m, registerRow.Total);                                  // لا خصم من الفاتورة
        Assert.Equal(base1102 + 30m, await AccountNetAsync("1102"));            // الأثر المحاسبي في 1102

        await ChangeCheckStateAsync(checkId, "deposit");
        var afterDeposit = await AgingAsync(DateTime.Today);
        await ChangeCheckStateAsync(checkId, "clear");
        var afterClear = await AgingAsync(DateTime.Today);

        // تغييرات الحالة لا تُضاعف الاحتساب (الإيداع بلا قيد · التحصيل ينقل 1102→1101 فقط)
        Assert.Equal(30m, afterDeposit.UnallocatedChecksTotal - baseUnallocatedChecks);
        Assert.Equal(30m, afterClear.UnallocatedChecksTotal - baseUnallocatedChecks);
        Assert.Equal(100m, PartyRow(afterClear, customerId)!.Total);

        // (ب) «تحت التحصيل» (1102) لا يُضاف إلى أرصدة الفواتير ولا إلى البند المستقل عند التحصيل
        Assert.Equal(base1102, await AccountNetAsync("1102"));                  // 1102 عاد إلى قيمته بعد التحصيل
        Assert.Equal(100m, PartyRow(afterClear, customerId)!.Total);

        // (ج) شيك 40 مرتبط بفاتورة موثقة: يُعرض منفصلًا ولا يُخصم ولا يدخل «بلا تخصيص موثق»
        await NewCheckAsync(customerId, 40m, relatedInvoiceId: invoiceId);
        var afterLinked = await AgingAsync(DateTime.Today);
        var linkedRow = PartyRow(afterLinked, customerId)!;
        Assert.Equal(40m, linkedRow.InvoiceLinkedChecks);
        Assert.Equal(30m, linkedRow.UnallocatedChecks);
        Assert.Equal(100m, linkedRow.Total);                                    // لا خصم في المرحلة A
        Assert.Equal(30m, afterLinked.UnallocatedChecksTotal - baseUnallocatedChecks);

        // (د) شيك 50 مرتجع: أثره على 1200 معكوس ⇒ مستبعد من كل البنود
        var bounced = await NewCheckAsync(customerId, 50m);
        await ChangeCheckStateAsync(bounced, "bounce");
        var afterBounce = await AgingAsync(DateTime.Today);
        var bounceRow = PartyRow(afterBounce, customerId)!;
        Assert.Equal(30m, bounceRow.UnallocatedChecks);
        Assert.Equal(40m, bounceRow.InvoiceLinkedChecks);
        Assert.Equal(100m, bounceRow.Total);
    }

    // ════════════════════ (9) تشغيل التقارير لا يغيّر القيود أو الأرصدة ════════════════════
    [LivePgFact]
    public async Task Running_aging_reports_does_not_change_ledger_or_party_balances()
    {
        if (!_pg.Available) return;
        var (customerId, invoiceId, _, itemId) = await NewCreditInvoiceAsync("P02H-T9");
        await NewPostedReturnAsync(invoiceId, itemId, 1m, 10m);
        await NewPostedReceiptAsync(customerId, 15m);
        await NewCheckAsync(customerId, 5m, relatedInvoiceId: invoiceId);

        var before = await LedgerSnapshotAsync();

        // تشغيل تقارير الأعمار (عملاء/موردين) عدة مرات وعلى تواريخ مختلفة.
        for (var i = 0; i < 3; i++)
        {
            await AgingAsync(DateTime.Today);
            await AgingAsync(DateTime.Today.AddDays(-1));
            await AgingAsync(DateTime.Today, receivable: false);
        }

        var after = await LedgerSnapshotAsync();

        Assert.Contains("entries=", before);
        Assert.Contains(";customers=", before);
        Assert.Equal(before, after);
    }

    // ════════════════════ (10) عدم تراجع شروط P0-2F ════════════════════
    [LivePgFact]
    public async Task P0_2F_invariants_still_hold_posted_only_and_asOf()
    {
        if (!_pg.Available) return;
        var (customerId, invoiceId, _, _) = await NewCreditInvoiceAsync("P02H-T10");

        // (أ) تاريخ القطع: حد شامل عند تاريخ الفاتورة، وغياب تام قبله.
        Assert.Equal(100m, PartyValue(await AgingAsync(DateTime.Today), customerId));
        Assert.Equal(0m, PartyValue(await AgingAsync(DateTime.Today.AddDays(-1)), customerId));

        // (ب) الفواتير المرحَّلة فقط.
        await SetInvoiceStatusAsync(invoiceId, DocumentStatus.Draft);
        var asDraft = PartyValue(await AgingAsync(DateTime.Today), customerId);
        await SetInvoiceStatusAsync(invoiceId, DocumentStatus.Posted);

        // (ج) فرع الموردين لم يتأثر (بلا فواتير موردين هنا ⇒ صفر بلا استثناء ولا تسريب من فرع العملاء).
        var ap = await AgingAsync(DateTime.Today, receivable: false);

        Assert.Equal(0m, asDraft);
        Assert.Equal(100m, PartyValue(await AgingAsync(DateTime.Today), customerId));
        Assert.Equal(0m, ap.UnallocatedReceiptsTotal + ap.UnallocatedChecksTotal + ap.InvoiceLinkedChecksTotal);
    }

    // ════════════════════ (11) الأداء: لا N+1 (عدد القراءات ثابت) ════════════════════
    [LivePgFact]
    public async Task AR_aging_issues_a_constant_number_of_reads_regardless_of_document_count()
    {
        if (!_pg.Available) return;
        var (customerId, _, _, itemId) = await NewCreditInvoiceAsync("P02H-T11");
        await NewPostedReceiptAsync(customerId, 5m);
        await NewCheckAsync(customerId, 5m);

        var readsWithFewDocuments = await CountAgingReadsAsync(DateTime.Today);

        // أضِف 20 فاتورة + 20 مردودًا لنفس العميل ⇒ يجب ألا يتغير عدد أوامر القراءة.
        for (var i = 0; i < 20; i++)
        {
            var (_, invId, _, _) = await NewCreditInvoiceAsync("P02H-T11x", 4m, 10m, customerId, itemId);
            await NewPostedReturnAsync(invId, itemId, 1m, 10m);
        }

        var readsWithManyDocuments = await CountAgingReadsAsync(DateTime.Today);

        await File.WriteAllTextAsync(@"D:\erpfix-pu01\_p02h_reads.txt",
            $"# P0-2H (المرحلة A) — قياس عدد أوامر القراءة لتقرير أعمار العملاء{Environment.NewLine}" +
            $"قبل الإضافة (فاتورة/مردود واحد + سند + شيك): {readsWithFewDocuments}{Environment.NewLine}" +
            $"بعد 20 فاتورة و20 مردودًا لنفس العميل: {readsWithManyDocuments}{Environment.NewLine}",
            Encoding.UTF8);

        Assert.True(readsWithFewDocuments > 0, "يجب أن يُنفَّذ استعلام واحد على الأقل");
        Assert.Equal(readsWithFewDocuments, readsWithManyDocuments);
    }

    // ════════════════════ (12) دليل مقيس: سيناريو مضبوط + المعادلة القابلة للإثبات ════════════════════
    [LivePgFact]
    public async Task Phase_A_evidence_scenario_writes_measurable_log()
    {
        if (!_pg.Available) return;

        // سيناريو مضبوط لعميل واحد: فاتورة 100 · مردود 20 · سند قبض 20
        //  · شيك 30 بلا رابط فاتورة (مسجَّل→محصَّل) · شيك 10 مرتبط بفاتورة · شيك 50 بلا رابط (→مرتجع)
        var arBefore = await AccountNetAsync("1200");
        var checks1102Before = await AccountNetAsync("1102");

        var (customerId, invoiceId, _, itemId) = await NewCreditInvoiceAsync("P02H-LOG", 10m, 10m);

        var returnId = await NewPostedReturnAsync(invoiceId, itemId, 2m, 10m);           // 20
        await NewPostedReceiptAsync(customerId, 20m);
        var unlinked = await NewCheckAsync(customerId, 30m);
        await ChangeCheckStateAsync(unlinked, "clear");
        await NewCheckAsync(customerId, 10m, relatedInvoiceId: invoiceId);
        var bounced = await NewCheckAsync(customerId, 50m);
        await ChangeCheckStateAsync(bounced, "bounce");

        var dto = await AgingAsync(DateTime.Today);
        var row = PartyRow(dto, customerId)!;
        var arAfter = await AccountNetAsync("1200");
        var checks1102After = await AccountNetAsync("1102");

        var arDelta = arAfter - arBefore;                                   // أثر هذا السيناريو على 1200 فقط
        var provenIdentity = row.Total - row.UnallocatedReceipts - row.UnallocatedChecks - row.InvoiceLinkedChecks;

        var log = new StringBuilder();
        log.AppendLine("# P0-2H (المرحلة A) — دليل مقيس (سيناريو واحد، قاعدة اختبار معزولة)");
        log.AppendLine($"- وقت التشغيل (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"- مردود مرحَّل مرتبط بالفاتورة: {returnId:N} بمبلغ 20.00");
        log.AppendLine();
        log.AppendLine("## مخرَج تقرير أعمار الذمم للعميل (AR)");
        log.AppendLine($"- المتبقي المحسوب (الفئات + الرصيد الدائن) = {row.Total + row.CreditBalance:F2}  (الفاتورة 100 − المردود 20 = 80)");
        log.AppendLine($"- مقبوضات بلا تخصيص موثق (سندات قبض) = {row.UnallocatedReceipts:F2}");
        log.AppendLine($"- شيكات بلا رابط فاتورة (غير مرتجعة) = {row.UnallocatedChecks:F2}");
        log.AppendLine($"- شيكات مرتبطة بفاتورة (معروضة ولا تُخصم) = {row.InvoiceLinkedChecks:F2}");
        log.AppendLine($"- الرصيد الدائن/المطالبة = {row.CreditBalance:F2} · عدد الفواتير المفتوحة = {row.Invoices.Count}");
        log.AppendLine();
        log.AppendLine("## الأثر على الأستاذ العام (مقاسًا من سطور القيود، لا من الرصيد المخزَّن)");
        log.AppendLine($"- Δ1200 (أثر السيناريو) = {arDelta:F2}");
        log.AppendLine($"- 1102 (شيكات برسم التحصيل) قبل/بعد = {checks1102Before:F2} / {checks1102After:F2}");
        log.AppendLine();
        log.AppendLine("## المعادلة القابلة للإثبات في هذا السيناريو المضبوط فقط");
        log.AppendLine("   Δ1200 = المتبقي المحسوب − مقبوضات بلا تخصيص موثق − شيكات مرتبطة بفاتورة (غير مخصومة)");
        log.AppendLine($"   {arDelta:F2} = {row.Total:F2} − {row.UnallocatedReceipts:F2} − {row.UnallocatedChecks:F2} − {row.InvoiceLinkedChecks:F2} = {provenIdentity:F2}");

        await File.WriteAllTextAsync(@"D:\erpfix-pu01\_p02h_phaseA_log.txt", log.ToString(), Encoding.UTF8);

        Assert.Equal(80m, row.Total + row.CreditBalance);
        Assert.Equal(20m, row.UnallocatedReceipts);
        Assert.Equal(30m, row.UnallocatedChecks);
        Assert.Equal(10m, row.InvoiceLinkedChecks);
        Assert.Equal(20m, arDelta);
        Assert.Equal(arDelta, provenIdentity);
    }
}
