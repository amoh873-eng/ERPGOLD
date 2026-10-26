using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.Interfaces;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// المحرّك الموحّد لأرصدة شجرة الحسابات — مصدر واحد لأرقام شاشة «شجرة الحسابات»
/// وتقرير «شجرة الحسابات والأرصدة حسب المستويات» (منع تكرار منطق الاحتساب).
///
/// الأداء (منع N+1): لكل نداء ثلاثة استعلامات كحد أقصى بصرف النظر عن عدد الحسابات:
///   1) كل الحسابات غير المحذوفة (قراءة واحدة).
///   2) تجميع بنود القيود حسب الحساب حتى نهاية الفترة (GROUP BY على الخادم).
///   3) نفس التجميع حتى ما قبل بداية الفترة (لا يُنفَّذ إلا عند تحديد From).
/// ثم تُبنى الشجرة وتُجمَّع في الذاكرة (لا استعلام لكل حساب).
///
/// قاعدة التجميع: قيمة الحساب التجميعي = حركته المباشرة + مجموع قيم أبنائه.
/// كل بند قيد يُحتسب مرة واحدة فقط على حسابه ثم يصعد داخل الشجرة ⇒ لا ازدواجية في الأرصدة.
/// </summary>
public sealed class AccountBalanceService : IAccountBalanceService
{
    private readonly DbContext _context;

    public AccountBalanceService(DbContext context) => _context = context;

    /// <inheritdoc />
    public async Task<List<AccountBalanceNodeDto>> GetFlatBalancesAsync(AccountBalanceQuery? query = null)
        => (await GetBalanceTreeAsync(query)).Flattened;

    /// <inheritdoc />
    public async Task<AccountBalanceTreeDto> GetBalanceTreeAsync(AccountBalanceQuery? query = null)
    {
        query ??= new AccountBalanceQuery();

        // (1) كل الحسابات (بلا المحذوف) — قراءة واحدة، والترتيب بالكود لضمان تسلسل شجري ثابت.
        var accounts = await _context.Set<Account>().AsNoTracking()
            .Where(a => !a.IsDeleted)
            .ToListAsync();

        var byId = accounts.ToDictionary(a => a.Id);
        var ordered = accounts.OrderBy(a => a.Code, StringComparer.Ordinal).ToList();

        // (2) الحركة التراكمية حتى نهاية الفترة + (3) الرصيد الافتتاحي (عند تحديد From فقط).
        var cumulative = await SumMovementsAsync(query.To);
        var opening = query.From.HasValue
            ? await SumMovementsAsync(query.From.Value.Date.AddDays(-1))
            : EmptySums();

        var childrenByParent = ordered
            .Where(a => a.ParentAccountId is not null)
            .GroupBy(a => a.ParentAccountId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // الجذور: الحسابات بلا أب. التصفية على فرع واحد تبني الشجرة من الحساب المطلوب فقط.
        List<Account> roots = ordered.Where(a => a.ParentAccountId is null).ToList();
        if (query.RootAccountId is Guid rootId)
        {
            roots = byId.TryGetValue(rootId, out var onlyRoot)
                ? new List<Account> { onlyRoot }
                : new List<Account>();
        }

        var openingAggregated = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        var built = new List<AccountBalanceNodeDto>();
        foreach (var root in roots)
        {
            var node = Build(root, LevelOf(root, byId), byId, childrenByParent,
                cumulative, opening, openingAggregated, query.IncludeZeroBalance);
            if (node is not null) built.Add(node);
        }

        var tree = new AccountBalanceTreeDto
        {
            From = query.From,
            To = query.To,
            Roots = built,
            Flattened = Flatten(built).ToList()
        };

        // الإجماليات من الجذور فقط — منع احتساب رصيد الأبناء والأحفاد مرتين.
        tree.TotalDebit = MoneyRounding.Round(built.Sum(n => n.TotalDebit));
        tree.TotalCredit = MoneyRounding.Round(built.Sum(n => n.TotalCredit));
        tree.NetBalance = MoneyRounding.Round(tree.TotalDebit - tree.TotalCredit);
        tree.AggregateAccountsWithDirectMovements = tree.Flattened
            .Where(n => n.IsAggregate && (n.DirectDebit != 0m || n.DirectCredit != 0m))
            .Select(n => n.Code)
            .ToList();

        return tree;
    }

    // ══════════════════════ المساعدات ══════════════════════

    private static Dictionary<Guid, (decimal Debit, decimal Credit)> EmptySums()
        => new Dictionary<Guid, (decimal Debit, decimal Credit)>();

    /// <summary>مستوى الحساب الحقيقي: 1 = أب (بلا أب)، 2 = ابن، 3 = حفيد، وهكذا.</summary>
    private static int LevelOf(Account account, Dictionary<Guid, Account> byId)
    {
        var level = 1;
        var current = account;
        var guard = 0; // حماية من دورة غير متوقعة في بيانات قديمة
        while (current.ParentAccountId is Guid parentId
               && byId.TryGetValue(parentId, out var parent)
               && guard++ < 64)
        {
            level++;
            current = parent;
        }
        return level;
    }

    /// <summary>
    /// يبني عقدة الرصيد من أسفل إلى أعلى: الأبناء أولاً ثم تجميع الحساب (مباشر + الأبناء).
    /// includeZero = false يحذف الفروع الخالية تمامًا (بلا حركة وبلا رصيد افتتاحي وبلا أبناء).
    /// </summary>
    private static AccountBalanceNodeDto? Build(
        Account account,
        int level,
        Dictionary<Guid, Account> byId,
        Dictionary<Guid, List<Account>> childrenByParent,
        Dictionary<Guid, (decimal Debit, decimal Credit)> cumulative,
        Dictionary<Guid, (decimal Debit, decimal Credit)> opening,
        Dictionary<Guid, (decimal Debit, decimal Credit)> openingAggregated,
        bool includeZero)
    {
        var cum = cumulative.TryGetValue(account.Id, out var c) ? c : (Debit: 0m, Credit: 0m);
        var ope = opening.TryGetValue(account.Id, out var o) ? o : (Debit: 0m, Credit: 0m);

        Account? parent = null;
        if (account.ParentAccountId is Guid parentId) byId.TryGetValue(parentId, out parent);

        var node = new AccountBalanceNodeDto
        {
            Id = account.Id,
            Code = account.Code,
            NameAr = account.NameAr,
            NameEn = account.NameEn,
            Level = level,
            LevelNameAr = AccountTreeLevels.NameAr(level),
            ParentAccountId = account.ParentAccountId,
            ParentAccountCode = parent?.Code,
            ParentAccountNameAr = parent?.NameAr,
            AccountType = (int)account.AccountType,
            AccountTypeNameAr = AccountTypeNames.Ar(account.AccountType),
            NormalBalance = (int)account.NormalBalance,
            NormalBalanceNameAr = account.NormalBalance == NormalBalance.Debit
                ? AccountBalanceNature.Debit
                : AccountBalanceNature.Credit,
            IsActive = account.IsActive,
            DirectDebit = MoneyRounding.Round(cum.Debit),
            DirectCredit = MoneyRounding.Round(cum.Credit)
        };

        var hasChildren = childrenByParent.TryGetValue(account.Id, out var children) && children.Count > 0;
        node.IsAggregate = hasChildren;

        if (hasChildren)
        {
            foreach (var child in children!)
            {
                var childNode = Build(child, level + 1, byId, childrenByParent,
                    cumulative, opening, openingAggregated, includeZero);
                if (childNode is not null) node.Children.Add(childNode);
            }
        }

        // التجميع الصاعد: كل بند قيد يُحتسب مرة واحدة على حسابه ثم يُجمع هنا ⇒ لا ازدواجية.
        node.TotalDebit = MoneyRounding.Round(node.DirectDebit + node.Children.Sum(x => x.TotalDebit));
        node.TotalCredit = MoneyRounding.Round(node.DirectCredit + node.Children.Sum(x => x.TotalCredit));

        // الرصيد الافتتاحي التجميعي بنفس القاعدة، وحركة الفترة = التراكمي − الافتتاحي.
        decimal openDebit = MoneyRounding.Round(ope.Debit) + node.Children.Sum(x => openingAggregated[x.Id].Debit);
        decimal openCredit = MoneyRounding.Round(ope.Credit) + node.Children.Sum(x => openingAggregated[x.Id].Credit);
        openingAggregated[account.Id] = (openDebit, openCredit);

        node.OpeningBalance = MoneyRounding.Round(openDebit - openCredit);
        node.PeriodDebit = MoneyRounding.Round(node.TotalDebit - openDebit);
        node.PeriodCredit = MoneyRounding.Round(node.TotalCredit - openCredit);

        // الأرصدة: الصافي مفصول تمامًا عن إجمالي الحركة (لا خلط بينهما).
        node.NetBalance = MoneyRounding.Round(node.TotalDebit - node.TotalCredit);
        node.DebitBalance = node.NetBalance > 0m ? node.NetBalance : 0m;
        node.CreditBalance = node.NetBalance < 0m ? -node.NetBalance : 0m;
        node.NetNatureAr = AccountBalanceNature.Of(node.NetBalance);

        var hasValue = node.TotalDebit != 0m || node.TotalCredit != 0m
                       || node.OpeningBalance != 0m || node.Children.Count > 0;
        return includeZero || hasValue ? node : null;
    }

    /// <summary>ترتيب هرمي: الحساب ثم أبناؤه ثم أحفاده (Pre-Order) — ترتيب العرض في التقرير.</summary>
    private static IEnumerable<AccountBalanceNodeDto> Flatten(IEnumerable<AccountBalanceNodeDto> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    /// <summary>
    /// تجميع واحد لكل الحسابات: مجموع المدين والدائن لكل حساب **حتى تاريخ معيّن شمولاً**
    /// (null = كامل السجل). يستثني البنود/القيود المحذوفة (لا يوجد فلتر عام في السياق).
    /// </summary>
    private async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>> SumMovementsAsync(DateTime? toInclusive)
    {
        var query = _context.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.JournalEntry != null && !l.JournalEntry.IsDeleted);

        if (toInclusive.HasValue)
        {
            var exclusiveEnd = toInclusive.Value.Date.AddDays(1); // شمول تاريخ النهاية
            query = query.Where(l => l.JournalEntry!.EntryDate < exclusiveEnd);
        }

        var rows = await query
            .GroupBy(l => l.AccountId)
            .Select(g => new
            {
                AccountId = g.Key,
                Debit = g.Sum(x => x.DebitAmount),
                Credit = g.Sum(x => x.CreditAmount)
            })
            .ToListAsync();

        var map = new Dictionary<Guid, (decimal Debit, decimal Credit)>(rows.Count);
        foreach (var row in rows) map[row.AccountId] = (row.Debit, row.Credit);
        return map;
    }
}

