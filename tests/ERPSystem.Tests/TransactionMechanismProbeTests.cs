using System.Data.Common;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// R5-1 — تجربة حاسمة: هل تبقى أقفال الصفوف فعّالة داخل معاملة `DbTransactionHelper`؟
/// المتغيّر الوحيد بين الاختبارين هو **آلية فتح المعاملة** (نفس القفل، نفس المسبار، نفس قاعدة البيانات).
/// </summary>
public class TransactionMechanismProbeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public TransactionMechanismProbeTests(AccountingPgFixture pg) => _pg = pg;

    private async Task<Guid> NewCustomerAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "عميل معاملة", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    private static Task LockCustomerAsync(AppDbContext ctx, Guid id)
        => ctx.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Customers\" WHERE \"Id\" = {id} FOR UPDATE");

    /// <summary>كاتب مستقل يحاول تعديل الصف: true = محجوب (القفل فعّال).</summary>
    private async Task<bool> IndependentWriteBlockedAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx.GetDbTransaction();
        cmd.CommandTimeout = 2;
        cmd.CommandText = "UPDATE \"Customers\" SET \"Phone\" = 'r5-probe' WHERE \"Id\" = @id";
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = customerId;
        cmd.Parameters.Add(p);
        try { await cmd.ExecuteNonQueryAsync(); await tx.RollbackAsync(); return false; }
        catch (DbException) { await tx.RollbackAsync(); return true; }
    }

    /// <summary>(1) المعاملة عبر `DbTransactionHelper` (نمط خدمات السندات).</summary>
    [LivePgFact]
    public async Task DbTransactionHelper_transaction_must_retain_the_row_lock()
    {
        if (!_pg.Available) return;
        var customer = await NewCustomerAsync("R5H");
        await using var ctx = _pg.NewContext();

        var blocked = false;
        await DbTransactionHelper.ExecuteAsync(ctx, async () =>
        {
            await LockCustomerAsync(ctx, customer);
            blocked = await IndependentWriteBlockedAsync(customer);
        });

        Assert.True(blocked,
            "القفل غير فعّال داخل معاملة DbTransactionHelper: كاتب مستقل عدّل الصف بينما القفل مأخوذ");
    }

    /// <summary>(2) ضابط المقارنة: معاملة صريحة بنفس القفل ونفس المسبار.</summary>
    [LivePgFact]
    public async Task Explicit_transaction_retains_the_row_lock_control()
    {
        if (!_pg.Available) return;
        var customer = await NewCustomerAsync("R5E");
        await using var ctx = _pg.NewContext();

        await using var tx = await ctx.Database.BeginTransactionAsync();
        await LockCustomerAsync(ctx, customer);
        var blocked = await IndependentWriteBlockedAsync(customer);
        await tx.RollbackAsync();

        Assert.True(blocked, "القفل غير فعّال حتى في المعاملة الصريحة (خلل في المنهجية لا في المساعد)");
    }
}
