using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Infrastructure.Data;
using ERPSystem.Web.Permissions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Web.Services;

/// <summary>حالة صلاحية واحدة لدور: هل هي ممنوحة كـClaim؟ وهل هي فعّالة الآن بقواعد التصريح؟</summary>
public sealed record VoucherPermissionState(string Permission, string Action, bool IsGranted, bool IsEffective);

/// <summary>
/// SUB-2b/P3 — منح/سحب أذونات سندات القبض والصرف لدور، صراحةً وبسجل تدقيق.
///
/// القيود الأمنية:
///   • المُنفِّذ يجب أن يكون Admin/SuperAdmin — لا يكفي دور «Permissions» (منع تصعيد الصلاحية).
///   • النطاق محصور في موديول Vouchers (لا يفتح بابًا عامًا لكل الموديولات).
///   • لا تُمنح <c>Vouchers.Refund</c> تلقائيًا أبدًا؛ ولا تُشتق من Accounting/Expenses.
///   • منع «السحب الصامت» (R18): إذا كان الدور لا يملك أذونات صريحة (يعمل بالـfallback)،
///     فيجب أن يتضمن المحفوظ كل الصلاحيات الفعّالة حاليًا، وإلا رُفض الطلب.
///   • كل إضافة/إزالة تُسجَّل في AuditTrails داخل نفس معاملة الحفظ.
/// </summary>
public interface IPermissionGrantService
{
    Task<IReadOnlyList<VoucherPermissionState>> GetVoucherPermissionsAsync(Guid roleId);

    /// <summary>يحفظ مجموعة الأذونات الصريحة للدور (استبدال كامل لنطاق Vouchers).</summary>
    Task SaveVoucherPermissionsAsync(Guid roleId, IReadOnlyCollection<string> grantedPermissions);
}

public sealed class PermissionGrantService : IPermissionGrantService
{
    private const string AuditEntity = "Role";

    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly AuthenticationStateProvider _authState;
    private readonly AppDbContext _db;

    public PermissionGrantService(
        RoleManager<IdentityRole> roleManager,
        AuthenticationStateProvider authState,
        AppDbContext db)
    {
        _roleManager = roleManager;
        _authState = authState;
        _db = db;
    }

    /// <summary>أفعال موديول السندات المعتمدة (الأربعة الأساسية + الأفعال الإضافية في الكتالوج).</summary>
    private static IReadOnlyList<string> VoucherActions()
    {
        var extras = PermissionCatalog.ModuleExtraActions
            .Where(m => m.ModuleKey == VoucherRules.Module)
            .SelectMany(m => m.Actions);

        return PermissionCatalog.Actions
            .Concat(extras)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<(string UserName, IReadOnlyList<string> Roles)> RequireAdministratorAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var user = state.User;

        if (user?.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("منح الصلاحيات يتطلب تسجيل الدخول.");

        var roles = user.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList();

        var isAdmin = roles.Any(r =>
            string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, "SuperAdmin", StringComparison.OrdinalIgnoreCase));

        if (!isAdmin)
            throw new UnauthorizedAccessException("منح/سحب صلاحيات السندات مقصور على مدير النظام (Admin/SuperAdmin).");

        return (user.Identity!.Name ?? "غير معروف", roles);
    }

    public async Task<IReadOnlyList<VoucherPermissionState>> GetVoucherPermissionsAsync(Guid roleId)
    {
        await RequireAdministratorAsync();

        var role = await _roleManager.FindByIdAsync(roleId.ToString())
            ?? throw new InvalidOperationException("الدور غير موجود.");

        var roleNames = new[] { role.Name ?? string.Empty };
        var claims = (await _roleManager.GetClaimsAsync(role))
            .Where(c => c.Type == VoucherRules.ClaimType)
            .Select(c => c.Value)
            .ToList();

        return VoucherActions()
            .Select(action =>
            {
                var permission = VoucherRules.Permission(action);
                var isGranted = claims.Any(c => string.Equals(c, permission, StringComparison.OrdinalIgnoreCase));
                var isEffective = string.Equals(action, VoucherRules.RefundAction, StringComparison.OrdinalIgnoreCase)
                    ? VoucherRules.IsExplicitlyAuthorized(roleNames, claims, action)
                    : VoucherRules.IsAuthorized(roleNames, claims, action);

                return new VoucherPermissionState(permission, action, isGranted, isEffective);
            })
            .ToList();
    }

    public async Task SaveVoucherPermissionsAsync(Guid roleId, IReadOnlyCollection<string> grantedPermissions)
    {
        var (userName, _) = await RequireAdministratorAsync();

        var role = await _roleManager.FindByIdAsync(roleId.ToString())
            ?? throw new InvalidOperationException("الدور غير موجود.");

        var allowed = VoucherActions()
            .Select(VoucherRules.Permission)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requested = (grantedPermissions ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unknown = requested.Where(p => !allowed.Contains(p)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException("صلاحيات غير معروفة: " + string.Join(", ", unknown));

        var roleName = role.Name ?? string.Empty;
        var roleNames = new[] { roleName };

        var currentClaims = (await _roleManager.GetClaimsAsync(role))
            .Where(c => c.Type == VoucherRules.ClaimType)
            .Select(c => c.Value)
            .ToList();

        var currentVoucherClaims = currentClaims
            .Where(c => c.StartsWith(VoucherRules.Module + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // منع السحب الصامت: دور يعمل بالـfallback (بلا أذونات صريحة) يجب أن تبقى كل صلاحياته الفعّالة.
        if (currentVoucherClaims.Count == 0)
        {
            var effectiveNow = VoucherActions()
                .Where(a => !string.Equals(a, VoucherRules.RefundAction, StringComparison.OrdinalIgnoreCase))
                .Where(a => VoucherRules.IsAuthorized(roleNames, currentClaims, a))
                .Select(VoucherRules.Permission)
                .ToList();

            var missing = effectiveNow.Except(requested, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "سيؤدي هذا الحفظ إلى سحب صلاحيات قائمة من الدور (" + string.Join(", ", missing) +
                    "). أبقِ الصلاحيات الفعّالة محددة عند منح Vouchers.Refund.");
        }

        var toAdd = requested.Except(currentClaims, StringComparer.OrdinalIgnoreCase).ToList();
        var toRemove = currentVoucherClaims.Except(requested, StringComparer.OrdinalIgnoreCase).ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
            return;

        await using var tx = await _db.Database.BeginTransactionAsync();

        foreach (var permission in toAdd)
        {
            var result = await _roleManager.AddClaimAsync(role, new System.Security.Claims.Claim(VoucherRules.ClaimType, permission));
            if (!result.Succeeded)
                throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));

            WriteAudit(role, roleName, "PermissionGranted", permission, userName);
        }

        foreach (var permission in toRemove)
        {
            var result = await _roleManager.RemoveClaimAsync(role, new System.Security.Claims.Claim(VoucherRules.ClaimType, permission));
            if (!result.Succeeded)
                throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));

            WriteAudit(role, roleName, "PermissionRevoked", permission, userName);
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private void WriteAudit(IdentityRole role, string roleName, string action, string permission, string performedBy)
    {
        var now = DateTime.UtcNow;
        _db.Set<AuditTrail>().Add(new AuditTrail
        {
            Id = Guid.NewGuid(),
            EntityName = AuditEntity,
            EntityId = Guid.TryParse(role.Id, out var rid) ? rid : Guid.Empty,
            DocumentNumber = roleName,
            Action = action,
            Details = permission,
            PerformedBy = performedBy,
            PerformedAt = now,
            CreatedAt = now,
            CreatedBy = performedBy
        });
    }
}
