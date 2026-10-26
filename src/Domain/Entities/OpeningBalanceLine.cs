using ERPSystem.Domain.Common;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// بند في دفعة الأرصدة الافتتاحية: رصيد مدين أو دائن على حساب متحرك (Leaf) محدَّد.
/// يُخزَّن AccountId بعد التحقق (حساب موجود + متحرك)، فالترحيل لا يخمّن حساباً ولا يهبط تلقائياً.
/// </summary>
public class OpeningBalanceLine : BaseEntity
{
    /// <summary>الدفعة التي ينتمي إليها البند.</summary>
    public Guid BatchId { get; set; }
    public OpeningBalanceBatch? Batch { get; set; }

    /// <summary>الحساب المتحرك (Leaf) — تم التحقق من وجوده ومن كونه غير رئيسي.</summary>
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>المبلغ المدين (صفر إذا كان البند دائناً).</summary>
    public decimal DebitAmount { get; set; }

    /// <summary>المبلغ الدائن (صفر إذا كان البند مديناً).</summary>
    public decimal CreditAmount { get; set; }

    // ─────────────────────────── البُعد التفصيلي (2026-10-25) ───────────────────────────
    // الهدف: أن يحمل الرصيد الافتتاحي تفصيله (طرف الذمم / الصنف المخزني) بحيث تتساوى
    // الحسابات الرقابية (1200 العملاء · 2200 الموردين · 1300 المخزون) مع مجموع الأرصدة التفصيلية.
    // البنود على هذه الحسابات الرقابية لا تُقبل بدون تفصيلها المطابق (قاعدة يفرضها OpeningBalanceService)،
    // أما بقية الحسابات فتبقى بلا تفصيل كما هي (سلوك سابق بلا تغيير).

    /// <summary>العميل صاحب الرصيد — إلزامي على بنود حساب العملاء (1200).</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>المورد صاحب الرصيد — إلزامي على بنود حساب الموردين (2200).</summary>
    public Guid? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>الصنف — إلزامي على بنود حساب المخزون (1300).</summary>
    public Guid? ItemId { get; set; }
    public Item? Item { get; set; }

    /// <summary>المخزن الذي يُقيَّد فيه الرصيد الافتتاحي للصنف — إلزامي مع بند الصنف.</summary>
    public Guid? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }

    /// <summary>الكمية الافتتاحية للصنف (موجبة دائماً؛ الإشارة تأتي من مدين/دائن البند).</summary>
    public decimal? Quantity { get; set; }

    /// <summary>تكلفة الوحدة الافتتاحية للصنف — بها تُقيَّم الكمية (قيمة البند = الكمية × التكلفة).</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>
    /// حركة المخزون الافتتاحية الناتجة عن هذا البند (MovementType.OpeningBalance).
    /// تُملأ عند الترحيل لتكون مرجعًا للعكس (وتُستخدم في مطابقة المخزون التفصيلي).
    /// </summary>
    public Guid? StockMovementId { get; set; }
    public StockMovement? StockMovement { get; set; }

    /// <summary>ملاحظة اختيارية على البند.</summary>
    public string? Note { get; set; }
}
