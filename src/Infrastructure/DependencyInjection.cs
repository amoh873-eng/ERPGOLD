using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Infrastructure.Data;
using ERPSystem.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ERPSystem.Infrastructure;

/// <summary>
/// Extension methods for registering Infrastructure-layer services.
/// Keeps Program.cs clean by encapsulating all DI registration in one place.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all infrastructure services: DbContext and application services.
    /// </summary>
    /// <param name="services">The IServiceCollection to add services to.</param>
    /// <param name="configuration">Application configuration (for connection string).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register DbContext with PostgreSQL
        // لماذا AddDbContext وليس AddDbContextPool؟
        // DbContextPool يعيد استخدام السياق بين الطلبات وهو أسرع،
        // لكننا نستخدم AddDbContext حالياً للبساطة.
        // في الإصدارات المستقبلية يمكن الترقية إلى Pool للتحسين.
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsqlOptions =>
                {
                    // مكان ملفات الترحيل (Migrations) داخل مشروع Infrastructure
                    npgsqlOptions.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                    // ── مرونة الاتصال: إعادة المحاولة تلقائياً على الأخطاء العابرة (Npgsql) ──
                    // يعيد المحاولة حتى 5 مرات بفاصل متزايد حتى 10 ثوانٍ عند انقطاع/بدء تشغيل القاعدة.
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorCodesToAdd: null);
                }).ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        // Register application services
        // AddScoped: ينشأ كائن جديد لكل طلب HTTP، ويُتلف بعد انتهاء الطلب.
        // هذا مناسب لـ Blazor Server لأن كل دائرة (Circuit) تمثل جلسة مستخدم.
        // خدمة الأرصدة الموحّدة: مصدر واحد لأرصدة شجرة الحسابات (الشاشة + التقرير الموحّد).
        // لا يُحتسب أي رصيد في الواجهة أو في التقرير بشكل منفصل — نقطة احتساب واحدة.
        services.AddScoped<IAccountBalanceService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new AccountBalanceService(dbContext);
        });

        services.AddScoped<IAccountService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var balances = sp.GetRequiredService<IAccountBalanceService>();
            return new AccountService(dbContext, balances);
        });

        // ==================== موديول المستودعات ====================
        services.AddScoped<IUnitService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new UnitService(dbContext);
        });

        services.AddScoped<ICategoryService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new CategoryService(dbContext);
        });

        services.AddScoped<IItemService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            // Requirement 3: مصدر تصريح تعديل السعر يدويًا يُحلّ ديناميكيًا من مضيف التشغيل (Web/Api)
            // — لا يأتي من الواجهة أبدًا، لذلك لا يمكن تجاوز الفحص من الشاشة.
            var priceAuth = sp.GetRequiredService<IManualPriceAuthorization>();
            return new ItemService(dbContext, priceAuth);
        });

        services.AddScoped<IWarehouseService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new WarehouseService(dbContext);
        });

        services.AddScoped<IStockMovementService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var priceAuth = sp.GetRequiredService<IManualPriceAuthorization>();
            return new StockMovementService(dbContext, priceAuth);
        });

        services.AddScoped<IStockCountService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new StockCountService(dbContext);
        });

        // ==================== المحاسبة العامة: القيود المحاسبية ====================
        services.AddScoped<IJournalEntryService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new JournalEntryService(dbContext);
        });

        // ==================== موديول المبيعات ====================
        services.AddScoped<ICustomerService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new CustomerService(dbContext);
        });

        services.AddScoped<ISalesInvoiceService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journalService = sp.GetRequiredService<IJournalEntryService>();
            var priceAuth = sp.GetRequiredService<IManualPriceAuthorization>();
            return new SalesInvoiceService(dbContext, journalService, priceAuth);
        });

        services.AddScoped<ISalesQuoteService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var invoiceService = sp.GetRequiredService<ISalesInvoiceService>();
            return new SalesQuoteService(dbContext, invoiceService);
        });

        services.AddScoped<IPurchaseOrderService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var invoiceService = sp.GetRequiredService<IPurchaseInvoiceService>();
            return new PurchaseOrderService(dbContext, invoiceService);
        });

        services.AddScoped<ISalesReturnService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journalService = sp.GetRequiredService<IJournalEntryService>();
            return new SalesReturnService(dbContext, journalService);
        });

        // ==================== التقارير المالية ====================
        services.AddScoped<IReportService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new ReportService(dbContext);
        });

        // ==================== موديول المشتريات ====================
        services.AddScoped<ISupplierService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new SupplierService(dbContext);
        });

        services.AddScoped<IPurchaseInvoiceService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journalService = sp.GetRequiredService<IJournalEntryService>();
            var priceAuth = sp.GetRequiredService<IManualPriceAuthorization>();
            return new PurchaseInvoiceService(dbContext, journalService, priceAuth);
        });

        services.AddScoped<IPurchaseReturnService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journalService = sp.GetRequiredService<IJournalEntryService>();
            return new PurchaseReturnService(dbContext, journalService);
        });

        // ==================== موديول الموارد البشرية ====================
        services.AddScoped<IDepartmentService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new DepartmentService(dbContext);
        });

        services.AddScoped<IPositionService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new PositionService(dbContext);
        });

        services.AddScoped<IEmployeeService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new EmployeeService(dbContext);
        });

        services.AddScoped<ILeaveService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new LeaveService(dbContext);
        });

        services.AddScoped<IAttendanceService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new AttendanceService(dbContext);
        });

        services.AddScoped<IPayrollService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journal = sp.GetRequiredService<IJournalEntryService>();
            var att = sp.GetRequiredService<IAttendanceService>();
            return new PayrollService(dbContext, journal, att);
        });

        // ==================== موديول المصاريف ====================
        services.AddScoped<IExpenseCategoryService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new ExpenseCategoryService(dbContext);
        });

        services.AddScoped<IExpenseService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journalService = sp.GetRequiredService<IJournalEntryService>();
            return new ExpenseService(dbContext, journalService);
        });

        // ==================== موديول سندات القبض والصرف (المرحلة B) ====================
        // دورة حياة (D1-A): مسودة → اعتماد → ترحيل → إلغاء · القيد عبر محرّك القيود المركزي حصرًا
        // · سجل تدقيق (D3-A) في نفس المعاملة · صلاحيات دقيقة (D2-A) تُحقن خلفيًا من طبقة الاستضافة.
        services.AddScoped<IReceiptVoucherService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journal = sp.GetRequiredService<IJournalEntryService>();
            var auth = sp.GetRequiredService<IVoucherAuthorization>();
            return new ReceiptVoucherService(dbContext, journal, auth);
        });

        services.AddScoped<IPaymentVoucherService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journal = sp.GetRequiredService<IJournalEntryService>();
            var auth = sp.GetRequiredService<IVoucherAuthorization>();
            return new PaymentVoucherService(dbContext, journal, auth);
        });

        // PU-04 — عكس سندات الصرف + استعلامات الرصيد/المطابقة.
        services.AddScoped<IPaymentVoucherReversalService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journal = sp.GetRequiredService<IJournalEntryService>();
            var auth = sp.GetRequiredService<IVoucherAuthorization>();
            return new PaymentVoucherReversalService(dbContext, journal, auth);
        });
        services.AddScoped<SupplierAllocationQueries>();
        services.AddScoped<SupplierReconciliationService>();

        // SUB-2b/P3 — استعلامات أهلية رد أموال العملاء (قراءة فقط).
        services.AddScoped<CustomerRefundQueries>();

        // ==================== موديول CRM ====================
        services.AddScoped<ILeadService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new LeadService(dbContext);
        });

        services.AddScoped<IOpportunityService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new OpportunityService(dbContext);
        });

        services.AddScoped<IActivityService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new ActivityService(dbContext);
        });

        services.AddScoped<IFollowUpService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new FollowUpService(dbContext);
        });

        services.AddScoped<ICustomReportService>(sp => new CustomReportService(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IReportService>()));
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<IJoFotaraIntegrationService, JoFotaraIntegrationService>();
        services.AddHttpClient();

        // ==================== إشعارات الموظفين + دفعات انتهاء الصلاحية + البيع الموقوف ====================
        services.AddScoped<IStaffNotificationService>(sp => new StaffNotificationService(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IStockBatchService>(sp => new StockBatchService(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IItemBatchService>(sp => new ItemBatchService(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IStockWriteOffService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journal = sp.GetRequiredService<IJournalEntryService>();
            return new StockWriteOffService(dbContext, journal);
        });
        services.AddScoped<IHeldSaleService>(sp => new HeldSaleService(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<ICashDrawerService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journal = sp.GetRequiredService<IJournalEntryService>();
            return new CashDrawerService(dbContext, journal);
        });

        // ==================== الأرصدة الافتتاحية الرسمية (Opening Balances) ====================
        services.AddScoped<IOpeningBalanceService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var journal = sp.GetRequiredService<IJournalEntryService>();
            var auth = sp.GetRequiredService<IOpeningBalanceAuthorization>();
            return new OpeningBalanceService(dbContext, journal, auth);
        });

        // ==================== الفترات المحاسبية (2026-10-25) ====================
        // قفل/فتح الشهور + الفحص المركزي الذي يمنع الترحيل داخل فترة مغلقة.
        services.AddScoped<IAccountingPeriodService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            var auth = sp.GetRequiredService<IAccountingPeriodAuthorization>();
            return new AccountingPeriodService(dbContext, auth);
        });

        // ==================== مطابقة الحسابات الرقابية مع السجلات المساعدة (قراءة فقط) ====================
        services.AddScoped<ISubLedgerReconciliationService>(sp =>
        {
            var dbContext = sp.GetRequiredService<AppDbContext>();
            return new SubLedgerReconciliationService(dbContext);
        });

        return services;
    }
}
