using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// إصلاحات R5/R6 (جلسة 2026-10-21) — إدارة الكيانات المعلّقة في سياق EF طويل العمر.
///
/// لماذا؟ في Blazor Server يكون <c>AppDbContext</c> محدود النطاق (Scoped) فيُعاش مع دائرة العرض
/// (Circuit) لا مع كل تفاعل. وأي محاولة إنشاء مستند تفشل بعد إضافة كياناتها كانت تترك كيانات
/// <c>Added</c> في السياق؛ فيحفظها أول <c>SaveChanges</c> لاحق ⇒ مستند «مرحّل» بلا قيد
/// (وحركة مخزون بلا مقابل محاسبي). هذه الأدوات:
///   • <see cref="EnsureNoPendingDocuments"/>: حاجز وقائي قبل بدء أي عملية مستند (فشل مغلق).
///   • <see cref="DetachPendingDocuments"/>: تنظيف **محدود بالأنواع** بعد الفشل (لا تفريغ شامل للسياق).
/// </summary>
internal static class DocumentFailureCleanup
{
    /// <summary>
    /// الأنواع التي تُنتجها مسارات إنشاء/ترحيل المستندات، ويجب ألا تبقى معلّقة بعد فشل العملية.
    /// تشمل رؤوس المستندات وبنودها وحركات المخزون والقيود وأرصدة الأطراف/الحسابات والأصناف والدُفعات.
    /// </summary>
    private static readonly Type[] DocumentTypes =
    {
        typeof(SalesInvoice), typeof(SalesInvoiceLine),
        typeof(SalesReturn), typeof(SalesReturnLine),
        typeof(PurchaseInvoice), typeof(PurchaseInvoiceLine),
        typeof(PurchaseReturn), typeof(PurchaseReturnLine),
        typeof(StockMovement), typeof(JournalEntry), typeof(JournalEntryLine),
        typeof(BankCheck), typeof(ItemBatch),
        typeof(Item), typeof(Account), typeof(Customer), typeof(Supplier)
    };

    private static bool IsDocumentEntity(object entity)
        => DocumentTypes.Any(t => t.IsInstanceOfType(entity));

    /// <summary>
    /// حاجز وقائي (R6): إن كان السياق يحمل كيانات مستندات مُضافة من محاولة سابقة فاشلة، نرفض بدء
    /// عملية جديدة برسالة واضحة بدل حفظها ضمناً مع المستند الجديد.
    /// </summary>
    public static void EnsureNoPendingDocuments(DbContext context)
    {
        var pending = context.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added && IsDocumentEntity(e.Entity))
            .Select(e => e.Entity.GetType().Name)
            .Distinct()
            .ToList();

        if (pending.Count > 0)
            throw new InvalidOperationException(
                "يوجد مستند غير مكتمل في الجلسة الحالية (" + string.Join(", ", pending) +
                ") — أعد المحاولة أو أعد تحميل الصفحة قبل إنشاء مستند جديد.");
    }

    /// <summary>
    /// تنظيف محدود (R5): فصل كيانات المستندات المعلّقة (Added/Modified/Deleted) بعد فشل العملية،
    /// فلا يمكن لأي <c>SaveChanges</c> لاحق أن يحفظها، وتبقى بقية أنواع السياق كما هي.
    /// </summary>
    public static void DetachPendingDocuments(DbContext context)
    {
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entries)
        {
            if (IsDocumentEntity(entry.Entity))
                entry.State = EntityState.Detached;
        }
    }
}
