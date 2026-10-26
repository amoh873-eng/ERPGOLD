using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// PU-04 — تمييز انتهاك القيد الفريد في PostgreSQL (`23505`) دون اعتمادية مباشرة على Npgsql
/// في طبقة Application (نفس أسلوب `PurchaseInvoiceService.IsIdempotencyConflict` و
/// `OpeningBalanceService` — نمط مرجعي قائم في هذا المشروع).
///
/// القاعدة: لا يُحوَّل أي استثناء إلى نجاح إلا إذا طابق **اسم الفهرس المتوقع** صراحةً؛
/// فتُترك بقية أخطاء قاعدة البيانات كما هي (لا إخفاء).
/// </summary>
internal static class UniqueViolation
{
    /// <summary>هل الاستثناء ناتج عن انتهاك قيد فريد/مفتاح أساسي؟</summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        var msg = Message(ex);
        return msg.Contains("23505", StringComparison.Ordinal)
               || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>هل الانتهاك يخصّ الفهرس المسمّى تحديدًا؟ (الاسم يُطابَق داخل نص الخطأ).</summary>
    public static bool IsOnIndex(DbUpdateException ex, string indexName)
        => ex is not null
           && !string.IsNullOrWhiteSpace(indexName)
           && Message(ex).Contains(indexName, StringComparison.OrdinalIgnoreCase)
           && IsUniqueViolation(ex);

    /// <summary>نص الخطأ الكامل (الرسالة الخارجية + الداخلية) بأسلوب موحّد.</summary>
    public static string Message(DbUpdateException ex)
        => (ex.InnerException?.Message ?? string.Empty) + " " + (ex.Message ?? string.Empty);
}
