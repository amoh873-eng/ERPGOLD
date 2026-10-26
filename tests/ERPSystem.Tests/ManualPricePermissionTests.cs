using System.Security.Claims;
using ERPSystem.Application.DTOs.Items;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Requirement 3 — السعر اليدوي بالصلاحية (Manual price editing by permission).
///
/// الشاشات المشمولة والصلاحية الحالية المستخدمة لكل شاشة (نظام الأدوار الحالي: role = Module،
/// أي أن «{Module}.Edit» هو إدارة الموديول الكاملة القائمة فعلاً — لا دور/إذن جديد):
///  - الأصناف + شاشات المخزون التي بها سعر (حركة مخزون/استيراد جماعي) → Inventory.Edit
///  - فواتير المبيعات (ومردوداتها: الأسعار موروثة من الفاتورة الأصلية)       → Sales.Edit
///  - فواتير المشتريات (ومردوداتها: التكاليف موروثة من الفاتورة الأصلية)      → Purchases.Edit
///
/// الحماية الفعلية خلفية (Application): <see cref="ManualPriceGuard"/> يرفض أي قيمة سعر تخالف
/// السعر النظامي (سعر الصنف/التكلفة المخزّنة) إذا لم يكن المستخدم مصرّحًا — حتى عند استدعاء
/// خدمة التطبيق مباشرة (تجاوز الواجهة/Disabled/JavaScript).
/// POS خارج النطاق تمامًا (dto.IsPos) ولا يتأثر بهذا التغيير.
/// </summary>
public class ManualPricePermissionTests
{
    // ══════════════════════════ أدوات مساعدة ══════════════════════════

    /// <summary>
    /// بديل لمصدر التصريح في الاختبار: يُمنح الموديولات المذكورة صراحةً فقط
    /// (في التطبيق الحقيقي يُحقَن من Web/Api ولا يأتي من الواجهة أبدًا).
    /// </summary>
    private sealed class ManualPriceAuth : IManualPriceAuthorization
    {
        private readonly HashSet<string> _allowed;

        public ManualPriceAuth(params string[] allowedModules)
            => _allowed = new HashSet<string>(allowedModules, StringComparer.OrdinalIgnoreCase);

        public bool CanEditManualPrice(string moduleKey) => _allowed.Contains(moduleKey);
    }

    private static ClaimsPrincipal Principal(string[]? roles = null, string[]? permissions = null)
    {
        var identity = new ClaimsIdentity("test");
        foreach (var role in roles ?? Array.Empty<string>())
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        foreach (var permission in permissions ?? Array.Empty<string>())
            identity.AddClaim(new Claim(ManualPriceRules.ClaimType, permission));
        return new ClaimsPrincipal(identity);
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("manual-price-" + Guid.NewGuid())
            .Options;
        return new AppDbContext(options);
    }

    private sealed record Seeded(Guid CategoryId, Guid UnitId, Guid WarehouseId, Guid CustomerId, Guid ItemId);

    /// <summary>يجهّز بيانات مرجعية مصغّرة: فئة/وحدة/مخزن/عميل/صنف (سعر بيع 2.38 وتكلفة 1.75).</summary>
    private static async Task<Seeded> SeedBasicsAsync(AppDbContext ctx, decimal salePrice = 2.38m, decimal costPrice = 1.75m)
    {
        var category = new Category { Id = Guid.NewGuid(), Code = "C1", NameAr = "فئة اختبار", CreatedAt = DateTime.UtcNow };
        var unit = new Unit { Id = Guid.NewGuid(), Code = "U1", NameAr = "وحدة اختبار", CreatedAt = DateTime.UtcNow };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), Code = "W1", NameAr = "مخزن اختبار", IsDefault = true, CreatedAt = DateTime.UtcNow };
        var customer = new Customer { Id = Guid.NewGuid(), Code = "CUST1", NameAr = "عميل اختبار", IsActive = true, CreatedAt = DateTime.UtcNow };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "ITM1",
            NameAr = "صنف اختبار",
            CategoryId = category.Id,
            UnitId = unit.Id,
            CostPrice = costPrice,
            SalePrice = salePrice,
            CreatedAt = DateTime.UtcNow
        };

        ctx.AddRange(category, unit, warehouse, customer, item);
        await ctx.SaveChangesAsync();

        return new Seeded(category.Id, unit.Id, warehouse.Id, customer.Id, item.Id);
    }

    private static SalesInvoiceService NewSalesService(AppDbContext ctx, IManualPriceAuthorization auth)
        => new(ctx, new JournalEntryService(ctx), auth);

    private static PurchaseInvoiceService NewPurchaseService(AppDbContext ctx, IManualPriceAuthorization auth)
        => new(ctx, new JournalEntryService(ctx), auth);

    private static CreateSalesInvoiceDto SalesDto(Seeded s, decimal unitPrice, bool isPos = false) => new()
    {
        CustomerId = s.CustomerId,
        WarehouseId = s.WarehouseId,
        InvoiceDate = DateTime.Today,
        InvoiceType = (int)SalesInvoiceType.Cash,
        PaymentMethod = (int)SalesPaymentMethod.Cash,
        IsPos = isPos,
        Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = s.ItemId, Quantity = 1m, UnitPrice = unitPrice } }
    };
    // ═════════════════════ 1) قواعد التصريح (نظام الأدوار/الصلاحيات الحالي) ═════════════════════

    [Theory]
    [InlineData(ManualPriceModules.Sales, true)]
    [InlineData(ManualPriceModules.Purchases, false)]
    [InlineData(ManualPriceModules.Inventory, false)]
    public void Module_role_grants_manual_price_only_in_its_own_module(string moduleKey, bool expected)
    {
        var user = Principal(roles: new[] { "Sales" });

        Assert.Equal(expected, ManualPriceRules.IsAuthorized(user, moduleKey));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("SuperAdmin")]
    public void Admin_roles_are_authorized_in_every_module(string adminRole)
    {
        var user = Principal(roles: new[] { adminRole });

        Assert.True(ManualPriceRules.IsAuthorized(user, ManualPriceModules.Sales));
        Assert.True(ManualPriceRules.IsAuthorized(user, ManualPriceModules.Purchases));
        Assert.True(ManualPriceRules.IsAuthorized(user, ManualPriceModules.Inventory));
    }

    [Fact]
    public void User_without_any_module_role_is_not_authorized()
    {
        var user = Principal(roles: new[] { "Reports" });

        Assert.False(ManualPriceRules.IsAuthorized(user, ManualPriceModules.Sales));
        Assert.False(ManualPriceRules.IsAuthorized(user, ManualPriceModules.Inventory));
    }

    [Fact]
    public void Explicit_permission_claim_Edit_is_authorized_opt_in()
    {
        // مستخدم بلا دور موديول، لكن لديه الإذن الصريح Sales.Edit → مصرّح (Explicit opt-in)
        var user = Principal(permissions: new[] { "Sales.Edit" });

        Assert.True(ManualPriceRules.IsAuthorized(user, ManualPriceModules.Sales));
        Assert.Equal("Sales.Edit", ManualPriceRules.EditPermission(ManualPriceModules.Sales));
    }

    [Fact]
    public void Explicit_permission_claims_take_precedence_view_only_is_denied()
    {
        // يوجد إذن صريح للموديول لكنه View فقط → لا سعر يدوي (الأذونات الصريحة هي المرجع عند وجودها)
        var user = Principal(roles: new[] { "Sales" }, permissions: new[] { "Sales.View" });

        Assert.False(ManualPriceRules.IsAuthorized(user, ManualPriceModules.Sales));
    }

    [Fact]
    public void Unauthenticated_user_is_never_authorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(ManualPriceRules.IsAuthorized(anonymous, ManualPriceModules.Sales));
        Assert.False(ManualPriceRules.IsAuthorized(null, ManualPriceModules.Sales));
    }

    [Fact]
    public void Module_keys_match_the_permissions_documented_for_each_screen()
    {
        Assert.Equal("Inventory.Edit", ManualPriceRules.EditPermission(ManualPriceModules.Inventory));
        Assert.Equal("Purchases.Edit", ManualPriceRules.EditPermission(ManualPriceModules.Purchases));
        Assert.Equal("Sales.Edit", ManualPriceRules.EditPermission(ManualPriceModules.Sales));
    }

    // ═════════════════════ 2) الحارس الخلفي (ManualPriceGuard) ═════════════════════

    [Fact]
    public void Submitting_the_system_price_is_never_a_manual_edit()
    {
        // المستخدم غير المصرّح يمرّر نفس سعر الصنف → ليس تعديلًا يدويًا → مسموح بلا تصريح
        ManualPriceGuard.EnsureCanOverride(null, ManualPriceModules.Sales, 2.38m, 2.38m, "سعر الوحدة");
    }

    [Fact]
    public void Manual_price_without_permission_is_rejected_with_the_required_permission()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ManualPriceGuard.EnsureCanOverride(new ManualPriceAuth(), ManualPriceModules.Sales,
                submittedValue: 2.45m, systemValue: 2.38m, fieldLabel: "سعر الوحدة"));

        Assert.Contains("Sales.Edit", ex.Message);
        Assert.Contains("سعر الوحدة", ex.Message);
    }

    [Fact]
    public void Manual_price_with_permission_is_allowed()
    {
        ManualPriceGuard.EnsureCanOverride(new ManualPriceAuth(ManualPriceModules.Sales),
            ManualPriceModules.Sales, 2.45m, 2.38m, "سعر الوحدة");
    }

    [Fact]
    public void Missing_authorization_source_fails_closed()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            ManualPriceGuard.EnsureCanOverride(null, ManualPriceModules.Inventory, 3m, 2m, "سعر البيع"));
    }


    // ═════════════════════ 3) الأصناف (Inventory.Edit) ═════════════════════

    [Fact]
    public async Task Authorized_user_can_change_item_price_and_value_persists_after_save()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var service = new ItemService(ctx, new ManualPriceAuth(ManualPriceModules.Inventory));

        await service.UpdateAsync(new UpdateItemDto
        {
            Id = seeded.ItemId,
            Code = "ITM1",
            NameAr = "صنف اختبار",
            CategoryId = seeded.CategoryId,
            UnitId = seeded.UnitId,
            CostPrice = 1.75m,
            SalePrice = 2.45m, // تعديل يدوي مصرّح به
            IsActive = true
        });

        // Save/Reopen: نقرأ من جديد بعد إفراغ التتبّع
        ctx.ChangeTracker.Clear();
        var reopened = await service.GetByIdAsync(seeded.ItemId);

        Assert.NotNull(reopened);
        Assert.Equal(2.45m, reopened!.SalePrice);
        Assert.Equal(2.45m, (await ctx.Set<Item>().FirstAsync(i => i.Id == seeded.ItemId)).SalePrice);
    }

    [Fact]
    public async Task Unauthorized_user_cannot_change_item_price_backend_rejects()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var service = new ItemService(ctx, new ManualPriceAuth()); // بلا أي تصريح

        // استدعاء خدمة التطبيق مباشرة (تجاوز الواجهة بالكامل) — Backend يرفض
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(new UpdateItemDto
        {
            Id = seeded.ItemId,
            Code = "ITM1",
            NameAr = "صنف اختبار",
            CategoryId = seeded.CategoryId,
            UnitId = seeded.UnitId,
            CostPrice = 1.75m,
            SalePrice = 2.45m,
            IsActive = true
        }));

        Assert.Contains("Inventory.Edit", ex.Message);

        ctx.ChangeTracker.Clear();
        Assert.Equal(2.38m, (await ctx.Set<Item>().FirstAsync(i => i.Id == seeded.ItemId)).SalePrice);
    }

    [Fact]
    public async Task Unauthorized_user_can_still_save_the_item_with_its_default_price()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var service = new ItemService(ctx, new ManualPriceAuth());

        // تغيير بيانات غير سعرية مع تمرير نفس السعر المخزّن → لا يُرفض (الشاشة تعمل كالمعتاد)
        await service.UpdateAsync(new UpdateItemDto
        {
            Id = seeded.ItemId,
            Code = "ITM1",
            NameAr = "اسم محدَّث",
            CategoryId = seeded.CategoryId,
            UnitId = seeded.UnitId,
            CostPrice = 1.75m,
            SalePrice = 2.38m,
            IsActive = true
        });

        ctx.ChangeTracker.Clear();
        var item = await ctx.Set<Item>().FirstAsync(i => i.Id == seeded.ItemId);
        Assert.Equal("اسم محدَّث", item.NameAr);
        Assert.Equal(2.38m, item.SalePrice);
    }

    [Fact]
    public async Task Unauthorized_user_cannot_create_item_with_manual_prices()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var service = new ItemService(ctx, new ManualPriceAuth());

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(new CreateItemDto
        {
            Code = "NEW1",
            NameAr = "صنف جديد",
            CategoryId = seeded.CategoryId,
            UnitId = seeded.UnitId,
            CostPrice = 1.75m,
            SalePrice = 2.38m
        }));

        Assert.Contains("Inventory.Edit", ex.Message);
        Assert.False(await ctx.Set<Item>().AnyAsync(i => i.Code == "NEW1"));
    }


    // ═════════════════════ 4) فواتير المبيعات (Sales.Edit) ═════════════════════

    [Fact]
    public async Task Sales_invoice_manual_price_is_rejected_for_unauthorized_user()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx); // Item.SalePrice = 2.38
        var service = NewSalesService(ctx, new ManualPriceAuth()); // بلا Sales.Edit

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateAsync(SalesDto(seeded, unitPrice: 2.45m)));

        Assert.Contains("Sales.Edit", ex.Message);
        // لا أثر على قاعدة البيانات: لا فاتورة/سطر، ولا رقم فاتورة محروق
        Assert.False(await ctx.Set<SalesInvoice>().AnyAsync());
        Assert.False(await ctx.Set<SalesInvoiceLine>().AnyAsync());
    }

    [Fact]
    public async Task Sales_invoice_default_price_is_accepted_for_unauthorized_user()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var service = NewSalesService(ctx, new ManualPriceAuth());

        // نفس سعر الصنف (2.38) → ليس تعديلًا يدويًا ⇒ لا رفض من الحارس
        var ex = await Record.ExceptionAsync(() => service.CreateAsync(SalesDto(seeded, unitPrice: 2.38m)));
        Assert.False(ex is UnauthorizedAccessException, $"رُفض السعر النظامي: {ex?.Message}");
    }

    [Fact]
    public async Task Sales_invoice_manual_price_is_accepted_for_authorized_user()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx); // Item.SalePrice = 2.38
        var service = NewSalesService(ctx, new ManualPriceAuth(ManualPriceModules.Sales));

        // المصرّح له يمرّر 2.45 → يجب ألا يرفضه الحارس الخلفي.
        // (على مزوّد InMemory يتوقف التنفيذ لاحقًا عند أجزاء PostgreSQL الخاصة: جدول الترقيم/قفل الصف
        //  — ومسار الترحيل الكامل مُغطّى في اختبار التكامل الحي ManualPriceLiveE2ETests.)
        var ex = await Record.ExceptionAsync(() => service.CreateAsync(SalesDto(seeded, unitPrice: 2.45m)));
        Assert.False(ex is UnauthorizedAccessException,
            $"الحارس الخلفي رفض مستخدمًا مصرّحًا له Sales.Edit: {ex?.Message}");

        // سعر الصنف نفسه لا يتغيّر أبدًا بتعديل سعر سطر الفاتورة
        ctx.ChangeTracker.Clear();
        Assert.Equal(2.38m, (await ctx.Set<Item>().FirstAsync(i => i.Id == seeded.ItemId)).SalePrice);
    }

    [Fact]
    public async Task Pos_invoice_is_out_of_scope_and_keeps_its_current_behaviour()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        // POS خارج النطاق: لا يُمنح تصريح سعر يدوي ولا يُطلب منه — الفاتورة المعلّمة IsPos
        // تمرّ بلا أي فحص سعر جديد (Regression: سلوك POS كما هو).
        var service = NewSalesService(ctx, new ManualPriceAuth());

        var ex = await Record.ExceptionAsync(() => service.CreateAsync(SalesDto(seeded, unitPrice: 2.45m, isPos: true)));

        Assert.False(ex is UnauthorizedAccessException,
            $"تأثّر POS بفحص السعر اليدوي (يجب ألا يتأثر): {ex?.Message}");
    }


    // ═════════════════════ 5) فواتير المشتريات (Purchases.Edit) ═════════════════════

    private static CreatePurchaseInvoiceDto PurchaseDto(Seeded s, Guid supplierId, decimal unitCost) => new()
    {
        SupplierId = supplierId,
        WarehouseId = s.WarehouseId,
        InvoiceDate = DateTime.Today,
        InvoiceType = (int)PurchaseInvoiceType.OnAccount,
        Lines = new List<CreatePurchaseInvoiceLineDto> { new() { ItemId = s.ItemId, Quantity = 1m, UnitCost = unitCost } }
    };

    private static async Task<Guid> SeedSupplierAsync(AppDbContext ctx)
    {
        var supplier = new Supplier { Id = Guid.NewGuid(), Code = "SUP1", NameAr = "مورد اختبار", CreatedAt = DateTime.UtcNow };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    [Fact]
    public async Task Purchase_invoice_manual_cost_is_rejected_for_unauthorized_user()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx); // Item.CostPrice = 1.75
        var supplierId = await SeedSupplierAsync(ctx);
        var service = NewPurchaseService(ctx, new ManualPriceAuth());

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateAsync(PurchaseDto(seeded, supplierId, unitCost: 1.99m)));

        Assert.Contains("Purchases.Edit", ex.Message);
        Assert.False(await ctx.Set<PurchaseInvoice>().AnyAsync());
    }

    [Fact]
    public async Task Purchase_invoice_default_cost_is_accepted_for_unauthorized_user()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var supplierId = await SeedSupplierAsync(ctx);
        var service = NewPurchaseService(ctx, new ManualPriceAuth());

        // القيمة الافتراضية في شاشة المشتريات = Item.CostPrice ⇒ لا تصريح مطلوب
        var ex = await Record.ExceptionAsync(
            () => service.CreateAsync(PurchaseDto(seeded, supplierId, unitCost: 1.75m)));

        Assert.False(ex is UnauthorizedAccessException, $"رُفضت التكلفة النظامية: {ex?.Message}");
    }

    [Fact]
    public async Task Purchase_invoice_manual_cost_is_accepted_for_authorized_user()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var supplierId = await SeedSupplierAsync(ctx);
        var service = NewPurchaseService(ctx, new ManualPriceAuth(ManualPriceModules.Purchases));

        var ex = await Record.ExceptionAsync(
            () => service.CreateAsync(PurchaseDto(seeded, supplierId, unitCost: 1.99m)));

        Assert.False(ex is UnauthorizedAccessException,
            $"الحارس الخلفي رفض مستخدمًا مصرّحًا له Purchases.Edit: {ex?.Message}");
    }

    // ═════════════════════ 6) شاشات المخزون التي بها سعر (Inventory.Edit) ═════════════════════

    [Fact]
    public async Task Stock_movement_manual_unit_cost_is_rejected_for_unauthorized_user()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var service = new StockMovementService(ctx, new ManualPriceAuth());

        // استدعاء الخدمة مباشرة (تجاوز شاشة الحركة) → Backend يرفض
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(new CreateStockMovementDto
        {
            ItemId = seeded.ItemId,
            WarehouseId = seeded.WarehouseId,
            MovementType = (int)MovementType.OpeningBalance,
            Quantity = 5m,
            UnitCost = 2.10m,
            MovementDate = DateTime.Today
        }));

        Assert.Contains("Inventory.Edit", ex.Message);
        Assert.False(await ctx.Set<StockMovement>().AnyAsync());
    }

    [Fact]
    public async Task Stock_movement_default_unit_cost_passes_the_guard_for_unauthorized_user()
    {
        using var ctx = NewContext();
        var seeded = await SeedBasicsAsync(ctx);
        var service = new StockMovementService(ctx, new ManualPriceAuth());

        var ex = await Record.ExceptionAsync(() => service.CreateAsync(new CreateStockMovementDto
        {
            ItemId = seeded.ItemId,
            WarehouseId = seeded.WarehouseId,
            MovementType = (int)MovementType.OpeningBalance,
            Quantity = 5m,
            UnitCost = 1.75m, // نفس Item.CostPrice (القيمة الافتراضية في الشاشة)
            MovementDate = DateTime.Today
        }));

        Assert.False(ex is UnauthorizedAccessException, $"رُفضت التكلفة النظامية: {ex?.Message}");
    }


}
