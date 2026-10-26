using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// أدوات تحقق محاسبية للاختبارات (كود اختبار فقط — لا يعدّل أي بيانات):
///  - تطابق رصيد كل حساب مخزَّن مع دفتر القيود وفق قاعدة NormalBalance المعتمدة.
///  - توازن كل قيد على حدة، وميزان المراجعة الكلي.
///  - لا حركات على حسابات رئيسية (Parent)، ولا تواريخ غير معقولة، ولا أرقام مستندات مكررة.
/// </summary>
internal static class LedgerInvariants
{
    public sealed record AccountCheck(string Code, string NameAr, decimal Stored, decimal Ledger);

    /// <summary>رصيد كل حساب: المخزَّن (Account.CurrentBalance) مقابل المحسوب من دفتر القيود.</summary>
    public static async Task<List<AccountCheck>> AccountsAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();

        var accounts = await ctx.Set<Account>().AsNoTracking()
            .Where(a => !a.IsDeleted)
            .ToListAsync();

        var sums = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
            .ToListAsync();

        var map = sums.ToDictionary(x => x.AccountId, x => (x.D, x.C));

        return accounts
            .OrderBy(a => a.Code, StringComparer.Ordinal)
            .Select(a =>
            {
                decimal d = 0m, c = 0m;
                if (map.TryGetValue(a.Id, out var t)) { d = t.D; c = t.C; }
                var ledger = a.NormalBalance == NormalBalance.Debit ? d - c : c - d;
                return new AccountCheck(a.Code, a.NameAr,
                    Math.Round(a.CurrentBalance, 2), Math.Round(ledger, 2));
            })
            .ToList();
    }

    /// <summary>مجموع المدين/الدائن لكامل دفتر القيود (يجب أن يتساويا دائماً).</summary>
    public static async Task<(decimal Debit, decimal Credit)> TotalsAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .ToListAsync();
        return (Math.Round(lines.Sum(l => l.DebitAmount), 2), Math.Round(lines.Sum(l => l.CreditAmount), 2));
    }

    /// <summary>أرقام القيود غير المتوازنة (يجب أن تكون فارغة).</summary>
    public static async Task<List<string>> UnbalancedEntriesAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        var entries = await ctx.Set<JournalEntry>().AsNoTracking()
            .Where(e => !e.IsDeleted)
            .Include(e => e.Lines)
            .ToListAsync();

        return entries
            .Where(e => Math.Round(e.Lines.Sum(l => l.DebitAmount), 2) != Math.Round(e.Lines.Sum(l => l.CreditAmount), 2))
            .Select(e => $"{e.EntryNumber}: D={e.Lines.Sum(l => l.DebitAmount):N2} C={e.Lines.Sum(l => l.CreditAmount):N2}")
            .ToList();
    }
    /// <summary>عدد بنود القيود المسجّلة على حسابات رئيسية (Parent) — يجب أن يكون صفراً للحركات الجديدة.</summary>
    public static async Task<int> ParentPostingsAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        var parentIds = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.ParentAccountId != null && !a.IsDeleted)
            .Select(a => a.ParentAccountId!.Value)
            .Distinct()
            .ToListAsync();

        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .CountAsync(l => !l.IsDeleted && parentIds.Contains(l.AccountId));
    }

    /// <summary>عدد القيود بتواريخ غير معقولة (أقل من 1900 أو أكبر من 2100).</summary>
    public static async Task<int> AbsurdDateRowsAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();
        return await ctx.Set<JournalEntry>().AsNoTracking()
            .CountAsync(e => e.EntryDate < new DateTime(1900, 1, 1) || e.EntryDate > new DateTime(2100, 1, 1));
    }

    /// <summary>أرقام مستندات/قيود مكررة (يجب أن تكون فارغة).</summary>
    public static async Task<List<string>> DuplicateNumbersAsync(AccountingPgFixture pg)
    {
        await using var ctx = pg.NewContext();

        var duplicates = await ctx.Set<JournalEntry>().AsNoTracking()
            .Where(e => !e.IsDeleted)
            .GroupBy(e => e.EntryNumber)
            .Where(g => g.Count() > 1)
            .Select(g => "JE:" + g.Key + " x" + g.Count())
            .ToListAsync();

        var dupInvoices = await ctx.Set<SalesInvoice>().AsNoTracking()
            .Where(i => !i.IsDeleted)
            .GroupBy(i => i.InvoiceNumber)
            .Where(g => g.Count() > 1)
            .Select(g => "SI:" + g.Key + " x" + g.Count())
            .ToListAsync();

        duplicates.AddRange(dupInvoices);
        return duplicates;
    }

    /// <summary>
    /// تأكيد شامل: كل حساب رصيده المخزَّن == دفتر القيود، وكل القيود متوازنة، والمجموع متوازن.
    /// يعيد تقريراً نصياً بالحسابات لطبعه في سجل الاختبار.
    /// </summary>
    public static async Task<string> AssertConsistentAsync(AccountingPgFixture pg)
    {
        var checks = await AccountsAsync(pg);
        var bad = checks.Where(c => c.Stored != c.Ledger).ToList();
        var report = string.Join("\n", checks.Select(c =>
            $"{c.Code,-9} مخزَّن={c.Stored,12:N2}  دفتر={c.Ledger,12:N2} {(c.Stored == c.Ledger ? "OK" : "DIFF")}"));

        Assert.True(bad.Count == 0,
            "أرصدة غير متسقة مع دفتر القيود:\n" +
            string.Join("\n", bad.Select(b => $"{b.Code} {b.NameAr}: مخزَّن={b.Stored} دفتر={b.Ledger}")) +
            "\n-- التقرير الكامل --\n" + report);

        var (debit, credit) = await TotalsAsync(pg);
        Assert.Equal(debit, credit);

        var unbalanced = await UnbalancedEntriesAsync(pg);
        Assert.True(unbalanced.Count == 0, "قيود غير متوازنة: " + string.Join(" | ", unbalanced));

        var duplicates = await DuplicateNumbersAsync(pg);
        Assert.True(duplicates.Count == 0, "أرقام مكررة: " + string.Join(" | ", duplicates));

        return report;
    }

}
