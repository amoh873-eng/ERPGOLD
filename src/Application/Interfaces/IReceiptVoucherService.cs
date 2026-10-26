using ERPSystem.Application.DTOs.Vouchers;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// عقد عمليات سندات القبض (Receipt Vouchers) — المرحلة B.
///
/// دورة الحياة (D1-A): مسودة → اعتماد → ترحيل → إلغاء.
///   • لا يُنشأ أي قيد محاسبي قبل الترحيل.
///   • الترحيل يمرّ حصرًا عبر محرّك القيود المركزي IJournalEntryService.PrepareEntryAsync.
///   • لا ترحيل مزدوج: JournalEntryId يُضبط مرة واحدة، ويحميه فهرس فريد في قاعدة البيانات + قيد الحالة.
///   • كل عملية تُسجَّل في سجل التدقيق (D3-A) في نفس المعاملة (ذرّي).
/// </summary>
public interface IReceiptVoucherService
{
    /// <summary>سندات القبض (الأحدث أولاً) مع فلاتر العميل/الحالة/المدة/البحث.</summary>
    Task<List<ReceiptVoucherDto>> GetAsync(
        Guid? customerId = null,
        int? status = null,
        DateTime? from = null,
        DateTime? to = null,
        string? search = null);

    /// <summary>سند قبض واحد.</summary>
    Task<ReceiptVoucherDto?> GetByIdAsync(Guid id);

    /// <summary>سجل تدقيق سند قبض (مرتّب زمنيًا) — D3-A.</summary>
    Task<List<VoucherAuditDto>> GetAuditAsync(Guid id);

    /// <summary>ينشئ مسودة سند قبض (بلا قيد محاسبي) — رقم السند من NumberSequenceHelper ببادئة RCV.</summary>
    Task<ReceiptVoucherDto> CreateDraftAsync(CreateReceiptVoucherDto dto);

    /// <summary>يعدّل مسودة سند قبض (المسودة فقط).</summary>
    Task<ReceiptVoucherDto> UpdateDraftAsync(Guid id, UpdateReceiptVoucherDto dto);

    /// <summary>يعتمد السند: مسودة → معتمد (بلا قيد).</summary>
    Task<ReceiptVoucherDto> ApproveAsync(Guid id);

    /// <summary>
    /// يرحّل السند: معتمد → مرحّل، وينشئ قيده المحاسبي
    /// (مدين: الصندوق/البنك — دائن: ذمم العميل) في SaveChanges واحد (ذرّي).
    /// إعادة الاستدعاء على سند مُرحَّل تُعيد السند نفسه بلا قيد جديد.
    /// </summary>
    Task<ReceiptVoucherDto> PostAsync(Guid id);

    /// <summary>
    /// P0-2 (المرحلة B): ترحيل السند **مع** تخصيص صريح لمقبوضاته على فواتير العميل
    /// (جزئي/كلي) داخل نفس المعاملة. التخصيص **لا يُنشئ أي قيد محاسبي إضافي**.
    /// </summary>
    Task<ReceiptVoucherDto> PostAsync(Guid id, PostReceiptVoucherRequest? request);

    /// <summary>P0-2: حالة تخصيص سند قبض (صفوفه + المخصَّص + غير المخصص + الفواتير المؤهَّلة).</summary>
    Task<ReceiptAllocationStateDto?> GetAllocationStateAsync(Guid voucherId, DateTime? asOf = null);

    /// <summary>P0-2: فواتير العميل المؤهَّلة للتخصيص (مع المتبقي والمتاح وسبب عدم الأهلية).</summary>
    Task<List<CustomerOpenInvoiceDto>> GetEligibleInvoicesAsync(Guid customerId, DateTime? asOf = null);

    /// <summary>P0-2: اقتراح FIFO **قراءة فقط** (ترتيب تاريخ الفاتورة ثم المعرّف) — لا كتابة ولا تنفيذ تلقائي.</summary>
    Task<CustomerFifoSuggestionDto?> SuggestFifoAsync(Guid voucherId, decimal? amount = null, DateTime? asOf = null);

    /// <summary>P0-2: ضبط تخصيصات سند مُرحَّل (عكس الصافي القائم ثم إضافة الجديد — بلا حذف تاريخي).</summary>
    Task<ReceiptAllocationStateDto> SetAllocationsAsync(Guid voucherId, SetReceiptAllocationsRequest request);

    /// <summary>P0-2: عكس صف تخصيص (كلي/جزئي) بصف `−1` مرتبط بالأصل + تدقيق.</summary>
    Task<ReceiptAllocationStateDto> ReverseAllocationAsync(Guid voucherId, ReverseCustomerAllocationRequest request);

    /// <summary>يلغي السند قبل الترحيل مع الحفاظ على الأثر التاريخي (لا حذف).</summary>
    Task<ReceiptVoucherDto> CancelAsync(Guid id, string? reason);
}
