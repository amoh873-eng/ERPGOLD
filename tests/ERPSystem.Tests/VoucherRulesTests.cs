using System.Security.Claims;
using ERPSystem.Application.Security;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة B (D2-A) — قواعد تصريح سندات القبض والصرف (VoucherRules).
/// اختبار وحدة بلا قاعدة بيانات: يثبت أن الفصل بين عرض/إنشاء/تعديل/اعتماد/ترحيل/إلغاء
/// يُطبَّق فعليًا عند وجود أذونات صريحة، وأن غياب الأذونات الصريحة يبقي السلوك القائم
/// (الأدوار الحالية Expenses/Accounting) فلا يتأثر أي مستخدم حالي.
/// </summary>
public class VoucherRulesTests
{
    private static readonly string[] AllActions =
    {
        VoucherRules.ViewAction, VoucherRules.AddAction, VoucherRules.EditAction,
        VoucherRules.ApproveAction, VoucherRules.PostAction, VoucherRules.CancelAction
    };

    [Fact]
    public void Admin_and_SuperAdmin_are_authorized_for_every_action()
    {
        foreach (var role in new[] { "Admin", "SuperAdmin" })
        foreach (var action in AllActions)
            Assert.True(VoucherRules.IsAuthorized(new[] { role }, Array.Empty<string>(), action), $"{role}/{action}");
    }

    [Fact]
    public void Explicit_vouchers_claims_separate_duties_strictly()
    {
        // مستخدم يملك عرض السندات فقط ⇒ لا إنشاء ولا تعديل ولا اعتماد ولا ترحيل ولا إلغاء.
        var claims = new[] { VoucherRules.Permission(VoucherRules.ViewAction) };

        Assert.True(VoucherRules.IsAuthorized(new[] { "Expenses" }, claims, VoucherRules.ViewAction));
        Assert.False(VoucherRules.IsAuthorized(new[] { "Expenses" }, claims, VoucherRules.AddAction));
        Assert.False(VoucherRules.IsAuthorized(new[] { "Expenses" }, claims, VoucherRules.EditAction));
        Assert.False(VoucherRules.IsAuthorized(new[] { "Expenses" }, claims, VoucherRules.ApproveAction));
        Assert.False(VoucherRules.IsAuthorized(new[] { "Expenses" }, claims, VoucherRules.PostAction));
        Assert.False(VoucherRules.IsAuthorized(new[] { "Expenses" }, claims, VoucherRules.CancelAction));
    }

    [Fact]
    public void Post_permission_does_not_grant_approve_or_cancel()
    {
        var claims = new[] { VoucherRules.Permission(VoucherRules.PostAction) };

        Assert.True(VoucherRules.IsAuthorized(Array.Empty<string>(), claims, VoucherRules.PostAction));
        Assert.False(VoucherRules.IsAuthorized(Array.Empty<string>(), claims, VoucherRules.ApproveAction));
        Assert.False(VoucherRules.IsAuthorized(Array.Empty<string>(), claims, VoucherRules.CancelAction));
    }

    [Fact]
    public void Claims_of_other_modules_do_not_restrict_vouchers_fallback_roles()
    {
        // وجود أذونات لموديول آخر لا يُلغي قاعدة الموديول الحالي (لا أذونات Vouchers صريحة ⇒ الأدوار الحالية).
        var claims = new[] { "Sales.View", "Reports.View" };

        Assert.True(VoucherRules.IsAuthorized(new[] { "Expenses" }, claims, VoucherRules.PostAction));
        Assert.True(VoucherRules.IsAuthorized(new[] { "Accounting" }, claims, VoucherRules.ApproveAction));
        Assert.False(VoucherRules.IsAuthorized(new[] { "Sales" }, claims, VoucherRules.PostAction));
    }

    [Fact]
    public void Without_explicit_claims_current_roles_keep_full_access()
    {
        foreach (var action in AllActions)
        {
            Assert.True(VoucherRules.IsAuthorized(new[] { "Expenses" }, Array.Empty<string>(), action), action);
            Assert.True(VoucherRules.IsAuthorized(new[] { "Accounting" }, Array.Empty<string>(), action), action);
            Assert.False(VoucherRules.IsAuthorized(new[] { "Inventory" }, Array.Empty<string>(), action), action);
            Assert.False(VoucherRules.IsAuthorized(Array.Empty<string>(), Array.Empty<string>(), action), action);
        }
    }

    [Fact]
    public void Unauthenticated_principal_is_denied()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var authenticatedWithoutRoles = new ClaimsPrincipal(new ClaimsIdentity("Blazor"));

        foreach (var action in AllActions)
        {
            Assert.False(VoucherRules.IsAuthorized(anonymous, action));
            Assert.False(VoucherRules.IsAuthorized(authenticatedWithoutRoles, action));
        }
    }

    [Fact]
    public void Authenticated_principal_with_role_claim_is_authorized()
    {
        var identity = new ClaimsIdentity("Blazor");
        identity.AddClaim(new Claim(ClaimTypes.Role, "Accounting"));
        var user = new ClaimsPrincipal(identity);

        Assert.True(VoucherRules.IsAuthorized(user, VoucherRules.PostAction));

        identity.AddClaim(new Claim(VoucherRules.ClaimType, VoucherRules.Permission(VoucherRules.ViewAction)));
        // بعد وجود إذن صريح للموديول يصبح هو المرجع: العرض فقط مسموح.
        Assert.True(VoucherRules.IsAuthorized(user, VoucherRules.ViewAction));
        Assert.False(VoucherRules.IsAuthorized(user, VoucherRules.PostAction));
    }

    // ═══ SUB-2b/P3 — صلاحية رد الأموال (Vouchers.Refund): صريحة فقط، بلا fallback ═══

    [Fact]
    public void Refund_is_never_derived_from_accounting_or_expenses_roles()
    {
        foreach (var role in new[] { "Accounting", "Expenses", "Sales", "Purchases", "Permissions" })
            Assert.False(
                VoucherRules.IsExplicitlyAuthorized(new[] { role }, Array.Empty<string>(), VoucherRules.RefundAction),
                $"{role} يجب ألا يمنح Vouchers.Refund ضمنيًا");
    }

    [Fact]
    public void Refund_requires_the_explicit_claim_or_admin_role()
    {
        var refundClaim = new[] { VoucherRules.Permission(VoucherRules.RefundAction) };

        Assert.True(VoucherRules.IsExplicitlyAuthorized(Array.Empty<string>(), refundClaim, VoucherRules.RefundAction));
        Assert.True(VoucherRules.IsExplicitlyAuthorized(new[] { "Admin" }, Array.Empty<string>(), VoucherRules.RefundAction));
        Assert.True(VoucherRules.IsExplicitlyAuthorized(new[] { "SuperAdmin" }, Array.Empty<string>(), VoucherRules.RefundAction));

        // Claim لفعل آخر لا يمنح الرد.
        Assert.False(VoucherRules.IsExplicitlyAuthorized(
            new[] { "Accounting" },
            new[] { VoucherRules.Permission(VoucherRules.PostAction) },
            VoucherRules.RefundAction));
    }

    [Fact]
    public void Refund_grant_does_not_grant_other_voucher_actions()
    {
        var refundClaim = new[] { VoucherRules.Permission(VoucherRules.RefundAction) };

        Assert.True(VoucherRules.IsExplicitlyAuthorized(Array.Empty<string>(), refundClaim, VoucherRules.RefundAction));
        foreach (var action in AllActions.Where(a => a != VoucherRules.RefundAction))
            Assert.False(VoucherRules.IsExplicitlyAuthorized(Array.Empty<string>(), refundClaim, action), action);
    }

    [Fact]
    public void Refund_denied_for_unauthenticated_principal()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(VoucherRules.IsExplicitlyAuthorized(anonymous, VoucherRules.RefundAction));

        var identity = new ClaimsIdentity("Blazor");
        identity.AddClaim(new Claim(VoucherRules.ClaimType, VoucherRules.Permission(VoucherRules.RefundAction)));
        Assert.True(VoucherRules.IsExplicitlyAuthorized(new ClaimsPrincipal(identity), VoucherRules.RefundAction));
    }

    [Fact]
    public void Permission_keys_match_the_documented_catalog_shape()
    {
        Assert.Equal("Vouchers", VoucherRules.Module);
        Assert.Equal("Vouchers.View", VoucherRules.Permission(VoucherRules.ViewAction));
        Assert.Equal("Vouchers.Add", VoucherRules.Permission(VoucherRules.AddAction));
        Assert.Equal("Vouchers.Edit", VoucherRules.Permission(VoucherRules.EditAction));
        Assert.Equal("Vouchers.Approve", VoucherRules.Permission(VoucherRules.ApproveAction));
        Assert.Equal("Vouchers.Post", VoucherRules.Permission(VoucherRules.PostAction));
        Assert.Equal("Vouchers.Cancel", VoucherRules.Permission(VoucherRules.CancelAction));
    }
}
