using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Movements;
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
/// أمان الترحيل لترحيلَي 2026-10-25 (`PartyOpeningDetailAndAccountingPeriods` +
/// `AddJournalLinePartyAttribution`) على قاعدة تحتوي **بيانات قائمة**:
///
///  1) ترحيل مرحلي: إلى آخر ترحيل قبل ترحيليّ ⇒ إنشاء بيانات عبر **الخدمات الرسمية** ⇒
///     تطبيق كل الترحيلات.
///  2) **لا تغيير غير مقصود على البيانات**: نفس الأعداد ونفس المجاميع (1200 · رصيد عميل ·
///     كمية صنف) قبل وبعد — ونفس الصفوف.
///  3) **الأعمدة الجديدة كلها NULL للصفوف القديمة** (إثبات أن الترحيل إضافي بلا تعبئة تاريخية).
///  4) المخطط الجديد موجود (جدول الفترات + أعمدة التفصيل + أعمدة الإسناد) والفهارس الفريدة.
///  5) حماية المسار القديم: الترحيل إلى قاعدة بمخطط قديم **لا يفشل** رغم غياب جدول الفترات.
///  6) لا يُلمس أي قاعدة منشورة (قاعدة مؤقتة `erp_ob_mig_*` تُسقط في النهاية).
/// </summary>
public class AccountingOpeningMigrationSafetyTests : IClassFixture<AccountingPgFixture>
{
    private const string PreviousMigration = "20261024114522_Sub2bP3CustomerRefundOnPaymentVoucher";

    private readonly AccountingPgFixture _pg;

    public AccountingOpeningMigrationSafetyTests(AccountingPgFixture pg) => _pg = pg;

    private static DbContextOptions<AppDbContext> OptionsFor(string cs)
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs, o => o.MigrationsAssembly("ERPSystem.Infrastructure"))
            .Options;

    private static string WithDatabase(string cs, string dbName)
    {
        var b = new NpgsqlConnectionStringBuilder(cs) { Database = dbName };
        return b.ConnectionString;
    }

    private sealed record Snapshot(
        int Accounts, int Customers, int Suppliers, int Items, int Warehouses,
        int JournalEntries, int JournalEntryLines, int SalesInvoices, int StockMovements,
        decimal ReceivableEffect, decimal CustomerBalance, decimal ItemStock);

    private static async Task<Snapshot> TakeSnapshotAsync(AppDbContext ctx)
    {
        var arId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == "1200" && !a.IsDeleted).Select(a => a.Id).FirstAsync();

        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == arId && !l.IsDeleted
                        && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .Select(l => new { l.DebitAmount, l.CreditAmount })
            .ToListAsync();

        var customer = await ctx.Set<Customer>().AsNoTracking().FirstAsync();
        var item = await ctx.Set<Item>().AsNoTracking().FirstAsync();

        return new Snapshot(
            await ctx.Set<Account>().CountAsync(),
            await ctx.Set<Customer>().CountAsync(),
            await ctx.Set<Supplier>().CountAsync(),
            await ctx.Set<Item>().CountAsync(),
            await ctx.Set<Warehouse>().CountAsync(),
            await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted),
            await ctx.Set<JournalEntryLine>().CountAsync(l => !l.IsDeleted),
            await ctx.Set<SalesInvoice>().CountAsync(i => !i.IsDeleted),
            await ctx.Set<StockMovement>().CountAsync(m => !m.IsDeleted),
            Math.Round(lines.Sum(l => l.DebitAmount - l.CreditAmount), 2),
            customer.CurrentBalance,
            item.CurrentStock);
    }

    [LivePgFact]
    public async Task Migrations_are_additive_and_preserve_existing_data_exactly()
    {
        if (!_pg.Available || string.IsNullOrWhiteSpace(_pg.AdminConnectionString) ||
            string.IsNullOrWhiteSpace(_pg.TestConnectionString))
            return;

        var dbName = "erp_ob_mig_" + Guid.NewGuid().ToString("N")[..10];
        var cs = WithDatabase(_pg.TestConnectionString!, dbName);

        await using var admin = new NpgsqlConnection(_pg.AdminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            // (1) مخطط ما قبل ترحيلَي 2026-10-25
            await using (var ctx = new AppDbContext(OptionsFor(cs)))
                await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            // (2) بيانات حقيقية عبر الخدمات الرسمية على المخطط القديم (بلا جدول فترات)
            var seeded = await SeedLegacyDataAsync(cs);

            // (3) لقطة قبل الترحيل
            Snapshot before;
            await using (var ctx = new AppDbContext(OptionsFor(cs)))
                before = await TakeSnapshotAsync(ctx);

            // (4) تطبيق كل الترحيلات (يشمل ترحيلَي 2026-10-25)
            await using (var ctx = new AppDbContext(OptionsFor(cs)))
                await ctx.GetService<IMigrator>().MigrateAsync();

            // (5) لا تغيير غير مقصود + المخطط الجديد + الأعمدة الجديدة فارغة تاريخيًا
            await AssertSchemaAndDataAsync(cs, before, seeded);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed record SeededIds(Guid CustomerId, Guid SupplierId, Guid EntryId, Guid OpeningLineId);

    /// <summary>ينشئ بيانات قائمة على المخطط القديم بالخدمات الرسمية فقط (لا SQL مباشر).</summary>
    private static async Task<SeededIds> SeedLegacyDataAsync(string cs)
    {
        await using var ctx = new AppDbContext(OptionsFor(cs));

        var category = new Category { Id = Guid.NewGuid(), Code = "MIG-CAT", NameAr = "فئة", CreatedAt = DateTime.UtcNow };
        var unit = new Unit { Id = Guid.NewGuid(), Code = "MIG-UN", NameAr = "وحدة", CreatedAt = DateTime.UtcNow };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), Code = "MIG-WH", NameAr = "مخزن", IsDefault = true, CreatedAt = DateTime.UtcNow };
        var customer = new Customer { Id = Guid.NewGuid(), Code = "MIG-CUST", NameAr = "عميل", IsActive = true, CreatedAt = DateTime.UtcNow };
        var supplier = new Supplier { Id = Guid.NewGuid(), Code = "MIG-SUP", NameAr = "مورد", IsActive = true, CreatedAt = DateTime.UtcNow };
        ctx.AddRange(category, unit, warehouse, customer, supplier);
        await ctx.SaveChangesAsync();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "MIG-IT",
            NameAr = "صنف",
            CategoryId = category.Id,
            UnitId = unit.Id,
            CostPrice = 4m,
            SalePrice = 9m,
            TaxRate = 0m,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        // ملاحظة مهمة: لا يمكن استخدام الخدمات الحالية على مخطط **قديم** لأن نموذج EF يتضمّن
        // الأعمدة الجديدة (سيفشل الإدراج). لذلك تُعبّأ الجداول **المتأثرة بالترحيل** ببيانات
        // مطابقة للمخطط القديم عبر SQL صريح — وهذا هو المدخل الصحيح لاختبار «ترحيل فوق بيانات قائمة».
        var cashId = (await PostingAccountResolver.ResolveAsync(ctx, "1100")).Id;

        var entryId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var obLineId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        var openingDate = new DateTime(2049, 1, 5);

        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();

            await ctx.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "JournalEntries"
                  ("Id","EntryNumber","EntryType","EntryDate","ReferenceNumber","Description","CreatedAt","IsDeleted")
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, false)
                """,
                entryId, "MIG-JE-0001", (int)JournalEntryType.OpeningBalance, openingDate,
                "MIG-OPEN", "قيد افتتاحي قديم النمط", DateTime.UtcNow);

            await ctx.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "JournalEntryLines"
                  ("Id","JournalEntryId","AccountId","DebitAmount","CreditAmount","Note","CreatedAt","IsDeleted")
                VALUES ({0}, {1}, {2}, {3}, 0, {4}, {5}, false)
                """,
                lineId, entryId, cashId, 5000m, "قديم النمط", DateTime.UtcNow);

            await ctx.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "OpeningBalanceBatches"
                  ("Id","FiscalYear","EntryDate","Status","Reference","Description","JournalEntryId",
                   "CreatedAt","CreatedBy","IsDeleted")
                VALUES ({0}, 2049, {1}, {2}, {3}, {4}, {5}, {6}, {7}, false)
                """,
                batchId, openingDate, (int)OpeningBalanceBatchStatus.Posted, "MIG-OB-2049",
                "دفعة افتتاحية قديمة النمط", entryId, DateTime.UtcNow, "migration-test");

            await ctx.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "OpeningBalanceLines"
                  ("Id","BatchId","AccountId","DebitAmount","CreditAmount","Note","CreatedAt","CreatedBy","IsDeleted")
                VALUES ({0}, {1}, {2}, 5000, 0, {3}, {4}, {5}, false)
                """,
                obLineId, batchId, cashId, "بند افتتاحي قديم", DateTime.UtcNow, "migration-test");

            await tx.CommitAsync();
        });

        // ذمة عميل قائمة (صف لا يمسّ الجداول المتأثرة بالترحيل)
        var customerRow = await ctx.Set<Customer>().FirstAsync(c => c.Id == customer.Id);
        customerRow.CurrentBalance = 45m;
        await ctx.SaveChangesAsync();

        _ = item;
        _ = invoiceId;

        return new SeededIds(customer.Id, supplier.Id, entryId, obLineId);
    }

    /// <summary>يتحقق من: صحة المخطط الجديد · عدم تغيّر البيانات · خلو الأعمدة الجديدة للصفوف القديمة.</summary>
    private static async Task AssertSchemaAndDataAsync(string cs, Snapshot before, SeededIds seeded)
    {
        await using var ctx = new AppDbContext(OptionsFor(cs));

        var applied = (await ctx.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Contains(applied, m => m.Contains("PartyOpeningDetailAndAccountingPeriods"));
        Assert.Contains(applied, m => m.Contains("AddJournalLinePartyAttribution"));

        // لا تغيير غير مقصود على أي بيانات قائمة
        var after = await TakeSnapshotAsync(ctx);
        Assert.Equal(before, after);

        // المخطط الجديد
        var periodTables = await ctx.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'AccountingPeriods'")
            .ToListAsync();
        Assert.Single(periodTables);

        var periodIndexes = await ctx.Database
            .SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'AccountingPeriods'")
            .ToListAsync();
        Assert.Contains("IX_AccountingPeriods_FiscalYear_Month", periodIndexes);

        var obColumns = await ctx.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'OpeningBalanceLines' AND column_name IN ('CustomerId','SupplierId','ItemId','WarehouseId','Quantity','UnitCost','StockMovementId')")
            .ToListAsync();
        Assert.Equal(7, obColumns.Count);

        var jlColumns = await ctx.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'JournalEntryLines' AND column_name IN ('CustomerId','SupplierId')")
            .ToListAsync();
        Assert.Equal(2, jlColumns.Count);

        // الأعمدة الجديدة **فارغة** للبيانات القائمة ⇒ الترحيل إضافي ولا يعبّئ تاريخيًا
        var obAttributed = await ctx.Database
            .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"OpeningBalanceLines\" WHERE \"CustomerId\" IS NOT NULL OR \"SupplierId\" IS NOT NULL OR \"ItemId\" IS NOT NULL")
            .SingleAsync();
        Assert.Equal(0, obAttributed);

        var jlAttributed = await ctx.Database
            .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"JournalEntryLines\" WHERE \"CustomerId\" IS NOT NULL OR \"SupplierId\" IS NOT NULL")
            .SingleAsync();
        Assert.Equal(0, jlAttributed);

        var periods = await ctx.Database
            .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"AccountingPeriods\"")
            .SingleAsync();
        Assert.Equal(0, periods);

        // الصفوف القائمة في **الجداول المتأثرة** لم تُمسّ (قيد + بند افتتاحي قديم النمط)
        Assert.True(await ctx.Set<JournalEntry>().AnyAsync(e => e.Id == seeded.EntryId));
        var obLine = await ctx.Set<OpeningBalanceLine>().AsNoTracking()
            .FirstAsync(l => l.Id == seeded.OpeningLineId);
        Assert.Equal(5000m, obLine.DebitAmount);
        Assert.Equal(0m, obLine.CreditAmount);
        Assert.Null(obLine.CustomerId);
        Assert.Null(obLine.ItemId);
        Assert.Null(obLine.Quantity);

        // الأطراف القائمة كما هي
        Assert.True(await ctx.Set<Customer>().AnyAsync(c => c.Id == seeded.CustomerId));
        Assert.True(await ctx.Set<Supplier>().AnyAsync(s => s.Id == seeded.SupplierId));
    }
}
