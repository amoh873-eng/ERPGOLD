using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace ERPSystem.Application.Services;

/// <summary>
/// سندات الصرف (Payment Vouchers) — المرحلة B (D1-A/D2-A/D3-A/D5-A).
/// نفس قواعد سند القبض حرفيًا (دورة الحياة، القيد المركزي، منع الترحيل المزدوج، سجل التدقيق،
/// الصلاحيات الخلفية)، والاختلاف في القيد عند الترحيل: مدين ذمم المورد (2200) — دائن الصندوق/البنك.
/// </summary>
public class PaymentVoucherService : IPaymentVoucherService
{
    private const string AccountCash = "1100";   // الصندوق/الخزنة الرئيسية
    private const string AccountBank = "1101";   // البنك
    private const string AccountPayable = "2200"; // الموردون/الدائنون
    private const string AccountReceivable = "1200"; // العملاء/المدينون (سند رد العميل)

    private readonly DbContext _context;
    private readonly IJournalEntryService _journalService;
    private readonly IVoucherAuthorization _authorization;

    public PaymentVoucherService(DbContext context, IJournalEntryService journalService, IVoucherAuthorization authorization)
    {
        _context = context;
        _journalService = journalService;
        _authorization = authorization;
    }

    public async Task<List<PaymentVoucherDto>> GetAsync(
        Guid? supplierId = null,
        int? status = null,
        DateTime? from = null,
        DateTime? to = null,
        string? search = null,
        Guid? customerId = null)
    {
        var query = QueryWithDetails();

        if (supplierId.HasValue)
            query = query.Where(v => v.SupplierId == supplierId.Value);

        if (customerId.HasValue)
            query = query.Where(v => v.CustomerId == customerId.Value);

        if (status.HasValue)
        {
            var s = (VoucherStatus)status.Value;
            query = query.Where(v => v.Status == s);
        }

        if (from.HasValue)
            query = query.Where(v => v.VoucherDate >= from.Value.Date);

        if (to.HasValue)
            query = query.Where(v => v.VoucherDate <= to.Value.Date);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(v =>
                v.VoucherNumber.Contains(term) ||
                (v.ExternalReference != null && v.ExternalReference.Contains(term)) ||
                (v.Description != null && v.Description.Contains(term)) ||
                (v.BeneficiaryName != null && v.BeneficiaryName.Contains(term)) ||
                (v.Supplier != null && (v.Supplier.NameAr.Contains(term) || v.Supplier.Code.Contains(term))) ||
                (v.Customer != null && (v.Customer.NameAr.Contains(term) || v.Customer.Code.Contains(term))) ||
                (v.SalesReturn != null && v.SalesReturn.ReturnNumber.Contains(term)));
        }

        var vouchers = await query
            .OrderByDescending(v => v.VoucherDate)
            .ThenByDescending(v => v.CreatedAt)
            .ToListAsync();

        return vouchers.Select(MapToDto).ToList();
    }

    public async Task<PaymentVoucherDto?> GetByIdAsync(Guid id)
    {
        var voucher = await QueryWithDetails().FirstOrDefaultAsync(v => v.Id == id);
        return voucher is null ? null : MapToDto(voucher);
    }

    public async Task<List<VoucherAuditDto>> GetAuditAsync(Guid id)
    {
        var entityName = VoucherAuditTrailWriter.EntityPayment;
        return await _context.Set<AuditTrail>()
            .Where(a => a.EntityName == entityName && a.EntityId == id)
            .OrderBy(a => a.PerformedAt)
            .Select(a => new VoucherAuditDto
            {
                PerformedAt = a.PerformedAt,
                Action = a.Action,
                PerformedBy = a.PerformedBy,
                Details = a.Details,
                JournalEntryId = a.JournalEntryId
            })
            .ToListAsync();
    }

    /// <summary>استعلام تفاصيل السند (طرف + حسابات + قيد) — مصدر واحد للعرض والترحيل.</summary>
    private IQueryable<PaymentVoucher> QueryWithDetails()
        => _context.Set<PaymentVoucher>()
            .Include(v => v.Supplier)
            .Include(v => v.Customer)
            .Include(v => v.SalesReturn)
            .Include(v => v.CashOrBankAccount)
            .Include(v => v.PayableAccount)
            .Include(v => v.JournalEntry);

    /// <summary>يفحص الصلاحية الخلفية ويُخفق برسالة صريحة عند غيابها (لا حماية بالواجهة فقط).</summary>
    private static void EnsureAllowed(bool allowed, string requiredPermission)
    {
        if (!allowed)
            throw new UnauthorizedAccessException(
                $"غير مصرّح بهذه العملية على سندات الصرف. الصلاحية المطلوبة: {requiredPermission}");
    }

    private string? CurrentUser() => _authorization.CurrentUserName();

    /// <summary>حفظ واحد ذرّي مع رسالة عربية واضحة عند تعارض التزامن (نمط ConcurrencyHelper).</summary>
    private Task SaveAsync() => ConcurrencyHelper.SaveChangesWithFriendlyErrorAsync(
        _context,
        "تعارض تزامني — قام مستخدم آخر بتعديل نفس السند. حدّث الصفحة وحاول مرة أخرى.");

    internal static string StatusName(VoucherStatus status) => ReceiptVoucherService.StatusName(status);

    private static string Label(Account? account)
        => account is null ? string.Empty : $"{account.Code} - {account.NameAr}";

    /// <summary>
    /// يحل حسابات السند بالكود النظامي عبر PostingAccountResolver (ورقة متحركة نشطة دائمًا)،
    /// ويتحقق أن الحسابين متحركان فعلًا ⇒ لا ترحيل على حساب تجميعي/جذري/غير نشط.
    /// </summary>
    private async Task<(Account CashOrBank, Account Payable)> ResolveAccountsAsync(int paymentMethod, bool customerParty = false)
    {
        var method = (PaymentMethod)paymentMethod;
        if (method != PaymentMethod.Cash && method != PaymentMethod.Bank)
            throw new InvalidOperationException("طريقة الصرف غير صالحة (نقدي أو بنكي فقط).");

        var cashOrBank = await PostingAccountResolver.ResolveAsync(
            _context, method == PaymentMethod.Cash ? AccountCash : AccountBank);

        // المورد ⇒ 2200 (دائنون) · العميل ⇒ 1200 (ذمم مدينة) — الحساب المقابل للطرف.
        var payable = await PostingAccountResolver.ResolveAsync(
            _context, customerParty ? AccountReceivable : AccountPayable);

        await MovementAccountValidator.EnsureMovableAccountAsync(_context, cashOrBank.Id);
        await MovementAccountValidator.EnsureMovableAccountAsync(_context, payable.Id);

        return (cashOrBank, payable);
    }

    private static void ValidateDraftInput(int paymentMethod, decimal amount, DateTime voucherDate)
    {
        var method = (PaymentMethod)paymentMethod;
        if (method != PaymentMethod.Cash && method != PaymentMethod.Bank)
            throw new InvalidOperationException("طريقة الصرف غير صالحة (نقدي أو بنكي فقط).");

        if (amount <= 0m)
            throw new InvalidOperationException("المبلغ يجب أن يكون أكبر من صفر.");

        if (!JournalEntryService.IsValidAccountingDate(voucherDate))
            throw new InvalidOperationException("تاريخ السند غير صالح.");
    }

    /// <summary>
    /// SUB-2b/P3 — غلاف التدقيق: أي رفض لطلب **رد عميل** (صلاحية/سقف/رصيد/مردود غير مؤهل)
    /// يُسجَّل في AuditTrails بفعل <c>RefundRejected</c> ثم يُعاد رمي الاستثناء نفسه.
    /// لا يُسجَّل شيء لسندات الموردين ولا للطلبات التي ليست رد عميل.
    /// </summary>
    public async Task<PaymentVoucherDto> CreateDraftAsync(CreatePaymentVoucherDto dto)
    {
        try
        {
            return await CreateDraftCoreAsync(dto);
        }
        catch (Exception ex) when (IsRefundRequest(dto.SupplierId, dto.CustomerId) && IsRefundRejection(ex))
        {
            await RefundAuditTrail.TryWriteRejectionAsync(
                _context, null, null, dto.CustomerId, dto.SalesReturnId, dto.Amount, ex.Message, CurrentUser());
            throw;
        }
    }

    private async Task<PaymentVoucherDto> CreateDraftCoreAsync(CreatePaymentVoucherDto dto)
    {
        // SUB-2b/P3: طرف واحد فقط (مورد أو عميل). فحص الطرف **قبل** أي قراءة/صلاحية أخرى.
        var isCustomerRefund = IsCustomerParty(dto.SupplierId, dto.CustomerId);

        if (isCustomerRefund)
            EnsureAllowed(_authorization.CanRefund(), VoucherRules.Permission(VoucherRules.RefundAction));
        else
            EnsureAllowed(_authorization.CanCreate(), VoucherRules.Permission(VoucherRules.AddAction));

        ValidateDraftInput(dto.PaymentMethod, dto.Amount, dto.VoucherDate);

        // PU-04 (N-1/R-2): إعادة إرسال الطلب نفسه (نفس المفتاح) تُعيد السند الأصلي بلا إنشاء ثانٍ.
        // ملاحظة: هذا يشمل حالة «انقطاع بعد نجاح الخادم» — المفتاح هو ما يمنع الازدواج.
        var createKey = NormalizeKey(dto.IdempotencyKey);
        if (createKey is { } alreadyKey)
        {
            var existing = await FindByIdempotencyKeyAsync(alreadyKey);
            if (existing is not null) return existing;
        }

        Supplier? supplier = null;
        Customer? customer = null;
        SalesReturn? salesReturn = null;

        if (isCustomerRefund)
        {
            if (dto.SalesReturnId is not { } returnId || returnId == Guid.Empty)
                throw new InvalidOperationException(
                    "سند رد العميل يتطلب تحديد المردود المرتبط (SalesReturnId) — لا يُسمح برد رصيد دائن بلا مستند مردود.");

            customer = await _context.Set<Customer>()
                .FirstOrDefaultAsync(c => c.Id == dto.CustomerId && !c.IsDeleted)
                ?? throw new InvalidOperationException("العميل غير موجود.");

            salesReturn = await LoadRefundableReturnAsync(returnId, customer.Id);
            await EnsureAdvisoryRefundWithinLimitsAsync(customer, salesReturn, dto.Amount);
        }
        else
        {
            supplier = await _context.Set<Supplier>()
                .FirstOrDefaultAsync(s => s.Id == dto.SupplierId && !s.IsDeleted)
                ?? throw new InvalidOperationException("المورد غير موجود.");
        }

        var accounts = await ResolveAccountsAsync(dto.PaymentMethod, isCustomerRefund);
        var user = CurrentUser();

        var voucher = new PaymentVoucher
        {
            Id = Guid.NewGuid(),
            VoucherNumber = await NumberSequenceHelper.NextAsync(_context, isCustomerRefund ? "CRF" : "PV"),
            VoucherDate = dto.VoucherDate,
            Status = VoucherStatus.Draft,
            SupplierId = isCustomerRefund ? null : supplier!.Id,
            CustomerId = isCustomerRefund ? customer!.Id : null,
            SalesReturnId = isCustomerRefund ? salesReturn!.Id : null,
            BeneficiaryName = string.IsNullOrWhiteSpace(dto.BeneficiaryName) && isCustomerRefund
                ? customer!.NameAr
                : dto.BeneficiaryName,
            PaymentMethod = (PaymentMethod)dto.PaymentMethod,
            CashOrBankAccountId = accounts.CashOrBank.Id,
            PayableAccountId = accounts.Payable.Id,
            Amount = Math.Round(dto.Amount, 2),
            Description = dto.Description,
            ExternalReference = dto.ExternalReference,
            AttachmentPath = dto.AttachmentPath,
            // PU-04 (N-1/R-2): يُخزَّن المفتاح مع السند ليُعيده أي طلب لاحق بنفس المفتاح.
            IdempotencyKey = createKey,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = user
        };

        _context.Set<PaymentVoucher>().Add(voucher);

        // سجل التدقيق (D3-A) يُضاف لنفس السياق ⇒ يُحفظ مع السند في SaveChanges واحد (ذرّي).
        VoucherAuditTrailWriter.Write(
            _context,
            VoucherAuditTrailWriter.EntityPayment,
            voucher.Id,
            voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionCreated,
            isCustomerRefund
                ? $"إنشاء مسودة رد مبلغ للعميل {customer!.NameAr} بمبلغ {voucher.Amount:0.00} مرتبط بالمردود {salesReturn!.ReturnNumber}"
                : $"إنشاء مسودة سند صرف بمبلغ {voucher.Amount:0.00} للمورد {supplier!.NameAr}",
            user);

        try
        {
            await SaveAsync();
        }
        catch (DbUpdateException ex) when (createKey is { } racedKey
                                           && UniqueViolation.IsOnIndex(ex, IndexVoucherIdempotencyKey))
        {
            // PU-04 (N-1/R-2): سباق متزامن على نفس المفتاح — أحد الطلبين فاز بالإدراج.
            // `SaveChanges` واحد ذرّي ⇒ لم يُحفظ سند ولا سجل تدقيق من هذا الطلب. نُعيد السند الفائز.
            DetachFailedVoucherWork();
            var winner = await FindByIdempotencyKeyAsync(racedKey);
            if (winner is not null) return winner;
            throw; // انتهاك على نفس الفهرس بلا سند مطابق ⇒ خطأ غير متوقع: لا يُخفى.
        }

        return await GetByIdAsync(voucher.Id) ?? MapToDto(voucher);
    }

    public async Task<PaymentVoucherDto> UpdateDraftAsync(Guid id, UpdatePaymentVoucherDto dto)
    {
        try
        {
            return await UpdateDraftCoreAsync(id, dto);
        }
        catch (Exception ex) when (IsRefundRejection(ex))
        {
            await TryAuditRefundRejectionForVoucherAsync(id, ex.Message);
            throw;
        }
    }

    private async Task<PaymentVoucherDto> UpdateDraftCoreAsync(Guid id, UpdatePaymentVoucherDto dto)
    {
        EnsureAllowed(_authorization.CanEdit(), VoucherRules.Permission(VoucherRules.EditAction));
        ValidateDraftInput(dto.PaymentMethod, dto.Amount, dto.VoucherDate);

        var voucher = await _context.Set<PaymentVoucher>().FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new InvalidOperationException("سند الصرف غير موجود.");

        if (voucher.Status != VoucherStatus.Draft)
            throw new InvalidOperationException(
                $"لا يُعدّل إلا سند مسودة (الحالة الحالية: {StatusName(voucher.Status)}).");

        Supplier? supplier = null;
        Customer? customer = null;
        SalesReturn? salesReturn = null;
        var isCustomerRefund = voucher.CustomerId is not null;

        if (isCustomerRefund)
        {
            // #20: تعديل رد عميل يتطلب صلاحية الرد صراحةً فوق صلاحية التعديل العامة.
            EnsureAllowed(_authorization.CanRefund(), VoucherRules.Permission(VoucherRules.RefundAction));

            if (voucher.SupplierId is not null)
                throw new InvalidOperationException("بيانات السند غير متسقة: طرفان في سند واحد.");

            // ثبات الطرف: لا تغيير للعميل ولا للمردود المرتبط بعد الإنشاء (يمنع تجاوز السقف).
            if (dto.CustomerId != voucher.CustomerId)
                throw new InvalidOperationException("لا يجوز تغيير عميل السند بعد إنشائه.");
            if (dto.SalesReturnId != voucher.SalesReturnId)
                throw new InvalidOperationException("لا يجوز تغيير المردود المرتبط بالسند بعد إنشائه.");

            customer = await _context.Set<Customer>()
                .FirstOrDefaultAsync(c => c.Id == voucher.CustomerId && !c.IsDeleted)
                ?? throw new InvalidOperationException("العميل غير موجود.");
            salesReturn = await LoadRefundableReturnAsync(voucher.SalesReturnId!.Value, customer.Id);
            await EnsureAdvisoryRefundWithinLimitsAsync(customer, salesReturn, dto.Amount, voucher.Id);
        }
        else
        {
            if (dto.CustomerId is not null)
                throw new InvalidOperationException("لا يجوز تحويل سند مورد إلى سند رد عميل بعد إنشائه.");
            if (dto.SalesReturnId is not null)
                throw new InvalidOperationException("المردود المرتبط لا يُستخدم في سندات الموردين.");

            supplier = await _context.Set<Supplier>()
                .FirstOrDefaultAsync(s => s.Id == dto.SupplierId && !s.IsDeleted)
                ?? throw new InvalidOperationException("المورد غير موجود.");
        }

        var accounts = await ResolveAccountsAsync(dto.PaymentMethod, isCustomerRefund);
        var user = CurrentUser();
        var before = $"{voucher.Amount:0.00} | {voucher.VoucherDate:yyyy-MM-dd} | {(int)voucher.PaymentMethod}";

        if (!isCustomerRefund)
            voucher.SupplierId = supplier!.Id;
        voucher.BeneficiaryName = string.IsNullOrWhiteSpace(dto.BeneficiaryName) && isCustomerRefund
            ? customer!.NameAr
            : dto.BeneficiaryName;
        voucher.VoucherDate = dto.VoucherDate;
        voucher.PaymentMethod = (PaymentMethod)dto.PaymentMethod;
        voucher.CashOrBankAccountId = accounts.CashOrBank.Id;
        voucher.PayableAccountId = accounts.Payable.Id;
        voucher.Amount = MoneyRounding.Round(dto.Amount);
        voucher.Description = dto.Description;
        voucher.ExternalReference = dto.ExternalReference;
        voucher.AttachmentPath = dto.AttachmentPath;
        voucher.UpdatedAt = DateTime.UtcNow;
        voucher.UpdatedBy = user;

        VoucherAuditTrailWriter.Write(
            _context,
            VoucherAuditTrailWriter.EntityPayment,
            voucher.Id,
            voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionUpdated,
            $"تعديل مسودة: {before} → {voucher.Amount:0.00} | {voucher.VoucherDate:yyyy-MM-dd} | {(int)voucher.PaymentMethod}",
            user);

        await SaveAsync();
        return await GetByIdAsync(voucher.Id) ?? MapToDto(voucher);
    }

    public async Task<PaymentVoucherDto> ApproveAsync(Guid id)
    {
        EnsureAllowed(_authorization.CanApprove(), VoucherRules.Permission(VoucherRules.ApproveAction));

        var voucher = await QueryWithDetails().FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new InvalidOperationException("سند الصرف غير موجود.");

        if (voucher.Status == VoucherStatus.Approved)
            return MapToDto(voucher);

        if (voucher.Status != VoucherStatus.Draft)
            throw new InvalidOperationException(
                $"لا يُعتمد إلا سند مسودة (الحالة الحالية: {StatusName(voucher.Status)}).");

        var user = CurrentUser();
        voucher.Status = VoucherStatus.Approved;
        voucher.ApprovedAt = DateTime.UtcNow;
        voucher.ApprovedBy = user;
        voucher.UpdatedAt = DateTime.UtcNow;
        voucher.UpdatedBy = user;

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityPayment, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionApproved,
            $"اعتماد سند صرف بمبلغ {voucher.Amount:0.00} (لا يُنشأ قيد إلا عند الترحيل)", user);

        await SaveAsync();
        return MapToDto(voucher);
    }

    public Task<PaymentVoucherDto> PostAsync(Guid id)
        // PU-03: معاملة صريحة ⇒ يبقى قفل الصف (FOR UPDATE) على المورد حتى نهاية الترحيل،
        // فلا يتجاوز سندان متزامنان المستحق القابل للسداد.
        => PostAsync(id, null, null);

    /// <summary>
    /// PU-04 — الترحيل مع نية التوزيع (D5-و/و-1): التخصيصات تُمرَّر مع طلب الترحيل فقط،
    /// ولا تُحفظ صفوف تخصيص قبل الترحيل. عند تمرير null/فارغ يُستخدم اقتراح FIFO الداخلي
    /// (مسار توافق للطلبات القديمة) ثم يُتحقق منه بنفس القواعد الصارمة.
    /// </summary>
    public async Task<PaymentVoucherDto> PostAsync(
        Guid id,
        IReadOnlyCollection<PaymentAllocationInput>? allocations,
        Guid? idempotencyKey = null)
    {
        try
        {
            return await DbTransactionHelper.ExecuteAsync(
                _context, () => PostCoreAsync(id, allocations, idempotencyKey));
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOnIndex(ex, IndexVoucherIdempotencyKey))
        {
            // PU-04 (P0-3): سباق متزامن على نفس مفتاح الترحيل. المعاملة تراجعت كاملة
            // (لا قيد ولا تخصيص ولا تغيير أرصدة). نُعيد نتيجة الطلب الأصلي **فقط** إذا كان
            // الفائز هو **نفس السند** ⇒ لا نُخفي تعارضًا يخصّ سندًا آخر.
            var key = NormalizeKey(idempotencyKey);
            if (key is null) throw;

            DetachFailedVoucherWork();
            var winner = await FindByIdempotencyKeyAsync(key.Value);
            if (winner is not null && winner.Id == id) return winner;

            // المفتاح مسجَّل على سند آخر ⇒ ليس «إعادة إرسال» بل استخدام خاطئ للمفتاح:
            // نرفض برسالة واضحة بدل إخفاء التعارض أو اعتباره نجاحًا.
            if (winner is not null)
                throw new InvalidOperationException(
                    $"مفتاح منع التكرار المُرسَل مسجَّل على سند آخر ({winner.VoucherNumber}) — " +
                    "لا يُعاد استخدام المفتاح لسند مختلف؛ أعد إرسال الطلب على السند الأصلي أو أنشئ طلبًا جديدًا.");

            throw; // انتهاك على نفس الفهرس بلا سند مطابق ⇒ خطأ غير متوقع: لا يُخفى.
        }
        catch (Exception ex) when (IsRefundRejection(ex))
        {
            // SUB-2b/P3 — تدقيق الرفض: السقف/الرصيد/الصلاحية. السند المُرحَّل مسبقًا لا يصل هنا
            // (يعود بنجاح من الأعلى) ⇒ لا سطر «مرفوض» لمحاولة إعادة إرسال ناجحة.
            await TryAuditRefundRejectionForVoucherAsync(id, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// يُسجّل رفضًا لسند **رد عميل** فقط (CustomerId ≠ null). سندات الموردين تُتجاهل بصمت.
    /// يُستخدم بعد Rollback المعاملة ⇒ لا أثر لسطر التدقيق في معاملة ملغاة.
    /// </summary>
    private async Task TryAuditRefundRejectionForVoucherAsync(Guid voucherId, string reason)
    {
        try
        {
            var info = await _context.Set<PaymentVoucher>().AsNoTracking()
                .Where(v => v.Id == voucherId && v.CustomerId != null)
                .Select(v => new { v.VoucherNumber, v.CustomerId, v.SalesReturnId, v.Amount })
                .FirstOrDefaultAsync();

            if (info is null) return;

            await RefundAuditTrail.TryWriteRejectionAsync(
                _context, voucherId, info.VoucherNumber, info.CustomerId, info.SalesReturnId,
                info.Amount, reason, CurrentUser());
        }
        catch
        {
            // أفضل جهد: فشل التدقيق لا يغيّر نتيجة الرفض.
        }
    }

    private static bool IsRefundRequest(Guid? supplierId, Guid? customerId)
    {
        var hasCustomer = customerId is { } c && c != Guid.Empty;
        var hasSupplier = supplierId is { } s && s != Guid.Empty;
        return hasCustomer && !hasSupplier;
    }

    /// <summary>أخطاء «الرفض» التي تستحق تدقيقًا: صلاحية مفقودة أو قاعدة عمل مرفوضة.</summary>
    private static bool IsRefundRejection(Exception ex)
        => ex is UnauthorizedAccessException or InvalidOperationException;

    private async Task<PaymentVoucherDto> PostCoreAsync(
        Guid id,
        IReadOnlyCollection<PaymentAllocationInput>? allocations,
        Guid? idempotencyKey)
    {
        EnsureAllowed(_authorization.CanPost(), VoucherRules.Permission(VoucherRules.PostAction));

        var voucher = await QueryWithDetails().FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new InvalidOperationException("سند الصرف غير موجود.");

        // منع الترحيل المزدوج (طبقة الخدمة): السند المُرحَّل يُعاد كما هو بلا قيد جديد.
        if (voucher.JournalEntryId is not null || voucher.Status == VoucherStatus.Posted)
            return MapToDto(voucher);

        if (voucher.Status == VoucherStatus.Cancelled)
            throw new InvalidOperationException("لا يُرحَّل سند ملغى.");

        if (voucher.Status != VoucherStatus.Approved)
            throw new InvalidOperationException(
                $"لا يُرحَّل إلا سند معتمد (الحالة الحالية: {StatusName(voucher.Status)}).");

        // ═══ إصلاح R3: حجز حق الترحيل بانتقال ذرّي مشروط — **قبل أي أثر مالي** ═══
        // (نفس مبدأ إصلاح سند القبض: قفل الصف وحده لم يمنع السباق فعليًا في هذا النمط.)
        var claimedForPost = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"PaymentVouchers\" SET \"Status\" = {(int)VoucherStatus.Posted} WHERE \"Id\" = {voucher.Id} AND \"Status\" = {(int)VoucherStatus.Approved} AND \"JournalEntryId\" IS NULL");

        if (claimedForPost == 0)
        {
            await _context.Entry(voucher).ReloadAsync();
            if (voucher.JournalEntryId is not null || voucher.Status == VoucherStatus.Posted)
                return MapToDto(voucher);   // ترحيل آخر سبقنا — لا نُنشئ قيدًا ثانيًا
            throw new InvalidOperationException("لا يُرحَّل سند ملغى.");
        }

        // الحسابات المحفوظة على السند حُدّدت عند الإنشاء؛ نتحقق مرة أخرى أنها متحركة قبل الترحيل.
        await MovementAccountValidator.EnsureMovableAccountAsync(_context, voucher.CashOrBankAccountId);
        await MovementAccountValidator.EnsureMovableAccountAsync(_context, voucher.PayableAccountId);

        // ═══ SUB-2b/P3 — بوابة الطرف: سند رد العميل مسار مستقل تمامًا (بلا تخصيصات مشتريات) ═══
        if (voucher.CustomerId is { } customerId)
            return await PostCustomerRefundCoreAsync(voucher, customerId, allocations, idempotencyKey);

        if (voucher.SupplierId is not { } supplierId)
            throw new InvalidOperationException("سند الصرف بلا طرف (مورد/عميل) — لا يمكن ترحيله.");

        // PU-03: منع السداد الزائد — المبلغ لا يتجاوز المستحق القابل للسداد للمورد.
        // PU-04 (D1): قفل المورد ثم التحقق من المستحق (حارس PU-03 لا يُضعَف).
        var outstanding = await LockSupplierAsync(supplierId, voucher.Supplier);
        if (voucher.Amount > outstanding)
            throw new InvalidOperationException(
                $"المبلغ المطلوب ({voucher.Amount:N2}) يتجاوز المستحق القابل للسداد للمورد ({outstanding:N2}). " +
                "لا يُسمح بالسداد الزائد كسداد عادي، والسلف غير مدعومة في هذا التصميم.");

        // PU-04 (D5-و): التخصيصات تُمرَّر مع طلب الترحيل؛ لا صفوف تخصيص تُحفظ قبل الترحيل.
        var residualQueries = new SupplierAllocationQueries(_context);
        var requested = NormalizeAllocations(allocations);
        if (requested.Count == 0)
            requested = await residualQueries.SuggestFifoAsync(
                supplierId, voucher.Amount, voucher.VoucherDate);

        if (requested.Count == 0)
            throw new InvalidOperationException(
                "لا توجد فواتير مشتريات مستحقة مؤهلة لهذا المورد. " +
                "لا يُسمح بترحيل سند دفع دون فواتير مؤهلة (D1)، ولا تُدعم السلف المستقلة.");

        // قفل صفوف الفواتير بترتيب حتمي (Id تصاعديًا) لمنع الجمود بين سندين متقاطعين.
        foreach (var invoiceId in requested.Select(a => a.PurchaseInvoiceId).Distinct().OrderBy(x => x))
            await LockInvoiceAsync(invoiceId);

        var rows = new List<PaymentAllocation>();
        var allocatedTotal = 0m;
        var allocatedPerInvoice = new Dictionary<Guid, decimal>();

        foreach (var item in requested.OrderBy(a => a.PurchaseInvoiceId))
        {
            var amount = MoneyRounding.Round(item.Amount);
            if (amount <= 0m)
                throw new InvalidOperationException("قيمة التخصيص يجب أن تكون أكبر من صفر.");

            var invoice = await _context.Set<PurchaseInvoice>()
                .FirstOrDefaultAsync(i => i.Id == item.PurchaseInvoiceId && !i.IsDeleted)
                ?? throw new InvalidOperationException("فاتورة المشتريات غير موجودة أو محذوفة.");

            if (invoice.SupplierId != supplierId)
                throw new InvalidOperationException(
                    $"الفاتورة {invoice.InvoiceNumber} لا تخص المورد نفسه — لا يُسمح بتخصيصها لهذا السند.");

            var residual = await residualQueries.GetResidualAsync(item.PurchaseInvoiceId)
                ?? throw new InvalidOperationException("تعذّر احتساب رصيد الفاتورة.");

            if (!residual.IsEligible)
                throw new InvalidOperationException(
                    $"الفاتورة {invoice.InvoiceNumber} غير مؤهلة للتخصيص: {residual.IneligibilityReason}");

            // D13-A: التحقق على المجموع التراكمي للفاتورة (يسمح بصفوف موجبة متعددة).
            allocatedPerInvoice.TryGetValue(invoice.Id, out var alreadyOnInvoice);
            if (MoneyRounding.Round(alreadyOnInvoice + amount) > residual.Available)
                throw new InvalidOperationException(
                    $"مجموع التخصيص ({MoneyRounding.Round(alreadyOnInvoice + amount):N2}) للفاتورة {invoice.InvoiceNumber} " +
                    $"يتجاوز الرصيد المتاح ({residual.Available:N2}).");
            allocatedPerInvoice[invoice.Id] = MoneyRounding.Round(alreadyOnInvoice + amount);

            rows.Add(new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                PaymentVoucherId = voucher.Id,
                PurchaseInvoiceId = invoice.Id,
                SupplierId = supplierId,
                Amount = amount,
                Direction = 1,
                AllocatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = CurrentUser()
            });

            allocatedTotal = MoneyRounding.Round(allocatedTotal + amount);
        }

        // D5: مساواة تامة بين مجموع التخصيصات ومبلغ السند (لا مبلغ غير موزع).
        if (allocatedTotal != voucher.Amount)
            throw new InvalidOperationException(
                $"مجموع التخصيصات ({allocatedTotal:N2}) لا يساوي مبلغ السند ({voucher.Amount:N2}). " +
                "لا يُسمح بترحيل سند يحتوي مبلغًا غير موزع.");

        // تثبيت مفتاح الترحيل (N-1) إن أُرسل — ويُرفض التبديل بمفتاح مختلف لنفس السند.
        if (idempotencyKey is { } postKey)
        {
            if (voucher.IdempotencyKey is { } existingKey && existingKey != postKey)
                throw new InvalidOperationException("مفتاح منع التكرار المرسل لا يطابق مفتاح السند المحفوظ.");
            voucher.IdempotencyKey ??= postKey;
        }

        // القيد المركزي: مدين ذمم المورد — دائن الصندوق/البنك
        var journal = await _journalService.PrepareEntryAsync(
            JournalEntryType.PaymentVoucher,
            voucher.VoucherDate,
            voucher.VoucherNumber,
            $"سند صرف {voucher.VoucherNumber} - {voucher.Supplier?.NameAr}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = voucher.PayableAccountId, DebitAmount = voucher.Amount },
                new() { AccountId = voucher.CashOrBankAccountId, CreditAmount = voucher.Amount }
            });

        var user = CurrentUser();
        voucher.JournalEntryId = journal.Id;
        voucher.Status = VoucherStatus.Posted;
        voucher.PostedAt = DateTime.UtcNow;
        voucher.PostedBy = user;
        voucher.UpdatedAt = DateTime.UtcNow;
        voucher.UpdatedBy = user;

        // إصلاح D-09-02 (تدقيق 2026-10-20): ترحيل السند يجب أن يخفّض رصيد المورد أيضًا.
        // كان القيد المحاسبي وحده يعكس السداد بينما Supplier.CurrentBalance يبقى كما هو،
        // فتتخلّف كشوف الموردين/أعمار الذمم عن دفتر الأستاذ. التحديث ضمن نفس وحدة العمل (حفظ واحد ذرّي).
        if (voucher.Supplier is not null)
        {
            // سجل التخصيص لا يُنشئ قيدًا محاسبيًا — القيد أعلاه هو الأثر المحاسبي الوحيد.
            _context.Set<PaymentAllocation>().AddRange(rows);
            voucher.Supplier.CurrentBalance = MoneyRounding.Round(voucher.Supplier.CurrentBalance - voucher.Amount);
            voucher.Supplier.UpdatedAt = DateTime.UtcNow;
        }

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityPayment, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionPosted,
            $"ترحيل سند صرف بمبلغ {voucher.Amount:0.00} وإنشاء القيد {journal.EntryNumber}",
            user, journal.Id);

        // حفظ واحد ذرّي: السند + القيد وبنوده + الأرصدة + سجل التدقيق
        await SaveAsync();
        return await GetByIdAsync(voucher.Id) ?? MapToDto(voucher);
    }

    /// <summary>
    /// PU-03: تحقق ذرّي من أن مبلغ السند لا يتجاوز المستحق القابل للسداد للمورد.
    /// يقفل صف المورد (SELECT … FOR UPDATE) داخل معاملة الترحيل ⇒ آمن ضد السداد المزدوج المتزامن.
    /// التصميم الحالي: السند **على مستوى المورد** (لا يوجد ربط بفاتورة) ⇒ المستحق = رصيد المورد الدائن.
    /// </summary>
    private async Task EnsureAmountWithinOutstandingAsync(PaymentVoucher voucher)
    {
        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = "SELECT \"CurrentBalance\" FROM \"Suppliers\" WHERE \"Id\" = @id FOR UPDATE";
            var idParam = cmd.CreateParameter();
            idParam.ParameterName = "id";
            idParam.Value = voucher.SupplierId;
            cmd.Parameters.Add(idParam);

            var raw = await cmd.ExecuteScalarAsync();
            if (raw is null or DBNull)
                throw new InvalidOperationException("المورد غير موجود — تعذّر التحقق من المستحق القابل للسداد.");

            var outstanding = Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (voucher.Amount > outstanding)
                throw new InvalidOperationException(
                    $"المبلغ المطلوب ({voucher.Amount:N2}) يتجاوز المستحق القابل للسداد للمورد ({outstanding:N2}). " +
                    "لا يُسمح بالسداد الزائد كسداد عادي، والسلف غير مدعومة في هذا التصميم.");
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// PU-04 — يقفل صف المورد (SELECT … FOR UPDATE) ويُعيد الرصيد الحالي بعد إعادة تحميل الكيان المتتبع،
    /// فلا يُبنى التحديث على قيمة قديمة خارج المعاملة.
    /// </summary>
    // ═══════════ SUB-2b/P3 — أدوات مسار رد العميل ═══════════

    /// <summary>يفرض طرفًا واحدًا فقط ويعيد true في وضع العميل. Guid.Empty تُعامَل كعدم تحديد.</summary>
    private static bool IsCustomerParty(Guid? supplierId, Guid? customerId)
    {
        var hasSupplier = supplierId is { } s && s != Guid.Empty;
        var hasCustomer = customerId is { } c && c != Guid.Empty;

        if (hasSupplier == hasCustomer)
            throw new InvalidOperationException(hasSupplier
                ? "لا يجوز تحديد مورد وعميل في السند نفسه — طرف واحد فقط."
                : "يجب تحديد طرف السند: مورد أو عميل.");

        return hasCustomer;
    }

    /// <summary>
    /// يقرأ المردود ويتحقق من أهليته للرد: مرحّل · للعميل نفسه · أصله مرحّل ·
    /// فاتورة آجلة أو شيك (مردود النقد/البطاقة عُوّض في وسيلة الدفع الأصلية).
    /// </summary>
    private async Task<SalesReturn> LoadRefundableReturnAsync(Guid returnId, Guid customerId)
    {
        var salesReturn = await _context.Set<SalesReturn>()
            .Include(r => r.SalesInvoice)
            .FirstOrDefaultAsync(r => r.Id == returnId && !r.IsDeleted)
            ?? throw new InvalidOperationException("المردود غير موجود أو محذوف.");

        if (salesReturn.CustomerId != customerId)
            throw new InvalidOperationException("المردود لا يخص العميل المحدد.");
        if (salesReturn.Status != DocumentStatus.Posted)
            throw new InvalidOperationException("لا يُرد إلا عن مردود مرحّل.");
        if (salesReturn.TotalAmount <= 0m)
            throw new InvalidOperationException("مبلغ المردود يجب أن يكون أكبر من صفر.");

        var invoice = salesReturn.SalesInvoice
            ?? throw new InvalidOperationException("فاتورة المردود الأصلية غير موجودة.");
        if (invoice.Status != DocumentStatus.Posted)
            throw new InvalidOperationException("فاتورة المردود الأصلية غير مرحّلة.");
        if (invoice.InvoiceType != SalesInvoiceType.OnAccount && invoice.PaymentMethod != SalesPaymentMethod.Check)
            throw new InvalidOperationException(
                "مردود النقد/البطاقة يُعوّض في وسيلة الدفع الأصلية ولا يُرد نقدًا/بنكيًا من جديد.");

        return salesReturn;
    }

    /// <summary>
    /// تحقق استشاري (بلا قفل) عند الإنشاء/التعديل: رصيد دائن + سقف المردود.
    /// الغرض تجربة استخدام أفضل فقط؛ الحارس الحاسم داخل معاملة الترحيل (منع TOCTOU).
    /// </summary>
    private async Task EnsureAdvisoryRefundWithinLimitsAsync(
        Customer customer, SalesReturn salesReturn, decimal amount, Guid? excludeVoucherId = null)
    {
        if (customer.CurrentBalance >= 0m)
            throw new InvalidOperationException("لا يوجد رصيد دائن للعميل — لا يجوز صرف رد.");

        var available = MoneyRounding.Round(-customer.CurrentBalance);
        if (amount > available)
            throw new InvalidOperationException(
                $"مبلغ الرد ({amount:N2}) يتجاوز الرصيد الدائن المتاح للعميل ({available:N2}).");

        var refunded = await SumPostedRefundsAsync(salesReturn.Id, excludeVoucherId);
        var remaining = MoneyRounding.Round(salesReturn.TotalAmount - refunded);
        if (amount > remaining)
            throw new InvalidOperationException(
                $"مبلغ الرد ({amount:N2}) يتجاوز المتبقي القابل للرد من المردود {salesReturn.ReturnNumber} ({remaining:N2}).");
    }

    /// <summary>Σ سندات الرد المرحّلة لمردود (المعكوس لا يُحتسب ⇒ يعود المبلغ قابلًا للرد).</summary>
    private async Task<decimal> SumPostedRefundsAsync(Guid salesReturnId, Guid? excludeVoucherId)
    {
        var total = await _context.Set<PaymentVoucher>()
            .Where(v => v.SalesReturnId == salesReturnId
                        && v.Status == VoucherStatus.Posted
                        && !v.IsDeleted
                        && (excludeVoucherId == null || v.Id != excludeVoucherId))
            .SumAsync(v => (decimal?)v.Amount);

        return MoneyRounding.Round(total ?? 0m);
    }

    /// <summary>يقفل صف المردود داخل المعاملة الحالية — يمنع سباق سندين على نفس السقف.</summary>
    private Task LockSalesReturnAsync(Guid salesReturnId)
        => _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"SalesReturns\" WHERE \"Id\" = {salesReturnId} FOR UPDATE");

    private async Task<decimal> LockSupplierAsync(Guid supplierId, Supplier? tracked)
    {
        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = "SELECT \"CurrentBalance\" FROM \"Suppliers\" WHERE \"Id\" = @id FOR UPDATE";
            var idParam = cmd.CreateParameter();
            idParam.ParameterName = "id";
            idParam.Value = supplierId;
            cmd.Parameters.Add(idParam);

            var raw = await cmd.ExecuteScalarAsync();
            if (raw is null or DBNull)
                throw new InvalidOperationException("المورد غير موجود — تعذّر التحقق من المستحق القابل للسداد.");

            if (tracked is not null)
                await _context.Entry(tracked).ReloadAsync();

            return Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open) await connection.CloseAsync();
        }
    }

    /// <summary>يقفل صف فاتورة داخل المعاملة الحالية — الترتيب التصاعدي بالمعرّف يمنع الجمود.</summary>
    private Task LockInvoiceAsync(Guid invoiceId)
        => _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PurchaseInvoices\" WHERE \"Id\" = {invoiceId} FOR UPDATE");

    /// <summary>
    /// يقفل صف السند داخل المعاملة الحالية — الفاصل الحاسم ضد الترحيل المزدوج المتزامن.
    /// (نفس نمط <see cref="LockInvoiceAsync"/> وقفل المورد المستخدم في هذا المسار.)
    /// </summary>
    private Task LockVoucherAsync(Guid voucherId)
        => VoucherRowLock.LockPaymentVoucherAsync(_context, voucherId);

    /// <summary>
    /// D13-A المعتمد: يُسمح بصفوف تخصيص موجبة متعددة لنفس الزوج (سند، فاتورة)،
    /// والتحقق يجري على المجموع التراكمي لكل فاتورة (لا تجميع صامت للمدخلات).
    /// </summary>
    private static List<PaymentAllocationInput> NormalizeAllocations(
        IReadOnlyCollection<PaymentAllocationInput>? allocations)
        => allocations is null || allocations.Count == 0
            ? new List<PaymentAllocationInput>()
            : allocations.ToList();

    /// <summary>
    /// إلغاء سند الصرف — **داخل معاملة صريحة مع قفل صف السند** (إصلاح 2026-10-23، نفس مبدأ
    /// الترحيل): القرار يُبنى على حالة أعيدت قراءتها بعد القفل، فلا يكتب الإلغاء فوق ترحيل ملتزم.
    /// </summary>
    public Task<PaymentVoucherDto> CancelAsync(Guid id, string? reason)
        => DbTransactionHelper.ExecuteAsync(_context, () => CancelCoreAsync(id, reason));

    private async Task<PaymentVoucherDto> CancelCoreAsync(Guid id, string? reason)
    {
        EnsureAllowed(_authorization.CanCancel(), VoucherRules.Permission(VoucherRules.CancelAction));

        var voucher = await QueryWithDetails().FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new InvalidOperationException("سند الصرف غير موجود.");

        // ═══ إصلاح R3: انتقال ذرّي مشروط — لا تُكتب «ملغى» إلا إذا كانت الحالة ما زالت «معتمد» وبلا قيد ═══
        var claimed = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"PaymentVouchers\" SET \"Status\" = {(int)VoucherStatus.Cancelled} WHERE \"Id\" = {voucher.Id} AND \"Status\" <> {(int)VoucherStatus.Posted} AND \"Status\" <> {(int)VoucherStatus.Cancelled} AND \"JournalEntryId\" IS NULL");

        if (claimed == 0)
        {
            await _context.Entry(voucher).ReloadAsync();
            if (voucher.Status == VoucherStatus.Cancelled)
                return MapToDto(voucher);
            throw new InvalidOperationException(
                "لا يُلغى سند مُرحَّل — الأثر المحاسبي محفوظ. استخدم قيدًا معاكسًا للمعالجة.");
        }

        if (voucher.Status == VoucherStatus.Cancelled)
            return MapToDto(voucher);

        if (voucher.Status == VoucherStatus.Posted || voucher.JournalEntryId is not null)
            throw new InvalidOperationException(
                "لا يُلغى سند مُرحَّل — الأثر المحاسبي محفوظ. استخدم قيدًا معاكسًا للمعالجة.");

        var user = CurrentUser();
        voucher.Status = VoucherStatus.Cancelled;
        voucher.CancelledAt = DateTime.UtcNow;
        voucher.CancelledBy = user;
        voucher.CancelReason = reason;
        voucher.UpdatedAt = DateTime.UtcNow;
        voucher.UpdatedBy = user;

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityPayment, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionCancelled,
            string.IsNullOrWhiteSpace(reason) ? "إلغاء السند قبل الترحيل (لا حذف)" : $"إلغاء السند: {reason}",
            user);

        await SaveAsync();
        return MapToDto(voucher);
    }

    /// <summary>
    /// SUB-2b/P3 — ترحيل سند رد عميل داخل معاملة الترحيل نفسها:
    ///  • بلا تخصيصات فواتير مشتريات إطلاقًا.
    ///  • قفل العميل ثم المردود (الترتيب: سند ← طرف ← مستند).
    ///  • سقف مزدوج: الرصيد الدائن (−CurrentBalance) والمتبقي من المردود (TotalAmount − Σ الردود المرحّلة).
    ///  • قيد واحد: Dr 1200 / Cr 1100|1101 بنوع CustomerRefund، وتحديث رصيد العميل **مرة واحدة**.
    /// </summary>
    private async Task<PaymentVoucherDto> PostCustomerRefundCoreAsync(
        PaymentVoucher voucher,
        Guid customerId,
        IReadOnlyCollection<PaymentAllocationInput>? allocations,
        Guid? idempotencyKey)
    {
        if (allocations is { Count: > 0 })
            throw new InvalidOperationException("سند رد العميل لا يقبل تخصيصات فواتير مشتريات.");

        var customer = await _context.Set<Customer>()
            .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted)
            ?? throw new InvalidOperationException("العميل غير موجود — تعذّر التحقق من الرصيد الدائن.");

        await PartyRowLock.LockCustomerAsync(_context, customerId);
        await _context.Entry(customer).ReloadAsync();

        if (customer.CurrentBalance >= 0m)
            throw new InvalidOperationException("لا يوجد رصيد دائن للعميل — لا يجوز صرف رد.");

        var available = MoneyRounding.Round(-customer.CurrentBalance);
        if (voucher.Amount > available)
            throw new InvalidOperationException(
                $"مبلغ الرد ({voucher.Amount:N2}) يتجاوز الرصيد الدائن المتاح للعميل ({available:N2}).");

        if (voucher.SalesReturnId is not { } returnId)
            throw new InvalidOperationException("سند رد العميل بلا مردود مرجعي — لا يمكن ترحيله.");

        await LockSalesReturnAsync(returnId);
        var salesReturn = await LoadRefundableReturnAsync(returnId, customerId);
        var refunded = await SumPostedRefundsAsync(returnId, excludeVoucherId: voucher.Id);
        var remaining = MoneyRounding.Round(salesReturn.TotalAmount - refunded);
        if (voucher.Amount > remaining)
            throw new InvalidOperationException(
                $"مبلغ الرد ({voucher.Amount:N2}) يتجاوز المتبقي القابل للرد من المردود {salesReturn.ReturnNumber} ({remaining:N2}).");

        // مفتاح الترحيل (N-1): يُثبَّت أو يُرفض التبديل — نفس منطق مسار المورد.
        if (idempotencyKey is { } postKey)
        {
            if (voucher.IdempotencyKey is { } existingKey && existingKey != postKey)
                throw new InvalidOperationException("مفتاح منع التكرار المرسل لا يطابق مفتاح السند المحفوظ.");
            voucher.IdempotencyKey ??= postKey;
        }

        var journal = await _journalService.PrepareEntryAsync(
            JournalEntryType.CustomerRefund,
            voucher.VoucherDate,
            voucher.VoucherNumber,
            $"رد مبلغ للعميل {customer.NameAr} - مردود {salesReturn.ReturnNumber}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = voucher.PayableAccountId, DebitAmount = voucher.Amount, Note = "رد مبلغ عميل" },
                new() { AccountId = voucher.CashOrBankAccountId, CreditAmount = voucher.Amount, Note = "رد مبلغ عميل" }
            });

        var user = CurrentUser();
        voucher.JournalEntryId = journal.Id;
        voucher.Status = VoucherStatus.Posted;
        voucher.PostedAt = DateTime.UtcNow;
        voucher.PostedBy = user;
        voucher.UpdatedAt = DateTime.UtcNow;
        voucher.UpdatedBy = user;

        // الرصيد مرة واحدة فقط: إرجاع المستحق (الرصيد الدائن يقل). لا صفوف تخصيص ولا رصيد مورد.
        customer.CurrentBalance = MoneyRounding.Round(customer.CurrentBalance + voucher.Amount);
        customer.UpdatedAt = DateTime.UtcNow;

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityPayment, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionPosted,
            $"ترحيل رد مبلغ للعميل {customer.NameAr} بمبلغ {voucher.Amount:0.00} مرتبط بالمردود {salesReturn.ReturnNumber} وإنشاء القيد {journal.EntryNumber}",
            user, journal.Id);

        await SaveAsync();
        return await GetByIdAsync(voucher.Id) ?? MapToDto(voucher);
    }

    internal static PaymentVoucherDto MapToDto(PaymentVoucher v) => new()
    {
        Id = v.Id,
        VoucherNumber = v.VoucherNumber,
        VoucherDate = v.VoucherDate,
        Status = (int)v.Status,
        StatusName = StatusName(v.Status),
        SupplierId = v.SupplierId,
        SupplierCode = v.Supplier?.Code ?? string.Empty,
        SupplierName = v.Supplier?.NameAr ?? string.Empty,
        CustomerId = v.CustomerId,
        CustomerCode = v.Customer?.Code,
        CustomerName = v.Customer?.NameAr,
        SalesReturnId = v.SalesReturnId,
        SalesReturnNumber = v.SalesReturn?.ReturnNumber,
        PartyType = v.CustomerId is not null ? "Customer" : "Supplier",
        PartyLabel = v.CustomerId is not null
            ? (v.Customer?.NameAr ?? string.Empty)
            : (v.Supplier?.NameAr ?? string.Empty),
        BeneficiaryName = v.BeneficiaryName,
        PaymentMethod = (int)v.PaymentMethod,
        CashOrBankAccountId = v.CashOrBankAccountId,
        CashOrBankAccountLabel = Label(v.CashOrBankAccount),
        PayableAccountId = v.PayableAccountId,
        PayableAccountLabel = Label(v.PayableAccount),
        Amount = v.Amount,
        Description = v.Description,
        ExternalReference = v.ExternalReference,
        AttachmentPath = v.AttachmentPath,
        JournalEntryId = v.JournalEntryId,
        JournalEntryNumber = v.JournalEntry?.EntryNumber,
        ApprovedAt = v.ApprovedAt,
        ApprovedBy = v.ApprovedBy,
        PostedAt = v.PostedAt,
        PostedBy = v.PostedBy,
        CancelledAt = v.CancelledAt,
        CancelledBy = v.CancelledBy,
        CancelReason = v.CancelReason,
        CreatedAt = v.CreatedAt
    };

    // ═══════════ PU-04 (N-1 / P0-3) — منع تكرار الإنشاء والترحيل ومعالجة تعارض الفهرس الفريد ═══════════

    /// <summary>اسم الفهرس الفريد الجزئي لمفتاح السند — يُطابَق داخل نص خطأ PostgreSQL (`23505`).</summary>
    private const string IndexVoucherIdempotencyKey = "UX_PaymentVouchers_IdempotencyKey";

    /// <summary>`Guid.Empty` تُعامَل كعدم إرسال مفتاح (توافق خلفي مع الطلبات القديمة).</summary>
    private static Guid? NormalizeKey(Guid? key)
        => key is { } k && k != Guid.Empty ? k : null;

    /// <summary>
    /// يقرأ السند المحفوظ بمفتاح التكرار (بلا تتبع) — وهو **مصدر نتيجة «إعادة الطلب نفسه»**
    /// في حالتي التسلسل (قبل الإدراج) والسباق المتزامن (بعد `23505`).
    /// </summary>
    private async Task<PaymentVoucherDto?> FindByIdempotencyKeyAsync(Guid key)
    {
        var existing = await _context.Set<PaymentVoucher>()
            .AsNoTracking()
            .Include(v => v.Supplier)
            .Include(v => v.Customer)
            .Include(v => v.SalesReturn)
            .Include(v => v.CashOrBankAccount)
            .Include(v => v.PayableAccount)
            .Include(v => v.JournalEntry)
            .FirstOrDefaultAsync(v => v.IdempotencyKey == key);

        return existing is null ? null : MapToDto(existing);
    }

    /// <summary>
    /// تفريغ آثار محاولة فاشلة من السياق (لا تفريغ شامل):
    ///  1) يمنع حاجز «لا إعادة محاولة صامتة» من رفض العملية التالية في نفس الجلسة (Blazor Server).
    ///  2) يمنع حفظ كيانات معلّقة في `SaveChanges` لاحق (لا سند/تخصيص/قيد يتيم).
    /// يُعاد استخدام نمط <see cref="DocumentFailureCleanup"/> القائم، ويُستكمَل بأنواع PU-04.
    /// </summary>
    private void DetachFailedVoucherWork()
    {
        DocumentFailureCleanup.DetachPendingDocuments(_context);

        foreach (var entry in _context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is PaymentVoucher or PaymentAllocation or PaymentVoucherReversal or AuditTrail)
                entry.State = EntityState.Detached;
        }
    }
}


