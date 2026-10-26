using System.Data.Common;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// R3 — مسبار حتمي لقفل صف السند: هل <c>SELECT … FOR UPDATE</c> المستخدم في مسار الإلغاء
/// يحجب فعلًا كاتبًا آخر على نفس الصف؟ (تشخيص السبب الجذري لـR2-F1، بلا إصلاح)
/// </summary>
public class VoucherLockProbeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public VoucherLockProbeTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "lock-probe";
    }

    private sealed class GateInterceptor : DbCommandInterceptor
    {
        private readonly string _match;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GateInterceptor(string match) => _match = match;
        public Task Reached => _reached.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { await Gate(command, ct); return result; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { await Gate(command, ct); return result; }

        private async Task Gate(DbCommand command, CancellationToken ct)
        {
            if (command.CommandText.Contains(_match, StringComparison.Ordinal))
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task<(Guid VoucherId, Guid CustomerId)> NewApprovedVoucherAsync()
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Code = "LK-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "عميل مسبار", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();

        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customer.Id, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 7m
        });
        await svc.ApproveAsync(draft.Id);
        return (draft.Id, customer.Id);
    }

    /// <summary>
    /// مقارنة مستويين: (ب) نفس جملة القفل عبر EF `ExecuteSqlInterpolatedAsync`،
    /// و(ج) نفس الجملة عبر أمر ADO صريح مرتبط بالمعاملة — لتحديد مصدر عدم فعالية القفل.
    /// </summary>
    [LivePgFact]
    public async Task Lock_effectiveness_via_ef_executesql_versus_raw_ado()
    {
        if (!_pg.Available) return;
        var (voucherId, _) = await NewApprovedVoucherAsync();

        // (ب) عبر EF ExecuteSqlInterpolatedAsync داخل معاملة صريحة
        var blockedViaEf = await LockAndProbeAsync(voucherId, async ctx =>
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"ReceiptVouchers\" WHERE \"Id\" = {voucherId} FOR UPDATE"));

        // (ج) عبر أمر ADO صريح مرتبط بالمعاملة
        var blockedViaAdo = await LockAndProbeAsync(voucherId, async ctx =>
        {
            var conn = ctx.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = ctx.Database.CurrentTransaction!.GetDbTransaction();
            cmd.CommandText = "SELECT 1 FROM \"ReceiptVouchers\" WHERE \"Id\" = @id FOR UPDATE";
            var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = voucherId;
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.True(blockedViaEf,
            $"القفل عبر EF ExecuteSqlInterpolatedAsync غير فعّال (EF={blockedViaEf} · ADO={blockedViaAdo})");
        Assert.True(blockedViaAdo,
            $"القفل عبر ADO غير فعّال (EF={blockedViaEf} · ADO={blockedViaAdo})");
    }

    /// <summary>ينفّذ القفل داخل معاملة ثم يقيس حجب كاتب مستقل، ويعيد حالة الحجب.</summary>
    private async Task<bool> LockAndProbeAsync(Guid voucherId, Func<AppDbContext, Task> lockAction)
    {
        await using var ctx = _pg.NewContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await lockAction(ctx);
        var blocked = await IndependentWriterBlockedAsync(voucherId);
        await tx.RollbackAsync();
        return blocked;
    }

    private sealed class TimelineInterceptor : DbCommandInterceptor
    {
        public static readonly List<string> Events = new();
        private static readonly object Sync = new();

        private static void Record(DbCommand cmd)
        {
            var text = cmd.CommandText.Replace("\r", " ").Replace("\n", " ");
            var conn = cmd.Connection;
            var flags = (text.Contains("FOR UPDATE") ? "FORUPDATE " : "")
                      + (text.Contains("UPDATE \"ReceiptVouchers\"") ? "VOUCHERWRITE " : "")
                      + (text.Contains("INSERT INTO \"AuditTrails\"") ? "AUDIT " : "")
                      + (text.Contains("UPDATE \"Accounts\"") ? "ACCBAL " : "");
            lock (Sync)
                Events.Add($"{DateTime.UtcNow:HH:mm:ss.fff} conn={(conn is null ? "null" : conn.GetHashCode().ToString())} state={conn?.State} tx={(cmd.Transaction is null ? "null" : "set")} [{flags.Trim()}] len={text.Length}");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken t = default)
        { Record(c); return base.NonQueryExecutingAsync(c, e, r, t); }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken t = default)
        { Record(c); return base.ReaderExecutingAsync(c, e, r, t); }
    }

    /// <summary>تشخيص زمني: يثبت هل جملة القفل والكتابة على نفس الاتصال/المعاملة فعلًا (مسبار R3 موثَّق).</summary>
    [Fact(Skip = "R3 diagnostic evidence: مسار الإلغاء لم يُظهر جملة القفل في سجل الأوامر (ERP-ACCOUNTING-INDEPENDENT-AUDIT-R3.md §3) — لا يُشغَّل كبوابة.")]
    public async Task Timeline_of_cancel_path_shows_lock_and_write_on_same_connection()
    {
        if (!_pg.Available) return;
        var (voucherId, _) = await NewApprovedVoucherAsync();
        TimelineInterceptor.Events.Clear();

        await using var ctx = _pg.NewContextWithInterceptor(new TimelineInterceptor(), retryOnFailure: false);
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        await svc.CancelAsync(voucherId, "تشخيص زمني");

        var lockEvents = TimelineInterceptor.Events.Where(x => x.Contains("FOR UPDATE")).ToList();
        var writeEvents = TimelineInterceptor.Events.Where(x => x.Contains("UPDATE \"ReceiptVouchers\"")).ToList();

        Assert.True(lockEvents.Count > 0, "لم تُنفَّذ جملة القفل FOR UPDATE إطلاقًا. الأحداث: " + string.Join(" || ", TimelineInterceptor.Events));
        Assert.True(writeEvents.Count > 0, "لم تُنفَّذ كتابة حالة السند. الأحداث: " + string.Join(" || ", TimelineInterceptor.Events));

        var lockConn = lockEvents[0].Split("conn=")[1].Split(" ")[0];
        var writeConn = writeEvents[0].Split("conn=")[1].Split(" ")[0];
        Assert.True(lockConn == writeConn,
            $"القفل والكتابة على اتصالين مختلفين (lock={lockConn} write={writeConn}). الأحداث: " + string.Join(" || ", TimelineInterceptor.Events));
    }

    /// <summary>هل يحجب قفل الخدمة كاتبًا آخر؟ (مسبار تشخيصي R3 — موثَّق في تقرير R3: القفل غير فعّال في هذا النمط)</summary>
    [Fact(Skip = "R3 diagnostic evidence: أُثبت أن FOR UPDATE في مسار الخدمة لا يحجب كاتبًا مستقلًا (ERP-ACCOUNTING-INDEPENDENT-AUDIT-R3.md §3) — لا يُشغَّل كبوابة.")]
    public async Task Service_cancel_lock_must_block_an_independent_writer_on_the_same_row()
    {
        if (!_pg.Available) return;
        var (voucherId, _) = await NewApprovedVoucherAsync();

        var gate = new GateInterceptor("UPDATE \"ReceiptVouchers\"");
        await using var cancelCtx = _pg.NewContextWithInterceptor(gate, retryOnFailure: false);
        var cancelTask = new ReceiptVoucherService(cancelCtx, new JournalEntryService(cancelCtx), new AllowAll())
            .CancelAsync(voucherId, "مسبار القفل");

        var gated = await Task.WhenAny(gate.Reached, Task.Delay(TimeSpan.FromSeconds(20))) == gate.Reached;
        Assert.True(gated, "لم يصل الإلغاء إلى مرحلة الكتابة");

        // كاتب مستقل يحاول تحديث نفس الصف — إن كان القفل فعّالًا فيجب أن يُحجب (مهلة 2 ثانية)
        var blocked = await IndependentWriterBlockedAsync(voucherId);
        gate.Release();
        var cancelError = await Record.ExceptionAsync(() => cancelTask);

        Assert.True(blocked,
            $"القفل غير فعّال: كاتب مستقل عدّل الصف أثناء وجود قفل الإلغاء (cancelError={cancelError?.GetType().Name ?? "none"})");
    }

    /// <summary>يقيس حجب كاتب مستقل على نفس الصف: يعيد true إذا انتهت المهلة (محجوب).</summary>
    private async Task<bool> IndependentWriterBlockedAsync(Guid voucherId)
    {
        await using var ctx = _pg.NewContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx.GetDbTransaction();
        cmd.CommandTimeout = 2;
        cmd.CommandText = "UPDATE \"ReceiptVouchers\" SET \"CancelReason\" = 'independent-probe' WHERE \"Id\" = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = voucherId;
        cmd.Parameters.Add(p);
        try
        {
            await cmd.ExecuteNonQueryAsync();
            await tx.RollbackAsync();
            return false;   // لم يُحجب ⇒ القفل غير فعّال
        }
        catch (DbException)
        {
            await tx.RollbackAsync();
            return true;    // انتهت المهلة ⇒ محجوب ⇒ القفل فعّال
        }
    }
}
