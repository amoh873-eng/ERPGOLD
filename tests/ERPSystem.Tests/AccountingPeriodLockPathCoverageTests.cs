using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.DTOs.Expenses;
using ERPSystem.Application.DTOs.Inventory;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Movements;
using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.DTOs.Vouchers;
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
/// تغطية قفل الفترات على **كل مسارات الترحيل والعكس المدعومة** (2026-10-25).
///
/// السلوك المُثبت (سياسة مُوثَّقة تحتاج تأكيد المالك لنسخ أشد):
///   • الفترة المغلقة ترفض **الترحيل الجديد** من كل المسارات (قيد يدوي · فاتورة بيع · مردود بيع ·
///     فاتورة مشتريات · مردود مشتريات · سند قبض · سند صرف · مصروف · إتلاف مخزون · رصيد افتتاحي)
///     عبر الحارس المركزي في JournalEntryService.PrepareEntryAsync.
///   • **العكس** يُقيَّد بتاريخ قيد العكس لا بتاريخ الأصل: عكس في شهر مغلق ⇒ مرفوض، وعكس في شهر
///     مفتوح ⇒ مسموح (ولا يُعدَّل أي قيد تاريخي).
///   • الإغلاق **لا يغيّر أي رصيد ولا يُنشئ قيدًا**.
/// </summary>
public class AccountingPeriodLockPathCoverageTests : IClassFixture<AccountingPgFixture>
{
    internal const int Year = 2085;
    internal const int ClosedMonth = 6;

    private readonly AccountingPgFixture _pg;

    public AccountingPeriodLockPathCoverageTests(AccountingPgFixture pg) => _pg = pg;

    internal static DateTime ClosedDate => new(Year, ClosedMonth, 15);
    internal static DateTime OpenDate => new(Year, 4, 15);

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
        public string? CurrentUserName() => "period-path-probe";
    }

    internal sealed class AllowAllPeriods : IAccountingPeriodAuthorization
    {
        public bool CanView() => true;
        public bool CanClose() => true;
        public bool CanReopen() => true;
    }

    private static ReceiptVoucherService Receipts(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    private static PaymentVoucherService Payments(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    private static PaymentVoucherReversalService Reversals(AppDbContext ctx)
        => new(ctx, new JournalEntryService(ctx), new AllowAllVouchers());

    // ───────────────────────── أدوات ─────────────────────────

    internal async Task<Guid> NewItemWithStockAsync(string suffix, decimal quantity, decimal unitCost)
    {
        Guid itemId;
        await using (var ctx = _pg.NewContext())
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                Code = "PLK-" + suffix,
                NameAr = "صنف فحص القفل " + suffix,
                CategoryId = _pg.CategoryId,
                UnitId = _pg.UnitId,
                SalePrice = unitCost * 2m,
                CostPrice = unitCost,
                TaxRate = 0m,
                CreatedAt = DateTime.UtcNow
            };
            ctx.Add(item);
            await ctx.SaveChangesAsync();
            itemId = item.Id;

            await new StockMovementService(ctx, new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreateStockMovementDto
                {
                    ItemId = itemId,
                    WarehouseId = _pg.WarehouseId,
                    MovementType = (int)MovementType.OpeningBalance,
                    Quantity = quantity,
                    UnitCost = unitCost,
                    MovementDate = new DateTime(Year, 1, 20),
                    ReferenceNumber = "PLK-SEED-" + suffix
                });
        }
        return itemId;
    }

    internal async Task<Guid> NewExpenseCategoryAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var existing = await ctx.Set<ExpenseCategory>().AsNoTracking()
            .Where(c => c.Code == code).Select(c => c.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty) return existing;

        // بند المصروف يلزمه حساب متحرك (FK) — نستخدم حساب مصروفات قائمًا (5200) بلا ابتكار شجرة جديدة.
        var accountId = (await PostingAccountResolver.ResolveAsync(ctx, "5200")).Id;

        var category = new ExpenseCategory
        {
            Id = Guid.NewGuid(),
            Code = code,
            NameAr = "بند مصروف فحص",
            AccountId = accountId,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(category);
        await ctx.SaveChangesAsync();
        return category.Id;
    }

    /// <summary>يغلق شهرًا بشكل قابل للتكرار (يتجاوز الإغلاق المسبق) — لاستقلال الفحوص عن ترتيب التنفيذ.</summary>
    internal async Task EnsureClosedAsync(int month)
    {
        await using var ctx = _pg.NewContext();
        var svc = new AccountingPeriodService(ctx, new AllowAllPeriods());
        try
        {
            await svc.CloseAsync(Year, month, "فحص تغطية المسارات", "tester");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("مغلقة بالفعل"))
        {
            // مغلقة مسبقًا من فحص آخر في نفس القاعدة — لا شيء يُفعل.
        }
    }

    /// <summary>يؤكد أن المسار يرفض الترحيل في الفترة المغلقة — مع بيان السلوك الفعلي عند الاختلاف.</summary>
    internal static async Task AssertClosedAsync(string path, Func<Task> action)
    {
        var ex = await Record.ExceptionAsync(action);

        Assert.True(ex is PeriodClosedException,
            $"المسار «{path}»: المتوقع PeriodClosedException، والفعلي: " +
            (ex is null ? "لا استثناء (تم الترحيل!)" : $"{ex.GetType().Name} — {ex.Message}"));
    }

    private async Task<int> EntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().CountAsync(e => !e.IsDeleted);
    }

    // ═══════════ 1) كل مسارات الترحيل ترفض الفترة المغلقة ═══════════

    [LivePgFact]
    public async Task Closed_period_rejects_posting_on_every_supported_path()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();   // رصيد الصندوق لازمة لمشتريات نقدية ولسند الصرف

        // (0) متطلبات مسبقة في **شهر مفتوح** (كي يصل كل مسار إلى مرحلة القيد لا إلى فحص آخر).
        var salesItem = await NewItemWithStockAsync("S1", 100m, 10m);
        Guid salesInvoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = OpenDate,
                InvoiceType = (int)SalesInvoiceType.OnAccount,
                PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = salesItem, Quantity = 10m, UnitPrice = 20m }
                }
            });
            salesInvoiceId = invoice.Id;
        }

        var purchaseItem = await NewItemWithStockAsync("P1", 5m, 10m);
        Guid purchaseInvoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await new PurchaseInvoiceService(
                    ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreatePurchaseInvoiceDto
                {
                    SupplierId = _pg.SupplierId,
                    WarehouseId = _pg.WarehouseId,
                    InvoiceDate = OpenDate,
                    InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                    PaymentMethod = CreatePurchaseInvoiceDto.PaymentOnAccount,
                    Lines = new List<CreatePurchaseInvoiceLineDto>
                    {
                        new() { ItemId = purchaseItem, Quantity = 4m, UnitCost = 15m }
                    }
                });
            purchaseInvoiceId = invoice.Id;
        }

        var expenseCategoryId = await NewExpenseCategoryAsync("PLK-EXP");
        var writeOffItem = await NewItemWithStockAsync("W1", 30m, 7m);

        // (1) الإغلاق نفسه لا يغيّر أي رصيد ولا يُنشئ قيدًا (والإغلاق قابل للتكرار بلا أثر).
        var balancesBefore = await LedgerInvariants.AccountsAsync(_pg);
        var entriesBefore = await EntryCountAsync();
        await EnsureClosedAsync(ClosedMonth);
        Assert.Equal(entriesBefore, await EntryCountAsync());
        Assert.Equal(balancesBefore, await LedgerInvariants.AccountsAsync(_pg));

        await PostingPathsAsync(salesItem, salesInvoiceId, purchaseItem, purchaseInvoiceId,
            expenseCategoryId, writeOffItem);

        // (3) لا شيء تغيّر: نفس عدد القيود ونفس الأرصدة
        Assert.Equal(entriesBefore, await EntryCountAsync());
        Assert.Equal(balancesBefore, await LedgerInvariants.AccountsAsync(_pg));
        await LedgerInvariants.AssertConsistentAsync(_pg);
    }

    /// <summary>محاولات الترحيل العشرة — كل واحدة بتاريخ داخل الشهر المغلق.</summary>
    private async Task PostingPathsAsync(Guid salesItem, Guid salesInvoiceId, Guid purchaseItem,
        Guid purchaseInvoiceId, Guid expenseCategoryId, Guid writeOffItem)
    {
        await AssertClosedAsync("قيد يدوي", async () =>
        {
            await using var ctx = _pg.NewContext();
            var cashId = (await PostingAccountResolver.ResolveAsync(ctx, "1100")).Id;
            var taxId = (await PostingAccountResolver.ResolveAsync(ctx, "2100")).Id;
            await new JournalEntryService(ctx).CreateManualEntryAsync(ClosedDate, "PLK-MAN",
                "قيد داخل فترة مغلقة",
                new List<JournalEntryLegInput>
                {
                    new() { AccountId = cashId, DebitAmount = 10m },
                    new() { AccountId = taxId, CreditAmount = 10m }
                });
        });

        var saleItem2 = await NewItemWithStockAsync("S2", 20m, 5m);
        await AssertClosedAsync("فاتورة بيع نقدية", async () =>
        {
            await using var ctx = _pg.NewContext();
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = _pg.CustomerId,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = ClosedDate,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                TaxRate = 0m,
                Lines = new List<CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = saleItem2, Quantity = 1m, UnitPrice = 9m }
                }
            });
        });

        await AssertClosedAsync("مردود بيع", async () =>
        {
            await using var ctx = _pg.NewContext();
            await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = salesInvoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = ClosedDate,
                Lines = new List<CreateSalesReturnLineDto>
                {
                    new() { ItemId = salesItem, Quantity = 2m, UnitPrice = 20m }
                }
            });
        });

        var purchaseItem2 = await NewItemWithStockAsync("P2", 5m, 4m);
        await AssertClosedAsync("فاتورة مشتريات نقدية", async () =>
        {
            await using var ctx = _pg.NewContext();
            await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx),
                    new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreatePurchaseInvoiceDto
                {
                    SupplierId = _pg.SupplierId,
                    WarehouseId = _pg.WarehouseId,
                    InvoiceDate = ClosedDate,
                    InvoiceType = (int)PurchaseInvoiceType.Cash,
                    PaymentMethod = CreatePurchaseInvoiceDto.PaymentCash,
                    Lines = new List<CreatePurchaseInvoiceLineDto>
                    {
                        new() { ItemId = purchaseItem2, Quantity = 1m, UnitCost = 4m }
                    }
                });
        });

        await AssertClosedAsync("مردود مشتريات", async () =>
        {
            await using var ctx = _pg.NewContext();
            await new PurchaseReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(
                new CreatePurchaseReturnDto
                {
                    PurchaseInvoiceId = purchaseInvoiceId,
                    WarehouseId = _pg.WarehouseId,
                    ReturnDate = ClosedDate,
                    SettlementType = (int)PurchaseReturnSettlementType.OnAccountCredit,
                    Lines = new List<CreatePurchaseReturnLineDto>
                    {
                        new() { ItemId = purchaseItem, Quantity = 1m, UnitCost = 15m }
                    }
                });
        });

        await AssertClosedAsync("سند قبض (ترحيل)", async () =>
        {
            await using var ctx = _pg.NewContext();
            var svc = Receipts(ctx);
            var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = _pg.CustomerId,
                VoucherDate = ClosedDate,
                PaymentMethod = 1,
                Amount = 25m
            });
            await svc.ApproveAsync(draft.Id);
            await svc.PostAsync(draft.Id);
        });

        await AssertClosedAsync("سند صرف (ترحيل بتخصيص)", async () =>
        {
            await using var ctx = _pg.NewContext();
            var svc = Payments(ctx);
            var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = _pg.SupplierId,
                VoucherDate = ClosedDate,
                PaymentMethod = 1,
                Amount = 20m
            });
            await svc.ApproveAsync(draft.Id);
            await svc.PostAsync(draft.Id, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = purchaseInvoiceId, Amount = 20m }
            });
        });

        await AssertClosedAsync("مصروف", async () =>
        {
            await using var ctx = _pg.NewContext();
            await new ExpenseService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateExpenseEntryDto
            {
                ExpenseCategoryId = expenseCategoryId,
                ExpenseDate = ClosedDate,
                Amount = 30m,
                PaymentMethod = (int)PaymentMethod.Cash,
                Description = "مصروف داخل فترة مغلقة"
            });
        });

        await AssertClosedAsync("إتلاف مخزون", async () =>
        {
            await using var ctx = _pg.NewContext();
            await new StockWriteOffService(ctx, new JournalEntryService(ctx)).CreateAsync(
                new CreateStockWriteOffDto
                {
                    ItemId = writeOffItem,
                    WarehouseId = _pg.WarehouseId,
                    Quantity = 1m,
                    Reason = (int)StockWriteOffReason.Expired,
                    Date = ClosedDate,
                    Notes = "إتلاف داخل فترة مغلقة"
                }, "tester");
        });

        await AssertClosedAsync("رصيد افتتاحي (ترحيل مسودة)", async () =>
        {
            await using var ctx = _pg.NewContext();
            var svc = AccountingPgFixture.NewOpeningBalanceService(ctx);
            var draft = await svc.CreateDraftAsync(new CreateOpeningBalanceBatchDto
            {
                FiscalYear = Year,
                EntryDate = ClosedDate,
                Reference = "PLK-OB-CLOSED",
                Lines = new List<OpeningBalanceLineInput>
                {
                    new() { AccountCode = "1100", DebitAmount = 10m },
                    new() { AccountCode = "2100", CreditAmount = 10m }
                }
            }, "tester");
            await svc.PostAsync(draft.Id, "tester");
        });
    }

    // ═══════════ 2) العكس: يُقيَّد بتاريخ قيد العكس لا بتاريخ الأصل ═══════════

    [LivePgFact]
    public async Task Reversal_is_governed_by_the_reversal_date_and_open_period_keeps_working()
    {
        Assert.True(_pg.Available, "بيئة الاختبار الحي غير مهيأة");
        await _pg.SeedCashAsync();   // سند الصرف يحتاج رصيد صندوق كافيًا

        // شهر مُغلق خاص بهذا الفحص (2085-09) — لاستقلال الفحص عن ترتيب التنفيذ داخل الفئة.
        const int reversalLockMonth = 9;
        var lockedDate = new DateTime(Year, reversalLockMonth, 12);
        await EnsureClosedAsync(reversalLockMonth);

        // سند صرف مُرحَّل في شهر مفتوح (2085-04) + فاتورة مشتريات آجلة للتخصيص
        var itemId = await NewItemWithStockAsync("R1", 10m, 6m);
        Guid invoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await new PurchaseInvoiceService(ctx, new JournalEntryService(ctx),
                    new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreatePurchaseInvoiceDto
                {
                    SupplierId = _pg.SupplierId,
                    WarehouseId = _pg.WarehouseId,
                    InvoiceDate = OpenDate,
                    InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                    PaymentMethod = CreatePurchaseInvoiceDto.PaymentOnAccount,
                    Lines = new List<CreatePurchaseInvoiceLineDto>
                    {
                        new() { ItemId = itemId, Quantity = 2m, UnitCost = 6m }
                    }
                });
            invoiceId = invoice.Id;
        }

        Guid voucherId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Payments(ctx);
            var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = _pg.SupplierId,
                VoucherDate = OpenDate,
                PaymentMethod = 1,
                Amount = 12m
            });
            voucherId = draft.Id;
            await svc.ApproveAsync(voucherId);
            await svc.PostAsync(voucherId, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = invoiceId, Amount = 12m }
            });
        }

        Guid allocationId;
        await using (var ctx = _pg.NewContext())
        {
            allocationId = await ctx.Set<PaymentAllocation>().AsNoTracking()
                .Where(a => a.PaymentVoucherId == voucherId && a.Direction == 1)
                .Select(a => a.Id).FirstAsync();
        }

        CreatePaymentVoucherReversalDto ReversalDto(DateTime date) => new()
        {
            OriginalPaymentVoucherId = voucherId,
            VoucherDate = date,
            Reason = "فحص تاريخ العكس",
            Allocations = new List<PaymentAllocationReversalInput>
            {
                new() { AllocationId = allocationId, Amount = 12m }
            }
        };

        // (أ) عكس بتاريخ داخل الشهر المغلق ⇒ مرفوض (لم يُلمس الأصل ولا أُنشئ قيد عكسي)
        var entriesBefore = await EntryCountAsync();
        await AssertClosedAsync("عكس سند صرف (تاريخ مغلق)", async () =>
        {
            await using var ctx = _pg.NewContext();
            await Reversals(ctx).ReverseAsync(ReversalDto(lockedDate));
        });
        Assert.Equal(entriesBefore, await EntryCountAsync());
        await using (var ctx = _pg.NewContext())
        {
            var original = await ctx.Set<PaymentVoucher>().AsNoTracking().FirstAsync(v => v.Id == voucherId);
            Assert.Equal(VoucherStatus.Posted, original.Status);
        }

        // (ب) عكس بتاريخ في شهر مفتوح ⇒ مسموح (قيد عكسي واحد)
        await using (var ctx = _pg.NewContext())
        {
            var reversal = await Reversals(ctx).ReverseAsync(ReversalDto(new DateTime(Year, 4, 25)));
            Assert.NotNull(reversal.JournalEntryId);
        }
        Assert.Equal(entriesBefore + 1, await EntryCountAsync());

        await LedgerInvariants.AssertConsistentAsync(_pg);
    }
}
