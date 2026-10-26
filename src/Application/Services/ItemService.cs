using ERPSystem.Application.DTOs.Items;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// Implements inventory item business logic (الأصناف).
/// Rules:
/// - الكود فريد
/// - الفئة والوحدة يجب أن تكونا موجودتين
/// - لا يمكن حذف صنف نظامي أو صنف له حركات مخزون
/// - Requirement 3: إدخال/تعديل السعر يدويًا يتطلب تصريح Inventory.Edit
/// </summary>
public partial class ItemService : IItemService
{
    private readonly DbContext _context;
    private readonly IManualPriceAuthorization _priceAuthorization;

    public ItemService(DbContext context, IManualPriceAuthorization priceAuthorization)
    {
        _context = context;
        _priceAuthorization = priceAuthorization;
    }

    public async Task<List<ItemDto>> GetAllAsync()
    {
        var items = await _context.Set<Item>()
            .Include(i => i.Category)
            .Include(i => i.Unit)
            .OrderBy(i => i.Code)
            .ToListAsync();

        return items.Select(MapToDto).ToList();
    }

    /// <summary>
    /// بحث مخزون خادمي (ILike غير حساس لحالة الأحرف على PostgreSQL) بالكود/الاسم/الباركود.
    /// حُلّ محل فلترة Contains في الذاكرة لأن Contains يُترجم إلى LIKE حساس للحالة في PostgreSQL.
    /// </summary>
    public async Task<List<ItemDto>> SearchAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return await GetAllAsync();

        var items = await _context.Set<Item>()
            .Include(i => i.Category)
            .Include(i => i.Unit)
            .Where(i => !i.IsDeleted && (
                EF.Functions.ILike(i.Code, $"%{term}%") ||
                EF.Functions.ILike(i.NameAr, $"%{term}%") ||
                (i.NameEn != null && EF.Functions.ILike(i.NameEn, $"%{term}%")) ||
                (i.Barcode != null && EF.Functions.ILike(i.Barcode, $"%{term}%"))))
            .OrderBy(i => i.Code)
            .ToListAsync();

        return items.Select(MapToDto).ToList();
    }

    public async Task<List<ItemDto>> GetLowStockAsync()
    {
        var items = await _context.Set<Item>()
            .Include(i => i.Category)
            .Include(i => i.Unit)
            .Where(i => i.CurrentStock <= i.MinStockLevel)
            .OrderBy(i => i.Code)
            .ToListAsync();

        return items.Select(MapToDto).ToList();
    }

    public async Task<ItemDto?> GetByIdAsync(Guid id)
    {
        var item = await _context.Set<Item>()
            .Include(i => i.Category)
            .Include(i => i.Unit)
            .FirstOrDefaultAsync(i => i.Id == id && !i.IsDeleted);

        return item is null ? null : MapToDto(item);
    }

    public async Task<ItemDto> CreateAsync(CreateItemDto dto)
    {
        // Requirement 3: إنشاء صنف بأسعار يعد "إدخال سعر يدويًا" → يتطلب تصريح Inventory.Edit.
        // (المستخدم غير المصرّح يحفظ الصنف بأسعار صفرية لأن حقلي السعر مقفلان في الواجهة.)
        ManualPriceGuard.EnsureCanOverride(_priceAuthorization, ManualPriceModules.Inventory, dto.CostPrice, 0m, "سعر التكلفة");
        ManualPriceGuard.EnsureCanOverride(_priceAuthorization, ManualPriceModules.Inventory, dto.SalePrice, 0m, "سعر البيع");

        var codeExists = await _context.Set<Item>()
            .AnyAsync(i => i.Code == dto.Code && !i.IsDeleted);
        if (codeExists)
            throw new InvalidOperationException($"كود الصنف '{dto.Code}' موجود مسبقاً");

        var categoryExists = await _context.Set<Category>()
            .AnyAsync(c => c.Id == dto.CategoryId && !c.IsDeleted);
        if (!categoryExists)
            throw new InvalidOperationException("الفئة المحددة غير موجودة");

        var unitExists = await _context.Set<Unit>()
            .AnyAsync(u => u.Id == dto.UnitId && !u.IsDeleted);
        if (!unitExists)
            throw new InvalidOperationException("الوحدة المحددة غير موجودة");

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = dto.Code,
            NameAr = dto.NameAr,
            NameEn = dto.NameEn,
            CategoryId = dto.CategoryId,
            UnitId = dto.UnitId,
            CostPrice = dto.CostPrice,
            SalePrice = dto.SalePrice,
            TaxRate = dto.TaxRate,
            MinStockLevel = dto.MinStockLevel,
            MaxStockLevel = dto.MaxStockLevel,
            Barcode = dto.Barcode,
            Description = dto.Description,
            TracksExpiry = dto.TracksExpiry,
            TracksBatches = dto.TracksBatches,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Set<Item>().Add(item);
        await _context.SaveChangesAsync();

        return MapToDto(item);
    }
}
