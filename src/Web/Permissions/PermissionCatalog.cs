namespace ERPSystem.Web.Permissions;

/// <summary>
/// الفهرس الثابت لأذونات النظام: الموديولات والأفعال (عرض/إضافة/تعديل/حذف).
/// كل إذن يُمثَّل كـ Claim من نوع "Permission" بقيمة مثل "Sales.View".
/// </summary>
public static class PermissionCatalog
{
    public const string ClaimType = "Permission";
    public const string PolicyPrefix = "Permission:";

    public static readonly string[] Actions = { "View", "Add", "Edit", "Delete" };

    /// <summary>
    /// الموديولات: (المفتاح التقني للقيمة، مفتاح الترجمة للاسم).
    /// </summary>
    public static readonly (string Key, string LabelKey)[] Modules =
    {
        ("Dashboard", "Dashboard"),
        ("Sales", "Sales"),
        ("Purchases", "Purchases"),
        ("Inventory", "Inventory"),
        ("Accounting", "Accounting"),
        ("Expenses", "Expenses"),
        ("Crm", "Crm"),
        ("Hr", "HumanResources"),
        ("Reports", "Reports"),
        ("Pos", "Pos"),
        ("Permissions", "Permissions"),
        ("Vouchers", "Vouchers")
    };

    /// <summary>
    /// مرحلة سندات القبض والصرف (المرحلة B / D2-A): أفعال إضافية تتجاوز الأفعال الأربعة الافتراضية.
    /// تُبنى بنفس نمط "{Module}.{Action}" فلا تحتاج أي تغيير في مُزوّد السياسات (Permission:{value}).
    /// </summary>
    public static readonly (string ModuleKey, string[] Actions)[] ModuleExtraActions =
    {
        // Allocate: فعل P0-2 القائم (كان ناقصًا من الكتالوج ⇒ لا يمكن منحه من أي شاشة).
        // Refund: SUB-2b/P3 — صلاحية رد مبلغ عميل (مستقلة، بلا fallback).
        ("Vouchers", new[] { "Approve", "Post", "Cancel", "Reverse", "Allocate", "Refund" })
    };

    /// <summary>
    /// الأدوار الجاهزة المقسمة حسب الموديولات: (اسم الدور، مفتاح الموديول).
    /// كل دور يحصل تلقائيًا على أذونات موديوله الأربعة (عرض/إضافة/تعديل/حذف).
    /// </summary>
    public static readonly (string Role, string ModuleKey)[] ModuleRoles =
    {
        ("كاشير", "Pos"),
        ("مخازن", "Inventory"),
        ("مبيعات", "Sales"),
        ("مشتريات", "Purchases"),
        ("محاسبة", "Accounting"),
        ("مصاريف", "Expenses"),
        ("علاقات عملاء", "Crm"),
        ("موارد بشرية", "Hr"),
        ("تقارير", "Reports"),
        ("صلاحيات", "Permissions"),
    };

    public static string Build(string moduleKey, string action) => $"{moduleKey}.{action}";

    public static IEnumerable<string> AllPermissions() =>
        Modules.SelectMany(m => Actions.Select(a => Build(m.Key, a)))
            .Concat(ModuleExtraActions.SelectMany(m => m.Actions.Select(a => Build(m.ModuleKey, a))));
}
