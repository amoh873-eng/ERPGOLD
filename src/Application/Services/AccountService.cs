using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.Interfaces;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// Implements account management business logic.
/// This is the core service for the Chart of Accounts module.
/// 
/// شرح منطق العمل:
/// - عند إنشاء حساب، نتحقق من: عدم تكرار الكود، وجود الحساب الأب، صحة النوع
/// - عند الحذف، نمنع حذف الحسابات النظامية أو التي لها أبناء
/// - نحدد NormalBalance تلقائياً من نوع الحساب:
///   الأصول والمصروفات → مدين، الخصوم وحقوق الملكية والإيرادات → دائن
/// - الأرصدة تُحتسب في خدمة واحدة مشتركة (AccountBalanceService) وتُعرض في نفس الصف
///   لكل المستويات: الأب (تجميع)، الابن (تجميع)، الحفيد (تفصيلي).
/// </summary>
public class AccountService : IAccountService
{
    private readonly DbContext _context;
    private readonly IAccountBalanceService _balanceService;

    /// <summary>
    /// الاحتساب موكول لخدمة الأرصدة الموحّدة (IAccountBalanceService) حتى لا يتكرر منطق الأرصدة
    /// بين شجرة الحسابات والتقرير. المعامل الثاني اختياري للحفاظ على توافق الاستدعاءات القائمة
    /// (new AccountService(ctx)) في الاختبارات وأي كود قديم.
    /// </summary>
    public AccountService(DbContext context, IAccountBalanceService? balanceService = null)
    {
        _context = context;
        _balanceService = balanceService ?? new AccountBalanceService(context);
    }

    /// <summary>
    /// شجرة الحسابات الإدارية + أرصدة المستويات الثلاثة (أب/ابن/حفيد) لكل صف.
    /// الرصيد يأتي من المصدر الموحّد: الحساب التجميعي = حركته المباشرة + مجموع أبنائه بلا ازدواجية.
    /// </summary>
    public async Task<List<AccountDto>> GetAccountTreeAsync()
    {
        var accounts = await _context.Set<Account>()
            .Where(a => !a.IsDeleted)
            .OrderBy(a => a.Code)
            .ToListAsync();

        // لماذا نبني الشجرة في الكود وليس في قاعدة البيانات؟
        // EF Core لا يدعم استعلامات تكرارية (Recursive CTEs) مباشرة بكفاءة.
        // البديل: نجلب كل الحسابات دفعة واحدة (Query واحد)، ثم نبني الشجرة في الذاكرة.
        // والأرصدة تُجلب باستعلامين تجميعيين ثابتين (لا استعلام لكل حساب ⇒ لا N+1).
        var balances = await _balanceService.GetBalanceTreeAsync();
        var balanceById = balances.Flattened.ToDictionary(n => n.Id);

        return BuildTree(accounts, null, balanceById);
    }

    public async Task<List<AccountDto>> GetAllAccountsAsync()
    {
        var accounts = await _context.Set<Account>()
            .Where(a => !a.IsDeleted)
            .OrderBy(a => a.Code)
            .ToListAsync();

        // المستوى الحقيقي مشتق من الشجرة الفعلية (ParentAccountId) — لا من عمود مخزَّن.
        var levels = await AccountHierarchyPolicy.LevelMapAsync(_context);
        var parentIds = accounts
            .Where(a => a.ParentAccountId != null)
            .Select(a => a.ParentAccountId!.Value)
            .ToHashSet();

        return accounts
            .Select(a => MapToDto(a,
                levels.TryGetValue(a.Id, out var level) ? level : AccountTreeLevels.MinLevel,
                parentIds.Contains(a.Id)))
            .ToList();
    }

    /// <summary>
    /// يعيد الحسابات المتحركة النشطة فقط (Movable Active) — أي حساب:
    ///  - ليس والداً لأي حساب غير محذوف،
    ///  - وليس حساباً جذرياً/تصنيفياً (ParentAccountId != null)،
    ///  - ونشط (IsActive = true).
    /// بهذا لا تظهر رؤوس التصنيفات (1، 2، 3، 4، 5) ولا الحسابات المعطَّلة في منسدلات الحركات.
    /// القاعدة تُشتق من بنية الشجرة الفعلية لكل قاعدة بيانات (لا يوجد عمود IsParent مخزّن)،
    /// لذا تعمل ديناميكياً في Production و Commercial معاً.
    /// </summary>
    public async Task<List<AccountDto>> GetLeafAccountsAsync()
    {
        var accounts = await _context.Set<Account>()
            .Where(a => !a.IsDeleted)
            .OrderBy(a => a.Code)
            .ToListAsync();

        var parentIds = accounts
            .Where(a => a.ParentAccountId != null)
            .Select(a => a.ParentAccountId!.Value)
            .ToHashSet();

        var levels = await AccountHierarchyPolicy.LevelMapAsync(_context);

        return accounts
            .Where(a => a.ParentAccountId is not null && a.IsActive && !parentIds.Contains(a.Id))
            .Select(a => MapToDto(a,
                levels.TryGetValue(a.Id, out var level) ? level : AccountTreeLevels.MinLevel,
                isAggregate: false))   // كل صف هنا ورقة (ليست أبًا لأحد)
            .ToList();
    }

    /// <summary>
    /// يعيد الحسابات الصالحة كـ«حساب أب» — تُستخدم في منسدلة شاشة الحسابات.
    /// الاستبعادات (بنفس قواعد السياسة، فلا يمكن اختيار حساب غير صالح من الأساس):
    ///   • الحساب نفسه عند التعديل (منع أن يكون أبًا لنفسه).
    ///   • كل أبنائه وأحفاده (منع العلاقة الدائرية).
    ///   • كل حساب في المستوى الرابع (آخر مستوى) لأنه لا يقبل أبناء.
    ///   • الحسابات المعطَّلة والمحذوفة (منع سلاسل مقطوعة).
    /// المستوى الحقيقي لكل حساب مشتق من الشجرة الفعلية لا من رقمه.
    /// </summary>
    public async Task<List<AccountDto>> GetValidParentAccountsAsync(Guid? editingAccountId = null)
    {
        var parentMap = await AccountHierarchyPolicy.LoadParentMapAsync(_context);

        var excluded = editingAccountId.HasValue
            ? AccountHierarchyPolicy.DescendantsOf(editingAccountId.Value, parentMap)
            : new HashSet<Guid>();
        if (editingAccountId.HasValue) excluded.Add(editingAccountId.Value);

        var parentIds = parentMap.Values
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToHashSet();

        var accounts = await _context.Set<Account>()
            .Where(a => !a.IsDeleted && a.IsActive)
            .OrderBy(a => a.Code)
            .ToListAsync();

        var result = new List<AccountDto>();
        foreach (var account in accounts)
        {
            if (excluded.Contains(account.Id)) continue;

            var level = AccountHierarchyPolicy.LevelOf(account.Id, parentMap);

            // الحساب في المستوى الرابع (آخر مستوى) لا يصلح أبًا — منع المستوى الخامس مقدماً.
            if (!AccountTreeLevels.CanHaveChildren(level)) continue;

            result.Add(MapToDto(account, level, parentIds.Contains(account.Id)));
        }

        return result;
    }


    public async Task<AccountDto?> GetByIdAsync(Guid id)
    {
        var account = await _context.Set<Account>()
            .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted);

        return account is null ? null : await MapSingleAsync(account);
    }

    public async Task<AccountDto> CreateAsync(CreateAccountDto dto)
    {
        // التحقق من عدم تكرار الكود
        var codeExists = await _context.Set<Account>()
            .AnyAsync(a => a.Code == dto.Code && !a.IsDeleted);
        if (codeExists)
            throw new InvalidOperationException($"كود الحساب '{dto.Code}' موجود مسبقاً");

        // التحقق من صحة الإسناد الهرمي (المستويات الأربعة):
        //  - الأب موجود وغير محذوف وغير معطَّل
        //  - منع المستوى الخامس (لا ابن تحت المستوى الرابع)
        //  - لا يكون الحساب أبًا لنفسه (غير وارد عند الإنشاء، ويُفحص في التعديل)
        // تُعيد مستوى الحساب الجديد (1 = الجد بلا أب، 2 = الأب، 3 = الحفيد، 4 = ابن الحفيد).
        var newLevel = await AccountHierarchyPolicy.EnsureParentAssignmentAllowedAsync(
            _context, accountId: null, parentAccountId: dto.ParentAccountId);

        var account = new Account
        {
            Id = Guid.NewGuid(),
            Code = dto.Code,
            NameAr = dto.NameAr,
            NameEn = dto.NameEn,
            AccountType = (AccountType)dto.AccountType,
            // تحديد طبيعة الحساب تلقائياً من نوعه - لا نتركه للمستخدم يحدده
            NormalBalance = DetermineNormalBalance((AccountType)dto.AccountType),
            ParentAccountId = dto.ParentAccountId,
            Description = dto.Description,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Set<Account>().Add(account);
        await _context.SaveChangesAsync();

        // الحساب الجديد بلا أبناء ⇒ ليس تجميعيًا، ومستواه محسوب من الإسناد أعلاه.
        return MapToDto(account, newLevel, isAggregate: false);
    }

    public async Task<AccountDto> UpdateAsync(UpdateAccountDto dto)
    {
        var account = await _context.Set<Account>()
            .FirstOrDefaultAsync(a => a.Id == dto.Id && !a.IsDeleted);

        if (account is null)
            throw new InvalidOperationException("الحساب غير موجود");

        if (account.IsSystem)
            throw new InvalidOperationException("لا يمكن تعديل الحسابات النظامية");

        var codeExists = await _context.Set<Account>()
            .AnyAsync(a => a.Code == dto.Code && a.Id != dto.Id && !a.IsDeleted);
        if (codeExists)
            throw new InvalidOperationException($"كود الحساب '{dto.Code}' موجود مسبقاً");

        // التحقق من صحة الإسناد الهرمي الجديد (المستويات الأربعة):
        //  - لا يكون الحساب أبًا لنفسه
        //  - منع المستوى الخامس (لا نقل إلى حساب في المستوى الرابع)
        //  - منع العلاقة الدائرية (نقل الحساب تحت أحد أبنائه/أحفاده)
        var newLevel = await AccountHierarchyPolicy.EnsureParentAssignmentAllowedAsync(
            _context, accountId: dto.Id, parentAccountId: dto.ParentAccountId);

        account.Code = dto.Code;
        account.NameAr = dto.NameAr;
        account.NameEn = dto.NameEn;
        account.AccountType = (AccountType)dto.AccountType;
        account.NormalBalance = DetermineNormalBalance((AccountType)dto.AccountType);
        account.ParentAccountId = dto.ParentAccountId;
        account.Description = dto.Description;
        account.IsActive = dto.IsActive;
        account.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        var isAggregate = await AccountHierarchyPolicy.IsAggregateAsync(_context, account.Id);
        return MapToDto(account, newLevel, isAggregate);
    }

    public async Task DeleteAsync(Guid id)
    {
        var account = await _context.Set<Account>()
            .Include(a => a.ChildAccounts)
            .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted);

        if (account is null)
            throw new InvalidOperationException("الحساب غير موجود");

        if (account.IsSystem)
            throw new InvalidOperationException("لا يمكن حذف الحسابات النظامية");

        var activeChildren = account.ChildAccounts.Where(c => !c.IsDeleted).ToList();
        if (activeChildren.Any())
            throw new InvalidOperationException(
                $"لا يمكن حذف الحساب لأنه يحتوي على {activeChildren.Count} حساب فرعي. احذف الحسابات الفرعية أولاً.");

        account.IsDeleted = true;
        account.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task ToggleActiveAsync(Guid id)
    {
        var account = await _context.Set<Account>()
            .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted);

        if (account is null)
            throw new InvalidOperationException("الحساب غير موجود");

        if (account.IsSystem)
            throw new InvalidOperationException("لا يمكن تعطيل الحسابات النظامية");

        account.IsActive = !account.IsActive;
        account.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }
    // ==================== Helper Methods ====================

    /// <summary>
    /// Determines the normal balance based on account type.
    /// هذا منطق محاسبي أساسي:
    /// - الأصول والمصروفات: طبيعتها مدينة (تزيد بالمدين)
    /// - الخصوم وحقوق الملكية والإيرادات: طبيعتها دائنة (تزيد بالدائن)
    /// </summary>
    private static NormalBalance DetermineNormalBalance(AccountType type)
    {
        return type switch
        {
            AccountType.Asset => NormalBalance.Debit,
            AccountType.Expense => NormalBalance.Debit,
            AccountType.Liability => NormalBalance.Credit,
            AccountType.Equity => NormalBalance.Credit,
            AccountType.Revenue => NormalBalance.Credit,
            _ => NormalBalance.Debit
        };
    }

    /// <summary>
    /// Builds a hierarchical tree from a flat list of accounts.
    /// خوارزمية بناء الشجرة:
    /// 1. نأخذ الحسابات التي ParentAccountId لها يساوي parentId المعطى
    /// 2. لكل حساب، ننشئ DTO ونستدعي الدالة تكرارياً لبناء الأبناء
    /// 3. النتيجة: شجرة كاملة متعددة المستويات
    /// </summary>
    private List<AccountDto> BuildTree(
        List<Account> allAccounts,
        Guid? parentId,
        Dictionary<Guid, AccountBalanceNodeDto> balanceById)
    {
        return allAccounts
            .Where(a => a.ParentAccountId == parentId)
            .Select(a =>
            {
                balanceById.TryGetValue(a.Id, out var balance);
                var dto = MapToDto(a, balance);
                dto.Children = BuildTree(allAccounts, a.Id, balanceById);
                return dto;
            })
            .ToList();
    }

    /// <summary>
    /// Maps a domain Account entity to an AccountDto.
    /// استخدام الـ Mapping اليدوي بدلاً من AutoMapper للتعلم والتحكم الكامل.
    /// عند توفر عقدة رصيد من الخدمة الموحّدة تُنسخ حقول المستوى والأرصدة كما حُسبت (لا حساب في الواجهة).
    /// </summary>
    private static AccountDto MapToDto(Account account, AccountBalanceNodeDto? balance = null)
    {
        var dto = new AccountDto
        {
            Id = account.Id,
            Code = account.Code,
            NameAr = account.NameAr,
            NameEn = account.NameEn,
            AccountType = (int)account.AccountType,
            AccountTypeNameAr = AccountTypeNames.Ar(account.AccountType),
            NormalBalance = (int)account.NormalBalance,
            NormalBalanceNameAr = account.NormalBalance == NormalBalance.Debit
                ? AccountBalanceNature.Debit
                : AccountBalanceNature.Credit,
            ParentAccountId = account.ParentAccountId,
            ParentAccountName = account.ParentAccount?.NameAr,
            Description = account.Description,
            IsActive = account.IsActive,
            IsSystem = account.IsSystem,
            CurrentBalance = account.CurrentBalance
        };

        if (balance is not null)
        {
            dto.Level = balance.Level;
            dto.LevelNameAr = balance.LevelNameAr;
            dto.ParentAccountCode = balance.ParentAccountCode;
            if (balance.ParentAccountNameAr is not null) dto.ParentAccountName = balance.ParentAccountNameAr;
            dto.IsAggregate = balance.IsAggregate;
            dto.OpeningBalance = balance.OpeningBalance;
            dto.PeriodDebit = balance.PeriodDebit;
            dto.PeriodCredit = balance.PeriodCredit;
            dto.TotalDebit = balance.TotalDebit;
            dto.TotalCredit = balance.TotalCredit;
            dto.NetBalance = balance.NetBalance;
            dto.DebitBalance = balance.DebitBalance;
            dto.CreditBalance = balance.CreditBalance;
            dto.NetNatureAr = balance.NetNatureAr;
        }
        else
        {
            // مسار دفاعي فقط (لا يفترض حدوثه): المستوى من العلاقة المباشرة (لا أكثر من أب واحد في هذا المسار).
            dto.Level = account.ParentAccountId is null ? AccountTreeLevels.MinLevel : AccountTreeLevels.MinLevel + 1;
            dto.LevelNameAr = AccountTreeLevels.NameAr(dto.Level);
        }

        return dto;
    }

    /// <summary>
    /// نفس التحويل لكن بمستوى حقيقي وحالة تجميعية محدَّدة صراحةً (بلا أرصدة).
    /// تُستخدم في مسارات الإضافة/التعديل/حساب واحد حيث لا تُستدعى خدمة الأرصدة.
    /// </summary>
    private static AccountDto MapToDto(Account account, int level, bool isAggregate)
    {
        var dto = MapToDto(account);
        dto.Level = level;
        dto.LevelNameAr = AccountTreeLevels.NameAr(level);
        dto.IsAggregate = isAggregate;
        return dto;
    }

    /// <summary>
    /// يحوّل حسابًا واحدًا إلى DTO بمستواه الحقيقي وحالته التجميعية مشتقَّين من الشجرة الفعلية
    /// (ParentAccountId) — لا من أي عمود مخزَّن ولا من رقم الحساب.
    /// </summary>
    private async Task<AccountDto> MapSingleAsync(Account account)
    {
        var parentMap = await AccountHierarchyPolicy.LoadParentMapAsync(_context);
        var level = AccountHierarchyPolicy.LevelOf(account.Id, parentMap);
        var isAggregate = parentMap.Values.Any(parentId => parentId == account.Id);
        return MapToDto(account, level, isAggregate);
    }
}

