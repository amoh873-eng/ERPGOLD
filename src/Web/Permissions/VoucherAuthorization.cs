using ERPSystem.Application.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace ERPSystem.Web.Permissions;

/// <summary>
/// D2-A — تصريح عمليات سندات القبض والصرف في مضيف Web (Blazor).
///
/// يقرأ هوية الدائرة الحالية (AuthenticationState) ويفوّض القرار إلى <see cref="VoucherRules"/>
/// (نفس القواعد المستخدمة في مضيف Api)، ولا يُمرَّر أي شيء من الواجهة إلى هذا القرار
/// ⇒ لا يمكن تجاوز فحص الاعتماد/الترحيل/الإلغاء من الشاشة أو بإرسال طلب مباشر إلى الخدمة.
/// </summary>
public sealed class BlazorVoucherAuthorization : IVoucherAuthorization
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public BlazorVoucherAuthorization(AuthenticationStateProvider authenticationStateProvider)
    {
        _authenticationStateProvider = authenticationStateProvider;
    }

    private bool IsAuthorized(string action)
    {
        try
        {
            var state = _authenticationStateProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return VoucherRules.IsAuthorized(state.User, action);
        }
        catch
        {
            // فشل مغلق: عند أي تعذّر لقراءة الهوية لا يُمنح تصريح.
            return false;
        }
    }

    public bool CanView() => IsAuthorized(VoucherRules.ViewAction);
    public bool CanCreate() => IsAuthorized(VoucherRules.AddAction);
    public bool CanEdit() => IsAuthorized(VoucherRules.EditAction);
    public bool CanApprove() => IsAuthorized(VoucherRules.ApproveAction);
    public bool CanPost() => IsAuthorized(VoucherRules.PostAction);
    public bool CanCancel() => IsAuthorized(VoucherRules.CancelAction);

    /// <summary>PU-04 (N-2): صلاحية عكس السند — مستقلة عن الترحيل.</summary>
    public bool CanReverse() => IsAuthorized(VoucherRules.ReverseAction);

    /// <summary>P0-2 (المرحلة B): صلاحية تخصيص المقبوضات على الفواتير — مستقلة عن الترحيل.</summary>
    public bool CanAllocate() => IsAuthorized(VoucherRules.AllocateAction);

    /// <summary>
    /// SUB-2b/P3: صلاحية رد مبلغ عميل — أذونات صريحة فقط، بلا fallback لأدوار Accounting/Expenses.
    /// </summary>
    public bool CanRefund() => IsExplicitlyAuthorized(VoucherRules.RefundAction);

    private bool IsExplicitlyAuthorized(string action)
    {
        try
        {
            var state = _authenticationStateProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return VoucherRules.IsExplicitlyAuthorized(state.User, action);
        }
        catch
        {
            // فشل مغلق: أي تعذّر في قراءة الهوية = لا تصريح.
            return false;
        }
    }

    public string? CurrentUserName()
    {
        try
        {
            var state = _authenticationStateProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return state.User?.Identity?.Name;
        }
        catch
        {
            return null;
        }
    }
}
