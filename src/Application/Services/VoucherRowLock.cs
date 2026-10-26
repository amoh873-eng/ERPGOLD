using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// قفل صف السند (قبض/صرف) داخل المعاملة الجارية — لمنع **سباق الإلغاء مع الترحيل**
/// ولمنع الترحيل المزدوج تحت التزامن.
///
/// لماذا؟ القرار في <c>CancelAsync</c> و<c>PostCoreAsync</c> يُبنى على حالة السند المقروءة
/// في الذاكرة. طلبٌ آخر (إلغاء أو ترحيل) قد يقرأ الحالة نفسها قبل التزامنا ثم يكتب نتيجته
/// بعد التزامنا ⇒ يُطمس قراره (سند «ملغى» له قيد مرحَّل، أو ترحيل مزدوج).
/// الحل المطبَّق في النظام (لا يُعتمد على <c>RowVersion</c> وحده — عمود <c>bytea</c> لا يولّد
/// PostgreSQL قيمته تلقائيًا): <c>SELECT … FOR UPDATE</c> ثم <c>ReloadAsync</c> قبل القرار.
///
/// ملاحظة إلزامية: القفل بلا معاملة مفتوحة لا فائدة منه (يُحرَّر فور انتهاء الجملة)،
/// لذلك يُستدعى حصرًا داخل مسارات ملفوفة بمعاملة (<see cref="DbTransactionHelper"/>).
/// </summary>
internal static class VoucherRowLock
{
    /// <summary>يقفل صف سند القبض (FOR UPDATE) داخل المعاملة الجارية.</summary>
    public static Task LockReceiptVoucherAsync(DbContext context, Guid voucherId)
        => context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"ReceiptVouchers\" WHERE \"Id\" = {voucherId} FOR UPDATE");

    /// <summary>يقفل صف سند الصرف (FOR UPDATE) داخل المعاملة الجارية.</summary>
    public static Task LockPaymentVoucherAsync(DbContext context, Guid voucherId)
        => context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PaymentVouchers\" WHERE \"Id\" = {voucherId} FOR UPDATE");
}
