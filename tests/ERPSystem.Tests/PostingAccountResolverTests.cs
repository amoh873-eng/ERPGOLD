using ERPSystem.Application.Services;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// الحل الديناميكي للحسابات الافتراضية حسب بنية الشجرة (Requirement 1).
/// لا يوجد Hard-code عالمي (1100 → 11001): الهبوط إلى الورقة الصحيحة يحدث فقط عند كون الكود رئيسياً.
/// </summary>
public class PostingAccountResolverTests
{
    [Fact]
    public async Task Production_1100_leaf_resolves_to_itself()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        AccountingSafetyFixtures.SeedProductionTree(ctx);

        var resolved = await PostingAccountResolver.ResolveAsync(ctx, "1100");

        Assert.Equal("1100", resolved.Code);
    }

    [Fact]
    public async Task Commercial_1100_parent_resolves_to_leaf_child_11001()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        AccountingSafetyFixtures.SeedCommercialTree(ctx);

        var resolved = await PostingAccountResolver.ResolveAsync(ctx, "1100");

        Assert.Equal("11001", resolved.Code);
    }

    [Fact]
    public async Task Commercial_resolved_1100_passes_central_validation()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        AccountingSafetyFixtures.SeedCommercialTree(ctx);

        // المسار التلقائي في Commercial: يحل 1100 → 11001 (ورقة) فلا يُرفض بالتحقق المركزي.
        var resolved = await PostingAccountResolver.ResolveAsync(ctx, "1100");
        await MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { resolved.Id });
    }

    [Fact]
    public async Task Unknown_code_throws()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        AccountingSafetyFixtures.SeedCommercialTree(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostingAccountResolver.ResolveAsync(ctx, "9999"));
    }
}
