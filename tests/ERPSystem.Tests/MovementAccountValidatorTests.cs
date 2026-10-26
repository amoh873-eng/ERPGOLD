using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Services;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// القاعدة المحاسبية المركزية: الحسابات المتحركة (Leaf) فقط تُرحَّل إليها الحركات.
/// Parent → Reject، Leaf → Allow.
/// </summary>
public class MovementAccountValidatorTests
{
    // ===== Production: 1100 ورقة (Leaf) =====

    [Fact]
    public async Task Production_1100_leaf_is_allowed()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedProductionTree(ctx);

        // لا يرمي استثناءً → مسموح
        await MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { ids["1100"] });
    }

    // ===== Commercial: 1100 رئيسي (Parent) =====

    [Fact]
    public async Task Commercial_1100_parent_is_rejected()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedCommercialTree(ctx);

        await Assert.ThrowsAsync<ParentAccountMovementException>(
            () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { ids["1100"] }));
    }

    [Fact]
    public async Task Commercial_11001_leaf_is_allowed()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedCommercialTree(ctx);

        await MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { ids["11001"] });
    }

    // ===== الحسابات الرئيسية الجذرية =====

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    public async Task Commercial_parent_roots_are_rejected(string code)
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedCommercialTree(ctx);

        await Assert.ThrowsAsync<ParentAccountMovementException>(
            () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { ids[code] }));
    }

    // ===== حساب متحرك عادي =====

    [Theory]
    [InlineData("1101")]
    [InlineData("5300")]
    public async Task Commercial_leaf_accounts_are_allowed(string code)
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedCommercialTree(ctx);

        await MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { ids[code] });
    }

    [Fact]
    public async Task Empty_leg_set_is_allowed()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        AccountingSafetyFixtures.SeedCommercialTree(ctx);

        await MovementAccountValidator.EnsureMovementAccountsAsync(ctx, Array.Empty<Guid>());
    }

    [Fact]
    public async Task FindParentAccountIds_returns_only_parents()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedCommercialTree(ctx);

        var parents = await MovementAccountValidator.FindParentAccountIdsAsync(
            ctx, new[] { ids["1"], ids["1100"], ids["11001"], ids["1101"] });

        Assert.Contains(ids["1"], parents);
        Assert.Contains(ids["1100"], parents);
        Assert.DoesNotContain(ids["11001"], parents);
        Assert.DoesNotContain(ids["1101"], parents);
    }

    [Fact]
    public async Task IsParent_true_for_parent_false_for_leaf()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedCommercialTree(ctx);

        Assert.True(await MovementAccountValidator.IsParentAsync(ctx, ids["1100"]));
        Assert.False(await MovementAccountValidator.IsParentAsync(ctx, ids["11001"]));
    }
}
