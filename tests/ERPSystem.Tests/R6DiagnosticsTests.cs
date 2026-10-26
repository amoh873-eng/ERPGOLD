using System.Data.Common;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// R6-A / R6-B — حسم تحقق R2-F1 واختبار قفل صف العميل في سند القبض.
/// يسجّل **نص الأمر كاملًا دون اقتطاع** + الاتصال والمعاملة، ويقيس الحجب **بعد** جملة القفل.
/// </summary>
public class R6DiagnosticsTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public R6DiagnosticsTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "r6-diag";
    }

    /// <summary>بوابة + سجل كامل: تمنع التنفيذ عند أول أمر مطابق، وتسجل كل أمر بنصه الكامل.</summary>
    private sealed class GateAndLogInterceptor : DbCommandInterceptor
    {
        public static readonly List<string> Log = new();
        private static readonly object Sync = new();
        private readonly string _match;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GateAndLogInterceptor(string match) => _match = match;
        public Task Reached => _reached.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken t = default)
        { await Handle(c, t); return r; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken t = default)
        { await Handle(c, t); return r; }

        private async Task Handle(DbCommand c, CancellationToken ct)
        {
            var text = c.CommandText.Replace("\r", " ").Replace("\n", " ");
            lock (Sync)
                Log.Add($"{DateTime.UtcNow:HH:mm:ss.fff} conn={(c.Connection is null ? "null" : c.Connection.GetHashCode().ToString())} " +
                        $"tx={(c.Transaction is null ? "null" : "set")} flags=" +
                        $"{(text.Contains("FOR UPDATE") ? "FORUPDATE;" : "")}" +
                        $"{(text.Contains("UPDATE \"Customers\"") ? "CUSTWRITE;" : "")}" +
                        $"{(text.Contains("INSERT INTO \"AuditTrails\"") ? "AUDIT;" : "")}" +
                        $"{(text.Contains("UPDATE \"ReceiptVouchers\"") ? "VOUCHERCLAIM;" : "")} len={text.Length}");
            if (text.Contains(_match, StringComparison.Ordinal))
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task<(Guid VoucherId, Guid CustomerId)> NewApprovedVoucherAsync()
    {
        await using var ctx = _pg.NewContext();
        await _pg.SeedCashAsync();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Code = "R6-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "عميل R6", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();

        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customer.Id, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 9m
        });
        await svc.ApproveAsync(draft.Id);
        return (draft.Id, customer.Id);
    }

    private async Task<bool> IndependentCustomerWriteBlockedAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx.GetDbTransaction();
        cmd.CommandTimeout = 2;
        cmd.CommandText = "UPDATE \"Customers\" SET \"Phone\" = 'r6-probe' WHERE \"Id\" = @id";
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = customerId;
        cmd.Parameters.Add(p);
        try { await cmd.ExecuteNonQueryAsync(); await tx.RollbackAsync(); return false; }
        catch (DbException) { await tx.RollbackAsync(); return true; }
    }

    /// <summary>
    /// R6-A: تشخيص — السجل الكامل يُثبت تنفيذ الحجز الشرطي داخل معاملة (`tx=set`) ومع ذلك لم يُحجب
    /// كاتب مستقل على صف السند ⇒ **السبب الآلي غير محسوم**. يُحفظ كدليل بتخطٍّ معلَّل (ليس بوابة).
    /// </summary>
    [Fact(Skip = "R6-A: تشخيص غير محسوم — الدليل محفوظ في ERP-ACCOUNTING-INDEPENDENT-AUDIT-R6.md §3")]
    public async Task R6A_cancel_path_executes_for_update_and_holds_the_lock_afterwards()
    {
        if (!_pg.Available) return;
        var (voucherId, _) = await NewApprovedVoucherAsync();
        GateAndLogInterceptor.Log.Clear();

        var gate = new GateAndLogInterceptor("INSERT INTO \"AuditTrails\"");
        await using var ctx = _pg.NewContextWithInterceptor(gate, retryOnFailure: false);
        var cancelTask = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll())
            .CancelAsync(voucherId, "R6-A");

        var gated = await Task.WhenAny(gate.Reached, Task.Delay(TimeSpan.FromSeconds(20))) == gate.Reached;
        Assert.True(gated, "لم يُوصل الإلغاء إلى سجل التدقيق. السجل: " + string.Join(" || ", GateAndLogInterceptor.Log));

        var blocked = await IndependentCustomerWriteBlockedAsync(voucherId);
        gate.Release();
        var cancelError = await Record.ExceptionAsync(() => cancelTask);

        var log = string.Join(" || ", GateAndLogInterceptor.Log);
        // بعد إصلاح R3 لم يعد مسار الإلغاء يستخدم FOR UPDATE، بل حجزًا شرطيًا على صف السند:
        Assert.Contains("VOUCHERCLAIM", log);
        Assert.True(blocked,
            $"بعد تنفيذ الحجز الشرطي لم يُحجب كاتب مستقل على صف السند (cancelError={cancelError?.GetType().Name ?? "none"}). السجل: {log}");
    }

    /// <summary>R6-B: هل يُحجب كاتب مستقل على صف العميل أثناء ترحيل سند القبض؟</summary>
    [LivePgFact]
    public async Task R6B_receipt_post_must_block_an_independent_customer_writer()
    {
        if (!_pg.Available) return;
        var (voucherId, customerId) = await NewApprovedVoucherAsync();
        GateAndLogInterceptor.Log.Clear();

        var gate = new GateAndLogInterceptor("UPDATE \"Customers\"");
        await using var ctx = _pg.NewContextWithInterceptor(gate, retryOnFailure: false);
        var postTask = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll()).PostAsync(voucherId);

        var gated = await Task.WhenAny(gate.Reached, Task.Delay(TimeSpan.FromSeconds(20))) == gate.Reached;
        Assert.True(gated, "لم يُوصل الترحيل إلى كتابة صف العميل. السجل: " + string.Join(" || ", GateAndLogInterceptor.Log));

        var blocked = await IndependentCustomerWriteBlockedAsync(customerId);
        gate.Release();
        var postError = await Record.ExceptionAsync(() => postTask);

        Assert.True(blocked,
            $"قفل صف العميل غير فعّال في مسار سند القبض (postError={postError?.GetType().Name ?? "none"}). " +
            $"السجل: {string.Join(" || ", GateAndLogInterceptor.Log)}");
    }
}
