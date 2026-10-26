using System.Security.Claims;
using ERPSystem.Application.Security;
using Microsoft.AspNetCore.Identity;

namespace ERPSystem.Api.Security;

/// <summary>
/// D2-A — تصريح عمليات سندات القبض والصرف في مضيف Api (JWT Bearer).
///
/// يحل المستخدم من نفس التوكن الذي يستخدمه <see cref="ApiAuth.RequireUserAsync"/> ثم يطبّق
/// نفس قواعد <see cref="VoucherRules"/> المستخدمة في Web ⇒ لا يمكن تجاوز فحص الاعتماد/الترحيل/الإلغاء
/// بإرسال طلب مباشر إلى الـ API (تجاوز الواجهة).
/// </summary>
public sealed class ApiVoucherAuthorization : IVoucherAuthorization
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly JwtService _jwt;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public ApiVoucherAuthorization(
        IHttpContextAccessor httpContextAccessor,
        JwtService jwt,
        UserManager<IdentityUser> userManager,
        RoleManager<IdentityRole> roleManager)
    {
        _httpContextAccessor = httpContextAccessor;
        _jwt = jwt;
        _userManager = userManager;
        _roleManager = roleManager;
    }

    /// <summary>يعيد الهوية الحالية (من Middleware أو من توكن Bearer) أو null عند التعذّر.</summary>
    private ClaimsPrincipal? ResolveUser()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null)
            return null;

        if (ctx.User?.Identity?.IsAuthenticated == true)
            return ctx.User;

        var header = ctx.Request.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var subject = _jwt.VerifySubject(header.Substring(7).Trim());
        if (string.IsNullOrEmpty(subject))
            return null;

        var user = _userManager.FindByIdAsync(subject).GetAwaiter().GetResult();
        if (user is null)
            return null;

        var identity = new ClaimsIdentity("ApiJwt");
        identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName ?? user.Email ?? string.Empty));
        foreach (var roleName in _userManager.GetRolesAsync(user).GetAwaiter().GetResult())
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
            var role = _roleManager.FindByNameAsync(roleName).GetAwaiter().GetResult();
            if (role is null) continue;
            foreach (var claim in _roleManager.GetClaimsAsync(role).GetAwaiter().GetResult())
                identity.AddClaim(claim);
        }

        return new ClaimsPrincipal(identity);
    }

    private bool IsAuthorized(string action)
    {
        try
        {
            return VoucherRules.IsAuthorized(ResolveUser(), action);
        }
        catch
        {
            // فشل مغلق: أي خطأ في قراءة هوية الـ API = لا تصريح.
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

    /// <summary>
    /// SUB-2b/P3: صلاحية رد مبلغ عميل — أذونات صريحة فقط، بلا fallback لأدوار Accounting/Expenses.
    /// </summary>
    public bool CanRefund() => IsExplicitlyAuthorized(VoucherRules.RefundAction);

    private bool IsExplicitlyAuthorized(string action)
    {
        try
        {
            return VoucherRules.IsExplicitlyAuthorized(ResolveUser(), action);
        }
        catch
        {
            return false;
        }
    }

    public string? CurrentUserName()
    {
        try
        {
            return ResolveUser()?.Identity?.Name;
        }
        catch
        {
            return null;
        }
    }
}
