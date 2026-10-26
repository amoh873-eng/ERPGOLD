using System.Data.Common;
using ERPSystem.Application.DTOs.Sales;
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
/// R4 — مسبار فعالية قفل الطرف (`PartyRowLock`) داخل مسار البيع الفعلي.
/// المنهجية نفسها التي أثبتت R2-F1: نُعلّق الطلب عند كتابة صف العميل ثم يحاول **كاتب مستقل**
/// تعديل الصف نفسه. إن نجح الكاتب ⇒ القفل غير فعّال ⇒ خطر تحديث ضائع على `Customer.CurrentBalance`.
/// </summary>
public class PartyLockProbeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public PartyLockProbeTests(AccountingPgFixture pg) => _pg = pg;

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

    private async Task<Guid> NewCustomerAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "عميل قفل", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>يحاول كاتب مستقل تعديل صف العميل: true = محجوب (القفل فعّال) · false = نجح (القفل غير فعّال).</summary>
    private async Task<bool> IndependentCustomerWriteBlockedAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx.GetDbTransaction();
        cmd.CommandTimeout = 2;
        cmd.CommandText = "UPDATE \"Customers\" SET \"Phone\" = 'r4-probe' WHERE \"Id\" = @id";
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = customerId;
        cmd.Parameters.Add(p);
        try
        {
            await cmd.ExecuteNonQueryAsync();
            await tx.RollbackAsync();
            return false;
        }
        catch (DbException)
        {
            await tx.RollbackAsync();
            return true;
        }
    }

    /// <summary>(1) قفل الطرف كما هو مستخدم فعليًا في مسار البيع (`PartyRowLock`).</summary>
    [LivePgFact]
    public async Task PartyRowLock_in_sales_path_must_block_an_independent_customer_writer()
    {
        if (!_pg.Available) return;
        var customer = await NewCustomerAsync("R4A");
        var itemId = await _pg.CreateItemAsync("R4A-" + Guid.NewGuid().ToString("N")[..6], 10m, 5m, 50m, taxRate: 16m);

        var gate = new GateInterceptor("UPDATE \"Customers\"");
        await using var saleCtx = _pg.NewContextWithInterceptor(gate, retryOnFailure: false);
        var saleTask = AccountingPgFixture.NewSalesService(saleCtx).CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = customer,
            WarehouseId = _pg.WarehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.OnAccount,
            PaymentMethod = (int)SalesPaymentMethod.OnAccount,
            TaxRate = 16m,
            Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 1m, UnitPrice = 10m } }
        });

        var gated = await Task.WhenAny(gate.Reached, Task.Delay(TimeSpan.FromSeconds(20))) == gate.Reached;
        Assert.True(gated, "لم تصل الفاتورة إلى مرحلة كتابة صف العميل");

        var blocked = await IndependentCustomerWriteBlockedAsync(customer);
        gate.Release();
        var saleError = await Record.ExceptionAsync(() => saleTask);

        Assert.True(blocked,
            $"قفل الطرف (PartyRowLock) غير فعّال في مسار البيع: كاتب مستقل عدّل صف العميل " +
            $"(saleError={saleError?.GetType().Name ?? "none"})");
    }

    /// <summary>(2) نمط ADO الصريح بنفس الجملة — للمقارنة (يُتوقع أن يحجب).</summary>
    [LivePgFact]
    public async Task Raw_ado_lock_on_customer_must_block_an_independent_writer()
    {
        if (!_pg.Available) return;
        var customer = await NewCustomerAsync("R4B");

        await using var ctx = _pg.NewContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var conn = ctx.Database.GetDbConnection();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx.GetDbTransaction();
            cmd.CommandText = "SELECT 1 FROM \"Customers\" WHERE \"Id\" = @id FOR UPDATE";
            var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = customer;
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync();
        }

        var blockedRaw = await IndependentCustomerWriteBlockedAsync(customer);
        await tx.RollbackAsync();
        Assert.True(blockedRaw, "نمط ADO الصريح لم يحجب الكاتب المستقل (متوقع أن يحجب)");
    }

    /// <summary>
    /// (3) مسار يستخدم `DbTransactionHelper` (سند الصرف): هل يبقى القفل فعّالًا؟
    /// **غير حاسم في R4**: البوابة لم تقع بعد الحجز/القفل في كل المحاولات (بوابة على تحديث السند =
    /// قبل تنفيذ الحجز، وبوابة على إدخال القيد = لم تُلتقط) ⇒ لا يُستخدم كبوابة، والنتيجة موثَّقة في R4 §4.
    /// </summary>
    [Fact(Skip = "R4: مسبار غير حاسم (موضع البوابة) — موثَّق في ERP-ACCOUNTING-INDEPENDENT-AUDIT-R4.md §4")]
    public async Task DbTransactionHelper_paths_must_also_hold_their_row_lock()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("R4C");

        Guid voucherId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = new PaymentVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var draft = await svc.CreateDraftAsync(new ERPSystem.Application.DTOs.Vouchers.CreatePaymentVoucherDto
            {
                SupplierId = supplier, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 5m
            });
            voucherId = draft.Id;
            await svc.ApproveAsync(voucherId);
        }

        // البوابة على إدخال القيد (يقع بعد حجز الحالة والقفل) — لقياس استمرار القفل داخل المعاملة
        var gate = new GateInterceptor("INSERT INTO \"JournalEntries\"");
        await using var postCtx = _pg.NewContextWithInterceptor(gate, retryOnFailure: false);
        var postTask = new PaymentVoucherService(postCtx, new JournalEntryService(postCtx), new AllowAll())
            .PostAsync(voucherId, null, null);

        var gated = await Task.WhenAny(gate.Reached, Task.Delay(TimeSpan.FromSeconds(20))) == gate.Reached;
        Assert.True(gated, "لم يصل الترحيل إلى مرحلة كتابة صف السند");

        var blocked = await IndependentVoucherWriteBlockedAsync(voucherId);
        gate.Release();
        var postError = await Record.ExceptionAsync(() => postTask);

        Assert.True(blocked,
            $"قفل صف السند في مسار `DbTransactionHelper` غير فعّال: كاتب مستقل عدّل صف السند " +
            $"(postError={postError?.GetType().Name ?? "none"})");
    }

    /// <summary>كاتب مستقل على صف سند الصرف: true = محجوب (القفل فعّال).</summary>
    private async Task<bool> IndependentVoucherWriteBlockedAsync(Guid voucherId)
    {
        await using var ctx = _pg.NewContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx.GetDbTransaction();
        cmd.CommandTimeout = 2;
        cmd.CommandText = "UPDATE \"PaymentVouchers\" SET \"CancelReason\" = 'r4-probe' WHERE \"Id\" = @id";
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = voucherId;
        cmd.Parameters.Add(p);
        try { await cmd.ExecuteNonQueryAsync(); await tx.RollbackAsync(); return false; }
        catch (DbException) { await tx.RollbackAsync(); return true; }
    }

    private sealed class AllowAll : ERPSystem.Application.Security.IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "r4-lock-probe";
    }

    private async Task<Guid> NewSupplierAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(), Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "مورد قفل", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }
}
