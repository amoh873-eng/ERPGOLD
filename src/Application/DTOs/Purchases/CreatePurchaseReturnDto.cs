using System.ComponentModel.DataAnnotations;

namespace ERPSystem.Application.DTOs.Purchases;

/// <summary>DTO لإنشاء (وترحيل) مردود مشتريات مرتبط بفاتورة أصلية.</summary>
public class CreatePurchaseReturnDto
{
    [Required(ErrorMessage = "الفاتورة الأصلية مطلوبة")]
    public Guid PurchaseInvoiceId { get; set; }

    [Required(ErrorMessage = "المخزن مطلوب")]
    public Guid WarehouseId { get; set; }

    public DateTime ReturnDate { get; set; } = DateTime.Today;

    [StringLength(1000)]
    public string? Note { get; set; }

    /// <summary>
    /// PU-04 (N-3) — نوع تسوية المردود: 1 = يخفض الذمم (OnAccountCredit) · 2 = رد نقدي فعلي (CashRefund).
    /// null ⇒ يُستنتج من نوع الفاتورة (آجلة ⇒ 1، نقدية ⇒ 2 مع إلزام مرجع الإثبات).
    /// لا يوجد نوع «مطالبة» لأن حساب المطالبات لم يُعتمد (N-4).
    /// </summary>
    public int? SettlementType { get; set; }

    /// <summary>مرجع إثبات رد النقد (رقم إيصال/كشف) — إلزامي عند CashRefund.</summary>
    [StringLength(200)]
    public string? RefundReference { get; set; }

    [MinLength(1, ErrorMessage = "يجب إضافة بند واحد على الأقل")]
    public List<CreatePurchaseReturnLineDto> Lines { get; set; } = new();
}