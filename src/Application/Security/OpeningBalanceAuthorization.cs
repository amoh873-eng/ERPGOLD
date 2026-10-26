using System.Security.Claims;

namespace ERPSystem.Application.Security;

/// <summary>
/// تصريح عمليات الأرصدة الافتتاحية — يُحقن من طبقة الاستضافة (Web/Api) ولا يأتي من الواجهة،
/// فلا يمكن تجاوز الفحص بإرسال طلب مباشر إلى خدمة Application.
///
/// لا يُنشئ هذا أي Permission أو Role جديد: يستخدم أذونات موديول «المحاسبة» الموجودة
/// في PermissionCatalog (Accounting.View/Add/Edit/Delete) عبر القاعدة المشتركة أدناه.
/// </summary>
public interface IOpeningBalanceAuthorization
{
    /// <summary>إنشاء مسودة (Accounting.Add).</summary>
    bool CanCreate();

    /// <summary>تعديل مسودة (Accounting.Edit).</summary>
    bool CanUpdate();

    /// <summary>ترحيل مسودة (Accounting.Edit).</summary>
    bool CanPost();

    /// <summary>عكس دفعة مُرحَّلة (Accounting.Edit).</summary>
    bool CanReverse();

    /// <summary>حذف مسودة (Accounting.Delete).</summary>
    bool CanDelete();
}

/// <summary>
/// القواعد النقية لتصريح الأرصدة الافتتاحية (قابلة للاختبار ومشتركة بين Web و Api):
/// نفس دلالات ManualPriceRules لكن مع فعل (Action) متغيّر بدل «Edit» الثابت.
///  1) Admin / SuperAdmin → مصرّح.
///  2) وجود أذونات صريحة لموديول «Accounting» → تلك الأذونات هي المرجع، فيلزم الفعل المطلوب.
///  3) لا أذونات صريحة للموديول → دور «Accounting» يمنح وصولاً كاملاً (السلوك الحالي للنظام).
/// </summary>
public static class OpeningBalanceRules
{
    /// <summary>موديول المحاسبة — مطابق لمفتاح PermissionCatalog (Web).</summary>
    public const string Module = "Accounting";

    /// <summary>نفس نوع الـ Claim المستخدم في PermissionCatalog.ClaimType.</summary>
    public const string ClaimType = "Permission";

    public const string AddAction = "Add";
    public const string EditAction = "Edit";
    public const string DeleteAction = "Delete";

    /// <summary>مفتاح الإذن المطلوب لفعل في موديول المحاسبة: "Accounting.{action}".</summary>
    public static string Permission(string action) => $"{Module}.{action}";

    /// <summary>القاعدة الأساسية: تُطبَّق على أدوار المستخدم + أذوناته الصريحة.</summary>
    public static bool IsAuthorized(IEnumerable<string>? roles, IEnumerable<string>? permissionClaims, string action)
    {
        var roleSet = new HashSet<string>(roles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (roleSet.Contains("Admin") || roleSet.Contains("SuperAdmin"))
            return true;

        var modulePermissions = (permissionClaims ?? Enumerable.Empty<string>())
            .Where(v => v.StartsWith(Module + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (modulePermissions.Count > 0)
        {
            var required = Permission(action);
            return modulePermissions.Any(v => string.Equals(v, required, StringComparison.OrdinalIgnoreCase));
        }

        // لا أذونات صريحة لموديول المحاسبة → دور «المحاسبة» يمنح وصولاً كاملاً.
        return roleSet.Contains(Module);
    }

    /// <summary>نفس القاعدة انطلاقًا من هوية جاهزة.</summary>
    public static bool IsAuthorized(ClaimsPrincipal? user, string action)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        return IsAuthorized(
            user.FindAll(ClaimTypes.Role).Select(c => c.Value),
            user.FindAll(ClaimType).Select(c => c.Value),
            action);
    }
}
