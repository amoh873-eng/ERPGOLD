using ERPSystem.Domain.Enums;

namespace ERPSystem.Application.DTOs.Accounts;

/// <summary>
/// نطاق احتساب أرصدة شجرة الحسابات.
///  - <see cref="From"/> = null ⇒ لا رصيد افتتاحي سابق (كل السجل رصيد جارٍ).
///  - <see cref="To"/>   = null ⇒ حتى آخر حركة مسجّلة (كامل السجل).
///  - <see cref="RootAccountId"/> = تصفية على فرع واحد (حساب أب أو أي حساب) مع الحفاظ على مستواه الحقيقي.
/// </summary>
public sealed class AccountBalanceQuery
{
    /// <summary>بداية الفترة (شمولاً) — null = بلا حدود دنيا.</summary>
    public DateTime? From { get; set; }

    /// <summary>نهاية الفترة (شمولاً) — null = حتى آخر حركة.</summary>
    public DateTime? To { get; set; }

    /// <summary>تصفية على فرع حساب واحد (نفس الشجرة بدءاً من هذا الحساب).</summary>
    public Guid? RootAccountId { get; set; }

    /// <summary>إظهار الحسابات بلا حركة (رصيد صفر) — الافتراضي نعم للحفاظ على الشجرة كاملة.</summary>
    public bool IncludeZeroBalance { get; set; } = true;
}

/// <summary>
/// عقدة رصيد حساب — المصدر الموحّد لأرصدة المستويات الثلاثة (أب/ابن/حفيد).
///
/// قواعد الاحتساب المعتمدة (سياسة واحدة للشاشة والتقرير):
///  - الحركة المباشرة: DirectDebit / DirectCredit = مجموع بنود القيود المسجّلة على الحساب نفسه.
///  - الرصيد التجميعي: TotalDebit / TotalCredit = الحركة المباشرة + مجموع أرصدة الأبناء.
///    كل بند قيد يُحتسب **مرة واحدة فقط** على الحساب الذي سُجّل عليه ثم يصعد للأب — لا ازدواجية.
///  - NetBalance = TotalDebit − TotalCredit (موجب = مدين، سالب = دائن) — لا يُخلط مع إجمالي الحركة.
///  - NetNatureAr = طبيعة الرصيد الصافي (مدين/دائن/صفر).
///  - OpeningBalance = صافي الرصيد حتى ما قبل From (صفر عندما لا تُحدَّد فترة).
/// </summary>
public sealed class AccountBalanceNodeDto
{
    // ── الهوية والمستوى ──
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string? NameEn { get; set; }

    /// <summary>رقم المستوى: 1 = أب، 2 = ابن، 3 = حفيد، 4+ = حفيد متعدد المستويات.</summary>
    public int Level { get; set; }

    /// <summary>اسم المستوى بالعربية (أب / ابن / حفيد).</summary>
    public string LevelNameAr { get; set; } = string.Empty;

    public Guid? ParentAccountId { get; set; }
    public string? ParentAccountCode { get; set; }
    public string? ParentAccountNameAr { get; set; }

    public int AccountType { get; set; }
    public string AccountTypeNameAr { get; set; } = string.Empty;
    public int NormalBalance { get; set; }
    public string NormalBalanceNameAr { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    /// <summary>حساب تجميعي = له أبناء غير محذوفين (لا تُرحَّل إليه حركات مباشرة وفق قاعدة النظام).</summary>
    public bool IsAggregate { get; set; }

    // ── الحركة المدينة/الدائنة ──

    /// <summary>صافي الرصيد الافتتاحي (مدين − دائن) حتى ما قبل بداية الفترة.</summary>
    public decimal OpeningBalance { get; set; }

    /// <summary>حركة الفترة المدينة (بعد استثناء ما قبل الفترة وما بعدها).</summary>
    public decimal PeriodDebit { get; set; }

    /// <summary>حركة الفترة الدائنة.</summary>
    public decimal PeriodCredit { get; set; }

    /// <summary>الحركة المباشرة على الحساب نفسه — مدينة (تراكمي حتى نهاية الفترة).</summary>
    public decimal DirectDebit { get; set; }

    /// <summary>الحركة المباشرة على الحساب نفسه — دائنة (تراكمي حتى نهاية الفترة).</summary>
    public decimal DirectCredit { get; set; }

    /// <summary>إجمالي الحركة المدينة (مباشر + تجميع الأبناء) — تراكمي حتى نهاية الفترة.</summary>
    public decimal TotalDebit { get; set; }

    /// <summary>إجمالي الحركة الدائنة (مباشر + تجميع الأبناء) — تراكمي حتى نهاية الفترة.</summary>
    public decimal TotalCredit { get; set; }

    // ── الأرصدة ──

    /// <summary>صافي الرصيد = TotalDebit − TotalCredit (موجب = مدين، سالب = دائن).</summary>
    public decimal NetBalance { get; set; }

    /// <summary>الجزء المدين من صافي الرصيد (0 إذا كان دائنياً).</summary>
    public decimal DebitBalance { get; set; }

    /// <summary>الجزء الدائن من صافي الرصيد (0 إذا كان مدينياً).</summary>
    public decimal CreditBalance { get; set; }

    /// <summary>طبيعة صافي الرصيد: «مدين» / «دائن» / «صفر».</summary>
    public string NetNatureAr { get; set; } = AccountBalanceNature.Zero;

    /// <summary>الحسابات التابعة (ابن/حفيد).</summary>
    public List<AccountBalanceNodeDto> Children { get; set; } = new();
}

/// <summary>شجرة أرصدة الحسابات كاملة مع الإجماليات (من الجذور فقط — بلا ازدواجية).</summary>
public sealed class AccountBalanceTreeDto
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>هل حُدِّدت فترة (وبالتالي يوجد رصيد افتتاحي محتسب)؟</summary>
    public bool HasPeriod => From.HasValue;

    /// <summary>جذور الشجرة (المستوى الأول — الأب).</summary>
    public List<AccountBalanceNodeDto> Roots { get; set; } = new();

    /// <summary>كل العقد مسطّحة بترتيب هرمي (أب ثم أبناؤه ثم أحفاده) — جاهزة للتقارير.</summary>
    public List<AccountBalanceNodeDto> Flattened { get; set; } = new();

    /// <summary>إجمالي المدين = مجموع الجذور فقط (لا يُجمع الأبناء مرة أخرى — منع الازدواجية).</summary>
    public decimal TotalDebit { get; set; }

    /// <summary>إجمالي الدائن = مجموع الجذور فقط (لا يُجمع الأبناء مرة أخرى).</summary>
    public decimal TotalCredit { get; set; }

    /// <summary>إجمالي صافي الرصيد = مجموع الجذور فقط.</summary>
    public decimal NetBalance { get; set; }

    public bool IsBalanced => TotalDebit == TotalCredit;

    /// <summary>
    /// الحسابات التجميعية (لها أبناء) التي تحمل **حركة مباشرة** — للتوثيق والتدقيق فقط.
    /// قاعدة النظام الحالية تمنع الترحيل إلى الحسابات الجذرية/التجميعية، لذا القائمة متوقعة فارغة.
    /// </summary>
    public List<string> AggregateAccountsWithDirectMovements { get; set; } = new();
}

/// <summary>أسماء وطبيعة الأرصدة — ثوابت موحّدة (لا نصوص متناثرة في الواجهة والتقارير).</summary>
public static class AccountBalanceNature
{
    public const string Debit = "مدين";
    public const string Credit = "دائن";
    public const string Zero = "صفر";

    /// <summary>طبيعة الرصيد الصافي من قيمته (موجب = مدين، سالب = دائن، صفر = صفر).</summary>
    public static string Of(decimal netBalance)
        => netBalance > 0m ? Debit : netBalance < 0m ? Credit : Zero;
}

/// <summary>أسماء أنواع الحسابات بالعربية — تعريف واحد تستخدمه خدمات الطبقة والواجهة.</summary>
public static class AccountTypeNames
{
    public static string Ar(AccountType type) => type switch
    {
        AccountType.Asset => "أصول",
        AccountType.Liability => "خصوم",
        AccountType.Equity => "حقوق ملكية",
        AccountType.Revenue => "إيرادات",
        AccountType.Expense => "مصروفات",
        _ => "غير معروف"
    };
}

/// <summary>
/// أسماء مستويات شجرة الحسابات الأربعة — تعريف واحد يُستخدم في الشاشة والتقرير وPDF والاختبارات.
///
/// المستويات الأربعة المعتمدة (وتُعرض بهذه النصوص حرفيًا):
///   1 = المستوى الأول (الجد)
///   2 = المستوى الثاني (الأب)
///   3 = المستوى الثالث (الحفيد)
///   4 = المستوى الرابع (ابن الحفيد)
///
/// القواعد الملزمة:
///   • المستوى يُشتق من علاقة <c>ParentAccountId</c> الفعلية — لا من اسم الحساب ولا من طول رقمه.
///   • الحد الأقصى أربعة مستويات: لا يُسمح بمستوى خامس (<see cref="MaxLevel"/>).
/// </summary>
public static class AccountTreeLevels
{
    /// <summary>الحد الأقصى لعمق الشجرة: أربعة مستويات — المستوى الخامس مرفوض.</summary>
    public const int MaxLevel = 4;

    /// <summary>أدنى مستوى ممكن (جذر بلا أب).</summary>
    public const int MinLevel = 1;

    // ── النصوص الأربعة المعتمدة حرفيًا ──
    public const string Level1Ar = "المستوى الأول (الجد)";
    public const string Level2Ar = "المستوى الثاني (الأب)";
    public const string Level3Ar = "المستوى الثالث (الحفيد)";
    public const string Level4Ar = "المستوى الرابع (ابن الحفيد)";

    /// <summary>أسماء المستويات معرفةً — بترتيب المستوى.</summary>
    public static readonly string[] NamesAr = { Level1Ar, Level2Ar, Level3Ar, Level4Ar };

    /// <summary>
    /// اسم المستوى بالعربية:
    /// 1 = «المستوى الأول (الجد)»، 2 = «المستوى الثاني (الأب)»،
    /// 3 = «المستوى الثالث (الحفيد)»، 4 = «المستوى الرابع (ابن الحفيد)».
    /// أي مستوى خارج 1..4 يُوصف صراحةً بأنه خارج الحد المسموح (مسار دفاعي فقط — التصميم يمنعه).
    /// </summary>
    public static string NameAr(int level) => level switch
    {
        1 => Level1Ar,
        2 => Level2Ar,
        3 => Level3Ar,
        4 => Level4Ar,
        _ => "مستوى " + level + " (خارج الحد المسموح — الأقصى " + MaxLevel + ")"
    };

    /// <summary>هل المستوى داخل الحد المسموح (1..4)؟</summary>
    public static bool IsValidLevel(int level) => level is >= MinLevel and <= MaxLevel;

    /// <summary>
    /// هل يجوز أن يكون لهذا المستوى حساب ابن؟ (كل المستويات إلا الرابع — الذي هو آخر مستوى).
    /// هو أساس قاعدتي: «منع المستوى الخامس» و«منع اختيار حساب غير صالح كأب».
    /// </summary>
    public static bool CanHaveChildren(int level) => level is >= MinLevel and < MaxLevel;

    /// <summary>المستوى الناتج عن إسناد أبٍ في المستوى المعطى (بلا أب ⇒ المستوى الأول).</summary>
    public static int ChildLevelOf(int? parentLevel)
        => parentLevel is null ? MinLevel : parentLevel.Value + 1;
}
