namespace ERPSystem.Application.DTOs.Accounts;

/// <summary>
/// Data Transfer Object for displaying account information in lists and tree views.
/// Separates the domain entity from what the UI sees (Separation of Concerns).
/// </summary>
public class AccountDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public int AccountType { get; set; }
    public string AccountTypeNameAr { get; set; } = string.Empty;
    public int NormalBalance { get; set; }
    public string NormalBalanceNameAr { get; set; } = string.Empty;
    public Guid? ParentAccountId { get; set; }
    public string? ParentAccountName { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public bool IsSystem { get; set; }
    public decimal CurrentBalance { get; set; }

    // ── البنية الهرمية (تُحتسب من الشجرة الفعلية لا من عمود مخزَّن) ──

    /// <summary>مستوى الحساب: 1 = أب، 2 = ابن، 3 = حفيد، 4+ = حفيد متعدد المستويات.</summary>
    public int Level { get; set; }

    /// <summary>اسم المستوى بالعربية (أب / ابن / حفيد).</summary>
    public string LevelNameAr { get; set; } = string.Empty;

    /// <summary>كود الحساب الأب (null للجذور).</summary>
    public string? ParentAccountCode { get; set; }

    /// <summary>حساب تجميعي = له أبناء غير محذوفين (لا تُرحَّل إليه حركات مباشرة وفق قاعدة النظام).</summary>
    public bool IsAggregate { get; set; }

    // ── الأرصدة (من خدمة الأرصدة الموحّدة IAccountBalanceService) ──
    // لا يُحتسب أي رصيد في الواجهة: هذه القيم تُعرض كما حُسبت في طبقة Application.

    /// <summary>صافي الرصيد الافتتاحي (مدين − دائن) — صفر بلا فترة محددة.</summary>
    public decimal OpeningBalance { get; set; }

    /// <summary>حركة الفترة المدينة.</summary>
    public decimal PeriodDebit { get; set; }

    /// <summary>حركة الفترة الدائنة.</summary>
    public decimal PeriodCredit { get; set; }

    /// <summary>إجمالي الحركة المدينة (مباشر + تجميع الأبناء).</summary>
    public decimal TotalDebit { get; set; }

    /// <summary>إجمالي الحركة الدائنة (مباشر + تجميع الأبناء).</summary>
    public decimal TotalCredit { get; set; }

    /// <summary>صافي الرصيد = TotalDebit − TotalCredit (موجب = مدين، سالب = دائن).</summary>
    public decimal NetBalance { get; set; }

    /// <summary>الجزء المدين من صافي الرصيد.</summary>
    public decimal DebitBalance { get; set; }

    /// <summary>الجزء الدائن من صافي الرصيد.</summary>
    public decimal CreditBalance { get; set; }

    /// <summary>طبيعة صافي الرصيد: «مدين» / «دائن» / «صفر».</summary>
    public string NetNatureAr { get; set; } = AccountBalanceNature.Zero;

    /// <summary>
    /// Child accounts for building the tree in the UI.
    /// </summary>
    public List<AccountDto> Children { get; set; } = new();
}
