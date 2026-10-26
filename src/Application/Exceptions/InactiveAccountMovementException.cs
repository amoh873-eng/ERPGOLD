namespace ERPSystem.Application.Exceptions;

/// <summary>
/// يُرمى عند محاولة تسجيل حركة مالية على حساب غير نشط (IsActive = false).
///
/// القاعدة المحاسبية: الحساب الصالح للحركة يجب أن يكون نشطًا وغير محذوف، وليس جذريًا/تصنيفيًا،
/// وليس حسابًا رئيسيًا تجميعيًا. تعطيل الحساب يمنع استخدامه في أي حركة جديدة (دون أي تبديل صامت
/// إلى حساب آخر).
///
/// النص المعروض عبر Loc["AccountInactiveMovementNotAllowed"] + SharedResources.resx؛
/// الرسالة هنا نص احتياطي (للسجلات/الـAPI غير المترجَم).
/// </summary>
public sealed class InactiveAccountMovementException : InvalidOperationException, IResourceKeyedError
{
    /// <summary>مفتاح الترجمة في SharedResources.resx.</summary>
    public const string Key = "AccountInactiveMovementNotAllowed";

    public string ResourceKey => Key;

    public InactiveAccountMovementException()
        : base("لا يمكن تسجيل حركة مالية على حساب غير نشط، يرجى تفعيل الحساب أو اختيار حساب متحرك نشط.")
    {
    }
}
