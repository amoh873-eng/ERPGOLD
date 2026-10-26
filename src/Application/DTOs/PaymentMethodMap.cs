namespace ERPSystem.Application.DTOs;

/// <summary>
/// PU-04 (N-7) — خريطة صريحة لدلالات <c>PaymentMethod</c> في طبقة الواجهات (DTO/API)،
/// لمنع الخلط بين فضاءين مختلفين لنفس اسم الحقل:
///
///   • فاتورة المشتريات (<see cref="Purchases.CreatePurchaseInvoiceDto.PaymentMethod"/>):
///       1 = آجل (OnAccount) · 2 = شيك (Check) · 3 = نقدي (Cash)
///   • سند الصرف/القبض (<see cref="Vouchers.CreatePaymentVoucherDto.PaymentMethod"/>):
///       1 = نقدي (Cash) · 2 = بنكي (Bank)  — ويطابق <c>Domain.Enums.PaymentMethod</c>
///
/// القيم الرقمية **لم تتغيّر** (توافق خلفي كامل مع الواجهات والبيانات القائمة)،
/// وأُضيفت أسماء صريحة + تحقق + وصف نصّي + اختبارات تثبت الدلالة لكل سياق.
/// </summary>
public static class PaymentMethodMap
{
    // ── فاتورة المشتريات ─────────────────────────────────────────────
    public const int InvoiceOnAccount = 1;
    public const int InvoiceCheck = 2;
    public const int InvoiceCash = 3;

    // ── سند الصرف/القبض ──────────────────────────────────────────────
    public const int VoucherCash = 1;
    public const int VoucherBank = 2;

    public static bool IsValidInvoice(int? value)
        => value is null or InvoiceOnAccount or InvoiceCheck or InvoiceCash;

    public static bool IsValidVoucher(int value)
        => value is VoucherCash or VoucherBank;

    /// <summary>هل طريقة دفع الفاتورة تعني «نقدي فعلي من الصندوق»؟ (الشيك ليس نقدًا متاحًا)</summary>
    public static bool IsInvoiceCash(int? value) => value == InvoiceCash;

    public static bool IsInvoiceCheck(int? value) => value == InvoiceCheck;

    public static string DescribeInvoice(int? value) => value switch
    {
        InvoiceOnAccount => "آجل (OnAccount)",
        InvoiceCheck => "شيك (Check)",
        InvoiceCash => "نقدي (Cash)",
        null => "غير محدّد (يُستنتج من نوع الفاتورة)",
        _ => "غير صالح"
    };

    public static string DescribeVoucher(int value) => value switch
    {
        VoucherCash => "نقدي (Cash)",
        VoucherBank => "بنكي (Bank)",
        _ => "غير صالح"
    };
}
