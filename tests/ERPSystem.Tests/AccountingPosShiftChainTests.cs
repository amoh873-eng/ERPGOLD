using ERPSystem.Application.DTOs.CashDrawer;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.DTOs.Shifts;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المرحلة 5 — سلسلة نقاط البيع الكاملة (2026-10-25):
/// فتح وردية (فكة من الخزنة) ⇒ بيع POS نقدي ⇒ مردود POS ⇒ تحويلات درج الكاش ⇒ ملخّص متوقَّع ⇒
/// إغلاق بفرق ⇒ قيد الفرق ⇒ الأستاذ العام وميزان المراجعة وتقارير الورديات/التحويلات.
///
/// تُفحص المعادلة **مستقلًّة عن الخدمة** (تُحسب في الاختبار من المستندات والتحويلات ثم تُقارن
/// بما تُخرجه الخدمة وبما هو مسجَّل في دفتر القيود).
/// </summary>
public class AccountingPosShiftChainTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public AccountingPosShiftChainTests(AccountingPgFixture pg) => _pg = pg;

    private static string Money(decimal v)
        => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

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

    private async Task<Guid> NewItemWithStockAsync(string suffix, decimal quantity, decimal unitCost,
        decimal salePrice)
    {
        await using var ctx = _pg.NewContext();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Code = "POS-" + suffix,
            NameAr = "صنف نقطة بيع " + suffix,
            CategoryId = _pg.CategoryId,
            UnitId = _pg.UnitId,
            SalePrice = salePrice,
            CostPrice = unitCost,
            TaxRate = 0m,
            TracksBatches = false,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        await new StockMovementService(ctx, new AccountingPgFixture.AllowAllManualPrice())
            .CreateAsync(new CreateStockMovementDto
            {
                ItemId = item.Id,
                WarehouseId = _pg.WarehouseId,
                MovementType = (int)MovementType.OpeningBalance,
                Quantity = quantity,
                UnitCost = unitCost,
                MovementDate = DateTime.Today,
                ReferenceNumber = "POS-OPEN-" + suffix
            });

        return item.Id;
    }

    [LivePgFact]
    public async Task Pos_shift_chain_reconciles_expected_cash_and_posts_the_variance_once()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();

        var itemId = await NewItemWithStockAsync(Guid.NewGuid().ToString("N")[..6], 100m, 10m, 20m);
        var cashier = "e2e-pos-" + Guid.NewGuid().ToString("N")[..6];

        var till1105Before = await LedgerNetAsync("1105");
        var varianceBefore = await LedgerNetAsync("5110");

        // ── (1) فتح الوردية بفكة 100 (تُموّل من الخزنة 1100 إلى الدرج 1105)
        Guid shiftId;
        await using (var ctx = _pg.NewContext())
        {
            var shift = await new CashierShiftService(ctx, new JournalEntryService(ctx))
                .StartAsync(100m, cashier);
            shiftId = shift.Id;
            Assert.Equal(100m, shift.OpeningFloatAmount);
            Assert.Equal((int)CashierShiftStatus.Open, shift.Status);
            // الوردية المفتوحة تحمل رقم **حركة التمويل** (CashDrawerTransaction)؛ أما JournalEntryId
            // فيُملأ عند الإغلاق بقيد تسوية الفرق فقط ⇒ يبقى فارغًا طالما لا يوجد فرق مرصود بعد.
            Assert.NotEqual(Guid.Empty, shift.OpeningTransferId);
            Assert.Null(shift.JournalEntryId);
        }

        // حركة الفكة الافتتاحية موثَّقة بقيد محاسبي (1105 مدين / 1100 دائن)
        Guid openingTransferId;
        await using (var ctx = _pg.NewContext())
        {
            openingTransferId = await ctx.Set<CashierShift>().AsNoTracking()
                .Where(s => s.Id == shiftId).Select(s => s.OpeningTransferId).FirstAsync();
        }
        await using (var ctx = _pg.NewContext())
        {
            var opening = await ctx.Set<CashDrawerTransaction>().AsNoTracking()
                .FirstAsync(t => t.Id == openingTransferId);
            Assert.Equal(CashTransferDirection.FloatIn, opening.Type);
            Assert.Equal(100m, opening.Amount);
            Assert.NotEqual(Guid.Empty, opening.JournalEntryId);
        }

        // وردية ثانية لنفس الكاشير قبل الإغلاق ⇒ مرفوضة (لا ورديتان مفتوحتان)
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new CashierShiftService(ctx, new JournalEntryService(ctx)).StartAsync(50m, cashier));
        }

        // ── (2) بيع نقطة بيع نقدي 3 × 20 = 60 (يُدين 1105 لا 1100)
        Guid posInvoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                IsPos = true,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = 3m, UnitPrice = 20m }
                }
            });
            posInvoiceId = created.Id;
        }

        // ── (3) مردود نقطة بيع نقدي 1 × 20 = 20 (يُقيَّد دائن 1105)
        await using (var ctx = _pg.NewContext())
        {
            await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = posInvoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto>
                {
                    new() { ItemId = itemId, Quantity = 1m, UnitPrice = 20m }
                }
            });
        }

        // ── (4) تحويلات الدرج: تمويل 30 + سحب 10 (وقتها بعد بدء الوردية)
        Guid floatInId, cashOutId;
        await using (var ctx = _pg.NewContext())
        {
            var drawer = new CashDrawerService(ctx, new JournalEntryService(ctx));
            var floatIn = await drawer.CreateAsync(new CreateCashTransferDto
            {
                Type = CashTransferDirection.FloatIn,
                Amount = 30m,
                Timestamp = DateTime.Now,
                Notes = "تمويل منتصف الوردية"
            }, cashier);
            floatInId = floatIn.Id;
        }
        await using (var ctx = _pg.NewContext())
        {
            var drawer = new CashDrawerService(ctx, new JournalEntryService(ctx));
            var cashOut = await drawer.CreateAsync(new CreateCashTransferDto
            {
                Type = CashTransferDirection.CashOut,
                Amount = 10m,
                Timestamp = DateTime.Now,
                Notes = "تحصين جزئي"
            }, cashier);
            cashOutId = cashOut.Id;
        }

        // ── (5) الملخّص: المتوقَّع محسوب مستقلًّا في الاختبار ثم يُقارَن بمخرجات الخدمة
        //      100 (فكة) + 60 (بيع POS) − 20 (مردود POS) + 30 (تمويل) − 10 (سحب) = 160
        var expected = 160m;
        await using (var ctx = _pg.NewContext())
        {
            var summary = await new CashierShiftService(ctx, new JournalEntryService(ctx))
                .ComputeSummaryAsync(shiftId);

            Assert.Equal(100m, summary.OpeningFloat);
            Assert.Equal(60m, summary.CashSales);
            Assert.Equal(20m, summary.CashRefunds);
            Assert.Equal(30m, summary.FloatsIn);
            Assert.Equal(10m, summary.FloatsOut);
            Assert.Equal(expected, summary.Expected);
        }

        // الدرج قبل الإغلاق: 100 + 60 − 20 + 30 − 10 = 160
        Assert.Equal(till1105Before + 160m, await LedgerNetAsync("1105"));

        // ── (6) إغلاق بعدّ 155 ⇒ عجز 5 (مدين فروقات الصندوق 5110 / دائن الدرج 1105)
        await using (var ctx = _pg.NewContext())
        {
            var closed = await new CashierShiftService(ctx, new JournalEntryService(ctx))
                .CloseAsync(shiftId, 155m, "فحص متكامل", "e2e-pos");
            Assert.Equal((int)CashierShiftStatus.Closed, closed.Status);
            Assert.Equal(expected, closed.ExpectedCashAmount);
            Assert.Equal(155m, closed.CountedCashAmount);
            Assert.Equal(-5m, closed.VarianceAmount);
            Assert.NotNull(closed.JournalEntryId);
        }

        // إغلاق مكرر ⇒ مرفوض بلا أي قيد إضافي
        await using (var ctx = _pg.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new CashierShiftService(ctx, new JournalEntryService(ctx))
                    .CloseAsync(shiftId, 155m, "إغلاق مكرر", "e2e-pos"));
        }

        // ── (7) الأثر الدفتري: الدرج = 155 (المعدود) وفروقات الصندوق = 5
        Assert.Equal(till1105Before + 155m, await LedgerNetAsync("1105"));
        Assert.Equal(varianceBefore + 5m, await LedgerNetAsync("5110"));

        // ── (8) التقارير والشاشات: سجل التحويلات وسجل الورديات يعرضان المستندات نفسها
        await using (var ctx = _pg.NewContext())
        {
            var transfers = await new CashDrawerService(ctx, new JournalEntryService(ctx)).GetAsync(
                new CashDrawerFilterDto { From = DateTime.Today, To = DateTime.Today, Cashier = cashier });
            var mineIn = transfers.SingleOrDefault(t => t.Id == floatInId);
            var mineOut = transfers.SingleOrDefault(t => t.Id == cashOutId);
            Assert.NotNull(mineIn);
            Assert.NotNull(mineOut);
            Assert.Equal(30m, mineIn!.Amount);
            Assert.Equal(CashTransferDirection.FloatIn, mineIn.Type);
            Assert.Equal(10m, mineOut!.Amount);
            Assert.Equal(CashTransferDirection.CashOut, mineOut.Type);
            Assert.False(string.IsNullOrWhiteSpace(mineIn.EntryNumber));

            var history = await new CashierShiftService(ctx, new JournalEntryService(ctx)).GetHistoryAsync(
                new CashierShiftFilterDto { From = DateTime.Today, To = DateTime.Today, Cashier = cashier });
            var closedShift = history.SingleOrDefault(s => s.Id == shiftId);
            Assert.NotNull(closedShift);
            Assert.Equal((int)CashierShiftStatus.Closed, closedShift!.Status);
            Assert.Equal(160m, closedShift.ExpectedCashAmount);
            Assert.Equal(-5m, closedShift.VarianceAmount);

            // مطابقة الرقابي: سيناريو نقاط البيع لا يُنشئ أي حركة غير مُسندة على 1200/2200
            var recon = await new SubLedgerReconciliationService(ctx).GetReportAsync();
            Assert.True(recon.UnattributedControlLines.Count == 0,
                "بنود رقابية غير مُسندة:\n" + string.Join("\n", recon.UnattributedControlLines.Select(
                    l => $"{l.AccountCode} {l.EntryNumber} {l.EntryTypeNameAr} {l.Reference} {Money(l.SignedAmount)}")));
            Assert.Equal(0m, Math.Round(recon.TotalUnattributedAmount, 2));

            var trial = await new ReportService(ctx).GetTrialBalanceAsync();
            Assert.True(trial.IsBalanced,
                $"ميزان المراجعة غير متوازن: مدين={trial.TotalDebit:N2} دائن={trial.TotalCredit:N2}");
        }

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
