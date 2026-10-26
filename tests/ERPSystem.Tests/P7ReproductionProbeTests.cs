using ERPSystem.Application.DTOs.BankChecks;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P7 — مسبار إعادة إنتاج G1 و G2 (دليل «قبل/بعد» على قاعدة معزولة).
///
/// ⚠️ غرضه القياس فقط: لا يعدّل أي سلوك إنتاجي، ويسجّل الأرقام في `_p7_evidence.txt`
/// موسومة بالمرحلة من متغيّر البيئة <c>P7_PHASE</c> (before / after).
///
///  • G1: حركة «رصيد افتتاحي» من مسار حركات المخزون (نفس ما تستدعيه الشاشة تمامًا)
///        ⇒ قياس كمية/تكلفة/قيمة المخزون مقابل أثر الحساب 1300 في الأستاذ.
///  • G2: فاتورة مسدَّدة بشيك ثم **ارتداد** الشيك
///        ⇒ قياس حالة دفع الفاتورة · رصيد العميل · حساب 1200 · تقرير أعمار الذمم
///          (مستوى الطرف + مستوى الفاتورة) + منع التكرار (ارتداد ثانٍ).
/// </summary>
public class P7ReproductionProbeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P7ReproductionProbeTests(AccountingPgFixture pg) => _pg = pg;

    private const string EvidenceFile = @"D:\erpfix-pu01\_p7_evidence.txt";

    private static string Phase() => Environment.GetEnvironmentVariable("P7_PHASE") ?? "unknown";

    private static async Task LogAsync(string line)
    {
        try { await File.AppendAllTextAsync(EvidenceFile, "[" + Phase() + "] " + line + Environment.NewLine); }
        catch { /* ملف الدليل اختياري */ }
    }

    private async Task<decimal> GlNetAsync(string accountCode)
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == accountCode && !a.IsDeleted).Select(a => a.Id).FirstAsync();

        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && !l.IsDeleted
                        && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }

    // ═══════════════════════ G1 ═══════════════════════

    [LivePgFact]
    public async Task G1_opening_movement_from_movements_screen_vs_account_1300()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var itemId = await _pg.CreateItemAsync("P7-G1-" + suffix, salePrice: 20m, costPrice: 0m, openingStock: 0m);

        var gl1300Before = await GlNetAsync("1300");

        // نفس الاستدعاء الذي تنفّذه شاشة حركات المخزون (MovementFormDialog → IStockMovementService)
        await using (var ctx = _pg.NewContext())
        {
            await new StockMovementService(ctx, new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreateStockMovementDto
                {
                    ItemId = itemId,
                    WarehouseId = _pg.WarehouseId,
                    MovementType = (int)MovementType.OpeningBalance,
                    Quantity = 100m,
                    UnitCost = 10m,
                    MovementDate = DateTime.Today,
                    ReferenceNumber = "P7-G1-" + suffix
                });
        }

        decimal stock, cost;
        await using (var ctx = _pg.NewContext())
        {
            var item = await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId);
            stock = item.CurrentStock;
            cost = item.CostPrice;
        }

        var gl1300After = await GlNetAsync("1300");
        var inventoryValue = Math.Round(stock * cost, 2);
        var gl1300Delta = Math.Round(gl1300After - gl1300Before, 2);

        await LogAsync(
            $"G1 | item=P7-G1-{suffix} | stock={stock} cost={cost} inventoryValue={inventoryValue} | " +
            $"gl1300_delta={gl1300Delta} | gap={Math.Round(inventoryValue - gl1300Delta, 2)}");
    }

    /// <summary>المسار المعتمد المدعوم: دفعة الأرصدة الافتتاحية (تُرحّل 1300 + تُنشئ الحركة) — مرجع للمقارنة.</summary>
    [LivePgFact]
    public async Task G1_official_opening_balance_batch_keeps_inventory_and_1300_matched()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var itemId = await _pg.CreateItemAsync("P7-G1O-" + suffix, salePrice: 20m, costPrice: 0m, openingStock: 0m);

        string itemCode;
        await using (var ctx = _pg.NewContext())
            itemCode = await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => i.Code).FirstAsync();

        var gl1300Before = await GlNetAsync("1300");

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var batch = await svc.CreateDraftAsync(new ERPSystem.Application.DTOs.Accounting.CreateOpeningBalanceBatchDto
            {
                FiscalYear = DateTime.Today.Year,
                EntryDate = DateTime.Today,
                Reference = "P7-G1O-" + suffix,
                Lines = new List<ERPSystem.Application.DTOs.Accounting.OpeningBalanceLineInput>
                {
                    new()
                    {
                        AccountCode = "1300",
                        DebitAmount = 1000m,
                        ItemCode = itemCode,
                        WarehouseCode = AccountingPgFixture.WarehouseCode,
                        Quantity = 100m,
                        UnitCost = 10m
                    },
                    new() { AccountCode = "1100", CreditAmount = 1000m }
                }
            }, "p7-probe");

            await svc.PostAsync(batch.Id, "p7-probe");
        }

        decimal stock, cost;
        await using (var ctx = _pg.NewContext())
        {
            var item = await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId);
            stock = item.CurrentStock;
            cost = item.CostPrice;
        }

        var gl1300Delta = Math.Round(await GlNetAsync("1300") - gl1300Before, 2);
        var inventoryValue = Math.Round(stock * cost, 2);

        await LogAsync(
            $"G1-official | item=P7-G1O-{suffix} | stock={stock} cost={cost} inventoryValue={inventoryValue} | " +
            $"gl1300_delta={gl1300Delta} | gap={Math.Round(inventoryValue - gl1300Delta, 2)}");
    }

    // ═══════════════════════ G2 ═══════════════════════

    [LivePgFact]
    public async Task G2_bounced_cheque_invoice_paid_state_customer_balance_and_aging()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var itemId = await _pg.CreateItemAsync("P7-G2-" + suffix, salePrice: 20m, costPrice: 10m, openingStock: 50m);

        // (1) فاتورة 2 × 20 = 40 مسدَّدة بشيك (نفس مسار الشاشة)
        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var dto = new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Check,
                TaxRate = 0m,
                CheckNumber = "P7-CHK-" + suffix,
                CheckBankName = "بنك اختبار",
                CheckIssueDate = DateTime.Today,
                CheckDueDate = DateTime.Today.AddDays(30),
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = 2m, UnitPrice = 20m }
                }
            };
            invoiceId = (await AccountingPgFixture.NewSalesService(ctx).CreateAsync(dto)).Id;
        }

        Guid checkId;
        await using (var ctx = _pg.NewContext())
            checkId = (await ctx.Set<BankCheck>().AsNoTracking()
                .FirstAsync(c => c.RelatedInvoiceId == invoiceId)).Id;

        // (2) الارتداد
        await using (var ctx = _pg.NewContext())
            await new BankCheckService(ctx, new JournalEntryService(ctx)).BounceAsync(checkId, DateTime.Today);

        decimal total, paid, customerBalance, gl1200, agingParty, agingInvoice;
        string checkStatus;
        await using (var ctx = _pg.NewContext())
        {
            var inv = await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => i.Id == invoiceId);
            total = inv.TotalAmount;
            paid = inv.PaidAmount;
            customerBalance = await ctx.Set<Customer>().AsNoTracking()
                .Where(c => c.Id == _pg.CustomerId).Select(c => c.CurrentBalance).FirstAsync();
            checkStatus = (await ctx.Set<BankCheck>().AsNoTracking()
                .FirstAsync(c => c.Id == checkId)).Status.ToString();
            gl1200 = await GlNetAsync("1200");

            var residuals = await new CustomerAllocationQueries(ctx).GetComputedResidualsAsync(DateTime.Today);
            agingInvoice = residuals.Where(r => r.InvoiceId == invoiceId)
                .Select(r => r.ComputedResidual).DefaultIfEmpty(0m).Sum();

            var aging = await AgingReportQueries.GetAgingAsync(ctx, isReceivable: true, asOf: DateTime.Today);
            agingParty = aging.Rows.Where(r => r.PartyId == _pg.CustomerId)
                .Sum(r => r.Total + r.CreditBalance);
        }

        await LogAsync(
            $"G2 | invoice=P7-G2-{suffix} total={total} paid={paid} | customerBalance={customerBalance} | " +
            $"gl1200={gl1200} | checkStatus={checkStatus} | aging_party={agingParty} | aging_invoice_residual={agingInvoice}");

        // (3) منع التكرار: ارتداد ثانٍ لنفس الشيك يجب أن يُرفض (لا أثر مضاعف على الفاتورة/الرصيد)
        string secondBounce;
        decimal paidAfterSecond, customerAfterSecond;
        await using (var ctx = _pg.NewContext())
        {
            var ex = await Record.ExceptionAsync(() =>
                new BankCheckService(ctx, new JournalEntryService(ctx)).BounceAsync(checkId, DateTime.Today));
            secondBounce = ex is null ? "NO-EXCEPTION" : ex.GetType().Name;
        }
        await using (var ctx = _pg.NewContext())
        {
            paidAfterSecond = (await ctx.Set<SalesInvoice>().AsNoTracking()
                .FirstAsync(i => i.Id == invoiceId)).PaidAmount;
            customerAfterSecond = await ctx.Set<Customer>().AsNoTracking()
                .Where(c => c.Id == _pg.CustomerId).Select(c => c.CurrentBalance).FirstAsync();
        }

        await LogAsync(
            $"G2-repeat | secondBounce={secondBounce} | paidAfterSecond={paidAfterSecond} | " +
            $"customerAfterSecond={customerAfterSecond}");
    }
}
