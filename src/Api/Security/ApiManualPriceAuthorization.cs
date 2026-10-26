using System.Security.Claims;
using ERPSystem.Application.Security;
using Microsoft.AspNetCore.Identity;

namespace ERPSystem.Api.Security;

/// <summary>
/// Requirement 3 — تصريح تعديل السعر يدويًا في مضيف Api (مصادقة JWT Bearer).
///
/// يحل المستخدم من نفس التوكن الذي يستخدمه <see cref="ApiAuth.RequireUserAsync"/> ثم يطبّق
/// نفس قواعد <see cref="ManualPriceRules"/> المستخدمة في Web — فلا يمكن تجاوز الفحص بإرسال
/// سعر يدوي عبر الـ API.
/// </summary>
public sealed class ApiManualPriceAuthorization : IManualPriceAuthorization
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly JwtService _jwt;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public ApiManualPriceAuthorization(
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

    public bool CanEditManualPrice(string moduleKey)
    {
        try
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null)
                return false;

            // إن أُثبتت الهوية عبر Middleware نستخدمها مباشرة (نفس قواعد Web).
            if (ctx.User?.Identity?.IsAuthenticated == true)
                return ManualPriceRules.IsAuthorized(ctx.User, moduleKey);

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

            return ManualPriceRules.IsAuthorized(new ClaimsPrincipal(identity), moduleKey);
        }
        catch
        {
            // فشل مغلق: أي خطأ في قراءة هوية الـ API = لا تصريح لتعديل السعر.
            return false;
        }
    }
}
