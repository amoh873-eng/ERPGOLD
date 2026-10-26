using System.ComponentModel.DataAnnotations;

namespace ERPSystem.Application.DTOs.Vouchers;

/// <summary>سند صرف للعرض (بما فيه اسم المورد وأرقام الحسابات ورقم القيد الناتج).</summary>
public class PaymentVoucherDto
{
    public Guid Id { get; set; }
    public string VoucherNumber { get; set; } = string.Empty;
    public DateTime VoucherDate { get; set; }

    /// <summary>1 = مسودة · 2 = معتمد · 3 = مرحّل · 4 = ملغى (VoucherStatus).</summary>
    public int Status { get; set; }

    /// <summary>اسم الحالة بالعربية للعرض.</summary>
    public string StatusName { get; set; } = string.Empty;

    /// <summary>المورد (سند مورد) — null في سند رد العميل.</summary>
    public Guid? SupplierId { get; set; }
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>العميل المستفيد (سند رد) — null في سند المورد.</summary>
    public Guid? CustomerId { get; set; }
    public string? CustomerCode { get; set; }
    public string? CustomerName { get; set; }

    /// <summary>المردود المرجعي لسند الرد.</summary>
    public Guid? SalesReturnId { get; set; }
    public string? SalesReturnNumber { get; set; }

    /// <summary>نوع الطرف: Supplier / Customer (للعرض والفلترة).</summary>
    public string PartyType { get; set; } = "Supplier";

    /// <summary>اسم الطرف المعروض (مورد أو عميل).</summary>
    public string PartyLabel { get; set; } = string.Empty;

    /// <summary>اسم المستفيد عند السداد النقدي بلا مورد مسجّل (اختياري — للطبع فقط).</summary>
    public string? BeneficiaryName { get; set; }

    /// <summary>1 = نقدي · 2 = بنكي (PaymentMethod).</summary>
    public int PaymentMethod { get; set; }

    public Guid CashOrBankAccountId { get; set; }
    public string CashOrBankAccountLabel { get; set; } = string.Empty;

    public Guid PayableAccountId { get; set; }
    public string PayableAccountLabel { get; set; } = string.Empty;

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

/// <summary>إنشاء مسودة سند صرف — لا يُنشأ أي قيد محاسبي عند الإنشاء.</summary>
public class CreatePaymentVoucherDto
{
    /// <summary>
    /// المورد (سند صرف مورد) — أو <see cref="CustomerId"/> (سند رد عميل)، أحدهما فقط ومطلوب.
    /// لا يوجد [Required] على Guid لأن القيمة الافتراضية Guid.Empty تُرفض في الخدمة برسالة صريحة.
    /// </summary>
    public Guid? SupplierId { get; set; }

    /// <summary>العميل المستفيد من الرد — يتطلب <see cref="SalesReturnId"/>.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>المردود المرتبط — إلزامي في وضع العميل.</summary>
    public Guid? SalesReturnId { get; set; }

    public DateTime VoucherDate { get; set; } = DateTime.Today;

    /// <summary>
    /// 1 = نقدي · 2 = بنكي (يطابق Domain.Enums.PaymentMethod).
    /// PU-04 (N-7): هذه الدلالة **مختلفة عن** CreatePurchaseInvoiceDto.PaymentMethod (1=آجل · 2=شيك · 3=نقدي)
    /// — الأرقام موثّقة مركزيًا في <see cref="ERPSystem.Application.DTOs.PaymentMethodMap"/>.
    /// </summary>
    [Required]
    [Range(1, 2)]
    public int PaymentMethod { get; set; } = ERPSystem.Application.DTOs.PaymentMethodMap.VoucherCash;

    /// <summary>نقدي (VoucherPaymentMethod = 1).</summary>
    public const int PaymentCash = ERPSystem.Application.DTOs.PaymentMethodMap.VoucherCash;

    /// <summary>بنكي (VoucherPaymentMethod = 2).</summary>
    public const int PaymentBank = ERPSystem.Application.DTOs.PaymentMethodMap.VoucherBank;

    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }

    [StringLength(200)]
    public string? BeneficiaryName { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? ExternalReference { get; set; }

    [StringLength(500)]
    public string? AttachmentPath { get; set; }

    /// <summary>
    /// PU-04 (N-1/R-2) — مفتاح منع تكرار **إنشاء** السند.
    ///
    /// دلالة الاستخدام (ملزمة للواجهات):
    ///  • يُولَّد **مرة واحدة** لكل سند جديد (من الواجهة)، ويُعاد **كما هو** عند إعادة إرسال الطلب نفسه
    ///    بعد انقطاع ⇒ يُعاد السند الأصلي ولا يُنشأ سند ثانٍ.
    ///  • طلب جديد **مقصود** (دفعة أخرى فعلًا) ⇒ مفتاح جديد (وإلا ظُنّ تكرارًا).
    ///  • `null` أو `Guid.Empty` ⇒ لا حماية تكرار (توافق خلفي مع الطلبات القديمة).
    /// </summary>
    public Guid? IdempotencyKey { get; set; }
}

/// <summary>تعديل مسودة سند صرف (مسموح للمسودة فقط).</summary>
public class UpdatePaymentVoucherDto
{
    /// <summary>طرف السند — لا يُغيَّر بعد الإنشاء (يُتحقق من مطابقته للقيم المحفوظة).</summary>
    public Guid? SupplierId { get; set; }

    /// <summary>العميل المحفوظ على سند الرد — للتحقق من ثبات الطرف، لا للتغيير.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>المردود المحفوظ على سند الرد — للتحقق من ثبات الربط، لا للتغيير.</summary>
    public Guid? SalesReturnId { get; set; }

    public DateTime VoucherDate { get; set; } = DateTime.Today;

    [Required]
    [Range(1, 2)]
    public int PaymentMethod { get; set; } = 1;

    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }

    [StringLength(200)]
    public string? BeneficiaryName { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? ExternalReference { get; set; }

    [StringLength(500)]
    public string? AttachmentPath { get; set; }
}
