using ERPSystem.Application.DTOs.Vouchers;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// PU-04 (D7-ب / D13-B / H-1) — عكس سند صرف مُرحَّل عبر مستند عكس مستقل.
/// العكس الجزئي يتطلب إعادة توزيع بنفس المبلغ للحفاظ على ΣA = Amount.
/// </summary>
public interface IPaymentVoucherReversalService
{
    /// <summary>
    /// عكس سند صرف **كاملًا** في معاملة واحدة: قيد عكسي + صفوف تخصيص سالبة + حالة الأصل Reversed.
    /// العكس الجزئي مرفوض لأنه يخلق مبلغًا غير موزع؛ استخدم <see cref="ReallocateAsync"/> للتصحيح الجزئي.
    /// </summary>
    Task<PaymentVoucherReversalDto> ReverseAsync(CreatePaymentVoucherReversalDto dto);

    /// <summary>
    /// إعادة توزيع مبلغ تخصيص بين الفواتير (D13-B/H-1) — تصحيح بحت **بلا قيد محاسبي**،
    /// يحافظ على ΣA = Amount، ويُسجَّل بصفوف سالبة للأصل وموجبة للوجهة.
    /// </summary>
    Task<List<PaymentAllocationDto>> ReallocateAsync(ReallocateAllocationsDto dto);

    /// <summary>مستندات العكس الخاصة بسند أصلي (0 أو 1).</summary>
    Task<List<PaymentVoucherReversalDto>> GetByVoucherAsync(Guid originalVoucherId);

    /// <summary>تخصيصات سند (موجبة وسالبة) للعرض والتدقيق.</summary>
    Task<List<PaymentAllocationDto>> GetAllocationsAsync(Guid paymentVoucherId);
}
