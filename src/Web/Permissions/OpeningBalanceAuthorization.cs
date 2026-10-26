using ERPSystem.Application.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace ERPSystem.Web.Permissions;

/// <summary>
/// Phase B — تصريح عمليات الأرصدة الافتتاحية في مضيف Web (Blazor).
///
/// يقرأ هوية الدائرة الحالية (AuthenticationState) ويفوّض القرار إلى
/// <see cref="OpeningBalanceRules"/> — نفس القواعد المستخدمة في مضيف Api،
/// وتعتمد على أذونات موديول «المحاسبة» الموجودة (Accounting.Add/Edit/Delete) دون إنشاء إذن جديد.
/// لا يُمرَّر أي شيء من الواجهة إلى هذا القرار، فلا يمكن تجاوز الفحص من الشاشة.
/// </summary>
public sealed class BlazorOpeningBalanceAuthorization : IOpeningBalanceAuthorization
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public BlazorOpeningBalanceAuthorization(AuthenticationStateProvider authenticationStateProvider)
    {
        _authenticationStateProvider = authenticationStateProvider;
    }

    private bool IsAuthorized(string action)
    {
        try
        {
            var state = _authenticationStateProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return OpeningBalanceRules.IsAuthorized(state.User, action);
        }
        catch
        {
            // فشل مغلق: عند أي تعذّر لقراءة الهوية لا يُمنح تصريح.
            return false;
        }
    }

    public bool CanCreate() => IsAuthorized(OpeningBalanceRules.AddAction);
    public bool CanUpdate() => IsAuthorized(OpeningBalanceRules.EditAction);
    public bool CanPost() => IsAuthorized(OpeningBalanceRules.EditAction);
    public bool CanReverse() => IsAuthorized(OpeningBalanceRules.EditAction);
    public bool CanDelete() => IsAuthorized(OpeningBalanceRules.DeleteAction);
}
