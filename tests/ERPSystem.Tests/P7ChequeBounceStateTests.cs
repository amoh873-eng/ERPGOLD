using ERPSystem.Application.DTOs.BankChecks;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P7/G2 — انحدار: ارتداد شيك فاتورة يجب أن يُعيد **حالة دفع المستند**، لا أن يكتفي بعكس القيد.
///
/// العيب قبل الإصلاح (مُقاس في `_p7_evidence.txt`): بعد الارتداد كان
///   `invoice.PaidAmount = 40` (الفاتورة «مدفوعة») بينما `1200 = 40` ورصيد العميل = 40
///   و**تقرير أعمار الذمم = 0.00** ⇒ ذمة غير مرئية للمحاسب.
///
/// القاعدة المعتمدة المُطبَّقة: «لا يُخصَّص شيك مرتجع (Bounced)» (P0-2K §239 / P0-2L §D3)
///   ⇒ الشيك المرتجع ليس تسويةً، فتُخفَّض حالة الدفع على مستوى الفاتورة بمقداره.
/// </summary>
public class P7ChequeBounceStateTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P7ChequeBounceStateTests(AccountingPgFixture pg) => _pg = pg;

    private async Task<Guid> GlAccountIdAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
    }

    private async Task<decimal> GlNetAsync(string code)
    {
        var accountId = await GlAccountIdAsync(code);
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && !l.IsDeleted
                        && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }

    private async Task<decimal> CustomerBalanceAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == _pg.CustomerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<decimal> InvoicePaidAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        return (await ctx.Set<SalesInvoice>().AsNoTracking()
            .FirstAsync(i => i.Id == invoiceId)).PaidAmount;
    }

    private async Task<decimal> InvoiceResidualAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        var residuals = await new CustomerAllocationQueries(ctx).GetComputedResidualsAsync(DateTime.Today);
        return residuals.Where(r => r.InvoiceId == invoiceId)
            .Select(r => r.ComputedResidual).DefaultIfEmpty(0m).Sum();
    }

    /// <summary>ينشئ فاتورة مسدَّدة بشيك ويعيد (معرّف الفاتورة، معرّف الشيك).</summary>
    private async Task<(Guid InvoiceId, Guid CheckId)> CreateChequePaidInvoiceAsync(
        Guid itemId, decimal quantity, decimal unitPrice, string checkNumber)
    {
        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            invoiceId = (await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Check,
                TaxRate = 0m,
                CheckNumber = checkNumber,
                CheckBankName = "بنك اختبار",
                CheckIssueDate = DateTime.Today,
                CheckDueDate = DateTime.Today.AddDays(30),
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = quantity, UnitPrice = unitPrice }
                }
            })).Id;
        }

        await using (var ctx = _pg.NewContext())
            return (invoiceId, (await ctx.Set<BankCheck>().AsNoTracking()
                .FirstAsync(c => c.RelatedInvoiceId == invoiceId)).Id);
    }

    [LivePgFact]
    public async Task Bounced_cheque_reopens_the_invoice_and_makes_the_debt_visible_in_aging_once()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var itemId = await _pg.CreateItemAsync("P7B-" + suffix, salePrice: 20m, costPrice: 10m, openingStock: 50m);
        var (invoiceId, checkId) = await CreateChequePaidInvoiceAsync(itemId, 2m, 20m, "P7B-CHK-" + suffix);

        // قبل الارتداد: الشيك تسوية ⇒ لا دين ظاهر
        Assert.Equal(40m, await InvoicePaidAsync(invoiceId));
        Assert.Equal(0m, await InvoiceResidualAsync(invoiceId));

        // الارتداد
        await using (var ctx = _pg.NewContext())
            await new BankCheckService(ctx, new JournalEntryService(ctx)).BounceAsync(checkId, DateTime.Today);

        var gl1200 = await GlNetAsync("1200");
        var entriesBeforeRepeat = 0;
        decimal agingParty;
        await using (var ctx = _pg.NewContext())
        {
            entriesBeforeRepeat = await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
            var aging = await AgingReportQueries.GetAgingAsync(ctx, isReceivable: true, asOf: DateTime.Today);
            agingParty = aging.Rows.Where(r => r.PartyId == _pg.CustomerId)
                .Sum(r => r.Total + r.CreditBalance);
        }

        // (1) حالة الفاتورة تعكس حقيقة الارتداد
        Assert.Equal(0m, await InvoicePaidAsync(invoiceId));
        // (2) الدين عاد على السجل المساعد والأستاذ مرة واحدة
        Assert.Equal(40m, await CustomerBalanceAsync());
        Assert.Equal(40m, gl1200);
        // (3) تقرير أعمار الذمم يعرض الدين (مستوى الفاتورة ومستوى الطرف) ويطابق الرقابي 1200
        Assert.Equal(40m, await InvoiceResidualAsync(invoiceId));
        Assert.Equal(40m, agingParty);
        Assert.Equal(gl1200, agingParty);

        // (4) منع التكرار: ارتداد ثانٍ مرفوض بلا أي أثر إضافي (لا قيد ولا خفض مزدوج)
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new BankCheckService(ctx, new JournalEntryService(ctx)).BounceAsync(checkId, DateTime.Today));
        }

        Assert.Equal(0m, await InvoicePaidAsync(invoiceId));
        Assert.Equal(40m, await CustomerBalanceAsync());
        Assert.Equal(40m, await GlNetAsync("1200"));
        await using (var ctx = _pg.NewContext())
            Assert.Equal(entriesBeforeRepeat, await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted));
    }

    [LivePgFact]
    public async Task Collected_cheque_keeps_the_invoice_paid_and_aging_clear()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var itemId = await _pg.CreateItemAsync("P7BOK-" + suffix, salePrice: 20m, costPrice: 10m, openingStock: 50m);
        var (invoiceId, checkId) = await CreateChequePaidInvoiceAsync(itemId, 2m, 20m, "P7BOK-CHK-" + suffix);

        // قياس تفاضلي: فحوص الفئة تتشارك قاعدة واحدة (قد يترك فحص ارتداد آخر ذمة قائمة على نفس العميل)،
        // والمقصود هنا أن التحصيل **لا يغيّر** رصيد الطرف ولا حساب 1200.
        var customerBefore = await CustomerBalanceAsync();
        var gl1200Before = await GlNetAsync("1200");

        await using (var ctx = _pg.NewContext())
        {
            var svc = new BankCheckService(ctx, new JournalEntryService(ctx));
            await svc.DepositAsync(checkId, DateTime.Today);
            await svc.ClearAsync(checkId, DateTime.Today);
        }

        // الشيك المحصَّل تسوية صحيحة ⇒ الفاتورة تبقى مدفوعة والدين غير ظاهر (الإصلاح لا يمسّ غير المرتجع)
        Assert.Equal(40m, await InvoicePaidAsync(invoiceId));
        Assert.Equal(0m, await InvoiceResidualAsync(invoiceId));
        Assert.Equal(customerBefore, await CustomerBalanceAsync());
        Assert.Equal(gl1200Before, await GlNetAsync("1200"));
    }
}
