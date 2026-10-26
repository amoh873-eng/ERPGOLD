namespace ERPSystem.Application.Exceptions;

/// <summary>
/// يُرمى عند محاولة تسجيل حركة مالية مباشرة على حساب رئيسي (Parent).
/// القاعدة المحاسبية: الحسابات المتحركة (Leaf) فقط تقبل الحركات المالية؛
/// الحساب الرئيسي يُجمَّع من أبنائه ولا يُرحَّل إليه مباشرة.
///
/// النص المعروض عبر Loc["AccountParentMovementNotAllowed"] + SharedResources.resx؛
/// الرسالة هنا نص احتياطي (للسجلات/الـAPI غير المترجَم).
/// </summary>
public sealed class ParentAccountMovementException : InvalidOperationException, IResourceKeyedError
{
    /// <summary>مفتاح الترجمة في SharedResources.resx.</summary>
    public const string Key = "AccountParentMovementNotAllowed";

    public string ResourceKey => Key;

    public ParentAccountMovementException()
        : base("لا يمكن تسجيل حركة مالية مباشرة على حساب رئيسي، يرجى اختيار حساب متحرك.")
    {
    }
}
