using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// إصلاح فجوات المرحلة الثانية/الثالثة (2026-10-25) — اختبارات الإصلاح الفعلية:
///
///  1) **الأرصدة الافتتاحية التفصيلية**: بند على 1200 يلزم بعميل، وعلى 2200 يلزم بمورد،
///     وعلى 1300 يلزم بصنف ومخزن وكمية وتكلفة — فيبقى الرصيد الرقابي **مساويًا** لمجموع الأرصدة
///     التفصيلية بالبناء لا بالتسوية.
///  2) **المخزون الافتتاحي**: لكل بند صنف تُنشأ حركة مخزون افتتاحية وتُحدَّث ذاكرة الصنف
///     (CurrentStock ومتوسط التكلفة المرجّح) وتتحرك قيمة القيد على 1300 بنفس المبلغ.
///  3) **العكس**: يعيد أرصدة الأطراف ويُخرج الكميات (MovementType.AdjustmentOut) ويفشل مغلقًا
///     إذا استُهلك المخزون — بلا أي تعديل على أي قيد تاريخي.
///  4) **قفل الفترات المحاسبية**: الفترة المغلقة ترفض أي ترحيل جديد من **كل** المسارات
///     (قيد يدوي/فاتورة بيع/رصيد افتتاحي) عبر الحارس المركزي، ولا تعدّل أي قيد قائم.
///  5) **مطابقة الحسابات الرقابية**: خدمة المطابقة (قراءة فقط) تعرض الفرق ومصدره ولا تسوّي شيئًا.
///
/// كل ذلك على قاعدة PostgreSQL معزولة تُنشأ وتُحذف ذاتيًا عبر <see cref="AccountingPgFixture"/>.
/// </summary>
public class AccountingOpeningDetailAndPeriodLockTests : IClassFixture<AccountingPgFixture>
{
    private const string Cash = "1100";
    private const string Receivable = "1200";
    private const string Inventory = "1300";
    private const string Payable = "2200";
    private const string SalesTax = "2100";

    private readonly AccountingPgFixture _pg;

    public AccountingOpeningDetailAndPeriodLockTests(AccountingPgFixture pg) => _pg = pg;

    // ───────────────────────── تصاريح الفترات (اختبار) ─────────────────────────

    private sealed class AllowAllPeriods : IAccountingPeriodAuthorization
    {
        public bool CanView() => true;
        public bool CanClose() => true;
        public bool CanReopen() => true;
    }

    private sealed class DenyAllPeriods : IAccountingPeriodAuthorization
    {
        public bool CanView() => false;
        public bool CanClose() => false;
        public bool CanReopen() => false;
    }

    private static AccountingPeriodService Periods(AppDbContext ctx, bool allow = true)
        => new(ctx, allow ? new AllowAllPeriods() : new DenyAllPeriods());

    // ───────────────────────── أدوات ─────────────────────────

    private static CreateOpeningBalanceBatchDto Batch(int year, string reference,
        params OpeningBalanceLineInput[] lines) => new()
    {
        FiscalYear = year,
        EntryDate = new DateTime(year, 1, 10),
        Reference = reference,
        Description = $"فحص تفصيل الأرصدة {year}",
        Lines = lines.ToList()
    };

    private static OpeningBalanceLineInput PartyLine(string account, decimal debit, decimal credit,
        string? customerCode = null, string? supplierCode = null) => new()
    {
        AccountCode = account,
        DebitAmount = debit,
        CreditAmount = credit,
        CustomerCode = customerCode,
        SupplierCode = supplierCode
    };

    private static OpeningBalanceLineInput ItemLine(string itemCode, decimal quantity, decimal unitCost,
        decimal? debit = null) => new()
    {
        AccountCode = Inventory,
        DebitAmount = debit ?? 0m,
        ItemCode = itemCode,
        WarehouseCode = AccountingPgFixture.WarehouseCode,
        Quantity = quantity,
        UnitCost = unitCost
    };

    private async Task<Guid> NewItemAsync(string suffix)
    {
        await using var ctx = _pg.NewContext();
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "OD-" + suffix,
            NameAr = "صنف فحص " + suffix,
            CategoryId = _pg.CategoryId,
            UnitId = _pg.UnitId,
            SalePrice = 20m,
            CostPrice = 0m,
            TaxRate = 0m,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private async Task<decimal> AccountBalanceAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code).Select(a => a.CurrentBalance).FirstAsync();
    }

    private async Task<decimal> CustomerBalanceAsync(Guid id)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.CurrentBalance).FirstAsync();
    }

    private async Task<decimal> SupplierBalanceAsync(Guid id)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Supplier>().AsNoTracking()
            .Where(s => s.Id == id).Select(s => s.CurrentBalance).FirstAsync();
    }

    private async Task<(decimal Stock, decimal Cost)> ItemStateAsync(Guid id)
    {
        await using var ctx = _pg.NewContext();
        var item = await ctx.Set<Item>().AsNoTracking()
            .Where(i => i.Id == id).Select(i => new { i.CurrentStock, i.CostPrice }).FirstAsync();
        return (item.CurrentStock, item.CostPrice);
    }

    private async Task<int> EntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    private async Task<SubLedgerReconciliationRowDto> ReconciliationRowAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var report = await new SubLedgerReconciliationService(ctx).GetReportAsync();
        return report.Rows.Single(r => r.AccountCode == code);
    }

    // ═══════════════ 1) تفصيل الأرصدة الافتتاحية: الرقابي = مجموع التفصيل ═══════════════

    [LivePgFact]
    public async Task Party_and_item_opening_balances_create_detail_and_keep_control_equal()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2060;
        var itemId = await NewItemAsync("A");

        var customerBefore = await CustomerBalanceAsync(_pg.CustomerId);
        var supplierBefore = await SupplierBalanceAsync(_pg.SupplierId);
        var receivableBefore = await AccountBalanceAsync(Receivable);
        var payableBefore = await AccountBalanceAsync(Payable);
        var inventoryBefore = await AccountBalanceAsync(Inventory);
        var receivableDifferenceBefore = (await ReconciliationRowAsync(Receivable)).Difference;
        var payableDifferenceBefore = (await ReconciliationRowAsync(Payable)).Difference;

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            // Dr 1200 (عميل) 1000 · Dr 1300 (صنف 100×12=1200) · Cr 2200 (مورد) 400 · Cr 2100 1800 ⇒ متوازن
            var draft = await svc.CreateDraftAsync(Batch(year, $"OD-PARTY-{year}",
                PartyLine(Receivable, 1000m, 0m, customerCode: AccountingPgFixture.CustomerCode),
                ItemLine("OD-A", 100m, 12m),
                PartyLine(Payable, 0m, 400m, supplierCode: AccountingPgFixture.SupplierCode),
                PartyLine(SalesTax, 0m, 1800m)), "tester");

            var posted = await svc.PostAsync(draft.Id, "tester");
            batchId = posted.Id;

            // البنود تعرض تفصيلها الصحيح (تدقيق)
            Assert.Equal("عميل", posted.Lines.Single(l => l.AccountCode == Receivable).DetailKindAr);
            Assert.Equal("مورد", posted.Lines.Single(l => l.AccountCode == Payable).DetailKindAr);
            var itemLine = posted.Lines.Single(l => l.AccountCode == Inventory);
            Assert.Equal("صنف مخزني", itemLine.DetailKindAr);
            Assert.Equal(100m, itemLine.Quantity);
            Assert.Equal(12m, itemLine.UnitCost);
            Assert.Equal(1200m, itemLine.Value);
            Assert.True(itemLine.HasStockMovement);
        }

        // (أ) الأرصدة التفصيلية تحرّكت
        Assert.Equal(customerBefore + 1000m, await CustomerBalanceAsync(_pg.CustomerId));
        Assert.Equal(supplierBefore + 400m, await SupplierBalanceAsync(_pg.SupplierId));

        // (ب) الرقابي تحرّك بنفس مقدار التفصيل ⇒ لا فرق
        Assert.Equal(receivableBefore + 1000m, await AccountBalanceAsync(Receivable));
        Assert.Equal(payableBefore + 400m, await AccountBalanceAsync(Payable));

        // (ج) المخزون: كمية + تكلفة + قيمة قيد متناظرة + حركة مخزون افتتاحية
        Assert.Equal(inventoryBefore + 1200m, await AccountBalanceAsync(Inventory));
        var item = await ItemStateAsync(itemId);
        Assert.Equal(100m, item.Stock);
        Assert.Equal(12m, item.Cost);

        await using (var ctx = _pg.NewContext())
        {
            var movements = await ctx.Set<StockMovement>().AsNoTracking()
                .Where(m => m.ItemId == itemId && !m.IsDeleted).ToListAsync();
            var opening = movements.Single(m => m.MovementType == MovementType.OpeningBalance);
            Assert.Equal(100m, opening.Quantity);
            Assert.Equal(12m, opening.UnitCost);
            Assert.Equal(1200m, opening.Quantity * opening.UnitCost);
            Assert.Equal(new DateTime(year, 1, 10), opening.MovementDate);

            var line = await ctx.Set<OpeningBalanceLine>().AsNoTracking()
                .FirstAsync(l => l.BatchId == batchId && l.ItemId == itemId);
            Assert.Equal(opening.Id, line.StockMovementId);   // ربط البند بحركته (تدقيق + أساس العكس)

            var receivableAccountId = await ctx.Set<Account>().AsNoTracking()
                .Where(a => a.Code == Receivable).Select(a => a.Id).FirstAsync();
            var partyLine = await ctx.Set<OpeningBalanceLine>().AsNoTracking()
                .FirstAsync(l => l.BatchId == batchId && l.AccountId == receivableAccountId);
            Assert.Equal(_pg.CustomerId, partyLine.CustomerId);
        }

        // (د) المخزَّن = المستنتج لكل الحسابات + الأستاذ متوازن
        await LedgerInvariants.AssertConsistentAsync(_pg);

        // (هـ) خدمة المطابقة: الرقابيان مطابقان (مقارنة تفاضلية: اختبارات الفصل تشترك في نفس القاعدة)
        var receivableRow = await ReconciliationRowAsync(Receivable);
        Assert.Equal(receivableDifferenceBefore, receivableRow.Difference);
        Assert.Contains("متطابق", receivableRow.StatusAr);

        var payableRow = await ReconciliationRowAsync(Payable);
        Assert.Equal(payableDifferenceBefore, payableRow.Difference);
        Assert.Contains("متطابق", payableRow.StatusAr);

        var inventoryRow = await ReconciliationRowAsync(Inventory);
        Assert.Equal(1200m, inventoryRow.ControlBalance);
        Assert.Equal(1200m, inventoryRow.DetailBalance);
        Assert.Equal(0m, inventoryRow.Difference);
        Assert.Contains("متطابق", inventoryRow.StatusAr);
        Assert.Equal(1, inventoryRow.DetailRowCount);
    }

    // ═══════════════ 2) قواعد الإلزام بالتفصيل (منع عودة الفجوة) ═══════════════

    [LivePgFact]
    public async Task Detail_is_mandatory_on_control_accounts_and_validated()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2061;
        var itemId = await NewItemAsync("B");
        Assert.NotEqual(Guid.Empty, itemId);

        async Task<string> RejectAsync(params OpeningBalanceLineInput[] lines)
        {
            await using var ctx = _pg.NewContext();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx)
                    .CreateDraftAsync(Batch(year, $"OD-BAD-{year}", lines), "tester"));
            return ex.Message;
        }

        // (1) 1200 بلا عميل
        Assert.Contains("كود العميل", await RejectAsync(
            PartyLine(Receivable, 100m, 0m), PartyLine(Payable, 0m, 100m, supplierCode: AccountingPgFixture.SupplierCode)));

        // (2) 2200 بلا مورد
        Assert.Contains("كود المورد", await RejectAsync(
            PartyLine(Cash, 100m, 0m), PartyLine(Payable, 0m, 100m)));

        // (3) تفصيل عميل على حساب غير الرقابي ⇒ رفض
        Assert.Contains("لا يقبل تحديد عميل", await RejectAsync(
            PartyLine(Cash, 100m, 0m, customerCode: AccountingPgFixture.CustomerCode),
            PartyLine(SalesTax, 0m, 100m)));

        // (4) عميل غير موجود
        Assert.Contains("غير موجود", await RejectAsync(
            PartyLine(Receivable, 100m, 0m, customerCode: "NO-SUCH-CUSTOMER"),
            PartyLine(SalesTax, 0m, 100m)));

        // (5) بند صنف بلا مخزن
        var noWarehouse = ItemLine("OD-B", 5m, 10m);
        noWarehouse.WarehouseCode = null;
        Assert.Contains("كود المخزن", await RejectAsync(noWarehouse, PartyLine(SalesTax, 0m, 50m)));

        // (6) بند صنف بلا كمية
        var noQuantity = ItemLine("OD-B", 5m, 10m);
        noQuantity.Quantity = null;
        Assert.Contains("كمية", await RejectAsync(noQuantity, PartyLine(SalesTax, 0m, 50m)));

        // (7) قيمة بند الصنف لا تساوي الكمية × التكلفة ⇒ رفض ببيان الفرق (لا استبدال صامت)
        var mismatched = ItemLine("OD-B", 5m, 10m, debit: 40m);
        Assert.Contains("لا تساوي الكمية × التكلفة", await RejectAsync(mismatched, PartyLine(SalesTax, 0m, 40m)));

        // (8) بند صنف **دائن** (رصيد افتتاحي سالب للمخزون) ⇒ رفض صريح (يحتاج قرارًا ماليًا مستقلًا)
        var creditItem = ItemLine("OD-B", 5m, 10m);
        creditItem.CreditAmount = 50m;
        Assert.Contains("يجب أن يكون", await RejectAsync(creditItem, PartyLine(SalesTax, 50m, 0m)));

        // (9) تكرار نفس العميل في بندين ⇒ رفض (منع الأثر المضاعف)
        Assert.Contains("أكثر من بند", await RejectAsync(
            PartyLine(Receivable, 100m, 0m, customerCode: AccountingPgFixture.CustomerCode),
            PartyLine(Receivable, 50m, 0m, customerCode: AccountingPgFixture.CustomerCode),
            PartyLine(SalesTax, 0m, 150m)));

        // (10) تكرار نفس الصنف+المخزن ⇒ رفض
        Assert.Contains("مرتين", await RejectAsync(
            ItemLine("OD-B", 5m, 10m), ItemLine("OD-B", 5m, 10m), PartyLine(SalesTax, 0m, 100m)));

        // ولا مسودة نجحت من كل ما سبق (كلها رُفضت قبل الكتابة)
        await using (var ctx = _pg.NewContext())
            Assert.False(await ctx.Set<OpeningBalanceBatch>().AnyAsync(b => b.Reference == $"OD-BAD-{year}"));
    }

    // ═══════════════ 3) العكس يعيد كل التفصيل (أطراف + مخزون) بلا تعديل تاريخي ═══════════════

    [LivePgFact]
    public async Task Opening_reversal_restores_party_balances_and_stock()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2062;
        var itemId = await NewItemAsync("C");

        var customerBefore = await CustomerBalanceAsync(_pg.CustomerId);
        var supplierBefore = await SupplierBalanceAsync(_pg.SupplierId);
        var inventoryBefore = await AccountBalanceAsync(Inventory);
        var entriesBefore = await EntryCountAsync();
        var receivableDifferenceBefore = (await ReconciliationRowAsync(Receivable)).Difference;
        var payableDifferenceBefore = (await ReconciliationRowAsync(Payable)).Difference;

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(Batch(year, $"OD-REV-{year}",
                PartyLine(Receivable, 600m, 0m, customerCode: AccountingPgFixture.CustomerCode),
                ItemLine("OD-C", 20m, 8m),
                PartyLine(Payable, 0m, 300m, supplierCode: AccountingPgFixture.SupplierCode),
                PartyLine(SalesTax, 0m, 460m)), "tester");
            batchId = (await svc.PostAsync(draft.Id, "tester")).Id;
        }

        // قبل العكس: التفصيل مُطبَّق
        Assert.Equal(customerBefore + 600m, await CustomerBalanceAsync(_pg.CustomerId));
        Assert.Equal(supplierBefore + 300m, await SupplierBalanceAsync(_pg.SupplierId));
        Assert.Equal(inventoryBefore + 160m, await AccountBalanceAsync(Inventory));
        Assert.Equal(20m, (await ItemStateAsync(itemId)).Stock);

        await using (var ctx = _pg.NewContext())
        {
            var reversal = await AccountingPgFixture.NewOpeningBalanceService(ctx)
                .ReverseAsync(batchId, new DateTime(year, 7, 1), "tester");
            Assert.Equal(batchId, reversal.ReversalOfBatchId);
            Assert.NotNull(reversal.JournalEntryId);
        }

        // بعد العكس: كل شيء عاد إلى ما قبل الدفعة، وقيدان أُضيفا (أصلي + عكسي)
        Assert.Equal(entriesBefore + 2, await EntryCountAsync());
        Assert.Equal(customerBefore, await CustomerBalanceAsync(_pg.CustomerId));
        Assert.Equal(supplierBefore, await SupplierBalanceAsync(_pg.SupplierId));
        Assert.Equal(inventoryBefore, await AccountBalanceAsync(Inventory));
        Assert.Equal(0m, (await ItemStateAsync(itemId)).Stock);

        await using (var ctx = _pg.NewContext())
        {
            var movement = await ctx.Set<StockMovement>().AsNoTracking()
                .Where(m => m.ItemId == itemId && !m.IsDeleted
                            && m.MovementType == MovementType.AdjustmentOut).SingleAsync();
            Assert.Equal(-20m, movement.Quantity);
            Assert.Equal(new DateTime(year, 7, 1), movement.MovementDate);   // تاريخ قيد العكس

            var original = await ctx.Set<OpeningBalanceBatch>().AsNoTracking().FirstAsync(b => b.Id == batchId);
            Assert.Equal(OpeningBalanceBatchStatus.Reversed, original.Status);
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
        Assert.Equal(receivableDifferenceBefore, (await ReconciliationRowAsync(Receivable)).Difference);
        Assert.Equal(payableDifferenceBefore, (await ReconciliationRowAsync(Payable)).Difference);
    }

    // ═══════════════ 4) عكس المخزون يفشل مغلقًا إذا استُهلك الرصيد ═══════════════

    [LivePgFact]
    public async Task Opening_reversal_of_consumed_stock_fails_closed_without_partial_changes()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2063;
        var itemId = await NewItemAsync("D");

        Guid batchId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(Batch(year, $"OD-CONS-{year}",
                ItemLine("OD-D", 100m, 5m),
                PartyLine(SalesTax, 0m, 500m)), "tester");
            batchId = (await svc.PostAsync(draft.Id, "tester")).Id;
        }

        // بيع نقدي 60 وحدة ⇒ يبقى 40 فقط في المخزون
        await using (var ctx = _pg.NewContext())
        {
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = new DateTime(year, 2, 1),
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = 60m, UnitPrice = 9m }
                }
            });
        }

        var stockBefore = await ItemStateAsync(itemId);
        var inventoryBefore = await AccountBalanceAsync(Inventory);
        var entriesBefore = await EntryCountAsync();

        await using (var ctx = _pg.NewContext())
        {
            var ex = await Assert.ThrowsAsync<InsufficientStockException>(() =>
                AccountingPgFixture.NewOpeningBalanceService(ctx)
                    .ReverseAsync(batchId, new DateTime(year, 3, 1), "tester"));
            Assert.Contains("الرصيد غير كافٍ", ex.Message);
        }

        // لا أثر جزئي: الحالة كما هي، ولا قيد عكسي، ولا حركة مخزون صادرة
        Assert.Equal(stockBefore.Stock, (await ItemStateAsync(itemId)).Stock);
        Assert.Equal(inventoryBefore, await AccountBalanceAsync(Inventory));
        Assert.Equal(entriesBefore, await EntryCountAsync());
        await using (var ctx = _pg.NewContext())
        {
            var batch = await ctx.Set<OpeningBalanceBatch>().AsNoTracking().FirstAsync(b => b.Id == batchId);
            Assert.Equal(OpeningBalanceBatchStatus.Posted, batch.Status);
            Assert.Null(batch.ReversedByBatchId);
            Assert.Equal(0, await ctx.Set<StockMovement>().CountAsync(m => !m.IsDeleted
                && m.ItemId == itemId && m.MovementType == MovementType.AdjustmentOut));
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ═══════════════ 5) المطابقة: قيد يدوي على الرقابي = فرق مُفسَّر (لا تسوية) ═══════════════

    [LivePgFact]
    public async Task Reconciliation_reports_manual_entry_on_control_as_explained_difference()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2064;

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(Batch(year, $"OD-REC-{year}",
                PartyLine(Receivable, 500m, 0m, customerCode: AccountingPgFixture.CustomerCode),
                PartyLine(SalesTax, 0m, 500m)), "tester");
            await svc.PostAsync(draft.Id, "tester");
        }

        var rowBefore = await ReconciliationRowAsync(Receivable);
        Assert.Equal(0m, rowBefore.Difference);
        Assert.Equal(0m, rowBefore.UnexplainedAmount);

        // قيد يدوي على الرقابي بلا طرف ⇒ فرق = مبلغ القيد، ومُفسَّر بالكامل
        await using (var ctx = _pg.NewContext())
        {
            var receivableId = (await PostingAccountResolver.ResolveAsync(ctx, Receivable)).Id;
            var taxId = (await PostingAccountResolver.ResolveAsync(ctx, SalesTax)).Id;
            await new JournalEntryService(ctx).CreateManualEntryAsync(new DateTime(year, 5, 5),
                "OD-MANUAL-AR", "قيد يدوي على الرقابي (توثيق فرق)",
                new List<ERPSystem.Application.DTOs.Journal.JournalEntryLegInput>
                {
                    new() { AccountId = receivableId, DebitAmount = 300m },
                    new() { AccountId = taxId, CreditAmount = 300m }
                });
        }

        var rowAfter = await ReconciliationRowAsync(Receivable);
        Assert.Equal(rowBefore.Difference + 300m, rowAfter.Difference);
        Assert.Equal(300m, rowAfter.ExplainedAmount);
        Assert.Equal(0m, rowAfter.UnexplainedAmount);
        Assert.Contains("فرق مُفسَّر", rowAfter.StatusAr);
        Assert.Contains(rowAfter.NonDocumentEffects, e => e.EntryType == (int)JournalEntryType.Manual);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ═══════════════ 6) قفل الفترة يمنع كل مسارات الترحيل، وإعادة الفتح تُعيدها ═══════════════

    [LivePgFact]
    public async Task Closed_period_blocks_every_posting_path_and_reopen_restores()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2075;
        const int closedMonth = 3;
        var closedDate = new DateTime(year, closedMonth, 15);

        var balancesBefore = await LedgerInvariants.AccountsAsync(_pg);
        var entriesBefore = await EntryCountAsync();

        // إغلاق الشهر (الإغلاق نفسه لا يغيّر أي رصيد ولا يُنشئ قيدًا)
        await using (var ctx = _pg.NewContext())
        {
            var dto = await Periods(ctx).CloseAsync(year, closedMonth, "إغلاق تجريبي", "tester");
            Assert.Equal(2, dto.Status);
            Assert.Equal("tester", dto.ClosedBy);
            Assert.NotNull(dto.ClosedAt);
        }
        Assert.Equal(entriesBefore, await EntryCountAsync());
        Assert.Equal(balancesBefore, await LedgerInvariants.AccountsAsync(_pg));

        // (أ) قيد يدوي داخل الفترة المغلقة ⇒ رفض
        await using (var ctx = _pg.NewContext())
        {
            var cashId = (await PostingAccountResolver.ResolveAsync(ctx, Cash)).Id;
            var taxId = (await PostingAccountResolver.ResolveAsync(ctx, SalesTax)).Id;
            var ex = await Assert.ThrowsAsync<PeriodClosedException>(() =>
                new JournalEntryService(ctx).CreateManualEntryAsync(closedDate, "OD-CLOSED",
                    "قيد داخل فترة مغلقة",
                    new List<ERPSystem.Application.DTOs.Journal.JournalEntryLegInput>
                    {
                        new() { AccountId = cashId, DebitAmount = 10m },
                        new() { AccountId = taxId, CreditAmount = 10m }
                    }));
            Assert.Contains($"{year}-0{closedMonth}", ex.Message);
        }

        // (ب) فاتورة بيع داخل الفترة المغلقة ⇒ رفض (نفس الحارس المركزي).
        //     نُزوّد الصنف برصيد مخزني أولًا (حركة وارد مؤرخة في شهر مفتوح) حتى يصل الطلب إلى مرحلة
        //     القيد المحاسبي، فلا يسبقه فحص كفاية المخزون بخطأ مختلف.
        var itemId = await NewItemAsync("E");
        await using (var ctx = _pg.NewContext())
        {
            await new StockMovementService(ctx, new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new ERPSystem.Application.DTOs.Movements.CreateStockMovementDto
                {
                    ItemId = itemId,
                    WarehouseId = _pg.WarehouseId,
                    MovementType = (int)MovementType.PurchaseReceipt,
                    Quantity = 10m,
                    UnitCost = 10m,
                    MovementDate = new DateTime(year, 1, 20),
                    ReferenceNumber = $"OD-SEED-{year}"
                });
        }

        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAsync<PeriodClosedException>(() =>
                AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
                {
                    CustomerId = _pg.CustomerId,
                    WarehouseId = _pg.WarehouseId,
                    InvoiceDate = closedDate,
                    InvoiceType = (int)SalesInvoiceType.Cash,
                    PaymentMethod = (int)SalesPaymentMethod.Cash,
                    TaxRate = 0m,
                    Lines = new List<CreateSalesInvoiceLineDto>
                    {
                        new() { ItemId = itemId, Quantity = 1m, UnitPrice = 10m }
                    }
                }));
        }

        // (ج) رصيد افتتاحي داخل الفترة المغلقة ⇒ رفض عند الترحيل، والدفعة تبقى مسودة (تراجع ذري)
        Guid draftId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(new CreateOpeningBalanceBatchDto
            {
                FiscalYear = year,
                EntryDate = new DateTime(year, closedMonth, 1),
                Reference = $"OD-CLOSED-{year}",
                Lines = new List<OpeningBalanceLineInput>
                {
                    new() { AccountCode = Cash, DebitAmount = 50m },
                    new() { AccountCode = SalesTax, CreditAmount = 50m }
                }
            }, "tester");
            draftId = draft.Id;

            await Assert.ThrowsAsync<PeriodClosedException>(() => svc.PostAsync(draftId, "tester"));
        }
        await using (var ctx = _pg.NewContext())
        {
            var batch = await ctx.Set<OpeningBalanceBatch>().AsNoTracking().FirstAsync(b => b.Id == draftId);
            Assert.Equal(OpeningBalanceBatchStatus.Draft, batch.Status);
            Assert.Null(batch.JournalEntryId);
        }

        // لا شيء تغيّر: لا قيد جديد ولا رصيد متغيّر
        Assert.Equal(entriesBefore, await EntryCountAsync());
        Assert.Equal(balancesBefore, await LedgerInvariants.AccountsAsync(_pg));

        // (د) الشهر التالي مفتوح ⇒ الترحيل يعمل بشكل طبيعي
        await using (var ctx = _pg.NewContext())
        {
            var cashId = (await PostingAccountResolver.ResolveAsync(ctx, Cash)).Id;
            var taxId = (await PostingAccountResolver.ResolveAsync(ctx, SalesTax)).Id;
            await new JournalEntryService(ctx).CreateManualEntryAsync(new DateTime(year, closedMonth + 1, 2),
                "OD-OPEN-NEXT", "قيد في فترة مفتوحة",
                new List<ERPSystem.Application.DTOs.Journal.JournalEntryLegInput>
                {
                    new() { AccountId = cashId, DebitAmount = 20m },
                    new() { AccountId = taxId, CreditAmount = 20m }
                });
        }
        Assert.Equal(entriesBefore + 1, await EntryCountAsync());

        // (هـ) إعادة الفتح ⇒ الترحيل داخل الشهر يعود مسموحًا (بقرار موثَّق مسجَّل)
        await using (var ctx = _pg.NewContext())
        {
            var reopened = await Periods(ctx).ReopenAsync(year, closedMonth, "إعادة فتح بقرار موثَّق", "tester");
            Assert.Equal(1, reopened.Status);
            Assert.Equal("tester", reopened.ReopenedBy);

            var cashId = (await PostingAccountResolver.ResolveAsync(ctx, Cash)).Id;
            var taxId = (await PostingAccountResolver.ResolveAsync(ctx, SalesTax)).Id;
            await new JournalEntryService(ctx).CreateManualEntryAsync(closedDate, "OD-REOPENED",
                "قيد بعد إعادة الفتح",
                new List<ERPSystem.Application.DTOs.Journal.JournalEntryLegInput>
                {
                    new() { AccountId = cashId, DebitAmount = 30m },
                    new() { AccountId = taxId, CreditAmount = 30m }
                });
        }
        Assert.Equal(entriesBefore + 2, await EntryCountAsync());
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ═══════════════ 7) القفل مقيَّد بالشهر + إدارة الفترات مُصرَّح بها ومُدقَّقة ═══════════════

    [LivePgFact]
    public async Task Period_lock_is_month_scoped_and_management_is_authorized_and_audited()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        const int year = 2076;

        // (أ) بلا تصريح ⇒ رفض صريح (فشل مغلق) على كل عمليات الإدارة
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Periods(ctx, allow: false).GetYearAsync(year));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                Periods(ctx, allow: false).CloseAsync(year, 5, null, "denied"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                Periods(ctx, allow: false).ReopenAsync(year, 5, null, "denied"));
        }

        // (ب) العرض الافتراضي: 12 شهرًا كلها مفتوحة بلا صفوف (توافق خلفي)
        await using (var ctx = _pg.NewContext())
        {
            var yearView = await Periods(ctx).GetYearAsync(year);
            Assert.Equal(12, yearView.Months.Count);
            Assert.All(yearView.Months, m => Assert.Equal("مفتوحة", m.StatusNameAr));
            Assert.All(yearView.Months, m => Assert.False(m.HasRow));
            Assert.Equal(12, yearView.OpenCount);
            Assert.Equal(0, yearView.ClosedCount);
        }

        // (ج) إغلاق مايو + رفض الإغلاق المكرر + رفض إعادة فتح شهر غير مغلق
        await using (var ctx = _pg.NewContext())
        {
            await Periods(ctx).CloseAsync(year, 5, "إغلاق مايو", "tester");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Periods(ctx).CloseAsync(year, 5, "تكرار", "tester"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Periods(ctx).ReopenAsync(year, 6, null, "tester"));
        }

        async Task PostManualAsync(DateTime date)
        {
            await using var ctx = _pg.NewContext();
            var cashId = (await PostingAccountResolver.ResolveAsync(ctx, Cash)).Id;
            var taxId = (await PostingAccountResolver.ResolveAsync(ctx, SalesTax)).Id;
            await new JournalEntryService(ctx).CreateManualEntryAsync(date, "OD-SCOPE", "فحص نطاق القفل",
                new List<ERPSystem.Application.DTOs.Journal.JournalEntryLegInput>
                {
                    new() { AccountId = cashId, DebitAmount = 5m },
                    new() { AccountId = taxId, CreditAmount = 5m }
                });
        }

        await PostManualAsync(new DateTime(year, 4, 30));  // الشهر المفتوح السابق
        await PostManualAsync(new DateTime(year, 6, 1));   // الشهر المفتوح التالي
        await Assert.ThrowsAsync<PeriodClosedException>(() => PostManualAsync(new DateTime(year, 5, 1)));
        await Assert.ThrowsAsync<PeriodClosedException>(() => PostManualAsync(new DateTime(year, 5, 31)));

        // (د) العرض بعد الإغلاق يعرض الحالة وسجل الإغلاق
        await using (var ctx = _pg.NewContext())
        {
            var view = await Periods(ctx).GetYearAsync(year);
            var may = view.Months.Single(m => m.Month == 5);
            Assert.Equal("مغلقة", may.StatusNameAr);
            Assert.True(may.HasRow);
            Assert.Equal("tester", may.ClosedBy);
            Assert.Equal(1, view.ClosedCount);
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
