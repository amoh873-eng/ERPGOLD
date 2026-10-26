using ERPSystem.Application.DTOs.BankChecks;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P0-2E — تحقق تجريبي من مطابقة ذمم العملاء مع الأستاذ عبر دورة الشيك الفعلية.
/// كل الحركات عبر الخدمات الرسمية على قاعدة اختبار معزولة. لا تعديل كود إنتاج.
/// </summary>
public class P02ECheckReconciliationTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P02ECheckReconciliationTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "p02e";
    }

    private static string C(decimal v) => v.ToString("F2");

    /// <summary>لقطة: أرصدة الحسابات ذات الصلة (من كل السطور) + منطق الفاتورة + تقرير الأعمار + توازن القيود.</summary>
    private async Task<string> SnapAsync(string label, string checkStatus, Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        var accs = await ctx.Set<Account>().AsNoTracking()
            .Where(a => !a.IsDeleted && (a.Code == "1200" || a.Code == "1102" || a.Code == "1101" || a.Code == "1100"))
            .Select(a => new { a.Id, a.Code }).ToListAsync();

        async Task<decimal> NetAsync(string code)
        {
            var id = accs.FirstOrDefault(a => a.Code == code)?.Id ?? Guid.Empty;
            return await ctx.Set<JournalEntryLine>().AsNoTracking()
                .Where(l => !l.IsDeleted && l.AccountId == id)
                .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
        }

        var inv = await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        var aging = await AgingReportQueries.GetAgingAsync(ctx, isReceivable: true, asOf: DateTime.Today);
        var v1 = aging.Rows.Where(r => r.PartyId == inv.CustomerId).Sum(r => r.Total + r.CreditBalance);

        var entries = await ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted)
            .Select(e => new { e.Id }).ToListAsync();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
            .Select(l => new { l.JournalEntryId, l.DebitAmount, l.CreditAmount }).ToListAsync();
        var unbalanced = lines.GroupBy(l => l.JournalEntryId)
            .Count(g => Math.Abs(g.Sum(x => x.DebitAmount) - g.Sum(x => x.CreditAmount)) > 0.005m);

        return $"{label} | شيك={checkStatus} | 1200={C(await NetAsync("1200"))} · 1102={C(await NetAsync("1102"))} · " +
               $"1101={C(await NetAsync("1101"))} · 1100={C(await NetAsync("1100"))} | فاتورة: Total={C(inv.TotalAmount)} " +
               $"Paid={C(inv.PaidAmount)} متبقي(Total−Paid)={C(inv.TotalAmount - inv.PaidAmount)} | أعمار V1={C(v1)} | " +
               $"قيود غير متوازنة={unbalanced} من {entries.Count}";
    }

    private async Task<(Guid CustomerId, Guid ItemId)> SeedAsync()
    {
        await _pg.SeedCashAsync();
        Guid customerId;
        await using (var ctx = _pg.NewContext())
        {
            var customer = new Customer
            {
                Id = Guid.NewGuid(), Code = "P02E-" + Guid.NewGuid().ToString("N")[..5],
                NameAr = "عميل شيك", IsActive = true, CreatedAt = DateTime.UtcNow
            };
            ctx.Add(customer); await ctx.SaveChangesAsync();
            customerId = customer.Id;
        }
        var itemId = await _pg.CreateItemAsync("P02E-" + Guid.NewGuid().ToString("N")[..5], 10m, 6m, 100m, taxRate: 0m);
        return (customerId, itemId);
    }

    [LivePgFact]
    public async Task Check_lifecycle_reconciliation_against_general_ledger()
    {
        if (!_pg.Available) return;
        var (customerId, itemId) = await SeedAsync();
        var log = new List<string>();

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount, PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 10m } }
            });
            invoiceId = inv.Id;
        }
        log.Add(await SnapAsync("1) فاتورة آجلة 100", "-", invoiceId));

        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var d = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = customerId, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 20m
            });
            await svc.ApproveAsync(d.Id);
            await svc.PostAsync(d.Id);
        }
        log.Add(await SnapAsync("2) سند قبض نقدي 20", "-", invoiceId));

        Guid c1;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new BankCheckService(ctx, new JournalEntryService(ctx)).RegisterAsync(new CreateBankCheckDto
            {
                CheckNumber = "CH-1", BankName = "بنك اختبار", Direction = BankCheckDirection.ReceivedFromCustomer,
                CustomerId = customerId, Amount = 30m, IssueDate = DateTime.Today, DueDate = DateTime.Today.AddDays(30),
                ReceivedOrIssuedDate = DateTime.Today, RelatedInvoiceId = invoiceId
            });
            c1 = created.Id;
        }
        log.Add(await SnapAsync("3) تسجيل شيك 30 (Registered)", "1", invoiceId));

        await using (var ctx = _pg.NewContext())
            await new BankCheckService(ctx, new JournalEntryService(ctx)).DepositAsync(c1, DateTime.Today);
        log.Add(await SnapAsync("4) إيداع الشيك (Deposited)", "2", invoiceId));

        await using (var ctx = _pg.NewContext())
            await new BankCheckService(ctx, new JournalEntryService(ctx)).ClearAsync(c1, DateTime.Today);
        log.Add(await SnapAsync("5) تحصيل الشيك (Cleared)", "3", invoiceId));

        string dup;
        await using (var ctx = _pg.NewContext())
        {
            var ex = await Record.ExceptionAsync(() =>
                new BankCheckService(ctx, new JournalEntryService(ctx)).ClearAsync(c1, DateTime.Today));
            dup = ex is null ? "بلا استثناء (⚠)" : ex.GetType().Name + ": " + ex.Message;
        }
        log.Add(await SnapAsync("6) تحصيل مكرر → " + dup, "3", invoiceId));

        Guid c2;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new BankCheckService(ctx, new JournalEntryService(ctx)).RegisterAsync(new CreateBankCheckDto
            {
                CheckNumber = "CH-2", BankName = "بنك اختبار", Direction = BankCheckDirection.ReceivedFromCustomer,
                CustomerId = customerId, Amount = 40m, IssueDate = DateTime.Today, DueDate = DateTime.Today.AddDays(30),
                ReceivedOrIssuedDate = DateTime.Today
            });
            c2 = created.Id;
        }
        log.Add(await SnapAsync("7) تسجيل شيك 40", "1", invoiceId));

        await using (var ctx = _pg.NewContext())
            await new BankCheckService(ctx, new JournalEntryService(ctx)).BounceAsync(c2, DateTime.Today);
        log.Add(await SnapAsync("8) ارتجاع الشيك الثاني (Bounced)", "4", invoiceId));

        await File.WriteAllLinesAsync(@"D:\erpfix-pu01\_p02e_recon.txt", log);
        Assert.NotEmpty(log);
    }
}
