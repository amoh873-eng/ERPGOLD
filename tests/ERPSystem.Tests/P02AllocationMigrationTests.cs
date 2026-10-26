using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P0-2 — التحقق من ترحيل `P02CustomerPaymentAllocation` على **قاعدة اختبار فارغة** عبر المسار الرسمي،
/// مع إثبات **عدم فقد البيانات** الموجودة قبل الترحيل (ترحيل مرحلي: السابق ⇒ بيانات ⇒ الأحدث).
/// لا يُلمس أي قاعدة إنتاج.
/// </summary>
public class P02AllocationMigrationTests : IClassFixture<AccountingPgFixture>
{
    private const string PreviousMigration = "20261022192835_Pu04SupplierPaymentAllocation";

    private readonly AccountingPgFixture _pg;

    public P02AllocationMigrationTests(AccountingPgFixture pg) => _pg = pg;

    private static DbContextOptions<AppDbContext> OptionsFor(string cs)
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs, o => o.MigrationsAssembly("ERPSystem.Infrastructure"))
            .Options;

    private static string WithDatabase(string cs, string dbName)
    {
        var b = new NpgsqlConnectionStringBuilder(cs) { Database = dbName };
        return b.ConnectionString;
    }

    [LivePgFact]
    public async Task Migration_applies_on_empty_db_and_preserves_existing_data()
    {
        if (!_pg.Available || string.IsNullOrWhiteSpace(_pg.AdminConnectionString) ||
            string.IsNullOrWhiteSpace(_pg.TestConnectionString))
            return;

        var dbName = "erp_p02_mig_" + Guid.NewGuid().ToString("N")[..10];
        var cs = WithDatabase(_pg.TestConnectionString!, dbName);

        await using var admin = new NpgsqlConnection(_pg.AdminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            // (1) ترحيل إلى آخر ترحيل قَبل ترحيل التخصيص ⇒ قاعدة فارغة بمخطط سابق.
            await using (var ctx = new AppDbContext(OptionsFor(cs)))
                await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            // (2) بيانات موجودة قبل الترحيل الجديد (عميل + صنف + قيد محاسبي).
            //
            // ملاحظة تحديث 2026-10-25 (ضرورية وموثَّقة): كانت هذه الخطوة تُنشئ **فاتورة بيع عبر
            // الخدمة الرسمية**، لكن نموذج EF الحالي يتضمّن أعمدة أُضيفت في ترحيلات **لاحقة** لهذا
            // الترحيل (`JournalEntryLines.CustomerId/SupplierId`) ⇒ كتابة الفاتورة على مخطط قديم
            // تفشل بحق (42703) لأن الشيفرة تسبق المخطط، وهو قيد طبيعي في EF Core.
            // الحل: يُنشأ **الأثر المحاسبي المطلوب للقياس** (قيد Dr 1200 / Cr 4100) بـ SQL صريح
            // مطابق للمخطط القديم ⇒ نفس الغرض (بيانات قائمة قبل الترحيل تُقاس قبل/بعد) بلا كسر.
            Guid customerId, seededEntryId;
            await using (var ctx = new AppDbContext(OptionsFor(cs)))
            {
                var customer = new Customer
                {
                    Id = Guid.NewGuid(), Code = "MIG-" + Guid.NewGuid().ToString("N")[..5],
                    NameAr = "عميل ترحيل", IsActive = true, CreatedAt = DateTime.UtcNow
                };
                ctx.Add(customer);
                await ctx.SaveChangesAsync();
                customerId = customer.Id;

                var category = await ctx.Set<Category>().AsNoTracking().FirstAsync();
                var unit = await ctx.Set<Unit>().AsNoTracking().FirstAsync();
                var warehouse = await ctx.Set<Warehouse>().AsNoTracking().FirstAsync();
                var item = new Item
                {
                    Id = Guid.NewGuid(), Code = "MIG-IT-" + Guid.NewGuid().ToString("N")[..5],
                    NameAr = "صنف ترحيل", CategoryId = category.Id, UnitId = unit.Id,
                    CostPrice = 5m, SalePrice = 10m, CurrentStock = 100m, IsActive = true,
                    TracksExpiry = false, MinStockLevel = 0m, CreatedAt = DateTime.UtcNow
                };
                ctx.Add(item);
                await ctx.SaveChangesAsync();

                // رصيد افتتاحي عبر الخدمة الرسمية (شرط توفر المخزون لإنشاء فاتورة البيع).
                await new StockMovementService(ctx, new AccountingPgFixture.AllowAllManualPrice()).CreateAsync(
                    new ERPSystem.Application.DTOs.Movements.CreateStockMovementDto
                    {
                        ItemId = item.Id,
                        WarehouseId = warehouse.Id,
                        MovementType = (int)MovementType.OpeningBalance,
                        Quantity = 100m,
                        UnitCost = 5m,
                        MovementDate = DateTime.Today,
                        ReferenceNumber = "P02-MIG-SEED"
                    });

                // قيد محاسبي قائم (Dr 1200 / Cr 4100) بـ SQL صريح مطابق للمخطط القديم.
                seededEntryId = Guid.NewGuid();
                var seededLineId = Guid.NewGuid();
                var arId = (await PostingAccountResolver.ResolveAsync(ctx, "1200")).Id;
                var revenueId = (await PostingAccountResolver.ResolveAsync(ctx, "4100")).Id;

                await ctx.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "JournalEntries"
                      ("Id","EntryNumber","EntryType","EntryDate","ReferenceNumber","Description","CreatedAt","IsDeleted")
                    VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, false)
                    """,
                    seededEntryId, "P02-MIG-JE", (int)JournalEntryType.SalesInvoice, DateTime.Today,
                    "P02-MIG", "قيد قائم قبل الترحيل", DateTime.UtcNow);

                await ctx.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "JournalEntryLines"
                      ("Id","JournalEntryId","AccountId","DebitAmount","CreditAmount","Note","CreatedAt","IsDeleted")
                    VALUES ({0}, {1}, {2}, 100, 0, {3}, {4}, false)
                    """,
                    seededLineId, seededEntryId, arId, "مدين ذمم", DateTime.UtcNow);

                await ctx.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "JournalEntryLines"
                      ("Id","JournalEntryId","AccountId","DebitAmount","CreditAmount","Note","CreatedAt","IsDeleted")
                    VALUES ({0}, {1}, {2}, 0, 100, {3}, {4}, false)
                    """,
                    Guid.NewGuid(), seededEntryId, revenueId, "دائن إيراد", DateTime.UtcNow);
            }

            int invoicesBefore, entriesBefore, accountsBefore;
            decimal arBefore;
            await using (var ctx = new AppDbContext(OptionsFor(cs)))
            {
                invoicesBefore = await ctx.Set<SalesInvoice>().CountAsync();
                entriesBefore = await ctx.Set<JournalEntry>().CountAsync();
                accountsBefore = await ctx.Set<Account>().CountAsync();
                var arId = await ctx.Set<Account>().AsNoTracking()
                    .Where(a => a.Code == "1200" && !a.IsDeleted).Select(a => a.Id).FirstAsync();
                arBefore = await ctx.Set<JournalEntryLine>().AsNoTracking()
                    .Where(l => !l.IsDeleted && l.AccountId == arId)
                    .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
            }

            // (3) ترحيل إلى الأحدث (يشمل ترحيل التخصيص) على قاعدة تحتوي بيانات.
            await using (var ctx = new AppDbContext(OptionsFor(cs)))
                await ctx.GetService<IMigrator>().MigrateAsync();

            // (4) الجدول الجديد + قيوده + فهارسه موجودة، والبيانات السابقة لم تُفقد ولم تتغير.
            await using (var ctx = new AppDbContext(OptionsFor(cs)))
            {
                var applied = (await ctx.Database.GetAppliedMigrationsAsync()).ToList();
                Assert.Contains(applied, m => m.Contains("P02CustomerPaymentAllocation"));

                var tables = await ctx.Database
                    .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'CustomerPaymentAllocations'")
                    .ToListAsync();
                Assert.Single(tables);

                var checks = await ctx.Database
                    .SqlQueryRaw<string>("SELECT constraint_name AS \"Value\" FROM information_schema.table_constraints WHERE table_name = 'CustomerPaymentAllocations' AND constraint_type = 'CHECK'")
                    .ToListAsync();
                Assert.Contains("ck_cpa_amount_positive", checks);
                Assert.Contains("ck_cpa_direction", checks);
                Assert.Contains("ck_cpa_reversal_shape", checks);

                var indexes = await ctx.Database
                    .SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'CustomerPaymentAllocations'")
                    .ToListAsync();
                Assert.Contains("UX_CustomerPaymentAllocations_IdempotencyKey", indexes);
                Assert.Contains("IX_CustomerPaymentAllocations_Invoice_Direction", indexes);

                // لا فقد بيانات: نفس الأعداد ونفس رصيد الذمم ونفس المستندات.
                Assert.Equal(invoicesBefore, await ctx.Set<SalesInvoice>().CountAsync());
                Assert.Equal(entriesBefore, await ctx.Set<JournalEntry>().CountAsync());
                Assert.Equal(accountsBefore, await ctx.Set<Account>().CountAsync());
                Assert.True(await ctx.Set<JournalEntry>().AnyAsync(e => e.Id == seededEntryId));
                // الأثر المحاسبي القائم (Dr 1200 = 100) محفوظ كما هو قبل/بعد الترحيل
                Assert.Equal(100m, arBefore);
                Assert.True(await ctx.Set<Customer>().AnyAsync(c => c.Id == customerId));

                var arId = await ctx.Set<Account>().AsNoTracking()
                    .Where(a => a.Code == "1200" && !a.IsDeleted).Select(a => a.Id).FirstAsync();
                var arAfter = await ctx.Set<JournalEntryLine>().AsNoTracking()
                    .Where(l => !l.IsDeleted && l.AccountId == arId)
                    .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
                Assert.Equal(arBefore, arAfter);

                // الجدول الجديد فارغ (لا تعبئة تاريخية).
                Assert.Equal(0, await ctx.Set<CustomerPaymentAllocation>().CountAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
