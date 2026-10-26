using System.Security.Claims;

namespace ERPSystem.Application.Security;

/// <summary>
/// تصريح عمليات الفترات المحاسبية (قفل/فتح) — يُحقن من طبقة الاستضافة (Web/Api)،
/// فلا يمكن تجاوزه بإرسال طلب مباشر إلى خدمة Application.
///
/// لا يُنشئ أي إذن جديد: يستخدم أذونات موديول «المحاسبة» الموجودة
/// (Accounting.View للعرض · Accounting.Edit للإغلاق وإعادة الفتح) — نفس نمط
/// <see cref="IOpeningBalanceAuthorization"/>.
/// </summary>
public interface IAccountingPeriodAuthorization
{
    /// <summary>عرض الفترات (Accounting.View).</summary>
    bool CanView();

    /// <summary>إغلاق فترة (Accounting.Edit).</summary>
    bool CanClose();

    /// <summary>إعادة فتح فترة (Accounting.Edit).</summary>
    bool CanReopen();
}

/// <summary>
/// القواعد النقية لتصريح الفترات المحاسبية (مشتركة بين Web و Api وقابلة للاختبار):
///  1) Admin / SuperAdmin → مصرّح.
///  2) وجود أذونات صريحة لموديول «Accounting» → تلك الأذونات هي المرجع.
///  3) لا أذونات صريحة للموديول → دور «Accounting» يمنح وصولًا كاملًا (السلوك الحالي).
/// </summary>
public static class AccountingPeriodRules
{
    /// <summary>موديول المحاسبة — مطابق لمفتاح PermissionCatalog (Web).</summary>
    public const string Module = "Accounting";

    /// <summary>نفس نوع الـ Claim المستخدم في PermissionCatalog.ClaimType.</summary>
    public const string ClaimType = "Permission";

    public const string ViewAction = "View";
    public const string EditAction = "Edit";

    /// <summary>مفتاح الإذن المطلوب لفعل في موديول المحاسبة: "Accounting.{action}".</summary>
    public static string Permission(string action) => $"{Module}.{action}";

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

        return roleSet.Contains(Module);
    }

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
