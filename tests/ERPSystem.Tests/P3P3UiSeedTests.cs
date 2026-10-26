using ERPSystem.Application.DTOs.Movements;
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
/// أداة تهيئة **اختيارية** لقاعدة واجهة معزولة (D‑E). لا تعمل في التشغيل العادي:
/// تتطلب متغير البيئة <c>P3P3_UI_SEED_CONNECTION</c>؛ بغيابه تعود فورًا (اختبار ناجح بلا أثر).
/// لا تلمس أي قاعدة أخرى، ولا تُنشئ مستخدمًا (المستخدم يُنشأ عبر /setup في القاعدة المعزولة).
/// </summary>
public class P3P3UiSeedTests
{
    private sealed class AllowAllAuth : IVoucherAuthorization
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
        public string? CurrentUserName() => "p3p3-ui-seed";
    }

    private static readonly string LogPath = @"D:\erpfix-pu01\_p3p3_ui_seed.txt";

    /// <summary>
    /// مستخدم اختباري بدور واحد فقط (كلمات المرور اختبارية ولا تُستخدم في أي بيئة حقيقية):
    ///  • Sales: لا يملك دور الويب المطلوب للسندات/التقارير ⇒ يجب أن يُعاد توجيهه لتسجيل الدخول.
    ///  • Accounting: يملك دور السندات (fallback) لكن **بلا** Claim صريح `Vouchers.Refund`
    ///    ⇒ يجب ألا يرى مبدّل «عميل (رد مبلغ)» إطلاقًا (إثبات لا-fallback في الواجهة).
    /// </summary>
    private static async Task EnsureLimitedUserAsync(AppDbContext ctx)
    {
        await EnsureRoleUserAsync(ctx, "p3p3.sales@local.test", "SALES", "P3p3!Sales2026");
        await EnsureRoleUserAsync(ctx, "p3p3.acc@local.test", "ACCOUNTING", "P3p3!Acc2026");
    }

    private static async Task EnsureRoleUserAsync(AppDbContext ctx, string email, string normalizedRole, string password)
    {
        if (await ctx.Set<Microsoft.AspNetCore.Identity.IdentityUser>().AnyAsync(u => u.UserName == email))
            return;

        var role = await ctx.Set<Microsoft.AspNetCore.Identity.IdentityRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRole);
        if (role is null) return;

        var user = new Microsoft.AspNetCore.Identity.IdentityUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<Microsoft.AspNetCore.Identity.IdentityUser>()
            .HashPassword(user, password);
        ctx.Add(user);
        ctx.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string>
        {
            UserId = user.Id,
            RoleId = role.Id
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Seed_p3p3_ui_database_when_requested()
    {
        var cs = Environment.GetEnvironmentVariable("P3P3_UI_SEED_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs)) return;   // no-op في التشغيل العادي

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
        await using var ctx = new AppDbContext(options);
        await ctx.Database.MigrateAsync();

        await EnsureLimitedUserAsync(ctx);

        var existing = await ctx.Set<Customer>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == "UI-P3-CUST");
        if (existing is not null)
        {
            await File.WriteAllTextAsync(LogPath,
                $"already_seeded customer={existing.Id} code={existing.Code} balance={existing.CurrentBalance}\n");
            return;
        }

        var category = new Category { Id = Guid.NewGuid(), Code = "UI-P3-CAT", NameAr = "فئة واجهة", CreatedAt = DateTime.UtcNow };
        var unit = new Unit { Id = Guid.NewGuid(), Code = "UI-P3-UN", NameAr = "وحدة واجهة", CreatedAt = DateTime.UtcNow };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), Code = "UI-P3-WH", NameAr = "مخزن واجهة", IsDefault = true, CreatedAt = DateTime.UtcNow };
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Code = "UI-P3-CUST", NameAr = "عميل واجهة الرد",
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.AddRange(category, unit, warehouse, customer);
        await ctx.SaveChangesAsync();

        var item = new Item
        {
            Id = Guid.NewGuid(), Code = "UI-P3-ITEM", NameAr = "صنف واجهة",
            CategoryId = category.Id, UnitId = unit.Id,
            SalePrice = 15m, CostPrice = 10m, TaxRate = 16m,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Add(item);
        await ctx.SaveChangesAsync();

        await new StockMovementService(ctx, new AccountingPgFixture.AllowAllManualPrice())
            .CreateAsync(new CreateStockMovementDto
            {
                ItemId = item.Id,
                WarehouseId = warehouse.Id,
                MovementType = (int)MovementType.OpeningBalance,
                Quantity = 500m,
                UnitCost = 10m,
                MovementDate = DateTime.Today,
                ReferenceNumber = "UI-P3-SEED"
            });

        var sales = new SalesInvoiceService(ctx, new JournalEntryService(ctx),
            new AccountingPgFixture.AllowAllManualPrice());
        var invoice = await sales.CreateAsync(new CreateSalesInvoiceDto
        {
            CustomerId = customer.Id,
            WarehouseId = warehouse.Id,
            InvoiceDate = DateTime.Today,
            InvoiceType = (int)SalesInvoiceType.OnAccount,
            PaymentMethod = (int)SalesPaymentMethod.OnAccount,
            TaxRate = 16m,
            Lines = new List<CreateSalesInvoiceLineDto>
            {
                new() { ItemId = item.Id, Quantity = 10m, UnitPrice = 15m }
            }
        });

        var receipts = new ReceiptVoucherService(ctx, new JournalEntryService(ctx), new AllowAllAuth());
        var receipt = await receipts.CreateDraftAsync(new CreateReceiptVoucherDto
        {
            CustomerId = customer.Id,
            VoucherDate = DateTime.Today,
            PaymentMethod = 1,
            Amount = 150m,
            Description = "تحصيل تهيئة واجهة"
        });
        await receipts.ApproveAsync(receipt.Id);
        await receipts.PostAsync(receipt.Id);
        await receipts.SetAllocationsAsync(receipt.Id, new SetReceiptAllocationsRequest
        {
            Allocations = new List<CustomerAllocationInput>
            {
                new() { SalesInvoiceId = invoice.Id, Amount = 150m }
            },
            Note = "تخصيص تهيئة واجهة"
        });

        var returns = new SalesReturnService(ctx, new JournalEntryService(ctx));
        var ret = await returns.CreateAsync(new CreateSalesReturnDto
        {
            SalesInvoiceId = invoice.Id,
            WarehouseId = warehouse.Id,
            ReturnDate = DateTime.Today,
            Lines = new List<CreateSalesReturnLineDto>
            {
                new() { ItemId = item.Id, Quantity = 4m, UnitPrice = 15m }
            }
        });

        await ctx.Entry(customer).ReloadAsync();
        var retTotal = await ctx.Set<SalesReturn>().AsNoTracking()
            .Where(r => r.Id == ret.Id).Select(r => r.TotalAmount).FirstAsync();

        await File.WriteAllTextAsync(LogPath,
            $"customer={customer.Id}|code={customer.Code}|balance={customer.CurrentBalance}\n" +
            $"invoice={invoice.Id}|invoice_number={invoice.InvoiceNumber}|total=174.00\n" +
            $"return={ret.Id}|return_number={ret.ReturnNumber}|total={retTotal}\n" +
            $"expected_credit=45.60|expected_refund_cap=69.60\n");
    }
}
