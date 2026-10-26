using ERPSystem.Application.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace ERPSystem.Web.Permissions;

/// <summary>
/// تصريح عمليات الفترات المحاسبية في مضيف Web (Blazor).
///
/// يقرأ هوية الدائرة الحالية (AuthenticationState) ويفوّض القرار إلى
/// <see cref="AccountingPeriodRules"/> — نفس القواعد المستخدمة في مضيف Api،
/// وتعتمد على أذونات موديول «المحاسبة» الموجودة (Accounting.View/Edit) دون إنشاء إذن جديد.
/// لا يُمرَّر أي شيء من الواجهة إلى هذا القرار، فلا يمكن تجاوز الفحص من الشاشة.
/// </summary>
public sealed class BlazorAccountingPeriodAuthorization : IAccountingPeriodAuthorization
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public BlazorAccountingPeriodAuthorization(AuthenticationStateProvider authenticationStateProvider)
    {
        _authenticationStateProvider = authenticationStateProvider;
    }

    private bool IsAuthorized(string action)
    {
        try
        {
            var state = _authenticationStateProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return AccountingPeriodRules.IsAuthorized(state.User, action);
        }
        catch
        {
            // فشل مغلق: عند أي تعذّر لقراءة الهوية لا يُمنح تصريح.
            return false;
        }
    }

    public bool CanView() => IsAuthorized(AccountingPeriodRules.ViewAction);
    public bool CanClose() => IsAuthorized(AccountingPeriodRules.EditAction);
    public bool CanReopen() => IsAuthorized(AccountingPeriodRules.EditAction);
}
