namespace ERPSystem.Application.Exceptions;

/// <summary>
/// واجهة للأخطاء التي تحمل مفتاح ترجمة (resx key) بدلاً من نص مضمّن،
/// بحيث تعرض طبقة الـWeb النص عبر Loc[ResourceKey] من SharedResources.resx.
/// </summary>
public interface IResourceKeyedError
{
    /// <summary>مفتاح الترجمة في SharedResources.resx.</summary>
    string ResourceKey { get; }
}
