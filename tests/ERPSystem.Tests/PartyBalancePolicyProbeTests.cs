using ERPSystem.Application.DTOs.Purchases;
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

/// <summary>تفويض كامل للسندات في اختبار قياس السياسة.</summary>
internal sealed class ProbeVoucherAuthorization : IVoucherAuthorization
{
    public bool CanView() => true;
    public bool CanCreate() => true;
    public bool CanEdit() => true;
    public bool CanApprove() => true;
    public bool CanPost() => true;
    public bool CanCancel() => true;
    public string CurrentUserName() => "policy-probe";
}

/// <summary>
/// يقيس أثر **كل نوع مستند** على رصيد العميل والمورد خطوة بخطوة، لاشتقاق السياسة الفعلية المطبَّقة بدقة
/// قبل بناء صيغة التحقق في SQL. يعمل على قاعدة اختبار مؤقتة تُحذف ذاتيًا.
/// </summary>
public class PartyBalancePolicyProbeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public PartyBalancePolicyProbeTests(AccountingPgFixture pg) => _pg = pg;

    private static string OutFile()
    {
        var f = Environment.GetEnvironmentVariable("ERPSYSTEM_PROBE_OUT");
        return string.IsNullOrWhiteSpace(f) ? "party-policy-probe.txt" : f;
    }

    private static SalesInvoiceService Sales(AppDbContext c) => new(c, new JournalEntryService(c), new AccountingPgFixture.AllowAllManualPrice());
    private static PurchaseInvoiceService Purchase(AppDbContext c) => new(c, new JournalEntryService(c), new AccountingPgFixture.AllowAllManualPrice());
    private static SalesReturnService SalesReturn(AppDbContext c) => new(c, new JournalEntryService(c));
    private static PurchaseReturnService PurchaseReturn(AppDbContext c) => new(c, new JournalEntryService(c));
    private static ReceiptVoucherService Receipts(AppDbContext c) => new(c, new JournalEntryService(c), new ProbeVoucherAuthorization());
    private static PaymentVoucherService Payments(AppDbContext c) => new(c, new JournalEntryService(c), new ProbeVoucherAuthorization());

    [LivePgFact]
    public async Task Measure_party_balance_effect_of_each_document_type()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var log = new List<string> { "== PARTY BALANCE POLICY PROBE ==" };
        var today = DateTime.Today;
        var itemId = await _pg.CreateItemAsync("PB-ITEM-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 100m, taxRate: 16m);

        async Task<string> Bal()
        {
            await using var c = _pg.NewContext();
            var cust = await c.Set<Customer>().AsNoTracking().FirstAsync(x => x.Id == _pg.CustomerId);
            var sup = await c.Set<Supplier>().AsNoTracking().FirstAsync(x => x.Id == _pg.SupplierId);
            return $"customer={cust.CurrentBalance:F2} supplier={sup.CurrentBalance:F2}";
        }

        Guid cashSale = Guid.Empty, creditSale = Guid.Empty, cardSale = Guid.Empty, checkSale = Guid.Empty, cashPurchase = Guid.Empty;

        log.Add("initial                              " + await Bal());

        await using (var ctx = _pg.NewContext())
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Cash, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 15m } }
            });
            cashSale = si.Id;
            log.Add($"after CASH sale   total={si.TotalAmount:F2}          " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = today,
                InvoiceType = (int)SalesInvoiceType.OnAccount, PaymentMethod = (int)SalesPaymentMethod.OnAccount, TaxRate = 16m,
                Lines = new() { new() { ItemId = itemId, Quantity = 5m, UnitPrice = 15m } }
            });
            creditSale = si.Id;
            log.Add($"after CREDIT sale total={si.TotalAmount:F2}          " + await Bal());
        }
        await File.WriteAllLinesAsync(OutFile(), log);
        Assert.True(File.Exists(OutFile()));
        await using (var ctx = _pg.NewContext())
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Card, TaxRate = 16m,
                CardApprovalCode = "PB-CARD", CardLast4 = "4242",
                Lines = new() { new() { ItemId = itemId, Quantity = 2m, UnitPrice = 15m } }
            });
            cardSale = si.Id;
            log.Add($"after CARD sale   total={si.TotalAmount:F2}          " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var si = await Sales(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId, WarehouseId = _pg.WarehouseId, InvoiceDate = today,
                InvoiceType = (int)SalesInvoiceType.Cash, PaymentMethod = (int)SalesPaymentMethod.Check, TaxRate = 16m,
                CheckNumber = "PB-CHK", CheckBankName = "بنك القياس", CheckDueDate = today.AddDays(30),
                Lines = new() { new() { ItemId = itemId, Quantity = 4m, UnitPrice = 15m } }
            });
            checkSale = si.Id;
            log.Add($"after CHECK sale  total={si.TotalAmount:F2}          " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var sr = await SalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = cashSale, WarehouseId = _pg.WarehouseId, ReturnDate = today,
                Lines = new() { new() { ItemId = itemId, Quantity = 3m, UnitPrice = 15m } }
            });
            log.Add($"after RETURN of CASH invoice  total={sr.TotalAmount:F2} " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var sr = await SalesReturn(ctx).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = checkSale, WarehouseId = _pg.WarehouseId, ReturnDate = today,
                Lines = new() { new() { ItemId = itemId, Quantity = 1m, UnitPrice = 15m } }
            });
            log.Add($"after RETURN of CHECK invoice total={sr.TotalAmount:F2} " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var dto = await Receipts(ctx).CreateDraftAsync(new CreateReceiptVoucherDto { CustomerId = _pg.CustomerId, VoucherDate = today, PaymentMethod = 1, Amount = 100m });
            log.Add("after RECEIPT draft (not posted)                " + await Bal());
            await Receipts(ctx).ApproveAsync(dto.Id);
            await Receipts(ctx).PostAsync(dto.Id);
            log.Add("after RECEIPT posted amount=100.00              " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var pi = await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = _pg.SupplierId, WarehouseId = _pg.WarehouseId, InvoiceDate = today,
                InvoiceType = (int)PurchaseInvoiceType.Cash,
                Lines = new() { new() { ItemId = itemId, Quantity = 20m, UnitCost = 10m } }
            });
            cashPurchase = pi.Id;
            log.Add($"after CASH purchase   total={pi.TotalAmount:F2}     " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var pi = await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = _pg.SupplierId, WarehouseId = _pg.WarehouseId, InvoiceDate = today,
                InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                Lines = new() { new() { ItemId = itemId, Quantity = 10m, UnitCost = 10m } }
            });
            log.Add($"after CREDIT purchase total={pi.TotalAmount:F2}     " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var pi = await Purchase(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = _pg.SupplierId, WarehouseId = _pg.WarehouseId, InvoiceDate = today,
                InvoiceType = (int)PurchaseInvoiceType.Cash, PaymentMethod = 2,
                CheckNumber = "PB-PCHK", CheckBankName = "بنك القياس", CheckDueDate = today.AddDays(30),
                Lines = new() { new() { ItemId = itemId, Quantity = 5m, UnitCost = 10m } }
            });
            log.Add($"after CHECK purchase  total={pi.TotalAmount:F2}     " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            var pr = await PurchaseReturn(ctx).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = cashPurchase, WarehouseId = _pg.WarehouseId, ReturnDate = today, SettlementType = 2, RefundReference = "PU04-TEST-REF",
                Lines = new() { new() { ItemId = itemId, Quantity = 5m, UnitCost = 10m } }
            });
            log.Add($"after PURCHASE RETURN total={pr.TotalAmount:F2}     " + await Bal());
        }
        await using (var ctx = _pg.NewContext())
        {
            // PU-03B: Arrange صريح — ذمّة كافية لسند الصرف (200) الذي يفحصه هذا الاختبار.
            await AccountingPgFixture.SeedPayableForAsync(ctx, _pg.SupplierId, 200m);
            var dto = await Payments(ctx).CreateDraftAsync(new CreatePaymentVoucherDto { SupplierId = _pg.SupplierId, VoucherDate = today, PaymentMethod = 1, Amount = 200m });
            await Payments(ctx).ApproveAsync(dto.Id);
            await Payments(ctx).PostAsync(dto.Id);
            log.Add("after PAYMENT posted amount=200.00              " + await Bal());
        }
        await using (var c = _pg.NewContext())
        {
            var ar = await c.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
                .Join(c.Set<Account>().AsNoTracking(), l => l.AccountId, a => a.Id, (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
                .Where(x => x.Code == "1200").SumAsync(x => (decimal?)(x.DebitAmount - x.CreditAmount)) ?? 0m;
            var ap = await c.Set<JournalEntryLine>().AsNoTracking().Where(l => !l.IsDeleted)
                .Join(c.Set<Account>().AsNoTracking(), l => l.AccountId, a => a.Id, (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
                .Where(x => x.Code == "2200").SumAsync(x => (decimal?)(x.DebitAmount - x.CreditAmount)) ?? 0m;
            log.Add($"ledger_1200_AR_net={ar:F2} ledger_2200_AP_net={ap:F2}");
            var checks = await c.Set<BankCheck>().AsNoTracking().Select(b => "dir" + b.Direction + ":" + b.Amount.ToString()).ToListAsync();
            log.Add("bank_checks=" + string.Join(", ", checks));
        }

        await File.AppendAllLinesAsync(OutFile(), log);
        Assert.True(File.Exists(OutFile()));
    }
}
