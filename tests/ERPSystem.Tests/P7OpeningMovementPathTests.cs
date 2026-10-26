using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// P7/G1 — مسار «رصيد افتتاحي»: فصل الواجهة عن المسار المالي الرسمي.
///
/// الحقيقة المُثبتة بالكود (قبل أي تعديل):
///   • `StockMovementService` (مسار شاشة حركات المخزون) **كميّ/تكلفة فقط**: لا مرجع لأي
///     `Journal`/`Account` في الملف كله ⇒ لا قيد على 1300 (مُقاس: قيمة مخزون 1000 مقابل أثر 1300 = 0).
///   • المسار المالي المعتمد للأرصدة الافتتاحية هو **دفعة الأرصدة الافتتاحية**
///     (`OpeningBalanceService`) التي تُرحّل 1300 + تُنشئ حركة المخزون وتربطهما (`StockMovementId`).
/// لذلك كان عرض نوع «رصيد افتتاحي» في شاشة الحركات يُنتج **قيمة مخزون بلا قيد** (وهم اتساق)،
/// وأُزيل من سطح الإدخال مع توجيه صريح للمسار الرسمي — بلا إنشاء أي قيد جديد وبلا ازدواج.
/// </summary>
public class P7OpeningMovementPathTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P7OpeningMovementPathTests(AccountingPgFixture pg) => _pg = pg;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Web")))
            dir = dir.Parent;

        Assert.True(dir is not null, "تعذر تحديد جذر المستودع من مجلد تشغيل الاختبارات");
        return dir!.FullName;
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"الملف غير موجود: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Movements_screen_no_longer_offers_opening_balance_and_guides_to_the_official_path()
    {
        var dialog = ReadSource("src/Web/Components/Pages/Inventory/MovementFormDialog.razor");
        Assert.DoesNotContain("Value=\"3\"", dialog);                       // نوع «رصيد افتتاحي» أُزيل
        Assert.Contains("OpeningBalanceOfficialPathHint", dialog);          // توجيه صريح
        Assert.Contains("/accounting/opening-balances", dialog);            // رابط المسار الرسمي

        var page = ReadSource("src/Web/Components/Pages/Inventory/Movements.razor");
        Assert.Contains("OpeningBalanceOfficialPathHint", page);
        Assert.Contains("/accounting/opening-balances", page);

        // مفتاح التعريب موجود في اللغتين (لا نص صلب في الواجهة)
        foreach (var resx in new[]
                 {
                     "src/Web/Resources/SharedResources.resx",
                     "src/Web/Resources/SharedResources.en.resx"
                 })
        {
            var content = ReadSource(resx);
            Assert.Contains("name=\"OpeningBalanceOfficialPathHint\"", content);
            Assert.Contains("name=\"OpeningBalances\"", content);
        }
    }

    /// <summary>
    /// حرس موثَّق (Fail-loud): خدمة حركات المخزون مسار كميّ/تكلفة بلا قيد **بالتصميم**.
    /// أي إضافة قيد جزئي هنا ستُضاعف مع مسارات المستندات (الفاتورة تُرحّل 1300 وCOGS) —
    /// لذلك يفشل هذا الفحص للمراجعة قبل أي تغيير من هذا النوع.
    /// </summary>
    [Fact]
    public void Stock_movement_service_remains_quantity_only_by_design()
    {
        var core = ReadSource("src/Application/Services/StockMovementService.cs");
        var partial = ReadSource("src/Application/Services/StockMovementService.Partial.cs");

        Assert.DoesNotContain("PrepareEntryAsync", core);
        Assert.DoesNotContain("PrepareEntryAsync", partial);
        Assert.DoesNotContain("JournalEntryLine", core);
        Assert.DoesNotContain("JournalEntryLine", partial);
    }

    /// <summary>المسار الرسمي: دفعة الأرصدة الافتتاحية تُبقي قيمة المخزون وأثر 1300 متطابقين.</summary>
    [LivePgFact]
    public async Task Official_opening_balance_batch_keeps_inventory_value_and_1300_matched()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var reference = "P7G1-" + suffix;
        var itemId = await _pg.CreateItemAsync(reference, salePrice: 20m, costPrice: 0m, openingStock: 0m);

        string itemCode;
        await using (var ctx = _pg.NewContext())
            itemCode = await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => i.Code).FirstAsync();

        var gl1300Before = await GlNet1300Async();

        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var batch = await svc.CreateDraftAsync(new CreateOpeningBalanceBatchDto
            {
                FiscalYear = DateTime.Today.Year,
                EntryDate = DateTime.Today,
                Reference = reference,
                Lines = new List<OpeningBalanceLineInput>
                {
                    new()
                    {
                        AccountCode = "1300",
                        DebitAmount = 1000m,
                        ItemCode = itemCode,
                        WarehouseCode = AccountingPgFixture.WarehouseCode,
                        Quantity = 100m,
                        UnitCost = 10m
                    },
                    new() { AccountCode = "1100", CreditAmount = 1000m }
                }
            }, "p7-test");

            await svc.PostAsync(batch.Id, "p7-test");
        }

        decimal stock, cost;
        await using (var ctx = _pg.NewContext())
        {
            var item = await ctx.Set<Item>().AsNoTracking().FirstAsync(i => i.Id == itemId);
            stock = item.CurrentStock;
            cost = item.CostPrice;
        }

        var gl1300Delta = Math.Round(await GlNet1300Async() - gl1300Before, 2);

        Assert.Equal(100m, stock);
        Assert.Equal(10m, cost);
        Assert.Equal(1000m, Math.Round(stock * cost, 2));   // قيمة المخزون
        Assert.Equal(1000m, gl1300Delta);                   // أثر القيد على 1300 ⇒ الفرق صفر

        // منع الازدواج: إعادة ترحيل نفس الدفعة مرفوضة (لا قيد ثانٍ ولا حركة ثانية)
        await using (var ctx = _pg.NewContext())
        {
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var posted = (await svc.GetAllAsync()).First(b => b.Reference == reference);

            await Assert.ThrowsAnyAsync<Exception>(() => svc.PostAsync(posted.Id, "p7-test"));
        }

        Assert.Equal(100m, await StockAsync(itemId));
        Assert.Equal(1000m, Math.Round(await GlNet1300Async() - gl1300Before, 2));
    }

    private async Task<decimal> StockAsync(Guid itemId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId)
            .Select(i => i.CurrentStock).FirstAsync();
    }

    private async Task<decimal> GlNet1300Async()
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == "1300" && !a.IsDeleted).Select(a => a.Id).FirstAsync();
        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && !l.IsDeleted
                        && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }
}
