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
/// الدفعة D — دورة حياة السند تحت التزامن (إصلاح 2026-10-23).
///
/// الثابت المُختبَر: **لا يجوز أن يوجد سند حالته «ملغى» وله قيد مرحَّل** (ولا سند مُرحَّل
/// كُتب فوق إلغائه). السباق: طلب إلغاء يقرأ الحالة `Approved` قبل التزام طلب الترحيل،
/// ثم يكتب الإلغاء بعد التزامه ⇒ يُطمس الترحيل أو يُلغى سند له أثر محاسبي.
/// التوقيت هنا **حتمي** عبر اعتراض أمر `UPDATE` على جدول السند.
/// </summary>
public class VoucherLifecycleConcurrencyTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public VoucherLifecycleConcurrencyTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "lifecycle-concurrency";
    }

    private static ReceiptVoucherService Receipts(AppDbContext ctx) => new(ctx, new JournalEntryService(ctx), new AllowAll());
    private static PaymentVoucherService Payments(AppDbContext ctx) => new(ctx, new JournalEntryService(ctx), new AllowAll());

    /// <summary>يعترض أول أمر يطابق نصًّا معيّنًا ويعلّقه حتى يُفرَج عنه — لتثبيت التوقيت.</summary>
    private sealed class GateInterceptor : DbCommandInterceptor
    {
        private readonly string _match;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GateInterceptor(string match) => _match = match;

        public Task Reached => _reached.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await GateAsync(command, cancellationToken);
            return result;
        }

        // UPDATE مع `RETURNING` (لأن الكيانات تحمل عمود RowVersion) يُنفَّذ عبر ExecuteReader لا ExecuteNonQuery.
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await GateAsync(command, cancellationToken);
            return result;
        }

        private async Task GateAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (command.CommandText.Contains(_match, StringComparison.Ordinal))
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
    }

    private async Task<Guid> NewCustomerAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "عميل سباق " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<Guid> NewApprovedReceiptVoucherAsync(Guid customerId, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        var svc = Receipts(ctx);
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        await svc.ApproveAsync(draft.Id);
        return draft.Id;
    }

    private async Task<Guid> NewApprovedPaymentVoucherAsync(Guid supplierId, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        var svc = Payments(ctx);
        var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = supplierId, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        await svc.ApproveAsync(draft.Id);
        return draft.Id;
    }

    // ═══════════ 1) سباق الإلغاء مع الترحيل — سند قبض ═══════════
    [LivePgFact]
    public async Task Receipt_voucher_cancel_racing_post_must_not_leave_cancelled_voucher_with_entry()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("RACE-RV");
        var voucherId = await NewApprovedReceiptVoucherAsync(customer, 10m);

        var gate = new GateInterceptor("UPDATE \"ReceiptVouchers\"");
        await using var postCtx = _pg.NewContext();
        await using var cancelCtx = _pg.NewContextWithInterceptor(gate, retryOnFailure: false);

        // (1) الإلغاء يقرأ «معتمد» ويتوقّف قبل كتابة الحالة
        var cancelTask = Receipts(cancelCtx).CancelAsync(voucherId, "سباق اختباري");
        var gated = await WaitForAsync(gate.Reached, TimeSpan.FromSeconds(20));
        Assert.True(gated, "لم يصل الإلغاء إلى مرحلة الكتابة — تغيّر في المسار يستوجب مراجعة الاختبار.");

        // (2) الترحيل يُشغَّل بعد أن قرّر الإلغاء: قد يكتمل أولًا (بلا قفل) أو يُحجب (مع قفل الصف)
        var postTask = Receipts(postCtx).PostAsync(voucherId);
        await Task.Delay(400);
        gate.Release();

        var cancelError = await Record.ExceptionAsync(() => cancelTask);
        var postError = await Record.ExceptionAsync(() => postTask);

        var state = await ReadReceiptStateAsync(voucherId);
        AssertLifecycleInvariant(state.Status, state.EntryId, state.EntriesInDb,
            $"سند قبض | gated={gated} | cancelError={Describe(cancelError)} | postError={Describe(postError)}");

        // لا يمرّ الطلبان معًا بنتيجتين متعارضتين، والخاسر يُرفض برسالة واضحة
        Assert.True(cancelError is not null || postError is not null,
            $"مرّ الإلغاء والترحيل كلاهما معًا: status={state.Status}");
        if (state.Status == VoucherStatus.Cancelled)
            Assert.IsType<InvalidOperationException>(postError);   // الإلغاء فاز ⇒ رُفض الترحيل
        else
            Assert.IsType<InvalidOperationException>(cancelError); // الترحيل فاز ⇒ رُفض الإلغاء
    }

    // ═══════════ 2) سباق الإلغاء مع الترحيل — سند صرف ═══════════
    [LivePgFact]
    public async Task Payment_voucher_cancel_racing_post_must_not_leave_cancelled_voucher_with_entry()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("RACE-PV");
        var voucherId = await NewApprovedPaymentVoucherAsync(supplier, 10m);

        var gate = new GateInterceptor("UPDATE \"PaymentVouchers\"");
        await using var postCtx = _pg.NewContext();
        await using var cancelCtx = _pg.NewContextWithInterceptor(gate, retryOnFailure: false);

        var cancelTask = Payments(cancelCtx).CancelAsync(voucherId, "سباق اختباري");
        var gated = await WaitForAsync(gate.Reached, TimeSpan.FromSeconds(20));
        Assert.True(gated, "لم يصل الإلغاء إلى مرحلة الكتابة — تغيّر في المسار يستوجب مراجعة الاختبار.");

        var postTask = Payments(postCtx).PostAsync(voucherId, null, null);
        await Task.Delay(400);
        gate.Release();

        var cancelError = await Record.ExceptionAsync(() => cancelTask);
        var postError = await Record.ExceptionAsync(() => postTask);

        var state = await ReadPaymentStateAsync(voucherId);
        AssertLifecycleInvariant(state.Status, state.EntryId, state.EntriesInDb, "سند صرف");

        Assert.True(cancelError is not null || postError is not null,
            $"مرّ الإلغاء والترحيل كلاهما معًا: status={state.Status}");
        if (state.Status == VoucherStatus.Cancelled)
            Assert.IsType<InvalidOperationException>(postError);   // الإلغاء فاز ⇒ رُفض الترحيل
        else
            Assert.IsType<InvalidOperationException>(cancelError); // الترحيل فاز ⇒ رُفض الإلغاء
    }

    // ═══════════ 3) ترحيل مزدوج متزامن — قيد واحد فقط ═══════════
    [LivePgFact]
    public async Task Concurrent_double_post_of_receipt_voucher_creates_single_entry()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("DUP-RV");
        var voucherId = await NewApprovedReceiptVoucherAsync(customer, 12m);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            await start.Task;
            await using var ctx = _pg.NewContext();
            return await Record.ExceptionAsync(() => Receipts(ctx).PostAsync(voucherId));
        }).ToList();

        start.SetResult();
        var errors = await Task.WhenAll(tasks);

        var state = await ReadReceiptStateAsync(voucherId);
        AssertLifecycleInvariant(state.Status, state.EntryId, state.EntriesInDb, "سند قبض (ترحيل مزدوج)");
        Assert.Equal(VoucherStatus.Posted, state.Status);
        Assert.Equal(1, state.EntriesInDb);
        Assert.All(errors, e => Assert.True(e is null or InvalidOperationException,
            "خطأ غير متوقع في ترحيل متزامن: " + e?.GetType().Name));
    }

    private async Task<(VoucherStatus Status, Guid? EntryId, int EntriesInDb)> ReadReceiptStateAsync(Guid id)
    {
        await using var ctx = _pg.NewContext();
        var v = await ctx.Set<ReceiptVoucher>().AsNoTracking().FirstAsync(x => x.Id == id);
        var entries = v.JournalEntryId is null ? 0
            : await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(e => e.Id == v.JournalEntryId);
        return (v.Status, v.JournalEntryId, entries);
    }

    private async Task<(VoucherStatus Status, Guid? EntryId, int EntriesInDb)> ReadPaymentStateAsync(Guid id)
    {
        await using var ctx = _pg.NewContext();
        var v = await ctx.Set<PaymentVoucher>().AsNoTracking().FirstAsync(x => x.Id == id);
        var entries = v.JournalEntryId is null ? 0
            : await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(e => e.Id == v.JournalEntryId);
        return (v.Status, v.JournalEntryId, entries);
    }

    private static void AssertLifecycleInvariant(VoucherStatus status, Guid? entryId, int entriesInDb, string label)
    {
        Assert.False(status == VoucherStatus.Cancelled && entryId is not null,
            $"{label}: سند ملغى مع قيد مرحَّل (entry={entryId}) — سباق إلغاء/ترحيل.");
        Assert.False(status == VoucherStatus.Posted && entryId is null,
            $"{label}: سند مرحَّل بلا قيد.");
        Assert.Equal(entryId is null ? 0 : 1, entriesInDb);
    }

    private static string Describe(Exception? ex)
        => ex is null ? "none" : ex.GetType().Name + ": " + ex.Message.Replace("\r", " ").Replace("\n", " ");

    private static async Task<bool> WaitForAsync(Task task, TimeSpan timeout)
        => await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private async Task<Guid> NewSupplierAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "مورد سباق " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }
}
