using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة 3 — تحقيقات الملاحظات المتبقية (قياس فعلي على قاعدة اختبار مؤقتة).
/// النتائج تُكتب إلى الملف المحدد بـ ERPSYSTEM_PROBE_OUT كدليل، والاختبارات تنجح طالما تم القياس.
/// </summary>
public class ResidualProbeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public ResidualProbeTests(AccountingPgFixture pg) => _pg = pg;

    private static SalesInvoiceService Sales(AppDbContext c) => new(c, new JournalEntryService(c), new AccountingPgFixture.AllowAllManualPrice());
    private static PurchaseInvoiceService Purchase(AppDbContext c) => new(c, new JournalEntryService(c), new AccountingPgFixture.AllowAllManualPrice());
    private static PurchaseReturnService PurchaseReturn(AppDbContext c) => new(c, new JournalEntryService(c));

    private static string OutFile()
    {
        var f = Environment.GetEnvironmentVariable("ERPSYSTEM_PROBE_OUT");
        return string.IsNullOrWhiteSpace(f) ? "residual-probes.txt" : f;
    }

    private async Task<long> SeqAsync(AppDbContext ctx, string prefix)
    {
        var key = $"{prefix}-{DateTime.Now:yyyyMMdd}";
        var conn = ctx.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"LastValue\" FROM \"NumberSequences\" WHERE \"SequenceKey\" = @k";
            var p = cmd.CreateParameter(); p.ParameterName = "k"; p.Value = key; cmd.Parameters.Add(p);
            var r = await cmd.ExecuteScalarAsync();
            return r is null or DBNull ? 0 : Convert.ToInt64(r);
        }
        finally { if (wasClosed && conn.State == System.Data.ConnectionState.Open) await conn.CloseAsync(); }
    }

    [LivePgFact]
    public async Task Probe_purchase_return_numbering_gap_and_collisions()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var log = new List<string> { "== P-01: purchase-return numbering (out-of-transaction) ==" };

        var itemId = await _pg.CreateItemAsync("P3-PR-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 50m, taxRate: 16m);
        Guid purchaseId; const decimal purchasedQty = 10m;
        await using (var ctx = _pg.NewContext())
        {
            var pi = await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = _pg.SupplierId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)PurchaseInvoiceType.Cash,
                Lines = new() { new() { ItemId = itemId, Quantity = purchasedQty, UnitCost = 10m } }
            });
            purchaseId = pi.Id;
        }

        long seqBefore, seqAfterOk, seqAfterRejected;
        await using (var ctx = _pg.NewContext()) seqBefore = await SeqAsync(ctx, "PR");
        await using (var ctx = _pg.NewContext())
        {
            await PurchaseReturn(ctx).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = purchaseId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today, SettlementType = 2, RefundReference = "PU04-TEST-REF",
                Lines = new() { new() { ItemId = itemId, Quantity = 2m, UnitCost = 10m } }
            });
        }
        await using (var ctx = _pg.NewContext()) seqAfterOk = await SeqAsync(ctx, "PR");
        log.Add("sequence_before=" + seqBefore + " after_successful_return=" + seqAfterOk + " delta=" + (seqAfterOk - seqBefore));

        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => PurchaseReturn(ctx).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = purchaseId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today, SettlementType = 2, RefundReference = "PU04-TEST-REF",
                Lines = new() { new() { ItemId = itemId, Quantity = purchasedQty + 5m, UnitCost = 10m } }
            }));
        }
        await using (var ctx = _pg.NewContext()) seqAfterRejected = await SeqAsync(ctx, "PR");
        log.Add("after_rejected_return=" + seqAfterRejected + " delta=" + (seqAfterRejected - seqAfterOk) + "  (1 = a number was burned by a rejected attempt)");
        log.Add("VERDICT_gap_exists=" + ((seqAfterRejected - seqAfterOk) > 0));

        // two concurrent returns on the same invoice: numbers must remain unique (atomic generator)
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            await start.Task;
            await using var c = _pg.NewContext();
            var dto = await PurchaseReturn(c).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = purchaseId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today, SettlementType = 2, RefundReference = "PU04-TEST-REF",
                Lines = new() { new() { ItemId = itemId, Quantity = 1m, UnitCost = 10m } }
            });
            return dto.ReturnNumber;
        }).ToList();
        start.SetResult();
        var numbers = await Task.WhenAll(tasks);
        log.Add("concurrent_numbers=" + string.Join(",", numbers) + " unique=" + (numbers.Distinct().Count() == numbers.Length));
        log.Add("VERDICT_collision_exists=" + (numbers.Distinct().Count() != numbers.Length));

        await using (var check = _pg.NewContext())
        {
            log.Add("duplicate_return_numbers_in_db=" + await check.Set<PurchaseReturn>().AsNoTracking().Where(r => !r.IsDeleted)
                .GroupBy(r => r.ReturnNumber).CountAsync(g => g.Count() > 1));
        }

        await File.WriteAllLinesAsync(OutFile(), log);
        Assert.True(File.Exists(OutFile()));
    }

    [LivePgFact]
    public async Task Probe_xmin_concurrency_token_feasibility()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var log = new List<string> { "== P-04: PostgreSQL concurrency-token feasibility (no schema change) ==" };

        // A) current model (RowVersion not generated) => last write wins
        await using (var a = _pg.NewContext())
        await using (var b = _pg.NewContext())
        {
            var ca = await a.Set<Customer>().FirstAsync(c => c.Id == _pg.CustomerId);
            var cb = await b.Set<Customer>().FirstAsync(c => c.Id == _pg.CustomerId);
            ca.NameEn = "STALE-A";
            cb.NameEn = "STALE-B";
            await a.SaveChangesAsync();
            var secondSaveThrew = false;
            try { await b.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { secondSaveThrew = true; }
            await using var check = _pg.NewContext();
            var final = await check.Set<Customer>().AsNoTracking().FirstAsync(c => c.Id == _pg.CustomerId);
            log.Add("without_token: second_save_threw=" + secondSaveThrew + " final_value=" + final.NameEn + " (B overwrote A silently = lost update)");
            log.Add("VERDICT_lost_update_exists=" + (!secondSaveThrew));
        }

        // B) is an xmin-based token available in the provider version referenced by this solution? (API probe, no schema change)
        var providerAssembly = typeof(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.NpgsqlOptionsExtension).Assembly;
        var members = providerAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
            .Where(m => m.Name.Contains("Xmin", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("ConcurrencyToken", StringComparison.OrdinalIgnoreCase))
            .Select(m => (m.DeclaringType?.Name ?? "?") + "." + m.Name)
            .Distinct().OrderBy(x => x).ToList();
        log.Add("provider=" + providerAssembly.GetName().Name + " v" + providerAssembly.GetName().Version);
        log.Add("xmin_or_concurrency_api_members=" + members.Count + " :: " + string.Join(", ", members.Take(12)));
        log.Add("VERDICT_xmin_token_available_out_of_the_box=" + (members.Count > 0));

        await File.AppendAllLinesAsync(OutFile(), log);
        Assert.True(File.Exists(OutFile()));
    }
}
