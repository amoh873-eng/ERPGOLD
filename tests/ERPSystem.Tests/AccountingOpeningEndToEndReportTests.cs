using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// الرصيد الافتتاحي من الإدخال حتى التقارير (2026-10-25) — مع إثبات **عدم ازدواج الأثر**:
///
///  • إدخال دفعة افتتاحية (عميل + صنف مخزني + مورد) ⇒ قيد واحد فقط، وترحيل ثانٍ مرفوض
///    (لا قيد ثانٍ ولا رصيد مضاعف).
///  • الظهور في **ميزان المراجعة** (توازن + أرصدة الأطراف) و**شجرة الأرصدة** (قيم الحسابات).
///  • **المطابقة الرقابية**: فرق صفر على 1200/2200/1300 بعد الدفعة، و**لا زيادة في «غير المُنسَب»**
///    (الدفعة مُنسَبة إلى أطرافها ⇒ لا تُحتسب غير مُنسَبة).
///  • العكس يعيد كل شيء (قيدان إجمالًا، والأرصدة والتقارير تعود إلى ما قبل الدفعة).
/// </summary>
public class AccountingOpeningEndToEndReportTests : IClassFixture<AccountingPgFixture>
{
    private const int Year = 2087;
    private const string Receivable = "1200";
    private const string Inventory = "1300";
    private const string Payable = "2200";
    private const string Equity = "2100";

    private readonly AccountingPgFixture _pg;

    public AccountingOpeningEndToEndReportTests(AccountingPgFixture pg) => _pg = pg;

    private async Task<Guid> NewItemAsync(string suffix, decimal costPrice)
    {
        await using var ctx = _pg.NewContext();
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "E2E-" + suffix,
            NameAr = "صنف e2e " + suffix,
            CategoryId = _pg.CategoryId,
            UnitId = _pg.UnitId,
            SalePrice = costPrice * 2m,
            CostPrice = costPrice,
            TaxRate = 0m,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private async Task<(decimal TotalDebit, decimal TotalCredit)> TrialTotalsAsync()
    {
        await using var ctx = _pg.NewContext();
        var tb = await new ReportService(ctx).GetTrialBalanceAsync();
        return (Math.Round(tb.TotalDebit, 2), Math.Round(tb.TotalCredit, 2));
    }

    // ملاحظة: تقرير الميزان/الشجرة يُظهران الحسابات ذات الحركة، فالقراءة تتعامل مع غياب السطر = صفر
    // (وهو الحال قبل ترحيل الدفعة) بلا افتراض خطأ.
    private async Task<decimal> TrialDebitAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var tb = await new ReportService(ctx).GetTrialBalanceAsync();
        return Math.Round(tb.Lines.Where(l => l.AccountCode == code).Select(l => l.TotalDebit).FirstOrDefault(), 2);
    }

    private async Task<decimal> TrialCreditAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var tb = await new ReportService(ctx).GetTrialBalanceAsync();
        return Math.Round(tb.Lines.Where(l => l.AccountCode == code).Select(l => l.TotalCredit).FirstOrDefault(), 2);
    }

    private async Task<decimal> TreeNetAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync(new AccountBalanceQuery());
        return Math.Round(tree.Flattened.Where(n => n.Code == code).Select(n => n.NetBalance).FirstOrDefault(), 2);
    }

    private async Task<SubLedgerReconciliationDto> ReconciliationAsync()
    {
        await using var ctx = _pg.NewContext();
        return await new SubLedgerReconciliationService(ctx).GetReportAsync();
    }

    private async Task<int> EntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
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

    private async Task<decimal> ItemStockAsync(Guid itemId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Item>().AsNoTracking()
            .Where(i => i.Id == itemId).Select(i => i.CurrentStock).FirstAsync();
    }

    /// <summary>
    /// انحدار لإصلاح 2026-10-25: بنود **قيد العكس** تحمل إسناد الطرف نفسه ⇒ لا حركة «غير مُنسَبة»
    /// جديدة ولا «فرق غير مُفسَّر» وهمي بعد عكس دفعة افتتاحية مُسندة إلى طرف.
    /// </summary>
    [LivePgFact]
    public async Task Opening_reversal_carries_party_attribution_and_keeps_reconciliation_clean()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await NewItemAsync("D", 10m);

        int inventoryLinesBefore;
        decimal inventoryDebitBefore, inventoryCreditBefore;
        await using (var snap = _pg.NewContext())
        {
            var inventoryId = await snap.Set<Account>().AsNoTracking()
                .Where(a => a.Code == Inventory).Select(a => a.Id).FirstAsync();
            var rows = await snap.Set<JournalEntryLine>().AsNoTracking()
                .Where(l => l.AccountId == inventoryId && !l.IsDeleted)
                .Select(l => new { l.DebitAmount, l.CreditAmount })
                .ToListAsync();
            inventoryLinesBefore = rows.Count;
            inventoryDebitBefore = rows.Sum(r => r.DebitAmount);
            inventoryCreditBefore = rows.Sum(r => r.CreditAmount);
        }

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(new CreateOpeningBalanceBatchDto
            {
                FiscalYear = 2088,
                EntryDate = new DateTime(2088, 1, 10),
                Reference = "DIAG-OPEN",
                Lines = new List<OpeningBalanceLineInput>
                {
                    new()
                    {
                        AccountCode = Inventory,
                        ItemCode = "E2E-D",
                        WarehouseCode = AccountingPgFixture.WarehouseCode,
                        Quantity = 40m,
                        UnitCost = 10m
                    },
                    new()
                    {
                        AccountCode = Receivable,
                        DebitAmount = 100m,
                        CustomerCode = AccountingPgFixture.CustomerCode
                    },
                    new() { AccountCode = Equity, CreditAmount = 500m }
                }
            }, "tester");
            batchId = (await svc.PostAsync(draft.Id, "tester")).Id;
        }

        Assert.Equal(400m, await TreeNetAsync(Inventory));
        Assert.Equal(100m, await TreeNetAsync(Receivable));

        var unattributedBefore = (await ReconciliationAsync()).TotalUnattributedAmount;

        await using (var ctx = _pg.NewContext())
        {
            await AccountingPgFixture.NewOpeningBalanceService(ctx)
                .ReverseAsync(batchId, new DateTime(2088, 2, 10), "tester");
        }

        // بعد العكس: الصافي صفر، وقيدان على 1300، وبنود العكس تحمل **إسناد الطرف** نفسه
        // (إصلاح 2026-10-25) ⇒ لا حركة «غير مُنسَبة» جديدة ولا «فرق غير مُفسَّر» وهمي.
        Assert.Equal(0m, await TreeNetAsync(Inventory));
        Assert.Equal(0m, await TreeNetAsync(Receivable));
        Assert.Equal(0m, await TreeNetAsync(Equity));

        await using (var ctx = _pg.NewContext())
        {
            var inventoryId = await ctx.Set<Account>().AsNoTracking()
                .Where(a => a.Code == Inventory).Select(a => a.Id).FirstAsync();
            var receivableId = await ctx.Set<Account>().AsNoTracking()
                .Where(a => a.Code == Receivable).Select(a => a.Id).FirstAsync();
            var equityId = await ctx.Set<Account>().AsNoTracking()
                .Where(a => a.Code == Equity).Select(a => a.Id).FirstAsync();
            var reversalBatch = await ctx.Set<OpeningBalanceBatch>().AsNoTracking()
                .FirstAsync(b => b.ReversalOfBatchId == batchId);

            // بند العميل في القيد العكسي يحمل الإسناد نفسه (جوهر الإصلاح)
            var reversalCustomerLine = await ctx.Set<JournalEntryLine>().AsNoTracking()
                .FirstAsync(l => l.JournalEntryId == reversalBatch.JournalEntryId && l.AccountId == receivableId);
            Assert.Equal(_pg.CustomerId, reversalCustomerLine.CustomerId);
            Assert.Equal(100m, reversalCustomerLine.CreditAmount);

            // بند الحساب العادي (2100) بلا إسناد في القيدين (لا إسناد في غير موضعه)
            var equityLines = await ctx.Set<JournalEntryLine>().AsNoTracking()
                .Where(l => l.AccountId == equityId).ToListAsync();
            Assert.All(equityLines, l => Assert.True(l.CustomerId is null && l.SupplierId is null));

            // أعداد البنود تُقاس **تفاضليًا**: نفس الفئة تشترك في قاعدة واحدة، وفحوص أخرى تُحرّك 1300.
            var inventoryLines = await ctx.Set<JournalEntryLine>().AsNoTracking()
                .Where(l => l.AccountId == inventoryId && !l.IsDeleted).ToListAsync();
            Assert.Equal(inventoryLinesBefore + 2, inventoryLines.Count);        // افتتاحي + عكسي
            Assert.Equal(400m, inventoryLines.Sum(l => l.DebitAmount) - inventoryDebitBefore);
            Assert.Equal(400m, inventoryLines.Sum(l => l.CreditAmount) - inventoryCreditBefore);

            // رصيد العميل عاد إلى ما قبل الدفعة (المزامنة تناظرية في الاتجاهين)
            var customer = await ctx.Set<Customer>().AsNoTracking().FirstAsync(c => c.Id == _pg.CustomerId);
            Assert.Equal(0m, customer.CurrentBalance);
        }

        var unattributedAfter = (await ReconciliationAsync()).TotalUnattributedAmount;
        Assert.Equal(unattributedBefore, unattributedAfter);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    [LivePgFact]
    public async Task Opening_batch_flows_to_reports_without_double_effect_and_reversal_restores()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await NewItemAsync("A", 10m);

        // لقطات قبل الدفعة
        var entriesBefore = await EntryCountAsync();
        var (debitBefore, creditBefore) = await TrialTotalsAsync();
        var receivableDebitBefore = await TrialDebitAsync(Receivable);
        var payableCreditBefore = await TrialCreditAsync(Payable);
        var equityCreditBefore = await TrialCreditAsync(Equity);
        var customerBefore = await CustomerBalanceAsync();
        var supplierBefore = await SupplierBalanceAsync();
        var reconciliationBefore = await ReconciliationAsync();

        // (1) الدفعة: Dr 1200 800 · Dr 1300 (40 × 10 = 400) · Cr 2200 500 · Cr 2100 700
        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(new CreateOpeningBalanceBatchDto
            {
                FiscalYear = Year,
                EntryDate = new DateTime(Year, 1, 10),
                Reference = "E2E-OPEN",
                Lines = new List<OpeningBalanceLineInput>
                {
                    new()
                    {
                        AccountCode = Receivable,
                        DebitAmount = 800m,
                        CustomerCode = AccountingPgFixture.CustomerCode
                    },
                    new()
                    {
                        AccountCode = Inventory,
                        ItemCode = "E2E-A",
                        WarehouseCode = AccountingPgFixture.WarehouseCode,
                        Quantity = 40m,
                        UnitCost = 10m
                    },
                    new()
                    {
                        AccountCode = Payable,
                        CreditAmount = 500m,
                        SupplierCode = AccountingPgFixture.SupplierCode
                    },
                    new() { AccountCode = Equity, CreditAmount = 700m }
                }
            }, "tester");

            var posted = await svc.PostAsync(draft.Id, "tester");
            batchId = posted.Id;
            Assert.NotNull(posted.EntryNumber);
            Assert.Equal(4, posted.Lines.Count);
        }

        // (2) قيد واحد فقط (لا ازدواج) + الأرصدة التفصيلية تحرّكت مرة واحدة
        Assert.Equal(entriesBefore + 1, await EntryCountAsync());
        Assert.Equal(customerBefore + 800m, await CustomerBalanceAsync());
        Assert.Equal(supplierBefore + 500m, await SupplierBalanceAsync());
        Assert.Equal(40m, await ItemStockAsync(itemId));

        // (3) ميزان المراجعة: التوازن + ظهور المبالغ الافتتاحية في الحسابات الصحيحة
        var (debitAfter, creditAfter) = await TrialTotalsAsync();
        Assert.Equal(debitAfter, creditAfter);
        Assert.Equal(debitBefore + 1200m, debitAfter);
        Assert.Equal(creditBefore + 1200m, creditAfter);
        Assert.Equal(receivableDebitBefore + 800m, await TrialDebitAsync(Receivable));
        Assert.Equal(payableCreditBefore + 500m, await TrialCreditAsync(Payable));
        Assert.Equal(equityCreditBefore + 700m, await TrialCreditAsync(Equity));

        // (4) شجرة الأرصدة: قيم الحسابات تعكس الافتتاحي
        Assert.Equal(800m, await TreeNetAsync(Receivable));
        Assert.Equal(-500m, await TreeNetAsync(Payable));   // دائن ⇒ صافي سالب بعُرف الشجرة
        Assert.Equal(400m, await TreeNetAsync(Inventory));

        // (5) المطابقة: فروق صفر، والدفعة **لا** تُحتسب غير مُنسَبة (مُسندة لأطرافها)
        var reconciliationAfter = await ReconciliationAsync();
        Assert.Equal(0m, reconciliationAfter.Rows.Single(r => r.AccountCode == Receivable).Difference);
        Assert.Equal(0m, reconciliationAfter.Rows.Single(r => r.AccountCode == Payable).Difference);
        Assert.Equal(0m, reconciliationAfter.Rows.Single(r => r.AccountCode == Inventory).Difference);
        Assert.Equal(reconciliationBefore.TotalUnattributedAmount, reconciliationAfter.TotalUnattributedAmount);

        // (6) منع ازدواج الأثر: إعادة ترحيل الدفعة نفسها مرفوضة بلا أي تغيير
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx).PostAsync(batchId, "tester"));
        }
        Assert.Equal(entriesBefore + 1, await EntryCountAsync());
        Assert.Equal(customerBefore + 800m, await CustomerBalanceAsync());
        Assert.Equal(40m, await ItemStockAsync(itemId));

        // (7) العكس: قيدان إجمالًا، وكل شيء يعود لما قبل الدفعة (بما في ذلك التقارير)
        await using (var ctx = _pg.NewContext())
        {
            var reversal = await AccountingPgFixture.NewOpeningBalanceService(ctx)
                .ReverseAsync(batchId, new DateTime(Year, 2, 10), "tester");
            Assert.NotNull(reversal.JournalEntryId);
        }

        Assert.Equal(entriesBefore + 2, await EntryCountAsync());
        Assert.Equal(customerBefore, await CustomerBalanceAsync());
        Assert.Equal(supplierBefore, await SupplierBalanceAsync());
        Assert.Equal(0m, await ItemStockAsync(itemId));

        // ميزان المراجعة هنا يعرض **إجمالي حركة الفترة** (لا الصافي): بعد العكس يظل الإجمالي
        // متضمّنًا القيدين (1200 افتتاحي + 1200 عكسي = 2400)، وهذا سلوك التقرير المتوقّع.
        var (debitFinal, creditFinal) = await TrialTotalsAsync();
        Assert.Equal(debitFinal, creditFinal);
        Assert.Equal(debitBefore + 2400m, debitFinal);
        // إجمالي مدين 1200 = 800 (الدين من القيد الافتتاحي فقط، والعكس دائن له) · إجمالي دائن 2200 = 500
        Assert.Equal(receivableDebitBefore + 800m, await TrialDebitAsync(Receivable));
        Assert.Equal(payableCreditBefore + 500m, await TrialCreditAsync(Payable));

        // والصافي يعود إلى الصفر في شجرة الأرصدة (وهو المرجع الصافي الصحيح)
        Assert.Equal(0m, await TreeNetAsync(Receivable));
        Assert.Equal(0m, await TreeNetAsync(Payable));
        Assert.Equal(0m, await TreeNetAsync(Inventory));

        var reconciliationFinal = await ReconciliationAsync();
        var rowR = reconciliationFinal.Rows.Single(r => r.AccountCode == Receivable);
        var rowP = reconciliationFinal.Rows.Single(r => r.AccountCode == Payable);
        var rowI = reconciliationFinal.Rows.Single(r => r.AccountCode == Inventory);

        Assert.True(rowR.Difference == 0m && rowP.Difference == 0m && rowI.Difference == 0m,
            $"بعد العكس: 1200={rowR.Difference} 2200={rowP.Difference} 1300={rowI.Difference}");

        // إصلاح 2026-10-25: بنود العكس تحمل إسناد الطرف نفسه ⇒ لا «فرق غير مُفسَّر» ولا حركة «غير مُنسَبة».
        Assert.Equal(0m, rowR.UnexplainedAmount);
        Assert.Equal(0m, rowP.UnexplainedAmount);
        Assert.Equal(0m, rowI.UnexplainedAmount);
        Assert.Equal(reconciliationBefore.TotalUnattributedAmount, reconciliationFinal.TotalUnattributedAmount);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
