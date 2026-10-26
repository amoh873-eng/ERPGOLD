using ERPSystem.Application.DTOs.Accounts;

namespace ERPSystem.Application.Reporting;

/// <summary>
/// معيار موحّد لعرض مستويات دليل الحسابات الأربعة
/// (الجد / الأب / الحفيد / ابن الحفيد) في الواجهة وفي PDF وفي Excel.
///
/// مصدر واحد للقيم ⇒ لا يمكن أن تختلف ألوان/أحجام/سماكات المستويات بين الشاشة وملف PDF.
/// القواعد الملزمة:
///   • كل حساب في نفس المستوى يأخذ نفس اللون والحجم والسماكة (لا علاقة للون بالرصيد أو نوع الحركة).
///   • اللون مستقل عن رقم الحساب وطوله.
///   • المستوى مشتق من العلاقة الفعلية (ParentAccountId) في طبقة البيانات — لا يُحتسب هنا.
///   • المستويات الأربعة مسمّاة بأسمائها المعتمدة من <see cref="AccountTreeLevels"/> (مصدر واحد للتسمية).
///   • اللون وحده ليس الوسيلة الوحيدة: تبقى المسافات البادئة ورموز الحسابات وعمود «المستوى» ظاهرة.
/// </summary>
public static class AccountLevelStyle
{
    /// <summary>
    /// عدد المستويات المسمّاة صراحة = أربعة (الجد/الأب/الحفيد/ابن الحفيد).
    /// مشتق من تعريف أسماء المستويات نفسه حتى لا يفترق النمط عن التسمية.
    /// </summary>
    public const int NamedLevels = AccountTreeLevels.MaxLevel;

    /// <summary>مفتاح النمط المحايد — مسار دفاعي فقط لأي مستوى خارج 1..4 (التصميم يمنعه).</summary>
    public const int DeepLevelKey = NamedLevels + 1;

    /// <summary>
    /// يرجّح مفتاح التنسيق للمستوى: 1/2/3/4 كما هي (أربعة مستويات مسمّاة)،
    /// وأي مستوى خارج الحد يُعرض بالنمط المحايد الموحّد.
    /// يُستخدم لفئات CSS في الواجهة، بينما يحتفظ عمود «المستوى» واسم المستوى بالقيمة الحقيقية.
    /// </summary>
    public static int StyleKey(int level) => level switch
    {
        <= 1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        _ => DeepLevelKey
    };

    /// <summary>نمط المستوى (لون + حجم بالبكسل + سماكة CSS) — القيم المرجعية المعتمدة للمهمة.</summary>
    public static AccountLevelTokens For(int level)
    {
        var key = StyleKey(level);
        return key switch
        {
            1 => new AccountLevelTokens(1, "#174A7E", 16, 700, AccountTreeLevels.Level1Ar),
            2 => new AccountLevelTokens(2, "#247653", 14, 600, AccountTreeLevels.Level2Ar),
            3 => new AccountLevelTokens(3, "#65518A", 13, 500, AccountTreeLevels.Level3Ar),
            4 => new AccountLevelTokens(4, "#8A4A18", 12, 450, AccountTreeLevels.Level4Ar),
            _ => new AccountLevelTokens(DeepLevelKey, "#4B5563", 11, 400, "خارج الحد المسموح")
        };
    }

    /// <summary>فئة CSS للمستوى (acct-lvl-1 .. acct-lvl-4) — نفس الفئات في الجداول والشجرة والقوائم.</summary>
    public static string CssClass(int level) => "acct-lvl-" + StyleKey(level);

    /// <summary>فئات CSS كاملة تُضاف لعنصر يخص مستوى حساب معيّن.</summary>
    public static string CssClasses(int level) => "acct-node " + CssClass(level);

    /// <summary>
    /// نمط داخلي (لون/حجم/سماكة) يُشتق من نفس القيم — يضمن تطابق الشاشة مع PDF تمامًا
    /// حتى لو تعذّر تحميل ملف الأنماط في السياق الحالي (نافذة/طباعة/تصدير HTML).
    /// </summary>
    public static string InlineStyle(int level)
    {
        var t = For(level);
        return $"color:{t.ColorHex};font-size:{t.FontSizePx}px;font-weight:{t.Weight}";
    }

    /// <summary>حجم الخط بالنقاط لاستخدامه في PDF (تحويل 96dpi ⇒ 72pt).</summary>
    public static float FontSizePt(int level) => For(level).FontSizePx * 0.75f;

    /// <summary>هل هذا مستوى من الأربعة المسمّاة (الجد/الأب/الحفيد/ابن الحفيد)؟</summary>
    public static bool IsNamedLevel(int level) => level is >= 1 and <= NamedLevels;
}

/// <summary>قيم تنسيق مستوى حساب واحد (مصدر الحقيقة الواحد للشاشة وPDF).</summary>
/// <param name="StyleKey">1..4 (المستويات المسمّاة)، و5 = النمط المحايد لأي مستوى خارج الحد.</param>
/// <param name="ColorHex">لون النص بصيغة #RRGGBB (داكن ومقروء على خلفية فاتحة وقابل للطباعة).</param>
/// <param name="FontSizePx">حجم الخط بالبكسل للواجهة (يقابله FontSizePt في PDF).</param>
/// <param name="Weight">سماكة CSS: 700/600/500/400.</param>
/// <param name="NameAr">وصف النمط بالعربية للتوثيق والتحقق.</param>
public readonly record struct AccountLevelTokens(int StyleKey, string ColorHex, int FontSizePx, int Weight, string NameAr);
