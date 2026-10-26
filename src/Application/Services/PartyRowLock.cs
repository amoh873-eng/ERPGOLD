using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// قفل صف الطرف (عميل/مورد) داخل المعاملة الجارية — لمنع فقدان التحديث (lost update)
/// على الرصيد التشغيلي المكرَّر (<c>CurrentBalance</c>) عند تزامن مستندين على نفس الطرف.
///
/// لماذا؟ الرصيد التشغيلي عمود مخزَّن يُقرأ ثم يُكتب من الذاكرة؛ ورمز التزامن <c>RowVersion</c>
/// على PostgreSQL عمود <c>bytea</c> لا يولّد الخادم قيمته ⇒ لا يكشف التعارض. لذلك يُستخدم
/// <c>SELECT … FOR UPDATE</c> ثم <c>ReloadAsync</c> قبل الكتابة — وهو النمط المطبَّق أصلًا
/// على الموردين في مسارات المشتريات وسندات الصرف والمردودات.
///
/// ملاحظة إلزامية: القفل بلا معاملة مفتوحة لا فائدة منه (يُحرَّر فور انتهاء الجملة)،
/// لذا يُستدعى حصرًا داخل مسارات ملفوفة بمعاملة (<see cref="DbTransactionHelper"/> أو معاملة صريحة).
/// </summary>
internal static class PartyRowLock
{
    /// <summary>يقفل صف العميل (FOR UPDATE) داخل المعاملة الجارية.</summary>
    public static Task LockCustomerAsync(DbContext context, Guid customerId)
        => context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Customers\" WHERE \"Id\" = {customerId} FOR UPDATE");

    /// <summary>يقفل صف المورد (FOR UPDATE) داخل المعاملة الجارية.</summary>
    public static Task LockSupplierAsync(DbContext context, Guid supplierId)
        => context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Suppliers\" WHERE \"Id\" = {supplierId} FOR UPDATE");
}
