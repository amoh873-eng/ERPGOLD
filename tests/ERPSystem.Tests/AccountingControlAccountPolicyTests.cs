using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// سياسة الحسابات الرقابية (2026-10-25) — منع انفصال الرصيد التفصيلي عن الأستاذ **بلا تعطيل
/// القيود المحاسبية المشروعة**:
///
///  1) القيد اليدوي **المُسند** إلى طرف على 1200/2200 يُزامن رصيد الطرف مع الرصيد الرقابي في
///     نفس وحدة العمل ⇒ لا فرق، ولا يُحتسب ضمن «الفرق المُفسَّر» (لأنه لا يُنتج فرقًا فعلًا).
///  2) القيد اليدوي **غير المُسند** يبقى مسموحًا (سلوك موثَّق قائم) لكنه **يُعرض صريحًا** في
///     تقرير المطابقة (سرد «غير المُنسَب» برقم القيد والمبلغ) — لا إخفاء ولا تسوية تلقائية.
///  3) الإسناد غير المطابق (عميل على 2200 · مورد على 1100 · طرفان في بند · طرف غير موجود)
///     يُرفض **قبل أي كتابة**.
///  4) الإسناد يُخزَّن على بند القيد (أثر تدقيقي دائم).
/// </summary>
public class AccountingControlAccountPolicyTests : IClassFixture<AccountingPgFixture>
{
    private const string Receivable = "1200";
    private const string Inventory = "1300";
    private const string Payable = "2200";
    private const string SalesTax = "2100";

    private readonly AccountingPgFixture _pg;

    public AccountingControlAccountPolicyTests(AccountingPgFixture pg) => _pg = pg;

    // ───────────────────────── أدوات ─────────────────────────

    private async Task<Guid> AccountIdAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
    }

    /// <summary>أثر الحساب من دفتر القيود (مدين − دائن) — مستقل عن اتجاه الطبيعة.</summary>
    private async Task<decimal> LedgerEffectAsync(string code)
    {
        var accountId = await AccountIdAsync(code);
        await using var ctx = _pg.NewContext();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && !l.IsDeleted
                        && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .Select(l => new { l.DebitAmount, l.CreditAmount })
            .ToListAsync();
        return Math.Round(lines.Sum(l => l.DebitAmount - l.CreditAmount), 2);
    }

    private async Task<decimal> CustomerBalanceAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == _pg.CustomerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<decimal> SupplierBalanceAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Supplier>().AsNoTracking()
            .Where(s => s.Id == _pg.SupplierId).Select(s => s.CurrentBalance).FirstAsync();
    }

    private async Task<SubLedgerReconciliationRowDto> RowAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var report = await new SubLedgerReconciliationService(ctx).GetReportAsync();
        return report.Rows.Single(r => r.AccountCode == code);
    }

    private async Task<SubLedgerReconciliationDto> ReportAsync()
    {
        await using var ctx = _pg.NewContext();
        return await new SubLedgerReconciliationService(ctx).GetReportAsync();
    }

    private async Task<int> EntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    private async Task<JournalEntryDto> CreateManualAsync(
        DateTime date, string reference, params JournalEntryLegInput[] legs)
    {
        await using var ctx = _pg.NewContext();
        return await new JournalEntryService(ctx)
            .CreateManualEntryAsync(date, reference, "فحص سياسة الرقابي", legs);
    }

    // ═══════════ 1) قيد يدوي مُسند إلى عميل: الرقابي = التفصيلي بلا أي فرق ═══════════

    [LivePgFact]
    public async Task Attributed_manual_entry_on_receivable_keeps_control_equal_to_detail()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var receivableId = await AccountIdAsync(Receivable);
        var taxId = await AccountIdAsync(SalesTax);

        var customerBefore = await CustomerBalanceAsync();
        var ledgerBefore = await LedgerEffectAsync(Receivable);
        var rowBefore = await RowAsync(Receivable);

        var entry = await CreateManualAsync(new DateTime(2080, 2, 10), "CTRL-ATTR-AR",
            new JournalEntryLegInput { AccountId = receivableId, DebitAmount = 250m, CustomerId = _pg.CustomerId },
            new JournalEntryLegInput { AccountId = taxId, CreditAmount = 250m });

        // (أ) الرصيد التفصيلي تحرّك مع الرصيد الرقابي بنفس المقدار
        Assert.Equal(customerBefore + 250m, await CustomerBalanceAsync());
        Assert.Equal(ledgerBefore + 250m, await LedgerEffectAsync(Receivable));

        // (ب) لا فرق رقابي، ولا «غير مُفسَّر» — والقيد المُسند لا يُحتسب تفسيرًا (لا فرق ليُفسَّر)
        var rowAfter = await RowAsync(Receivable);
        Assert.Equal(rowBefore.Difference, rowAfter.Difference);
        Assert.Equal(rowBefore.ExplainedAmount, rowAfter.ExplainedAmount);
        Assert.Equal(0m, rowAfter.UnexplainedAmount);

        // (ج) الإسناد محفوظ على بند القيد (أثر تدقيقي دائم)
        await using (var ctx = _pg.NewContext())
        {
            var line = await ctx.Set<JournalEntryLine>().AsNoTracking()
                .FirstAsync(l => l.JournalEntryId == entry.Id && l.AccountId == receivableId);
            Assert.Equal(_pg.CustomerId, line.CustomerId);
            Assert.Null(line.SupplierId);
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ═══════════ 2) قيد يدوي مُسند إلى مورد (طبيعة دائنة) ═══════════

    [LivePgFact]
    public async Task Attributed_manual_entry_on_payable_keeps_control_equal_to_detail()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var payableId = await AccountIdAsync(Payable);
        var inventoryId = await AccountIdAsync(Inventory);

        var supplierBefore = await SupplierBalanceAsync();
        var ledgerBefore = await LedgerEffectAsync(Payable);   // (مدين − دائن) ⇒ ينقص عند زيادة الالتزام
        var rowBefore = await RowAsync(Payable);

        var entry = await CreateManualAsync(new DateTime(2080, 2, 11), "CTRL-ATTR-AP",
            new JournalEntryLegInput { AccountId = inventoryId, DebitAmount = 120m },
            new JournalEntryLegInput { AccountId = payableId, CreditAmount = 120m, SupplierId = _pg.SupplierId });

        Assert.Equal(supplierBefore + 120m, await SupplierBalanceAsync());
        Assert.Equal(ledgerBefore - 120m, await LedgerEffectAsync(Payable));

        var rowAfter = await RowAsync(Payable);
        Assert.Equal(rowBefore.Difference, rowAfter.Difference);
        Assert.Equal(rowBefore.ExplainedAmount, rowAfter.ExplainedAmount);
        Assert.Equal(0m, rowAfter.UnexplainedAmount);

        await using (var ctx = _pg.NewContext())
        {
            var line = await ctx.Set<JournalEntryLine>().AsNoTracking()
                .FirstAsync(l => l.JournalEntryId == entry.Id && l.AccountId == payableId);
            Assert.Equal(_pg.SupplierId, line.SupplierId);
            Assert.Null(line.CustomerId);
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ═══════════ 3) قيد غير مُسند: مسموح + معروض صريحًا (لا إخفاء ولا تسوية) ═══════════

    [LivePgFact]
    public async Task Unattributed_manual_entry_is_allowed_and_listed_explicitly()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var receivableId = await AccountIdAsync(Receivable);
        var taxId = await AccountIdAsync(SalesTax);

        var rowBefore = await RowAsync(Receivable);
        var reportBefore = await ReportAsync();

        var entry = await CreateManualAsync(new DateTime(2080, 3, 5), "CTRL-UNATTR-AR",
            new JournalEntryLegInput { AccountId = receivableId, DebitAmount = 40m },
            new JournalEntryLegInput { AccountId = taxId, CreditAmount = 40m });

        var rowAfter = await RowAsync(Receivable);
        var reportAfter = await ReportAsync();

        // (أ) القيد مسموح (سلوك موثَّق قائم) ⇒ الفرق يزيد 40 والمُفسَّر يزيد 40 وغير المُفسَّر يبقى صفرًا
        Assert.Equal(rowBefore.Difference + 40m, rowAfter.Difference);
        Assert.Equal(rowBefore.ExplainedAmount + 40m, rowAfter.ExplainedAmount);
        Assert.Equal(0m, rowAfter.UnexplainedAmount);
        Assert.Contains("فرق مُفسَّر", rowAfter.StatusAr);

        // (ب) الحركة مُدرجة بالاسم في السرد الصريح (رقم القيد + المبلغ + نوعه)
        var listed = reportAfter.UnattributedControlLines
            .Single(l => l.EntryNumber == entry.EntryNumber && l.AccountCode == Receivable);
        Assert.Equal(40m, listed.SignedAmount);
        Assert.Equal("قيد يدوي", listed.EntryTypeNameAr);
        Assert.Equal(new DateTime(2080, 3, 5), listed.EntryDate);
        Assert.Equal("CTRL-UNATTR-AR", listed.Reference);

        // (ج) المؤشر الإجمالي غير المُنسَب يزيد بنفس المقدار (شفافية كاملة)
        Assert.Equal(
            Math.Round(reportBefore.TotalUnattributedAmount + 40m, 2),
            Math.Round(reportAfter.TotalUnattributedAmount, 2));

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ═══════════ 4) الإسناد غير المطابق يُرفض قبل أي كتابة ═══════════

    [LivePgFact]
    public async Task Mismatched_attribution_is_rejected_before_any_write()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var receivableId = await AccountIdAsync(Receivable);
        var payableId = await AccountIdAsync(Payable);
        var taxId = await AccountIdAsync(SalesTax);

        var entriesBefore = await EntryCountAsync();

        async Task<string> RejectAsync(DateTime date, string reference, params JournalEntryLegInput[] legs)
        {
            await using var ctx = _pg.NewContext();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new JournalEntryService(ctx).CreateManualEntryAsync(date, reference, "فحص رفض الإسناد", legs));
            return ex.Message;
        }

        // (1) عميل على حساب الموردين
        Assert.Contains("إسناد العميل مقبول على حساب العملاء",
            await RejectAsync(new DateTime(2080, 4, 1), "CTRL-BAD-1",
                new JournalEntryLegInput { AccountId = payableId, DebitAmount = 10m, CustomerId = _pg.CustomerId },
                new JournalEntryLegInput { AccountId = taxId, CreditAmount = 10m }));

        // (2) مورد على حساب غير الموردين
        Assert.Contains("إسناد المورد مقبول على حساب الموردين",
            await RejectAsync(new DateTime(2080, 4, 2), "CTRL-BAD-2",
                new JournalEntryLegInput { AccountId = receivableId, DebitAmount = 10m, SupplierId = _pg.SupplierId },
                new JournalEntryLegInput { AccountId = taxId, CreditAmount = 10m }));

        // (3) طرفان في بند واحد
        Assert.Contains("عميل ومورد معًا",
            await RejectAsync(new DateTime(2080, 4, 3), "CTRL-BAD-3",
                new JournalEntryLegInput
                {
                    AccountId = receivableId, DebitAmount = 10m,
                    CustomerId = _pg.CustomerId, SupplierId = _pg.SupplierId
                },
                new JournalEntryLegInput { AccountId = taxId, CreditAmount = 10m }));

        // (4) عميل غير موجود
        Assert.Contains("غير موجود",
            await RejectAsync(new DateTime(2080, 4, 4), "CTRL-BAD-4",
                new JournalEntryLegInput { AccountId = receivableId, DebitAmount = 10m, CustomerId = Guid.NewGuid() },
                new JournalEntryLegInput { AccountId = taxId, CreditAmount = 10m }));

        // ولا قيد واحد أُضيف من كل المحاولات المرفوضة
        Assert.Equal(entriesBefore, await EntryCountAsync());
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
