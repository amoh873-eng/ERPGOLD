using System.Data.Common;
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
/// اختبارات سلامة أرصدة الأطراف تحت التزامن (إصلاح 2026-10-23).
///
/// العيب المكتشف قبل الإصلاح: الرصيد التشغيلي المكرَّر (<c>Customer.CurrentBalance</c>) كان
/// يُقرأ ثم يُكتب من الذاكرة **بلا قفل صف ولا معاملة** في مسارات: سند القبض، البيع، مردود البيع،
/// والشيكات (جانب العميل) — بينما جانب المورد محمي بقفل <c>FOR UPDATE</c> (N-5). ورمز التزامن
/// <c>RowVersion</c> على PostgreSQL عمود <c>bytea</c> لا يولّد الخادم قيمته ⇒ لا يكشف التعارض.
/// النتيجة: عند عمليتين متزامنتين على نفس العميل يضيع أحد الأثرين (lost update).
/// </summary>
public class PartyBalanceConcurrencyTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public PartyBalanceConcurrencyTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "party-concurrency";
    }

    private static ReceiptVoucherService Receipts(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAll());

    private static SalesInvoiceService Sales(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice());

    // ───────────────────────── بُناة البيانات ─────────────────────────

    /// <summary>عميل مخصَّص للاختبار برصيد ابتدائي معروف (تهيئة اختبار محدَّدة القيم).</summary>
    private async Task<Guid> NewCustomerAsync(string code, decimal initialBalance)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "عميل تزامن " + code,
            IsActive = true,
            CurrentBalance = initialBalance,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<decimal> CustomerBalanceAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    /// <summary>إجمالي قيود سندات القبض المرحّلة (يُقاس كفرق قبل/بعد لتفادي أثر اختبارات أخرى).</summary>
    private async Task<int> ReceiptEntryTotalAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().AsNoTracking()
            .CountAsync(e => !e.IsDeleted && e.EntryType == JournalEntryType.ReceiptVoucher);
    }

    /// <summary>الرصيد **المخزَّن** في عمود الحساب (الذي يحدّثه محرّك القيود فورًا قبل SaveChanges).</summary>
    private async Task<decimal> StoredAccountBalanceAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.CurrentBalance).FirstAsync();
    }

    /// <summary>مُعترِض يفشل عند حفظ سجل التدقيق — أي **بعد** أن يكون محرّك القيود قد حدّث أرصدة الحسابات.</summary>
    private sealed class FailOnAuditInsertInterceptor : DbCommandInterceptor
    {
        private static bool IsAuditInsert(DbCommand command)
        {
            var sql = command.CommandText ?? string.Empty;
            return sql.Contains("AuditTrails", StringComparison.OrdinalIgnoreCase)
                   && sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);
        }

        // Npgsql ينفّذ INSERT مع RETURNING كـReader، لذا يُعترَض المساران معًا.
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsAuditInsert(command))
                throw new InvalidOperationException("فشل مصطنع بعد تحديث أرصدة الحسابات (اختبار ذرّية)");

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsAuditInsert(command))
                throw new InvalidOperationException("فشل مصطنع بعد تحديث أرصدة الحسابات (اختبار ذرّية)");

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // ═══════════ 4) ذرّية ترحيل سند القبض: لا أثر جزئي على أرصدة الحسابات ═══════════
    /// <summary>
    /// العيب المؤكد قبل الإصلاح: `ReceiptVoucherService.PostAsync` كان **بلا معاملة صريحة**،
    /// ومحرّك القيود المركزي يحدّث أرصدة الحسابات بـ`UPDATE … RETURNING` فورًا قبل `SaveChanges`،
    /// فإذا فشل الحفظ (هنا: فشل مصطنع عند حفظ سجل التدقيق) تبقى **أرصدة الحسابات متحركة بلا سند
    /// مُرحَّل ولا قيد** (أثر جزئي). الإصلاح: لفّ الترحيل في معاملة واحدة ⇒ تراجع كامل.
    /// </summary>
    [LivePgFact]
    public async Task Receipt_post_failure_rolls_back_account_balances_completely()
    {
        if (!_pg.Available) return;
        var customerId = await NewCustomerAsync("CC-ATOM", 500m);
        var voucherId = await NewApprovedReceiptAsync(customerId, 100m);

        var storedCashBefore = await StoredAccountBalanceAsync("1100");
        var storedArBefore = await StoredAccountBalanceAsync("1200");
        var linesCashBefore = await AccountNetAsync("1100");
        var linesArBefore = await AccountNetAsync("1200");
        var balanceBefore = await CustomerBalanceAsync(customerId);
        var entriesBefore = await ReceiptEntryTotalAsync();

        await using (var ctx = _pg.NewContextWithInterceptor(new FailOnAuditInsertInterceptor(), retryOnFailure: false))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Receipts(ctx).PostAsync(voucherId));
        }

        // (أ) أرصدة الحسابات المخزَّنة لم تتغيّر ⇒ لا أثر جزئي
        Assert.Equal(storedCashBefore, await StoredAccountBalanceAsync("1100"));
        Assert.Equal(storedArBefore, await StoredAccountBalanceAsync("1200"));
        // (ب) ولا قيد ولا خط قيد
        Assert.Equal(linesCashBefore, await AccountNetAsync("1100"));
        Assert.Equal(linesArBefore, await AccountNetAsync("1200"));
        Assert.Equal(entriesBefore, await ReceiptEntryTotalAsync());
        // (ج) ولا تغيّر رصيد العميل، والسند ما زال معتمدًا بلا قيد
        Assert.Equal(balanceBefore, await CustomerBalanceAsync(customerId));
        await using var check = _pg.NewContext();
        var voucher = await check.Set<ReceiptVoucher>().AsNoTracking().FirstAsync(v => v.Id == voucherId);
        Assert.Null(voucher.JournalEntryId);
        Assert.Equal(VoucherStatus.Approved, voucher.Status);
    }

    /// <summary>صافي حركة حساب (مدين − دائن) من القيود المرحّلة.</summary>
    private async Task<decimal> AccountNetAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.AccountId == accountId)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }

    private async Task<Guid> NewApprovedReceiptAsync(Guid customerId, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        var svc = Receipts(ctx);
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId,
            VoucherDate = DateTime.Today,
            PaymentMethod = 1,   // نقدي
            Amount = amount,
            Description = "اختبار تزامن أرصدة العملاء"
        });
        await svc.ApproveAsync(draft.Id);
        return draft.Id;
    }

    private static CreateSalesInvoiceDto NewCashSale(Guid customerId, Guid warehouseId, Guid itemId, decimal qty)
        => new()
        {
            CustomerId = customerId,
            WarehouseId = warehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.Cash,
            PaymentMethod = (int)SalesPaymentMethod.Cash,
            TaxRate = 16m,
            Lines = new List<CreateSalesInvoiceLineDto>
            {
                new() { ItemId = itemId, Quantity = qty, UnitPrice = 15m }
            }
        };

    /// <summary>فاتورة بيع **آجلة** (تُنشئ ذمة على العميل ⇒ تُختبر سلامة الرصيد التشغيلي تحت التزامن).</summary>
    private static CreateSalesInvoiceDto NewOnAccountSale(Guid customerId, Guid warehouseId, Guid itemId, decimal qty)
        => new()
        {
            CustomerId = customerId,
            WarehouseId = warehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.OnAccount,
            PaymentMethod = (int)SalesPaymentMethod.OnAccount,
            TaxRate = 16m,
            Lines = new List<CreateSalesInvoiceLineDto>
            {
                new() { ItemId = itemId, Quantity = qty, UnitPrice = 15m }
            }
        };

    // ═══════════ 1) سندان قبض متزامنان لنفس العميل ⇒ لا يضيع أي أثر ═══════════
    /// <summary>يُعاد السيناريو دورات متعددة (نمط اختبارات التزامن القائمة) لأن نافذة السباق ضيقة.</summary>
    [LivePgFact]
    public async Task Concurrent_receipts_on_same_customer_apply_both_without_lost_update()
    {
        if (!_pg.Available) return;
        const int iterations = 10;

        for (var i = 1; i <= iterations; i++)
        {
            var customerId = await NewCustomerAsync("CC-RCV", 500m);
            var v1 = await NewApprovedReceiptAsync(customerId, 100m);
            var v2 = await NewApprovedReceiptAsync(customerId, 100m);

            var entriesBefore = await ReceiptEntryTotalAsync();
            var arBefore = await AccountNetAsync("1200");

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new[] { v1, v2 }.Select(async id =>
            {
                await start.Task;                        // حاجز: بدء متزامن حقيقي
                await using var ctx = _pg.NewContext();  // سياق/اتصال مستقل
                await Receipts(ctx).PostAsync(id);
            }).ToList();

            start.SetResult();
            await Task.WhenAll(tasks);

            // الرصيد التشغيلي: 500 − 100 − 100 = 300 (لا 400 كما يحدث عند فقدان أحد الأثرين)
            Assert.Equal(300m, await CustomerBalanceAsync(customerId));

            // دفتر الأستاذ: قيدان (واحد لكل سند) وصافي دائن 200 على ذمم العملاء
            Assert.Equal(entriesBefore + 2, await ReceiptEntryTotalAsync());
            Assert.Equal(arBefore - 200m, await AccountNetAsync("1200"));
        }
    }

    // ═══════════ 2) فاتورتا بيع **آجل** متزامنتان لنفس العميل ⇒ لا يضيع أي أثر ═══════════
    // (SUB-1: البيع الآجل وحده يحرّك رصيد العميل ⇒ هو المسار الصحيح لاختبار lost update.)
    [LivePgFact]
    public async Task Concurrent_credit_sales_on_same_customer_apply_both_without_lost_update()
    {
        if (!_pg.Available) return;
        const int iterations = 5;
        var itemId = await _pg.CreateItemAsync("PC-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 1000m, taxRate: 16m);

        for (var i = 1; i <= iterations; i++)
        {
            var customerId = await NewCustomerAsync("CC-SAL", 0m);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 2).Select(async _ =>
            {
                await start.Task;
                await using var ctx = _pg.NewContext();
                return await Sales(ctx).CreateAsync(NewOnAccountSale(customerId, _pg.WarehouseId, itemId, 5m));
            }).ToList();

            start.SetResult();
            var created = await Task.WhenAll(tasks);

            var expected = created.Sum(c => c.TotalAmount);
            Assert.Equal(2, created.Select(c => c.InvoiceNumber).Distinct().Count());
            Assert.Equal(expected, await CustomerBalanceAsync(customerId));   // مجموع الأثرين لا واحدًا منهما
        }
    }

    // ═══════════ 3) رصد (لا إصلاح): انحراف رصيد العميل عن الأستاذ في البيع النقدي ═══════════
    /// <summary>
    /// **إثبات إصلاح SUB-1 (سياسة الذمم 2026-10-24)** — كان البيع النقدي يُدين الصندوق/الدرج
    /// (1100/1105) ولا يمس ذمم العملاء (1200) بينما يزيد `Customer.CurrentBalance` بالكامل (انحراف).
    /// بعد الإصلاح: الرصيد التشغيلي للعميل لا يتغير في البيع النقدي، ويطابق 1200.
    /// </summary>
    [LivePgFact]
    public async Task Cash_sale_customer_balance_vs_ar_ledger_probe()
    {
        if (!_pg.Available) return;
        var customerId = await NewCustomerAsync("CC-PRB", 0m);
        var itemId = await _pg.CreateItemAsync("PP-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 100m, taxRate: 16m);

        var arBefore = await AccountNetAsync("1200");
        var cashBefore = await AccountNetAsync("1100");

        await using (var ctx = _pg.NewContext())
            await Sales(ctx).CreateAsync(NewCashSale(customerId, _pg.WarehouseId, itemId, 5m));

        var total = 5m * 15m * 1.16m;                       // 87.00
        var customerDelta = await CustomerBalanceAsync(customerId);
        var arDelta = (await AccountNetAsync("1200")) - arBefore;
        var cashDelta = (await AccountNetAsync("1100")) - cashBefore;

        // بعد السياسة (SUB-1): البيع النقدي لا يُنشئ ذمة ⇒ رصيد العميل ثابت، وذمم العملاء ثابتة، والصندوق زاد.
        Assert.Equal(0m, customerDelta);
        Assert.Equal(0m, arDelta);
        Assert.Equal(MoneyRounding.Round(total), cashDelta);
    }
}
