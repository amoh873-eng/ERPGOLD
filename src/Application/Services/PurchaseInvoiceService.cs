using System.Data;
using ERPSystem.Application.DTOs.BankChecks;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERPSystem.Application.Services;

/// <summary>
/// Implements purchase invoice business logic (فواتير المشتريات) مع الربط المحاسبي:
/// - حركة مخزون "وارد مشتريات" لكل بند.
/// - قيد: مدين (مخزون البضاعة) ، دائن (الصندوق نقدي / الموردين آجل).
/// </summary>
public class PurchaseInvoiceService : IPurchaseInvoiceService
{
    private const string AccountInventory = "1300";
    private const string AccountCash = "1100";
    private const string AccountPayable = "2200";

    private readonly DbContext _context;
    private readonly IJournalEntryService _journalService;
    private readonly IManualPriceAuthorization _priceAuthorization;

    public PurchaseInvoiceService(DbContext context, IJournalEntryService journalService,
        IManualPriceAuthorization priceAuthorization)
    {
        _context = context;
        _journalService = journalService;
        _priceAuthorization = priceAuthorization;
    }

    public async Task<List<PurchaseInvoiceDto>> GetInvoicesAsync()
    {
        var invoices = await _context.Set<PurchaseInvoice>()
            .Include(i => i.Supplier)
            .Include(i => i.Warehouse)
            .OrderByDescending(i => i.InvoiceDate)
            .ThenByDescending(i => i.CreatedAt)
            .ToListAsync();
        return invoices.Select(MapToDto).ToList();
    }

    public async Task<PurchaseInvoiceDto?> GetByIdAsync(Guid id)
    {
        var invoice = await _context.Set<PurchaseInvoice>()
            .Include(i => i.Supplier)
            .Include(i => i.Warehouse)
            .Include(i => i.Lines).ThenInclude(l => l.Item)
            .FirstOrDefaultAsync(i => i.Id == id && !i.IsDeleted);
        return invoice is null ? null : MapToDto(invoice);
    }

    public async Task<PurchaseInvoiceDto> CreateAsync(CreatePurchaseInvoiceDto dto)
    {
        // ── D-01: مفتاح التمييز (Idempotency) ──────────────────────────────────────────
        // إعادة إرسال الطلب نفسه (نقرة مكررة / إعادة محاولة بعد غموض النتيجة) لا تُنشئ
        // فاتورة أو قيدًا أو حركة مخزون جديدة — بل تُعاد **نتيجة العملية الأصلية** كما هي
        // (نفس Id ونفس InvoiceNumber)، فينتهي غموض الواجهة (D-06) بلا تكرار (D-01).
        if (dto.IdempotencyKey is { } existingKey && existingKey != Guid.Empty)
        {
            var already = await _context.Set<PurchaseInvoice>()
                .AsNoTracking()
                .Include(i => i.Supplier).Include(i => i.Warehouse)
                .Include(i => i.Lines).ThenInclude(l => l.Item)
                .FirstOrDefaultAsync(i => i.IdempotencyKey == existingKey && !i.IsDeleted);
            if (already is not null)
                return MapToDto(already);
        }

        var supplier = await _context.Set<Supplier>()
            .FirstOrDefaultAsync(s => s.Id == dto.SupplierId && !s.IsDeleted);
        if (supplier is null) throw new InvalidOperationException("المورد غير موجود");

        var warehouse = await _context.Set<Warehouse>()
            .FirstOrDefaultAsync(w => w.Id == dto.WarehouseId && !w.IsDeleted);
        if (warehouse is null) throw new InvalidOperationException("المخزن غير موجود");

        if (dto.Lines is null || dto.Lines.Count == 0)
            throw new InvalidOperationException("يجب إضافة بند واحد على الأقل.");

        var type = (PurchaseInvoiceType)dto.InvoiceType;
        if (type != PurchaseInvoiceType.Cash && type != PurchaseInvoiceType.OnAccount)
            throw new InvalidOperationException("نوع الفاتورة غير صالح.");

        // ── D-04: فرض التوافق بين نوع الفاتورة وطريقة الدفع (دفاع في الخدمة، لا في الواجهة فقط) ──
        // null = غير محدَّد ⇒ يُستنتج من النوع (توافق خلفي).
        // والشيك (2) مسموح مع النوعين لأنه يتبع دورة حياة الشيك ولا يُعتبر نقدًا متاحًا.
        if (dto.PaymentMethod is int pm)
        {
            if (type == PurchaseInvoiceType.Cash && pm == CreatePurchaseInvoiceDto.PaymentOnAccount)
                throw new InvalidOperationException(
                    "تعارض: فاتورة نقدية بطريقة دفع «آجل». اختر «نقدي» أو «شيك».");
            if (type == PurchaseInvoiceType.OnAccount && pm == CreatePurchaseInvoiceDto.PaymentCash)
                throw new InvalidOperationException(
                    "تعارض: فاتورة آجلة بطريقة دفع «نقدي». اختر «آجل» أو «شيك».");
            if (pm is not (CreatePurchaseInvoiceDto.PaymentOnAccount
                           or CreatePurchaseInvoiceDto.PaymentCheck
                           or CreatePurchaseInvoiceDto.PaymentCash))
                throw new InvalidOperationException("طريقة الدفع غير صالحة.");
        }

        // السداد بشيك (PaymentMethod = 2): تُرحَّل الفاتورة على المورد كالمعتاد ثم يُخفَّض
        // الالتزام فوراً بقيد إصدار شيك (مدين المورد / دائن «شيكات برسم السداد» 2300).
        var isCheckPayment = dto.PaymentMethod == CreatePurchaseInvoiceDto.PaymentCheck;
        if (isCheckPayment)
        {
            if (string.IsNullOrWhiteSpace(dto.CheckNumber) || string.IsNullOrWhiteSpace(dto.CheckBankName))
                throw new InvalidOperationException("رقم الشيك واسم البنك مطلوبان عند السداد بشيك.");
            if (dto.CheckDueDate is null)
                throw new InvalidOperationException("تاريخ استحقاق الشيك مطلوب عند السداد بشيك.");
            if (DateOnly.FromDateTime(dto.CheckDueDate.Value) < DateOnly.FromDateTime(dto.InvoiceDate))
                throw new InvalidOperationException("تاريخ استحقاق الشيك لا يمكن أن يسبق تاريخ الفاتورة.");
        }

        var distinctItemIds = dto.Lines.Select(l => l.ItemId).Distinct().ToList();
        if (distinctItemIds.Count != dto.Lines.Count)
            throw new InvalidOperationException("لا يمكن تكرار نفس الصنف في فاتورة واحدة.");

        var items = await _context.Set<Item>()
            .Where(i => distinctItemIds.Contains(i.Id) && !i.IsDeleted)
            .ToListAsync();
        if (items.Count != distinctItemIds.Count)
            throw new InvalidOperationException("أحد الأصناف المحددة غير موجود.");
        var itemDict = items.ToDictionary(i => i.Id);

        // ── Requirement 3: رفض تكلفة الوحدة اليدوية غير المصرّح بها قبل أي ترقيم/كتابة ──
        // الحارس الخلفي هو الحماية الفعلية: أي استدعاء مباشر للخدمة (تجاوز الواجهة) يُرفض،
        // والتحقق قبل الترقيم/الكتابة حتى لا يترك أي أثر (رقم فاتورة/حركة/قيد).
        // القيمة الافتراضية في الشاشة = Item.CostPrice ⇒ لا يتأثر المستخدم غير المصرّح بالسعر النظامي.
        foreach (var input in dto.Lines)
        {
            if (!itemDict.TryGetValue(input.ItemId, out var pricedItem))
                continue; // يلتقطه التحقق العام للأصناف أعلاه

            ManualPriceGuard.EnsureCanOverride(_priceAuthorization, ManualPriceModules.Purchases,
                input.UnitCost, pricedItem.CostPrice, "تكلفة الوحدة");
        }

        var invoice = new PurchaseInvoice
        {
            Id = Guid.NewGuid(),
            InvoiceNumber = string.Empty, // إصلاح R2 (2026-10-21): يُعيَّن داخل المعاملة
            SupplierId = supplier.Id,
            WarehouseId = warehouse.Id,
            DueDate = dto.DueDate ?? dto.InvoiceDate.AddDays(30),
            InvoiceDate = dto.InvoiceDate,
            InvoiceType = type,
            Status = DocumentStatus.Posted,
            Note = dto.Note,
            CreatedAt = DateTime.UtcNow
        };

        var subTotal = 0m;
        var lines = new List<PurchaseInvoiceLine>();

        foreach (var input in dto.Lines)
        {
            if (input.Quantity <= 0)
                throw new InvalidOperationException("الكمية يجب أن تكون أكبر من صفر.");

            var item = itemDict[input.ItemId];

            var lineTotal = Math.Round(input.Quantity * input.UnitCost, 2);
            // ضريبة المبيعات لكل سطر من نسبة الصنف (لقطة تُحفظ مع السطر — لا تتأثر بتغيّر نسبة الصنف لاحقاً)
            var line = new PurchaseInvoiceLine
            {
                Id = Guid.NewGuid(),
                PurchaseInvoiceId = invoice.Id,
                ItemId = input.ItemId,
                Quantity = input.Quantity,
                UnitCost = input.UnitCost,
                LineTotal = lineTotal,
                TaxRate = item.TaxRate,
                TaxAmount = Math.Round(lineTotal * item.TaxRate / 100m, 2),
                CreatedAt = DateTime.UtcNow
            };
            lines.Add(line);
            subTotal += line.LineTotal;
        }

        invoice.SubTotal = Math.Round(subTotal, 2);
        invoice.TaxAmount = Math.Round(lines.Sum(l => l.TaxAmount), 2);
        // نسبة مرجعية على الرأس فقط (لا تُستخدم في الحساب/التقارير/العرض)
        invoice.TaxRate = invoice.SubTotal > 0m
            ? Math.Round(invoice.TaxAmount / invoice.SubTotal * 100m, 2)
            : 0m;
        // إجمالي مستحق للمورد = الصافي + الضريبة (تُرسمل الضريبة ضمن تكلفة المخزون)
        invoice.TotalAmount = Math.Round(invoice.SubTotal + invoice.TaxAmount, 2);
        invoice.PaidAmount = type == PurchaseInvoiceType.Cash ? invoice.TotalAmount : 0m;
        invoice.Lines = lines;

        // إصلاح R2/R6 (2026-10-21): دورة مشتريات ذرّية + حاجز وقائي قبل الإضافة إلى السياق
        DocumentFailureCleanup.EnsureNoPendingDocuments(_context);

        // D-01: نُغلّف المعاملة بالكامل — عند تصادم مفتاح التمييز تتراجع كاملة (لا آثار جزئية)
        // ثم نُعيد نتيجة الفاتورة الفائزة بالسباق بدل إظهار خطأ للمستخدم.
        try
        {
        return await DbTransactionHelper.ExecuteAsync(_context, async () =>
        {
            try
            {
                // إصلاح R2: الترقيم داخل المعاملة ⇒ لا يُستهلك رقم فاتورة في محاولة تُرفض لاحقاً
                invoice.InvoiceNumber = await NextInvoiceNumberAsync();
                // D-01: ختم مفتاح التمييز على المستند — بدونه لا يعمل الفهرس الفريد
                // ولا مسار إعادة الإرسال (وهو الخطأ الذي كشفته اختبارات الانحدار T01/T02).
                invoice.IdempotencyKey = dto.IdempotencyKey;

        _context.Set<PurchaseInvoice>().Add(invoice);
        _context.Set<PurchaseInvoiceLine>().AddRange(lines);
        // بيانات الصلاحية لكل صنف (لإنشاء دفعات للصنف الذي يتتبّع الانتهاء).
        var expiryByItem = dto.Lines.ToDictionary(l => l.ItemId, l => l.ExpiryDate);
        var batchNoByItem = dto.Lines.ToDictionary(l => l.ItemId, l => l.BatchNumber);

        foreach (var line in lines)
        {
            _context.Set<StockMovement>().Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ItemId = line.ItemId,
                WarehouseId = warehouse.Id,
                MovementType = MovementType.PurchaseReceipt,
                Quantity = line.Quantity,
                UnitCost = line.UnitCost,
                ReferenceNumber = invoice.InvoiceNumber,
                Note = $"فاتورة مشتريات {invoice.InvoiceNumber}",
                MovementDate = invoice.InvoiceDate,
                CreatedAt = DateTime.UtcNow
            });

            // قفل صف الصنف + إعادة قراءة الرصيد والتكلفة المخزَّنين تحت القفل
            // (يمنع أن يكتب وارد متزامن قيمة قديمة فوق تحديث آخر، ويصحّح أساس المتوسط المرجّح)
            await StockAvailabilityHelper.LockAndRefreshItemAsync(_context, line.ItemId);

            // تقييم المتوسط المرجح: عند كل وارد نعيد حساب تكلفة الوحدة
            // newCost = (الرصيد القديم × التكلفة القديمة + الوارد × تكلفته) / إجمالي الكمية
            var item = itemDict[line.ItemId];
            item.CostPrice = InventoryValuation.ComputeWeightedAverage(
                item.CurrentStock, item.CostPrice, line.Quantity, line.UnitCost);
            item.CurrentStock += line.Quantity;
            item.UpdatedAt = DateTime.UtcNow;

            // ── تتبّع انتهاء الصلاحية (طبقة دفعات موازية، فقط للأصناف المفعّلة) ──
            // إنشاء دفعة بجانب حركة الوارد — لا يُستبدل وصلا/CurrentStock بل يُضاف.
            if (item.TracksExpiry)
            {
                var expiry = expiryByItem.TryGetValue(line.ItemId, out var e) ? e : null;
                if (expiry is null)
                    throw new InvalidOperationException(
                        $"الصنف '{item.Code}' يتتبّع انتهاء الصلاحية — مطلوب إدخال تاريخ الانتهاء عند الاستلام.");
                await StockBatchHelper.CreateOnReceiveAsync(_context, line.ItemId, warehouse.Id,
                    line.Quantity, expiry, batchNoByItem.TryGetValue(line.ItemId, out var bn) ? bn : null,
                    invoice.InvoiceNumber, invoice.InvoiceDate);
            }

            // ── تتبّع الدُفعات (ItemBatch): إلزامي للصنف TracksBatches + إنشاء/دمج دُفعة + تكامل الرصيد ──
            if (item.TracksBatches)
            {
                var expiry = expiryByItem.TryGetValue(line.ItemId, out var e2) ? e2 : null;
                var bno = batchNoByItem.TryGetValue(line.ItemId, out var b2) ? b2 : null;
                await ItemBatchHelper.CreateOnReceiveAsync(_context, line.ItemId, warehouse.Id,
                    line.Quantity, expiry, bno, invoice.InvoiceDate, invoice.Id);
                // ضمانة التزامن: مجموع الدُفعات == رصيد حركات المخزون (نفس المعاملة)
                await AssertBatchesConsistentAfterReceiptAsync(line.ItemId, warehouse.Id);
            }
        }

        var inventoryAccount = await GetAccountByCodeAsync(AccountInventory);
        var creditAccount = await GetAccountByCodeAsync(
            (type == PurchaseInvoiceType.Cash && !isCheckPayment) ? AccountCash : AccountPayable);

        // ── D-03 (ق-2): منع الرصيد النقدي السالب افتراضيًا — تحقق خادمي ذرّي ──
        // يقفل صف حساب الصندوق (SELECT … FOR UPDATE) داخل نفس معاملة الفاتورة:
        //  • لا يتجاوز طلبان متزامنان الرصيد المتاح (القفل يُحرَّر بنهاية المعاملة).
        //  • عند الرفض تتراجع المعاملة كاملة ⇒ لا فاتورة ولا قيد ولا حركة مخزون جزئية.
        //  • لا توجد صلاحية تجاوز (ق-2). والشيك المؤجل لا يُعتبر نقدًا متاحًا (لا يمر من هنا).
        if (type == PurchaseInvoiceType.Cash && !isCheckPayment)
            await EnsureCashSufficientAsync(creditAccount.Id, invoice.TotalAmount, invoice.InvoiceNumber);

        var entry = await _journalService.PrepareEntryAsync(
            JournalEntryType.PurchaseInvoice,
            invoice.InvoiceDate,
            invoice.InvoiceNumber,
            $"فاتورة مشتريات {invoice.InvoiceNumber} - {supplier.NameAr}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = inventoryAccount.Id, DebitAmount = invoice.TotalAmount, Note = $"فاتورة {invoice.InvoiceNumber}" },
                new() { AccountId = creditAccount.Id, CreditAmount = invoice.TotalAmount }
            });

        invoice.JournalEntryId = entry.Id;

        // ── D-02 (ق-1): رصيد المورد = صافي الذمم غير المسدَّدة، لا إجمالي المشتريات ──
        // الشراء النقدي المدفوع بالكامل (PaidAmount = TotalAmount) لا يُنشئ أي ذمة مستحقة.
        // الشراء الآجل (PaidAmount = 0) يُثبت الالتزام كاملًا. الدفع الجزئي يترك الصافي.
        // مسار الشيك يبقى كما هو: قيد إصدار الشيك (مدين 2200) هو الذي يسوّي الالتزام،
        // فلا يجوز خصمه هنا مرة ثانية (وإلا انحرف الرصيد بالاتجاه المعاكس).
        // PU-04 (N-5): قفل صف المورد قبل التعديل — يمنع lost update بين فاتورتين متزامنتين.
        await LockSupplierAsync(supplier.Id);
        await _context.Entry(supplier).ReloadAsync();
        if (isCheckPayment)
            supplier.CurrentBalance = MoneyRounding.Round(supplier.CurrentBalance + invoice.TotalAmount);
        else
            supplier.CurrentBalance = MoneyRounding.Round(
                supplier.CurrentBalance + invoice.TotalAmount - invoice.PaidAmount);
        supplier.UpdatedAt = DateTime.UtcNow;

        // السداد بشيك: سجل الشيك (مصدر لمورد) + قيد إصدار يخفض التزام المورد فوراً
        // (مدين المورد 2200 / دائن «شيكات برسم السداد» 2300) — نفس المعاملة الذرية.
        if (isCheckPayment)
        {
            var checkDto = new CreateBankCheckDto
            {
                CheckNumber = dto.CheckNumber ?? string.Empty,
                BankName = dto.CheckBankName ?? string.Empty,
                BranchName = dto.CheckBranch,
                Direction = BankCheckDirection.IssuedToSupplier,
                SupplierId = supplier.Id,
                RelatedInvoiceId = invoice.Id,
                Amount = invoice.TotalAmount,
                IssueDate = dto.CheckIssueDate.HasValue ? dto.CheckIssueDate.Value : invoice.InvoiceDate,
                DueDate = dto.CheckDueDate.HasValue ? dto.CheckDueDate.Value : invoice.InvoiceDate.AddDays(30),
                ReceivedOrIssuedDate = invoice.InvoiceDate,
                Notes = dto.Note
            };
            var check = await BankCheckService.PrepareRegisteredCheckAsync(
                _context, _journalService, checkDto, null, supplier);
            _context.Set<BankCheck>().Add(check);
        }

        await _context.SaveChangesAsync();
        return MapToDto(invoice);
            }
            catch
            {
                // إصلاح R2/R5: تراجع المعاملة + تنظيف محدود للكيانات المعلّقة
                // (المستند/البنود/حركات المخزون/القيد/أرصدة الأطراف) فلا يُحفظ شيء جزئياً لاحقاً.
                DocumentFailureCleanup.DetachPendingDocuments(_context);
                throw;
            }
        });
        }
        catch (DbUpdateException ex) when (IsIdempotencyConflict(ex)
                                           && dto.IdempotencyKey is { } dupKey && dupKey != Guid.Empty)
        {
            // سباق متزامن على نفس المفتاح: أحد الطلبين فاز بالإدراج، والآخر رُفض بالفهرس الفريد.
            // المعاملة تراجعت كاملة ⇒ لا مستند ولا قيد ولا حركة جزئية. نُعيد النتيجة الأصلية.
            var winner = await _context.Set<PurchaseInvoice>()
                .AsNoTracking()
                .Include(i => i.Supplier).Include(i => i.Warehouse)
                .Include(i => i.Lines).ThenInclude(l => l.Item)
                .FirstOrDefaultAsync(i => i.IdempotencyKey == dupKey && !i.IsDeleted);
            if (winner is not null)
                return MapToDto(winner);
            throw;
        }
    }

    /// <summary>
    /// هل الاستثناء ناتج عن تصادم فهرس مفتاح التمييز (D-01)؟
    /// يُفحص نص الخطأ (بدون اعتماد مباشر على Npgsql في طبقة Application):
    /// 23505 = unique_violation، أو رسالة الفهرس IX_PurchaseInvoices_IdempotencyKey.
    /// </summary>
    private static bool IsIdempotencyConflict(DbUpdateException ex)
    {
        var msg = (ex.InnerException?.Message ?? string.Empty) + " " + ex.Message;
        return msg.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase)
               && (msg.Contains("23505", StringComparison.Ordinal)
                   || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// D-03: تحقق ذرّي من كفاية رصيد حساب الصندوق قبل الصرف النقدي.
    /// يقفل صف الحساب (SELECT … FOR UPDATE) حتى نهاية المعاملة ⇒ آمن ضد التزامن،
    /// ويرفض العملية برسالة واضحة عند عدم الكفاية (لا صلاحية تجاوز — ق-2).
    /// </summary>
    private async Task EnsureCashSufficientAsync(Guid accountId, decimal amount, string reference)
    {
        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = "SELECT \"CurrentBalance\" FROM \"Accounts\" WHERE \"Id\" = @id FOR UPDATE";
            var idParam = cmd.CreateParameter();
            idParam.ParameterName = "id";
            idParam.Value = accountId;
            cmd.Parameters.Add(idParam);

            var raw = await cmd.ExecuteScalarAsync();
            if (raw is null or DBNull)
                throw new InvalidOperationException(
                    $"حساب الصندوق غير موجود — تعذّر إتمام الصرف النقدي للفاتورة {reference}.");

            var balance = Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (balance - amount < 0m)
                throw new InvalidOperationException(
                    $"رصيد الصندوق غير كافٍ للفاتورة {reference}: المتاح {balance:N2} والمطلوب {amount:N2}. " +
                    "لا يُسمح برصيد نقدي سالب.");
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private Task<string> NextInvoiceNumberAsync()
        => NumberSequenceHelper.NextAsync(_context, "PI");

    /// <summary>
    /// يبحث عن الحساب النظامي بالكود ثم يحلّه ديناميكياً إلى الحساب المتحرك (Leaf) حسب بنية الشجرة،
    /// فلا يُرحَّل أبداً إلى حساب رئيسي: Production (1100 ورقة) → 1100،
    /// Commercial (1100 رئيسي) → الورقة الصحيحة تحته عبر PostingAccountResolver.
    /// </summary>
    /// <summary>
    /// PU-04 (N-5) — قفل صف المورد (FOR UPDATE) قبل أي تعديل على رصيده التشغيلي.
    /// </summary>
    private async Task LockSupplierAsync(Guid supplierId)
    {
        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = "SELECT \"CurrentBalance\" FROM \"Suppliers\" WHERE \"Id\" = @id FOR UPDATE";
            var p = cmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = supplierId;
            cmd.Parameters.Add(p);
            if (await cmd.ExecuteScalarAsync() is null or DBNull)
                throw new InvalidOperationException("المورد غير موجود — تعذّر قفل رصيده.");
        }
        finally
        {
            if (wasClosed && connection.State == System.Data.ConnectionState.Open) await connection.CloseAsync();
        }
    }

    private async Task<Account> GetAccountByCodeAsync(string code)
        => await PostingAccountResolver.ResolveAsync(_context, code);

    /// <summary>تحقق التزامن بعد استلام صنف TracksBatches: مجموع الدُفعات == رصيد حركات المخزون.</summary>
    private async Task AssertBatchesConsistentAfterReceiptAsync(Guid itemId, Guid warehouseId)
    {
        var batchTotal = await _context.Set<ItemBatch>()
            .Where(b => b.ItemId == itemId && b.WarehouseId == warehouseId)
            .SumAsync(b => (decimal?)b.Quantity) ?? 0m;
        var movementTotal = await _context.Set<StockMovement>()
            .Where(m => m.ItemId == itemId && m.WarehouseId == warehouseId)
            .SumAsync(m => (decimal?)m.Quantity) ?? 0m;
        if (batchTotal != movementTotal)
            throw new InvalidOperationException(
                $"تعارض تكامل دُفعات الاستلام: مجاميع الدُفعات ({batchTotal:N2}) ≠ حركات المخزون ({movementTotal:N2}).");
    }

    private static PurchaseInvoiceDto MapToDto(PurchaseInvoice invoice) => new()
    {
        Id = invoice.Id,
        InvoiceNumber = invoice.InvoiceNumber,
        SupplierId = invoice.SupplierId,
        SupplierCode = invoice.Supplier?.Code ?? "—",
        SupplierName = invoice.Supplier?.NameAr ?? "—",
        WarehouseId = invoice.WarehouseId,
        WarehouseName = invoice.Warehouse?.NameAr ?? "—",
        InvoiceDate = invoice.InvoiceDate,
        InvoiceType = (int)invoice.InvoiceType,
        Status = (int)invoice.Status,
        SubTotal = invoice.SubTotal,
        TaxAmount = invoice.TaxAmount,
        TaxRate = invoice.TaxRate,
        TotalAmount = invoice.TotalAmount,
        PaidAmount = invoice.PaidAmount,
        Note = invoice.Note,
        Lines = invoice.Lines.Select(l => new PurchaseInvoiceLineDto
        {
            Id = l.Id,
            ItemId = l.ItemId,
            ItemCode = l.Item?.Code ?? "—",
            ItemNameAr = l.Item?.NameAr ?? "—",
            Quantity = l.Quantity,
            UnitCost = l.UnitCost,
            LineTotal = l.LineTotal,
            TaxRate = l.TaxRate,
            TaxAmount = l.TaxAmount
        }).ToList()
    };
}