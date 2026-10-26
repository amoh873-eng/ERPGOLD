using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>تفويض كامل للسندات (للإثبات فقط — الاختبارات الأمنية منفصلة).</summary>
internal sealed class ProofVoucherAuthorization : IVoucherAuthorization
{
    public bool CanView() => true;
    public bool CanCreate() => true;
    public bool CanEdit() => true;
    public bool CanApprove() => true;
    public bool CanPost() => true;
    public bool CanCancel() => true;
    public string CurrentUserName() => "proof-seed";
}

/// <summary>
/// إثبات السلامة المحاسبية على قاعدة مستقلة (ERPSYSTEM_GATE_DB):
/// يولّد بيانات أعمال كاملة عبر طبقة الخدمات + حالات فشل متعمدة في منتصف العملية، ويكتب بيانًا بالأرقام.
/// التحقق النهائي يتم بـ SQL على القاعدة نفسها، ثم يُعاد بعد إعادة تشغيل التطبيق.
/// </summary>
public class AccountingProofSeedTests
{
    static AccountingProofSeedTests() { AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true); }

    private static string Cs()
    {
        var cs = Environment.GetEnvironmentVariable(GateDbFactAttribute.EnvVar)
                 ?? throw new InvalidOperationException("ERPSYSTEM_GATE_DB غير معيّن");
        if (cs.Contains("commercial", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("رفض: قاعدة الإثبات ليست قاعدة النسخة التجارية.");
        return cs;
    }

    private static string OutFile()
    {
        var f = Environment.GetEnvironmentVariable("ERPSYSTEM_GATE_OUT");
        return string.IsNullOrWhiteSpace(f) ? "accounting-proof-manifest.txt" : f;
    }

    private static AppDbContext Ctx(string cs) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(cs, o => o.MigrationsAssembly("ERPSystem.Infrastructure")).Options);

    private static SalesInvoiceService Sales(AppDbContext c) => new(c, new JournalEntryService(c), new AccountingPgFixture.AllowAllManualPrice());
    private static PurchaseInvoiceService Purchase(AppDbContext c) => new(c, new JournalEntryService(c), new AccountingPgFixture.AllowAllManualPrice());
    private static SalesReturnService SalesReturn(AppDbContext c) => new(c, new JournalEntryService(c));
    private static PurchaseReturnService PurchaseReturn(AppDbContext c) => new(c, new JournalEntryService(c));
    private static ReceiptVoucherService Receipts(AppDbContext c) => new(c, new JournalEntryService(c), new ProofVoucherAuthorization());
    private static PaymentVoucherService Payments(AppDbContext c) => new(c, new JournalEntryService(c), new ProofVoucherAuthorization());
    private static StockMovementService Movements(AppDbContext c) => new(c, new AccountingPgFixture.AllowAllManualPrice());

    /// <summary>لقطة رقمية للنظام قبل/بعد أي عملية فاشلة (لإثبات عدم وجود أثر جزئي).</summary>
    private static async Task<string> SnapshotAsync(AppDbContext ctx)
    {
        var docs = await ctx.Set<SalesInvoice>().AsNoTracking().CountAsync(x => !x.IsDeleted)
                 + await ctx.Set<PurchaseInvoice>().AsNoTracking().CountAsync(x => !x.IsDeleted)
                 + await ctx.Set<SalesReturn>().AsNoTracking().CountAsync(x => !x.IsDeleted)
                 + await ctx.Set<PurchaseReturn>().AsNoTracking().CountAsync(x => !x.IsDeleted)
                 + await ctx.Set<ReceiptVoucher>().AsNoTracking().CountAsync()
                 + await ctx.Set<PaymentVoucher>().AsNoTracking().CountAsync();
        var entries = await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(x => !x.IsDeleted);
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking().CountAsync(l => !l.IsDeleted);
        var movements = await ctx.Set<StockMovement>().AsNoTracking().CountAsync(m => !m.IsDeleted);
        var balances = await ctx.Set<Account>().AsNoTracking().SumAsync(a => (decimal?)a.CurrentBalance) ?? 0m;
        var stock = await ctx.Set<Item>().AsNoTracking().SumAsync(i => (decimal?)i.CurrentStock) ?? 0m;
        var custBal = await ctx.Set<Customer>().AsNoTracking().SumAsync(c => (decimal?)c.CurrentBalance) ?? 0m;
        var supBal = await ctx.Set<Supplier>().AsNoTracking().SumAsync(s => (decimal?)s.CurrentBalance) ?? 0m;
        var seq = await ctx.Database.SqlQueryRaw<long?>("SELECT coalesce(sum(\"LastValue\"),0)::bigint AS \"Value\" FROM \"NumberSequences\"").SingleAsync() ?? 0L;
        return $"docs={docs};entries={entries};lines={lines};movements={movements};acct_bal_sum={balances:F2};stock_sum={stock:F2};cust_bal={custBal:F2};sup_bal={supBal:F2};seq_sum={seq}";
    }

    [GateDbFact]
    public async Task Run_full_proof_dataset()
    {
        await Seed_accounting_proof_dataset();
        await Seed_purchases_and_sales();
        await Seed_returns_and_vouchers();
        await Failure_injections_leave_zero_residue();
        var log = new List<string> { "ALL_STEPS_COMPLETED=" + DateTime.UtcNow.ToString("o") };
        await File.AppendAllLinesAsync(OutFile(), log);
    }

    private async Task Seed_accounting_proof_dataset()
    {
        var cs = Cs();
        var log = new List<string> { "=== ACCOUNTING PROOF MANIFEST " + DateTime.UtcNow.ToString("o") + " ===" };
        var today = DateTime.Today;
        var openingDate = new DateTime(2026, 1, 1);

        // ── 1) master data ───────────────────────────────────────────────────────────
        Guid whId, custId, supId, itemA, itemB, itemC;
        await using (var ctx = Ctx(cs))
        {
            var cat = new Category { Id = Guid.NewGuid(), Code = "PF-CAT", NameAr = "فئة الإثبات", CreatedAt = DateTime.UtcNow };
            var unit = new Unit { Id = Guid.NewGuid(), Code = "PF-UN", NameAr = "وحدة الإثبات", CreatedAt = DateTime.UtcNow };
            var wh = new Warehouse { Id = Guid.NewGuid(), Code = "PF-WH", NameAr = "مخزن الإثبات", IsDefault = true, CreatedAt = DateTime.UtcNow };
            var cust = new Customer { Id = Guid.NewGuid(), Code = "PF-CUST", NameAr = "عميل الإثبات", IsActive = true, CreatedAt = DateTime.UtcNow };
            var sup = new Supplier { Id = Guid.NewGuid(), Code = "PF-SUP", NameAr = "مورد الإثبات", IsActive = true, CreatedAt = DateTime.UtcNow };
            ctx.AddRange(cat, unit, wh, cust, sup);
            await ctx.SaveChangesAsync();

            var a = new Item { Id = Guid.NewGuid(), Code = "PF-A", NameAr = "صنف أ", CategoryId = cat.Id, UnitId = unit.Id, SalePrice = 15m, CostPrice = 0m, TaxRate = 16m, CreatedAt = DateTime.UtcNow };
            var b = new Item { Id = Guid.NewGuid(), Code = "PF-B", NameAr = "صنف ب", CategoryId = cat.Id, UnitId = unit.Id, SalePrice = 25m, CostPrice = 0m, TaxRate = 16m, CreatedAt = DateTime.UtcNow };
            var c = new Item { Id = Guid.NewGuid(), Code = "PF-C", NameAr = "صنف ج (دفعات)", CategoryId = cat.Id, UnitId = unit.Id, SalePrice = 30m, CostPrice = 0m, TaxRate = 16m, TracksBatches = true, CreatedAt = DateTime.UtcNow };
            ctx.AddRange(a, b, c);
            await ctx.SaveChangesAsync();

            whId = wh.Id; custId = cust.Id; supId = sup.Id; itemA = a.Id; itemB = b.Id; itemC = c.Id;
            log.Add("warehouse=PF-WH customer=PF-CUST supplier=PF-SUP");
            log.Add("itemA=" + itemA + " itemB=" + itemB + " itemC(batches)=" + itemC);
        }

        // ── 2) opening stock movements + posted opening balance batch ─────────────────
        await using (var ctx = Ctx(cs))
        {
            await Movements(ctx).CreateAsync(new CreateStockMovementDto
            {
                ItemId = itemA, WarehouseId = whId, MovementType = (int)MovementType.OpeningBalance,
                Quantity = 20m, UnitCost = 9m, MovementDate = openingDate, ReferenceNumber = "PF-OPEN-A"
            });
            log.Add("opening_stock_A=20@9 value=180.00");
        }
        await using (var ctx = Ctx(cs))
        {
            await Movements(ctx).CreateAsync(new CreateStockMovementDto
            {
                ItemId = itemB, WarehouseId = whId, MovementType = (int)MovementType.OpeningBalance,
                Quantity = 10m, UnitCost = 20m, MovementDate = openingDate, ReferenceNumber = "PF-OPEN-B"
            });
            log.Add("opening_stock_B=10@20 value=200.00");
        }
        await using (var ctx = Ctx(cs))
        {
            await Movements(ctx).CreateAsync(new CreateStockMovementDto
            {
                ItemId = itemC, WarehouseId = whId, MovementType = (int)MovementType.OpeningBalance,
                Quantity = 5m, UnitCost = 7m, MovementDate = openingDate, ReferenceNumber = "PF-OPEN-C"
            });
            log.Add("opening_stock_C=5@7 (deliberately without a batch row)");
        }
        await using (var ctx = Ctx(cs))
        {
            var ob = new OpeningBalanceService(ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllOpeningBalance());
            var batch = await ob.CreateDraftAsync(new CreateOpeningBalanceBatchDto
            {
                FiscalYear = 2026,
                EntryDate = openingDate,
                Reference = "PF-OB-2026",
                Description = "أرصدة افتتاحية للإثبات",
                Lines = new List<OpeningBalanceLineInput>
                {
                    new() { AccountCode = "1100", DebitAmount = 3000m, CreditAmount = 0m, Note = "نقدية افتتاحية" },
                    new() { AccountCode = "2200", DebitAmount = 0m, CreditAmount = 3000m, SupplierCode = "PF-SUP", Note = "التزام افتتاحي" }
                }
            }, "proof-seed");
            var posted = await ob.PostAsync(batch.Id, "proof-seed");
            log.Add("opening_batch=" + posted.Reference + " entry=" + posted.EntryNumber + " status=" + posted.StatusNameAr);
            log.Add("opening_lines=1100 D 3000.00 | 2200 C 3000.00 (entry_date=" + openingDate.ToString("yyyy-MM-dd") + ")");
        }
        await File.WriteAllLinesAsync(OutFile(), log);
        Assert.True(File.Exists(OutFile()));
    }

    private async Task Seed_purchases_and_sales()
    {
        var cs = Cs();
        var log = new List<string> { "=== DOCUMENTS: PURCHASES & SALES ===" };
        var today = DateTime.Today;
        Guid whId, custId, supId, itemA, itemB, itemC;
        await using (var ctx = Ctx(cs))
        {
            whId = (await ctx.Set<Warehouse>().AsNoTracking().FirstAsync(w => w.Code == "PF-WH")).Id;
            custId = (await ctx.Set<Customer>().AsNoTracking().FirstAsync(c => c.Code == "PF-CUST")).Id;
            supId = (await ctx.Set<Supplier>().AsNoTracking().FirstAsync(s => s.Code == "PF-SUP")).Id;
            itemA = (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Code == "PF-A")).Id;
            itemB = (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Code == "PF-B")).Id;
            itemC = (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Code == "PF-C")).Id;
            log.Add("ids: wh=" + whId + " cust=" + custId + " sup=" + supId + " A=" + itemA + " B=" + itemB + " C=" + itemC);
        }

        // ── purchases ────────────────────────────────────────────────────────────────
        await using (var ctx = Ctx(cs))
        {
            var pi = await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = supId, WarehouseId = whId, InvoiceDate = today,
                InvoiceType = (int)PurchaseInvoiceType.Cash,
                Lines = new()
                {
                    new() { ItemId = itemA, Quantity = 40m, UnitCost = 10m },
                    new() { ItemId = itemB, Quantity = 10m, UnitCost = 18m }
                }
            });
            log.Add("P1_cash_purchase=" + pi.InvoiceNumber + " total=580.00 (A 40@10 + B 10@18)");
        }
        await using (var ctx = Ctx(cs))
        {
            var pi = await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = supId, WarehouseId = whId, InvoiceDate = today,
                InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                Lines = new() { new() { ItemId = itemA, Quantity = 10m, UnitCost = 12m } }
            });
            log.Add("P2_credit_purchase=" + pi.InvoiceNumber + " total=120.00 (A 10@12)");
            log.Add("expected_A_stock=70 expected_A_cost=10.0000");
            log.Add("expected_B_stock=20 expected_B_cost=19.0000");
        }
        await using (var ctx = Ctx(cs))
        {
            var pi = await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = supId, WarehouseId = whId, InvoiceDate = today,
                InvoiceType = (int)PurchaseInvoiceType.Cash, PaymentMethod = 2,
                CheckNumber = "PF-PCHK-1", CheckBankName = "بنك الإثبات", CheckDueDate = today.AddDays(30),
                Lines = new() { new() { ItemId = itemB, Quantity = 5m, UnitCost = 18m } }
            });
            log.Add("P3_purchase_by_check=" + pi.InvoiceNumber + " total=90.00 -> issued BankCheck");
        }

        // ── sales: cash / credit / card / check ──────────────────────────────────────
        await using (var ctx = Ctx(cs))
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = custId, WarehouseId = whId, InvoiceDate = today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemA, Quantity = 10m, UnitPrice = 15m } }
            });
            log.Add("S1_cash_sale=" + si.InvoiceNumber + " net=150.00 tax=24.00 total=174.00 cogs=100.00");
        }
        await using (var ctx = Ctx(cs))
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = custId, WarehouseId = whId, InvoiceDate = today,
                InvoiceType = (int)SalesInvoiceType.OnAccount, PaymentMethod = (int)SalesPaymentMethod.OnAccount, TaxRate = 16m,
                Lines = new()
                {
                    new() { ItemId = itemA, Quantity = 5m, UnitPrice = 15m },
                    new() { ItemId = itemB, Quantity = 3m, UnitPrice = 25m }
                }
            });
            log.Add("S2_credit_sale=" + si.InvoiceNumber + " net=150.00 tax=24.00 total=174.00 cogs=107.00 (A 5@10 + B 3@19)");
        }
        await using (var ctx = Ctx(cs))
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = custId, WarehouseId = whId, InvoiceDate = today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Card, TaxRate = 16m,
                CardApprovalCode = "PF-CARD-1", CardLast4 = "4242",
                Lines = new() { new() { ItemId = itemA, Quantity = 2m, UnitPrice = 15m } }
            });
            log.Add("S3_card_sale=" + si.InvoiceNumber + " net=30.00 tax=4.80 total=34.80 cogs=20.00");
        }
        await using (var ctx = Ctx(cs))
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = custId, WarehouseId = whId, InvoiceDate = today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Check, TaxRate = 16m,
                CheckNumber = "PF-CHK-1", CheckBankName = "بنك الإثبات", CheckDueDate = today.AddDays(30),
                Lines = new() { new() { ItemId = itemA, Quantity = 4m, UnitPrice = 15m } }
            });
            log.Add("S4_check_sale=" + si.InvoiceNumber + " net=60.00 tax=9.60 total=69.60 cogs=40.00 -> received BankCheck");
        }
        log.Add("expected_stock_after_sales: A=49.00 B=17.00");
        await File.AppendAllLinesAsync(OutFile(), log);
        Assert.True(File.Exists(OutFile()));
    }

    private async Task Seed_returns_and_vouchers()
    {
        var cs = Cs();
        var log = new List<string> { "=== RETURNS & VOUCHERS ===" };
        var today = DateTime.Today;
        Guid whId, custId, supId, itemA;
        await using (var ctx = Ctx(cs))
        {
            whId = (await ctx.Set<Warehouse>().AsNoTracking().FirstAsync(w => w.Code == "PF-WH")).Id;
            custId = (await ctx.Set<Customer>().AsNoTracking().FirstAsync(c => c.Code == "PF-CUST")).Id;
            supId = (await ctx.Set<Supplier>().AsNoTracking().FirstAsync(s => s.Code == "PF-SUP")).Id;
            itemA = (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Code == "PF-A")).Id;
        }
        Guid s1Id, s3Id, p1Id;
        await using (var ctx = Ctx(cs))
        {
            var invoices = await ctx.Set<SalesInvoice>().AsNoTracking().Where(i => !i.IsDeleted).ToListAsync();
            s1Id = invoices.First(i => i.PaymentMethod == SalesPaymentMethod.Cash).Id;
            s3Id = invoices.First(i => i.PaymentMethod == SalesPaymentMethod.Card).Id;
            p1Id = (await ctx.Set<PurchaseInvoice>().AsNoTracking().Where(i => !i.IsDeleted).OrderBy(i => i.InvoiceNumber).FirstAsync()).Id;
        }

        await using (var ctx = Ctx(cs))
        {
            var sr = await SalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = s1Id, WarehouseId = whId, ReturnDate = today,
                Lines = new() { new() { ItemId = itemA, Quantity = 3m, UnitPrice = 15m } }
            });
            log.Add("SR1_partial_sales_return=" + sr.ReturnNumber + " net=45.00 tax=7.20 total=52.20 cogs_reverse=30.00");
        }
        await using (var ctx = Ctx(cs))
        {
            var sr = await SalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = s3Id, WarehouseId = whId, ReturnDate = today,
                Lines = new() { new() { ItemId = itemA, Quantity = 2m, UnitPrice = 15m } }
            });
            log.Add("SR2_full_sales_return=" + sr.ReturnNumber + " net=30.00 tax=4.80 total=34.80 cogs_reverse=20.00");
        }
        await using (var ctx = Ctx(cs))
        {
            var pr = await PurchaseReturn(ctx).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = p1Id, WarehouseId = whId, ReturnDate = today,
                Lines = new() { new() { ItemId = itemA, Quantity = 5m, UnitCost = 10m } }
            });
            log.Add("PR1_partial_purchase_return=" + pr.ReturnNumber + " total=50.00");
            log.Add("expected_stock_after_returns: A=49.00 B=22.00");
        }

        Guid rvId;
        await using (var ctx = Ctx(cs))
        {
            var dto = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = custId, VoucherDate = today, PaymentMethod = 1, Amount = 100m, Description = "قبض من عميل الإثبات"
            });
            rvId = dto.Id;
            log.Add("RV1_draft=" + dto.VoucherNumber + " amount=100.00 entry_is_null=" + (dto.JournalEntryId is null));
        }
        await using (var ctx = Ctx(cs)) { await Receipts(ctx).ApproveAsync(rvId); }
        await using (var ctx = Ctx(cs))
        {
            var posted = await Receipts(ctx).PostAsync(rvId);
            log.Add("RV1_posted entry=" + (posted.JournalEntryId?.ToString() ?? "NULL"));
        }
        await using (var ctx = Ctx(cs))
        {
            var before = await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(e => !e.IsDeleted);
            try { await Receipts(ctx).PostAsync(rvId); log.Add("RV1_second_post=no-exception"); }
            catch (Exception ex) { log.Add("RV1_second_post=rejected:" + ex.GetType().Name); }
            var after = await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(e => !e.IsDeleted);
            log.Add("RV1_idempotency entries_before=" + before + " entries_after=" + after + " no_duplicate=" + (before == after));
        }
        await using (var ctx = Ctx(cs))
        {
            var dto = await Payments(ctx).CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = supId, VoucherDate = today, PaymentMethod = 1, Amount = 200m, BeneficiaryName = "مورد الإثبات"
            });
            log.Add("PV1_draft=" + dto.VoucherNumber + " amount=200.00");
            await Payments(ctx).ApproveAsync(dto.Id);
            var posted = await Payments(ctx).PostAsync(dto.Id);
            log.Add("PV1_posted entry=" + (posted.JournalEntryId?.ToString() ?? "NULL"));
        }
        await File.AppendAllLinesAsync(OutFile(), log);
        Assert.True(File.Exists(OutFile()));
    }

    private async Task Failure_injections_leave_zero_residue()
    {
        var cs = Cs();
        var log = new List<string> { "=== FAILURE INJECTIONS (mid-operation) ===" };
        var today = DateTime.Today;
        Guid whId, custId, supId, itemA, itemC, s1Id, p1Id;
        await using (var ctx = Ctx(cs))
        {
            whId = (await ctx.Set<Warehouse>().AsNoTracking().FirstAsync(w => w.Code == "PF-WH")).Id;
            custId = (await ctx.Set<Customer>().AsNoTracking().FirstAsync(c => c.Code == "PF-CUST")).Id;
            supId = (await ctx.Set<Supplier>().AsNoTracking().FirstAsync(s => s.Code == "PF-SUP")).Id;
            itemA = (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Code == "PF-A")).Id;
            itemC = (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Code == "PF-C")).Id;
            s1Id = (await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => !i.IsDeleted && i.PaymentMethod == SalesPaymentMethod.Cash)).Id;
            p1Id = (await ctx.Set<PurchaseInvoice>().AsNoTracking().Where(i => !i.IsDeleted).OrderBy(i => i.InvoiceNumber).FirstAsync()).Id;
        }

        // F1 — sale of more than the available stock
        await using (var ctx = Ctx(cs))
        {
            var before = await SnapshotAsync(ctx);
            try
            {
                await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
                {
                    CustomerId = custId, WarehouseId = whId, InvoiceDate = today,
                    InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                    Lines = new() { new() { ItemId = itemA, Quantity = 10000m, UnitPrice = 15m } }
                });
                log.Add("F1_insufficient_stock=NO-EXCEPTION(!!)");
            }
            catch (Exception ex) { log.Add("F1_insufficient_stock=rejected:" + ex.GetType().Name); }
            var after = await SnapshotAsync(ctx);
            log.Add("F1_before=" + before);
            log.Add("F1_after =" + after);
            log.Add("F1_no_residue=" + (before == after));
        }

        // F2 — mid-operation failure: inventory account disabled after the stock work is prepared
        await using (var ctx = Ctx(cs))
        {
            var acc = await ctx.Set<Account>().FirstAsync(a => a.Code == "1300");
            acc.IsActive = false;
            await ctx.SaveChangesAsync();
        }
        try
        {
            await using var ctx = Ctx(cs);
            var before = await SnapshotAsync(ctx);
            try
            {
                await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
                {
                    CustomerId = custId, WarehouseId = whId, InvoiceDate = today,
                    InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                    Lines = new() { new() { ItemId = itemA, Quantity = 1m, UnitPrice = 15m } }
                });
                log.Add("F2_midop_unmovable_account=NO-EXCEPTION(!!)");
            }
            catch (Exception ex) { log.Add("F2_midop_unmovable_account=rejected:" + ex.GetType().Name); }
            var after = await SnapshotAsync(ctx);
            log.Add("F2_before=" + before);
            log.Add("F2_after =" + after);
            log.Add("F2_no_residue=" + (before == after));
        }
        finally
        {
            await using var ctx = Ctx(cs);
            var acc = await ctx.Set<Account>().FirstAsync(a => a.Code == "1300");
            acc.IsActive = true;
            await ctx.SaveChangesAsync();
        }

        // F3 — sales return larger than the sold quantity
        await using (var ctx = Ctx(cs))
        {
            var before = await SnapshotAsync(ctx);
            try
            {
                await SalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
                {
                    SalesInvoiceId = s1Id, WarehouseId = whId, ReturnDate = today,
                    Lines = new() { new() { ItemId = itemA, Quantity = 8m, UnitPrice = 15m } }
                });
                log.Add("F3_over_return_sales=NO-EXCEPTION(!!)");
            }
            catch (Exception ex) { log.Add("F3_over_return_sales=rejected:" + ex.GetType().Name); }
            var after = await SnapshotAsync(ctx);
            log.Add("F3_no_residue=" + (before == after));
        }

        // F4 — purchase return larger than the purchased quantity
        await using (var ctx = Ctx(cs))
        {
            var before = await SnapshotAsync(ctx);
            try
            {
                await PurchaseReturn(ctx).CreateAsync(new CreatePurchaseReturnDto
                {
                    PurchaseInvoiceId = p1Id, WarehouseId = whId, ReturnDate = today,
                    Lines = new() { new() { ItemId = itemA, Quantity = 40m, UnitCost = 10m } }
                });
                log.Add("F4_over_return_purchase=NO-EXCEPTION(!!)");
            }
            catch (Exception ex) { log.Add("F4_over_return_purchase=rejected:" + ex.GetType().Name); }
            var after = await SnapshotAsync(ctx);
            log.Add("F4_no_residue=" + (before == after));
        }

        // F5 — batch-tracked item received while batch rows do not match stock movements
        await using (var ctx = Ctx(cs))
        {
            var before = await SnapshotAsync(ctx);
            try
            {
                await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
                {
                    SupplierId = supId, WarehouseId = whId, InvoiceDate = today,
                    InvoiceType = (int)PurchaseInvoiceType.Cash,
                    Lines = new() { new() { ItemId = itemC, Quantity = 2m, UnitCost = 7m, BatchNumber = "PF-B1", ExpiryDate = DateOnly.FromDateTime(today.AddMonths(6)) } }
                });
                log.Add("F5_batch_integrity=NO-EXCEPTION(!!)");
            }
            catch (Exception ex) { log.Add("F5_batch_integrity=rejected:" + ex.GetType().Name); }
            var after = await SnapshotAsync(ctx);
            log.Add("F5_no_residue=" + (before == after));
        }

        await using (var ctx = Ctx(cs)) { log.Add("FINAL_SNAPSHOT " + await SnapshotAsync(ctx)); }
        await File.AppendAllLinesAsync(OutFile(), log);
        Assert.True(File.Exists(OutFile()));
    }
}
