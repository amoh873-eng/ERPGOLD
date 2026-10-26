using ERPSystem.Application.Interfaces;
using ERPSystem.Web.Middleware;
using Microsoft.AspNetCore.Authentication;

namespace ERPSystem.Web.Owner;

public static class OwnerTrialEndpoints
{
    public static void Map(WebApplication app)
    {
        var secret = OwnerMiddlewareHelpers.GetOwnerSecretPath(app.Configuration);
        var prefix = "/" + secret;
        app.MapGet(prefix + "/trial", async (HttpContext ctx) =>
        {
            var ar = await ctx.AuthenticateAsync("OwnerScheme");
            if (ar.Succeeded != true) { ctx.Response.StatusCode = 404; return; }
            using var scope = ctx.RequestServices.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            var s = await svc.GetAsync(ctx.RequestAborted);
            var sec2 = OwnerMiddlewareHelpers.GetOwnerSecretPath(ctx.RequestServices.GetRequiredService<IConfiguration>());
            var body=$@"<div class='card'><h2>الوضع التجريبي (3 أيام)</h2>
<p style='color:#6B7280'>الحالة: {(s.IsTrialMode? $"<span class='badge badge-on'>مفعل — ينتهي {s.TrialExpiresAt:yyyy-MM-dd HH:mm}</span>" : "<span class='badge badge-off'>غير مفعل</span>")}</p>
<form method='post' action='/{sec2}/trial/activate'><button class='primary' type='submit'>تفعيل تجريبي (3 أيام)</button></form>
<form method='post' action='/{sec2}/trial/deactivate' style='margin-top:8px'><button class='ghost' type='submit'>إلغاء التجريبي</button></form>
<p style='font-size:.82rem;color:#6B7280'>عند انتهاء 3 أيام يُقفل النظام تلقائيًا برسالة تجريبية + بيانات التواصل.</p>
<hr style='margin:16px 0'>
<h3>بيانات تجريبية</h3><p style='font-size:.85rem;color:#6B7280'>تعبئة زبائن/أصناف/فواتير/موظفين وهمية للعرض التسويقي</p>
<form method='post' action='/{sec2}/trial/seed'><button class='primary' type='submit'>تعبئة بيانات وهمية</button></form>
</div>";
            var html=OwnerLayout.Wrap("التجريبي", sec2, body, "trial");
            ctx.Response.ContentType="text/html; charset=utf-8"; await ctx.Response.WriteAsync(html);
        }).AllowAnonymous();

        app.MapPost(prefix + "/trial/activate", async (HttpContext ctx) =>
        {
            var ar=await ctx.AuthenticateAsync("OwnerScheme"); if(ar.Succeeded!=true){ctx.Response.StatusCode=404;return;}
            using var scope=ctx.RequestServices.CreateScope();
            var svc=scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            await svc.UpdateAsync(s=>{ s.IsTrialMode=true; s.TrialStartedAt=DateTime.UtcNow; s.TrialExpiresAt=DateTime.UtcNow.AddDays(3); s.IsDeploymentActive=true; }, ctx.RequestAborted);
            var sec2=OwnerMiddlewareHelpers.GetOwnerSecretPath(ctx.RequestServices.GetRequiredService<IConfiguration>());
            ctx.Response.Redirect("/"+sec2+"/trial");
        }).DisableAntiforgery();
        app.MapPost(prefix + "/trial/deactivate", async (HttpContext ctx) =>
        {
            var ar=await ctx.AuthenticateAsync("OwnerScheme"); if(ar.Succeeded!=true){ctx.Response.StatusCode=404;return;}
            using var scope=ctx.RequestServices.CreateScope();
            var svc=scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            await svc.UpdateAsync(s=>{ s.IsTrialMode=false; s.TrialStartedAt=null; s.TrialExpiresAt=null; }, ctx.RequestAborted);
            var sec2=OwnerMiddlewareHelpers.GetOwnerSecretPath(ctx.RequestServices.GetRequiredService<IConfiguration>());
            ctx.Response.Redirect("/"+sec2+"/trial");
        }).DisableAntiforgery();
        app.MapPost(prefix + "/trial/seed", async (HttpContext ctx) =>
        {
            var ar=await ctx.AuthenticateAsync("OwnerScheme"); if(ar.Succeeded!=true){ctx.Response.StatusCode=404;return;}
            using var scope=ctx.RequestServices.CreateScope();
            var sp=scope.ServiceProvider;
            await SeedDemoDataAsync(sp);
            var sec2=OwnerMiddlewareHelpers.GetOwnerSecretPath(ctx.RequestServices.GetRequiredService<IConfiguration>());
            ctx.Response.Redirect("/"+sec2+"/trial");
        }).DisableAntiforgery();
    }

    private static async Task SeedDemoDataAsync(IServiceProvider sp)
    {
        var db=sp.GetRequiredService<ERPSystem.Infrastructure.Data.AppDbContext>();
        // Seed realistic QA dataset: customers, items with movements, suppliers, employees, invoices, etc.
        var wh=db.Warehouses.FirstOrDefault();
        if(wh==null) return;
        var cat=db.Categories.FirstOrDefault();
        var unit=db.Units.FirstOrDefault();
        // Customers
        for(int i=6;i<=8;i++){
            if(db.Customers.Any(c=>c.Code==$"QA-CUST{i}")) continue;
            db.Customers.Add(new ERPSystem.Domain.Entities.Customer{ Id=Guid.NewGuid(), Code=$"QA-CUST{i}", NameAr=$"عميل فحص {i}", NameEn=$"QA Customer {i}", Phone=$"96279{i:D7}", IsActive=true, CreatedAt=DateTime.UtcNow });
        }
        // Items
        for(int i=6;i<=10;i++){
            if(db.Items.Any(it=>it.Code==$"QA-ITEM{i}")) continue;
            // إصلاح 2026-10-23 (الدفعة E): لا يُزرع رصيد مخزون بلا حركة مقابلة.
            // المخزون الافتتاحي كان يُكتب في CurrentStock مباشرة بلا StockMovement ⇒ كمية مخزَّنة
            // بلا أثر في دفتر الكميات. الآن يبدأ الصنف بصفر، ويُدخَل المخزون عبر فاتورة شراء معتمدة المسار.
            var it=new ERPSystem.Domain.Entities.Item{ Id=Guid.NewGuid(), Code=$"QA-ITEM{i}", NameAr=$"صنف فحص {i}", NameEn=$"QA Item {i}", CategoryId=cat?.Id ?? Guid.Empty, UnitId=unit?.Id ?? Guid.Empty, CostPrice=10*i, SalePrice=15*i, CurrentStock=0, IsActive=true, CreatedAt=DateTime.UtcNow };
            if(unit!=null) it.UnitId=unit.Id;
            if(cat!=null) it.CategoryId=cat.Id;
            db.Items.Add(it);
        }
        db.SaveChanges();
        // Suppliers
        for(int i=1;i<=2;i++){
            if(db.Suppliers.Any(s=>s.Code==$"QA-SUP{i}")) continue;
            db.Suppliers.Add(new ERPSystem.Domain.Entities.Supplier{ Id=Guid.NewGuid(), Code=$"QA-SUP{i}", NameAr=$"مورد فحص {i}", NameEn=$"QA Supplier {i}", IsActive=true, CreatedAt=DateTime.UtcNow });
        }
        // Departments/Positions already seeded; add a test employee if missing
        var dept=db.Departments.FirstOrDefault();
        var pos=db.Positions.FirstOrDefault();
        if(!db.Employees.Any(e=>e.EmployeeNumber=="QA-EMP1") && dept!=null && pos!=null){
            db.Employees.Add(new ERPSystem.Domain.Entities.Employee{ Id=Guid.NewGuid(), EmployeeNumber="QA-EMP1", NameAr="موظف فحص", DepartmentId=dept.Id, PositionId=pos.Id, HireDate=DateTime.UtcNow.Date.AddMonths(-2), BasicSalary=800, CreatedAt=DateTime.UtcNow });
        }
        db.SaveChanges();
        // ═══ إصلاح 2026-10-23 (الدفعة E): لا تُنشأ مستندات مالية مباشرة من DbContext ═══
        // كان هنا إنشاء فاتورة مبيعات بحالة «مرحّل» + سطر + حركة مخزون (-2) مباشرة:
        //   • بلا قيد محاسبي (لا إيراد ولا ضريبة ولا تكلفة بضاعة ولا ذمم) ⇒ فاتورة مرحَّلة بلا أثر مالي.
        //   • بلا تحديث Item.CurrentStock ⇒ الكمية المخزَّنة ≠ مجموع الحركات.
        //   • PaidAmount = الإجمالي بلا تحصيل فعلي.
        // الآن: المخزون يُدخَل عبر خدمة المشتريات والبيع عبر خدمة المبيعات (المسار المركزي الوحيد)،
        // فتُنشأ القيود والحركات والأرصدة معًا. والمسارات الفاشلة تُتجاهل بلا أثر مالي وهمي.
        await SeedStockAndSaleViaCentralServicesAsync(sp, wh.Id);
        await Task.CompletedTask;
    }

    /// <summary>
    /// يزرع مخزونًا وفاتورة مبيعات تجريبية عبر الخدمات المركزية فقط (أفضل جهد):
    /// أي فشل (فترة محاسبية مغلقة، إعداد ناقص…) يُسجَّل ولا يُنشئ مستندًا وهميًا.
    /// </summary>
    private static async Task SeedStockAndSaleViaCentralServicesAsync(IServiceProvider sp, Guid warehouseId)
    {
        var db = sp.GetRequiredService<ERPSystem.Infrastructure.Data.AppDbContext>();
        var log = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("OwnerTrialSeed");

        var supplier = db.Suppliers.FirstOrDefault(s => s.Code == "QA-SUP1") ?? db.Suppliers.FirstOrDefault();
        var customer = db.Customers.FirstOrDefault(c => c.Code.StartsWith("QA-CUST")) ?? db.Customers.FirstOrDefault();
        var item = db.Items.FirstOrDefault(it => it.Code.StartsWith("QA-ITEM")) ?? db.Items.FirstOrDefault();
        if (supplier is null || customer is null || item is null) return;

        // (1) إدخال المخزون عبر فاتورة شراء آجلة (تُنشئ الحركة + الرصيد + القيد + رصيد المورد)
        var hasStock = (db.Items.FirstOrDefault(it => it.Id == item.Id)?.CurrentStock ?? 0m) > 0;
        if (!hasStock)
        {
            try
            {
                var purchases = sp.GetRequiredService<IPurchaseInvoiceService>();
                await purchases.CreateAsync(new ERPSystem.Application.DTOs.Purchases.CreatePurchaseInvoiceDto
                {
                    SupplierId = supplier.Id,
                    WarehouseId = warehouseId,
                    InvoiceDate = DateTime.UtcNow.Date,
                    InvoiceType = (int)ERPSystem.Domain.Enums.PurchaseInvoiceType.OnAccount,
                    PaymentMethod = ERPSystem.Application.DTOs.Purchases.CreatePurchaseInvoiceDto.PaymentOnAccount,
                    Lines = new List<ERPSystem.Application.DTOs.Purchases.CreatePurchaseInvoiceLineDto>
                    {
                        new() { ItemId = item.Id, Quantity = 20m, UnitCost = item.CostPrice }
                    }
                });
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "تعذّر إنشاء فاتورة الشراء التجريبية — تم تخطيها بلا أثر مالي.");
                return;
            }
        }

        // (2) فاتورة مبيعات آجلة عبر الخدمة المركزية (إيراد + ضريبة + تكلفة بضاعة + مخزون + رصيد العميل)
        if (db.SalesInvoices.Any()) return;
        try
        {
            var sales = sp.GetRequiredService<ISalesInvoiceService>();
            await sales.CreateAsync(new ERPSystem.Application.DTOs.Sales.CreateSalesInvoiceDto
            {
                CustomerId = customer.Id,
                WarehouseId = warehouseId,
                InvoiceDate = DateTime.UtcNow.Date,
                InvoiceType = (int)ERPSystem.Domain.Enums.SalesInvoiceType.OnAccount,
                PaymentMethod = (int)ERPSystem.Domain.Enums.SalesPaymentMethod.OnAccount,
                TaxRate = 16m,
                Lines = new List<ERPSystem.Application.DTOs.Sales.CreateSalesInvoiceLineDto>
                {
                    new() { ItemId = item.Id, Quantity = 2m, UnitPrice = item.SalePrice }
                }
            });
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "تعذّر إنشاء فاتورة المبيعات التجريبية — تم تخطيها بلا أثر مالي.");
        }
    }
}
