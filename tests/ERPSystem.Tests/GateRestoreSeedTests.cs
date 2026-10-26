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
/// يعمل فقط عند تعيين ERPSYSTEM_GATE_DB (قاعدة بوابة قابلة للإتلاف)، ويرفض صراحةً قاعدة النسخة التجارية.
/// </summary>
public sealed class GateDbFactAttribute : FactAttribute
{
    public const string EnvVar = "ERPSYSTEM_GATE_DB";
    public GateDbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"يتطلب قاعدة بوابة قابلة للإتلاف عبر {EnvVar} (اختبار استرجاع — Development فقط).";
    }
}

/// <summary>
/// المرحلة 2-ج: يجهّز بيانات أعمال حقيقية (مستندات + قيود + حركات + أرصدة) في قاعدة بوابة
/// عبر **طبقة الخدمات** نفسها (لا SQL يدوي)، لتُستخدم في اختبار النسخ الاحتياطي والاسترجاع.
/// </summary>
public class GateRestoreSeedTests
{
    static GateRestoreSeedTests() { AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true); }

    private static AppDbContext NewContext(string connectionString)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsAssembly("ERPSystem.Infrastructure")).Options);

    private static string Conn()
    {
        var cs = Environment.GetEnvironmentVariable(GateDbFactAttribute.EnvVar)!;
        if (cs.Contains("commercial", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("رفض: قاعدة البوابة لا يجوز أن تكون قاعدة النسخة التجارية.");
        return cs;
    }

    private static SalesInvoiceService Sales(AppDbContext c) => new(c, new JournalEntryService(c), new AccountingPgFixture.AllowAllManualPrice());
    private static PurchaseInvoiceService Purchase(AppDbContext c) => new(c, new JournalEntryService(c), new AccountingPgFixture.AllowAllManualPrice());
    private static SalesReturnService SalesReturn(AppDbContext c) => new(c, new JournalEntryService(c));
    private static PurchaseReturnService PurchaseReturn(AppDbContext c) => new(c, new JournalEntryService(c));

    [GateDbFact]
    public async Task Seed_gate_database_with_real_posted_documents()
    {
        var cs = Conn();
        var outFile = Environment.GetEnvironmentVariable("ERPSYSTEM_GATE_OUT") ?? "gate-seed-summary.txt";
        var log = new List<string>();

        Guid warehouseId, customerId, supplierId, itemAId, itemBId;
        await using (var ctx = NewContext(cs))
        {
            var wh = await ctx.Set<Warehouse>().FirstOrDefaultAsync(w => w.Code == "GATE-WH");
            if (wh is null)
            {
                wh = new Warehouse { Id = Guid.NewGuid(), Code = "GATE-WH", NameAr = "مخزن البوابة", IsDefault = false, CreatedAt = DateTime.UtcNow };
                ctx.Add(wh);
                await ctx.SaveChangesAsync();
            }
            var cat = await ctx.Set<Category>().FirstOrDefaultAsync(c => c.Code == "GATE-CAT")
                      ?? new Category { Id = Guid.NewGuid(), Code = "GATE-CAT", NameAr = "فئة البوابة", CreatedAt = DateTime.UtcNow };
            var unit = await ctx.Set<Unit>().FirstOrDefaultAsync(u => u.Code == "GATE-UN")
                       ?? new Unit { Id = Guid.NewGuid(), Code = "GATE-UN", NameAr = "وحدة البوابة", CreatedAt = DateTime.UtcNow };
            var cust = await ctx.Set<Customer>().FirstOrDefaultAsync(c => c.Code == "GATE-CUST")
                       ?? new Customer { Id = Guid.NewGuid(), Code = "GATE-CUST", NameAr = "عميل البوابة", IsActive = true, CreatedAt = DateTime.UtcNow };
            var sup = await ctx.Set<Supplier>().FirstOrDefaultAsync(s => s.Code == "GATE-SUP")
                      ?? new Supplier { Id = Guid.NewGuid(), Code = "GATE-SUP", NameAr = "مورد البوابة", IsActive = true, CreatedAt = DateTime.UtcNow };
            ctx.AddRange(cat, unit, cust, sup);
            await ctx.SaveChangesAsync();

            var itemA = await ctx.Set<Item>().FirstOrDefaultAsync(i => i.Code == "GATE-ITM-A");
            if (itemA is null)
            {
                itemA = new Item { Id = Guid.NewGuid(), Code = "GATE-ITM-A", NameAr = "صنف البوابة أ", CategoryId = cat.Id, UnitId = unit.Id, SalePrice = 15m, CostPrice = 10m, CurrentStock = 0m, IsActive = true, CreatedAt = DateTime.UtcNow };
                ctx.Add(itemA);
            }
            var itemB = await ctx.Set<Item>().FirstOrDefaultAsync(i => i.Code == "GATE-ITM-B");
            if (itemB is null)
            {
                itemB = new Item { Id = Guid.NewGuid(), Code = "GATE-ITM-B", NameAr = "صنف البوابة ب", CategoryId = cat.Id, UnitId = unit.Id, SalePrice = 25m, CostPrice = 18m, CurrentStock = 0m, IsActive = true, CreatedAt = DateTime.UtcNow };
                ctx.Add(itemB);
            }
            await ctx.SaveChangesAsync();

            warehouseId = wh.Id; customerId = cust.Id; supplierId = sup.Id; itemAId = itemA.Id; itemBId = itemB.Id;
            log.Add($"master warehouse={wh.Code} customer={cust.Code} supplier={sup.Code} itemA={itemA.Code} itemB={itemB.Code}");
        }

        // 1) فاتورة مشتريات نقدية (تُنشئ المخزون والتكلفة)
        Guid purchaseId;
        await using (var ctx = NewContext(cs))
        {
            var pi = await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = supplierId, WarehouseId = warehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)PurchaseInvoiceType.Cash, Note = "GATE purchase",
                Lines = new()
                {
                    new() { ItemId = itemAId, Quantity = 40m, UnitCost = 10m },
                    new() { ItemId = itemBId, Quantity = 20m, UnitCost = 18m }
                }
            });
            purchaseId = pi.Id;
            log.Add($"purchase_invoice={pi.InvoiceNumber} total={pi.TotalAmount}");
        }

        // 2) فاتورة مبيعات نقدية + فاتورة مبيعات آجلة
        Guid salesId;
        await using (var ctx = NewContext(cs))
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customerId, WarehouseId = warehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemAId, Quantity = 10m, UnitPrice = 15m } }
            });
            salesId = si.Id;
            log.Add($"sales_invoice_cash={si.InvoiceNumber} total={si.TotalAmount}");
        }
        await using (var ctx = NewContext(cs))
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customerId, WarehouseId = warehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount, PaymentMethod = (int)SalesPaymentMethod.OnAccount, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemBId, Quantity = 5m, UnitPrice = 25m } }
            });
            log.Add($"sales_invoice_credit={si.InvoiceNumber} total={si.TotalAmount}");
        }

        // 3) مردود مبيعات جزئي + مردود مشتريات جزئي
        await using (var ctx = NewContext(cs))
        {
            var sr = await SalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = salesId, WarehouseId = warehouseId, ReturnDate = DateTime.Today,
                Lines = new() { new() { ItemId = itemAId, Quantity = 4m, UnitPrice = 15m } }
            });
            log.Add($"sales_return={sr.ReturnNumber} total={sr.TotalAmount}");
        }
        await using (var ctx = NewContext(cs))
        {
            var pr = await PurchaseReturn(ctx).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = purchaseId, WarehouseId = warehouseId, ReturnDate = DateTime.Today,
                Lines = new() { new() { ItemId = itemAId, Quantity = 5m, UnitCost = 10m } }
            });
            log.Add($"purchase_return={pr.ReturnNumber} total={pr.TotalAmount}");
        }

        // ملخّص محاسبي (يُقارن قبل/بعد الاسترجاع)
        await using (var ctx = NewContext(cs))
        {
            var inv = await ctx.Set<SalesInvoice>().AsNoTracking().CountAsync(x => !x.IsDeleted);
            var pinv = await ctx.Set<PurchaseInvoice>().AsNoTracking().CountAsync(x => !x.IsDeleted);
            var sret = await ctx.Set<SalesReturn>().AsNoTracking().CountAsync(x => !x.IsDeleted);
            var pret = await ctx.Set<PurchaseReturn>().AsNoTracking().CountAsync(x => !x.IsDeleted);
            var entries = await ctx.Set<JournalEntry>().AsNoTracking().CountAsync(x => !x.IsDeleted);
            var mov = await ctx.Set<StockMovement>().AsNoTracking().CountAsync(x => !x.IsDeleted);
            var debit = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted).SumAsync(l => (decimal?)l.DebitAmount) ?? 0m;
            var credit = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted).SumAsync(l => (decimal?)l.CreditAmount) ?? 0m;
            log.Add($"counts sales={inv} purchases={pinv} sales_returns={sret} purchase_returns={pret} entries={entries} movements={mov}");
            log.Add($"ledger debit={debit} credit={credit} net={debit - credit}");
        }

        await File.WriteAllLinesAsync(outFile, log);
        Assert.True(File.Exists(outFile));
    }
}
