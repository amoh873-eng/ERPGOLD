using System.ComponentModel.DataAnnotations;

namespace ERPSystem.Application.DTOs.Purchases;

/// <summary>DTO لإنشاء (وترحيل) فاتورة مشتريات.</summary>
public class CreatePurchaseInvoiceDto : IValidatableObject
{
    /// <summary>
    /// أسلوب الدفع (D-04). القيم: 1/آجل (أو null) · 2/شيك · 3/نقدي.
    /// PU-04 (N-7): الأرقام موثّقة مركزيًا في <see cref="PaymentMethodMap"/> لتفادي الخلط مع
    /// <c>CreatePaymentVoucherDto.PaymentMethod</c> (1=نقدي · 2=بنكي).
    /// </summary>
    public const int PaymentCash = PaymentMethodMap.InvoiceCash;
    public const int PaymentOnAccount = PaymentMethodMap.InvoiceOnAccount;
    public const int PaymentCheck = PaymentMethodMap.InvoiceCheck;

    /// <summary>
    /// مفتاح التمييز (Idempotency) — يُولَّد في الواجهة مرة واحدة لكل «نية حفظ».
    /// إعادة إرسال الطلب بنفس المفتاح لا تُنشئ فاتورة/قيدًا/حركة مخزون جديدة، بل تُعيد النتيجة الأصلية.
    /// </summary>
    public Guid? IdempotencyKey { get; set; }

    [Required(ErrorMessage = "المورد مطلوب")]
    public Guid SupplierId { get; set; }

    [Required(ErrorMessage = "المخزن مطلوب")]
    public Guid WarehouseId { get; set; }

    public DateTime InvoiceDate { get; set; } = DateTime.Today;

    public DateTime? DueDate { get; set; }

    [Range(1, 2, ErrorMessage = "نوع الفاتورة غير صالح")]
    public int InvoiceType { get; set; }

    [StringLength(1000)]
    public string? Note { get; set; }

    [MinLength(1, ErrorMessage = "يجب إضافة بند واحد على الأقل")]
    public List<CreatePurchaseInvoiceLineDto> Lines { get; set; } = new();

    /// <summary>
    /// أسلوب سداد فاتورة المشتريات: null أو 1 = آجل (يُدين المورد 2200 — الوضع التاريخي)،
    /// 2 = شيك (تُسجَّل فاتورة على المورد + قيد إصدار شيك يخفض الالتزام فوراً).
    /// </summary>
    public int? PaymentMethod { get; set; }

    /// <summary>رقم الشيك المطبوع — مطلوب عندما PaymentMethod = 2.</summary>
    [StringLength(100)]
    public string? CheckNumber { get; set; }

    /// <summary>اسم البنك — مطلوب عندما PaymentMethod = 2.</summary>
    [StringLength(150)]
    public string? CheckBankName { get; set; }

    /// <summary>الفرع (اختياري).</summary>
    [StringLength(150)]
    public string? CheckBranch { get; set; }

    /// <summary>تاريخ كتابة الشيك (افتراضياً تاريخ الفاتورة).</summary>
    public DateTime? CheckIssueDate { get; set; }

    /// <summary>تاريخ الاستحقاق — مطلوب عندما PaymentMethod = 2.</summary>
    public DateTime? CheckDueDate { get; set; }

    /// <summary>
    /// D-04: فرض التوافق بين نوع الفاتورة وطريقة الدفع.
    /// القواعد: نقدي(1) ⇒ «نقدي»(3) أو «شيك»(2) · آجل(2) ⇒ «آجل»(1 أو null) أو «شيك»(2).
    /// والقيمة null تبقى مقبولة (تُستنتج من نوع الفاتورة) حفاظًا على التوافق الخلفي.
    /// الشيك المؤجل لا يُعتبر نقدًا متاحًا — لا يمر بمسار الصندوق إطلاقًا.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PaymentMethod is null) yield break;

        if (PaymentMethod is not (PaymentOnAccount or PaymentCheck or PaymentCash))
        {
            yield return new ValidationResult("طريقة الدفع غير صالحة.",
                new[] { nameof(PaymentMethod) });
            yield break;
        }

        if (InvoiceType == 1 && PaymentMethod == PaymentOnAccount)
            yield return new ValidationResult(
                "تعارض: فاتورة نقدية بطريقة دفع «آجل». اختر «نقدي» أو «شيك».",
                new[] { nameof(PaymentMethod) });

        if (InvoiceType == 2 && PaymentMethod == PaymentCash)
            yield return new ValidationResult(
                "تعارض: فاتورة آجلة بطريقة دفع «نقدي». اختر «آجل» أو «شيك».",
                new[] { nameof(PaymentMethod) });
    }
}