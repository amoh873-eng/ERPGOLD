using ERPSystem.Application.DTOs.Accounts;
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
/// الدفعة B — سلامة الأرصدة واتساق الشجرة الحسابية (إصلاح/تحصين 2026-10-23).
///
/// المبدأ المُختبَر: الرصيد **المخزَّن** في عمود `Accounts.CurrentBalance` يجب أن يساوي
/// الرصيد **المستنتج** من سطور القيود غير المحذوفة (بإشارة طبيعة الحساب)، ولا يُصلَح أي فرق
/// بالكتابة فوق أحد الطرفين — الفحص يقيس ويفشل إن وُجد اختلاف.
/// </summary>
public class AccountingBalanceConsistencyTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public AccountingBalanceConsistencyTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "balance-consistency";
    }

    private static JournalEntryService Journal(AppDbContext ctx) => new(ctx);
    private static PurchaseInvoiceService Purchases(AppDbContext ctx)
        => new(ctx, Journal(ctx), new AccountingPgFixture.AllowAllManualPrice());
    private static PurchaseReturnService Returns(AppDbContext ctx) => new(ctx, Journal(ctx));
    private static ReceiptVoucherService Receipts(AppDbContext ctx) => new(ctx, Journal(ctx), new AllowAll());
    private static PaymentVoucherService Payments(AppDbContext ctx) => new(ctx, Journal(ctx), new AllowAll());
    private static PaymentVoucherReversalService Reversals(AppDbContext ctx) => new(ctx, Journal(ctx), new AllowAll());

    // ───────────────────────── قياس الاتساق ─────────────────────────

    private sealed record BalanceRow(string Code, decimal Stored, decimal Derived);

    /// <summary>يقارن رصيد كل حساب مخزَّنًا مع المستنتج من سطور القيود (بإشارة طبيعة الحساب).</summary>
    private async Task<List<BalanceRow>> StoredVsDerivedAsync()
    {
        await using var ctx = _pg.NewContext();
        var accounts = await ctx.Set<Account>().AsNoTracking()
            .Where(a => !a.IsDeleted)
            .Select(a => new { a.Id, a.Code, a.NormalBalance, a.CurrentBalance })
            .ToListAsync();

        var nets = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted)
            .Join(ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted),
                  l => l.JournalEntryId, e => e.Id, (l, e) => new { l.AccountId, l.DebitAmount, l.CreditAmount })
            .GroupBy(x => x.AccountId)
            .Select(g => new { AccountId = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Net);

        return accounts.Select(a => new BalanceRow(
            a.Code,
            a.CurrentBalance,
            a.NormalBalance == NormalBalance.Debit ? nets.GetValueOrDefault(a.Id, 0m) : -nets.GetValueOrDefault(a.Id, 0m)))
            .ToList();
    }

    private async Task<(decimal Debit, decimal Credit, int Lines)> LedgerTotalsAsync()
    {
        await using var ctx = _pg.NewContext();
        var lines = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted).Select(l => new { l.DebitAmount, l.CreditAmount }).ToListAsync();
        return (lines.Sum(l => l.DebitAmount), lines.Sum(l => l.CreditAmount), lines.Count);
    }

    private static void AssertAllAccountsConsistent(List<BalanceRow> rows)
    {
        var bad = rows.Where(r => Math.Abs(r.Stored - r.Derived) > 0.005m)
            .Select(r => $"{r.Code}: stored={r.Stored:F2} derived={r.Derived:F2}").ToList();
        Assert.True(bad.Count == 0, "أرصدة مخزَّنة ≠ مستنتجة: " + string.Join(" ; ", bad));
    }

    // ───────────────────────── بُناة البيانات ─────────────────────────

    private async Task<Guid> NewCustomerAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "عميل اتساق " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<Guid> NewSupplierAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Code = code + "-" + Guid.NewGuid().ToString("N")[..5],
            NameAr = "مورد اتساق " + code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    // ═══════════ 1) حراس الشجرة: منع الدورات والعمق والأب المعطَّل ═══════════
    [LivePgFact]
    public async Task Hierarchy_guards_reject_self_parent_cycle_depth_missing_and_disabled_parent()
    {
        if (!_pg.Available) return;
        await using var ctx = _pg.NewContext();
        var svc = new AccountService(ctx);
        var suffix = Guid.NewGuid().ToString("N")[..5];

        async Task<Guid> CreateAsync(string code, Guid? parent)
        {
            var dto = await svc.CreateAsync(new CreateAccountDto
            {
                Code = code,
                NameAr = "حساب اختبار " + code,
                AccountType = (int)ERPSystem.Domain.Enums.AccountType.Asset,
                ParentAccountId = parent
            });
            return dto.Id;
        }

        var root = await CreateAsync("T1-" + suffix, null);
        var child = await CreateAsync("T1.1-" + suffix, root);
        var grand = await CreateAsync("T1.1.1-" + suffix, child);
        var great = await CreateAsync("T1.1.1.1-" + suffix, grand);

        // (أ) المستوى الخامس مرفوض
        var depthEx = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync("T1.1.1.1.1-" + suffix, great));
        Assert.Contains("آخر مستوى", depthEx.Message);

        // (ب) الأب غير الموجود مرفوض
        var missingEx = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync("T1.X-" + suffix, Guid.NewGuid()));
        Assert.Contains("غير موجود", missingEx.Message);

        // (ج) الحساب أبٌ لنفسه مرفوض
        var selfEx = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateAsync(new UpdateAccountDto
        {
            Id = root, Code = "T1-" + suffix, NameAr = "حساب اختبار", AccountType = (int)ERPSystem.Domain.Enums.AccountType.Asset, ParentAccountId = root
        }));
        Assert.Contains("أباً لنفسه", selfEx.Message);

        // (د) العلاقة الدائرية مرفوضة (جد أعلى تحت حفيده) — الأب على مستوى يمكنه الاستقبال
        //     حتى يصل الفحص إلى قاعدة الدورة لا قاعدة العمق.
        var cycleEx = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateAsync(new UpdateAccountDto
        {
            Id = root, Code = "T1-" + suffix, NameAr = "حساب اختبار", AccountType = (int)ERPSystem.Domain.Enums.AccountType.Asset, ParentAccountId = grand
        }));
        Assert.Contains("دائرية", cycleEx.Message);

        // (هـ) الأب المعطَّل مرفوض
        await svc.ToggleActiveAsync(child);
        var disabledEx = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync("T1.2-" + suffix, child));
        Assert.Contains("معطَّل", disabledEx.Message);
    }

    // ═══════════ 2) لا يُرحَّل على حساب تجميعي ولا يحمل رصيدًا ═══════════
    [LivePgFact]
    public async Task Aggregate_accounts_are_not_posted_to_and_hold_zero_balance()
    {
        if (!_pg.Available) return;
        await using var ctx = _pg.NewContext();
        var parentIds = await ctx.Set<Account>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.ParentAccountId != null)
            .Select(a => a.ParentAccountId!.Value).Distinct().ToListAsync();

        Assert.NotEmpty(parentIds);

        var aggregates = await ctx.Set<Account>().AsNoTracking()
            .Where(a => !a.IsDeleted && parentIds.Contains(a.Id))
            .Select(a => new { a.Code, a.CurrentBalance }).ToListAsync();

        // الحساب التجميعي لا يُرحَّل عليه ⇒ رصيده المخزَّن صفر (العرض يجمع الأبناء في طبقة التقارير)
        var nonZero = aggregates.Where(a => Math.Abs(a.CurrentBalance) > 0.005m)
            .Select(a => $"{a.Code}={a.CurrentBalance:F2}").ToList();
        Assert.True(nonZero.Count == 0, "حسابات تجميعية بأرصدة مخزَّنة: " + string.Join(" ; ", nonZero));

        // ولا توجد سطور قيود على أي حساب تجميعي
        var postedToAggregate = await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && parentIds.Contains(l.AccountId)).CountAsync();
        Assert.Equal(0, postedToAggregate);
    }

    // ═══════════ 3) المسودات/المعتمدات غير المرحّلة لا تؤثر في أي رصيد ═══════════
    [LivePgFact]
    public async Task Unposted_documents_do_not_change_any_balance()
    {
        if (!_pg.Available) return;
        var supplier = await NewSupplierAsync("DRAFT");
        await _pg.SeedCashAsync();

        var before = await StoredVsDerivedAsync();
        var totalsBefore = await LedgerTotalsAsync();

        await using (var ctx = _pg.NewContext())
        {
            var svc = Payments(ctx);
            var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = supplier, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 25m
            });
            await svc.ApproveAsync(draft.Id);   // معتمد لكن غير مُرحَّل
        }

        var after = await StoredVsDerivedAsync();
        var totalsAfter = await LedgerTotalsAsync();

        Assert.Equal(totalsBefore, totalsAfter);
        Assert.Equal(before, after);
        AssertAllAccountsConsistent(after);
    }

    // ═══════════ 4) سيناريو مختلط: المخزَّن = المستنتج لكل الحسابات ═══════════
    /// <summary>
    /// يمرّ على: شراء آجل · بيع نقدي · بيع آجل · سند قبض · مردود شراء · سند دفع · عكس سند الدفع،
    /// ثم يقارن **كل حساب** (مخزَّن مقابل مستنتج من سطور القيود) ويتحقق من توازن الأستاذ والمخزون.
    /// لا يُصلح أي فرق — يقيس ويفشل إن وُجد.
    /// </summary>
    [LivePgFact]
    public async Task Stored_account_balances_equal_derived_after_mixed_scenario()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var supplier = await NewSupplierAsync("MIX-SUP");
        var customer = await NewCustomerAsync("MIX-CUS");
        var itemId = await _pg.CreateItemAsync("MIX-" + Guid.NewGuid().ToString("N")[..6], 15m, 20m, 100m, taxRate: 16m);

        // (1) شراء آجل 10 × 20 = 200
        Guid purchaseInvoiceId;
        await using (var ctx = _pg.NewContext())
        {
            var invoice = await Purchases(ctx).CreateAsync(new CreatePurchaseInvoiceDto
            {
                SupplierId = supplier,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                PaymentMethod = CreatePurchaseInvoiceDto.PaymentOnAccount,
                Lines = new List<CreatePurchaseInvoiceLineDto>
                {
                    new() { ItemId = itemId, Quantity = 10m, UnitCost = 20m }
                }
            });
            purchaseInvoiceId = invoice.Id;
        }

        // (2) بيع نقدي 5 × 15 + 16% = 87
        await using (var ctx = _pg.NewContext())
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customer,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                TaxRate = 16m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 5m, UnitPrice = 15m } }
            });

        // (3) بيع آجل 5 × 15 + 16% = 87
        await using (var ctx = _pg.NewContext())
            await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customer,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.OnAccount,
                PaymentMethod = (int)SalesPaymentMethod.OnAccount,
                TaxRate = 16m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 5m, UnitPrice = 15m } }
            });

        // (4) سند قبض 50
        await using (var ctx = _pg.NewContext())
        {
            var svc = Receipts(ctx);
            var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
            {
                CustomerId = customer, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 50m
            });
            await svc.ApproveAsync(draft.Id);
            await svc.PostAsync(draft.Id);
        }

        // (5) مردود شراء 1 × 20 على الحساب
        await using (var ctx = _pg.NewContext())
            await Returns(ctx).CreateAsync(new CreatePurchaseReturnDto
            {
                PurchaseInvoiceId = purchaseInvoiceId,
                WarehouseId = _pg.WarehouseId,
                ReturnDate = DateTime.Today,
                SettlementType = (int)PurchaseReturnSettlementType.OnAccountCredit,
                Lines = new List<CreatePurchaseReturnLineDto> { new() { ItemId = itemId, Quantity = 1m, UnitCost = 20m } }
            });

        // (6) سند دفع 30 مخصَّص كاملًا على فاتورة الشراء
        Guid paymentVoucherId;
        await using (var ctx = _pg.NewContext())
        {
            var svc = Payments(ctx);
            var draft = await svc.CreateDraftAsync(new CreatePaymentVoucherDto
            {
                SupplierId = supplier, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = 30m
            });
            paymentVoucherId = draft.Id;
            await svc.ApproveAsync(paymentVoucherId);
            await svc.PostAsync(paymentVoucherId, new[]
            {
                new PaymentAllocationInput { PurchaseInvoiceId = purchaseInvoiceId, Amount = 30m }
            });
        }

        // (7) عكس كامل لسند الدفع
        Guid allocationId;
        await using (var ctx = _pg.NewContext())
            allocationId = await ctx.Set<PaymentAllocation>().AsNoTracking()
                .Where(a => a.PaymentVoucherId == paymentVoucherId && a.Direction == 1)
                .Select(a => a.Id).FirstAsync();

        await using (var ctx = _pg.NewContext())
            await Reversals(ctx).ReverseAsync(new CreatePaymentVoucherReversalDto
            {
                OriginalPaymentVoucherId = paymentVoucherId,
                VoucherDate = DateTime.Today,
                Allocations = new List<PaymentAllocationReversalInput>
                {
                    new() { AllocationId = allocationId, Amount = 30m }
                }
            });

        // ── الفحوص ──
        var rows = await StoredVsDerivedAsync();
        AssertAllAccountsConsistent(rows);

        var totals = await LedgerTotalsAsync();
        Assert.True(Math.Abs(totals.Debit - totals.Credit) <= 0.005m,
            $"الأستاذ غير متوازن: debit={totals.Debit:F2} credit={totals.Credit:F2}");

        await using (var ctx = _pg.NewContext())
        {
            var stock = await ctx.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => i.CurrentStock).FirstAsync();
            var movements = await ctx.Set<StockMovement>().AsNoTracking()
                .Where(m => m.ItemId == itemId && !m.IsDeleted).SumAsync(m => (decimal?)m.Quantity) ?? 0m;
            Assert.Equal(movements, stock);                       // الكمية المخزَّنة = مجموع الحركات

            // متبقي فاتورة الشراء **شاملًا ضريبة المدخلات** (الإجمالي المستحق):
            // 232 (200 + 32 ضريبة) − 23.20 (مردود 20 + 3.20) − 30 (تخصيص) + 30 (عكس) = 208.80
            var residual = await new SupplierAllocationQueries(ctx).GetResidualAsync(purchaseInvoiceId);
            Assert.NotNull(residual);
            Assert.Equal(208.80m, residual!.Residual);

            // مطابقة السجل الفرعي (رصيد المورد) مع المتبقي ومع الأستاذ
            var supplierBalance = await ctx.Set<Supplier>().AsNoTracking()
                .Where(s => s.Id == supplier).Select(s => s.CurrentBalance).FirstAsync();
            Assert.Equal(208.80m, supplierBalance);

            var payableAccountId = await ctx.Set<Account>().AsNoTracking()
                .Where(a => a.Code == "2200" && !a.IsDeleted).Select(a => a.Id).FirstOrDefaultAsync();
            Assert.NotEqual(Guid.Empty, payableAccountId);
            var payableDerived = await ctx.Set<JournalEntryLine>().AsNoTracking()
                .Where(l => !l.IsDeleted && l.AccountId == payableAccountId)
                .SumAsync(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m;

            // الرصيد المستنتج من الأستاذ للحساب الدائن لا يقل عن رصيد هذا المورد (قد يحوي موردين آخرين)
            Assert.True(payableDerived + 0.005m >= supplierBalance,
                $"رصيد المورد {supplierBalance:F2} أكبر من الرصيد المستنتج للحساب الدائن {payableDerived:F2}");
        }
    }

    // ═══════════ 5) التزامن: الأرصدة المخزَّنة تبقى = المستنتجة ═══════════
    [LivePgFact]
    public async Task Concurrent_sales_keep_stored_balances_equal_to_derived()
    {
        if (!_pg.Available) return;
        var itemId = await _pg.CreateItemAsync("CONC-B-" + Guid.NewGuid().ToString("N")[..6], 15m, 10m, 500m, taxRate: 16m);
        var customer = await NewCustomerAsync("CONC-B");

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 3).Select(async _ =>
        {
            await start.Task;
            await using var ctx = _pg.NewContext();
            return await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
            {
                CustomerId = customer,
                WarehouseId = _pg.WarehouseId,
                InvoiceDate = DateTime.Today,
                InvoiceType = (int)SalesInvoiceType.Cash,
                PaymentMethod = (int)SalesPaymentMethod.Cash,
                TaxRate = 16m,
                Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = 2m, UnitPrice = 15m } }
            });
        }).ToList();

        start.SetResult();
        var created = await Task.WhenAll(tasks);
        Assert.Equal(3, created.Select(c => c.InvoiceNumber).Distinct().Count());

        var rows = await StoredVsDerivedAsync();
        AssertAllAccountsConsistent(rows);

        var totals = await LedgerTotalsAsync();
        Assert.True(Math.Abs(totals.Debit - totals.Credit) <= 0.005m, "الأستاذ غير متوازن بعد التزامن");

        await using var check = _pg.NewContext();
        var stock = await check.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => i.CurrentStock).FirstAsync();
        var movements = await check.Set<StockMovement>().AsNoTracking()
            .Where(m => m.ItemId == itemId && !m.IsDeleted).SumAsync(m => (decimal?)m.Quantity) ?? 0m;
        Assert.Equal(movements, stock);
        Assert.Equal(500m - 6m, stock);   // 3 فواتير × 2
    }
}

