using ERPSystem.Domain.Common;
using ERPSystem.Domain.Enums;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// مردود مشتريات مرتبط بفاتورة مشتريات أصلية: يعيد البضاعة للمورد ويعكس القيد المحاسبي.
/// </summary>
public class PurchaseReturn : BaseEntity
{
    public string ReturnNumber { get; set; } = string.Empty;
    public Guid PurchaseInvoiceId { get; set; }
    public PurchaseInvoice? PurchaseInvoice { get; set; }
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public Guid WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public DateTime ReturnDate { get; set; }
    public DocumentStatus Status { get; set; }

    public decimal SubTotal { get; set; }
    public decimal TotalAmount { get; set; }
    public string? Note { get; set; }

    /// <summary>
    /// PU-04 (N-3) — نوع تسوية المردود: يحدد الحساب المدين وأثر رصيد المورد.
    /// الافتراضي: OnAccountCredit (سلوك الفواتير الآجلة القائم).
    /// </summary>
    public PurchaseReturnSettlementType SettlementType { get; set; } = PurchaseReturnSettlementType.OnAccountCredit;

    /// <summary>مرجع إثبات رد النقد (إيصال/كشف) — إلزامي عند CashRefund.</summary>
    public string? RefundReference { get; set; }

    public ICollection<PurchaseReturnLine> Lines { get; set; } = new List<PurchaseReturnLine>();

    /// <summary>القيد المحاسبي العكسي الناتج عند الترحيل.</summary>
    public Guid? JournalEntryId { get; set; }
}