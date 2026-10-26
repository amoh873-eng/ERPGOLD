using ERPSystem.Application.DTOs;
using ERPSystem.Application.DTOs.Purchases;
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
/// PU-04 (D11) — عقد الواجهة لسندات دفع الموردين.
///
/// لا يوجد متصفّح/Playwright في هذه البيئة، لذلك نختبر:
///  1) وجود عناصر الواجهة الفعلية في ملفات الـRazor (عقد نصّي قابل للكشف الآلي).
///  2) **نفس مسار الواجهة** من الخدمات: مسودة ← اعتماد ← ترحيل مع تخصيصات.
/// أي فحص بصري/متصفح **لم يُنفَّذ** — مُعلن صراحةً في التقرير النهائي.
/// </summary>
public class Pu04PaymentVoucherUiContractTests : IClassFixture<AccountingPgFixture>
{
    private readonly AccountingPgFixture _pg;

    public Pu04PaymentVoucherUiContractTests(AccountingPgFixture pg) => _pg = pg;

    private sealed class AllowAll : IVoucherAuthorization
    {
        public bool CanView() => true;
        public bool CanCreate() => true;
        public bool CanEdit() => true;
        public bool CanApprove() => true;
        public bool CanPost() => true;
        public bool CanCancel() => true;
        public bool CanReverse() => true;
        public string? CurrentUserName() => "pu04-ui";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ERPSystem.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadUi(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"ملف الواجهة غير موجود: {relativePath}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Dialog_exposes_allocation_panel_and_blocks_unbalanced_posting()
    {
        var dialog = ReadUi(Path.Combine("src", "Web", "Components", "Pages", "Vouchers", "PaymentVoucherFormDialog.razor"));

        Assert.Contains("تخصيص الدفعة على الفواتير المؤهلة", dialog);
        Assert.Contains("SuggestFifoAsync", dialog);
        Assert.Contains("AllocationOf(context.InvoiceId)", dialog);
        Assert.Contains("Difference != 0m", dialog);
        Assert.Contains("_eligible.Count == 0", dialog);
        Assert.Contains("PostAsync(voucher.Id, allocationInputs)", dialog);
        Assert.Contains("AllocationTotal", dialog);
    }

    [Fact]
    public void Page_passes_allocation_queries_and_injects_service()
    {
        var page = ReadUi(Path.Combine("src", "Web", "Components", "Pages", "Vouchers", "PaymentVouchers.razor"));

        Assert.Contains("@inject SupplierAllocationQueries AllocationQueries", page);
        Assert.Contains("[\"AllocationQueries\"] = AllocationQueries", page);
    }

    // ── نفس مدخلات الواجهة من الخدمات: مسودة ← اعتماد ← ترحيل بتخصيص كامل ──
    [LivePgFact]
    public async Task Ui_flow_draft_approve_post_with_allocations_succeeds()
    {
        if (!_pg.Available) return;

        Guid supplierId;
        Guid invoiceId;

        await using (var ctx = _pg.NewContext())
        {
            var supplier = new Supplier
            {
                Id = Guid.NewGuid(),
                Code = "UI-" + Guid.NewGuid().ToString("N")[..6],
                NameAr = "مورد واجهة",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            ctx.Add(supplier);
            await ctx.SaveChangesAsync();
            supplierId = supplier.Id;

            var warehouseId = await ctx.Set<Warehouse>().Where(w => !w.IsDeleted).Select(w => w.Id).FirstAsync();
            var categoryId = await ctx.Set<Category>().Where(c => !c.IsDeleted).Select(c => c.Id).FirstAsync();
            var unitId = await ctx.Set<Unit>().Where(u => !u.IsDeleted).Select(u => u.Id).FirstAsync();
            var item = new Item
            {
                Id = Guid.NewGuid(),
                Code = "UI-IT-" + Guid.NewGuid().ToString("N")[..5],
                NameAr = "صنف واجهة",
                CategoryId = categoryId,
                UnitId = unitId,
                SalePrice = 150m,
                CostPrice = 150m,
                TaxRate = 0m,
                TracksBatches = false,
                CreatedAt = DateTime.UtcNow
            };
            ctx.Add(item);
            await ctx.SaveChangesAsync();

            var invoice = await new PurchaseInvoiceService(
                    ctx, new JournalEntryService(ctx), new AccountingPgFixture.AllowAllManualPrice())
                .CreateAsync(new CreatePurchaseInvoiceDto
                {
                    SupplierId = supplierId,
                    WarehouseId = warehouseId,
                    InvoiceDate = DateTime.Today,
                    InvoiceType = (int)PurchaseInvoiceType.OnAccount,
                    PaymentMethod = CreatePurchaseInvoiceDto.PaymentOnAccount,
                    Lines = new List<CreatePurchaseInvoiceLineDto>
                    {
                        new() { ItemId = item.Id, Quantity = 1m, UnitCost = 150m }
                    }
                });
            invoiceId = invoice.Id;
        }

        await using var flowCtx = _pg.NewContext();
        var service = new PaymentVoucherService(flowCtx, new JournalEntryService(flowCtx), new AllowAll());

        // مدخلات الشاشة: المورد ← قائمة الفواتير المؤهلة ← مبلغ السند
        var eligible = await new SupplierAllocationQueries(flowCtx)
            .GetInvoicesAsync(supplierId, DateTime.Today, onlyEligible: true);
        Assert.Single(eligible);
        Assert.Equal(150m, eligible[0].Available);

        var draft = await service.CreateDraftAsync(new CreatePaymentVoucherDto
        {
            SupplierId = supplierId,
            VoucherDate = DateTime.Today,
            PaymentMethod = PaymentMethodMap.VoucherCash,
            Amount = 150m
        });
        await service.ApproveAsync(draft.Id);
        var posted = await service.PostAsync(draft.Id, new[]
        {
            new PaymentAllocationInput { PurchaseInvoiceId = invoiceId, Amount = 150m }
        });

        Assert.Equal((int)VoucherStatus.Posted, posted.Status);
        Assert.NotNull(posted.JournalEntryNumber);

        var residual = await new SupplierAllocationQueries(flowCtx).GetResidualAsync(invoiceId);
        Assert.Equal(0m, residual!.Residual);
    }

    // ── تسمية حالة «معكوس» المعروضة في الواجهة ──
    [Fact]
    public void Reversed_status_is_labelled_for_the_ui()
    {
        var method = typeof(PaymentVoucherService).GetMethod(
            "StatusName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var text = (string)method!.Invoke(null, new object[] { VoucherStatus.Reversed })!;
        Assert.Equal("معكوس", text);
    }
}
