using System.ComponentModel.DataAnnotations;

namespace ERPSystem.Application.DTOs.Vouchers;

/// <summary>سند قبض للعرض (بما فيه اسم العميل وأرقام الحسابات ورقم القيد الناتج).</summary>
public class ReceiptVoucherDto
{
    public Guid Id { get; set; }
    public string VoucherNumber { get; set; } = string.Empty;
    public DateTime VoucherDate { get; set; }

    /// <summary>1 = مسودة · 2 = معتمد · 3 = مرحّل · 4 = ملغى (VoucherStatus).</summary>
    public int Status { get; set; }

    /// <summary>اسم الحالة بالعربية للعرض.</summary>
    public string StatusName { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>1 = نقدي · 2 = بنكي (PaymentMethod).</summary>
    public int PaymentMethod { get; set; }

    public Guid CashOrBankAccountId { get; set; }
    public string CashOrBankAccountLabel { get; set; } = string.Empty;

    public Guid ReceivableAccountId { get; set; }
    public string ReceivableAccountLabel { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public string? ExternalReference { get; set; }
    public string? AttachmentPath { get; set; }

    public Guid? JournalEntryId { get; set; }
    public string? JournalEntryNumber { get; set; }

    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? PostedAt { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>إنشاء مسودة سند قبض — لا يُنشأ أي قيد محاسبي عند الإنشاء.</summary>
public class CreateReceiptVoucherDto
{
    [Required]
    public Guid CustomerId { get; set; }

    public DateTime VoucherDate { get; set; } = DateTime.Today;

    /// <summary>1 = نقدي · 2 = بنكي.</summary>
    [Required]
    [Range(1, 2)]
    public int PaymentMethod { get; set; } = 1;

    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? ExternalReference { get; set; }

    [StringLength(500)]
    public string? AttachmentPath { get; set; }
}

/// <summary>تعديل مسودة سند قبض (مسموح للمسودة فقط).</summary>
public class UpdateReceiptVoucherDto
{
    [Required]
    public Guid CustomerId { get; set; }

    public DateTime VoucherDate { get; set; } = DateTime.Today;

    [Required]
    [Range(1, 2)]
    public int PaymentMethod { get; set; } = 1;

    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? ExternalReference { get; set; }

    [StringLength(500)]
    public string? AttachmentPath { get; set; }
}

/// <summary>سطر سجل تدقيق سند (D3-A) للعرض.</summary>
public class VoucherAuditDto
{
    public DateTime PerformedAt { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? PerformedBy { get; set; }
    public string? Details { get; set; }
    public Guid? JournalEntryId { get; set; }
}
