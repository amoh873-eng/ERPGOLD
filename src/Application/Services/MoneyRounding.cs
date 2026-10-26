namespace ERPSystem.Application.Services;

/// <summary>
/// سياسة التقريب النقدي الموحّدة والصريحة (money rounding policy).
///
/// السياسة المعتمدة = <see cref="MidpointRounding.ToEven"/> (تقريب البنك / banker's rounding)
/// بنفس ما كان يفعله النظام ضمنيًا عبر <c>Math.Round(value, 2)</c> بدون تحديد الوضع.
/// جُعلت هنا **صريحة ومسمّاة** للمبالغ النقدية في مسار التكلفة/COGS حتى تكون متسقة وموثّقة
/// ولا تُغيَّر بالخطأ عند أي تعديل لاحق.
///
/// حالة القيم الواقعة في منتصف خانة التقريب (midpoint، أي تنتهي بـ .xx5 بالضبط):
///   تُقرَّب إلى **القرش الزوجي الأقرب** (to even).
///   أمثلة: 6.225 → 6.22 · 6.235 → 6.24 · 15.205 → 15.20 · 15.215 → 15.22.
/// ملاحظة: هذا يخالف التقريب «نصف لأعلى» (AwayFromZero) الذي تستخدمه بعض أدوات SQL/Excel،
/// لذا قد يختلف إعادة احتساب مبلغ نقدي في قاعدة البيانات بمقدار قرش واحد عند القيم المتطابقة للمنتصف.
/// تغيير السياسة قرار مالي يحتاج اعتمادًا صريحًا — لا يُغيَّر هنا.
/// </summary>
public static class MoneyRounding
{
    /// <summary>عدد المنازل النقدية المعتمدة (قرشان).</summary>
    public const int Scale = 2;

    /// <summary>وضع التقريب النقدي المعتمد (صريح — لا يعتمد على الافتراضي الضمني).</summary>
    public const MidpointRounding Mode = MidpointRounding.ToEven;

    /// <summary>يقرّب مبلغًا نقديًا وفق سياسة النظام الموحّدة.</summary>
    public static decimal Round(decimal value) => Math.Round(value, Scale, Mode);
}
