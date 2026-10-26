using System.Globalization;
using MudBlazor;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Requirement 2 — إدخال الأسعار/الأرقام بالأرقام اللاتينية (مثال: 2.38) مهما كانت ثقافة الواجهة.
///
/// السبب الجذري: حقول <c>MudNumericField</c> تعتمد على محوّل MudBlazor <see cref="DefaultConverter{T}"/>،
/// وثقافته الافتراضية هي <c>Converters.DefaultCulture</c> = <see cref="CultureInfo.CurrentUICulture"/>.
/// واجهة النظام عربية (ar) فتستخدم الفاصلة العشرية "٫" (U+066B) والأرقام الهندية، ولذلك يرفض المحوّل
/// تحويل النص "2.38" ويصبح 0.
///
/// الحل: ضبط خاصية <c>Culture</c> على حقول الأسعار/الأرقام فقط إلى <see cref="CultureInfo.InvariantCulture"/>
/// (أرقام لاتينية 0-9 + الفاصلة العشرية ".")، من دون تغيير ثقافة التطبيق العامة.
///
/// هذا الاختبار يثبّت سلوك المحوّل نفسه (نفس الآلية التي يستخدمها المكوّن) بمعزل عن الواجهة.
/// </summary>
public class LatinDecimalInputTests
{
    /// <summary>القيم المرجعية المطلوب دعمها في حقول الأسعار.</summary>
    public static readonly decimal[] Values = { 2.38m, 1.75m, 10.50m, 125.99m };

    public static IEnumerable<object[]> ValuesAsDecimal() => Values.Select(v => new object[] { v });

    [Fact]
    public void Numeric_converter_defaults_to_the_ambient_UI_culture()
    {
        // يوثّق السبب الجذري مباشرة: الثقافة الافتراضية للمحوّل تأتي من CurrentUICulture.
        Assert.Equal(CultureInfo.CurrentUICulture, new DefaultConverter<decimal>().Culture);
    }

    [Fact]
    public void Arabic_ui_culture_rejects_latin_dot_input()
    {
        // ثقافة "ar" تستخدم "٫" فاصلةً عشرية، لذا "2.38" لا يُحلَّل ويصبح 0 — وهو الخطأ الأصلي.
        var converter = new DefaultConverter<decimal> { Culture = CultureInfo.GetCultureInfo("ar") };

        Assert.NotEqual(".", converter.Culture.NumberFormat.NumberDecimalSeparator);
        Assert.Equal(0m, converter.Get("2.38"));
        Assert.True(converter.GetError);
    }

    [Theory]
    [MemberData(nameof(ValuesAsDecimal))]
    public void Invariant_culture_parses_and_formats_latin_decimal(decimal value)
    {
        // الإصلاح: InvariantCulture (أرقام لاتينية + ".") يحوّل "2.38" → 2.38 ويعيد التنسيق كما هو.
        var converter = new DefaultConverter<decimal> { Culture = CultureInfo.InvariantCulture, Format = "0.00" };

        var text = converter.Set(value);
        Assert.Equal(value.ToString("0.00", CultureInfo.InvariantCulture), text);
        Assert.False(converter.SetError);

        Assert.Equal(value, converter.Get(text));
        Assert.False(converter.GetError);
    }
}
