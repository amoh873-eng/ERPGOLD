using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.RepCustody;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// SUB-1 / SUB-2 / SUB-3 — اتساق رصيد العميل التشغيلي (<c>Customer.CurrentBalance</c>) مع ذمم الأستاذ (1200)
/// وفق **سياسة الذمم المعتمدة 2026-10-24**: الحقل = صافي المستحق، يتغير فقط بما يغيّر 1200 للعميل.
/// قاعدة معزولة تُنشأ وتُحذف ذاتيًا · كل الكتابات عبر الخدمات الرسمية.
/// </summary>
public class SubCustomerBalanceTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public SubCustomerBalanceTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true; public bool CanAllocate() => true;
        public string? CurrentUserName() => "sub-rm";
    }

    private static string Code(string tag) => tag + "-" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Guid> NewCustomerAsync(string tag)
    {
        await using var ctx = _pg.NewContext();
        var c = new Customer
        {
            Id = Guid.NewGuid(), Code = Code(tag), NameAr = "عميل سياسة " + tag,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<Guid> NewItemAsync(string tag)
        => await _pg.CreateItemAsync(Code(tag), 10m, 5m, 10_000m, taxRate: 0m);

    private static CreateSalesInvoiceDto Sale(Guid cust, Guid wh, Guid item, SalesInvoiceType type,
        SalesPaymentMethod method, decimal qty = 5m)
        => new()
        {
            CustomerId = cust, WarehouseId = wh, InvoiceDate = DateTime.Today,
            InvoiceType = (int)type, PaymentMethod = (int)method, TaxRate = 0m,
            CardApprovalCode = method == SalesPaymentMethod.Card ? "CARD-OK" : null,
            CheckNumber = method == SalesPaymentMethod.Check ? Code("CHK") : null,
            CheckBankName = method == SalesPaymentMethod.Check ? "بنك اختبار SUB" : null,
            CheckDueDate = method == SalesPaymentMethod.Check ? DateTime.Today.AddDays(30) : null,
            Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = item, Quantity = qty, UnitPrice = 10m } }
        };

    private async Task<SalesInvoiceDto> CreateSaleAsync(Guid cust, Guid item, SalesInvoiceType type,
        SalesPaymentMethod method, decimal qty = 5m)
    {
        await using var ctx = _pg.NewContext();
        return await AccountingPgFixture.NewSalesService(ctx)
            .CreateAsync(Sale(cust, _pg.WarehouseId, item, type, method, qty));
    }

    private async Task CreateReturnAsync(Guid invoiceId, Guid item, decimal qty = 2m)
    {
        await using var ctx = _pg.NewContext();
        await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
        {
            SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
            Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = item, Quantity = qty, UnitPrice = 10m } }
        });
    }

    private async Task CreateReceiptAsync(Guid cust, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = cust, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        await svc.ApproveAsync(draft.Id);
        await svc.PostAsync(draft.Id);
    }

    private async Task CreateCustodyAsync(Guid cust, RepCustodyEffect effect, decimal amount)
    {
        await using var ctx = _pg.NewContext();
        await new RepCustodyService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateRepCustodyDto
        {
            Effect = effect, RepUserId = "REP-SUB", CustomerId = cust, Amount = amount, Timestamp = DateTime.Today
        }, "sub-rm");
    }

    private async Task<decimal> CustomerBalAsync(Guid customerId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<decimal> AcctNetAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.AccountId == accountId)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }

    private async Task AssertAllEntriesBalancedAsync()
    {
        await using var ctx = _pg.NewContext();
        var unbalanced = await ctx.Set<JournalEntry>().AsNoTracking()
            .Where(e => !e.IsDeleted)
            .Select(e => new
            {
                e.EntryNumber,
                Dr = e.Lines.Where(l => !l.IsDeleted).Sum(l => (decimal?)l.DebitAmount) ?? 0m,
                Cr = e.Lines.Where(l => !l.IsDeleted).Sum(l => (decimal?)l.CreditAmount) ?? 0m
            })
            .Where(x => x.Dr != x.Cr).ToListAsync();
        Assert.True(unbalanced.Count == 0,
            "قيود غير متوازنة: " + string.Join(",", unbalanced.Select(x => x.EntryNumber)));
    }

    private async Task<Guid> FindCheckForInvoiceAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<BankCheck>().AsNoTracking()
            .Where(b => b.RelatedInvoiceId == invoiceId).Select(b => b.Id).FirstAsync();
    }

    private async Task<int> JournalEntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted).CountAsync();
    }

    private async Task<BankCheckStatus> CheckStatusAsync(Guid checkId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<BankCheck>().AsNoTracking().Where(b => b.Id == checkId).Select(b => b.Status).FirstAsync();
    }

    private async Task DepositCheckAsync(Guid checkId)
    {
        await using var ctx = _pg.NewContext();
        await new BankCheckService(ctx, new JournalEntryService(ctx)).DepositAsync(checkId, DateTime.Today);
    }

    private async Task ClearCheckAsync(Guid checkId)
    {
        await using var ctx = _pg.NewContext();
        await new BankCheckService(ctx, new JournalEntryService(ctx)).ClearAsync(checkId, DateTime.Today);
    }

    private async Task BounceCheckAsync(Guid checkId)
    {
        await using var ctx = _pg.NewContext();
        await new BankCheckService(ctx, new JournalEntryService(ctx)).BounceAsync(checkId, DateTime.Today);
    }

    private async Task CreateReturnExpectingFailureAsync(Guid invoiceId, Guid item, decimal qty)
    {
        await using var ctx = _pg.NewContext();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = item, Quantity = qty, UnitPrice = 10m } }
            }));
    }

    // ── SUB-1: البيع النقدي/البطاقة لا يُنشئ ذمة؛ الآجل والشيك يطابقان 1200 ──
    [LivePgFact]
    public async Task T1_Cash_sale_does_not_change_customer_balance()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S1"); var item = await NewItemAsync("S1");
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200"); var cash0 = await AcctNetAsync("1100");

        await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Cash);

        Assert.Equal(0m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(0m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(50m, await AcctNetAsync("1100") - cash0);
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T2_Credit_sale_increases_customer_and_1200_equally()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S2"); var item = await NewItemAsync("S2");
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200");

        await CreateSaleAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);

        Assert.Equal(50m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(50m, await AcctNetAsync("1200") - ar0);
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T3_Card_sale_does_not_change_customer_balance_and_debits_1205()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S3"); var item = await NewItemAsync("S3");
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200"); var card0 = await AcctNetAsync("1205");

        await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Card);

        Assert.Equal(0m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(0m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(50m, await AcctNetAsync("1205") - card0);
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T4_Check_sale_nets_zero_on_customer_and_1200()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S4"); var item = await NewItemAsync("S4");
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200"); var chk0 = await AcctNetAsync("1102");

        await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);

        Assert.Equal(0m, await CustomerBalAsync(cust) - c0);   // +الإجمالي (فاتورة) ثم −الإجمالي (تسجيل الشيك)
        Assert.Equal(0m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(50m, await AcctNetAsync("1102") - chk0);
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T5_Bounced_check_restores_customer_and_1200()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S5"); var item = await NewItemAsync("S5");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);
        var checkId = await FindCheckForInvoiceAsync(inv.Id);
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200");

        await using (var ctx = _pg.NewContext())
            await new BankCheckService(ctx, new JournalEntryService(ctx)).BounceAsync(checkId, DateTime.Today);

        Assert.Equal(50m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(50m, await AcctNetAsync("1200") - ar0);
        await AssertAllEntriesBalancedAsync();
    }

    // ── SUB-2: دائن المردود يتبع وسيلة سداد الفاتورة الأصلية ──
    [LivePgFact]
    public async Task T6_Return_on_credit_sale_reduces_customer_and_1200()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S6"); var item = await NewItemAsync("S6");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount);
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200");
        var ret0 = await AcctNetAsync("4110"); var inv0 = await AcctNetAsync("1300");

        await CreateReturnAsync(inv.Id, item, 2m);   // 20

        Assert.Equal(-20m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(-20m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(20m, await AcctNetAsync("4110") - ret0);
        Assert.Equal(10m, await AcctNetAsync("1300") - inv0);   // عكس التكلفة (2×5)
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T7_Return_on_cash_sale_does_not_change_customer_balance()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S7"); var item = await NewItemAsync("S7");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Cash);
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200"); var cash0 = await AcctNetAsync("1100");

        await CreateReturnAsync(inv.Id, item, 2m);

        Assert.Equal(0m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(0m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(-20m, await AcctNetAsync("1100") - cash0);
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T8_Return_on_card_sale_reverses_1205_not_cash()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S8"); var item = await NewItemAsync("S8");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Card);
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200");
        var card0 = await AcctNetAsync("1205"); var cash0 = await AcctNetAsync("1100");

        await CreateReturnAsync(inv.Id, item, 2m);

        Assert.Equal(0m, await CustomerBalAsync(cust) - c0);      // لا ذمة على العميل للبطاقة
        Assert.Equal(0m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(-20m, await AcctNetAsync("1205") - card0);   // SUB-2: يعكس ذمم البطاقات
        Assert.Equal(0m, await AcctNetAsync("1100") - cash0);     // لا يخفض نقدًا لم يُقبض
        await AssertAllEntriesBalancedAsync();
    }

    // ── SUB-1: سند القبض يعدّل الرصيد مرة واحدة ──
    [LivePgFact]
    public async Task T9_Receipt_voucher_reduces_customer_and_1200_once()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S9");
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200");

        await CreateReceiptAsync(cust, 30m);

        Assert.Equal(-30m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(-30m, await AcctNetAsync("1200") - ar0);
        await AssertAllEntriesBalancedAsync();
    }

    // ── SUB-3: تحصيل العهدة يخفض الرصيد (دائن 1200)؛ التسليم لا يمسّ الذمم ──
    [LivePgFact]
    public async Task T10_Custody_collect_reduces_customer_and_1200_delivery_does_not()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S10");
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200"); var cst0 = await AcctNetAsync("1110");

        await CreateCustodyAsync(cust, RepCustodyEffect.Collect, 40m);

        Assert.Equal(-40m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(-40m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(40m, await AcctNetAsync("1110") - cst0);

        var c1 = await CustomerBalAsync(cust); var ar1 = await AcctNetAsync("1200");
        await CreateCustodyAsync(cust, RepCustodyEffect.Delivery, 40m);
        Assert.Equal(0m, await CustomerBalAsync(cust) - c1);
        Assert.Equal(0m, await AcctNetAsync("1200") - ar1);
        await AssertAllEntriesBalancedAsync();
    }

    // ── إجمالي: سيناريو مركّب يبقى متوازنًا ورصيد العميل = مجموع أثره الصافي ──
    [LivePgFact]
    public async Task T11_Full_sequence_keeps_entries_balanced_and_customer_matches_net()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S11"); var item = await NewItemAsync("S11");

        await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Cash);            // لا ذمة
        var credit = await CreateSaleAsync(cust, item, SalesInvoiceType.OnAccount, SalesPaymentMethod.OnAccount); // +50
        await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Card);            // لا ذمة
        await CreateReturnAsync(credit.Id, item, 2m);                                                 // −20
        await CreateReceiptAsync(cust, 10m);                                                          // −10
        await CreateCustodyAsync(cust, RepCustodyEffect.Collect, 5m);                                 // −5

        await AssertAllEntriesBalancedAsync();
        Assert.Equal(15m, await CustomerBalAsync(cust));   // 50 − 20 − 10 − 5
    }

    // ═══════════ SUB-2b: مردود فاتورة الشيك — دائن 1200 دائمًا (مبلغ مستحق للعميل) بلا نقد/بنك ═══════════
    [LivePgFact]
    public async Task T12_Return_on_registered_uncollected_check_credits_1200_only()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S12"); var item = await NewItemAsync("S12");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);
        var checkId = await FindCheckForInvoiceAsync(inv.Id);
        Assert.Equal(BankCheckStatus.Registered, await CheckStatusAsync(checkId));

        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200");
        var chk0 = await AcctNetAsync("1102"); var cash0 = await AcctNetAsync("1100"); var bank0 = await AcctNetAsync("1101");

        await CreateReturnAsync(inv.Id, item, 2m);   // 20

        Assert.Equal(-20m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(-20m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(0m, await AcctNetAsync("1102") - chk0);     // الشيك ما زال أصلًا (50)
        Assert.Equal(0m, await AcctNetAsync("1100") - cash0);    // لا يُثبت نقدًا
        Assert.Equal(0m, await AcctNetAsync("1101") - bank0);    // لا يخفض البنك
        Assert.Equal(BankCheckStatus.Registered, await CheckStatusAsync(checkId));
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T13_Return_on_deposited_uncollected_check_credits_1200_only()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S13"); var item = await NewItemAsync("S13");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);
        var checkId = await FindCheckForInvoiceAsync(inv.Id);
        await DepositCheckAsync(checkId);                       // لا أثر محاسبي
        Assert.Equal(BankCheckStatus.Deposited, await CheckStatusAsync(checkId));

        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200");
        var chk0 = await AcctNetAsync("1102"); var cash0 = await AcctNetAsync("1100");

        await CreateReturnAsync(inv.Id, item, 2m);

        Assert.Equal(-20m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(-20m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(0m, await AcctNetAsync("1102") - chk0);
        Assert.Equal(0m, await AcctNetAsync("1100") - cash0);
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T14_Return_on_cleared_check_credits_1200_and_does_not_touch_bank()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S14"); var item = await NewItemAsync("S14");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);
        var checkId = await FindCheckForInvoiceAsync(inv.Id);
        await ClearCheckAsync(checkId);
        Assert.Equal(BankCheckStatus.Cleared, await CheckStatusAsync(checkId));

        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200");
        var bank0 = await AcctNetAsync("1101"); var cash0 = await AcctNetAsync("1100");

        await CreateReturnAsync(inv.Id, item, 2m);

        Assert.Equal(-20m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(-20m, await AcctNetAsync("1200") - ar0);     // رصيد دائن للعميل
        Assert.Equal(0m, await AcctNetAsync("1101") - bank0);     // البنك لم يُخفَّض (لم يخرج مبلغ)
        Assert.Equal(0m, await AcctNetAsync("1100") - cash0);
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T15_Return_on_bounced_check_reduces_the_restored_receivable()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S15"); var item = await NewItemAsync("S15");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);
        var checkId = await FindCheckForInvoiceAsync(inv.Id);
        await BounceCheckAsync(checkId);   // 1200 = +50 والرصيد = +50
        Assert.Equal(BankCheckStatus.Bounced, await CheckStatusAsync(checkId));

        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200"); var chk0 = await AcctNetAsync("1102");

        await CreateReturnAsync(inv.Id, item, 2m);

        Assert.Equal(-20m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(-20m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(0m, await AcctNetAsync("1102") - chk0);   // الارتداد سبق أن أخرج الشيك من 1102
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T16_Repeated_partial_returns_accumulate_once_and_cap_is_enforced()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S16"); var item = await NewItemAsync("S16");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);  // 5 × 10 = 50
        var c0 = await CustomerBalAsync(cust);            // = 0 (فاتورة الشيك صافية صفر)
        var je0 = await JournalEntryCountAsync();

        await CreateReturnAsync(inv.Id, item, 3m);        // 30
        await CreateReturnAsync(inv.Id, item, 2m);        // 20 ⇒ استُنفدت الكمية المباعة

        Assert.Equal(-50m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(4, await JournalEntryCountAsync() - je0);   // مردودان × (عكس بيع + عكس تكلفة)

        // تجاوز الكمية ⇒ مرفوض بلا أي كتابة
        var c1 = await CustomerBalAsync(cust); var je1 = await JournalEntryCountAsync();
        await CreateReturnExpectingFailureAsync(inv.Id, item, 1m);
        Assert.Equal(c1, await CustomerBalAsync(cust));
        Assert.Equal(je1, await JournalEntryCountAsync());
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T17_Full_return_of_uncollected_check_creates_customer_credit_and_keeps_check_asset()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S17"); var item = await NewItemAsync("S17");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);  // 50
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200"); var chk0 = await AcctNetAsync("1102");

        await CreateReturnAsync(inv.Id, item, 5m);        // مردود كامل

        Assert.Equal(-50m, await CustomerBalAsync(cust) - c0);
        Assert.Equal(-50m, await AcctNetAsync("1200") - ar0);
        Assert.Equal(0m, await AcctNetAsync("1102") - chk0);   // الشيك ما زال أصلًا حتى يُعاد/يُسوّى
        await AssertAllEntriesBalancedAsync();
    }

    [LivePgFact]
    public async Task T18_Over_return_is_rejected_without_side_effects()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var cust = await NewCustomerAsync("S18"); var item = await NewItemAsync("S18");
        var inv = await CreateSaleAsync(cust, item, SalesInvoiceType.Cash, SalesPaymentMethod.Check);  // 5 × 10 = 50
        var c0 = await CustomerBalAsync(cust); var ar0 = await AcctNetAsync("1200"); var je0 = await JournalEntryCountAsync();

        await CreateReturnExpectingFailureAsync(inv.Id, item, 6m);   // أكثر من المباع (5)

        Assert.Equal(c0, await CustomerBalAsync(cust));
        Assert.Equal(ar0, await AcctNetAsync("1200"));
        Assert.Equal(je0, await JournalEntryCountAsync());
        await AssertAllEntriesBalancedAsync();
    }

}
