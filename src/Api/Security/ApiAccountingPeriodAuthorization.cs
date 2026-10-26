using System.Security.Claims;
using ERPSystem.Application.Security;
using Microsoft.AspNetCore.Identity;

namespace ERPSystem.Api.Security;

/// <summary>
/// تصريح عمليات الفترات المحاسبية في مضيف Api (JWT Bearer).
///
/// يحل المستخدم من نفس التوكن الذي يستخدمه <see cref="ApiAuth.RequireUserAsync"/> ثم يطبّق
/// نفس قواعد <see cref="AccountingPeriodRules"/> المستخدمة في Web (أذونات موديول المحاسبة
/// الموجودة: Accounting.View للعرض و Accounting.Edit للإغلاق/إعادة الفتح)، فلا يمكن تجاوز
/// الفحص بإرسال طلب مباشر إلى الـ API.
/// </summary>
public sealed class ApiAccountingPeriodAuthorization : IAccountingPeriodAuthorization
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly JwtService _jwt;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public ApiAccountingPeriodAuthorization(
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

    private bool IsAuthorized(string action)
    {
        try
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null)
                return false;

            if (ctx.User?.Identity?.IsAuthenticated == true)
                return AccountingPeriodRules.IsAuthorized(ctx.User, action);

            var header = ctx.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(header) ||
                !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return false;

            var subject = _jwt.VerifySubject(header.Substring(7).Trim());
            if (string.IsNullOrEmpty(subject))
                return false;

            var user = _userManager.FindByIdAsync(subject).GetAwaiter().GetResult();
            if (user is null)
                return false;

            var roles = _userManager.GetRolesAsync(user).GetAwaiter().GetResult();
            var identity = new ClaimsIdentity("ApiJwt");
            foreach (var roleName in roles)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
                var role = _roleManager.FindByNameAsync(roleName).GetAwaiter().GetResult();
                if (role is null) continue;
                foreach (var claim in _roleManager.GetClaimsAsync(role).GetAwaiter().GetResult())
                    identity.AddClaim(claim);
            }

            return AccountingPeriodRules.IsAuthorized(new ClaimsPrincipal(identity), action);
        }
        catch
        {
            // فشل مغلق: أي خطأ في قراءة هوية الـ API = لا تصريح.
            return false;
        }
    }

    public bool CanView() => IsAuthorized(AccountingPeriodRules.ViewAction);
    public bool CanClose() => IsAuthorized(AccountingPeriodRules.EditAction);
    public bool CanReopen() => IsAuthorized(AccountingPeriodRules.EditAction);
}
