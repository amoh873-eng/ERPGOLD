namespace ERPSystem.Application.Exceptions;

/// <summary>
/// يُرمى عند محاولة تسجيل حركة مالية مباشرة على حساب جذري/تصنيفي (ParentAccountId = null).
///
/// القاعدة المحاسبية: الحسابات الجذرية (مثل «1 الأصول» و«3 حقوق الملكية») هي حسابات تصنيف/تجميع
/// تُبنى من أبنائها ولا تُرحَّل إليها حركات مباشرة — حتى لو لم يكن لها أبناء بعد (شجرة ناقصة).
/// الحساب المتحرك (Leaf) وحده يقبل الحركات المالية.
///
/// النص المعروض عبر Loc["AccountRootMovementNotAllowed"] + SharedResources.resx؛
/// الرسالة هنا نص احتياطي (للسجلات/الـAPI غير المترجَم).
/// </summary>
public sealed class RootAccountMovementException : InvalidOperationException, IResourceKeyedError
{
    /// <summary>مفتاح الترجمة في SharedResources.resx.</summary>
    public const string Key = "AccountRootMovementNotAllowed";

    public string ResourceKey => Key;

    public RootAccountMovementException()
        : base("لا يمكن تسجيل حركة مالية مباشرة على حساب جذري/تصنيفي، يرجى اختيار حساب متحرك (فرعي).")
    {
    }
}
