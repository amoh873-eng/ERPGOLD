using ERPSystem.Application.DTOs.Expenses;
using ERPSystem.Application.DTOs.Inventory;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Purchases;
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
/// المرحلة 5 — الاختبار المتكامل النهائي للنسخة التجريبية (2026-10-25).
///
/// السلسلة المُفحوصة لكل نوع مستند: **إدخال المستند ⇒ القيد ⇒ الأستاذ العام ⇒ ميزان المراجعة ⇒ تقارير الموديول**،
/// مع فحص صحة المدين/الدائن (بنود القيد الفعلية من قاعدة البيانات لا من مخرجات الخدمة)، والكميات والتكاليف،
/// والأرصدة المساعدة (عملاء/موردين/مخزون)، ومنع التكرار، ومطابقة الحسابات الرقابية مع السجلات المساعدة.
///
/// كل الحركات عبر الخدمات الرسمية على قاعدة اختبار معزولة تُنشأ وتُحذف ذاتيًا؛ لا يُلمس أي قاعدة منشورة.
/// </summary>
public class AccountingEndToEndDocumentMatrixTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public AccountingEndToEndDocumentMatrixTests(AccountingPgFixture pg) => _pg = pg;

    // تواريخ داخل نطاق مقبول (1900..2100) وفي نفس الشهر، وبعيدة عن «اليوم» كي لا تتداخل
    // مع نافذة ورديات نقطة البيع (التي تعمل بمقياس اليوم).
    private static readonly DateTime D1 = new(2086, 3, 2);
    private static readonly DateTime D2 = new(2086, 3, 3);

    internal sealed class AllowAllVouchers : IVoucherAuthorization
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
        public string? CurrentUserName() => "e2e-matrix";
    }

    private static string Money(decimal v)
        => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Legs(params string[] legs)
        => legs.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>بنود القيد الفعلية مخزَّنةً (كود الحساب:جهة:مبلغ) — تُقرأ من الجداول، لا من كائنات الخدمة.</summary>
    private async Task<string[]> EntryLegsAsync(Guid entryId)
    {
        await using var ctx = _pg.NewContext();
        var rows = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.JournalEntryId == entryId && !l.IsDeleted)
            .Join(ctx.Set<Account>().AsNoTracking().Where(a => !a.IsDeleted),
                l => l.AccountId, a => a.Id,
                (l, a) => new { a.Code, l.DebitAmount, l.CreditAmount })
            .ToListAsync();

        return rows
            .SelectMany(r => new[]
            {
                r.DebitAmount != 0m ? $"{r.Code}:D:{Money(r.DebitAmount)}" : null,
                r.CreditAmount != 0m ? $"{r.Code}:C:{Money(r.CreditAmount)}" : null
            })
            .Where(s => s is not null).Select(s => s!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertLegs(string label, string[] expected, string[] actual)
        => Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"{label}\n  المتوقع: {string.Join(" | ", expected)}\n  الفعلي  : {string.Join(" | ", actual)}");

    /// <summary>صافي الأثر على حساب من **دفتر القيود** (مدين − دائن).</summary>
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

    /// <summary>
    /// يؤكد صافي الحساب **دفتريًا** (مدين − دائن) ومساواته للرصيد المخزَّن (Account.CurrentBalance)
    /// وفق اصطلاح طبيعة الحساب المخزَّن: مدين ⇒ (مدين − دائن) · دائن ⇒ (دائن − مدين).
    /// </summary>
    private async Task AssertAccountAsync(string code, decimal expectedDebitNet)
    {
        await using var ctx = _pg.NewContext();
        var account = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted)
            .Select(a => new { a.CurrentBalance, a.NormalBalance }).FirstAsync();
        var ledger = await LedgerNetAsync(code);
        var expectedStored = account.NormalBalance == NormalBalance.Debit
            ? expectedDebitNet
            : -expectedDebitNet;

        Assert.True(Math.Round(ledger, 2) == Math.Round(expectedDebitNet, 2),
            $"الحساب {code}: المتوقع صافي مدين {Money(expectedDebitNet)} — الدفتري الفعلي {Money(ledger)}");
        Assert.True(Math.Round(account.CurrentBalance, 2) == Math.Round(expectedStored, 2),
            $"الحساب {code}: المخزَّن {Money(account.CurrentBalance)} لا يطابق المتوقع بطبيعة الحساب " +
            $"({Money(expectedStored)}) — الدفتري {Money(ledger)}");
    }

    private async Task<int> EntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    private async Task<decimal> PartyBalanceAsync(bool customer)
    {
        await using var ctx = _pg.NewContext();
        return customer
            ? await ctx.Set<Customer>().AsNoTracking().Where(c => c.Id == _pg.CustomerId)
                .Select(c => c.CurrentBalance).FirstAsync()
            : await ctx.Set<Supplier>().AsNoTracking().Where(s => s.Id == _pg.SupplierId)
                .Select(s => s.CurrentBalance).FirstAsync();
    }

    private async Task<decimal> StockAsync(Guid itemId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId)
            .Select(i => i.CurrentStock).FirstAsync();
    }

    /// <summary>ينشئ صنفًا برصيد افتتاحي (حركة مخزون حقيقية — لا كتابة مباشرة على الرصيد).</summary>
    private async Task<Guid> NewItemWithStockAsync(string suffix, decimal quantity, decimal unitCost,
        decimal salePrice, decimal taxRate = 0m)
    {
        await using var ctx = _pg.NewContext();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "E2E-" + suffix,
            NameAr = "صنف اختبار متكامل " + suffix,
            CategoryId = _pg.CategoryId,
            UnitId = _pg.UnitId,
            SalePrice = salePrice,
            CostPrice = unitCost,
            TaxRate = taxRate,
            TracksBatches = false,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        if (quantity != 0m)
        {
            await new StockMovementService(ctx, new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreateStockMovementDto
                {
                    ItemId = item.Id,
                    WarehouseId = _pg.WarehouseId,
                    MovementType = (int)MovementType.OpeningBalance,
                    Quantity = quantity,
                    UnitCost = unitCost,
                    MovementDate = D1,
                    ReferenceNumber = "E2E-OPEN-" + suffix
                });
        }

        return item.Id;
    }

    private async Task<Guid> NewExpenseCategoryAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var existing = await ctx.Set<ExpenseCategory>().AsNoTracking()
            .Where(c => c.Code == code).Select(c => c.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty) return existing;

        var accountId = (await PostingAccountResolver.ResolveAsync(ctx, "5200")).Id;
        var category = new ExpenseCategory
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "بند مصروف اختبار متكامل",
            AccountId = accountId,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(category);
        await ctx.SaveChangesAsync();
        return category.Id;
    }

    // ═══════════ 1) مصفوفة أنواع المستندات: من الإدخال حتى التقارير ═══════════

    /// <summary>
    /// يمرّر أنواع المستندات المطلوبة في سيناريو واحد مترابط ويتحقق من:
    ///   (أ) بنود كل قيد فعلية (مدين/دائن + المبلغ) لكل مستند — من الجداول لا من مخرجات الخدمة،
    ///   (ب) عدد القيود المتوقع (لا قيد مزدوج ولا قيد ناقص)،
    ///   (ج) الكميات والتكاليف (COGS = كمية × تكلفة اللقطة)،
    ///   (د) الأرصدة المساعدة (عميل/مورد) والأرصدة المخزَّنة مقابل دفتر القيود،
    ///   (هـ) ميزان المراجعة وتقارير الموديول،
    ///   (و) مطابقة الرقابي (1200/2200) مع السجل المساعد بلا فرق غير مُفسَّر ولا بند غير مُنسَب.
    /// </summary>
    [LivePgFact]
    public async Task Document_matrix_posts_correct_debit_credit_and_is_traceable_to_reports()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        var run = Guid.NewGuid().ToString("N")[..6];
        const decimal cost = 10m;
        const decimal price = 20m;
        var itemSale = await NewItemWithStockAsync("S-" + run, 100m, cost, price);
        var itemBuy = await NewItemWithStockAsync("B-" + run, 0m, 12m, 25m);
        var expenseCategoryId = await NewExpenseCategoryAsync("E2E-EXP");

        var entriesBefore = await EntryCountAsync();
        // الرصيد النقدي الافتتاحي لبيئة الاختبار (SeedCashAsync) يُضاف إلى صافي 1100
        var cashBefore = await LedgerNetAsync("1100");
        Assert.Equal(0m, await PartyBalanceAsync(customer: true));
        Assert.Equal(0m, await PartyBalanceAsync(customer: false));

        // ── (1) فاتورة بيع نقدية 10 × 20 = 200 ⇒ قيد البيع (1100/4100) + قيد التكلفة (5100/1300)
        Guid siCashId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = D1,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemSale, Quantity = 10m, UnitPrice = price }
                }
            });
            siCashId = created.Id;
        }

        Guid siCashSaleEntry, siCashCogsEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => i.Id == siCashId);
            Assert.Equal(200m, row.TotalAmount);
            Assert.Equal(200m, row.PaidAmount);
            Assert.Equal(DocumentStatus.Posted, row.Status);
            siCashSaleEntry = row.SalesJournalEntryId!.Value;
            siCashCogsEntry = row.CogsJournalEntryId!.Value;
        }
        AssertLegs("بيع نقدي 200: قيد البيع", Legs("1100:D:200.00", "4100:C:200.00"),
            await EntryLegsAsync(siCashSaleEntry));
        AssertLegs("بيع نقدي 200: قيد التكلفة", Legs("5100:D:100.00", "1300:C:100.00"),
            await EntryLegsAsync(siCashCogsEntry));
        Assert.Equal(90m, await StockAsync(itemSale));

        // ── (2) فاتورة بيع آجلة 5 × 20 = 100 ⇒ 1200/4100 + تكلفة 50 + رصيد عميل +100
        Guid siCreditId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = D1,
                InvoiceType = (int)SalesInvoiceType.OnAccount,
                PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemSale, Quantity = 5m, UnitPrice = price }
                }
            });
            siCreditId = created.Id;
        }

        Guid siCreditSaleEntry, siCreditCogsEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => i.Id == siCreditId);
            Assert.Equal(100m, row.TotalAmount);
            Assert.Equal(0m, row.PaidAmount);
            siCreditSaleEntry = row.SalesJournalEntryId!.Value;
            siCreditCogsEntry = row.CogsJournalEntryId!.Value;
        }
        AssertLegs("بيع آجل 100: قيد البيع", Legs("1200:D:100.00", "4100:C:100.00"),
            await EntryLegsAsync(siCreditSaleEntry));
        AssertLegs("بيع آجل 100: قيد التكلفة", Legs("5100:D:50.00", "1300:C:50.00"),
            await EntryLegsAsync(siCreditCogsEntry));
        Assert.Equal(100m, await PartyBalanceAsync(customer: true));
        Assert.Equal(85m, await StockAsync(itemSale));

        // ── (3) مردود مبيعات 2 × 20 = 40 على الفاتورة الآجلة ⇒ 4110/1200 + عكس تكلفة 20
        Guid salesReturnId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new SalesReturnService(ctx, new JournalEntryService(ctx))
                .CreateAsync(new CreateSalesReturnDto
                {
                    SalesInvoiceId = siCreditId,
                    WarehouseId = _pg.WarehouseId,
                    ReturnDate = D1,
                    Lines = new List<CreateSalesReturnLineDto>
                    {
                        new() { ItemId = itemSale, Quantity = 2m, UnitPrice = price }
                    }
                });
            salesReturnId = created.Id;
        }

        Guid srSaleEntry, srCogsEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<SalesReturn>().AsNoTracking().FirstAsync(r => r.Id == salesReturnId);
            Assert.Equal(40m, row.TotalAmount);
            Assert.Equal(DocumentStatus.Posted, row.Status);
            srSaleEntry = row.SalesJournalEntryId!.Value;
            srCogsEntry = row.CogsJournalEntryId!.Value;
        }
        AssertLegs("مردود مبيعات 40: قيد المردود", Legs("4110:D:40.00", "1200:C:40.00"),
            await EntryLegsAsync(srSaleEntry));
        AssertLegs("مردود مبيعات 40: عكس التكلفة", Legs("1300:D:20.00", "5100:C:20.00"),
            await EntryLegsAsync(srCogsEntry));
        Assert.Equal(60m, await PartyBalanceAsync(customer: true));
        Assert.Equal(87m, await StockAsync(itemSale));

        // ── (4) فاتورة نقطة بيع نقدية 3 × 20 = 60 ⇒ 1105/4100 + تكلفة 30 (درج الكاش لا الخزنة)
        Guid siPosId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = D1,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                IsPos = true,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemSale, Quantity = 3m, UnitPrice = price }
                }
            });
            siPosId = created.Id;
        }

        Guid posSaleEntry, posCogsEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<SalesInvoice>().AsNoTracking().FirstAsync(i => i.Id == siPosId);
            Assert.True(row.IsPos);
            Assert.Equal(60m, row.PaidAmount);
            Assert.Equal(DocumentStatus.Posted, row.Status);
            posSaleEntry = row.SalesJournalEntryId!.Value;
            posCogsEntry = row.CogsJournalEntryId!.Value;
        }
        AssertLegs("بيع نقطة بيع 60: قيد البيع", Legs("1105:D:60.00", "4100:C:60.00"),
            await EntryLegsAsync(posSaleEntry));
        AssertLegs("بيع نقطة بيع 60: قيد التكلفة", Legs("5100:D:30.00", "1300:C:30.00"),
            await EntryLegsAsync(posCogsEntry));
        Assert.Equal(60m, await PartyBalanceAsync(customer: true));   // البيع النقدي لا يُنشئ ذمة
        Assert.Equal(84m, await StockAsync(itemSale));

        // ── (5) فاتورة مشتريات نقدية 3 × 12 = 36 ⇒ 1300/1100
        Guid piCashId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx),
                    new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreatePurchaseInvoiceDto
                {
                    SupplierId = _pg.SupplierId,
                    WarehouseId = _pg.WarehouseId,
                    InvoiceDate = D1,
                    InvoiceType = (int)PurchaseInvoiceType.Cash,
                    PaymentMethod = CreatePurchaseInvoiceDto.PaymentCash,
                    Lines = new List<CreatePurchaseInvoiceLineDto>
                    {
                        new() { ItemId = itemBuy, Quantity = 3m, UnitCost = 12m }
                    }
                });
            piCashId = created.Id;
        }

        Guid piCashEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<PurchaseInvoice>().AsNoTracking().FirstAsync(i => i.Id == piCashId);
            Assert.Equal(36m, row.TotalAmount);
            piCashEntry = row.JournalEntryId!.Value;
        }
        AssertLegs("شراء نقدي 36", Legs("1300:D:36.00", "1100:C:36.00"), await EntryLegsAsync(piCashEntry));
        Assert.Equal(3m, await StockAsync(itemBuy));

        // ── (6) فاتورة مشتريات آجلة 4 × 12 = 48 ⇒ 1300/2200 + رصيد مورد +48
        Guid piCreditId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx),
                    new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreatePurchaseInvoiceDto
                {
                    SupplierId = _pg.SupplierId,
                    WarehouseId = _pg.WarehouseId,
                    InvoiceDate = D1,
                    InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                    PaymentMethod = CreatePurchaseInvoiceDto.PaymentOnAccount,
                    Lines = new List<CreatePurchaseInvoiceLineDto>
                    {
                        new() { ItemId = itemBuy, Quantity = 4m, UnitCost = 12m }
                    }
                });
            piCreditId = created.Id;
        }

        Guid piCreditEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<PurchaseInvoice>().AsNoTracking().FirstAsync(i => i.Id == piCreditId);
            Assert.Equal(48m, row.TotalAmount);
            piCreditEntry = row.JournalEntryId!.Value;
        }
        AssertLegs("شراء آجل 48", Legs("1300:D:48.00", "2200:C:48.00"), await EntryLegsAsync(piCreditEntry));
        Assert.Equal(48m, await PartyBalanceAsync(customer: false));
        Assert.Equal(7m, await StockAsync(itemBuy));

        // ── (7) مردود مشتريات 1 × 12 = 12 (خصم على الحساب) ⇒ 2200/1300 + رصيد مورد 36
        Guid purchaseReturnId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new PurchaseReturnService(ctx, new JournalEntryService(ctx))
                .CreateAsync(new CreatePurchaseReturnDto
                {
                    PurchaseInvoiceId = piCreditId,
                    WarehouseId = _pg.WarehouseId,
                    ReturnDate = D1,
                    SettlementType = (int)PurchaseReturnSettlementType.OnAccountCredit,
                    Lines = new List<CreatePurchaseReturnLineDto>
                    {
                        new() { ItemId = itemBuy, Quantity = 1m, UnitCost = 12m }
                    }
                });
            purchaseReturnId = created.Id;
        }

        Guid prEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<PurchaseReturn>().AsNoTracking().FirstAsync(r => r.Id == purchaseReturnId);
            Assert.Equal(12m, row.TotalAmount);
            prEntry = row.JournalEntryId!.Value;
        }
        AssertLegs("مردود مشتريات 12", Legs("2200:D:12.00", "1300:C:12.00"), await EntryLegsAsync(prEntry));
        Assert.Equal(36m, await PartyBalanceAsync(customer: false));
        Assert.Equal(6m, await StockAsync(itemBuy));

        // ── (8) سند قبض نقدي 60 من العميل ⇒ 1100/1200 + رصيد عميل 0
        Guid receiptId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAllVouchers());
            var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = _pg.CustomerId,
                VoucherDate = D1,
                PaymentMethod = 1,
                Amount = 60m
            });
            receiptId = draft.Id;
            await svc.ApproveAsync(receiptId);
            await svc.PostAsync(receiptId);
        }

        Guid receiptEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<ReceiptVoucher>().AsNoTracking().FirstAsync(v => v.Id == receiptId);
            Assert.Equal(VoucherStatus.Posted, row.Status);
            receiptEntry = row.JournalEntryId!.Value;
        }
        AssertLegs("سند قبض 60", Legs("1100:D:60.00", "1200:C:60.00"), await EntryLegsAsync(receiptEntry));
        Assert.Equal(0m, await PartyBalanceAsync(customer: true));

        // ── (9) سند صرف نقدي 30 للمورد بتخصيص على الفاتورة الآجلة ⇒ 2200/1100 + رصيد مورد 6
        Guid paymentId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = new PaymentVoucherService(ctx, new JournalEntryService(ctx), new AllowAllVouchers());
            var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = _pg.SupplierId,
                VoucherDate = D1,
                PaymentMethod = 1,
                Amount = 30m
            });
            paymentId = draft.Id;
            await svc.ApproveAsync(paymentId);
            await svc.PostAsync(paymentId, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = piCreditId, Amount = 30m }
            });
        }

        Guid paymentEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<PaymentVoucher>().AsNoTracking().FirstAsync(v => v.Id == paymentId);
            Assert.Equal(VoucherStatus.Posted, row.Status);
            paymentEntry = row.JournalEntryId!.Value;
        }
        AssertLegs("سند صرف 30", Legs("2200:D:30.00", "1100:C:30.00"), await EntryLegsAsync(paymentEntry));
        Assert.Equal(6m, await PartyBalanceAsync(customer: false));

        // ── (10) مصروف نقدي 25 على بند مرتبط بحساب 5200 ⇒ 5200/1100
        Guid expenseId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new ExpenseService(ctx, new JournalEntryService(ctx))
                .CreateAsync(new CreateExpenseEntryDto
                {
                    ExpenseCategoryId = expenseCategoryId,
                    ExpenseDate = D1,
                    Amount = 25m,
                    PaymentMethod = (int)PaymentMethod.Cash,
                    Description = "مصروف اختبار متكامل"
                });
            expenseId = created.Id;
        }

        Guid expenseEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<ExpenseEntry>().AsNoTracking().FirstAsync(e => e.Id == expenseId);
            Assert.Equal(DocumentStatus.Posted, row.Status);
            expenseEntry = row.JournalEntryId!.Value;
        }
        AssertLegs("مصروف 25", Legs("5200:D:25.00", "1100:C:25.00"), await EntryLegsAsync(expenseEntry));

        // ── (11) إتلاف مخزون 2 × تكلفة 10 = 20 ⇒ 5300/1300
        Guid writeOffId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new StockWriteOffService(ctx, new JournalEntryService(ctx))
                .CreateAsync(new CreateStockWriteOffDto
                {
                    ItemId = itemSale,
                    WarehouseId = _pg.WarehouseId,
                    Quantity = 2m,
                    Reason = (int)StockWriteOffReason.Expired,
                    Date = D1,
                    Notes = "إتلاف اختبار متكامل"
                }, "e2e-matrix");
            writeOffId = created.Id;
        }

        Guid writeOffEntry;
        await using (var ctx = _pg.NewContext())
        {
            var row = await ctx.Set<StockWriteOff>().AsNoTracking().FirstAsync(w => w.Id == writeOffId);
            Assert.Equal(2m, row.Quantity);
            Assert.Equal(10m, row.UnitCost);
            writeOffEntry = row.JournalEntryId;
        }
        AssertLegs("إتلاف مخزون 20", Legs("5300:D:20.00", "1300:C:20.00"), await EntryLegsAsync(writeOffEntry));

        // ═══════════ التحقق الشامل: عدد القيود + الأرصدة + التقارير + المطابقة ═══════════

        // قيود السيناريو: 2+2+2+2 (بيع نقدي/آجل/مردود/نقطة بيع) + 1+1+1 (مشتريات نقدي/آجل/مردود)
        //                + 1+1 (سند قبض/صرف) + 1 (مصروف) + 1 (إتلاف) = 15 قيدًا بالضبط
        Assert.Equal(entriesBefore + 15, await EntryCountAsync());

        Assert.Equal(82m, await StockAsync(itemSale));
        Assert.Equal(6m, await StockAsync(itemBuy));

        // الأرصدة الدفترية المتوقعة في نهاية السيناريو (مدين موجب)
        await AssertAccountAsync("1100", cashBefore + 169m);   // 200 − 36 + 60 − 30 − 25 + الرصيد الافتتاحي
        await AssertAccountAsync("1105", 60m);    // بيع نقطة البيع
        await AssertAccountAsync("1200", 0m);     // 100 − 40 − 60
        // 1300: أثر **الحركات المستندية** فقط (شراء +36/+48 · مردود مشتريات −12 · تكلفة بيع −100/−50/−30
        //      · عكس تكلفة مردود +20 · إتلاف −20 = −108).
        // فجوة موثَّقة (F-B): حركة المخزون من نوع «رصيد افتتاحي» تُسجَّل كميةً وتكلفةً على الصنف
        // **ولا تُرحّل قيدًا على 1300** ⇒ قيمة المخزون (كمية × تكلفة = 892) لا تساوي رصيد 1300 الدفتري.
        // المسار الرسمي لافتتاحي المخزون هو دفعة الأرصدة الافتتاحية (تُرحّل قيدًا + حركة معًا).
        await AssertAccountAsync("1300", -108m);
        await AssertAccountAsync("2200", -6m);    // 48 − 12 − 30 (رصيد دائن)
        await AssertAccountAsync("4100", -360m);  // إيراد البيع
        await AssertAccountAsync("4110", 40m);    // مردودات البيع
        await AssertAccountAsync("5100", 160m);   // تكلفة المبيعات
        await AssertAccountAsync("5200", 25m);    // المصروف
        await AssertAccountAsync("5300", 20m);    // الإتلاف

        // تقارير الموديول: كل مستند يظهر في تقريره وبالمبلغ الصحيح
        await using (var ctx = _pg.NewContext())
        {
            var invoices = await AccountingPgFixture.NewSalesService(ctx).GetInvoicesAsync();
            var mine = invoices.Where(i => i.Id == siCashId || i.Id == siCreditId || i.Id == siPosId).ToList();
            Assert.Equal(3, mine.Count);
            Assert.Equal(200m, mine.Single(i => i.Id == siCashId).TotalAmount);
            Assert.Equal(0m, mine.Single(i => i.Id == siCreditId).PaidAmount);
            Assert.Equal(60m, mine.Single(i => i.Id == siPosId).TotalAmount);

            var returns = await new SalesReturnService(ctx, new JournalEntryService(ctx)).GetReturnsAsync();
            Assert.Equal(40m, returns.Single(r => r.Id == salesReturnId).TotalAmount);

            var purchases = await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx),
                new AccountingPgFixture.AllowAllManualPrice()).GetInvoicesAsync();
            Assert.Equal(36m, purchases.Single(p => p.Id == piCashId).TotalAmount);
            Assert.Equal(48m, purchases.Single(p => p.Id == piCreditId).TotalAmount);

            var expenses = await new ExpenseService(ctx, new JournalEntryService(ctx)).GetEntriesAsync();
            Assert.Equal(25m, expenses.Single(e => e.Id == expenseId).Amount);

            var writeOff = await new StockWriteOffService(ctx, new JournalEntryService(ctx)).GetByIdAsync(writeOffId);
            Assert.NotNull(writeOff);
            Assert.Equal(2m, writeOff!.Quantity);
            Assert.Equal(10m, writeOff.UnitCost);

            var receipt = await new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAllVouchers())
                .GetByIdAsync(receiptId);
            Assert.Equal((int)VoucherStatus.Posted, receipt!.Status);
            Assert.NotNull(receipt.JournalEntryId);

            var payment = await new PaymentVoucherService(ctx, new JournalEntryService(ctx), new AllowAllVouchers())
                .GetByIdAsync(paymentId);
            Assert.Equal((int)VoucherStatus.Posted, payment!.Status);
            Assert.NotNull(payment.JournalEntryId);

            // ميزان المراجعة: متوازن، وكل حساب بأثر صافٍ مطابق للدفتر
            var trial = await new ReportService(ctx).GetTrialBalanceAsync();
            Assert.True(trial.IsBalanced,
                $"ميزان المراجعة غير متوازن: مدين={trial.TotalDebit:N2} دائن={trial.TotalCredit:N2}");

            foreach (var (code, expected) in new (string Code, decimal Expected)[]
                     {
                         ("1100", cashBefore + 169m), ("1105", 60m), ("1200", 0m), ("1300", -108m), ("2200", -6m),
                         ("4100", -360m), ("4110", 40m), ("5100", 160m), ("5200", 25m), ("5300", 20m)
                     })
            {
                var line = trial.Lines.FirstOrDefault(l => l.AccountCode == code);
                Assert.True(line is not null, $"ميزان المراجعة لا يحتوي الحساب {code}");
                Assert.True(Math.Round(line!.Balance, 2) == Math.Round(expected, 2),
                    $"ميزان المراجعة — {code}: المتوقع {Money(expected)} والفعلي {Money(line.Balance)}");
            }

            // مطابقة الرقابي مع السجل المساعد: 1200/2200 بلا فرق وبلا بند غير مُنسَب
            var recon = await new SubLedgerReconciliationService(ctx).GetReportAsync();
            foreach (var code in new[] { "1200", "2200" })
            {
                var row = recon.Rows.Single(r => r.AccountCode == code);
                Assert.True(Math.Abs(row.Difference) < 0.005m,
                    $"{code}: فرق رقابي/تفصيلي {Money(row.Difference)}");
                Assert.True(Math.Abs(row.UnexplainedAmount) < 0.005m,
                    $"{code}: فرق غير مُفسَّر {Money(row.UnexplainedAmount)}");
            }
            Assert.True(recon.UnattributedControlLines.Count == 0,
                "بنود رقابية غير مُسندة:\n" + string.Join("\n", recon.UnattributedControlLines.Select(
                    l => $"{l.AccountCode} {l.EntryNumber} {l.EntryTypeNameAr} {l.Reference} {Money(l.SignedAmount)}")));
            Assert.Equal(0m, Math.Round(recon.TotalUnattributedAmount, 2));
        }

        // ── منع التكرار: إعادة ترحيل سند قبض مُرحَّل ⇒ سلوك Idempotent مُوثَّق
        //    (يُعاد نفس السند المرحّل بلا قيد ثانٍ ولا تغيير رصيد — لا يرمي استثناءً بطبيعة التصميم)
        var beforeGuard = await EntryCountAsync();
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAllVouchers());
            var again = await svc.PostAsync(receiptId);
            Assert.Equal((int)VoucherStatus.Posted, again.Status);
            Assert.Equal(receiptId, again.Id);
        }
        Assert.Equal(beforeGuard, await EntryCountAsync());
        Assert.Equal(0m, await PartyBalanceAsync(customer: true));

        // ── مردود يتجاوز الكمية المفوترة (3 في فاتورة نقطة البيع) ⇒ مرفوض بلا أثر محاسبي ولا مخزني
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
                {
                    SalesInvoiceId = siPosId,
                    WarehouseId = _pg.WarehouseId,
                    ReturnDate = D1,
                    Lines = new List<CreateSalesReturnLineDto>
                    {
                        new() { ItemId = itemSale, Quantity = 6m, UnitPrice = price }
                    }
                }));
        }
        Assert.Equal(beforeGuard, await EntryCountAsync());
        Assert.Equal(82m, await StockAsync(itemSale));

        // كل حساب: الرصيد المخزَّن == دفتر القيود، ولا قيود غير متوازنة ولا أرقام مكررة
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
