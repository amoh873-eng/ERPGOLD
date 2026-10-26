using System.Text;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P0-2A — تقرير المطابقة M1–M3 (**قراءة فقط**).
/// V2 = CurrentBalance المخزَّن · V1 = الذمم المفتوحة من المستندات المرحَّلة (AgingReportQueries)
/// · V3 = رصيد حساب الذمم المنسوب للعميل عبر قيود مستنداته. لا كتابة في قاعدة البيانات.
/// </summary>
public class P02ReconciliationProbeTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P02ReconciliationProbeTests(AccountingPgFixture pg) => _pg = pg;

    private sealed record Row(string Code, string Name, decimal V2, decimal V1, decimal V3);

    [LivePgFact]
    public async Task Produce_M1_M3_reconciliation_report_read_only()
    {
        if (!_pg.Available) return;

        // ═══ بيانات اصطناعية عبر الخدمات الرسمية (لا كتابة مباشرة في القيود/الأرصدة) ═══
        await _pg.SeedCashAsync();
        var custA = await NewCustomerAsync("M1A");
        var custB = await NewCustomerAsync("M1B");
        var itemId = await _pg.CreateItemAsync("M1-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 200m, taxRate: 16m);

        Guid creditInvoiceId;
        await using (var seed = _pg.NewContext())
        {
            var sales = AccountingPgFixture.NewSalesService(seed);
            await sales.CreateAsync(Sale(custA, itemId, ERPSystem.Domain.Enums.SalesInvoiceType.Cash));
            var inv = await sales.CreateAsync(Sale(custA, itemId, ERPSystem.Domain.Enums.SalesInvoiceType.OnAccount));
            creditInvoiceId = inv.Id;
            await sales.CreateAsync(Sale(custB, itemId, ERPSystem.Domain.Enums.SalesInvoiceType.OnAccount));
        }
        await using (var seed = _pg.NewContext())
        {
            var vouchers = new ReceiptVoucherService(seed, new JournalEntryService(seed), new AllowAll());
            var draft = await vouchers.CreateDraftAsync(new ERPSystem.Application.DTOs.Vouchers.CreateReceiptVoucherDto
            {
                CustomerId = custA, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 30m
            });
            await vouchers.ApproveAsync(draft.Id);
            await vouchers.PostAsync(draft.Id);
        }
        await using (var seed = _pg.NewContext())
        {
            await new SalesReturnService(seed, new JournalEntryService(seed)).CreateAsync(
                new ERPSystem.Application.DTOs.Sales.CreateSalesReturnDto
                {
                    SalesInvoiceId = creditInvoiceId,
                    WarehouseId = _pg.WarehouseId,
                    ReturnDate = DateTime.Today,
                    Lines = new List<ERPSystem.Application.DTOs.Sales.CreateSalesReturnLineDto>
                    {
                        new() { ItemId = itemId, Quantity = 2m, UnitPrice = 15m }
                    }
                });
        }

        await using var ctx = _pg.NewContext();

        var customers = await ctx.Set<Customer>().AsNoTracking().Where(c => !c.IsDeleted)
            .Select(c => new { c.Id, c.Code, c.NameAr, c.CurrentBalance }).ToListAsync();

        var aging = await AgingReportQueries.GetAgingAsync(ctx, isReceivable: true, asOf: DateTime.Today);
        var v1ByCustomer = aging.Rows.GroupBy(r => r.PartyId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Total + x.CreditBalance));

        var arAccountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == "1200" && !a.IsDeleted).Select(a => a.Id).FirstOrDefaultAsync();

        var entryToCustomer = new Dictionary<Guid, Guid>();
        foreach (var i in await ctx.Set<SalesInvoice>().AsNoTracking()
                     .Where(i => !i.IsDeleted)
                     .Select(i => new { i.CustomerId, i.SalesJournalEntryId }).ToListAsync())
        {
            if (i.SalesJournalEntryId is not null)
                entryToCustomer[i.SalesJournalEntryId.Value] = i.CustomerId;
        }
        foreach (var r in await ctx.Set<SalesReturn>().AsNoTracking().Where(r => !r.IsDeleted)
                     .Select(r => new { CustomerId = r.SalesInvoice!.CustomerId, r.SalesJournalEntryId }).ToListAsync())
        {
            if (r.SalesJournalEntryId is not null)
                entryToCustomer[r.SalesJournalEntryId.Value] = r.CustomerId;
        }
        foreach (var v in await ctx.Set<ReceiptVoucher>().AsNoTracking()
                     .Where(v => !v.IsDeleted && v.JournalEntryId != null)
                     .Select(v => new { v.CustomerId, v.JournalEntryId }).ToListAsync())
            entryToCustomer[v.JournalEntryId!.Value] = v.CustomerId;

        var arLines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.AccountId == arAccountId)
            .Select(l => new { l.JournalEntryId, Net = l.DebitAmount - l.CreditAmount }).ToListAsync();

        var v3ByCustomer = new Dictionary<Guid, decimal>();
        decimal attributed = 0m, arTotal = 0m;
        foreach (var l in arLines)
        {
            arTotal += l.Net;
            if (entryToCustomer.TryGetValue(l.JournalEntryId, out var cust))
            {
                v3ByCustomer[cust] = v3ByCustomer.GetValueOrDefault(cust) + l.Net;
                attributed += l.Net;
            }
        }

        var rows = customers.Select(c => new Row(
            c.Code, c.NameAr, c.CurrentBalance,
            v1ByCustomer.GetValueOrDefault(c.Id, 0m),
            -(v3ByCustomer.GetValueOrDefault(c.Id, 0m)))).ToList();

        var withData = rows.Where(r => Math.Abs(r.V1) > 0.005m || Math.Abs(r.V2) > 0.005m || Math.Abs(r.V3) > 0.005m).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# P0-2A — تقرير المطابقة M1–M3 (قراءة فقط)");
        sb.AppendLine();
        sb.AppendLine($"- وقت التقرير (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("- مصدر البيانات: قاعدة اختبار معزولة (PostgreSQL 127.0.0.1:15432)");
        sb.AppendLine($"- عدد العملاء الكلي: {rows.Count} · لهم بيانات: {withData.Count}");
        sb.AppendLine("- V2 = CurrentBalance المخزَّن · V1 = الذمم المفتوحة (AgingReportQueries) · V3 = منسوب من قيود المستندات");
        sb.AppendLine();
        sb.AppendLine("| العميل | الكود | V2 المخزَّن | V1 المستحق | V3 المنسوب | V2−V1 | V3−V1 | التصنيف |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in withData.OrderByDescending(x => Math.Abs(x.V2 - x.V1)).Take(40))
        {
            var d21 = r.V2 - r.V1; var d31 = r.V3 - r.V1;
            var cls = Math.Abs(d31) <= 0.005m
                ? (Math.Abs(d21) <= 0.005m ? "مطابق" : "فرق V2 فقط (أثر نقدي متوقع)")
                : "فرق V3 — يحتاج فحص مستندات";
            sb.AppendLine($"| {r.Name} | {r.Code} | {r.V2:F2} | {r.V1:F2} | {r.V3:F2} | {d21:F2} | {d31:F2} | {cls} |");
        }
        sb.AppendLine();
        sb.AppendLine("## المجاميع");
        sb.AppendLine($"- مجموع V2: {rows.Sum(r => r.V2):F2}");
        sb.AppendLine($"- مجموع V1: {rows.Sum(r => r.V1):F2}");
        sb.AppendLine($"- رصيد 1200 الكلي: {arTotal:F2}");
        sb.AppendLine($"- المنسوب للعملاء: {attributed:F2}");
        sb.AppendLine($"- غير قابل للنسب: {arTotal - attributed:F2}");
        sb.AppendLine($"- مجموع V3: {rows.Sum(r => r.V3):F2}");

        await File.WriteAllTextAsync(@"D:\erpfix-pu01\_p02a_recon.md", sb.ToString(), Encoding.UTF8);
        Assert.True(rows.Count > 0, "لا توجد بيانات عملاء في قاعدة الاختبار");
    }

    private ERPSystem.Application.DTOs.Sales.CreateSalesInvoiceDto Sale(
        Guid customerId, Guid itemId, ERPSystem.Domain.Enums.SalesInvoiceType type)
        => new()
        {
            CustomerId = customerId,
            WarehouseId = _pg.WarehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)type,
            PaymentMethod = type == ERPSystem.Domain.Enums.SalesInvoiceType.Cash
                ? (int)ERPSystem.Domain.Enums.SalesPaymentMethod.Cash
                : (int)ERPSystem.Domain.Enums.SalesPaymentMethod.OnAccount,
            TaxRate = 16m,
            Lines = new List<ERPSystem.Application.DTOs.Sales.CreateSalesInvoiceLineDto>
            {
                new() { ItemId = itemId, Quantity = 5m, UnitPrice = 15m }
            }
        };

    private sealed class AllowAll : ERPSystem.Application.Security.IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "p02a";
    }

    private async Task<Guid> NewCustomerAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "عميل مطابقة " + code, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }
}
