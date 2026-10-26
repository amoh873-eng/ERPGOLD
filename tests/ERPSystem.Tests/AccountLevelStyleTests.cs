using ERPSystem.Application.Reporting;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// معيار عرض مستويات الحسابات (أب/ابن/حفيد/أعمق): قيم ثابتة لكل مستوى،
/// ونفس المصدر يستهلكه جدول الشاشة (ReportTable) وتصدير PDF (PdfExporter).
/// هذه الاختبارات تثبّت العقد: لا يمكن أن يتغيّر مستوى واحد دون الآخر.
/// </summary>
public class AccountLevelStyleTests
{
    [Fact]
    public void Parent_child_grandchild_tokens_are_fixed_and_ordered()
    {
        var parent = AccountLevelStyle.For(1);
        var child = AccountLevelStyle.For(2);
        var grand = AccountLevelStyle.For(3);

        // الألوان المعتمدة في المهمة
        Assert.Equal("#174A7E", parent.ColorHex);
        Assert.Equal("#247653", child.ColorHex);
        Assert.Equal("#65518A", grand.ColorHex);

        // الأحجام والسماكات المعتمدة
        Assert.Equal(16, parent.FontSizePx);
        Assert.Equal(14, child.FontSizePx);
        Assert.Equal(13, grand.FontSizePx);
        Assert.Equal(700, parent.Weight);
        Assert.Equal(600, child.Weight);
        Assert.Equal(500, grand.Weight);

        // ترتيب ملزم: الأب أكبر ثم الابن ثم الحفيد
        Assert.True(parent.FontSizePx > child.FontSizePx);
        Assert.True(child.FontSizePx > grand.FontSizePx);
        Assert.True(parent.Weight > child.Weight);
        Assert.True(child.Weight > grand.Weight);

        // مستويات مسمّاة
        Assert.True(AccountLevelStyle.IsNamedLevel(1));
        Assert.True(AccountLevelStyle.IsNamedLevel(2));
        Assert.True(AccountLevelStyle.IsNamedLevel(3));
    }

    // المستوى 4 أصبح **مسمّى** في العقد الرباعي المعتمد ⇒ لم يعد «أعمق/محايد».
    // نختبر هنا المستويات خارج الحد (5 و9) فقط.
    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void Deeper_levels_use_neutral_style_and_are_not_misclassified(int level)
    {
        Assert.False(AccountLevelStyle.IsNamedLevel(level));

        var deep = AccountLevelStyle.For(level);
        Assert.Equal(AccountLevelStyle.DeepLevelKey, deep.StyleKey);
        Assert.Equal("#4B5563", deep.ColorHex);
        Assert.Equal(11, deep.FontSizePx);   // القيمة المعتمدة للنمط المحايد (DeepLevelKey)
        Assert.Equal(400, deep.Weight);

        // لا يستعير لون أي من المستويات الثلاثة المسمّاة
        Assert.NotEqual(AccountLevelStyle.For(1).ColorHex, deep.ColorHex);
        Assert.NotEqual(AccountLevelStyle.For(2).ColorHex, deep.ColorHex);
        Assert.NotEqual(AccountLevelStyle.For(3).ColorHex, deep.ColorHex);

        // كل المستويات الأعمق تأخذ النمط نفسه (اتساق كامل)
        Assert.Equal(AccountLevelStyle.For(7), deep);
    }

    [Fact]
    public void Zero_or_unknown_level_falls_back_to_root_style()
    {
        Assert.Equal(1, AccountLevelStyle.StyleKey(0));
        Assert.Equal(AccountLevelStyle.For(1), AccountLevelStyle.For(0));
    }

    [Fact]
    public void Css_class_and_inline_style_carry_the_very_same_tokens_used_by_pdf()
    {
        Assert.Equal("acct-lvl-1", AccountLevelStyle.CssClass(1));
        Assert.Equal("acct-lvl-2", AccountLevelStyle.CssClass(2));
        Assert.Equal("acct-lvl-3", AccountLevelStyle.CssClass(3));
        Assert.Equal("acct-lvl-" + AccountLevelStyle.DeepLevelKey, AccountLevelStyle.CssClass(6));   // الأعمق → فئة محايدة واحدة

        Assert.Contains("color:#174A7E", AccountLevelStyle.InlineStyle(1));
        Assert.Contains("font-size:16px", AccountLevelStyle.InlineStyle(1));
        Assert.Contains("font-weight:700", AccountLevelStyle.InlineStyle(1));

        Assert.Contains("acct-node", AccountLevelStyle.CssClasses(2));
        Assert.Contains("acct-lvl-2", AccountLevelStyle.CssClasses(2));

        // تحويل الحجم إلى نقاط لملف PDF (96dpi → 72pt): 16→12 ، 14→10.5 ، 13→9.75 ، 12→9
        Assert.Equal(12f, AccountLevelStyle.FontSizePt(1));
        Assert.Equal(10.5f, AccountLevelStyle.FontSizePt(2));
        Assert.Equal(9.75f, AccountLevelStyle.FontSizePt(3));
        Assert.Equal(9f, AccountLevelStyle.FontSizePt(4));
    }
}
