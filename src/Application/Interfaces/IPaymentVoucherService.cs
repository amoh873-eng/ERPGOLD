using ERPSystem.Application.DTOs.Vouchers;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// عقد عمليات سندات الصرف (Payment Vouchers) — المرحلة B.
/// نفس دورة حياة سند القبض وقواعده (D1-A/D3-A)؛ الاختلاف في طرف التسوية والحساب المقابل:
/// عند الترحيل: مدين ذمم المورد (2200) — دائن الصندوق/البنك.
/// </summary>
public interface IPaymentVoucherService
{
    /// <summary>سندات الصرف (الأحدث أولاً) مع فلاتر المورد/الحالة/المدة/البحث.</summary>
    Task<List<PaymentVoucherDto>> GetAsync(
        Guid? supplierId = null,
        int? status = null,
        DateTime? from = null,
        DateTime? to = null,
        string? search = null,
        Guid? customerId = null);

    /// <summary>سند صرف واحد.</summary>
    Task<PaymentVoucherDto?> GetByIdAsync(Guid id);

    /// <summary>سجل تدقيق سند صرف (مرتّب زمنيًا) — D3-A.</summary>
    Task<List<VoucherAuditDto>> GetAuditAsync(Guid id);

    /// <summary>ينشئ مسودة سند صرف (بلا قيد محاسبي) — رقم السند ببادئة PV.</summary>
    Task<PaymentVoucherDto> CreateDraftAsync(CreatePaymentVoucherDto dto);

    /// <summary>يعدّل مسودة سند صرف (المسودة فقط).</summary>
    Task<PaymentVoucherDto> UpdateDraftAsync(Guid id, UpdatePaymentVoucherDto dto);

    /// <summary>يعتمد السند: مسودة → معتمد (بلا قيد).</summary>
    Task<PaymentVoucherDto> ApproveAsync(Guid id);

    /// <summary>
    /// يرحّل السند: معتمد → مرحّل، وينشئ قيده المحاسبي
    /// (مدين: ذمم المورد — دائن: الصندوق/البنك) في SaveChanges واحد (ذرّي).
    /// إعادة الاستدعاء على سند مُرحَّل تُعيد السند نفسه بلا قيد جديد.
    /// </summary>
    Task<PaymentVoucherDto> PostAsync(Guid id);

    /// <summary>
    /// PU-04 — ترحيل السند مع نية التوزيع (D5-و): التخصيصات تُمرَّر مع الطلب ولا تُحفظ مسبقًا.
    /// يجب أن يساوي مجموع التخصيصات مبلغ السند تمامًا، وأن تكون الفواتير مؤهلة وتخص المورد نفسه.
    /// </summary>
    Task<PaymentVoucherDto> PostAsync(
        Guid id,
        IReadOnlyCollection<PaymentAllocationInput>? allocations,
        Guid? idempotencyKey = null);

    /// <summary>يلغي السند قبل الترحيل مع الحفاظ على الأثر التاريخي (لا حذف).</summary>
    Task<PaymentVoucherDto> CancelAsync(Guid id, string? reason);
}
