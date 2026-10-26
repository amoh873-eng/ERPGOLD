using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Phase B.3 — شكل Commercial (1100 رئيسي وله ورقة 11001) في قاعدة بيانات معزولة مستقلة
/// (تركيبة خاصة بهذا الصنف حتى لا يؤثر تحويل الشجرة على اختبارات أخرى).
/// </summary>
public class RootAccountCommercialShapeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public RootAccountCommercialShapeTests(AccountingPgFixture pg) => _pg = pg;

    [LivePgFact]
    public async Task Commercial_shape_rejects_parent_1100_and_accepts_leaf_11001()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // نحوّل الشجرة إلى شكل Commercial: 1100 رئيسي وله ورقة 11001.
        await _pg.CreateChildAccountAsync("1100", "11001", "الصندوق (فرعي)");

        var parent1100 = await _pg.FindAccountIdAsync("1100");
        var leaf11001 = await _pg.FindAccountIdAsync("11001");
        var leaf1101 = await _pg.FindAccountIdAsync("1101");

        await using (var ctx = _pg.NewContext())
        {
            // 1100 صار رئيسياً (له أبناء) → رفض
            await Assert.ThrowsAsync<ParentAccountMovementException>(
                () => MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { parent1100 }));

            // 11001 ورقة تحت 1100 → قبول
            await MovementAccountValidator.EnsureMovableAccountAsync(ctx, leaf11001);

            // 1101 ورقة → قبول
            await MovementAccountValidator.EnsureMovableAccountAsync(ctx, leaf1101);

            // المحلّل يهبط من الكود الرئيسي 1100 إلى الورقة 11001
            var resolved = await PostingAccountResolver.ResolveAsync(ctx, "1100");
            Assert.Equal("11001", resolved.Code);

            // الحساب الجذري «3» يُرفض حتى من المحلّل (لا استخدام ولا هبوط من جذري)
            await Assert.ThrowsAsync<RootAccountMovementException>(
                () => PostingAccountResolver.ResolveAsync(ctx, "3"));

            // المنسدلة: تستبعد Parent 1100 وتضم الورقة 11001، ولا تعرض أي حساب جذري
            var leaves = await new AccountService(ctx).GetLeafAccountsAsync();
            Assert.DoesNotContain(leaves, a => a.Code == "1100");
            Assert.Contains(leaves, a => a.Code == "11001");
            Assert.DoesNotContain(leaves, a => a.Code == "3");
        }
    }
}
