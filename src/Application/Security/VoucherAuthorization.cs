using System.Security.Claims;

namespace ERPSystem.Application.Security;

/// <summary>
/// D2-A — تصريح عمليات سندات القبض والصرف.
///
/// تُنفَّذ هذه الواجهة في طبقة الاستضافة (Web/Api) وتُحقن في خدمة السندات، ولا يأتي أي شيء
/// منها من الواجهة أو من الطلب ⇒ لا يمكن تجاوز فحص الاعتماد/الترحيل/الإلغاء من الشاشة،
/// ولا باستدعاء خدمة Application مباشرة (تجاوز الواجهة).
///
/// نفس نمط <see cref="IOpeningBalanceAuthorization"/> و<see cref="IManualPriceAuthorization"/> القائمين.
/// </summary>
public interface IVoucherAuthorization
{
    /// <summary>عرض السندات والتقارير المرتبطة بها (Vouchers.View).</summary>
    bool CanView();

    /// <summary>إنشاء مسودة سند (Vouchers.Add).</summary>
    bool CanCreate();

    /// <summary>تعديل مسودة سند (Vouchers.Edit).</summary>
    bool CanEdit();

    /// <summary>اعتماد سند (Vouchers.Approve).</summary>
    bool CanApprove();

    /// <summary>ترحيل سند وإنشاء قيده المحاسبي (Vouchers.Post).</summary>
    bool CanPost();

    /// <summary>إلغاء سند قبل الترحيل (Vouchers.Cancel).</summary>
    bool CanCancel();

    /// <summary>
    /// PU-04 (N-2) — عكس سند مُرحَّل (Vouchers.Reverse): صلاحية مستقلة عن الترحيل.
    /// الافتراضي رفض (فشل مغلق) حتى تُمنح صراحةً.
    /// </summary>
    bool CanReverse() => false;

    /// <summary>
    /// P0-2 (المرحلة B) — تخصيص مقبوضات العميل على فواتيره (Vouchers.Allocate).
    /// الافتراضي رفض (فشل مغلق) حتى تُمنح صراحةً في المضيف.
    /// </summary>
    bool CanAllocate() => false;

    /// <summary>
    /// SUB-2b/P3 — رد مبلغ ناتج عن مردود مبيعات (Vouchers.Refund).
    ///
    /// صلاحية **مستقلة تمامًا**: لا تُشتق من دور Accounting/Expenses ولا من أي صلاحية أخرى،
    /// ولا تُمنح بالـfallback. الافتراضي رفض (فشل مغلق) حتى يمنحها مدير النظام صراحةً.
    /// </summary>
    bool CanRefund() => false;

    /// <summary>اسم المستخدم الحالي — لتسجيل أثر التدقيق (D3-A) من هوية الجلسة فقط.</summary>
    string? CurrentUserName();
}

/// <summary>
/// القواعد النقية لتصريح سندات القبض والصرف (قابلة للاختبار ومشتركة بين Web و Api):
///  1) Admin / SuperAdmin → مصرّح.
///  2) وجود أذونات صريحة لموديول «Vouchers» (شاشة الصلاحيات: Vouchers.View/Add/Edit/Approve/Post/Cancel)
///     → تلك الأذونات هي المرجع، فيلزم الفعل المطلوب تحديدًا ⇒ الفصل بين الاعتماد/الترحيل/الإلغاء مُطبَّق فعليًا.
///  3) لا أذونات صريحة للموديول (الوضع الحالي للنظام: لا توجد Claims أذونات مُنشأة) → دورا
///     «المصاريف» أو «المحاسبة» يمنحان وصولًا كاملًا، وهو نفس ما تُصرّح به شاشات المصاريف اليوم،
///     فلا يتأثر أي مستخدم حالي ولا يُنشأ دور جديد.
/// </summary>
public static class VoucherRules
{
    /// <summary>مفتاح الموديول في الفهرس (PermissionCatalog) وفي الأذونات: "Vouchers".</summary>
    public const string Module = "Vouchers";

    /// <summary>نفس نوع الـ Claim المستخدم في PermissionCatalog.ClaimType.</summary>
    public const string ClaimType = "Permission";

    public const string ViewAction = "View";
    public const string AddAction = "Add";
    public const string EditAction = "Edit";
    public const string ApproveAction = "Approve";
    public const string PostAction = "Post";
    public const string CancelAction = "Cancel";
    public const string ReverseAction = "Reverse";

    /// <summary>P0-2 (المرحلة B) — تخصيص مقبوضات العميل على فواتيره (فعل مستقل عن الترحيل).</summary>
    public const string AllocateAction = "Allocate";

    /// <summary>
    /// SUB-2b/P3 — رد مبلغ ناتج عن مردود مبيعات (فعل مستقل، بلا fallback للأدوار).
    /// </summary>
    public const string RefundAction = "Refund";

    /// <summary>الأدوار الحالية التي تمنح وصولًا كاملًا للسندات (سلوك النظام اليوم — بلا دور جديد).</summary>
    public static readonly string[] FallbackRoles = { "Expenses", "Accounting" };

    /// <summary>الإذن المطلوب لفعل: "Vouchers.{action}".</summary>
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

        // لا أذونات صريحة لموديول السندات → الأدوار الحالية تمنح وصولاً كاملاً (السلوك القائم).
        return FallbackRoles.Any(r => roleSet.Contains(r));
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

    /// <summary>
    /// قاعدة الأذونات الصريحة فقط (بلا أي fallback للأدوار): Admin/SuperAdmin أو Claim مطابق.
    ///
    /// سُمِّيت منفصلة عن <see cref="IsAuthorized"/> لأن الأخيرة تدخل «الوضع الصارم» بمجرد وجود
    /// أي Claim للموديول؛ لو استُخدمت لصلاحية الرد لأدّى منح <c>Vouchers.Refund</c> وحده إلى
    /// إسقاط View/Add/Approve/Post من الفallback بصمت (خطر R18). لذلك:
    ///   • صلاحية الرد تُقيَّم بهذه القاعدة فقط ⇒ لا تُمنح ضمنيًا أبدًا.
    ///   • بقية أفعال السندات تبقى على <see cref="IsAuthorized"/> بلا تغيير سلوكي.
    /// </summary>
    public static bool IsExplicitlyAuthorized(IEnumerable<string>? roles, IEnumerable<string>? permissionClaims, string action)
    {
        var roleSet = new HashSet<string>(roles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (roleSet.Contains("Admin") || roleSet.Contains("SuperAdmin"))
            return true;

        var required = Permission(action);
        return (permissionClaims ?? Enumerable.Empty<string>())
            .Any(v => string.Equals(v, required, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>نفس قاعدة الأذونات الصريحة انطلاقًا من هوية جاهزة.</summary>
    public static bool IsExplicitlyAuthorized(ClaimsPrincipal? user, string action)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        return IsExplicitlyAuthorized(
            user.FindAll(ClaimTypes.Role).Select(c => c.Value),
            user.FindAll(ClaimType).Select(c => c.Value),
            action);
    }
}
