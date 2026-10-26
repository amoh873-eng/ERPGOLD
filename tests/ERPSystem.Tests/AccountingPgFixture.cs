using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Theory لا يعمل إلا عند توفر PostgreSQL تطويرية (نفس متغير <see cref="AccountingPgFixture"/>)،
/// وإلا يُبلَّغ عنه كاختبار متخطّى بدل الفشل — نفس نمط LivePgFactAttribute.
/// </summary>
public sealed class LivePgTheoryAttribute : TheoryAttribute
{
    public LivePgTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AccountingPgFixture.EnvVar)))
            Skip = $"يتطلب قاعدة PostgreSQL تطويرية عبر {AccountingPgFixture.EnvVar} (اختبار تكامل حي).";
    }
}

/// <summary>
/// Phase A — بيئة اختبار تكامل حي: قاعدة PostgreSQL مؤقتة تُنشأ وتُحذف ذاتيًا على خادم التطوير
/// (تُحدَّد عبر ERPSYSTEM_TEST_PG). تُطبَّق الترحيلات (وبها شجرة الحسابات المزروعة)، ثم تُزرع
/// بيانات مرجعية (فئة/وحدة/مخزن/عميل/مورد). لا تُلمس أي قاعدة منشورة.
/// </summary>
public sealed class AccountingPgFixture : IAsyncLifetime
{
    public const string EnvVar = "ERPSYSTEM_TEST_PG";

    public bool Available { get; private set; }
    public string? AdminConnectionString { get; private set; }
    public string? TestConnectionString { get; private set; }

    public Guid CategoryId { get; private set; }
    public Guid UnitId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid SupplierId { get; private set; }

    /// <summary>كود العميل المزروع في كل قاعدة اختبار — يُستخدم في بنود الأرصدة الافتتاحية للرقابي 1200.</summary>
    public const string CustomerCode = "A5-CUST";

    /// <summary>كود المورد المزروع في كل قاعدة اختبار — يُستخدم في بنود الأرصدة الافتتاحية للرقابي 2200.</summary>
    public const string SupplierCode = "A5-SUP";

    /// <summary>كود المخزن المزروع في كل قاعدة اختبار — يُستخدم في بنود الأرصدة الافتتاحية للمخزون 1300.</summary>
    public const string WarehouseCode = "A5-WH";

    /// <summary>مصدر تصريح كامل (لتجهيز البيانات فقط — لا علاقة له بفحوصات الصلاحيات).</summary>
    public sealed class AllowAllManualPrice : IManualPriceAuthorization
    {
        public bool CanEditManualPrice(string moduleKey) => true;
    }

    /// <summary>تصريح كامل لعمليات الأرصدة الافتتاحية (لاختبار المسار المحاسبي وحده).</summary>
    public sealed class AllowAllOpeningBalance : IOpeningBalanceAuthorization
    {
        public bool CanCreate() => true;
        public bool CanUpdate() => true;
        public bool CanPost() => true;
        public bool CanReverse() => true;
        public bool CanDelete() => true;
    }

    /// <summary>تصريح رافض لعمليات الأرصدة الافتتاحية (لفحص الفشل المغلق عند غياب الصلاحية).</summary>
    public sealed class DenyAllOpeningBalance : IOpeningBalanceAuthorization
    {
        public bool CanCreate() => false;
        public bool CanUpdate() => false;
        public bool CanPost() => false;
        public bool CanReverse() => false;
        public bool CanDelete() => false;
    }

    /// <summary>خدمة الأرصدة الافتتاحية بسياق معطى (authorization = null ⇒ تصريح كامل).</summary>
    public static OpeningBalanceService NewOpeningBalanceService(
        AppDbContext ctx, IOpeningBalanceAuthorization? authorization = null)
        => new(ctx, new JournalEntryService(ctx), authorization ?? new AllowAllOpeningBalance());

    static AccountingPgFixture()
    {
        // نفس إعداد مضيف Web لهذا التطبيق (DateTime محلي مع أعمدة timestamptz).
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    /// <summary>
    /// سياق جديد. retryOnFailure = true يحاكي سلوك الإنتاج
    /// (DependencyInjection يُفعّل EnableRetryOnFailure) لاختبار سلامة إعادة المحاولة.
    /// </summary>
    public AppDbContext NewContext(bool retryOnFailure = false)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(TestConnectionString, o =>
            {
                o.MigrationsAssembly("ERPSystem.Infrastructure");
                if (retryOnFailure) o.EnableRetryOnFailure(maxRetryCount: 3);
            });
        return new AppDbContext(builder.Options);
    }

    public async Task InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(baseConnection)) { Available = false; return; }

        var builder = new NpgsqlConnectionStringBuilder(baseConnection);
        builder.Database = "postgres";
        AdminConnectionString = builder.ConnectionString;

        var dbName = "erp_a5_" + Guid.NewGuid().ToString("N")[..10];
        builder.Database = dbName;
        TestConnectionString = builder.ConnectionString;

        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        await using (var ctx = NewContext())
            await ctx.Database.MigrateAsync();

        await using (var ctx = NewContext())
        {
            var category = new Category { Id = Guid.NewGuid(), Code = "A5-CAT", NameAr = "فئة A5", CreatedAt = DateTime.UtcNow };
            var unit = new Unit { Id = Guid.NewGuid(), Code = "A5-UN", NameAr = "وحدة A5", CreatedAt = DateTime.UtcNow };
            var warehouse = new Warehouse { Id = Guid.NewGuid(), Code = WarehouseCode, NameAr = "مخزن A5", IsDefault = true, CreatedAt = DateTime.UtcNow };
            var customer = new Customer { Id = Guid.NewGuid(), Code = CustomerCode, NameAr = "عميل A5", IsActive = true, CreatedAt = DateTime.UtcNow };
            var supplier = new Supplier { Id = Guid.NewGuid(), Code = SupplierCode, NameAr = "مورد A5", IsActive = true, CreatedAt = DateTime.UtcNow };

            ctx.AddRange(category, unit, warehouse, customer, supplier);
            await ctx.SaveChangesAsync();

            CategoryId = category.Id;
            UnitId = unit.Id;
            WarehouseId = warehouse.Id;
            CustomerId = customer.Id;
            SupplierId = supplier.Id;
        }

        // ملاحظة: لا يُزرع رصيد نقدي هنا. الزرع **كسول** عبر SeedCashAsync() لأن بعض الاختبارات
        // تبني شكل شجرة الحسابات بنفسها (مثل تحويل 1100 إلى حساب رئيسي)، وزرع رصيد على حساب
        // سيصبح رئيسيًا لاحقًا يخالف قاعدة «لا حركات على الحسابات الرئيسية». انظر SeedCashAsync.

        Available = true;
    }

    private bool _cashSeeded;

    /// <summary>
    /// يزرع رصيدًا نقديًا افتتاحيًا **عند الطلب فقط** (كسول، ومرة واحدة لكل قاعدة).
    /// يُستدعى من الاختبارات التي تحتاج حركة نقدية — وليس من InitializeAsync — لأن بعض الاختبارات
    /// تبني شكل الشجرة بنفسها؛ وزرع رصيد على حساب سيصبح رئيسيًا يخالف قاعدة «الحسابات المتحركة فقط».
    /// الزرع بقيد محاسبي حقيقي (لا كتابة مباشرة على الرصيد).
    /// </summary>
    public async Task SeedCashAsync(decimal amount = 1_000_000m)
    {
        if (!Available || _cashSeeded) return;

        await using var ctx = NewContext();
        var cashId = (await PostingAccountResolver.ResolveAsync(ctx, "1100")).Id;
        var parentIds = ctx.Set<Account>().Where(a => a.ParentAccountId != null)
            .Select(a => a.ParentAccountId!.Value).ToHashSet();
        var equityId = ctx.Set<Account>()
            .Where(a => !a.IsDeleted && a.IsActive && a.NormalBalance == NormalBalance.Credit)
            .AsEnumerable().Where(a => !parentIds.Contains(a.Id))
            .OrderBy(a => a.Code, StringComparer.Ordinal).First().Id;

        var js = new JournalEntryService(ctx);
        await js.PrepareEntryAsync(JournalEntryType.OpeningBalance, DateTime.Today,
            "A5-OPENING-CASH", "رصيد نقدي افتتاحي لبيئة الاختبار",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = cashId, DebitAmount = amount },
                new() { AccountId = equityId, CreditAmount = amount }
            });
        await ctx.SaveChangesAsync();
        _cashSeeded = true;
    }

    private bool _payableSeeded;

    /// <summary>
    /// يزرع ذمّة مورد افتتاحية (قابل للسداد) عبر **قيد محاسبي حقيقي** + مزامنة الرصيد المخزَّن،
    /// ليكون اختبار سندات الصرف ممكناً بعد حارس PU-03 (منع السداد الزائد).
    /// كسول ومرة واحدة لكل قاعدة — ولا يُنشئ مستنداً وهمياً.
    /// </summary>
    public async Task SeedSupplierPayableAsync(decimal amount = 100_000m)
    {
        if (!Available || _payableSeeded) return;

        await using var ctx = NewContext();
        var payableId = (await PostingAccountResolver.ResolveAsync(ctx, "2200")).Id;
        var inventoryId = (await PostingAccountResolver.ResolveAsync(ctx, "1300")).Id;

        var js = new JournalEntryService(ctx);
        await js.PrepareEntryAsync(JournalEntryType.OpeningBalance, DateTime.Today,
            "A5-OPENING-PAYABLE", "ذمّة مورد افتتاحية لبيئة الاختبار",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = inventoryId, DebitAmount = amount },
                new() { AccountId = payableId, CreditAmount = amount }
            });

        var supplier = await ctx.Set<Supplier>().FirstAsync(s => s.Id == SupplierId);
        supplier.CurrentBalance += amount;   // دفتر الأطراف يُزامَن مع الأستاذ 2200
        await ctx.SaveChangesAsync();
        _payableSeeded = true;
    }

    /// <summary>
    /// Arrange صريح: يزرع ذمّة مورد **لمورد محدَّد** بقيد متوازن (مدين 1300 مخزون / دائن 2200 المورد)
    /// مع مزامنة الرصيد المخزَّن للمورد — ليتمكّن الاختبار من ترحيل سند صرف بعد حارس PU-03.
    /// يُستدعى من جسم الاختبار (وليس خفيةً من مسار إنتاجي).
    /// </summary>
    /// <summary>
    /// Arrange صريح (محدَّث PU-04/D1): يُنشئ **فاتورة مشتريات آجلة مرحّلة** بمبلغ مساوٍ
    /// بدل قيد افتتاحي بلا فاتورة — لأن ترحيل سند الدفع أصبح مشروطًا بفواتير مؤهلة للتخصيص.
    /// الأثر على F2 و2200 مطابق للسابق (دائن 2200 بمقدار المبلغ)، مع فاتورة مؤهلة للتخصيص.
    /// </summary>
    public static async Task<Guid> SeedPayableForAsync(AppDbContext ctx, Guid supplierId, decimal amount)
    {
        var warehouseId = await ctx.Set<Warehouse>().Where(w => !w.IsDeleted).Select(w => w.Id).FirstAsync();
        var categoryId = await ctx.Set<Category>().Where(c => !c.IsDeleted).Select(c => c.Id).FirstAsync();
        var unitId = await ctx.Set<Unit>().Where(u => !u.IsDeleted).Select(u => u.Id).FirstAsync();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "A5-AP-" + Guid.NewGuid().ToString("N")[..6],
            NameAr = "صنف ذمّة اختبار",
            CategoryId = categoryId,
            UnitId = unitId,
            SalePrice = amount,
            CostPrice = amount,
            TaxRate = 0m,
            TracksBatches = false,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        var service = new PurchaseInvoiceService(ctx, new JournalEntryService(ctx), new AllowAllManualPrice());
        var invoice = await service.CreateAsync(new CreatePurchaseInvoiceDto
        {
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)PurchaseInvoiceType.OnAccount,
            Lines = new List<CreatePurchaseInvoiceLineDto>
            {
                new() { ItemId = item.Id, Quantity = 1m, UnitCost = amount }
            }
        });

        return invoice.Id;
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(AdminConnectionString) || string.IsNullOrWhiteSpace(TestConnectionString))
            return;

        var dbName = new NpgsqlConnectionStringBuilder(TestConnectionString).Database;
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    // ───────────────────────── بيانات التجارة ─────────────────────────

    /// <summary>ينشئ صنفًا برصيد افتتاحي مسجَّل كحركة مخزون (لا كتابة مباشرة على الرصيد).</summary>
    public async Task<Guid> CreateItemAsync(string code, decimal salePrice, decimal costPrice,
        decimal openingStock, decimal taxRate = 0m, bool tracksBatches = false)
    {
        // تمويل الصندوق كسولًا عند أول حاجة فعلية (إنشاء أصناف) ليتسنّى الشراء النقدي
        // بعد حارس D‑03. آمن للاختبارات التي تبني شكل الشجرة: تُنشئ 11001 **قبل** الأصناف،
        // فيقع الزرع على الورقة 11001 ولا يمسّ الحساب الرئيسي 1100.
        await SeedCashAsync();
        // ملاحظة PU-03A: لا تُزرع ذمّة مورد هنا خفيةً — كل اختبار يزرع ذمّته صراحةً
        // عبر SeedPayableForAsync(ctx, supplierId, amount) في Arrange.

        await using var ctx = NewContext();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "صنف A5 " + code,
            CategoryId = CategoryId,
            UnitId = UnitId,
            SalePrice = salePrice,
            CostPrice = costPrice,
            TaxRate = taxRate,
            TracksBatches = tracksBatches,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        if (openingStock != 0m)
        {
            var movements = new StockMovementService(ctx, new AllowAllManualPrice());
            await movements.CreateAsync(new CreateStockMovementDto
            {
                ItemId = item.Id,
                WarehouseId = WarehouseId,
                MovementType = (int)MovementType.OpeningBalance,
                Quantity = openingStock,
                UnitCost = costPrice,
                MovementDate = DateTime.Today,
                ReferenceNumber = "A5-SEED"
            });
        }

        return item.Id;
    }

    /// <summary>ينشئ فئة مصروف مرتبطة بحساب متحرك موجود بالكود المحدد (مثل 5300).</summary>
    public async Task<Guid> CreateExpenseCategoryAsync(string code, string accountCode)
    {
        await using var ctx = NewContext();
        var account = await ctx.Set<Account>().FirstAsync(a => a.Code == accountCode && !a.IsDeleted);

        var category = new ExpenseCategory
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "فئة مصروف " + code,
            AccountId = account.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(category);
        await ctx.SaveChangesAsync();
        return category.Id;
    }

    /// <summary>ينشئ حسابًا ابنًا (ورقة) تحت حساب رئيسي — لمحاكاة شجرة Commercial (1100 رئيسي → 11001 ورقة).</summary>
    public async Task<Guid> CreateChildAccountAsync(string parentCode, string childCode, string nameAr)
    {
        await using var ctx = NewContext();
        var parent = await ctx.Set<Account>().FirstAsync(a => a.Code == parentCode && !a.IsDeleted);

        var child = new Account
        {
            Id = Guid.NewGuid(),
            Code = childCode,
            NameAr = nameAr,
            NameEn = nameAr,
            AccountType = parent.AccountType,
            NormalBalance = parent.NormalBalance,
            ParentAccountId = parent.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(child);
        await ctx.SaveChangesAsync();
        return child.Id;
    }

    public async Task<Guid> FindAccountIdAsync(string code)
    {
        await using var ctx = NewContext();
        return await ctx.Set<Account>().Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
    }

    // ───────────────────────── بُناة DTO جاهزة ─────────────────────────

    /// <summary>فاتورة بيع نقدية ببند واحد.</summary>
    public Application.DTOs.Sales.CreateSalesInvoiceDto NewCashSale(Guid itemId, decimal quantity, decimal unitPrice)
        => new()
        {
            CustomerId = CustomerId,
            WarehouseId = WarehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.Cash,
            PaymentMethod = (int)SalesPaymentMethod.Cash,
            IsPos = false,
            Lines = new List<Application.DTOs.Sales.CreateSalesInvoiceLineDto>
            {
                new() { ItemId = itemId, Quantity = quantity, UnitPrice = unitPrice }
            }
        };

    /// <summary>فاتورة بيع بالبطاقة ببند واحد (تُدين ذمم البطاقات 1205).</summary>
    public Application.DTOs.Sales.CreateSalesInvoiceDto NewCardSale(Guid itemId, decimal quantity, decimal unitPrice)
    {
        var dto = NewCashSale(itemId, quantity, unitPrice);
        dto.PaymentMethod = (int)SalesPaymentMethod.Card;
        dto.CardApprovalCode = "A5-CARD-OK";
        return dto;
    }

    /// <summary>فاتورة مشتريات (بشكلها الافتراضي آجل: تُدين المخزون وتدائن المورد 2200).</summary>
    public Application.DTOs.Purchases.CreatePurchaseInvoiceDto NewPurchase(Guid itemId, decimal quantity, decimal unitCost, bool cash = false)
        => new()
        {
            SupplierId = SupplierId,
            WarehouseId = WarehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = cash ? 1 : 2,
            PaymentMethod = cash ? 3 : null,
            Lines = new List<Application.DTOs.Purchases.CreatePurchaseInvoiceLineDto>
            {
                new() { ItemId = itemId, Quantity = quantity, UnitCost = unitCost }
            }
        };

    public static SalesInvoiceService NewSalesService(ERPSystem.Infrastructure.Data.AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllManualPrice());

    /// <summary>
    /// سياق يتضمن مُعترِض أوامر (لاختبار سلوك إعادة المحاولة عند فشل عابر) مع تفعيل إعادة المحاولة
    /// كما في الإنتاج (EnableRetryOnFailure).
    /// </summary>
    public AppDbContext NewContextWithInterceptor(Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor interceptor,
        bool retryOnFailure = true)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(TestConnectionString, o =>
            {
                o.MigrationsAssembly("ERPSystem.Infrastructure");
                if (retryOnFailure) o.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .AddInterceptors(interceptor);
        return new AppDbContext(builder.Options);
    }


}
