using ERPSystem.Application.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace ERPSystem.Web.Permissions;

/// <summary>
/// Requirement 3 — تصريح تعديل السعر يدويًا في مضيف Web (Blazor).
///
/// يقرأ هوية الدائرة الحالية (AuthenticationState) ويفوّض القرار إلى
/// <see cref="ManualPriceRules"/> — نفس القواعد المستخدمة في مضيف Api.
/// لا يُمرَّر أي شيء من الواجهة إلى هذا القرار، لذلك لا يمكن تجاوز الفحص من الشاشة
/// أو بإرسال السعر يدويًا في الطلب.
/// </summary>
public sealed class BlazorManualPriceAuthorization : IManualPriceAuthorization
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public BlazorManualPriceAuthorization(AuthenticationStateProvider authenticationStateProvider)
    {
        _authenticationStateProvider = authenticationStateProvider;
    }

    public bool CanEditManualPrice(string moduleKey)
    {
        try
        {
            // في Blazor Server تكون حالة المصادقة متاحة مباشرة للدائرة الحالية (لا انتظار فعلي).
            var state = _authenticationStateProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return ManualPriceRules.IsAuthorized(state.User, moduleKey);
        }
        catch
        {
            // فشل مغلق: عند أي تعذّر لقراءة الهوية لا يُمنح تصريح تعديل السعر.
            return false;
        }
    }
}
