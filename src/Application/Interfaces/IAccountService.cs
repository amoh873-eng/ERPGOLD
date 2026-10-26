using ERPSystem.Application.DTOs.Accounts;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// Defines the contract for account-related business operations.
/// The Web layer depends on this interface, not on the concrete implementation
/// (Dependency Inversion Principle - the 'D' in SOLID).
/// </summary>
public interface IAccountService
{
    /// <summary>
    /// Returns all active accounts as a hierarchical tree structure.
    /// </summary>
    Task<List<AccountDto>> GetAccountTreeAsync();

    /// <summary>
    /// Returns a flat list of all accounts (useful for dropdowns and search).
    /// </summary>
    Task<List<AccountDto>> GetAllAccountsAsync();

    /// <summary>
    /// Returns a flat list of the movement accounts only — those with no active children (Leaf).
    /// يُستخدم في شاشات الحركات المحاسبية (القيد اليدوي/فئات المصاريف) لعرض الحسابات المتحركة فقط،
    /// تطبيقاً للقاعدة المحاسبية: لا حركة مباشرة على حساب رئيسي (Parent).
    /// لا يغيّر هذا شجرة الحسابات الإدارية (GetAccountTreeAsync يبقى كما هو).
    /// </summary>
    Task<List<AccountDto>> GetLeafAccountsAsync();

    /// <summary>
    /// Gets a single account by its unique identifier.
    /// </summary>
    Task<AccountDto?> GetByIdAsync(Guid id);

    /// <summary>
    /// Returns the flat list of accounts that may be chosen as a PARENT for a new or edited account.
    /// Excludes the edited account itself and all its descendants (prevents cycles), and excludes
    /// any account already at the deepest level («المستوى الرابع (ابن الحفيد)») because it cannot have children.
    /// يُستخدم في منسدلة «الحساب الأب» لمنع اختيار حساب غير صالح من الأساس.
    /// </summary>
    /// <param name="editingAccountId">معرّف الحساب عند التعديل (null عند الإنشاء).</param>
    Task<List<AccountDto>> GetValidParentAccountsAsync(Guid? editingAccountId = null);

    /// <summary>
    /// Creates a new account. Validates uniqueness of code and parent existence.
    /// Returns the created account DTO.
    /// </summary>
    Task<AccountDto> CreateAsync(CreateAccountDto dto);

    /// <summary>
    /// Updates an existing account. Returns the updated account DTO.
    /// </summary>
    Task<AccountDto> UpdateAsync(UpdateAccountDto dto);

    /// <summary>
    /// Soft-deletes an account (sets IsDeleted = true).
    /// Fails if the account has child accounts or journal entries.
    /// </summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Toggles the active status of an account.
    /// </summary>
    Task ToggleActiveAsync(Guid id);
}
