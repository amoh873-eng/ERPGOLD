using System.Data;
using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERPSystem.Application.Services;

/// <summary>
/// خدمة الأرصدة الافتتاحية الرسمية (Opening Balance).
///
/// المبادئ المطبَّقة (وفق القرار المعتمد):
///  - لا تجاوز للخدمة المركزية: كل قيد يُبنى عبر <see cref="IJournalEntryService.PrepareEntryAsync"/>
///    (تحقق التاريخ + التوازن + قاعدة الحسابات المتحركة + الترقيم الآمن + التحديث الذرّي للأرصدة).
///  - لا كتابة مباشرة على Account.CurrentBalance إطلاقاً من هذه الخدمة.
///  - لا افتراض لحساب مقابل: البنود صريحة، وإن لم تتوازن تُرفض مع بيان الفرق.
///  - الحسابات المتحركة (Leaf) فقط: رقم غير موجود أو حساب رئيسي ⇒ رفض صريح.
///  - ذرية كاملة: الدفعة + القيد + بنوده + تحديث الأرصدة في معاملة واحدة (DbTransactionHelper).
///  - التصحيح بعد الترحيل بقيد عكسي مرتبط بالأصل (لا تعديل ولا حذف).
/// </summary>
public class OpeningBalanceService : IOpeningBalanceService
{
    private readonly DbContext _context;
    private readonly IJournalEntryService _journalService;
    private readonly IOpeningBalanceAuthorization? _authorization;

    public OpeningBalanceService(
        DbContext context,
        IJournalEntryService journalService,
        IOpeningBalanceAuthorization? authorization = null)
    {
        _context = context;
        _journalService = journalService;
        _authorization = authorization;
    }

    // ─────────────────────────── الإنشاء والتعديل والحذف (Draft) ───────────────────────────

    public async Task<OpeningBalanceBatchDto> CreateDraftAsync(CreateOpeningBalanceBatchDto dto, string userId)
    {
        EnsureAllowed(_authorization?.CanCreate() == true, OpeningBalanceRules.Permission(OpeningBalanceRules.AddAction));
        EnsureValidPeriod(dto.FiscalYear, dto.EntryDate);

        var resolved = await ResolveAndValidateLinesAsync(dto.Lines);

        // منع التكرار المسبق (الحماية النهائية هي الفهرس الفريد الجزئي على مستوى قاعدة البيانات).
        if (await HasActiveBatchAsync(dto.FiscalYear))
            throw new InvalidOperationException(
                $"توجد دفعة أرصدة افتتاحية فعّالة للسنة المالية {dto.FiscalYear} بالفعل. " +
                "لا يُسمح بأكثر من دفعة واحدة لكل سنة مالية (عدّل المسودة القائمة أو احذفها، أو اعكس المُرحَّلة).");

        var batch = new OpeningBalanceBatch
        {
            Id = Guid.NewGuid(),
            FiscalYear = dto.FiscalYear,
            EntryDate = dto.EntryDate,
            Status = OpeningBalanceBatchStatus.Draft,
            Reference = dto.Reference,
            Description = dto.Description,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = userId
        };

        foreach (var line in resolved)
            batch.Lines.Add(NewLine(batch.Id, line, userId));

        _context.Set<OpeningBalanceBatch>().Add(batch);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // سباق إنشاء متزامن: الفهرس الفريد رفض الدفعة الثانية → رسالة واضحة بدل خطأ قاعدة بيانات.
            _context.ChangeTracker.Clear();
            throw new InvalidOperationException(
                $"توجد دفعة أرصدة افتتاحية فعّالة للسنة المالية {dto.FiscalYear} بالفعل (منع تكرار متزامن).");
        }

        return await LoadRequiredAsync(batch.Id);
    }

    public async Task<OpeningBalanceBatchDto> UpdateDraftAsync(Guid batchId, UpdateOpeningBalanceBatchDto dto, string userId)
    {
        EnsureAllowed(_authorization?.CanUpdate() == true, OpeningBalanceRules.Permission(OpeningBalanceRules.EditAction));

        var batch = await LoadTrackedAsync(batchId);
        EnsureDraft(batch, "تعديل");

        EnsureValidPeriod(batch.FiscalYear, dto.EntryDate);
        var resolved = await ResolveAndValidateLinesAsync(dto.Lines);

        batch.EntryDate = dto.EntryDate;
        batch.Reference = dto.Reference;
        batch.Description = dto.Description;
        batch.UpdatedAt = DateTime.UtcNow;
        batch.UpdatedBy = userId;

        // استبدال البنود بالكامل (المسودة فقط).
        foreach (var existing in batch.Lines.ToList())
            _context.Set<OpeningBalanceLine>().Remove(existing);
        batch.Lines.Clear();

        foreach (var line in resolved)
            batch.Lines.Add(NewLine(batch.Id, line, userId));

        await _context.SaveChangesAsync();
        return await LoadRequiredAsync(batch.Id);
    }

    public async Task DeleteDraftAsync(Guid batchId, string userId)
    {
        EnsureAllowed(_authorization?.CanDelete() == true, OpeningBalanceRules.Permission(OpeningBalanceRules.DeleteAction));

        var batch = await LoadTrackedAsync(batchId);
        EnsureDraft(batch, "حذف");

        // حذف ناعم (يبقى للأثر التدقيقي) — والفهرس الفريد يستثني المحذوف.
        batch.IsDeleted = true;
        batch.UpdatedAt = DateTime.UtcNow;
        batch.UpdatedBy = userId;

        foreach (var line in batch.Lines)
        {
            line.IsDeleted = true;
            line.UpdatedAt = DateTime.UtcNow;
            line.UpdatedBy = userId;
        }

        await _context.SaveChangesAsync();
    }

    // ─────────────────────────── الترحيل (Draft → Posted) ───────────────────────────

    public async Task<OpeningBalanceBatchDto> PostAsync(Guid batchId, string userId)
    {
        EnsureAllowed(_authorization?.CanPost() == true, OpeningBalanceRules.Permission(OpeningBalanceRules.EditAction));

        return await DbTransactionHelper.ExecuteAsync(_context, async () =>
        {
            // قفل صف الدفعة: ترحيلان متزامنان لنفس المسودة يُنتجان قيداً واحداً فقط.
            await LockBatchRowAsync(batchId);

            var batch = await LoadTrackedAsync(batchId);
            EnsureDraft(batch, "ترحيل");
            EnsureValidPeriod(batch.FiscalYear, batch.EntryDate);

            if (batch.ReversalOfBatchId is null && await HasActiveBatchAsync(batch.FiscalYear, batch.Id))
                throw new InvalidOperationException(
                    $"توجد دفعة أرصدة افتتاحية فعّالة أخرى للسنة المالية {batch.FiscalYear}.");

            var legs = await BuildPostingLegsAsync(batch);

            // القيد يُبنى عبر الخدمة المركزية فقط: توازن + حسابات متحركة + ترقيم + تحديث ذرّي للأرصدة.
            var entry = await _journalService.PrepareEntryAsync(
                JournalEntryType.OpeningBalance,
                batch.EntryDate,
                batch.Reference ?? $"OPEN-{batch.FiscalYear}",
                batch.Description ?? $"أرصدة افتتاحية للسنة المالية {batch.FiscalYear}",
                legs);

            // ── آثار التفصيل (طرف/صنف) بعد تجهيز القيد المركزي وداخل نفس المعاملة ──
            //  • بنود العملاء/الموردين ⇒ تحديث الرصيد التفصيلي (قفل الصف + قراءة طازجة) فيبقى
            //    الرقابي 1200/2200 مساويًا لمجموع الأرصدة التفصيلية **بالبناء** لا بالتسوية.
            //  • بنود المخزون ⇒ حركة مخزون افتتاحية (MovementType.OpeningBalance) + تحديث ذاكرة
            //    الصنف (CurrentStock ومتوسط التكلفة المرجّح) بنفس منطق حركات المخزون الرسمية.
            await ApplyDetailEffectsAsync(batch);

            batch.JournalEntryId = entry.Id;
            batch.Status = OpeningBalanceBatchStatus.Posted;
            batch.PostedBy = userId;
            batch.PostedAt = DateTime.UtcNow;
            batch.UpdatedAt = DateTime.UtcNow;
            batch.UpdatedBy = userId;

            // حفظ ذري واحد: الدفعة (Posted) + القيد + بنوده + الأرصدة.
            await _context.SaveChangesAsync();
            return await LoadRequiredAsync(batch.Id);
        });
    }

    // ─────────────────────────── العكس (Posted → Reversed) ───────────────────────────

    public async Task<OpeningBalanceBatchDto> ReverseAsync(Guid batchId, DateTime entryDate, string userId)
    {
        EnsureAllowed(_authorization?.CanReverse() == true, OpeningBalanceRules.Permission(OpeningBalanceRules.EditAction));

        return await DbTransactionHelper.ExecuteAsync(_context, async () =>
        {
            await LockBatchRowAsync(batchId);

            var original = await LoadTrackedAsync(batchId);
            if (original.Status != OpeningBalanceBatchStatus.Posted)
                throw new InvalidOperationException("لا يمكن عكس إلا دفعة أرصدة افتتاحية مُرحَّلة.");
            if (original.ReversedByBatchId is not null)
                throw new InvalidOperationException("هذه الدفعة معكوسة مسبقاً.");

            EnsureValidPeriod(original.FiscalYear, entryDate);

            // البنود المعاكسة (مدين ↔ دائن) من الأصل — نفس الحسابات المتحركة **ونفس إسناد الطرف**.
            // إصلاح 2026-10-25: بنود العكس كانت تُبنى بلا إسناد طرف، فيُصنَّف أثر العكس خطأً
            // «غير مُنسَب» في تقرير المطابقة (ويظهر «فرق غير مُفسَّر» وهمي). الآن الإسناد يُنقل
            // كما هو ⇒ العكس يبقى مطابقًا للتفصيلي ولا يُنشئ أي فرق ولا أي حركة «غير مُنسَبة».
            var legs = original.Lines
                .Select(l => new JournalEntryLegInput
                {
                    AccountId = l.AccountId,
                    DebitAmount = l.CreditAmount,
                    CreditAmount = l.DebitAmount,
                    Note = l.Note,
                    CustomerId = l.CustomerId,
                    SupplierId = l.SupplierId
                })
                .ToList();

            var entry = await _journalService.PrepareEntryAsync(
                JournalEntryType.OpeningBalance,
                entryDate,
                $"OPEN-REV-{original.FiscalYear}",
                $"عكس دفعة الأرصدة الافتتاحية للسنة المالية {original.FiscalYear}",
                legs);

            var reversal = new OpeningBalanceBatch
            {
                Id = Guid.NewGuid(),
                FiscalYear = original.FiscalYear,
                EntryDate = entryDate,
                Status = OpeningBalanceBatchStatus.Posted,
                Reference = $"OPEN-REV-{original.FiscalYear}",
                Description = $"عكس دفعة الأرصدة الافتتاحية للسنة المالية {original.FiscalYear}",
                ReversalOfBatchId = original.Id,
                JournalEntryId = entry.Id,
                PostedBy = userId,
                PostedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = userId
            };

            // بنود العكس تحمل نفس التفصيل (طرف/صنف) مع تبادل مدين/دائن — ليكون العكس متماثلًا وتدقيقيًا.
            foreach (var l in original.Lines)
                reversal.Lines.Add(new OpeningBalanceLine
                {
                    Id = Guid.NewGuid(),
                    BatchId = reversal.Id,
                    AccountId = l.AccountId,
                    DebitAmount = l.CreditAmount,
                    CreditAmount = l.DebitAmount,
                    CustomerId = l.CustomerId,
                    SupplierId = l.SupplierId,
                    ItemId = l.ItemId,
                    WarehouseId = l.WarehouseId,
                    Quantity = l.Quantity,
                    UnitCost = l.UnitCost,
                    Note = l.Note,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = userId
                });

            // آثار التفصيل العكسية: إعادة أرصدة الأطراف إلى ما قبل الدفعة، وإخراج كميات المخزون
            // بحركة صادرة (AdjustmentOut) مع التحقق من كفاية المتاح (فشل مغلق برسالة واضحة).
            await ReverseDetailEffectsAsync(original, entryDate);

            _context.Set<OpeningBalanceBatch>().Add(reversal);

            original.Status = OpeningBalanceBatchStatus.Reversed;
            original.ReversedByBatchId = reversal.Id;
            original.UpdatedAt = DateTime.UtcNow;
            original.UpdatedBy = userId;

            await _context.SaveChangesAsync();
            return await LoadRequiredAsync(reversal.Id);
        });
    }


    // ─────────────────────────── الاستعلام ───────────────────────────

    public async Task<List<OpeningBalanceBatchDto>> GetAllAsync()
    {
        var batches = await _context.Set<OpeningBalanceBatch>()
            .AsNoTracking()
            .Include(b => b.JournalEntry)
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Include(b => b.Lines).ThenInclude(l => l.Customer)
            .Include(b => b.Lines).ThenInclude(l => l.Supplier)
            .Include(b => b.Lines).ThenInclude(l => l.Item)
            .Include(b => b.Lines).ThenInclude(l => l.Warehouse)
            .OrderByDescending(b => b.FiscalYear)
            .ThenByDescending(b => b.CreatedAt)
            .ToListAsync();

        return batches.Select(MapToDto).ToList();
    }

    public async Task<OpeningBalanceBatchDto?> GetByIdAsync(Guid id) => await LoadDtoAsync(id);

    public async Task<OpeningBalanceBatchDto?> GetActiveForYearAsync(int fiscalYear)
    {
        var batch = await _context.Set<OpeningBalanceBatch>()
            .AsNoTracking()
            .Include(b => b.JournalEntry)
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Include(b => b.Lines).ThenInclude(l => l.Customer)
            .Include(b => b.Lines).ThenInclude(l => l.Supplier)
            .Include(b => b.Lines).ThenInclude(l => l.Item)
            .Include(b => b.Lines).ThenInclude(l => l.Warehouse)
            .FirstOrDefaultAsync(b => b.FiscalYear == fiscalYear
                                      && b.ReversalOfBatchId == null
                                      && b.Status != OpeningBalanceBatchStatus.Reversed);

        return batch is null ? null : MapToDto(batch);
    }

    // ─────────────────────────── مساعدات ───────────────────────────

    private static void EnsureAllowed(bool allowed, string permission)
    {
        if (!allowed)
            throw new UnauthorizedAccessException(
                $"غير مصرّح بترحيل الأرصدة الافتتاحية. الصلاحية المطلوبة: {permission}");
    }

    // ─────────────────────────── آثار التفصيل عند الترحيل/العكس ───────────────────────────

    /// <summary>
    /// يُطبّق آثار بنود التفصيل عند الترحيل (داخل معاملة الترحيل نفسها):
    ///  • العميل: `CurrentBalance += (مدين − دائن)` (طبيعته مدينة).
    ///  • المورد: `CurrentBalance += (دائن − مدين)` (طبيعته دائنة).
    ///  • الصنف: حركة مخزون افتتاحية موجبة + تحديث الرصيد ومتوسط التكلفة المرجّح.
    /// كل رصيد طرف يُكتب بعد قفل صفه وقراءته طازجًا (PartyRowLock + Reload) ⇒ لا فقدان تحديث.
    /// </summary>
    private async Task ApplyDetailEffectsAsync(OpeningBalanceBatch batch)
    {
        foreach (var line in batch.Lines.OrderBy(l => l.CreatedAt))
        {
            // مزامنة الرصيد التفصيلي للطرف — مصدر واحد مشترك (PartyBalanceSync).
            if (line.CustomerId is Guid customerId)
                await PartyBalanceSync.ApplyCustomerDeltaAsync(_context, customerId,
                    PartyBalanceSync.CustomerDelta(line.DebitAmount, line.CreditAmount));

            if (line.SupplierId is Guid supplierId)
                await PartyBalanceSync.ApplySupplierDeltaAsync(_context, supplierId,
                    PartyBalanceSync.SupplierDelta(line.DebitAmount, line.CreditAmount));

            if (line.ItemId is Guid itemId && line.WarehouseId is Guid warehouseId
                && line.Quantity is decimal quantity && quantity > 0m)
            {
                await StockAvailabilityHelper.LockAndRefreshItemAsync(_context, itemId);
                var item = await _context.Set<Item>().FirstAsync(i => i.Id == itemId);
                var unitCost = line.UnitCost ?? item.CostPrice;

                if (unitCost > 0m)
                    item.CostPrice = InventoryValuation.ComputeWeightedAverage(
                        item.CurrentStock, item.CostPrice, quantity, unitCost);

                item.CurrentStock += quantity;
                item.UpdatedAt = DateTime.UtcNow;

                var movement = new StockMovement
                {
                    Id = Guid.NewGuid(),
                    ItemId = itemId,
                    WarehouseId = warehouseId,
                    MovementType = MovementType.OpeningBalance,
                    Quantity = quantity,
                    UnitCost = unitCost,
                    ReferenceNumber = batch.Reference ?? $"OPEN-{batch.FiscalYear}",
                    Note = $"رصيد افتتاحي للصنف {item.Code}",
                    MovementDate = batch.EntryDate,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Set<StockMovement>().Add(movement);
                line.StockMovementId = movement.Id;
            }
        }
    }

    private static void EnsureValidPeriod(int fiscalYear, DateTime entryDate)
    {
        if (!JournalEntryService.IsValidAccountingDate(entryDate))
            throw new InvalidOperationException(
                "تاريخ الدفعة الافتتاحية غير صالح. لا يمكن استخدام تاريخ غير محدد أو خارج النطاق المسموح (1900-2100).");

        if (fiscalYear < 1900 || fiscalYear > 2100)
            throw new InvalidOperationException(
                $"السنة المالية غير صالحة ({fiscalYear}). النطاق المسموح (1900-2100).");

        if (entryDate.Year != fiscalYear)
            throw new InvalidOperationException(
                $"تاريخ الترحيل ({entryDate:yyyy-MM-dd}) خارج السنة المالية ({fiscalYear}). يجب أن يقع التاريخ داخل السنة المالية نفسها.");
    }

    private static void EnsureDraft(OpeningBalanceBatch batch, string operation)
    {
        if (batch.Status != OpeningBalanceBatchStatus.Draft)
            throw new InvalidOperationException(
                $"لا يمكن {operation} دفعة أرصدة افتتاحية بحالة «{StatusNameAr((int)batch.Status)}». " +
                "المسودات فقط قابلة للتعديل/الحذف/الترحيل، والتصحيح بعد الترحيل يكون بقيد عكسي.");
    }

    private static OpeningBalanceLine NewLine(Guid batchId, ResolvedLine line, string userId) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = batchId,
        AccountId = line.Account.Id,
        DebitAmount = line.Debit,
        CreditAmount = line.Credit,
        CustomerId = line.Customer?.Id,
        SupplierId = line.Supplier?.Id,
        ItemId = line.Item?.Id,
        WarehouseId = line.Warehouse?.Id,
        Quantity = line.Quantity,
        UnitCost = line.UnitCost,
        Note = line.Note,
        CreatedAt = DateTime.UtcNow,
        CreatedBy = userId
    };

    /// <summary>
    /// بند مُحلَّل ومُتحقَّق منه: حساب متحرك + مبالغ + تفصيل (طرف/صنف) عند لزومه.
    /// التفصيل محفوظ ككيانات مُتتبَّعة لأن بعضها يُقرأ من الذاكرة عند الترحيل (اسم الصنف مثلًا).
    /// </summary>
    private sealed record ResolvedLine(
        Account Account,
        decimal Debit,
        decimal Credit,
        string? Note,
        Customer? Customer,
        Supplier? Supplier,
        Item? Item,
        Warehouse? Warehouse,
        decimal? Quantity,
        decimal? UnitCost);

    private async Task<OpeningBalanceBatch> LoadTrackedAsync(Guid id)
        => await _context.Set<OpeningBalanceBatch>()
               .Include(b => b.Lines)
               .FirstOrDefaultAsync(b => b.Id == id)
           ?? throw new InvalidOperationException("دفعة الأرصدة الافتتاحية غير موجودة.");

    private async Task<OpeningBalanceBatchDto> LoadRequiredAsync(Guid id)
        => await LoadDtoAsync(id) ?? throw new InvalidOperationException("دفعة الأرصدة الافتتاحية غير موجودة.");

    private async Task<OpeningBalanceBatchDto?> LoadDtoAsync(Guid id)
    {
        var batch = await _context.Set<OpeningBalanceBatch>()
            .AsNoTracking()
            .Include(b => b.JournalEntry)
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Include(b => b.Lines).ThenInclude(l => l.Customer)
            .Include(b => b.Lines).ThenInclude(l => l.Supplier)
            .Include(b => b.Lines).ThenInclude(l => l.Item)
            .Include(b => b.Lines).ThenInclude(l => l.Warehouse)
            .FirstOrDefaultAsync(b => b.Id == id);

        return batch is null ? null : MapToDto(batch);
    }

    private async Task<bool> HasActiveBatchAsync(int fiscalYear, Guid? excludeBatchId = null)
        => await _context.Set<OpeningBalanceBatch>()
            .AnyAsync(b => b.FiscalYear == fiscalYear
                           && b.ReversalOfBatchId == null
                           && b.Status != OpeningBalanceBatchStatus.Reversed
                           && (excludeBatchId == null || b.Id != excludeBatchId));


    private async Task<List<ResolvedLine>> ResolveAndValidateLinesAsync(
        IReadOnlyCollection<OpeningBalanceLineInput>? lines)
    {
        if (lines is null || lines.Count == 0)
            throw new InvalidOperationException("لا يمكن إنشاء دفعة افتتاحية بدون بنود.");

        var cache = new Dictionary<string, Account>(StringComparer.Ordinal);
        var resolved = new List<ResolvedLine>();

        foreach (var line in lines)
        {
            var code = line.AccountCode?.Trim() ?? string.Empty;
            if (code.Length == 0)
                throw new InvalidOperationException("رقم الحساب مطلوب في كل بند.");

            if (line.DebitAmount < 0m || line.CreditAmount < 0m)
                throw new InvalidOperationException($"لا يمكن أن تكون مبالغ البند على الحساب '{code}' سالبة.");
            if (line.DebitAmount > 0m && line.CreditAmount > 0m)
                throw new InvalidOperationException($"البند على الحساب '{code}' لا يمكن أن يكون مديناً ودائناً معاً.");
            // استثناء مقصود: بند الصنف قد يُدخل بالكمية والتكلفة فقط فيُشتق مبلغه (ResolveInventoryDetailAsync).
            if (line.DebitAmount == 0m && line.CreditAmount == 0m && string.IsNullOrWhiteSpace(line.ItemCode))
                throw new InvalidOperationException($"البند على الحساب '{code}' بلا مبلغ (مدين ودائن = صفر).");

            if (!cache.TryGetValue(code, out var account))
            {
                account = await _context.Set<Account>()
                    .FirstOrDefaultAsync(a => a.Code == code && !a.IsDeleted)
                    ?? throw new InvalidOperationException($"الحساب '{code}' غير موجود في شجرة الحسابات.");
                cache[code] = account;
            }

            // رفض الحساب غير المتحرك: لا جذري/تصنيفي (بلا أب) ولا رئيسي تجميعي (له أبناء).
            // لا هبوط تلقائي ولا افتراض لحساب بديل — البند يُرفض صريحًا.
            await MovementAccountValidator.EnsureMovableAccountAsync(_context, account.Id);

            var detail = await ResolveDetailAsync(account, line, code);

            resolved.Add(new ResolvedLine(
                account,
                detail.Debit,
                detail.Credit,
                line.Note,
                detail.Customer,
                detail.Supplier,
                detail.Item,
                detail.Warehouse,
                detail.Quantity,
                detail.UnitCost));
        }

        EnsureNoDuplicateDetail(resolved);

        var totalDebit = resolved.Sum(r => r.Debit);
        var totalCredit = resolved.Sum(r => r.Credit);
        if (totalDebit != totalCredit)
            throw new InvalidOperationException(
                $"القيد الافتتاحي غير متوازن: مجموع المدين ({totalDebit:N2}) ≠ مجموع الدائن ({totalCredit:N2}) — " +
                $"الفرق ({Math.Abs(totalDebit - totalCredit):N2}). لا يمكن الترحيل.");

        return resolved;
    }

    // ─────────────────────────── تفصيل الرصيد (طرف/صنف) ───────────────────────────

    /// <summary>حساب العملاء الرقابي — بنوده تُلزم بتحديد العميل.</summary>
    private const string ReceivableAccount = "1200";

    /// <summary>حساب الموردين الرقابي — بنوده تُلزم بتحديد المورد.</summary>
    private const string PayableAccount = "2200";

    /// <summary>حساب المخزون — بنوده تُلزم بالصنف والمخزن والكمية والتكلفة.</summary>
    private const string InventoryAccount = "1300";

    /// <summary>
    /// يُحلّل تفصيل البند ويتحقق من قواعده:
    ///  • 1200 ⇒ عميل إلزامي · 2200 ⇒ مورد إلزامي · 1300 ⇒ صنف + مخزن + كمية + تكلفة،
    ///    وقيمة البند = الكمية × التكلفة (تُقارن بالمبلغ المُدخل ولا تُستبدل صامتة).
    ///  • أي تفصيل على حساب لا يخصّه ⇒ رفض صريح.
    /// القاعدة موجودة لمنع عودة الفجوة: رصيد رقابي بلا سجل تفصيلي ⇒ رقابي ≠ مجموع الأطراف.
    /// </summary>
    private async Task<(decimal Debit, decimal Credit, Customer? Customer, Supplier? Supplier,
        Item? Item, Warehouse? Warehouse, decimal? Quantity, decimal? UnitCost)> ResolveDetailAsync(
        Account account, OpeningBalanceLineInput line, string code)
    {
        var isReceivable = string.Equals(account.Code, ReceivableAccount, StringComparison.Ordinal);
        var isPayable = string.Equals(account.Code, PayableAccount, StringComparison.Ordinal);
        var isInventory = string.Equals(account.Code, InventoryAccount, StringComparison.Ordinal);

        var customerCode = CleanCode(line.CustomerCode);
        var supplierCode = CleanCode(line.SupplierCode);
        var itemCode = CleanCode(line.ItemCode);
        var warehouseCode = CleanCode(line.WarehouseCode);

        if (!isReceivable && customerCode is not null)
            throw new InvalidOperationException(
                $"البند على الحساب '{code}' لا يقبل تحديد عميل — تحديد العميل مقبول على حساب العملاء ({ReceivableAccount}) فقط.");
        if (!isPayable && supplierCode is not null)
            throw new InvalidOperationException(
                $"البند على الحساب '{code}' لا يقبل تحديد مورد — تحديد المورد مقبول على حساب الموردين ({PayableAccount}) فقط.");
        if (!isInventory && (itemCode is not null || warehouseCode is not null || line.Quantity.HasValue || line.UnitCost.HasValue))
            throw new InvalidOperationException(
                $"البند على الحساب '{code}' لا يقبل تفصيل صنف/كمية/تكلفة — ذلك مقبول على حساب المخزون ({InventoryAccount}) فقط.");

        if (isReceivable && customerCode is null)
            throw new InvalidOperationException(
                $"بنود حساب العملاء ({ReceivableAccount}) تلزم بتحديد «كود العميل» — " +
                "بدون ذلك يبقى الرصيد الرقابي بلا سجل تفصيلي ولا يمكن مطابقته.");
        if (isPayable && supplierCode is null)
            throw new InvalidOperationException(
                $"بنود حساب الموردين ({PayableAccount}) تلزم بتحديد «كود المورد» — " +
                "بدون ذلك يبقى الرصيد الرقابي بلا سجل تفصيلي ولا يمكن مطابقته.");

        Customer? customer = null;
        Supplier? supplier = null;
        Item? item = null;
        Warehouse? warehouse = null;

        if (customerCode is not null)
        {
            customer = await _context.Set<Customer>()
                .FirstOrDefaultAsync(c => c.Code == customerCode && !c.IsDeleted)
                ?? throw new InvalidOperationException($"العميل '{customerCode}' غير موجود في سجل العملاء.");
        }

        if (supplierCode is not null)
        {
            supplier = await _context.Set<Supplier>()
                .FirstOrDefaultAsync(s => s.Code == supplierCode && !s.IsDeleted)
                ?? throw new InvalidOperationException($"المورد '{supplierCode}' غير موجود في سجل الموردين.");
        }

        if (isInventory)
            return await ResolveInventoryDetailAsync(line, itemCode!, warehouseCode, code);

        return (line.DebitAmount, line.CreditAmount, customer, supplier, null, null, null, null);
    }

    private static string? CleanCode(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// بند المخزون: يُلزم بالصنف والمخزن والكمية والتكلفة، وتُشتق قيمته = الكمية × التكلفة.
    /// القيمة **لا تُستبدل صامتة**: إن أدخل المستخدم مبلغًا مخالفًا يُرفض البند ببيان الفرق.
    /// </summary>
    private async Task<(decimal Debit, decimal Credit, Customer? Customer, Supplier? Supplier,
        Item? Item, Warehouse? Warehouse, decimal? Quantity, decimal? UnitCost)> ResolveInventoryDetailAsync(
        OpeningBalanceLineInput line, string itemCode, string? warehouseCode, string accountCode)
    {
        if (warehouseCode is null)
            throw new InvalidOperationException(
                $"بند الصنف '{itemCode}' يلزم بتحديد «كود المخزن» — الكمية تُقيَّد في مخزن محدَّد.");

        var item = await _context.Set<Item>()
            .FirstOrDefaultAsync(i => i.Code == itemCode && !i.IsDeleted)
            ?? throw new InvalidOperationException($"الصنف '{itemCode}' غير موجود في سجل الأصناف.");

        var warehouse = await _context.Set<Warehouse>()
            .FirstOrDefaultAsync(w => w.Code == warehouseCode && !w.IsDeleted)
            ?? throw new InvalidOperationException($"المخزن '{warehouseCode}' غير موجود في سجل المخازن.");

        if (line.Quantity is not decimal quantity || quantity <= 0m)
            throw new InvalidOperationException($"بند الصنف '{itemCode}' يلزم بكمية افتتاحية موجبة أكبر من صفر.");
        if (line.UnitCost is not decimal unitCost || unitCost < 0m)
            throw new InvalidOperationException($"بند الصنف '{itemCode}' يلزم بتكلفة وحدة افتتاحية غير سالبة.");

        var value = MoneyRounding.Round(quantity * unitCost);
        var debit = MoneyRounding.Round(line.DebitAmount);
        var credit = MoneyRounding.Round(line.CreditAmount);

        // تسهيلًا للواجهة: إدخال الكمية والتكلفة فقط كافٍ لاشتقاق قيمة البند.
        if (debit == 0m && credit == 0m)
            debit = value;

        if (credit > 0m)
            throw new InvalidOperationException(
                $"بند الصنف '{itemCode}' على الحساب '{accountCode}' يجب أن يكون **مدينًا** بقيمته (زيادة مخزون). " +
                "الرصيد الافتتاحي السالب للمخزون يحتاج قرارًا ماليًا ومسارًا مستقلًا.");

        if (debit != value)
            throw new InvalidOperationException(
                $"قيمة بند الصنف '{itemCode}' ({debit:N2}) لا تساوي الكمية × التكلفة ({value:N2}). " +
                "صحّح القيمة أو الكمية/التكلفة — لا يُستبدل المبلغ صامتًا.");

        return (value, 0m, null, null, item, warehouse, quantity, unitCost);
    }

    /// <summary>يمنع تكرار التفصيل داخل الدفعة (سطران لنفس العميل/المورد/الصنف+المخزن).</summary>
    private static void EnsureNoDuplicateDetail(List<ResolvedLine> lines)
    {
        var seenCustomers = new HashSet<Guid>();
        var seenSuppliers = new HashSet<Guid>();
        var seenItems = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (line.Customer is not null && !seenCustomers.Add(line.Customer.Id))
                throw new InvalidOperationException(
                    $"العميل '{line.Customer.Code}' مُدخل في أكثر من بند داخل الدفعة. " +
                    "ادمجه في بند واحد — التكرار يُنتج قيدًا مضاعفًا على الرصيد الافتتاحي.");

            if (line.Supplier is not null && !seenSuppliers.Add(line.Supplier.Id))
                throw new InvalidOperationException(
                    $"المورد '{line.Supplier.Code}' مُدخل في أكثر من بند داخل الدفعة. " +
                    "ادمجه في بند واحد — التكرار يُنتج قيدًا مضاعفًا على الرصيد الافتتاحي.");

            if (line.Item is not null && line.Warehouse is not null)
            {
                var key = line.Item.Id.ToString("N") + ":" + line.Warehouse.Id.ToString("N");
                if (!seenItems.Add(key))
                    throw new InvalidOperationException(
                        $"الصنف '{line.Item.Code}' مُدخل مرتين لنفس المخزن داخل الدفعة. " +
                        "ادمجه في بند واحد — التكرار يُنتج حركتي مخزون مضاعفتين.");
            }
        }
    }

    private async Task<List<JournalEntryLegInput>> BuildPostingLegsAsync(OpeningBalanceBatch batch)
    {
        if (batch.Lines.Count == 0)
            throw new InvalidOperationException("لا يمكن ترحيل دفعة افتتاحية بدون بنود.");

        var legs = new List<JournalEntryLegInput>();
        foreach (var line in batch.Lines)
        {
            if (line.DebitAmount < 0m || line.CreditAmount < 0m)
                throw new InvalidOperationException("لا يمكن أن تكون مبالغ البنود سالبة.");
            if (line.DebitAmount > 0m && line.CreditAmount > 0m)
                throw new InvalidOperationException("لا يمكن أن يكون البند مديناً ودائناً معاً.");
            if (line.DebitAmount == 0m && line.CreditAmount == 0m)
                throw new InvalidOperationException("بند بلا مبلغ (مدين ودائن = صفر).");

            legs.Add(new JournalEntryLegInput
            {
                AccountId = line.AccountId,
                DebitAmount = line.DebitAmount,
                CreditAmount = line.CreditAmount,
                Note = line.Note,
                // إسناد الطرف يُنقل إلى بند القيد ليكون أثرًا تدقيقيًا دائمًا (ومصدرًا لحساب
                // «غير المُنسَب» في تقرير المطابقة). مزامنة الرصيد التفصيلي تتم في ApplyDetailEffectsAsync
                // ولا تتكرر هنا (PrepareEntryAsync لا يُزامن الأطراف).
                CustomerId = line.CustomerId,
                SupplierId = line.SupplierId
            });
        }

        var totalDebit = legs.Sum(l => l.DebitAmount);
        var totalCredit = legs.Sum(l => l.CreditAmount);
        if (totalDebit != totalCredit)
            throw new InvalidOperationException(
                $"القيد الافتتاحي غير متوازن: مجموع المدين ({totalDebit:N2}) ≠ مجموع الدائن ({totalCredit:N2}) — " +
                $"الفرق ({Math.Abs(totalDebit - totalCredit):N2}). لا يمكن الترحيل.");

        await MovementAccountValidator.EnsureMovementAccountsAsync(
            _context, legs.Select(l => l.AccountId).Distinct().ToList());

        return legs;
    }

    /// <summary>
    /// يعكس آثار التفصيل لدفعة مُرحَّلة: يُنقص أرصدة الأطراف بنفس مقاديرها الأصلية، ويُخرج
    /// كميات المخزون بحركة صادرة (AdjustmentOut) بعد التحقق من كفاية المتاح — فلا يبقى أثر مخزني
    /// أو طرفي معلّقًا بعد العكس، ولا يُلمس أي قيد تاريخي (العكس قيد جديد مربوط بالأصل).
    /// </summary>
    private async Task ReverseDetailEffectsAsync(OpeningBalanceBatch original, DateTime reversalEntryDate)
    {
        foreach (var line in original.Lines.OrderBy(l => l.CreatedAt))
        {
            // العكس: نُطبّق الدلتا نفسها بإشارة معاكسة عبر المصدر المشترك نفسه.
            if (line.CustomerId is Guid customerId)
                await PartyBalanceSync.ApplyCustomerDeltaAsync(_context, customerId,
                    -PartyBalanceSync.CustomerDelta(line.DebitAmount, line.CreditAmount));

            if (line.SupplierId is Guid supplierId)
                await PartyBalanceSync.ApplySupplierDeltaAsync(_context, supplierId,
                    -PartyBalanceSync.SupplierDelta(line.DebitAmount, line.CreditAmount));

            if (line.ItemId is Guid itemId && line.WarehouseId is Guid warehouseId
                && line.Quantity is decimal quantity && quantity > 0m)
            {
                // فشل مغلق: لا يُخرج مخزونًا غير متاح (سبق بيعه) — الرسالة تُبيّن المتاح والمطلوب.
                await StockAvailabilityHelper.EnsureEnoughStockAsync(_context, itemId, warehouseId, quantity);

                var item = await _context.Set<Item>().FirstAsync(i => i.Id == itemId);
                item.CurrentStock -= quantity;
                item.UpdatedAt = DateTime.UtcNow;

                _context.Set<StockMovement>().Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    ItemId = itemId,
                    WarehouseId = warehouseId,
                    MovementType = MovementType.AdjustmentOut,
                    Quantity = -quantity,
                    UnitCost = line.UnitCost ?? item.CostPrice,
                    ReferenceNumber = $"OPEN-REV-{original.FiscalYear}",
                    Note = "عكس رصيد افتتاحي مخزني",
                    MovementDate = reversalEntryDate,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
    }

    /// <summary>يقفل صف الدفعة (FOR UPDATE) داخل المعاملة الجارية — يمنع الترحيل/العكس المتزامن المزدوج.</summary>
    private async Task LockBatchRowAsync(Guid batchId)
    {
        if (!_context.Database.IsRelational())
            return;

        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "SELECT 1 FROM \"OpeningBalanceBatches\" WHERE \"Id\" = @id FOR UPDATE";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = batchId;
            command.Parameters.Add(parameter);

            await command.ExecuteScalarAsync();
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    /// <summary>يكشف انتهاك القيد الفريد (PostgreSQL 23505) دون اعتمادية على مزوّد قاعدة البيانات.</summary>
    private static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().FullName != "Npgsql.PostgresException")
                continue;

            if (e.GetType().GetProperty("SqlState")?.GetValue(e) is string sqlState && sqlState == "23505")
                return true;
        }

        return false;
    }

    private static OpeningBalanceBatchDto MapToDto(OpeningBalanceBatch b)
    {
        var dto = new OpeningBalanceBatchDto
        {
            Id = b.Id,
            FiscalYear = b.FiscalYear,
            Status = (int)b.Status,
            StatusNameAr = StatusNameAr((int)b.Status),
            EntryDate = b.EntryDate,
            Reference = b.Reference,
            Description = b.Description,
            JournalEntryId = b.JournalEntryId,
            EntryNumber = b.JournalEntry?.EntryNumber,
            ReversalOfBatchId = b.ReversalOfBatchId,
            ReversedByBatchId = b.ReversedByBatchId,
            CreatedBy = b.CreatedBy,
            CreatedAt = b.CreatedAt,
            PostedBy = b.PostedBy,
            PostedAt = b.PostedAt,
            Lines = b.Lines
                .OrderBy(l => l.CreatedAt)
                .Select(l => new OpeningBalanceLineDto
                {
                    AccountId = l.AccountId,
                    AccountCode = l.Account?.Code ?? "—",
                    AccountNameAr = l.Account?.NameAr ?? "—",
                    DebitAmount = l.DebitAmount,
                    CreditAmount = l.CreditAmount,
                    Note = l.Note,
                    DetailKindAr = l.Item is not null ? "صنف مخزني"
                        : l.Customer is not null ? "عميل"
                        : l.Supplier is not null ? "مورد"
                        : "حساب",
                    CustomerId = l.CustomerId,
                    CustomerCode = l.Customer?.Code,
                    CustomerNameAr = l.Customer?.NameAr,
                    SupplierId = l.SupplierId,
                    SupplierCode = l.Supplier?.Code,
                    SupplierNameAr = l.Supplier?.NameAr,
                    ItemId = l.ItemId,
                    ItemCode = l.Item?.Code,
                    ItemNameAr = l.Item?.NameAr,
                    WarehouseId = l.WarehouseId,
                    WarehouseNameAr = l.Warehouse?.NameAr,
                    Quantity = l.Quantity,
                    UnitCost = l.UnitCost,
                    Value = MoneyRounding.Round(l.DebitAmount - l.CreditAmount),
                    HasStockMovement = l.StockMovementId is not null
                })
                .ToList()
        };

        dto.TotalDebit = dto.Lines.Sum(l => l.DebitAmount);
        dto.TotalCredit = dto.Lines.Sum(l => l.CreditAmount);
        return dto;
    }

    private static string StatusNameAr(int status) => status switch
    {
        (int)OpeningBalanceBatchStatus.Draft => "مسودة",
        (int)OpeningBalanceBatchStatus.Posted => "مُرحَّلة",
        (int)OpeningBalanceBatchStatus.Reversed => "معكوسة",
        _ => "غير معروفة"
    };
}

