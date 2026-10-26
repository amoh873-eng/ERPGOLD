using ERPSystem.Application.DTOs.BankChecks;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.RepCustody;
using ERPSystem.Application.DTOs.Reports;
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
/// P0-2 — تخصيص مقبوضات العملاء على الفواتير: الاختبارات الإلزامية + ثبات الأستاذ العام.
///
/// قواعد: قاعدة اختبار معزولة (تُنشأ وتُحذف ذاتيًا) · كل الكتابات عبر الخدمات الرسمية
/// · لا تعديل قيود/أرصدة تاريخية · لا تخصيصات تاريخية افتراضية.
/// </summary>
public class P02AllocationTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P02AllocationTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true; public bool CanAllocate() => true;
        public string? CurrentUserName() => "p02-alloc";
    }

    private static string Code(string tag) => tag + "-" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Guid> NewCustomerAsync(string tag)
    {
        await using var ctx = _pg.NewContext();
        var c = new Customer
        {
            Id = Guid.NewGuid(), Code = Code(tag), NameAr = "عميل تخصيص " + tag,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<Guid> NewItemAsync(string tag, decimal stock = 100_000m)
        => await _pg.CreateItemAsync(Code(tag), 10m, 5m, stock, taxRate: 0m);

    /// <summary>فاتورة بيع (آجلة افتراضيًا) — بلا ضريبة.</summary>
    private async Task<Guid> NewInvoiceAsync(Guid customerId, Guid itemId, decimal qty = 10m, decimal price = 10m,
        SalesInvoiceType type = SalesInvoiceType.OnAccount, DateTime? date = null)
    {
        await using var ctx = _pg.NewContext();
        var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = customerId, WarehouseId = _pg.WarehouseId,
            InvoiceDate = date ?? DateTime.Today,
            InvoiceType = (int)type,
            PaymentMethod = type == SalesInvoiceType.OnAccount ? (int)SalesPaymentMethod.OnAccount : (int)SalesPaymentMethod.Cash,
            TaxRate = 0m,
            Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = qty, UnitPrice = price } }
        });
        return inv.Id;
    }

    /// <summary>سند قبض: إنشاء ← اعتماد ← (ترحيل مع تخصيصات اختيارية).</summary>
    private async Task<(Guid Id, ReceiptVoucherDto Voucher)> NewReceiptAsync(
        Guid customerId, decimal amount, IReadOnlyList<CustomerAllocationInput>? allocations = null,
        DateTime? date = null, bool post = true, Guid? idempotencyKey = null)
    {
        await using var ctx = _pg.NewContext();
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = date ?? DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        if (!post) return (draft.Id, draft);
        await svc.ApproveAsync(draft.Id);
        var posted = await svc.PostAsync(draft.Id, new PostReceiptVoucherRequest
        {
            Allocations = allocations?.ToList() ?? new List<CustomerAllocationInput>(),
            IdempotencyKey = idempotencyKey
        });
        return (posted.Id, posted);
    }

    private async Task<AgingReportDto> AgingAsync(DateTime asOf)
    {
        await using var ctx = _pg.NewContext();
        return await AgingReportQueries.GetAgingAsync(ctx, isReceivable: true, asOf: asOf);
    }

    private static AgingPartyRow? Row(AgingReportDto dto, Guid customerId)
        => dto.Rows.FirstOrDefault(r => r.PartyId == customerId);

    private static decimal PartyResidual(AgingReportDto dto, Guid customerId)
        => dto.Rows.Where(r => r.PartyId == customerId).Sum(r => r.Total);

    private async Task<ReceiptAllocationStateDto?> StateAsync(Guid voucherId)
    {
        await using var ctx = _pg.NewContext();
        return await new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll())
            .GetAllocationStateAsync(voucherId);
    }

    private async Task<int> AllocationRowCountAsync(Guid? voucherId = null)
    {
        await using var ctx = _pg.NewContext();
        var q = ctx.Set<CustomerPaymentAllocation>().AsNoTracking().Where(a => !a.IsDeleted);
        if (voucherId is not null) q = q.Where(a => a.ReceiptVoucherId == voucherId);
        return await q.CountAsync();
    }

    private async Task<int> JournalEntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted).CountAsync();
    }

    private async Task<decimal> AccountNetAsync(string code)
    {
        await using var ctx = _pg.NewContext();
        var accountId = await ctx.Set<Account>().AsNoTracking()
            .Where(a => a.Code == code && !a.IsDeleted).Select(a => a.Id).FirstAsync();
        return await ctx.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => !l.IsDeleted && l.AccountId == accountId)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
    }

    private static CustomerAllocationInput Line(Guid invoiceId, decimal amount)
        => new() { SalesInvoiceId = invoiceId, Amount = amount };

    // ═══════════ (1) فاتورة 100 + قبض 30 مخصص ⇒ المتبقي 70 ═══════════
    [LivePgFact]
    public async Task T01_Invoice100_with_allocated_receipt30_gives_residual70()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T01");
        var item = await NewItemAsync("P02-T01");
        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);

        await NewReceiptAsync(customer, 30m, new[] { Line(invoice, 30m) });

        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, customer)!;

        Assert.Equal(70m, row.Total);
        Assert.Equal(30m, row.AllocatedTotal);
        Assert.Equal(100m, row.InvoicedTotal);
        Assert.Equal(70m, row.Invoices.Single().Balance);
        Assert.Equal(30m, row.Invoices.Single().AllocatedAmount);
        Assert.Equal(0m, row.UnallocatedReceipts);   // لا غير مخصص
    }

    // ═══════════ (2) فاتورة 100 + قبضان 30 و20 ⇒ المتبقي 50 ═══════════
    [LivePgFact]
    public async Task T02_Invoice100_with_two_receipts_30_and_20_gives_residual50()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T02");
        var item = await NewItemAsync("P02-T02");
        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);

        await NewReceiptAsync(customer, 30m, new[] { Line(invoice, 30m) });
        await NewReceiptAsync(customer, 20m, new[] { Line(invoice, 20m) });

        var dto = await AgingAsync(DateTime.Today);
        Assert.Equal(50m, PartyResidual(dto, customer));
        Assert.Equal(50m, Row(dto, customer)!.AllocatedTotal);
    }

    // ═══════════ (3) قبض 100 على فاتورتين 60 و40 ⇒ صفر لكلتيهما ═══════════
    [LivePgFact]
    public async Task T03_Receipt100_split_over_two_invoices_clears_both()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T03");
        var item = await NewItemAsync("P02-T03");
        var inv1 = await NewInvoiceAsync(customer, item, 6m, 10m);   // 60
        var inv2 = await NewInvoiceAsync(customer, item, 4m, 10m);   // 40

        await NewReceiptAsync(customer, 100m, new[] { Line(inv1, 60m), Line(inv2, 40m) });

        var dto = await AgingAsync(DateTime.Today);
        Assert.Null(Row(dto, customer));                    // لا أرصدة مفتوحة
        Assert.Equal(0m, PartyResidual(dto, customer));
    }

    // ═══════════ (4) قبض 100 بتخصيص 70 فقط ⇒ 30 غير مخصصة ═══════════
    [LivePgFact]
    public async Task T04_Partial_allocation_shows_30_unallocated()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T04");
        var item = await NewItemAsync("P02-T04");
        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);

        var (voucherId, _) = await NewReceiptAsync(customer, 100m, new[] { Line(invoice, 70m) });

        var state = await StateAsync(voucherId);
        Assert.NotNull(state);
        Assert.Equal(70m, state!.AllocatedAmount);
        Assert.Equal(30m, state.UnallocatedAmount);

        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, customer)!;
        Assert.Equal(30m, row.Total);                       // متبقٍّ الفاتورة
        Assert.Equal(30m, row.UnallocatedReceipts);         // الباقي بند مستقل
    }

    // ═══════════ (5) تجاوز المتاح ⇒ رفض بلا تغيير بيانات ═══════════
    [LivePgFact]
    public async Task T05_Exceeding_available_is_rejected_without_data_change()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T05");
        var item = await NewItemAsync("P02-T05");
        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);   // 100

        // (أ) تجاوز مبلغ السند: سند 50 ومحاولة تخصيص 60 على فاتورة متاحها 100.
        var (voucherId, _) = await NewReceiptAsync(customer, 50m);
        var rowsBefore = await AllocationRowCountAsync();
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var ex = await Record.ExceptionAsync(() => svc.SetAllocationsAsync(voucherId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 60m) } }));
            Assert.NotNull(ex);
            Assert.Contains("المبلغ المتاح على السند", ex!.Message);
        }
        Assert.Equal(rowsBefore, await AllocationRowCountAsync());

        // (ب) تجاوز متبقي الفاتورة: فاتورة 100 بمتاح 100 ⇒ نخصم 60، ثم نحاول 50 أخرى بنفس السند.
        await NewReceiptAsync(customer, 60m, new[] { Line(invoice, 60m) });
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var ex = await Record.ExceptionAsync(() => svc.SetAllocationsAsync(voucherId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 50m) } }));
            Assert.NotNull(ex);
            Assert.Contains("يتجاوز المتبقي المتاح", ex!.Message);
        }
        Assert.Equal(rowsBefore + 1, await AllocationRowCountAsync());   // صف واحد فقط من الناجحة
    }

    // ═══════════ (6) اختلاف العميل ⇒ رفض ═══════════
    [LivePgFact]
    public async Task T06_Customer_mismatch_is_rejected()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customerA = await NewCustomerAsync("P02-T06A");
        var customerB = await NewCustomerAsync("P02-T06B");
        var item = await NewItemAsync("P02-T06");
        var invoiceB = await NewInvoiceAsync(customerB, item, 10m, 10m);

        var (voucherA, _) = await NewReceiptAsync(customerA, 50m);

        await using var ctx = _pg.NewContext();
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var ex = await Record.ExceptionAsync(() => svc.SetAllocationsAsync(voucherA,
            new SetReceiptAllocationsRequest { Allocations = new() { Line(invoiceB, 50m) } }));

        Assert.NotNull(ex);
        Assert.Contains("لا تخص هذا العميل", ex!.Message);
        Assert.Equal(0, await AllocationRowCountAsync(voucherA));
    }

    // ═══════════ (7) فاتورة/سند غير مرحّل ⇒ لا يدخل الحسابات المرحّلة ═══════════
    [LivePgFact]
    public async Task T07_Non_posted_documents_are_excluded()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T07");
        var item = await NewItemAsync("P02-T07");
        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);

        // سند قبض غير مُرحَّل (مسودة) ⇒ لا يدخل «بلا تخصيص موثق» ولا يمكن التخصيص.
        var (draftId, _) = await NewReceiptAsync(customer, 40m, post: false);
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var ex = await Record.ExceptionAsync(() => svc.SetAllocationsAsync(draftId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 40m) } }));
            Assert.NotNull(ex);
            Assert.Contains("مُرحَّل", ex!.Message);
        }

        var dto = await AgingAsync(DateTime.Today);
        Assert.Equal(100m, PartyResidual(dto, customer));            // الفاتورة كاملة
        Assert.Equal(0m, Row(dto, customer)!.UnallocatedReceipts);   // المسودة مستبعدة

        // فاتورة غير مُرحَّلة (مسودة) ⇒ غير مؤهَّلة للتخصيص أصلاً.
        await using (var ctx = _pg.NewContext())
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"SalesInvoices\" SET \"Status\" = {(int)DocumentStatus.Draft} WHERE \"Id\" = {invoice}");
        await using (var ctx2 = _pg.NewContext())
        {
            var eligible = await new ReceiptVoucherService(ctx2, new JournalEntryService(ctx2), new AllowAll())
                .GetEligibleInvoicesAsync(customer);
            Assert.DoesNotContain(eligible, i => i.InvoiceId == invoice && i.IsEligible);
        }
        await using (var ctx3 = _pg.NewContext())
            await ctx3.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"SalesInvoices\" SET \"Status\" = {(int)DocumentStatus.Posted} WHERE \"Id\" = {invoice}");
    }

    // ═══════════ (8) مرتجع مرتبط بالفاتورة ⇒ يُحتسب وفق نوع تسويته مرة واحدة ═══════════
    [LivePgFact]
    public async Task T08_Linked_return_is_deducted_once_per_settlement_effect()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();

        // (أ) فاتورة آجلة: المردود يخفض الذمم (يُقيَّد دائن 1200) ⇒ يُخصم من المتبقي مرة واحدة.
        var customer = await NewCustomerAsync("P02-T08A");
        var item = await NewItemAsync("P02-T08A");
        var creditInvoice = await NewInvoiceAsync(customer, item, 10m, 10m);   // 100
        await using (var ctx = _pg.NewContext())
        {
            await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = creditInvoice, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = item, Quantity = 2m, UnitPrice = 10m } }
            });   // مردود 20
        }
        await NewReceiptAsync(customer, 30m, new[] { Line(creditInvoice, 30m) });

        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, customer)!;
        Assert.Equal(50m, row.Total);                        // 100 − 20 − 30 (المردود مرة واحدة)
        Assert.Equal(20m, row.EffectiveReturnsTotal);
        Assert.Equal(30m, row.AllocatedTotal);
        Assert.Equal(20m, row.Invoices.Single().EffectiveReturns);

        // (ب) فاتورة نقدية: المردود يُقيَّد ضد 1100 (لا يخفض الذمم) ⇒ لا يُخصم من أي متبقٍّ للذمم.
        var cashCustomer = await NewCustomerAsync("P02-T08B");
        var cashItem = await NewItemAsync("P02-T08B");
        var cashInvoice = await NewInvoiceAsync(cashCustomer, cashItem, 10m, 10m, SalesInvoiceType.Cash);
        await using (var ctx = _pg.NewContext())
        {
            await new SalesReturnService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateSalesReturnDto
            {
                SalesInvoiceId = cashInvoice, WarehouseId = _pg.WarehouseId, ReturnDate = DateTime.Today,
                Lines = new List<CreateSalesReturnLineDto> { new() { ItemId = cashItem, Quantity = 2m, UnitPrice = 10m } }
            });
        }
        var cashDto = await AgingAsync(DateTime.Today);
        Assert.Null(Row(cashDto, cashCustomer));             // الفاتورة النقدية مستبعدة أصلًا
        Assert.Equal(0m, PartyResidual(cashDto, cashCustomer));
    }

    // ═══════════ (9) شيك قيد التحصيل ثم تحصيله/ارتداdه ⇒ لا خصم مزدوج ولا قيد مكرر ═══════════
    [LivePgFact]
    public async Task T09_Check_in_collection_is_not_double_counted()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T09");
        var item = await NewItemAsync("P02-T09");
        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);   // 100

        Guid checkId;
        await using (var ctx = _pg.NewContext())
        {
            var created = await new BankCheckService(ctx, new JournalEntryService(ctx)).RegisterAsync(new CreateBankCheckDto
            {
                CheckNumber = Code("CHK"), BankName = "بنك اختبار P0-2",
                Direction = BankCheckDirection.ReceivedFromCustomer,
                CustomerId = customer, Amount = 30m,
                IssueDate = DateTime.Today, DueDate = DateTime.Today.AddDays(30),
                ReceivedOrIssuedDate = DateTime.Today
            });
            checkId = created.Id;
        }

        var afterRegister = await AgingAsync(DateTime.Today);
        var row1 = Row(afterRegister, customer)!;
        var entries1 = await JournalEntryCountAsync();

        await using (var ctx = _pg.NewContext())
            await new BankCheckService(ctx, new JournalEntryService(ctx)).ClearAsync(checkId, DateTime.Today);

        var afterClear = await AgingAsync(DateTime.Today);
        var row2 = Row(afterClear, customer)!;

        Assert.Equal(100m, row1.Total);                       // الشيك لا يُخصم من الفاتورة
        Assert.Equal(30m, row1.UnallocatedChecks);            // بند مستقل مرة واحدة
        Assert.Equal(30m, row2.UnallocatedChecks);            // لا تكرار بعد التحصيل
        Assert.Equal(100m, row2.Total);
        Assert.Equal(entries1 + 1, await JournalEntryCountAsync());   // قيد التحصيل فقط (لا تكرار)
    }

    // ═══════════ (10) حدود تاريخ التقرير: لا تسرّب من اليوم التالي ═══════════
    [LivePgFact]
    public async Task T10_Cutoff_boundaries_include_today_and_exclude_next_day()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T10");
        var item = await NewItemAsync("P02-T10");
        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);

        await NewReceiptAsync(customer, 30m, new[] { Line(invoice, 30m) });
        await NewReceiptAsync(customer, 25m, date: DateTime.Today.AddDays(1));

        var today = await AgingAsync(DateTime.Today);
        var tomorrow = await AgingAsync(DateTime.Today.AddDays(1));

        Assert.Equal(70m, PartyResidual(today, customer));            // 100 − 30
        Assert.Equal(0m, Row(today, customer)!.UnallocatedReceipts);  // سند الغد غير مُدرج
        Assert.Equal(70m, PartyResidual(tomorrow, customer));
        Assert.Equal(25m, Row(tomorrow, customer)!.UnallocatedReceipts);
    }

    // ═══════════ (11) عكس التخصيص وإعادة التخصيص + سجل التدقيق ═══════════
    [LivePgFact]
    public async Task T11_Reversal_and_reallocation_keep_history_and_audit()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T11");
        var item = await NewItemAsync("P02-T11");
        var inv1 = await NewInvoiceAsync(customer, item, 10m, 10m);   // 100
        var inv2 = await NewInvoiceAsync(customer, item, 5m, 10m);    // 50

        var (voucherId, _) = await NewReceiptAsync(customer, 40m, new[] { Line(inv1, 40m) });
        var rowsAfterCreate = await AllocationRowCountAsync(voucherId);

        ReceiptAllocationStateDto state;
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            state = await svc.GetAllocationStateAsync(voucherId) ?? throw new InvalidOperationException();
            var original = state.Allocations.Single(a => a.Direction == 1);
            state = await svc.ReverseAllocationAsync(voucherId,
                new ReverseCustomerAllocationRequest { AllocationId = original.Id, Note = "اختبار عكس" });
        }
        Assert.Equal(0m, state.AllocatedAmount);
        Assert.Equal(40m, state.UnallocatedAmount);

        var afterReverse = await AgingAsync(DateTime.Today);
        Assert.Equal(150m, PartyResidual(afterReverse, customer));        // inv1 (100) + inv2 (50) بلا تخصيص
        Assert.Equal(40m, Row(afterReverse, customer)!.UnallocatedReceipts);
        Assert.Equal(rowsAfterCreate + 1, await AllocationRowCountAsync(voucherId));  // صف عكس مضاف (لا حذف)

        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            state = await svc.SetAllocationsAsync(voucherId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(inv2, 40m) }, Note = "إعادة توزيع" });
        }
        Assert.Equal(40m, state.AllocatedAmount);

        var afterRealloc = await AgingAsync(DateTime.Today);
        var rowRealloc = Row(afterRealloc, customer)!;
        Assert.Equal(110m, rowRealloc.Total);                 // 100 (الأولى كاملة) + 10 (الثانية بعد 40)
        Assert.Equal(0m, rowRealloc.UnallocatedReceipts);

        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var actions = (await svc.GetAuditAsync(voucherId)).Select(a => a.Action).Distinct().ToList();
            Assert.Contains("Allocated", actions);
            Assert.Contains("AllocationReversed", actions);
            Assert.Contains("AllocationsReplaced", actions);
        }
    }

    // ═══════════ (12) تخصيصان متزامنان ⇒ لا تجاوز للمتاح ═══════════
    [LivePgFact]
    public async Task T12_Concurrent_allocations_cannot_exceed_available()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T12");
        var item = await NewItemAsync("P02-T12");
        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);   // 100

        var (v1, _) = await NewReceiptAsync(customer, 60m);
        var (v2, _) = await NewReceiptAsync(customer, 60m);

        async Task<string> TryAllocateAsync(Guid voucherId)
        {
            try
            {
                await using var ctx = _pg.NewContext();
                var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
                var s = await svc.SetAllocationsAsync(voucherId,
                    new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 60m) } });
                return "ok:" + s.AllocatedAmount;
            }
            catch (Exception ex)
            {
                return "fail:" + ex.Message;
            }
        }

        var results = await Task.WhenAll(TryAllocateAsync(v1), TryAllocateAsync(v2));

        await using var ctx = _pg.NewContext();
        var netAllocated = await ctx.Set<CustomerPaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.SalesInvoiceId == invoice)
            .SumAsync(a => (decimal?)(a.Direction == 1 ? a.Amount : -a.Amount)) ?? 0m;

        var after = await AgingAsync(DateTime.Today);

        Assert.True(netAllocated <= 100m + 0.005m, $"إجمالي التخصيص ({netAllocated}) تجاوز متاح الفاتورة");
        Assert.Equal(100m - netAllocated, PartyResidual(after, customer));
        Assert.Contains(results, r => r.StartsWith("ok:"));                 // طلب واحد على الأقل نجح
        Assert.True(results.Count(r => r.StartsWith("ok:")) <= 2);
    }

    // ═══════════ (13) مقارنة التقرير بالأستاذ + تفسير الفروق القابلة للتتبع ═══════════
    [LivePgFact]
    public async Task T13_Aging_versus_ledger_differences_are_traceable()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-T13");
        var item = await NewItemAsync("P02-T13");

        var arBefore = await AccountNetAsync("1200");

        var invoice = await NewInvoiceAsync(customer, item, 10m, 10m);      // +100 على 1200
        var (_, _) = await NewReceiptAsync(customer, 30m, new[] { Line(invoice, 30m) });   // −30 وتخصيص 30
        await NewReceiptAsync(customer, 20m);                              // −20 بلا تخصيص (بند مستقل)

        // (أ) فرق قابل للنسب: عهدة مندوب (مدين 1110 / دائن 1200) بمبلغ 40.
        await using (var ctx = _pg.NewContext())
        {
            await new RepCustodyService(ctx, new JournalEntryService(ctx)).CreateAsync(new CreateRepCustodyDto
            {
                Effect = RepCustodyEffect.Collect, RepUserId = "REP-P02-T13",
                CustomerId = customer, Amount = 40m, Timestamp = DateTime.Today
            }, "p02-alloc");
        }

        // (ب) فرق غير قابل للنسب: قيد يدوي على 1200 بمبلغ 25 (بلا مرجع عميل).
        await using (var ctx = _pg.NewContext())
        {
            var arAccountId = await ctx.Set<Account>().AsNoTracking()
                .Where(a => a.Code == "1200" && !a.IsDeleted).Select(a => a.Id).FirstAsync();
            var counterId = await ctx.Set<Account>().AsNoTracking()
                .Where(a => a.Code == "4100" && !a.IsDeleted).Select(a => a.Id).FirstAsync();
            await new JournalEntryService(ctx).PrepareEntryAsync(
                JournalEntryType.Manual, DateTime.Today, "P02-T13-MANUAL", "قيد يدوي اختباري على الذمم",
                new List<JournalEntryLegInput>
                {
                    new() { AccountId = arAccountId, DebitAmount = 25m },
                    new() { AccountId = counterId, CreditAmount = 25m }
                });
            await ctx.SaveChangesAsync();
        }

        var arAfter = await AccountNetAsync("1200");
        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, customer)!;

        var attributed = row.Total - row.UnallocatedReceipts;     // (1) − (2)
        var ledgerDelta = arAfter - arBefore;                     // أثر السيناريو على 1200 = +100 −30 −20 −40 +25 = 35
        var unexplained = ledgerDelta - attributed;

        Assert.Equal(70m, row.Total);                             // 100 − 30 (تخصيص)
        Assert.Equal(20m, row.UnallocatedReceipts);
        Assert.Equal(50m, attributed);
        Assert.Equal(35m, ledgerDelta);
        // الفرق المتبقي (15-) = عهدة المندوبين (−40، قابلة للنسب) + القيد اليدوي (+25، بلا مرجع عميل)
        // (المقبوضات بلا تخصيص (20) ليست جزءًا من الفرق: (1) هو المتبقي المحسوب و(2) يُطرح منه لحساب المنسوب).
        Assert.Equal(-40m + 25m, unexplained);
    }
}
