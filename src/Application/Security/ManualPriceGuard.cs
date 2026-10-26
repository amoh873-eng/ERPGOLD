namespace ERPSystem.Application.Security;

/// <summary>
/// Requirement 3 — نقطة التحقق الخلفية الوحيدة لتعديل السعر يدويًا.
///
/// القاعدة: خدمة Application ترفض أي قيمة سعر تخالف السعر النظامي (سعر الصنف/التكلفة المخزّنة)
/// إذا لم يكن المستخدم الحالي مصرّحًا به "{Module}.Edit" — أي أن الحماية ليست في الواجهة فقط،
/// بل أي استدعاء مباشر لخدمة التطبيق (تجاوز الواجهة/أداة خارجية) يُرفض أيضًا.
/// </summary>
public static class ManualPriceGuard
{
    /// <summary>
    /// يتحقق من أن القيمة المُرسلة هي السعر النظامي أو أن المستخدم مصرّح بتعديله يدويًا.
    /// </summary>
    /// <param name="authorization">مصدر الصلاحية (يُحقن خلفيًا — لا يأتي من الواجهة).</param>
    /// <param name="moduleKey">موديول الصلاحية (Inventory / Sales / Purchases).</param>
    /// <param name="submittedValue">القيمة القادمة من الطلب.</param>
    /// <param name="systemValue">القيمة النظامية (سعر الصنف/التكلفة المخزّنة) — لا تحتاج تصريحًا.</param>
    /// <param name="fieldLabel">وصف الحقل لرسالة الرفض.</param>
    /// <exception cref="UnauthorizedAccessException">عند محاولة تعديل يدوي بدون تصريح.</exception>
    public static void EnsureCanOverride(
        IManualPriceAuthorization? authorization,
        string moduleKey,
        decimal submittedValue,
        decimal systemValue,
        string fieldLabel)
    {
        // القيمة النظامية نفسها ليست "تعديلًا يدويًا" — تُقبل دائمًا (فلا تتأثر الشاشات للمستخدم العادي).
        if (submittedValue == systemValue)
            return;

        if (authorization is not null && authorization.CanEditManualPrice(moduleKey))
            return;

        throw new UnauthorizedAccessException(
            $"غير مصرّح بتعديل {fieldLabel} يدويًا. الصلاحية المطلوبة: {ManualPriceRules.EditPermission(moduleKey)}");
    }
}
