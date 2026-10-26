using System.Globalization;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Requirement 1 — دقة الأسعار العشرية (preserve decimal pricing precision).
///
/// يتحقّق من أنّ:
///  - سلسلة الأنواع كلها <c>decimal</c> (لا float/double) من Domain → EF Core → PostgreSQL.
///  - القيم 1.75 / 2.38 / 10.50 / 125.99 / 0.00 تُحفظ وتُقرأ بلا أي فقدان
///    (لا 2.38 → 2 ولا 2.38 → 238).
///  - سعر البيع يبقى بدقة (18,2) بينما سعر التكلفة (المتوسط المرجّح الداخلي) أصبح (18,4)
///    فلا يفقد كسور التقييم.
/// </summary>
public class PricingPrecisionTests
{
    /// <summary>القيم المرجعية المطلوب دعمها بدقة.</summary>
    public static readonly decimal[] PrecisionValues = { 1.75m, 2.38m, 10.50m, 125.99m, 0.00m };

    public static IEnumerable<object[]> ValuesAsDecimal()
        => PrecisionValues.Select(v => new object[] { v });

    private static AppDbContext NewInMemory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("pricing-" + Guid.NewGuid())
            .Options;
        return new AppDbContext(options);
    }

    private static AppDbContext NewNpgsqlModelOnly()
    {
        // لا يتّصل بقاعدة البيانات — نُبني الموديل فقط بمزوّد Npgsql لفحص أنواع الأعمدة
        // التي يُنتجها EF Core فعلياً لـ PostgreSQL.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=x;Password=x")
            .Options;
        return new AppDbContext(options);
    }

    private static string ColumnType(AppDbContext ctx, Type entity, string property)
    {
        var entityType = ctx.Model.FindEntityType(entity)
            ?? throw new InvalidOperationException($"Entity {entity.Name} not found in model.");
        var prop = entityType.FindProperty(property)
            ?? throw new InvalidOperationException($"Property {entity.Name}.{property} not found in model.");
        return prop.GetColumnType();
    }

    private static Type ClrType(AppDbContext ctx, Type entity, string property)
        => ctx.Model.FindEntityType(entity)!.FindProperty(property)!.ClrType;
// ───────────────────────── 1) EF Core / PostgreSQL mapping ─────────────────────────

    [Fact]
    public void Price_chain_uses_decimal_with_expected_column_precision()
    {
        using var ctx = NewNpgsqlModelOnly();

        // سعر البيع وسعر الوحدة للبيع: عشرتان (مبلغ عملة معروض للعميل)
        Assert.Equal("numeric(18,2)", ColumnType(ctx, typeof(Item), nameof(Item.SalePrice)));
        Assert.Equal("numeric(18,2)", ColumnType(ctx, typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.UnitPrice)));

        // سعر التكلفة (تقييم داخلي / متوسط مرجّح): أربع خانات
        Assert.Equal("numeric(18,4)", ColumnType(ctx, typeof(Item), nameof(Item.CostPrice)));

        // لقطة تكلفة البيع (COGS) = تكلفة داخلية رباعية الخانات (Phase C1)؛
        // أما أسعار المورّد المُرحَّلة على فواتير المشتريات فتبقى بعشرتين (سلوك محفوظ)
        Assert.Equal("numeric(18,4)", ColumnType(ctx, typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.UnitCost)));
        Assert.Equal("numeric(18,2)", ColumnType(ctx, typeof(PurchaseInvoiceLine), nameof(PurchaseInvoiceLine.UnitCost)));
    }

    [Fact]
    public void Price_chain_clr_types_are_decimal_not_float_or_double()
    {
        using var ctx = NewNpgsqlModelOnly();

        Assert.Equal(typeof(decimal), ClrType(ctx, typeof(Item), nameof(Item.CostPrice)));
        Assert.Equal(typeof(decimal), ClrType(ctx, typeof(Item), nameof(Item.SalePrice)));
        Assert.Equal(typeof(decimal), ClrType(ctx, typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.UnitPrice)));
        Assert.Equal(typeof(decimal), ClrType(ctx, typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.UnitCost)));
        Assert.Equal(typeof(decimal), ClrType(ctx, typeof(PurchaseInvoiceLine), nameof(PurchaseInvoiceLine.UnitCost)));
    }
// ───────────────────────── 2) Round-trip بلا فقدان ─────────────────────────

    [Theory]
    [MemberData(nameof(ValuesAsDecimal))]
    public void Item_prices_roundtrip_without_loss(decimal value)
    {
        using var ctx = NewInMemory();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "SKU-" + Guid.NewGuid().ToString("N")[..8],
            NameAr = "صنف اختبار",
            CostPrice = value,
            SalePrice = value,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Items.Add(item);
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();

        var loaded = ctx.Items.Single(i => i.Id == item.Id);

        Assert.Equal(value, loaded.SalePrice);
        Assert.Equal(value, loaded.CostPrice);
        // التنسيق بعشرتين يعيد النص الأصلي تماماً (لا 238، ولا حذف الخانتين)
        Assert.Equal(value.ToString("0.00", CultureInfo.InvariantCulture),
                     loaded.SalePrice.ToString("0.00", CultureInfo.InvariantCulture));
    }

    [Theory]
    [MemberData(nameof(ValuesAsDecimal))]
    public void SalesInvoiceLine_unitprice_roundtrips_without_loss(decimal value)
    {
        using var ctx = NewInMemory();

        var line = new SalesInvoiceLine
        {
            Id = Guid.NewGuid(),
            SalesInvoiceId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Quantity = 2m,
            UnitPrice = value,
            UnitCost = 0m,
            LineTotal = Math.Round(2m * value, 2),
            CreatedAt = DateTime.UtcNow
        };
        ctx.SalesInvoiceLines.Add(line);
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();

        var loaded = ctx.SalesInvoiceLines.Single(l => l.Id == line.Id);
        Assert.Equal(value, loaded.UnitPrice);
    }

    [Fact]
    public void Two_point_three_eight_is_neither_2_nor_238()
    {
        var value = decimal.Parse("2.38", CultureInfo.InvariantCulture);

        Assert.Equal(2.38m, value);
        Assert.NotEqual(2m, value);     // لا يُقرَّب إلى عدد صحيح
        Assert.NotEqual(238m, value);   // لا يُعامَل منزلة الفاصلة كفاصل آلاف
        Assert.Equal("2.38", value.ToString("0.00", CultureInfo.InvariantCulture));
    }

// ───────────────────────── 3) المتوسط المرجّح (18,4) ─────────────────────────

    [Fact]
    public void WeightedAverage_preserves_four_decimals()
    {
        // (3×10.00 + 2×10.33) / 5 = 50.66 / 5 = 10.132  → يجب أن يبقى 10.1320 لا 10.13
        var avg = InventoryValuation.ComputeWeightedAverage(
            oldQuantity: 3m, oldCost: 10.00m,
            receivedQuantity: 2m, receivedUnitCost: 10.33m);

        Assert.Equal(10.1320m, avg);
        // التقريب لخانتين (السلوك القديم) كان يُسقط 0.002 لكل وحدة
        Assert.NotEqual(avg, Math.Round(avg, 2));
        Assert.Equal(10.13m, Math.Round(avg, 2));
    }

    [Fact]
    public void WeightedAverage_cost_roundtrips_through_item_at_four_decimals()
    {
        using var ctx = NewInMemory();

        var avg = InventoryValuation.ComputeWeightedAverage(3m, 10.00m, 2m, 10.33m);
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "WG-" + Guid.NewGuid().ToString("N")[..8],
            NameAr = "صنف متوسط",
            CostPrice = avg,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Items.Add(item);
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();

        var loaded = ctx.Items.Single(i => i.Id == item.Id);
        Assert.Equal(10.1320m, loaded.CostPrice);
    }
}