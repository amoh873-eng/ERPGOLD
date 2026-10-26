using System.Data.Common;
using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.DTOs.Journal;
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

/// <summary>
/// المرحلة الثانية (تدقيق اتساق الأرصدة) — اختبارات فحص **إضافية** لتغطية حالات لم تُغطَّ
/// صراحةً في المجموعات القائمة. لا تُعدَّل أي شيفرة إنتاج، وكل شيء على قاعدة PostgreSQL
/// معزولة تُنشأ وتُحذف ذاتيًا (AccountingPgFixture ⇒ erp_a5_*).
///
/// الحالات المغطاة:
///  1) سند **ملغى** (قبل الترحيل) ⇒ بلا قيد وبلا أثر على أي رصيد.
///  2) سند صرف **معكوس** ⇒ قيدان (أصلي + عكسي) وصافي صفر، والمخزَّن = المستنتج.
///  3) **حركات متعددة** على الحساب نفسه ⇒ تُجمع مرة واحدة (لا ازدواج).
///  4) الحساب الرقابي 1200 مقابل **مجموع أرصدة العملاء**: المطابقة على مسارات المستندات،
///     والفرق يُفسَّر بمصدره (قيد يدوي على الرقابي) بدل تسويته.
///  5) الحساب الرقابي 2200 مقابل **مجموع أرصدة الموردين**: نفس المنهج.
///  6) شجرة الأرصدة **لفترة** = دفتر الأستاذ لنفس الفترة، والشجرة **بلا تواريخ** = الرصيد المخزَّن.
///  7) المخزون: حركة المخزون الافتتاحية بلا قيد، ومردود/بيع COGS بقيد ⇒ قياس الفرق وتوثيقه.
/// </summary>
public class AccountingBalanceReconciliationProbeTests : IClassFixture<AccountingPgFixture>
{
    private const string Cash = "1100";
    private const string Receivable = "1200";
    private const string Inventory = "1300";
    private const string Payable = "2200";
    private const string Cogs = "5100";

    private readonly AccountingPgFixture _pg;

    public AccountingBalanceReconciliationProbeTests(AccountingPgFixture pg) => _pg = pg;

    // ───────────────────────── تصاريح اختبار ─────────────────────────

    private sealed class AllowAllVouchers : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanRefund() => true;
        public bool CanReverse() => true;
        public bool CanAllocate() => true;
        public string? CurrentUserName() => "bal2-probe";
    }

    private static PaymentVoucherService Payments(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    private static PaymentVoucherReversalService Reversals(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    // ───────────────────────── أدوات قياس ─────────────────────────

    private async Task<Guid> AccountIdAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
    }

    private async Task<decimal> StoredAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code).Select(a => a.CurrentBalance).FirstAsync();
    }

    /// <summary>الرصيد المستنتج من دفتر القيود وفق طبيعية الحساب.</summary>
    private async Task<decimal> DerivedAsync(string code)
    {
        var id = await AccountIdAsync(code);
        await using var ctx = _pg.NewContext();
        var normal = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Id == id).Select(a => a.NormalBalance).FirstAsync();
        var rows = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == id && !l.IsDeleted && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .ToListAsync();
        var d = rows.Sum(l => l.DebitAmount);
        var c = rows.Sum(l => l.CreditAmount);
        return normal == NormalBalance.Debit ? d - c : c - d;
    }

    private async Task<int> EntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    private async Task<decimal> SumCustomerBalancesAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => !c.IsDeleted).SumAsync(c => c.CurrentBalance);
    }

    private async Task<decimal> SumSupplierBalancesAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Supplier>().AsNoTracking()
            .Where(s => !s.IsDeleted).SumAsync(s => s.CurrentBalance);
    }

    /// <summary>فحص شامل: المخزَّن = المستنتج لكل الحسابات + توازن الأستاذ.</summary>
    private async Task AssertStoredEqualsDerivedAsync()
    {
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    private async Task<Guid> NewCustomerAsync(string tag)
    {
        await using var ctx = _pg.NewContext();
        var c = new Customer
        {
            Id = Guid.NewGuid(), Code = "BAL2-C-" + tag, NameAr = "عميل " + tag,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>
    /// ورقة دائنة (حقوق ملكية/التزام) صالحة للحركة اليدوية — بنفس منطق بذر البيئة:
    /// أول حساب نشط غير محذوف بطبيعته دائنة وليس أبًا لأحد.
    /// </summary>
    private async Task<Guid> CreditLeafIdAsync(AppDbContext ctx)
    {
        var parentIds = ctx.Set<Account>().Where(a => a.ParentAccountId != null)
            .Select(a => a.ParentAccountId!.Value).ToHashSet();
        return ctx.Set<Account>()
            .Where(a => !a.IsDeleted && a.IsActive && a.NormalBalance == NormalBalance.Credit)
            .AsEnumerable()
            .Where(a => !parentIds.Contains(a.Id))
            .OrderBy(a => a.Code, StringComparer.Ordinal)
            .First().Id;
    }

    // ─────────── 8) بيع آجل + مردود كامل: القيد الصحيح والتناظر الرقابي (إثبات أرقام) ───────────

    /// <summary>
    /// إثبات سلوك مسار البيع الآجل + مردوده الكامل مع الأرقام:
    ///  • بيع آجل 150 ⇒ Dr 1200 / Cr 4100 وذمة الطرف +150.
    ///  • مردود كامل ⇒ **Cr 1200** (لا 1100/1105/1205) وذمة الطرف −150 ⇒ الرقابي والطرف يعودان إلى الصفر معًا.
    ///  • الفرق الرقابي قبل/بعد يبقى كما هو (لا انزياح).
    /// </summary>
    [LivePgFact]
    public async Task Probe8_OnAccount_sale_and_full_return_net_to_zero_preserving_gap()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("DIAG");
        var itemId = await _pg.CreateItemAsync("BAL2-DIAG-" + Guid.NewGuid().ToString("N")[..6],
            15m, 10m, 100m, taxRate: 0m);

        var controlBefore = await DerivedAsync(Receivable);
        var subBefore = await SumCustomerBalancesAsync();
        var gapBefore = Math.Round(controlBefore - subBefore, 2);
        var cashBefore = await DerivedAsync(Cash);

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customer, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount,
                PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 15m } }
            });
            invoiceId = inv.Id;
        }

        // تأكيد أن الفاتورة محفوظة كآجلة فعلًا (شرط توجيه المردود إلى 1200)
        await using (var ctx = _pg.NewContext())
        {
            var inv = await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => i.Id == invoiceId);
            Assert.Equal(SalesInvoiceType.OnAccount, inv.InvoiceType);
            Assert.Equal(SalesPaymentMethod.OnAccount, inv.PaymentMethod);
            Assert.Equal(150m, inv.TotalAmount);
        }

        var controlAfterSale = await DerivedAsync(Receivable);
        var subAfterSale = await SumCustomerBalancesAsync();
        Assert.Equal(150m, Math.Round(controlAfterSale - controlBefore, 2));
        Assert.Equal(150m, Math.Round(subAfterSale - subBefore, 2));

        await using (var ctx = _pg.NewContext())
        {
            await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 15m } }
            });
        }

        // قيد المردود: Dr 4110 / **Cr 1200** فقط (بلا مساس بالنقد/الدرج/البطاقة)
        await using (var ctx = _pg.NewContext())
        {
            var ret = await ctx.Set<SalesReturn>().AsNoTracking().FirstAsync(r => r.SalesInvoiceId == invoiceId);
            var rows = await ctx.Set<JournalEntryLine>().AsNoTracking()
                .Where(l => l.JournalEntryId == ret.SalesJournalEntryId)
                .Join(ctx.Set<Account>().AsNoTracking(), l => l.AccountId, a => a.Id,
                    (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
                .ToListAsync();

            Assert.Equal(150m, rows.Where(r => r.Code == "4110").Sum(r => r.DebitAmount));
            Assert.Equal(150m, rows.Where(r => r.Code == Receivable).Sum(r => r.CreditAmount));
            Assert.DoesNotContain(rows, r => r.Code == Cash || r.Code == "1105" || r.Code == "1205");
        }

        var controlAfterReturn = await DerivedAsync(Receivable);
        var subAfterReturn = await SumCustomerBalancesAsync();
        Assert.Equal(-150m, Math.Round(controlAfterReturn - controlAfterSale, 2)); // الرقابي يُخفَّض بالمردود
        Assert.Equal(-150m, Math.Round(subAfterReturn - subAfterSale, 2));         // والذمة كذلك
        Assert.Equal(0m, Math.Round(controlAfterReturn - controlBefore, 2));       // صافي صفر
        Assert.Equal(0m, Math.Round(subAfterReturn - subBefore, 2));
        Assert.Equal(gapBefore, Math.Round(controlAfterReturn - subAfterReturn, 2));

        // النقد لم يتحرك إطلاقًا في الدورة (لا بيع نقدي ولا رد نقدي) — قياس تفاضلي لتفادي أثر اختبارات أخرى
        Assert.Equal(cashBefore, await DerivedAsync(Cash));

        await AssertStoredEqualsDerivedAsync();
    }

    private async Task<Guid> NewSupplierAsync(string tag)
    {
        await using var ctx = _pg.NewContext();
        var s = new Supplier
        {
            Id = Guid.NewGuid(), Code = "BAL2-S-" + tag, NameAr = "مورد " + tag,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(s);
        await ctx.SaveChangesAsync();
        return s.Id;
    }

    // ─────────── 1) سند ملغى (قبل الترحيل) ⇒ بلا قيد وبلا أثر على الأرصدة ───────────

    [LivePgFact]
    public async Task Probe1_Cancelled_voucher_leaves_no_entry_and_no_balance_change()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("CANCEL");

        var cashStoredBefore = await StoredAsync(Cash);
        var payableStoredBefore = await StoredAsync(Payable);
        var entriesBefore = await EntryCountAsync();

        Guid voucherId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Payments(ctx);
            var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = supplier, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 75m,
                Description = "BAL2 cancel probe"
            });
            voucherId = draft.Id;
            await svc.ApproveAsync(voucherId);
            await svc.CancelAsync(voucherId, "BAL2 probe cancel");
        }

        await using (var ctx = _pg.NewContext())
        {
            var v = await ctx.Set<PaymentVoucher>().AsNoTracking().FirstAsync(x => x.Id == voucherId);
            Assert.Equal(VoucherStatus.Cancelled, v.Status);
            Assert.Null(v.JournalEntryId);          // لا قيد إطلاقًا
        }

        Assert.Equal(entriesBefore, await EntryCountAsync());
        Assert.Equal(cashStoredBefore, await StoredAsync(Cash));
        Assert.Equal(payableStoredBefore, await StoredAsync(Payable));
        await AssertStoredEqualsDerivedAsync();     // المخزَّن = المستنتج بلا تغيير
    }

    // ─────────── 2) سند صرف معكوس ⇒ قيدان وصافي صفر ───────────

    [LivePgFact]
    public async Task Probe2_Reversed_payment_voucher_keeps_two_entries_netting_to_zero()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("REV");
        Guid invoice;
        await using (var seedCtx = _pg.NewContext())
            invoice = await AccountingPgFixture.SeedPayableForAsync(seedCtx, supplier, 500m);

        var cashStoredBefore = await StoredAsync(Cash);
        var payableStoredBefore = await StoredAsync(Payable);
        var entriesBefore = await EntryCountAsync();

        Guid voucherId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Payments(ctx);
            var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = supplier, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 300m
            });
            voucherId = draft.Id;
            await svc.ApproveAsync(voucherId);
            await svc.PostAsync(voucherId, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoice, Amount = 300m }
            });
        }

        Assert.Equal(cashStoredBefore - 300m, await StoredAsync(Cash));
        Assert.Equal(payableStoredBefore - 300m, await StoredAsync(Payable));

        Guid allocationId;
        await using (var ctx = _pg.NewContext())
        {
            allocationId = await ctx.Set<PaymentAllocation>().AsNoTracking()
                .Where(a => a.PaymentVoucherId == voucherId && a.Direction == 1)
                .Select(a => a.Id).FirstAsync();
        }

        await using (var ctx = _pg.NewContext())
        {
            await Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = voucherId,
                VoucherDate = DateTime.Today,
                Reason = "BAL2 reversal probe",
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 300m }
                }
            });
        }

        // قيدان فقط أُضيفا (الأصلي + العكسي)، والصافي على الخزنة والذمم = صفر
        Assert.Equal(entriesBefore + 2, await EntryCountAsync());
        Assert.Equal(cashStoredBefore, await StoredAsync(Cash));
        Assert.Equal(payableStoredBefore, await StoredAsync(Payable));
        await AssertStoredEqualsDerivedAsync();
    }

    // ─────────── 3) حركات متعددة على الحساب نفسه ⇒ تُجمع مرة واحدة ───────────

    [LivePgFact]
    public async Task Probe3_Multiple_movements_on_same_account_are_summed_once()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        await using var ctx = _pg.NewContext();
        var cashId = (await PostingAccountResolver.ResolveAsync(ctx, Cash)).Id;
        var equityId = await CreditLeafIdAsync(ctx);
        var js = new JournalEntryService(ctx);

        var before = await StoredAsync(Cash);
        for (var i = 0; i < 3; i++)
        {
            await js.CreateManualEntryAsync(new DateTime(2071, 3, 1 + i), $"BAL2-MULTI-{i}",
                "ثلاث حركات على الحساب نفسه", new List<JournalEntryLegInput>
                {
                    new() { AccountId = cashId, DebitAmount = 100m * (i + 1) },
                    new() { AccountId = equityId, CreditAmount = 100m * (i + 1) }
                });
        }

        Assert.Equal(before + 600m, await StoredAsync(Cash));       // 100 + 200 + 300
        Assert.Equal(before + 600m, await DerivedAsync(Cash));      // من دفتر القيود
        await AssertStoredEqualsDerivedAsync();
    }

    // ─────────── 4) الرقابي 1200 مقابل مجموع أرصدة العملاء (مع تفسير الفرق) ───────────

    [LivePgFact]
    public async Task Probe4_Receivable_control_versus_customer_balances_with_classified_difference()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("AR");
        var itemId = await _pg.CreateItemAsync("BAL2-AR-" + Guid.NewGuid().ToString("N")[..6],
            15m, 10m, 500m, taxRate: 0m);

        var controlBefore = await DerivedAsync(Receivable);
        var subBefore = await SumCustomerBalancesAsync();
        var gapBefore = Math.Round(controlBefore - subBefore, 2);

        // (1) بيع **آجل** ⇒ Dr 1200 / Cr 4100 ⇒ الطرف والرقابي يتحركان معًا (+150)
        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customer, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount,
                PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 15m } }
            });
            invoiceId = inv.Id;
        }

        var control1 = await DerivedAsync(Receivable);
        var sub1 = await SumCustomerBalancesAsync();
        Assert.Equal(150m, Math.Round(control1 - controlBefore, 2));
        Assert.Equal(150m, Math.Round(sub1 - subBefore, 2));
        Assert.Equal(gapBefore, Math.Round(control1 - sub1, 2));

        // (2) مردود **آجل** كامل ⇒ Dr 4110 / Cr 1200 ⇒ الاثنان ينقصان بالمنعكس
        await using (var ctx = _pg.NewContext())
        {
            await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = invoiceId, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 15m } }
            });
        }

        var control2 = await DerivedAsync(Receivable);
        var sub2 = await SumCustomerBalancesAsync();
        // المردود الآجل يقيّد **Cr 1200** ⇒ الرقابي ينقص بالمبلغ، والذمة تنقص بنفس المبلغ
        Assert.Equal(-150m, Math.Round(control2 - control1, 2));
        Assert.Equal(-150m, Math.Round(sub2 - sub1, 2));
        Assert.Equal(0m, Math.Round(control2 - controlBefore, 2));      // صافي صفر بعد الدورة
        Assert.Equal(0m, Math.Round(sub2 - subBefore, 2));

        // (3) قيد **يدوي** على الرقابي 1200 بلا أي مستند عميل ⇒ الفرق = مبلغ القيد بالضبط
        const decimal manual = 500m;
        await using (var ctx = _pg.NewContext())
        {
            var recId = (await PostingAccountResolver.ResolveAsync(ctx, Receivable)).Id;
            var equityId = await CreditLeafIdAsync(ctx);
            await new JournalEntryService(ctx).CreateManualEntryAsync(new DateTime(2072, 1, 15),
                "BAL2-MANUAL-AR", "قيد يدوي على الرقابي بلا طرف (توثيق فرق)",
                new List<JournalEntryLegInput>
                {
                    new() { AccountId = recId, DebitAmount = manual },
                    new() { AccountId = equityId, CreditAmount = manual }
                });
        }

        var control3 = await DerivedAsync(Receivable);
        var sub3 = await SumCustomerBalancesAsync();
        Assert.Equal(manual, Math.Round(control3 - control2, 2));   // الرقابي زاد 500
        Assert.Equal(0m, Math.Round(sub3 - sub2, 2));               // الأطراف لم تتغير
        Assert.Equal(manual, Math.Round(control3 - sub3, 2));       // الفرق مُفسَّر بالكامل

        await AssertStoredEqualsDerivedAsync();
    }

    // ─────────── 5) الرقابي 2200 مقابل مجموع أرصدة الموردين (مع تفسير الفرق) ───────────

    [LivePgFact]
    public async Task Probe5_Payable_control_versus_supplier_balances_with_classified_difference()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("AP");

        var controlBefore = await DerivedAsync(Payable);
        var subBefore = await SumSupplierBalancesAsync();
        var gapBefore = Math.Round(controlBefore - subBefore, 2);

        // (1) فاتورة مشتريات آجلة ⇒ Dr 1300 / Cr 2200 ⇒ الاثنان يتحركان معًا (+400)
        await using (var ctx = _pg.NewContext())
            await AccountingPgFixture.SeedPayableForAsync(ctx, supplier, 400m);

        var control1 = await DerivedAsync(Payable);
        var sub1 = await SumSupplierBalancesAsync();
        Assert.Equal(400m, Math.Round(control1 - controlBefore, 2));
        Assert.Equal(400m, Math.Round(sub1 - subBefore, 2));
        Assert.Equal(gapBefore, Math.Round(control1 - sub1, 2));

        // (2) قيد **يدوي** على 2200 بلا مستند مورد ⇒ الفرق = مبلغ القيد بالضبط
        const decimal manual = 250m;
        await using (var ctx = _pg.NewContext())
        {
            var apId = (await PostingAccountResolver.ResolveAsync(ctx, Payable)).Id;
            var invId = (await PostingAccountResolver.ResolveAsync(ctx, Inventory)).Id;
            await new JournalEntryService(ctx).CreateManualEntryAsync(new DateTime(2072, 2, 10),
                "BAL2-MANUAL-AP", "قيد يدوي على الرقابي بلا مورد (توثيق فرق)",
                new List<JournalEntryLegInput>
                {
                    new() { AccountId = invId, DebitAmount = manual },
                    new() { AccountId = apId, CreditAmount = manual }
                });
        }

        var control2 = await DerivedAsync(Payable);
        var sub2 = await SumSupplierBalancesAsync();
        Assert.Equal(manual, Math.Round(control2 - control1, 2));
        Assert.Equal(0m, Math.Round(sub2 - sub1, 2));
        Assert.Equal(manual, Math.Round(control2 - sub2, 2));       // فرق مُفسَّر بالكامل

        await AssertStoredEqualsDerivedAsync();
    }

    // ─────────── 6) شجرة الأرصدة لفترة = دفتر الأستاذ، وبلا تواريخ = الرصيد المخزَّن ───────────

    [LivePgFact]
    public async Task Probe6_Period_tree_matches_ledger_and_all_time_tree_matches_stored()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        await using (var ctx = _pg.NewContext())
        {
            var cashId = (await PostingAccountResolver.ResolveAsync(ctx, Cash)).Id;
            var equityId = await CreditLeafIdAsync(ctx);
            var js = new JournalEntryService(ctx);
            await js.CreateManualEntryAsync(new DateTime(2073, 2, 10), "BAL2-P1",
                "حركة فترة أولى", new List<JournalEntryLegInput>
                {
                    new() { AccountId = cashId, DebitAmount = 200m },
                    new() { AccountId = equityId, CreditAmount = 200m }
                });
            await js.CreateManualEntryAsync(new DateTime(2073, 8, 20), "BAL2-P2",
                "حركة فترة ثانية", new List<JournalEntryLegInput>
                {
                    new() { AccountId = cashId, DebitAmount = 300m },
                    new() { AccountId = equityId, CreditAmount = 300m }
                });
        }

        var cashIdForQuery = await AccountIdAsync(Cash);
        var from = new DateTime(2073, 1, 1);
        var to = new DateTime(2073, 6, 30);

        // (أ) دفتر الأستاذ لنفس الفترة
        await using (var ctx = _pg.NewContext())
        {
            var ledger = await new ReportService(ctx).GetAccountLedgerAsync(cashIdForQuery, from, to);

            // (ب) شجرة الأرصدة لنفس الفترة
            var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync(
                new AccountBalanceQuery { From = from, To = to });
            var node = tree.Flattened.Single(n => n.Code == Cash);

            // حركة الفترة في الشجرة == حركة دفتر الأستاذ في نفس المدى
            Assert.Equal(ledger.OpeningBalance, node.OpeningBalance);
            Assert.Equal(200m, node.PeriodDebit);
            Assert.Equal(0m, node.PeriodCredit);
            var ledgerPeriodDebit = ledger.Lines.Sum(l => l.Debit);
            Assert.Equal(ledgerPeriodDebit, node.PeriodDebit);
            Assert.Equal(ledger.ClosingBalance, node.OpeningBalance + node.PeriodDebit - node.PeriodCredit);

            // (ج) شجرة بلا تواريخ == الرصيد المخزَّن الكامل (المخزَّن رصيد جارٍ لكل الزمن)
            var allTime = await new AccountBalanceService(ctx).GetBalanceTreeAsync(new AccountBalanceQuery());
            var allNode = allTime.Flattened.Single(n => n.Code == Cash);
            Assert.Equal(Math.Round(allNode.NetBalance, 2), Math.Round(await StoredAsync(Cash), 2));

            // الإجماليات متوازنة في الحالتين
            Assert.Equal(allTime.TotalDebit, allTime.TotalCredit);
            Assert.Equal(tree.TotalDebit, tree.TotalCredit);
        }

        await AssertStoredEqualsDerivedAsync();
    }

    // ─────────── 7) المخزون: حركة افتتاحية بلا قيد، والبيع يُنشئ COGS بقيد ───────────

    [LivePgFact]
    public async Task Probe7_Opening_stock_has_no_entry_but_sale_cogs_does()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        var invBefore = await StoredAsync(Inventory);
        var cogsBefore = await StoredAsync(Cogs);
        var entriesBefore = await EntryCountAsync();

        // (1) صنف برصيد افتتاحي = **حركة مخزون** (بلا قيد بحسب الفحص الساكن)
        var itemId = await _pg.CreateItemAsync(
            "BAL2-INV-" + Guid.NewGuid().ToString("N")[..6], 20m, 12m, 100m, taxRate: 0m);

        Assert.Equal(entriesBefore, await EntryCountAsync());       // لا قيد جديد
        Assert.Equal(invBefore, await StoredAsync(Inventory));      // حساب المخزون لم يتحرك
        Assert.Equal(cogsBefore, await StoredAsync(Cogs));

        await using (var ctx = _pg.NewContext())
        {
            var item = await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId);
            Assert.True(item.CurrentStock > 0m,
                "الكمية الافتتاحية موجودة في المخزون فعليًا (حركة مخزون) بينما حساب المخزون لم يتحرك ⇒ فجوة مُقاسة");
        }

        // (2) بيع نقدي 10 وحدات ⇒ COGS: Dr 5100 / Cr 1300 (بقيد ومسار مركزي)
        var customer = await NewCustomerAsync("INV");
        await using (var ctx = _pg.NewContext())
        {
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customer, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 10m, UnitPrice = 20m } }
            });
        }

        var invAfter = await StoredAsync(Inventory);
        var cogsAfter = await StoredAsync(Cogs);
        var cogsDelta = Math.Round(cogsAfter - cogsBefore, 2);
        var invDelta = Math.Round(invAfter - invBefore, 2);

        Assert.True(cogsDelta > 0m, "البيع يجب أن يُنشئ قيد تكلفة بضاعة (COGS)");
        Assert.Equal(-cogsDelta, invDelta);                         // التناظر: المخزون المُنصرف = COGS
        Assert.Equal(120m, cogsDelta);                              // 10 وحدات × تكلفة 12 = 120
        Assert.Equal(-120m, invDelta);
        await AssertStoredEqualsDerivedAsync();
    }

    // ─────────── 9) قيد غير متوازن يُرفض مركزيًا ولا يترك أي أثر ───────────

    [LivePgFact]
    public async Task Probe9_Unbalanced_entry_is_rejected_centrally_without_any_write()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        var entriesBefore = await EntryCountAsync();
        var cashStoredBefore = await StoredAsync(Cash);

        await using (var ctx = _pg.NewContext())
        {
            var cashId = (await PostingAccountResolver.ResolveAsync(ctx, Cash)).Id;
            var equityId = await CreditLeafIdAsync(ctx);
            var js = new JournalEntryService(ctx);

            // مدين 100 مقابل دائن 40 ⇒ رفض مركزي قبل أي كتابة أو ترقيم
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                js.CreateManualEntryAsync(new DateTime(2074, 1, 5), "BAL2-UNBAL",
                    "قيد غير متوازن (يجب أن يُرفض)",
                    new List<JournalEntryLegInput>
                    {
                        new() { AccountId = cashId, DebitAmount = 100m },
                        new() { AccountId = equityId, CreditAmount = 40m }
                    }));

            Assert.Contains("غير متوازن", ex.Message);

            // ولا قيد بلا بنود
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                js.CreateManualEntryAsync(new DateTime(2074, 1, 5), "BAL2-EMPTY",
                    "قيد بلا بنود", new List<JournalEntryLegInput>()));
        }

        Assert.Equal(entriesBefore, await EntryCountAsync());     // لا قيد جديد
        Assert.Equal(cashStoredBefore, await StoredAsync(Cash));  // ولا أثر على الرصيد
        await AssertStoredEqualsDerivedAsync();
    }
}
