using ERPSystem.Application.DTOs.BankChecks;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة 5 — سلسلة الشيكات الكاملة (2026-10-25):
/// فاتورة بيع مسدَّدة بشيك ⇒ سجل الشيك وقيد «برسم التحصيل» 1102 ⇒ إيداع (بلا أثر) ⇒
/// تحصيل (1102 ← 1101) ⇒ شيك ثانٍ يرتد (عودة الدين على العميل) ⇒ التقارير والأستاذ والمطابقة.
///
/// القيم المتوقعة لكل مرحلة معروفة مسبقًا، وكل تحقق يُقرأ من قاعدة البيانات لا من كائنات الخدمة.
/// </summary>
public class AccountingChequeSettlementChainTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public AccountingChequeSettlementChainTests(AccountingPgFixture pg) => _pg = pg;

    private const string EvidenceFile = @"D:\erpfix-pu01\_p6_cheque_chain.txt";

    private static string Money(decimal v)
        => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<decimal> LedgerNetAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();

        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && !l.IsDeleted
                        && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }

    private async Task<decimal> CustomerBalanceAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking().Where(c => c.Id == _pg.CustomerId)
            .Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<int> EntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    private async Task WriteEvidenceAsync(string line)
    {
        try { await File.AppendAllTextAsync(EvidenceFile, line + Environment.NewLine); }
        catch { /* ملف الدليل اختياري */ }
    }

    private async Task<Guid> NewItemWithStockAsync(string suffix, decimal quantity, decimal unitCost,
        decimal salePrice)
    {
        await using var ctx = _pg.NewContext();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "CHK-" + suffix,
            NameAr = "صنف شيكات " + suffix,
            CategoryId = _pg.CategoryId,
            UnitId = _pg.UnitId,
            SalePrice = salePrice,
            CostPrice = unitCost,
            TaxRate = 0m,
            TracksBatches = false,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        await new StockMovementService(ctx, new AccountingPgFixture.AllowAllManualPrice())
            .CreateAsync(new CreateStockMovementDto
            {
                ItemId = item.Id,
                WarehouseId = _pg.WarehouseId,
                MovementType = (int)MovementType.OpeningBalance,
                Quantity = quantity,
                UnitCost = unitCost,
                MovementDate = DateTime.Today,
                ReferenceNumber = "CHK-OPEN-" + suffix
            });

        return item.Id;
    }

    [LivePgFact]
    public async Task Cheque_paid_invoices_flow_to_checks_bank_and_back_on_bounce_with_matching_reports()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var itemId = await NewItemWithStockAsync(suffix, 50m, 10m, 20m);
        var due = DateTime.Today.AddDays(30);

        var checks1102Before = await LedgerNetAsync("1102");
        var bank1101Before = await LedgerNetAsync("1101");
        var ar1200Before = await LedgerNetAsync("1200");
        var inventory1300Before = await LedgerNetAsync("1300");
        var cogs5100Before = await LedgerNetAsync("5100");

        // ── (1) فاتورة 5 × 20 = 100 مسدَّدة بشيك: قيد بيع (1200) + تكلفة + قيد تسجيل الشيك (1102/1200)
        Guid invoiceA;
        await using (var ctx = _pg.NewContext())
        {
            var created = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Check,
                TaxRate = 0m,
                CheckNumber = "CH-A-" + suffix,
                CheckBankName = "بنك اختبار",
                CheckBranch = "الفرع الرئيسي",
                CheckIssueDate = DateTime.Today,
                CheckDueDate = due,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = 5m, UnitPrice = 20m }
                }
            });
            invoiceA = created.Id;
        }

        Guid checkA;
        await using (var ctx = _pg.NewContext())
        {
            var inv = await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => i.Id == invoiceA);
            Assert.Equal(100m, inv.TotalAmount);
            Assert.Equal(100m, inv.PaidAmount);
            Assert.NotNull(inv.SalesJournalEntryId);
            Assert.NotNull(inv.CogsJournalEntryId);

            var check = await ctx.Set<BankCheck>().AsNoTracking()
                .FirstAsync(c => c.RelatedInvoiceId == invoiceA);
            checkA = check.Id;
            Assert.Equal(100m, check.Amount);
            Assert.Equal(BankCheckStatus.Registered, check.Status);
            Assert.Equal(_pg.CustomerId, check.CustomerId);
        }

        Assert.Equal(checks1102Before + 100m, await LedgerNetAsync("1102"));   // أصل «برسم التحصيل»
        Assert.Equal(ar1200Before, await LedgerNetAsync("1200"));              // الشيك سوّى الذمة فورًا
        Assert.Equal(0m, await CustomerBalanceAsync());
        Assert.Equal(inventory1300Before - 50m, await LedgerNetAsync("1300"));
        Assert.Equal(cogs5100Before + 50m, await LedgerNetAsync("5100"));

        // ── (2) الإيداع: مرحلة وسيطة بلا أي أثر محاسبي
        var entriesBeforeDeposit = await EntryCountAsync();
        await using (var ctx = _pg.NewContext())
        {
            var deposited = await new BankCheckService(ctx, new JournalEntryService(ctx))
                .DepositAsync(checkA, DateTime.Today);
            Assert.Equal((int)BankCheckStatus.Deposited, deposited.Status);
        }
        Assert.Equal(entriesBeforeDeposit, await EntryCountAsync());
        Assert.Equal(checks1102Before + 100m, await LedgerNetAsync("1102"));

        // ── (3) التحصيل: 1102 ← 1101 (نقد في البنك)
        await using (var ctx = _pg.NewContext())
        {
            var cleared = await new BankCheckService(ctx, new JournalEntryService(ctx))
                .ClearAsync(checkA, DateTime.Today);
            Assert.Equal((int)BankCheckStatus.Cleared, cleared.Status);
            Assert.Equal(100m, cleared.Amount);
        }
        Assert.Equal(checks1102Before, await LedgerNetAsync("1102"));
        Assert.Equal(bank1101Before + 100m, await LedgerNetAsync("1101"));

        // تحصيل مكرر ⇒ مرفوض بلا قيد إضافي
        var entriesBeforeDup = await EntryCountAsync();
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new BankCheckService(ctx, new JournalEntryService(ctx)).ClearAsync(checkA, DateTime.Today));
        }
        Assert.Equal(entriesBeforeDup, await EntryCountAsync());

        // ── (4) فاتورة ثانية 2 × 20 = 40 بشيك ثم **ارتداد** الشيك ⇒ عودة الدين على العميل
        Guid invoiceB;
        await using (var ctx = _pg.NewContext())
        {
            var created = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Check,
                TaxRate = 0m,
                CheckNumber = "CH-B-" + suffix,
                CheckBankName = "بنك اختبار",
                CheckIssueDate = DateTime.Today,
                CheckDueDate = due,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = 2m, UnitPrice = 20m }
                }
            });
            invoiceB = created.Id;
        }

        Guid checkB;
        await using (var ctx = _pg.NewContext())
        {
            checkB = (await ctx.Set<BankCheck>().AsNoTracking()
                .FirstAsync(c => c.RelatedInvoiceId == invoiceB)).Id;
        }
        Assert.Equal(checks1102Before + 40m, await LedgerNetAsync("1102"));

        await using (var ctx = _pg.NewContext())
        {
            var bounced = await new BankCheckService(ctx, new JournalEntryService(ctx))
                .BounceAsync(checkB, DateTime.Today);
            Assert.Equal((int)BankCheckStatus.Bounced, bounced.Status);
        }
        Assert.Equal(checks1102Before, await LedgerNetAsync("1102"));          // الشيك خرج من الأصول
        Assert.Equal(ar1200Before + 40m, await LedgerNetAsync("1200"));        // الدين عاد للذمم
        Assert.Equal(40m, await CustomerBalanceAsync());                       // الرصيد التشغيلي عاد مرة واحدة
        Assert.Equal(inventory1300Before - 70m, await LedgerNetAsync("1300")); // تكلفة الفاتورتين
        Assert.Equal(cogs5100Before + 70m, await LedgerNetAsync("5100"));

        // ── (5) التقارير: قائمة الشيكات وتقرير الشيكات مقابل الأستاذ
        await using (var ctx = _pg.NewContext())
        {
            var svc = new BankCheckService(ctx, new JournalEntryService(ctx));

            var all = await svc.ListAsync();
            var mineA = all.Single(c => c.Id == checkA);
            var mineB = all.Single(c => c.Id == checkB);
            Assert.Equal((int)BankCheckStatus.Cleared, mineA.Status);
            Assert.Equal((int)BankCheckStatus.Bounced, mineB.Status);
            Assert.Equal(100m, mineA.Amount);
            Assert.Equal(40m, mineB.Amount);
            Assert.False(string.IsNullOrWhiteSpace(mineA.EntryNumber));

            var received = await svc.ListAsync(BankCheckDirection.ReceivedFromCustomer);
            var expectedIncoming = received.Sum(r => r.Amount);
            var report = await svc.ReportAsync(null, null, BankCheckDirection.ReceivedFromCustomer, null);
            Assert.Equal(expectedIncoming, report.IncomingTotal);
            Assert.Equal(received.Count, report.Rows.Count);
            Assert.Contains(report.Rows, r => r.Id == checkA);
            Assert.Contains(report.Rows, r => r.Id == checkB);

            // ميزان المراجعة: 1102 صفر بعد التحصيل والارتداد، والبنك +100
            var trial = await new ReportService(ctx).GetTrialBalanceAsync();
            Assert.True(trial.IsBalanced,
                $"ميزان المراجعة غير متوازن: مدين={trial.TotalDebit:N2} دائن={trial.TotalCredit:N2}");
            Assert.Equal(0m, Math.Round(trial.Lines.First(l => l.AccountCode == "1102").Balance, 2));
            Assert.Equal(bank1101Before + 100m,
                Math.Round(trial.Lines.First(l => l.AccountCode == "1101").Balance, 2));

            // مطابقة الرقابي: حركات الشيك على 1200 يجب أن تكون **مُسندة** للعميل بلا فرق غير مُفسَّر
            var recon = await new SubLedgerReconciliationService(ctx).GetReportAsync();
            var row1200 = recon.Rows.Single(r => r.AccountCode == "1200");
            Assert.True(Math.Abs(row1200.Difference) < 0.005m,
                $"1200: فرق رقابي/تفصيلي {Money(row1200.Difference)}");
            Assert.True(Math.Abs(row1200.UnexplainedAmount) < 0.005m,
                $"1200: فرق غير مُفسَّر {Money(row1200.UnexplainedAmount)}");
            Assert.True(recon.UnattributedControlLines.Count == 0,
                "بنود رقابية غير مُسندة (حركات شيك بلا إسناد طرف):\n" +
                string.Join("\n", recon.UnattributedControlLines.Select(
                    l => $"{l.AccountCode} {l.EntryNumber} {l.EntryTypeNameAr} {l.Reference} {Money(l.SignedAmount)}")));
            Assert.Equal(0m, Math.Round(recon.TotalUnattributedAmount, 2));

            // قياس إعلامي (لا حكم سياسة): حالة أعمار الذمم بعد ارتداد شيك فاتورة
            var aging = await AgingReportQueries.GetAgingAsync(ctx, isReceivable: true, asOf: DateTime.Today);
            var agingOurs = aging.Rows.Where(r => r.PartyId == _pg.CustomerId)
                .Sum(r => r.Total + r.CreditBalance);
            var invB = await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => i.Id == invoiceB);

            await WriteEvidenceAsync(
                "ارتداد شيك فاتورة | " +
                $"1200={Money(await LedgerNetAsync("1200"))} · رصيد العميل={Money(await CustomerBalanceAsync())} · " +
                $"أعمار ذمة العميل={Money(agingOurs)} · فاتورة الشيك: Total={Money(invB.TotalAmount)} " +
                $"Paid={Money(invB.PaidAmount)} (السياسة مفتوحة: هل يُعاد فتح الفاتورة عند الارتداد؟)");
        }

        // ثابت: بعد الارتداد الرصيد التشغيلي للعميل = الرقابي 1200 (الدين عاد مرة واحدة بلا ازدواج)
        Assert.Equal(await LedgerNetAsync("1200"), await CustomerBalanceAsync());

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
