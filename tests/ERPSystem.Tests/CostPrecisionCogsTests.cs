using ERPSystem.Application.DTOs.Inventory;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// Phase C1 — اتساق دقة COGS (COGS precision consistency).
///
/// يثبّت أنّ:
///  - لقطات التكلفة الداخلية تُخزَّن وتُقرأ بأربع خانات (18,4): Item.CostPrice،
///    SalesInvoiceLine.UnitCost، SalesReturnLine.UnitCost، StockMovement.UnitCost، StockWriteOff.UnitCost.
///  - أسعار المورّد (PurchaseInvoiceLine/PurchaseReturnLine.UnitCost) وأسعار العملاء تبقى بعشرتين (18,2).
///  - COGS يُحتسب من اللقطة المخزَّنة (الكمية × UnitCost) ويُقرَّب نقديًا **مرة واحدة** على الإجمالي.
///  - سياسة التقريب الصريحة MoneyRounding = ToEven على خانتين (تشمل حالات المنتصف الدقيقة).
///  - القيود (COGS، عكس COGS، الإتلاف، البيع) متوازنة ومطابقة للمبالغ المرحّلة.
/// </summary>
public class CostPrecisionCogsTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public CostPrecisionCogsTests(AccountingPgFixture pg) => _pg = pg;

    private static string Code() => "C1-COGS-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>موديل Npgsql بلا اتصال — لفحص أنواع الأعمدة التي يولّدها المزوّد لـ PostgreSQL.</summary>
    private static AppDbContext NewNpgsqlModelOnly()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=x;Password=x")
            .Options;
        return new AppDbContext(options);
    }

    private static string ColumnType(AppDbContext ctx, Type entity, string property)
        => ctx.Model.FindEntityType(entity)!.FindProperty(property)!.GetColumnType();

    /// <summary>دقة/مقياس أعمدة UnitCost الفعلية في قاعدة الاختبار الحيّة (بعد تطبيق الترحيلات).</summary>
    private static async Task<Dictionary<string, (int Precision, int Scale)>> UnitCostColumnsAsync(AccountingPgFixture pg)
    {
        await using var conn = new NpgsqlConnection(pg.TestConnectionString!);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select table_name || '.' || column_name, numeric_precision, numeric_scale " +
            "from information_schema.columns where table_schema='public' and column_name='UnitCost'", conn);
        var result = new Dictionary<string, (int, int)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        return result;
    }

    /// <summary>مجموع المدين/الدائن لقيد مرحَّل (من قاعدة الاختبار).</summary>
    private static async Task<(decimal Debit, decimal Credit)> EntryTotalsAsync(AccountingPgFixture pg, Guid entryId)
    {
        await using var ctx = pg.NewContext();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.JournalEntryId == entryId && !l.IsDeleted)
            .ToListAsync();
        return (lines.Sum(l => l.DebitAmount), lines.Sum(l => l.CreditAmount));
    }

    /// <summary>فاتورة بيع نقدية متعددة البنود (أصناف مختلفة — لا يسمح النظام بتكرار الصنف).</summary>
    private static CreateSalesInvoiceDto CashSaleMultiline(
        AccountingPgFixture pg, IReadOnlyList<(Guid ItemId, decimal Quantity, decimal UnitPrice)> lines)
        => new()
        {
            CustomerId = pg.CustomerId,
            WarehouseId = pg.WarehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.Cash,
            PaymentMethod = (int)SalesPaymentMethod.Cash,
            IsPos = false,
            Lines = lines.Select(l => new CreateSalesInvoiceLineDto
            {
                ItemId = l.ItemId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };
    // ───────── 1) تعيين الأعمدة: نموذج Npgsql + قاعدة حيّة ─────────

    [Fact]
    public void Cost_snapshot_columns_map_to_numeric_18_4_in_the_npgsql_model()
    {
        using var ctx = NewNpgsqlModelOnly();

        // لقطات/مرجع التكلفة الداخلية → أربع خانات
        Assert.Equal("numeric(18,4)", ColumnType(ctx, typeof(Item), nameof(Item.CostPrice)));
        Assert.Equal("numeric(18,4)", ColumnType(ctx, typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.UnitCost)));
        Assert.Equal("numeric(18,4)", ColumnType(ctx, typeof(SalesReturnLine), nameof(SalesReturnLine.UnitCost)));
        Assert.Equal("numeric(18,4)", ColumnType(ctx, typeof(StockMovement), nameof(StockMovement.UnitCost)));
        Assert.Equal("numeric(18,4)", ColumnType(ctx, typeof(StockWriteOff), nameof(StockWriteOff.UnitCost)));

        // أسعار المورّد المُرحَّلة على فواتير المشتريات تبقى بعشرتين (سلوك محفوظ — لم يتغيّر في C1)
        Assert.Equal("numeric(18,2)", ColumnType(ctx, typeof(PurchaseInvoiceLine), nameof(PurchaseInvoiceLine.UnitCost)));
        Assert.Equal("numeric(18,2)", ColumnType(ctx, typeof(PurchaseReturnLine), nameof(PurchaseReturnLine.UnitCost)));

        // أسعار العملاء (بيع) تبقى بعشرتين
        Assert.Equal("numeric(18,2)", ColumnType(ctx, typeof(Item), nameof(Item.SalePrice)));
        Assert.Equal("numeric(18,2)", ColumnType(ctx, typeof(SalesInvoiceLine), nameof(SalesInvoiceLine.UnitPrice)));
    }

    [LivePgFact]
    public async Task Cost_snapshot_columns_are_numeric_18_4_in_postgresql()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var cols = await UnitCostColumnsAsync(_pg);

        Assert.Equal((18, 4), cols["SalesInvoiceLines.UnitCost"]);
        Assert.Equal((18, 4), cols["SalesReturnLines.UnitCost"]);
        Assert.Equal((18, 4), cols["StockMovements.UnitCost"]);
        Assert.Equal((18, 4), cols["StockWriteOffs.UnitCost"]);

        // سلوك محفوظ: أسعار المورّد تبقى عشرتين
        Assert.Equal((18, 2), cols["PurchaseInvoiceLines.UnitCost"]);
        Assert.Equal((18, 2), cols["PurchaseReturnLines.UnitCost"]);
    }

    // ───────── 2) سياسة التقريب النقدي (MoneyRounding) ─────────

    [Fact]
    public void MoneyRounding_is_two_decimals_banker_rounding_and_handles_exact_midpoints()
    {
        Assert.Equal(2, MoneyRounding.Scale);
        Assert.Equal(MidpointRounding.ToEven, MoneyRounding.Mode);

        // حالات منتصف دقيقة (تنتهي بـ 5 بالضبط) → إلى القرش الزوجي الأقرب
        Assert.Equal(6.22m, MoneyRounding.Round(6.225m));
        Assert.Equal(6.24m, MoneyRounding.Round(6.235m));
        Assert.Equal(15.20m, MoneyRounding.Round(15.205m));
        Assert.Equal(15.22m, MoneyRounding.Round(15.215m));
        Assert.Equal(0.12m, MoneyRounding.Round(0.125m));
        Assert.Equal(0.14m, MoneyRounding.Round(0.135m));

        // قيمة عادية بلا منتصف
        Assert.Equal(30.40m, MoneyRounding.Round(30.396m));

        // السياسة مقصودة ومخالفة لـ AwayFromZero عند المنتصف (توثيق صريح لا تغيير صامت)
        Assert.Equal(0.13m, Math.Round(0.125m, 2, MidpointRounding.AwayFromZero));
        Assert.NotEqual(Math.Round(0.125m, 2, MidpointRounding.AwayFromZero), MoneyRounding.Round(0.125m));

        // مطابقة السلوك التاريخي الضمني: Math.Round(value, 2) الافتراضي = ToEven
        Assert.Equal(Math.Round(6.225m, 2), MoneyRounding.Round(6.225m));
        Assert.Equal(Math.Round(30.396m, 2), MoneyRounding.Round(30.396m));
    }


    // ───────── 3) لقطة التكلفة الرباعية + ضرب الكمية + قيد COGS ─────────

    [LivePgFact]
    public async Task Sale_snapshots_the_four_decimal_cost_and_posts_matching_cogs()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        const decimal unitCost = 10.1320m;
        var itemId = await _pg.CreateItemAsync(Code(), salePrice: 12.00m, costPrice: unitCost, openingStock: 50m);

        SalesInvoiceDto invoice;
        await using (var ctx = _pg.NewContext())
            invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(itemId, 3m, 12.00m));

        // اللقطة تُحفظ بأربع خانات عبر مسار البيع (سطر الفاتورة + حركة المخزون + الصنف بلا تغيير)
        await using (var ctx = _pg.NewContext())
        {
            var line = await ctx.Set<SalesInvoiceLine>().AsNoTracking()
                .SingleAsync(l => l.SalesInvoiceId == invoice.Id);
            Assert.Equal(unitCost, line.UnitCost);

            var movement = await ctx.Set<StockMovement>().AsNoTracking()
                .SingleAsync(m => m.ItemId == itemId && m.MovementType == MovementType.SalesIssue);
            Assert.Equal(unitCost, movement.UnitCost);

            Assert.Equal(unitCost, (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId)).CostPrice);
        }

        // COGS = تقريب مرة واحدة على (الكمية × اللقطة): 3 × 10.1320 = 30.3960 → 30.40
        var expectedCogs = MoneyRounding.Round(3m * unitCost);
        Assert.Equal(30.40m, expectedCogs);

        var (cogsDebit, cogsCredit) = await EntryTotalsAsync(_pg, invoice.CogsJournalEntryId!.Value);
        Assert.Equal(expectedCogs, cogsDebit);
        Assert.Equal(expectedCogs, cogsCredit);

        // قيد البيع أيضًا متوازن
        var (saleDebit, saleCredit) = await EntryTotalsAsync(_pg, invoice.SalesJournalEntryId!.Value);
        Assert.Equal(saleDebit, saleCredit);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    // ───────── 4) حالة المنتصف الدقيقة في مسار البيع الحقيقي ─────────

    [LivePgFact]
    public async Task Sale_at_an_exact_midpoint_cost_rounds_to_even_in_posted_cogs()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        const decimal unitCost = 10.1250m; // × 1 = 10.125 (منتصف) → ToEven → 10.12 (لا 10.13)
        var itemId = await _pg.CreateItemAsync(Code(), salePrice: 12.00m, costPrice: unitCost, openingStock: 10m);

        SalesInvoiceDto invoice;
        await using (var ctx = _pg.NewContext())
            invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(itemId, 1m, 12.00m));

        Assert.Equal(10.12m, MoneyRounding.Round(1m * unitCost));

        var (debit, credit) = await EntryTotalsAsync(_pg, invoice.CogsJournalEntryId!.Value);
        Assert.Equal(10.12m, debit);
        Assert.Equal(10.12m, credit);

        // اللقطة نفسها تحتفظ بالمنتصف بأربع خانات (لا تُختصر قبل الاشتقاق)
        await using (var ctx = _pg.NewContext())
        {
            var line = await ctx.Set<SalesInvoiceLine>().AsNoTracking()
                .SingleAsync(l => l.SalesInvoiceId == invoice.Id);
            Assert.Equal(10.1250m, line.UnitCost);
        }
    }

    // ───────── 5) التقريب مرة واحدة على الإجمالي (لا لكل سطر) ─────────

    [LivePgFact]
    public async Task Cogs_is_rounded_once_on_the_invoice_total_not_per_line()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        const decimal unitCost = 10.1250m;
        var items = new List<Guid>();
        for (var i = 0; i < 3; i++)
            items.Add(await _pg.CreateItemAsync(Code(), 12.00m, unitCost, 5m));

        var dto = CashSaleMultiline(_pg, items.Select(id => (id, 1m, 12.00m)).ToList());

        SalesInvoiceDto invoice;
        await using (var ctx = _pg.NewContext())
            invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(dto);

        // الإجمالي = 30.3750 → ToEven → 30.38؛ أما التقريب لكل سطر لكان 3 × 10.12 = 30.36
        var expected = MoneyRounding.Round(3m * unitCost);
        Assert.Equal(30.38m, expected);
        Assert.NotEqual(3m * MoneyRounding.Round(unitCost), expected);

        var (debit, credit) = await EntryTotalsAsync(_pg, invoice.CogsJournalEntryId!.Value);
        Assert.Equal(expected, debit);
        Assert.Equal(expected, credit);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }


    // ───────── 6) عكس COGS (مردود بيع) + سند الإتلاف ─────────

    [LivePgFact]
    public async Task Return_reverses_cogs_and_write_off_posts_from_the_four_decimal_snapshot()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        const decimal unitCost = 10.1320m;
        var itemId = await _pg.CreateItemAsync(Code(), salePrice: 12.00m, costPrice: unitCost, openingStock: 50m);

        SalesInvoiceDto invoice;
        await using (var ctx = _pg.NewContext())
            invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(_pg.NewCashSale(itemId, 3m, 12.00m));

        // مردود بيع لوحدة واحدة → عكس COGS = round(1 × 10.1320) = 10.13
        SalesReturnDto salesReturn;
        await using (var ctx = _pg.NewContext())
            salesReturn = await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(
                new CreateSalesReturnDto
                {
                    SalesInvoiceId = invoice.Id,
                    WarehouseId = _pg.WarehouseId,
                    ReturnDate = DateTime.Today,
                    Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = itemId, Quantity = 1m, UnitPrice = 12.00m } }
                });

        var expectedReverse = MoneyRounding.Round(1m * unitCost);
        Assert.Equal(10.13m, expectedReverse);

        await using (var ctx = _pg.NewContext())
        {
            var ret = await ctx.Set<SalesReturn>().AsNoTracking().SingleAsync(r => r.Id == salesReturn.Id);
            Assert.NotNull(ret.CogsJournalEntryId);
            var (debit, credit) = await EntryTotalsAsync(_pg, ret.CogsJournalEntryId!.Value);
            Assert.Equal(expectedReverse, debit);
            Assert.Equal(expectedReverse, credit);

            var retLine = await ctx.Set<SalesReturnLine>().AsNoTracking().SingleAsync(l => l.SalesReturnId == salesReturn.Id);
            Assert.Equal(unitCost, retLine.UnitCost);
        }

        // سند إتلاف لوحدتين → round(2 × 10.1320) = 20.26
        StockWriteOffDto writeOff;
        await using (var ctx = _pg.NewContext())
            writeOff = await new StockWriteOffService(ctx, new JournalEntryService(ctx)).CreateAsync(
                new CreateStockWriteOffDto
                {
                    ItemId = itemId,
                    WarehouseId = _pg.WarehouseId,
                    Quantity = 2m,
                    Reason = (int)StockWriteOffReason.Damaged,
                    Date = DateTime.Today
                }, "c1-tester");

        var expectedWriteOff = MoneyRounding.Round(2m * unitCost);
        Assert.Equal(20.26m, expectedWriteOff);
        Assert.Equal(unitCost, writeOff.UnitCost);            // لقطة رباعية محفوظة
        Assert.Equal(expectedWriteOff, writeOff.TotalCost);   // قيمة السند = round(qty × cost)

        var (wDebit, wCredit) = await EntryTotalsAsync(_pg, writeOff.JournalEntryId);
        Assert.Equal(expectedWriteOff, wDebit);
        Assert.Equal(expectedWriteOff, wCredit);

        await using (var ctx = _pg.NewContext())
        {
            var movement = await ctx.Set<StockMovement>().AsNoTracking()
                .SingleAsync(m => m.ItemId == itemId && m.MovementType == MovementType.WriteOff);
            Assert.Equal(unitCost, movement.UnitCost);
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }


    // ───────── 7) انحدار: السعر/الضريبة/الخصم والقيود غير متأثرة ─────────

    [LivePgFact]
    public async Task Sale_price_tax_and_discount_are_unaffected_by_the_cost_precision_change()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        // سعر 10.00، تكلفة 4.00، بلا ضريبة → الاشتقاق موثق في SalesInvoice: فرق سعر/خصم/ضريبة
        var itemId = await _pg.CreateItemAsync(Code(), salePrice: 10.00m, costPrice: 4.00m, openingStock: 20m);

        var dto = _pg.NewCashSale(itemId, 1m, 10.00m);
        dto.DiscountPercentage = 5m; // خصم 5%

        SalesInvoiceDto invoice;
        await using (var ctx = _pg.NewContext())
            invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(dto);

        // SubTotal=10.00، Discount=0.50، Tax=0، Total=9.50 (نفس النموذج الموثق سابقًا)
        Assert.Equal(10.00m, invoice.SubTotal);
        Assert.Equal(0.50m, invoice.DiscountAmount);
        Assert.Equal(0.00m, invoice.TaxAmount);
        Assert.Equal(9.50m, invoice.TotalAmount);

        await using (var ctx = _pg.NewContext())
        {
            var line = await ctx.Set<SalesInvoiceLine>().AsNoTracking().SingleAsync(l => l.SalesInvoiceId == invoice.Id);
            Assert.Equal(10.00m, line.UnitPrice);   // سعر العميل بلا تغيير
        }

        // COGS مستقل عن السعر/الخصم: round(1 × 4.00) = 4.00
        var expectedCogs = MoneyRounding.Round(1m * 4.00m);
        var (cogsDebit, cogsCredit) = await EntryTotalsAsync(_pg, invoice.CogsJournalEntryId!.Value);
        Assert.Equal(expectedCogs, cogsDebit);
        Assert.Equal(expectedCogs, cogsCredit);

        // قيد البيع: صندوق 9.50 مدين / إيراد 9.50 دائن
        var (saleDebit, saleCredit) = await EntryTotalsAsync(_pg, invoice.SalesJournalEntryId!.Value);
        Assert.Equal(9.50m, saleDebit);
        Assert.Equal(9.50m, saleCredit);

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }


}
