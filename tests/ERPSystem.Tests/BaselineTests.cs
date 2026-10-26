using System.Globalization;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// اختبارات أساسية (Baseline) تثبت أن مشروع الاختبارات مربوط صحيحًا
/// بمشاريع Application/Infrastructure وأن الأرقام العشرية تُعامَل كـ decimal بلا فقدان دقة.
/// هذه اختبارات تحضيرية فقط؛ اختبارات المطالب التفصيلية تُضاف مع تنفيذها.
/// </summary>
public class BaselineTests
{
    [Theory]
    [InlineData("1.75", 1.75)]
    [InlineData("2.38", 2.38)]
    [InlineData("10.50", 10.50)]
    [InlineData("125.99", 125.99)]
    [InlineData("0.00", 0.00)]
    public void Decimal_literals_roundtrip_losslessly_invariant(string text, decimal expected)
    {
        var parsed = decimal.Parse(text, CultureInfo.InvariantCulture);
        Assert.Equal(expected, parsed);
        // لا تُقرّب، ولا تصبح 238، ولا تُفقد الخانتان
        Assert.Equal(text, parsed.ToString("0.00", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AppDbContext_builds_with_inmemory_provider()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: "baseline-" + Guid.NewGuid())
            .Options;

        using var context = new AppDbContext(options);

        // بناء الموديل يثبت أن ربط الـEntities/Configurations سليم
        var model = context.Model;
        Assert.NotNull(model);
    }
}
