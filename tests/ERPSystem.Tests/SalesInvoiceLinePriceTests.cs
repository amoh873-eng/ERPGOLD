using System.Globalization;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ERPSystem.Tests;

/// <summary>
/// Requirement 4 — سعر بند فاتورة المبيعات (Sales Invoice Line Price).
///
/// السلوك المطلوب والمُغطّى هنا:
///  - عند اختيار الصنف يُحمَّل سعره <c>Item.SalePrice = 2.38</c> إلى <c>SalesInvoiceLine.UnitPrice</c>.
///  - المستخدم المصرّح (Sales.Edit — Requirement 3) يستطيع تغيير سعر السطر إلى 2.45، ويبقى محفوظًا بعد إعادة الفتح.
///  - <c>Item.SalePrice</c> يبقى 2.38 ولا يُعاد كتابة بيانات الصنف إطلاقًا من مسار فاتورة البيع.
///  - غير المصرّح: يُرفض في الـBackend (خدمة التطبيق) قبل أي كتابة (لا فاتورة/بند/قيد/رقم محروق)،
///    بينما تمرّ فاتورته بالسعر الافتراضي 2.38.
///  - الحسابات (LineTotal، توزيع الخصم، ضريبة السطر TaxRate/TaxAmount، SubTotal/Total، القيد، COGS)
///    تُبنى على سعر السطر الفعلي بعد التعديل، مع لقطة تكلفة الوحدة <c>UnitCost</c> كما هي (بلا تغيير).
///
/// الاختبارات التي تلمس قاعدة البيانات تستخدم <see cref="LivePgFixture"/>: تنشئ قاعدة PostgreSQL مؤقتة
/// خاصة بها على خادم التطوير، ثم تحذفها بالكامل عند الانتهاء — لا Production ولا Commercial.
/// </summary>
public class SalesInvoiceLinePriceTests : IClassFixture<LivePgFixture>
{
    // قيم المطلب المرجعية
    private const decimal ItemSalePrice = 2.38m;     // سعر الصنف الأساسي (يُحمَّل افتراضيًا)
    private const decimal ManualLinePrice = 2.45m;   // سعر السطر اليدوي (تعديل مصرّح به)
    private const decimal ItemCostPrice = 1.75m;     // لقطة تكلفة الوحدة (COGS) — لا تُغيَّر في هذا المطلب

    private readonly LivePgFixture _pg;
    private readonly ITestOutputHelper _out;

    public SalesInvoiceLinePriceTests(LivePgFixture pg, ITestOutputHelper output)
    {
        _pg = pg;
        _out = output;
    }

    private static SalesInvoiceService NewSalesService(AppDbContext ctx, IManualPriceAuthorization auth)
        => new(ctx, new JournalEntryService(ctx), auth);

    /// <summary>المستخدم المصرّح بتعديل سعر السطر (Sales.Edit كما يُحقنه مضيف Web/Api).</summary>
    private static LivePgFixture.ModuleManualPrice AuthorizedUser()
        => new(ManualPriceModules.Sales);

    /// <summary>المستخدم غير المصرّح (لا يملك Sales.Edit).</summary>
    private static LivePgFixture.ModuleManualPrice UnauthorizedUser()
        => new(null);

    /// <summary>
    /// ينشئ صنفًا بسعر/تكلفة/نسبة ضريبة/رصيد افتتاحي محدّدين (قاعدة تطوير مؤقتة فقط).
    /// نفس مسار <see cref="LivePgFixture.CreateItemWithStockAsync"/> مع تحكّم في الأسعار والضريبة.
    /// </summary>
    private async Task<Guid> SeedItemAsync(string code, decimal salePrice, decimal costPrice, decimal taxRate, decimal openingStock)
    {
        await using var ctx = _pg.NewContext();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "صنف R4 " + code,
            CategoryId = _pg.CategoryId,
            UnitId = _pg.UnitId,
            SalePrice = salePrice,
            CostPrice = costPrice,
            TaxRate = taxRate,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        var movements = new StockMovementService(ctx, new LivePgFixture.AllowAllManualPrice());
        await movements.CreateAsync(new CreateStockMovementDto
        {
            ItemId = item.Id,
            WarehouseId = _pg.WarehouseId,
            MovementType = (int)MovementType.OpeningBalance,
            Quantity = openingStock,
            UnitCost = costPrice,
            MovementDate = DateTime.Today,
            ReferenceNumber = "R4-SEED"
        });

        return item.Id;
    }

    /// <summary>فاتورة بيع نقدية بالبنود المطلوبة (نفس شكل ما ترسله شاشة فاتورة المبيعات).</summary>
    private CreateSalesInvoiceDto Invoice(decimal discountPercentage, params (Guid ItemId, decimal Quantity, decimal UnitPrice)[] lines)
        => new()
        {
            CustomerId = _pg.CustomerId,
            WarehouseId = _pg.WarehouseId,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.Cash,
            PaymentMethod = (int)SalesPaymentMethod.Cash,
            DiscountPercentage = discountPercentage,
            IsPos = false,
            Lines = lines.Select(l => new CreateSalesInvoiceLineDto
            {
                ItemId = l.ItemId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };

    private static decimal DebitOf(JournalEntry entry, string accountCode)
        => entry.Lines.Where(l => l.Account!.Code == accountCode).Sum(l => l.DebitAmount);

    private static decimal CreditOf(JournalEntry entry, string accountCode)
        => entry.Lines.Where(l => l.Account!.Code == accountCode).Sum(l => l.CreditAmount);
    // ══════════════════════ الاختبار A — المستخدم المصرّح له ══════════════════════
    // 1) صنف سعره 2.38 → 2) اختياره يحمّل 2.38 → 3) تعديل السطر إلى 2.45
    // → 4) حفظ → 5) إعادة فتح → 6) السطر 2.45 وسعر الصنف 2.38.

    [LivePgFact]
    public async Task A_authorized_user_loads_default_price_then_saves_manual_price_and_reopens()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var code = "R4-A-" + Guid.NewGuid().ToString("N")[..6];
        var itemId = await SeedItemAsync(code, ItemSalePrice, ItemCostPrice, taxRate: 0m, openingStock: 10m);

        // (2) السعر الذي تحمّله الشاشة عند اختيار الصنف = Item.SalePrice
        //     (الشاشة تنفّذ: line.UnitPrice = item.SalePrice في OnLineItemChanged/AddLine).
        await using (var ctx = _pg.NewContext())
        {
            var loaded = await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId);
            Assert.Equal(ItemSalePrice, loaded.SalePrice);
            Assert.Equal(ItemCostPrice, loaded.CostPrice);
        }

        // (2أ) الحفظ بالسعر الافتراضي 2.38 (المسار الذي يمرّ به غير المصرّح أيضًا)
        await using (var ctx = _pg.NewContext())
        {
            var defaultInvoice = await NewSalesService(ctx, AuthorizedUser())
                .CreateAsync(Invoice(0m, (itemId, 1m, ItemSalePrice)));

            Assert.Equal(ItemSalePrice, defaultInvoice.Lines.Single().UnitPrice);
            Assert.Equal(ItemSalePrice, defaultInvoice.Lines.Single().LineTotal);
            Assert.Equal(ItemSalePrice, defaultInvoice.TotalAmount);
        }

        // (3+4) تعديل سعر السطر إلى 2.45 ثم الحفظ
        Guid manualInvoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var manualInvoice = await NewSalesService(ctx, AuthorizedUser())
                .CreateAsync(Invoice(0m, (itemId, 1m, ManualLinePrice)));

            manualInvoiceId = manualInvoice.Id;
            Assert.Equal(ManualLinePrice, manualInvoice.Lines.Single().UnitPrice);
            Assert.Equal(ManualLinePrice, manualInvoice.Lines.Single().LineTotal);
        }

        // (5+6) إعادة الفتح من قاعدة البيانات بسياق جديد (نفس ما يقرأه المستخدم عند فتح الفاتورة)
        await using (var ctx = _pg.NewContext())
        {
            var reopened = await NewSalesService(ctx, UnauthorizedUser()).GetByIdAsync(manualInvoiceId);

            Assert.NotNull(reopened);
            Assert.Equal(ManualLinePrice, reopened!.Lines.Single().UnitPrice);
            Assert.Equal(ManualLinePrice, reopened.Lines.Single().LineTotal);
        }

        // (6) سعر الصنف الأساسي وبياناته لم تتغيّر
        await using (var ctx = _pg.NewContext())
        {
            var itemAfter = await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId);
            Assert.Equal(ItemSalePrice, itemAfter.SalePrice);
            Assert.Equal(ItemCostPrice, itemAfter.CostPrice);
            Assert.Equal("صنف R4 " + code, itemAfter.NameAr);
        }
    }

    // ══════════════════════ الاختبار B — المستخدم غير المصرّح له ══════════════════════

    [LivePgFact]
    public async Task B_unauthorized_manual_price_is_rejected_by_the_backend_without_any_write()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await SeedItemAsync("R4-B1-" + Guid.NewGuid().ToString("N")[..6],
            ItemSalePrice, ItemCostPrice, taxRate: 0m, openingStock: 10m);

        var sequenceKey = $"SI-{DateTime.Now:yyyyMMdd}";

        long sequenceBefore;
        await using (var ctx = _pg.NewContext())
            sequenceBefore = (await ctx.Set<NumberSequence>().AsNoTracking()
                .FirstOrDefaultAsync(n => n.SequenceKey == sequenceKey))?.LastValue ?? 0L;

        // إرسال سعر يدوي 2.45 مباشرة إلى الـBackend (تجاوز الشاشة) → يجب الرفض
        await using (var ctx = _pg.NewContext())
        {
            var service = NewSalesService(ctx, UnauthorizedUser());
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.CreateAsync(Invoice(0m, (itemId, 1m, ManualLinePrice))));

            Assert.Contains("Sales.Edit", ex.Message);
        }

        // لا أثر إطلاقًا: لا بند، ولا فاتورة، ولا رقم تسلسلي محروق
        await using (var ctx = _pg.NewContext())
        {
            Assert.False(await ctx.Set<SalesInvoiceLine>().AnyAsync(l => l.ItemId == itemId));
            Assert.False(await ctx.Set<SalesInvoice>().AnyAsync(i => i.Lines.Any(l => l.ItemId == itemId)));

            var sequenceAfter = (await ctx.Set<NumberSequence>().AsNoTracking()
                .FirstOrDefaultAsync(n => n.SequenceKey == sequenceKey))?.LastValue ?? 0L;
            Assert.Equal(sequenceBefore, sequenceAfter);
        }
    }

    [LivePgFact]
    public async Task B_unauthorized_user_can_still_invoice_with_the_default_item_price()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await SeedItemAsync("R4-B2-" + Guid.NewGuid().ToString("N")[..6],
            ItemSalePrice, ItemCostPrice, taxRate: 0m, openingStock: 10m);

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await NewSalesService(ctx, UnauthorizedUser())
                .CreateAsync(Invoice(0m, (itemId, 2m, ItemSalePrice)));

            invoiceId = invoice.Id;
            Assert.Equal(ItemSalePrice, invoice.Lines.Single().UnitPrice);
            Assert.Equal(ItemSalePrice * 2m, invoice.Lines.Single().LineTotal);
        }

        await using (var ctx = _pg.NewContext())
        {
            var reopened = await NewSalesService(ctx, UnauthorizedUser()).GetByIdAsync(invoiceId);
            Assert.Equal(ItemSalePrice, reopened!.Lines.Single().UnitPrice);
            Assert.Equal(ItemSalePrice, (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId)).SalePrice);
        }
    }


    // ══════════════════════ الاختبار C — الحسابات بعد تعديل السعر ══════════════════════
    // البيانات المعروفة: صنف أ (بيع 2.38 / تكلفة 1.75 / ضريبة 16%) وصنف ب (بيع 5.00 / تكلفة 2.00 / ضريبة 16%).
    // البنود: أ 2 × 2.45 (سعر يدوي مصرّح) ، ب 2 × 5.00 (سعر الصنف) ، خصم رأس الفاتورة 10%.
    // المتوقع بالحساب اليدوي:
    //   LineTotal:           أ = 4.90 ، ب = 10.00   → SubTotal = 14.90
    //   DiscountAmount:      14.90 × 10% = 1.49     → الصافي قبل الضريبة = 13.41
    //   توزيع الخصم:         أ = 0.49 ، ب = 1.00    (مجموعهما = 1.49)
    //   صافي السطر + ضريبة:  أ = 4.41 → 0.71  ،  ب = 9.00 → 1.44   → TaxAmount = 2.15
    //   TotalAmount:         13.41 + 2.15 = 15.56
    //   UnitCost (لقطة):     أ = 1.75 ، ب = 2.00   → COGS = 7.50
    [LivePgFact]
    public async Task C_totals_discount_tax_journal_and_cogs_follow_the_actual_line_price()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var itemA = await SeedItemAsync("R4-C-A-" + Guid.NewGuid().ToString("N")[..5],
            salePrice: 2.38m, costPrice: 1.75m, taxRate: 16m, openingStock: 10m);
        var itemB = await SeedItemAsync("R4-C-B-" + Guid.NewGuid().ToString("N")[..5],
            salePrice: 5.00m, costPrice: 2.00m, taxRate: 16m, openingStock: 10m);

        await using var ctx = _pg.NewContext();
        var invoice = await NewSalesService(ctx, AuthorizedUser())
            .CreateAsync(Invoice(10m, (itemA, 2m, ManualLinePrice), (itemB, 2m, 5.00m)));

        var lineA = invoice.Lines.Single(l => l.ItemId == itemA);
        var lineB = invoice.Lines.Single(l => l.ItemId == itemB);

        // ── البنود ──
        Assert.Equal(ManualLinePrice, lineA.UnitPrice);   // سعر السطر الفعلي بعد التعديل
        Assert.Equal(2m, lineA.Quantity);
        Assert.Equal(4.90m, lineA.LineTotal);             // 2 × 2.45
        Assert.Equal(16m, lineA.TaxRate);                 // لقطة نسبة ضريبة الصنف
        Assert.Equal(1.75m, lineA.UnitCost);              // لقطة التكلفة (COGS) — لم تتغيّر

        Assert.Equal(5.00m, lineB.UnitPrice);
        Assert.Equal(10.00m, lineB.LineTotal);            // 2 × 5.00
        Assert.Equal(16m, lineB.TaxRate);
        Assert.Equal(2.00m, lineB.UnitCost);

        // ── رأس الفاتورة ──
        Assert.Equal(14.90m, invoice.SubTotal);
        Assert.Equal(10m, invoice.DiscountPercentage);
        Assert.Equal(1.49m, invoice.DiscountAmount);

        // توزيع الخصم على السطور (تناسبيًا مع سعر السطر الفعلي) — يظهر في ضريبة كل سطر:
        Assert.Equal(0.71m, lineA.TaxAmount);             // صافي 4.41 × 16%
        Assert.Equal(1.44m, lineB.TaxAmount);             // صافي 9.00 × 16%
        Assert.Equal(2.15m, lineA.TaxAmount + lineB.TaxAmount);

        Assert.Equal(2.15m, invoice.TaxAmount);           // مجموع ضريبة البنود = ضريبة الفاتورة
        Assert.Equal(15.56m, invoice.TotalAmount);

        // ── قيد البيع: مدين الصندوق 1100 = الإجمالي، دائن الإيراد 4100 = الصافي، دائن الضريبة 2100 ──
        Assert.NotNull(invoice.SalesJournalEntryId);
        var saleEntry = await ctx.Set<JournalEntry>().AsNoTracking()
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .FirstAsync(e => e.Id == invoice.SalesJournalEntryId);

        Assert.Equal(15.56m, DebitOf(saleEntry, "1100"));
        Assert.Equal(13.41m, CreditOf(saleEntry, "4100"));
        Assert.Equal(2.15m, CreditOf(saleEntry, "2100"));
        Assert.Equal(saleEntry.Lines.Sum(l => l.DebitAmount), saleEntry.Lines.Sum(l => l.CreditAmount));

        // ── قيد التكلفة COGS: مدين 5100 = Σ(كمية × لقطة التكلفة) ، دائن المخزون 1300 ──
        Assert.NotNull(invoice.CogsJournalEntryId);
        var cogsEntry = await ctx.Set<JournalEntry>().AsNoTracking()
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .FirstAsync(e => e.Id == invoice.CogsJournalEntryId);

        Assert.Equal(7.50m, DebitOf(cogsEntry, "5100"));  // 2×1.75 + 2×2.00
        Assert.Equal(7.50m, CreditOf(cogsEntry, "1300"));
        Assert.Equal(cogsEntry.Lines.Sum(l => l.DebitAmount), cogsEntry.Lines.Sum(l => l.CreditAmount));

        // ── القيم المتوقعة مقابل الفعلية (تظهر في سجل الاختبار للتقرير) ──
        _out.WriteLine($"[C] السطر أ: السعر متوقع 2.45 / فعلي {lineA.UnitPrice}");
        _out.WriteLine($"[C] السطر ب: السعر متوقع 5.00 / فعلي {lineB.UnitPrice}");
        _out.WriteLine($"[C] LineTotal أ: متوقع 4.90 / فعلي {lineA.LineTotal}");
        _out.WriteLine($"[C] LineTotal ب: متوقع 10.00 / فعلي {lineB.LineTotal}");
        _out.WriteLine($"[C] SubTotal: متوقع 14.90 / فعلي {invoice.SubTotal}");
        _out.WriteLine($"[C] DiscountAmount: متوقع 1.49 / فعلي {invoice.DiscountAmount}");
        _out.WriteLine($"[C] ضريبة السطر أ بعد توزيع الخصم: متوقع 0.71 / فعلي {lineA.TaxAmount}");
        _out.WriteLine($"[C] ضريبة السطر ب بعد توزيع الخصم: متوقع 1.44 / فعلي {lineB.TaxAmount}");
        _out.WriteLine($"[C] TaxAmount: متوقع 2.15 / فعلي {invoice.TaxAmount}");
        _out.WriteLine($"[C] TotalAmount: متوقع 15.56 / فعلي {invoice.TotalAmount}");
        _out.WriteLine($"[C] قيد البيع 1100 مدين: متوقع 15.56 / فعلي {DebitOf(saleEntry, "1100")}");
        _out.WriteLine($"[C] قيد البيع 4100 دائن: متوقع 13.41 / فعلي {CreditOf(saleEntry, "4100")}");
        _out.WriteLine($"[C] قيد البيع 2100 دائن: متوقع 2.15 / فعلي {CreditOf(saleEntry, "2100")}");
        _out.WriteLine($"[C] COGS 5100 مدين: متوقع 7.50 / فعلي {DebitOf(cogsEntry, "5100")}");
        _out.WriteLine($"[C] COGS 1300 دائن: متوقع 7.50 / فعلي {CreditOf(cogsEntry, "1300")}");
    }


    // ضريبة السطر تُحسب من لقطة نسبة ضريبة الصنف المحفوظة على السطر (TaxRate/TaxAmount) بعد تعديل السعر.
    // صنف أ ضريبة 16% (1 × 2.45 = 2.45 → 0.39) وصنف ب ضريبة 0% (1 × 10.00 → 0.00).
    [LivePgFact]
    public async Task C_line_tax_uses_the_saved_line_tax_rate_snapshot_after_the_price_edit()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var itemA = await SeedItemAsync("R4-C2-A-" + Guid.NewGuid().ToString("N")[..5],
            salePrice: 2.38m, costPrice: 1.75m, taxRate: 16m, openingStock: 10m);
        var itemB = await SeedItemAsync("R4-C2-B-" + Guid.NewGuid().ToString("N")[..5],
            salePrice: 10.00m, costPrice: 4.00m, taxRate: 0m, openingStock: 10m);

        await using var ctx = _pg.NewContext();
        var invoice = await NewSalesService(ctx, AuthorizedUser())
            .CreateAsync(Invoice(0m, (itemA, 1m, ManualLinePrice), (itemB, 1m, 10.00m)));

        var lineA = invoice.Lines.Single(l => l.ItemId == itemA);
        var lineB = invoice.Lines.Single(l => l.ItemId == itemB);

        Assert.Equal(2.45m, lineA.LineTotal);
        Assert.Equal(0.39m, lineA.TaxAmount);              // round(2.45 × 16% , 2)
        Assert.Equal(16m, lineA.TaxRate);

        Assert.Equal(10.00m, lineB.LineTotal);
        Assert.Equal(0.00m, lineB.TaxAmount);              // ضريبة الصنف 0%
        Assert.Equal(0m, lineB.TaxRate);

        Assert.Equal(12.45m, invoice.SubTotal);
        Assert.Equal(0.00m, invoice.DiscountAmount);
        Assert.Equal(0.39m, invoice.TaxAmount);
        Assert.Equal(12.84m, invoice.TotalAmount);         // 12.45 + 0.39
        Assert.Equal(3.13m, invoice.TaxRate);              // نسبة فعالة مرجّحة: 0.39 / 12.45 × 100

        _out.WriteLine($"[C2] ضريبة السطر أ (صنف 16%): متوقع 0.39 / فعلي {lineA.TaxAmount}");
        _out.WriteLine($"[C2] ضريبة السطر ب (صنف 0%): متوقع 0.00 / فعلي {lineB.TaxAmount}");
        _out.WriteLine($"[C2] SubTotal: متوقع 12.45 / فعلي {invoice.SubTotal}");
        _out.WriteLine($"[C2] TaxAmount: متوقع 0.39 / فعلي {invoice.TaxAmount}");
        _out.WriteLine($"[C2] TotalAmount: متوقع 12.84 / فعلي {invoice.TotalAmount}");
        _out.WriteLine($"[C2] نسبة الضريبة الفعالة: متوقع 3.13 / فعلي {invoice.TaxRate}");
    }

    // ══════════════════════ الاختبار D — حفظ وإعادة فتح (قيمة قاعدة البيانات ثم قيمة العرض) ══════════════════════

    [LivePgFact]
    public async Task D_stored_database_values_match_the_reopened_invoice()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        var itemId = await SeedItemAsync("R4-D-" + Guid.NewGuid().ToString("N")[..6],
            ItemSalePrice, ItemCostPrice, taxRate: 0m, openingStock: 10m);

        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await NewSalesService(ctx, AuthorizedUser())
                .CreateAsync(Invoice(0m, (itemId, 1m, ManualLinePrice)));
            invoiceId = invoice.Id;
        }

        await using (var ctx = _pg.NewContext())
        {
            // القيمة الرقمية المحفوظة فعليًا في قاعدة البيانات (قراءة خام من الأعمدة numeric(18,2))
            var storedLinePrice = await ctx.Database
                .SqlQueryRaw<decimal>("SELECT \"UnitPrice\" AS \"Value\" FROM \"SalesInvoiceLines\" WHERE \"SalesInvoiceId\" = {0}", invoiceId)
                .SingleAsync();
            var storedLineTotal = await ctx.Database
                .SqlQueryRaw<decimal>("SELECT \"LineTotal\" AS \"Value\" FROM \"SalesInvoiceLines\" WHERE \"SalesInvoiceId\" = {0}", invoiceId)
                .SingleAsync();
            var storedItemPrice = await ctx.Database
                .SqlQueryRaw<decimal>("SELECT \"SalePrice\" AS \"Value\" FROM \"Items\" WHERE \"Id\" = {0}", itemId)
                .SingleAsync();

            Assert.Equal(ManualLinePrice, storedLinePrice);
            Assert.Equal(ManualLinePrice, storedLineTotal);
            Assert.Equal(ItemSalePrice, storedItemPrice);

            // القيمة المعروضة بعد إعادة فتح الفاتورة (نفس مسار الشاشة: GetByIdAsync ← SalesInvoiceLineDto.UnitPrice)
            var reopened = await NewSalesService(ctx, AuthorizedUser()).GetByIdAsync(invoiceId);

            Assert.NotNull(reopened);
            var reopenedLine = reopened!.Lines.Single();
            Assert.Equal(ManualLinePrice, reopenedLine.UnitPrice);
            Assert.Equal(ManualLinePrice, reopenedLine.LineTotal);
            Assert.Equal(ItemCostPrice, reopenedLine.UnitCost);   // التكلفة تُحفظ كلقطة منفصلة عن السعر
            Assert.Equal("2.45", reopenedLine.UnitPrice.ToString("N2", CultureInfo.InvariantCulture));
            Assert.Equal(ItemSalePrice, (await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId)).SalePrice);

            _out.WriteLine($"[D] UnitPrice محفوظ في قاعدة البيانات: متوقع 2.45 / فعلي {storedLinePrice}");
            _out.WriteLine($"[D] LineTotal محفوظ في قاعدة البيانات: متوقع 2.45 / فعلي {storedLineTotal}");
            _out.WriteLine($"[D] Item.SalePrice في قاعدة البيانات: متوقع 2.38 / فعلي {storedItemPrice}");
            _out.WriteLine($"[D] UnitPrice بعد إعادة فتح الفاتورة: متوقع 2.45 / فعلي {reopenedLine.UnitPrice}");
            _out.WriteLine($"[D] UnitCost (لقطة التكلفة): متوقع 1.75 / فعلي {reopenedLine.UnitCost}");
        }
    }


}
