using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Sales;
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
/// اختبار لا يعمل إلا عند توفر قاعدة PostgreSQL تطويرية — يُحدَّد عبر متغير البيئة ERPSYSTEM_TEST_PG.
/// عند غيابه يُبلَّغ عنه كاختبار متخطّى (Skip) بدل الفشل، فلا تعتمد بقية الحزمة على قاعدة بيانات.
/// </summary>
public sealed class LivePgFactAttribute : FactAttribute
{
    public const string EnvVar = "ERPSYSTEM_TEST_PG";

    public LivePgFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"يتطلب قاعدة PostgreSQL تطويرية عبر {EnvVar} (اختبار تكامل حي — Development فقط).";
    }
}

/// <summary>
/// Requirement 3 — اختبار تكامل حي على PostgreSQL (Development/اختبار فقط، ولا يلمس Production):
///  - ينشئ قاعدة بيانات مؤقتة خاصة به على نفس الخادم التطويري، يطبّق الترحيلات (بها شجرة الحسابات
///    المزروعة)، يزرع فئة/وحدة/مخزن/عميل، ثم يحذف القاعدة بالكامل — بلا أي أثر على قواعد التطوير/الإنتاج.
///  - يغطّي: فاتورة مبيعات بسعر يدوي 2.45 مع بقاء Item.SalePrice = 2.38 (Save/Reopen)،
///    رفض Backend لمحاولة غير مصرّح بها (تجاوز الواجهة)، وعدم تأثر POS.
/// </summary>
public sealed class LivePgFixture : IAsyncLifetime
{
    public string? AdminConnectionString { get; private set; }
    public string? TestConnectionString { get; private set; }
    public bool Available { get; private set; }

    public Guid CategoryId { get; private set; }
    public Guid UnitId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid CustomerId { get; private set; }

    /// <summary>مستخدم مصرّح بكل الموديولات (لتجهيز البيانات فقط).</summary>
    public sealed class AllowAllManualPrice : IManualPriceAuthorization
    {
        public bool CanEditManualPrice(string moduleKey) => true;
    }

    /// <summary>تصريح موديول واحد فقط — نمثّل المستخدم المصرّح/غير المصرّح به.</summary>
    public sealed class ModuleManualPrice : IManualPriceAuthorization
    {
        private readonly string? _module;
        public ModuleManualPrice(string? module) => _module = module;
        public bool CanEditManualPrice(string moduleKey) =>
            _module is not null && string.Equals(_module, moduleKey, StringComparison.OrdinalIgnoreCase);
    }

    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(TestConnectionString, o => o.MigrationsAssembly("ERPSystem.Infrastructure"))
        .Options);

    static LivePgFixture()
    {
        // نفس إعداد مضيف Web (DateTime محلي في هذا التطبيق مع أعمدة timestamptz).
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    /// <summary>ينشئ صنفًا (سعر بيع 2.38 / تكلفة 1.75) مع رصيد افتتاحي 10 وحدات.</summary>
    public async Task<Guid> CreateItemWithStockAsync(string code)
    {
        await using var ctx = NewContext();
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "صنف R3 " + code,
            CategoryId = CategoryId,
            UnitId = UnitId,
            CostPrice = 1.75m,
            SalePrice = 2.38m,
            TaxRate = 0m,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        var movements = new StockMovementService(ctx, new AllowAllManualPrice());
        await movements.CreateAsync(new CreateStockMovementDto
        {
            ItemId = item.Id,
            WarehouseId = WarehouseId,
            MovementType = (int)MovementType.OpeningBalance,
            Quantity = 10m,
            UnitCost = 1.75m,
            MovementDate = DateTime.Today,
            ReferenceNumber = "R3-SEED"
        });

        return item.Id;
    }

    public CreateSalesInvoiceDto SalesDto(Guid itemId, decimal unitPrice, bool isPos) => new()
    {
        CustomerId = CustomerId,
        WarehouseId = WarehouseId,
        InvoiceDate = DateTime.Today,
        InvoiceType = (int)SalesInvoiceType.Cash,
        PaymentMethod = (int)SalesPaymentMethod.Cash,
        IsPos = isPos,
        Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 1m, UnitPrice = unitPrice } }
    };

    public async Task InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable(LivePgFactAttribute.EnvVar);
        if (string.IsNullOrWhiteSpace(baseConnection)) { Available = false; return; }

        var builder = new NpgsqlConnectionStringBuilder(baseConnection);
        builder.Database = "postgres";
        AdminConnectionString = builder.ConnectionString;

        var dbName = "erp_r3_e2e_" + Guid.NewGuid().ToString("N")[..10];
        builder.Database = dbName;
        TestConnectionString = builder.ConnectionString;

        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        // الترحيلات تزرع شجرة الحسابات (HasData) المطلوبة لترحيل الفاتورة
        await using (var ctx = NewContext())
        {
            await ctx.Database.MigrateAsync();
        }

        await using (var ctx = NewContext())
        {
            var category = new Category { Id = Guid.NewGuid(), Code = "R3-CAT", NameAr = "فئة R3", CreatedAt = DateTime.UtcNow };
            var unit = new Unit { Id = Guid.NewGuid(), Code = "R3-UN", NameAr = "وحدة R3", CreatedAt = DateTime.UtcNow };
            var warehouse = new Warehouse { Id = Guid.NewGuid(), Code = "R3-WH", NameAr = "مخزن R3", IsDefault = true, CreatedAt = DateTime.UtcNow };
            var customer = new Customer { Id = Guid.NewGuid(), Code = "R3-CUST", NameAr = "عميل R3", IsActive = true, CreatedAt = DateTime.UtcNow };
            ctx.AddRange(category, unit, warehouse, customer);
            await ctx.SaveChangesAsync();

            CategoryId = category.Id;
            UnitId = unit.Id;
            WarehouseId = warehouse.Id;
            CustomerId = customer.Id;
        }

        Available = true;
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

/// <summary>
/// Requirement 3 — سيناريوهات الاختبار الحي (PostgreSQL) مع إعادة فتح (Save/Reopen) من قاعدة البيانات.
/// </summary>
public class ManualPriceLiveE2ETests : IClassFixture<LivePgFixture>
{
    private readonly LivePgFixture _pg;

    public ManualPriceLiveE2ETests(LivePgFixture pg) => _pg = pg;

    private static SalesInvoiceService NewSalesService(AppDbContext ctx, IManualPriceAuthorization auth)
        => new(ctx, new JournalEntryService(ctx), auth);

    [LivePgFact]
    public async Task Authorized_user_saves_line_2_45_while_item_price_stays_2_38()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemWithStockAsync("R3-LIVE-AUTH-" + Guid.NewGuid().ToString("N")[..6]);

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var service = NewSalesService(ctx, new LivePgFixture.ModuleManualPrice(ManualPriceModules.Sales));
            var invoice = await service.CreateAsync(_pg.SalesDto(itemId, unitPrice: 2.45m, isPos: false));
            invoiceId = invoice.Id;

            Assert.Equal(2.45m, invoice.Lines.Single().UnitPrice);
            Assert.Equal(2.45m, invoice.Lines.Single().LineTotal);
        }

        // Save/Reopen: نقرأ الفاتورة من قاعدة البيانات ثم سعر الصنف الأصلي
        await using (var ctx = _pg.NewContext())
        {
            var reader = NewSalesService(ctx, new LivePgFixture.ModuleManualPrice(null));
            var reopened = await reader.GetByIdAsync(invoiceId);

            Assert.NotNull(reopened);
            Assert.Equal(2.45m, reopened!.Lines.Single().UnitPrice);
            Assert.Equal(2.38m, (await ctx.Set<Item>().FirstAsync(i => i.Id == itemId)).SalePrice);
        }
    }

    [LivePgFact]
    public async Task Unauthorized_user_is_rejected_by_the_backend_when_bypassing_the_ui()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemWithStockAsync("R3-LIVE-DENY-" + Guid.NewGuid().ToString("N")[..6]);

        await using var ctx = _pg.NewContext();
        // استدعاء خدمة التطبيق مباشرة بلا تصريح (تجاوز الواجهة/الشاشة بالكامل)
        var service = NewSalesService(ctx, new LivePgFixture.ModuleManualPrice(null));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateAsync(_pg.SalesDto(itemId, unitPrice: 2.45m, isPos: false)));

        Assert.Contains("Sales.Edit", ex.Message);
        Assert.False(await ctx.Set<SalesInvoice>().AnyAsync(i => i.Lines.Any(l => l.ItemId == itemId)));
    }

    [LivePgFact]
    public async Task Unauthorized_user_posts_with_the_default_item_price()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemWithStockAsync("R3-LIVE-DEFAULT-" + Guid.NewGuid().ToString("N")[..6]);

        await using var ctx = _pg.NewContext();
        var service = NewSalesService(ctx, new LivePgFixture.ModuleManualPrice(null));
        var invoice = await service.CreateAsync(_pg.SalesDto(itemId, unitPrice: 2.38m, isPos: false));

        Assert.Equal(2.38m, invoice.Lines.Single().UnitPrice);
        Assert.Equal(2.38m, (await ctx.Set<Item>().FirstAsync(i => i.Id == itemId)).SalePrice);
    }

    [LivePgFact]
    public async Task Pos_invoice_regression_price_path_is_unchanged()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await _pg.CreateItemWithStockAsync("R3-LIVE-POS-" + Guid.NewGuid().ToString("N")[..6]);

        await using var ctx = _pg.NewContext();
        // POS (IsPos = true) يعمل كما هو اليوم: بلا تصريح سعر يدوي وبلا أي فحص سعر جديد — Regression فقط
        var service = NewSalesService(ctx, new LivePgFixture.ModuleManualPrice(null));
        var invoice = await service.CreateAsync(_pg.SalesDto(itemId, unitPrice: 2.45m, isPos: true));

        Assert.True(invoice.IsPos);
        Assert.Equal(2.45m, invoice.Lines.Single().UnitPrice);
        Assert.Equal(2.38m, (await ctx.Set<Item>().FirstAsync(i => i.Id == itemId)).SalePrice);
    }
}


}

