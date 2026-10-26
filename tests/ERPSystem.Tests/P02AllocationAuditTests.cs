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
/// P0-2 — تدقيق تكاملي بعد تخصيص سند القبض: اختبارات فجوات المرحلة (Phase 4).
/// يغطي: غياب صلاحية التخصيص، تكرار الحفظ (عدم مضاعفة الصافي)، فاتورة عليها دفعة سابقة،
/// دفعة أكبر من إجمالي المتاح على الفواتير، وسند ملغى/غير مرخّص.
///
/// قاعدة اختبار معزولة (تُنشأ وتُحذف ذاتيًا عبر <see cref="AccountingPgFixture"/>)،
/// والكتابات تمرّ كلها عبر الخدمات الرسمية — لا تعديل مباشر لقيود/أرصدة.
/// </summary>
public class P02AllocationAuditTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public P02AllocationAuditTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true; public bool CanAllocate() => true;
        public string? CurrentUserName() => "p02-audit";
    }

    private sealed class DenyAllocate : IVoucherAuthorization
    {
        public bool CanView() => true; public bool CanCreate() => true; public bool CanEdit() => true;
        public bool CanApprove() => true; public bool CanPost() => true; public bool CanCancel() => true;
        public bool CanReverse() => true; public bool CanAllocate() => false;
        public string? CurrentUserName() => "p02-audit";
    }

    private static string Code(string tag) => tag + "-" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Guid> NewCustomerAsync(string tag)
    {
        await using var ctx = _pg.NewContext();
        var c = new Customer
        {
            Id = Guid.NewGuid(), Code = Code(tag), NameAr = "عميل تدقيق " + tag,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<Guid> NewItemAsync(string tag)
        => await _pg.CreateItemAsync(Code(tag), 10m, 5m, 100_000m, taxRate: 0m);

    private async Task<Guid> NewInvoiceAsync(Guid customerId, Guid itemId, decimal qty = 10m, decimal price = 10m)
    {
        await using var ctx = _pg.NewContext();
        var inv = await AccountingPgFixture.NewSalesService(ctx).CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = customerId, WarehouseId = _pg.WarehouseId, InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.OnAccount,
            PaymentMethod = (int)SalesPaymentMethod.OnAccount, TaxRate = 0m,
            Lines = new List<CreateSalesInvoiceLineDto> { new() { ItemId = itemId, Quantity = qty, UnitPrice = price } }
        });
        return inv.Id;
    }

    private async Task<(Guid Id, ReceiptVoucherDto Voucher)> NewReceiptAsync(
        Guid customerId, decimal amount, IReadOnlyList<CustomerAllocationInput>? allocations = null, bool post = true)
    {
        await using var ctx = _pg.NewContext();
        var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
        var draft = await svc.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customerId, VoucherDate = DateTime.Today, PaymentMethod = 1, Amount = amount
        });
        if (!post) return (draft.Id, draft);
        await svc.ApproveAsync(draft.Id);
        var posted = await svc.PostAsync(draft.Id, new PostReceiptVoucherRequest
        {
            Allocations = allocations?.ToList() ?? new List<CustomerAllocationInput>()
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

    private static decimal PartyTotal(AgingReportDto dto, Guid customerId)
        => dto.Rows.Where(r => r.PartyId == customerId).Sum(r => r.Total);

    private async Task<int> AllocRowCountAsync(Guid? voucherId = null)
    {
        await using var ctx = _pg.NewContext();
        var q = ctx.Set<CustomerPaymentAllocation>().AsNoTracking().Where(a => !a.IsDeleted);
        if (voucherId is not null) q = q.Where(a => a.ReceiptVoucherId == voucherId);
        return await q.CountAsync();
    }

    private async Task<decimal> InvoiceNetAllocatedAsync(Guid invoiceId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<CustomerPaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.SalesInvoiceId == invoiceId)
            .SumAsync(a => (decimal?)(a.Direction == 1 ? a.Amount : -a.Amount)) ?? 0m;
    }

    private async Task<int> JournalEntryCountAsync()
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<JournalEntry>().AsNoTracking().Where(e => !e.IsDeleted).CountAsync();
    }

    private static CustomerAllocationInput Line(Guid invoiceId, decimal amount)
        => new() { SalesInvoiceId = invoiceId, Amount = amount };

    // ── A1: غياب صلاحية التخصيص ⇒ رفض مُغلَق بلا أي كتابة ──
    [LivePgFact]
    public async Task A1_Allocation_without_permission_writes_nothing()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-A1");
        var item = await NewItemAsync("P02-A1");
        var invoice = await NewInvoiceAsync(customer, item);
        var (voucherId, _) = await NewReceiptAsync(customer, 50m);

        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new DenyAllocate());
            var ex = await Record.ExceptionAsync(() => svc.SetAllocationsAsync(voucherId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 50m) } }));
            Assert.IsType<UnauthorizedAccessException>(ex);
        }
        Assert.Equal(0, await AllocRowCountAsync(voucherId));
        Assert.Equal(0m, await InvoiceNetAllocatedAsync(invoice));
    }

    // ── A2: إعادة الحفظ مرتين لا تضاعف الصافي ولا تُنشئ قيدًا محاسبيًا ──
    [LivePgFact]
    public async Task A2_Repeated_save_does_not_double_the_net_allocation()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-A2");
        var item = await NewItemAsync("P02-A2");
        var invoice = await NewInvoiceAsync(customer, item);   // 100
        var (voucherId, _) = await NewReceiptAsync(customer, 30m, new[] { Line(invoice, 30m) });

        var jeAfterPost = await JournalEntryCountAsync();

        // حفظ ثانٍ وثالث بنفس النية: الصافي يجب أن يبقى 30 (لا 60/90) والقيد لا يتغيّر.
        for (var i = 0; i < 2; i++)
        {
            await using var ctx = _pg.NewContext();
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var s = await svc.SetAllocationsAsync(voucherId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 30m) }, Note = "إعادة حفظ" });
            Assert.Equal(30m, s.AllocatedAmount);
            Assert.Equal(0m, s.UnallocatedAmount);
        }

        Assert.Equal(30m, await InvoiceNetAllocatedAsync(invoice));   // لا مضاعفة للصافي
        Assert.Equal(jeAfterPost, await JournalEntryCountAsync());    // التخصيص لا يُنشئ أي قيد
        var dto = await AgingAsync(DateTime.Today);
        Assert.Equal(70m, Row(dto, customer)!.Total);
        Assert.Equal(0m, Row(dto, customer)!.UnallocatedReceipts);
    }

    // ── A3: فاتورة عليها دفعة سابقة (PaidAmount) ⇒ المتبقي صافٍ من الدفعة والتخصيص ──
    [LivePgFact]
    public async Task A3_Invoices_with_prior_payment_are_net_of_paid_amount()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-A3");
        var item = await NewItemAsync("P02-A3");
        var invoice = await NewInvoiceAsync(customer, item);   // 100

        await using (var ctx = _pg.NewContext())
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"SalesInvoices\" SET \"PaidAmount\" = {40m} WHERE \"Id\" = {invoice}");

        await NewReceiptAsync(customer, 30m, new[] { Line(invoice, 30m) });

        var dto = await AgingAsync(DateTime.Today);
        var row = Row(dto, customer)!;
        Assert.Equal(30m, row.Total);                                  // 100 − 40 (مدفوع) − 30 (مخصَّص)
        Assert.Equal(30m, row.Invoices.Single().Balance);
        Assert.Equal(30m, row.Invoices.Single().AllocatedAmount);
    }

    // ── A4: دفعة أكبر من إجمالي المتاح على الفواتير ⇒ رفض الكامل، والجزئي مسموح ──
    [LivePgFact]
    public async Task A4_Receipt_bigger_than_invoice_available_is_rejected_but_partial_succeeds()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-A4");
        var item = await NewItemAsync("P02-A4");
        var invoice = await NewInvoiceAsync(customer, item);   // 100
        var (voucherId, _) = await NewReceiptAsync(customer, 150m);

        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var ex = await Record.ExceptionAsync(() => svc.SetAllocationsAsync(voucherId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 150m) } }));
            Assert.NotNull(ex);
            Assert.Contains("يتجاوز المتبقي المتاح", ex!.Message);
        }
        Assert.Equal(0, await AllocRowCountAsync(voucherId));

        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var s = await svc.SetAllocationsAsync(voucherId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 100m) } });
            Assert.Equal(100m, s.AllocatedAmount);
            Assert.Equal(50m, s.UnallocatedAmount);        // الباقي يبقى غير مخصَّص ولا يُعلَّق على فاتورة
        }

        var dto = await AgingAsync(DateTime.Today);
        Assert.Equal(0m, PartyTotal(dto, customer));          // الفاتورة مسدَّدة بالكامل (لا متبقٍّ)
        Assert.Equal(50m, dto.UnallocatedReceiptsTotal);      // الإجمالي على مستوى التقرير
        // G-2 (بعد الإصلاح): يُنشأ صف للعميل حتى بلا فاتورة مفتوحة، وتظهر الدفعة غير المخصصة
        // في حقلها المستقل، بلا مساهمة في الفئات العمرية ولا في أرصدة الفواتير (لا ازدواج).
        var row = Row(dto, customer)!;
        Assert.Equal(50m, row.UnallocatedReceipts);
        Assert.Equal(0m, row.Total);            // لا مساهمة في أعمار الفواتير
        Assert.Equal(0m, row.InvoicedTotal);
        Assert.Equal(0m, row.AllocatedTotal);
        Assert.Empty(row.Invoices);
    }

    // ── A5: سند ملغى/غير مُرحَّل ⇒ لا يُخصَّص ──
    [LivePgFact]
    public async Task A5_Cancelled_voucher_cannot_be_allocated()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-A5");
        var item = await NewItemAsync("P02-A5");
        var invoice = await NewInvoiceAsync(customer, item);
        var (draftId, _) = await NewReceiptAsync(customer, 40m, post: false);

        await using (var ctx = _pg.NewContext())
            await new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll())
                .CancelAsync(draftId, "إلغاء اختباري");

        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var ex = await Record.ExceptionAsync(() => svc.SetAllocationsAsync(draftId,
                new SetReceiptAllocationsRequest { Allocations = new() { Line(invoice, 40m) } }));
            Assert.NotNull(ex);
            Assert.Contains("مُرحَّل", ex!.Message);
        }
        Assert.Equal(0, await AllocRowCountAsync(draftId));
    }

    // ── A6 (G-1): المفتاح نفسه ⇒ إعادة الطلب لا تُنشئ أي صف أو سجل تدقيق؛ ومفتاح مختلف ⇒ يُنفَّذ التعديل ──
    [LivePgFact]
    public async Task A6_Same_idempotency_key_does_not_write_again()
    {
        if (!_pg.Available) return;
        await _pg.SeedCashAsync();
        var customer = await NewCustomerAsync("P02-A6");
        var item = await NewItemAsync("P02-A6");
        var invoice = await NewInvoiceAsync(customer, item);   // 100
        var (voucherId, _) = await NewReceiptAsync(customer, 30m);

        var key = Guid.NewGuid();
        await using (var ctx = _pg.NewContext())
            await new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll())
                .SetAllocationsAsync(voucherId, new SetReceiptAllocationsRequest
                {
                    Allocations = new() { Line(invoice, 30m) }, IdempotencyKey = key
                });

        var rowsAfterFirst = await AllocRowCountAsync(voucherId);
        var auditAfterFirst = await AuditCountAsync(voucherId);
        Assert.Equal(30m, await InvoiceNetAllocatedAsync(invoice));

        // إعادة الطلب نفسه (نفس المفتاح) ⇒ لا كتابة ثانية إطلاقًا (لا صفوف عكس/إضافة ولا تدقيق).
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var s = await svc.SetAllocationsAsync(voucherId, new SetReceiptAllocationsRequest
            {
                Allocations = new() { Line(invoice, 30m) }, IdempotencyKey = key
            });
            Assert.Equal(30m, s.AllocatedAmount);
        }
        Assert.Equal(rowsAfterFirst, await AllocRowCountAsync(voucherId));
        Assert.Equal(auditAfterFirst, await AuditCountAsync(voucherId));
        Assert.Equal(30m, await InvoiceNetAllocatedAsync(invoice));

        // مفتاح مختلف ⇒ الطلب الجديد يُنفَّذ (تعديل صحيح مصرّح به): إعادة توزيع 30 على فاتورة أخرى.
        var invoice2 = await NewInvoiceAsync(customer, item, 5m, 10m);   // 50
        await using (var ctx = _pg.NewContext())
        {
            var svc = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAll());
            var s = await svc.SetAllocationsAsync(voucherId, new SetReceiptAllocationsRequest
            {
                Allocations = new() { Line(invoice2, 30m) }, IdempotencyKey = Guid.NewGuid()
            });
            Assert.Equal(30m, s.AllocatedAmount);
        }
        Assert.Equal(0m, await InvoiceNetAllocatedAsync(invoice));    // أُعيد التوزيع بنجاح
        Assert.Equal(30m, await InvoiceNetAllocatedAsync(invoice2));
    }

    private async Task<int> AuditCountAsync(Guid entityId)
    {
        await using var ctx = _pg.NewContext();
        return await ctx.Set<AuditTrail>().AsNoTracking().Where(a => a.EntityId == entityId).CountAsync();
    }
}
