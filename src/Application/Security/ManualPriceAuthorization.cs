using System.Security.Claims;

namespace ERPSystem.Application.Security;

/// <summary>
/// Requirement 3 — تعديل السعر يدويًا: مفاتيح الموديولات المستخدمة في سلسلة الصلاحية.
/// القيم مطابقة لمفاتيح PermissionCatalog (Web) — أي أن الإذن الحالي المطلوب هو "{Module}.Edit".
/// </summary>
public static class ManualPriceModules
{
    /// <summary>الأصناف + شاشات المخزون التي تحمل سعرًا (تكلفة الحركة).</summary>
    public const string Inventory = "Inventory";

    /// <summary>فواتير ومردودات المبيعات.</summary>
    public const string Sales = "Sales";

    /// <summary>فواتير ومردودات المشتريات.</summary>
    public const string Purchases = "Purchases";
}

/// <summary>
/// هل المستخدم الحالي مصرّح له بإدخال/تعديل السعر يدويًا في موديول معيّن؟
///
/// مهم: تُنفَّذ هذه الواجهة في طبقة الاستضافة (Web: Blazor AuthenticationState / Api: HttpContext)
/// وتُحقن في خدمات Application — ولا يمكن تمريرها من الواجهة/الطلب، لذلك لا يمكن تجاوزها
/// بإرسال سعر يدوي من خارج الشاشة.
/// </summary>
public interface IManualPriceAuthorization
{
    /// <summary>يُعيد true فقط للمستخدم المصرّح (Explicit opt-in) في الموديول المحدد.</summary>
    bool CanEditManualPrice(string moduleKey);
}

/// <summary>
/// القواعد النقية لتصريح تعديل السعر (قابلة للاختبار ومشتركة بين Web و Api):
///  1) Admin / SuperAdmin → مصرّح.
///  2) وجود أذونات صريحة لهذا الموديول ("{Module}.xxx") في قاعدة البيانات (شاشة الصلاحيات)
///     → تلك الأذونات هي المرجع، فيلزم "{Module}.Edit" تحديدًا.
///  3) الوضع الحالي للنظام (لا أذونات صريحة لهذا الموديول): دور الموديول نفسه
///     (Sales/Purchases/Inventory) يمنح وصولًا كاملًا للموديول — وهو نفس ما تُصرّح به
///     صفحات الشاشات حاليًا، فلا يتأثر أي مستخدم حالي.
/// لا يُنشئ هذا أي دور أو إذن جديد — يستخدم الموجود فقط.
/// </summary>
public static class ManualPriceRules
{
    /// <summary>نفس نوع الـ Claim المستخدم في PermissionCatalog.ClaimType.</summary>
    public const string ClaimType = "Permission";

    /// <summary>الإذن المطلوب لتعديل السعر في موديول: "{Module}.Edit".</summary>
    public static string EditPermission(string moduleKey) => $"{moduleKey}.Edit";

    /// <summary>القاعدة الأساسية: تُطبَّق على أدوار المستخدم + أذوناته الصريحة.</summary>
    public static bool IsAuthorized(IEnumerable<string>? roles, IEnumerable<string>? permissionClaims, string moduleKey)
    {
        var roleSet = new HashSet<string>(roles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (roleSet.Contains("Admin") || roleSet.Contains("SuperAdmin"))
            return true;

        var modulePermissions = (permissionClaims ?? Enumerable.Empty<string>())
            .Where(v => v.StartsWith(moduleKey + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (modulePermissions.Count > 0)
        {
            var required = EditPermission(moduleKey);
            return modulePermissions.Any(v => string.Equals(v, required, StringComparison.OrdinalIgnoreCase));
        }

        // لا أذونات صريحة لهذا الموديول → دور الموديول (السلوك الحالي للنظام).
        return roleSet.Contains(moduleKey);
    }

    /// <summary>نفس القاعدة انطلاقًا من هوية جاهزة (تُستخدم عندما تكون الهوية مثبتة في الطلب).</summary>
    public static bool IsAuthorized(ClaimsPrincipal? user, string moduleKey)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        return IsAuthorized(
            user.FindAll(ClaimTypes.Role).Select(c => c.Value),
            user.FindAll(ClaimType).Select(c => c.Value),
            moduleKey);
    }
}
