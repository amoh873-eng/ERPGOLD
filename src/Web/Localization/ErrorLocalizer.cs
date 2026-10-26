using ERPSystem.Application.Exceptions;
using Microsoft.Extensions.Localization;

namespace ERPSystem.Web;

/// <summary>
/// يترجم أخطاء طبقة Application التي تحمل مفتاح resx (IResourceKeyedError) عبر Loc[]،
/// وإلا يعيد ex.Message كما هو. يُستخدم في الشاشات التي تعرض رسائل الأخطاء للمستخدم.
/// </summary>
public static class ErrorLocalizer
{
    public static string Resolve(IStringLocalizer<SharedResources> loc, Exception ex)
        => ex is IResourceKeyedError keyed ? loc[keyed.ResourceKey].Value : ex.Message;
}
