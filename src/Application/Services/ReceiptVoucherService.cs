using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// سندات القبض (Receipt Vouchers) — المرحلة B (D1-A/D2-A/D3-A/D5-A).
///
/// القواعد الملزمة المطبَّقة هنا:
///   1) دورة الحياة: مسودة → اعتماد → ترحيل → إلغاء. **لا يُنشأ قيد إلا عند الترحيل**.
///   2) القيد يُبنى حصرًا عبر محرّك القيود المركزي PrepareEntryAsync — لا آلية موازية.
///   3) لا ترحيل مزدوج: JournalEntryId يُضبط مرة واحدة فقط، ويحميه: قيد الحالة في الخدمة،
///      وفهرس فريد على JournalEntryId في قاعدة البيانات، وRowVersion للترحيل المتزامن.
///   4) كل عملية تُسجَّل في AuditTrails (D3-A) داخل نفس SaveChanges (ذرّي — لا حفظ جزئي).
///   5) الصلاحيات تُفحص خلفيًا عبر IVoucherAuthorization (لا يمكن تجاوزها من الواجهة).
/// </summary>
public partial class ReceiptVoucherService : IReceiptVoucherService
{
    // أكواد الحسابات النظامية (نفس أكواد بذور شجرة الحسابات المستخدمة في بقية الخدمات)
    private const string AccountCash = "1100";       // الصندوق/الخزنة الرئيسية
    private const string AccountBank = "1101";       // البنك
    private const string AccountReceivable = "1200"; // ذمم العملاء

    private readonly DbContext _context;
    private readonly IJournalEntryService _journalService;
    private readonly IVoucherAuthorization _authorization;

    public ReceiptVoucherService(DbContext context, IJournalEntryService journalService, IVoucherAuthorization authorization)
    {
        _context = context;
        _journalService = journalService;
        _authorization = authorization;
    }

    public async Task<List<ReceiptVoucherDto>> GetAsync(
        Guid? customerId = null,
        int? status = null,
        DateTime? from = null,
        DateTime? to = null,
        string? search = null)
    {
        var query = QueryWithDetails();

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
                (v.Customer != null && (v.Customer.NameAr.Contains(term) || v.Customer.Code.Contains(term))));
        }

        var vouchers = await query
            .OrderByDescending(v => v.VoucherDate)
            .ThenByDescending(v => v.CreatedAt)
            .ToListAsync();

        return vouchers.Select(MapToDto).ToList();
    }

    public async Task<ReceiptVoucherDto?> GetByIdAsync(Guid id)
    {
        var voucher = await QueryWithDetails().FirstOrDefaultAsync(v => v.Id == id);
        return voucher is null ? null : MapToDto(voucher);
    }

    public async Task<List<VoucherAuditDto>> GetAuditAsync(Guid id)
    {
        var entityName = VoucherAuditTrailWriter.EntityReceipt;
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
    private IQueryable<ReceiptVoucher> QueryWithDetails()
        => _context.Set<ReceiptVoucher>()
            .Include(v => v.Customer)
            .Include(v => v.CashOrBankAccount)
            .Include(v => v.ReceivableAccount)
            .Include(v => v.JournalEntry);

    /// <summary>يفحص الصلاحية الخلفية ويُخفق برسالة صريحة عند غيابها (لا حماية بالواجهة فقط).</summary>
    private static void EnsureAllowed(bool allowed, string requiredPermission)
    {
        if (!allowed)
            throw new UnauthorizedAccessException(
                $"غير مصرّح بهذه العملية على سندات القبض. الصلاحية المطلوبة: {requiredPermission}");
    }

    private string? CurrentUser() => _authorization.CurrentUserName();

    /// <summary>حفظ واحد ذرّي مع رسالة عربية واضحة عند تعارض التزامن (نمط ConcurrencyHelper).</summary>
    private Task SaveAsync() => ConcurrencyHelper.SaveChangesWithFriendlyErrorAsync(
        _context,
        "تعارض تزامني — قام مستخدم آخر بتعديل نفس السند. حدّث الصفحة وحاول مرة أخرى.");

    internal static string StatusName(VoucherStatus status) => status switch
    {
        VoucherStatus.Draft => "مسودة",
        VoucherStatus.Approved => "معتمد",
        VoucherStatus.Posted => "مرحّل",
        VoucherStatus.Cancelled => "ملغى",
        VoucherStatus.Reversed => "معكوس",

        _ => status.ToString()
    };

    private static string Label(Account? account)
        => account is null ? string.Empty : $"{account.Code} - {account.NameAr}";

    /// <summary>
    /// يحل حسابات السند بالكود النظامي عبر PostingAccountResolver (ورقة متحركة نشطة دائمًا)،
    /// ويتحقق أن الحسابين متحركان فعلًا ⇒ لا ترحيل على حساب تجميعي/جذري/غير نشط.
    /// </summary>
    private async Task<(Account CashOrBank, Account Receivable)> ResolveAccountsAsync(int paymentMethod)
    {
        var method = (PaymentMethod)paymentMethod;
        if (method != PaymentMethod.Cash && method != PaymentMethod.Bank)
            throw new InvalidOperationException("طريقة القبض غير صالحة (نقدي أو بنكي فقط).");

        var cashOrBank = await PostingAccountResolver.ResolveAsync(
            _context, method == PaymentMethod.Cash ? AccountCash : AccountBank);
        var receivable = await PostingAccountResolver.ResolveAsync(_context, AccountReceivable);

        await MovementAccountValidator.EnsureMovableAccountAsync(_context, cashOrBank.Id);
        await MovementAccountValidator.EnsureMovableAccountAsync(_context, receivable.Id);

        return (cashOrBank, receivable);
    }

    private static void ValidateDraftInput(int paymentMethod, decimal amount, DateTime voucherDate)
    {
        var method = (PaymentMethod)paymentMethod;
        if (method != PaymentMethod.Cash && method != PaymentMethod.Bank)
            throw new InvalidOperationException("طريقة القبض غير صالحة (نقدي أو بنكي فقط).");

        if (amount <= 0m)
            throw new InvalidOperationException("المبلغ يجب أن يكون أكبر من صفر.");

        if (!JournalEntryService.IsValidAccountingDate(voucherDate))
            throw new InvalidOperationException("تاريخ السند غير صالح.");
    }

    public async Task<ReceiptVoucherDto> CreateDraftAsync(CreateReceiptVoucherDto dto)
    {
        EnsureAllowed(_authorization.CanCreate(), VoucherRules.Permission(VoucherRules.AddAction));
        ValidateDraftInput(dto.PaymentMethod, dto.Amount, dto.VoucherDate);

        var customer = await _context.Set<Customer>()
            .FirstOrDefaultAsync(c => c.Id == dto.CustomerId && !c.IsDeleted)
            ?? throw new InvalidOperationException("العميل غير موجود.");

        var accounts = await ResolveAccountsAsync(dto.PaymentMethod);
        var user = CurrentUser();

        var voucher = new ReceiptVoucher
        {
            Id = Guid.NewGuid(),
            VoucherNumber = await NumberSequenceHelper.NextAsync(_context, "RCV"),
            VoucherDate = dto.VoucherDate,
            Status = VoucherStatus.Draft,
            CustomerId = customer.Id,
            PaymentMethod = (PaymentMethod)dto.PaymentMethod,
            CashOrBankAccountId = accounts.CashOrBank.Id,
            ReceivableAccountId = accounts.Receivable.Id,
            Amount = Math.Round(dto.Amount, 2),
            Description = dto.Description,
            ExternalReference = dto.ExternalReference,
            AttachmentPath = dto.AttachmentPath,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = user
        };

        _context.Set<ReceiptVoucher>().Add(voucher);

        // سجل التدقيق (D3-A) يُضاف لنفس السياق ⇒ يُحفظ مع السند في SaveChanges واحد (ذرّي).
        VoucherAuditTrailWriter.Write(
            _context,
            VoucherAuditTrailWriter.EntityReceipt,
            voucher.Id,
            voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionCreated,
            $"إنشاء مسودة سند قبض بمبلغ {voucher.Amount:0.00} للعميل {customer.NameAr}",
            user);

        await SaveAsync();
        return await GetByIdAsync(voucher.Id) ?? MapToDto(voucher);
    }

    public async Task<ReceiptVoucherDto> UpdateDraftAsync(Guid id, UpdateReceiptVoucherDto dto)
    {
        EnsureAllowed(_authorization.CanEdit(), VoucherRules.Permission(VoucherRules.EditAction));
        ValidateDraftInput(dto.PaymentMethod, dto.Amount, dto.VoucherDate);

        var voucher = await _context.Set<ReceiptVoucher>().FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new InvalidOperationException("سند القبض غير موجود.");

        if (voucher.Status != VoucherStatus.Draft)
            throw new InvalidOperationException(
                $"لا يُعدّل إلا سند مسودة (الحالة الحالية: {StatusName(voucher.Status)}).");

        var customer = await _context.Set<Customer>()
            .FirstOrDefaultAsync(c => c.Id == dto.CustomerId && !c.IsDeleted)
            ?? throw new InvalidOperationException("العميل غير موجود.");

        var accounts = await ResolveAccountsAsync(dto.PaymentMethod);
        var user = CurrentUser();
        var before = $"{voucher.Amount:0.00} | {voucher.VoucherDate:yyyy-MM-dd} | {(int)voucher.PaymentMethod}";

        voucher.CustomerId = customer.Id;
        voucher.VoucherDate = dto.VoucherDate;
        voucher.PaymentMethod = (PaymentMethod)dto.PaymentMethod;
        voucher.CashOrBankAccountId = accounts.CashOrBank.Id;
        voucher.ReceivableAccountId = accounts.Receivable.Id;
        voucher.Amount = Math.Round(dto.Amount, 2);
        voucher.Description = dto.Description;
        voucher.ExternalReference = dto.ExternalReference;
        voucher.AttachmentPath = dto.AttachmentPath;
        voucher.UpdatedAt = DateTime.UtcNow;
        voucher.UpdatedBy = user;

        VoucherAuditTrailWriter.Write(
            _context,
            VoucherAuditTrailWriter.EntityReceipt,
            voucher.Id,
            voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionUpdated,
            $"تعديل مسودة: {before} → {voucher.Amount:0.00} | {voucher.VoucherDate:yyyy-MM-dd} | {(int)voucher.PaymentMethod}",
            user);

        await SaveAsync();
        return await GetByIdAsync(voucher.Id) ?? MapToDto(voucher);
    }

    public async Task<ReceiptVoucherDto> ApproveAsync(Guid id)
    {
        EnsureAllowed(_authorization.CanApprove(), VoucherRules.Permission(VoucherRules.ApproveAction));

        var voucher = await QueryWithDetails().FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new InvalidOperationException("سند القبض غير موجود.");

        if (voucher.Status == VoucherStatus.Approved)
            return MapToDto(voucher); // إعادة الاستدعاء لا تغيّر شيئًا (لا قيد في كل الأحوال)

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
            _context, VoucherAuditTrailWriter.EntityReceipt, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionApproved,
            $"اعتماد سند قبض بمبلغ {voucher.Amount:0.00} (لا يُنشأ قيد إلا عند الترحيل)", user);

        await SaveAsync();
        return MapToDto(voucher);
    }

    /// <summary>
    /// ترحيل سند القبض — **داخل معاملة صريحة** (إصلاح 2026-10-23):
    /// محرّك القيود المركزي يحدّث أرصدة الحسابات فورًا (`UPDATE Accounts … RETURNING`) قبل
    /// `SaveChanges`؛ وبلا معاملة كان تحديث الأرصدة يُثبَّت منفردًا ⇒ احتمال **أثر جزئي**
    /// (أرصدة متحركة بلا سند مُرحَّل) عند فشل الحفظ. المعاملة تجعل الأثر كله ذرّيًا،
    /// وهي شرط لفعّالية قفل صف العميل حتى الـcommit.
    /// </summary>
    public Task<ReceiptVoucherDto> PostAsync(Guid id)
        => DbTransactionHelper.ExecuteAsync(_context, () => PostCoreAsync(id, null));

    /// <summary>
    /// P0-2 (المرحلة B): ترحيل السند مع تخصيص صريح لمقبوضاته على فواتير العميل —
    /// التخصيص يُطبَّق **داخل نفس المعاملة** بعد إنشاء القيد (بلا قيد إضافي).
    /// </summary>
    public Task<ReceiptVoucherDto> PostAsync(Guid id, PostReceiptVoucherRequest? request)
        => DbTransactionHelper.ExecuteAsync(_context, () => PostCoreAsync(id, request));

    private async Task<ReceiptVoucherDto> PostCoreAsync(Guid id, PostReceiptVoucherRequest? request)
    {
        EnsureAllowed(_authorization.CanPost(), VoucherRules.Permission(VoucherRules.PostAction));

        var voucher = await QueryWithDetails().FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new InvalidOperationException("سند القبض غير موجود.");

        // منع الترحيل المزدوج (طبقة الخدمة): السند المُرحَّل يُعاد كما هو بلا قيد جديد.
        if (voucher.JournalEntryId is not null || voucher.Status == VoucherStatus.Posted)
            return MapToDto(voucher);

        if (voucher.Status == VoucherStatus.Cancelled)
            throw new InvalidOperationException("لا يُرحَّل سند ملغى.");

        if (voucher.Status != VoucherStatus.Approved)
            throw new InvalidOperationException(
                $"لا يُرحَّل إلا سند معتمد (الحالة الحالية: {StatusName(voucher.Status)}).");

        // ═══ إصلاح R3 (جذر R2-F1): حجز حق الترحيل بانتقال ذرّي مشروط — **قبل أي أثر مالي** ═══
        // إن خسر الطلب السباق فلا يوجد كيان مُضاف بعد ⇒ الإرجاع/الرفض آمنان (لا قيد ولا رصيد).
        var claimedForPost = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ReceiptVouchers\" SET \"Status\" = {(int)VoucherStatus.Posted} WHERE \"Id\" = {voucher.Id} AND \"Status\" = {(int)VoucherStatus.Approved} AND \"JournalEntryId\" IS NULL");

        if (claimedForPost == 0)
        {
            await _context.Entry(voucher).ReloadAsync();
            if (voucher.JournalEntryId is not null || voucher.Status == VoucherStatus.Posted)
                return MapToDto(voucher);   // ترحيل آخر سبقنا — لا نُنشئ قيدًا ثانيًا
            throw new InvalidOperationException("لا يُرحَّل سند ملغى.");
        }

        // الحسابات المحفوظة على السند حُدّدت عند الإنشاء؛ نتحقق مرة أخرى أنها متحركة قبل الترحيل.
        await MovementAccountValidator.EnsureMovableAccountAsync(_context, voucher.CashOrBankAccountId);
        await MovementAccountValidator.EnsureMovableAccountAsync(_context, voucher.ReceivableAccountId);

        // القيد المركزي: مدين الصندوق/البنك — دائن ذمم العميل
        var journal = await _journalService.PrepareEntryAsync(
            JournalEntryType.ReceiptVoucher,
            voucher.VoucherDate,
            voucher.VoucherNumber,
            $"سند قبض {voucher.VoucherNumber} - {voucher.Customer?.NameAr}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = voucher.CashOrBankAccountId, DebitAmount = voucher.Amount },
                new() { AccountId = voucher.ReceivableAccountId, CreditAmount = voucher.Amount }
            });

        var user = CurrentUser();
        voucher.JournalEntryId = journal.Id;
        voucher.Status = VoucherStatus.Posted;
        voucher.PostedAt = DateTime.UtcNow;
        voucher.PostedBy = user;
        voucher.UpdatedAt = DateTime.UtcNow;
        voucher.UpdatedBy = user;

        // إصلاح D-09-01 (تدقيق 2026-10-20): ترحيل السند يجب أن يخفّض رصيد العميل أيضًا.
        // كان القيد المحاسبي وحده يعكس التحصيل بينما Customer.CurrentBalance يبقى كما هو،
        // فتتخلّف كشوف الحساب/أعمار الذمم عن دفتر الأستاذ. التحديث هنا ضمن نفس وحدة العمل
        // (حفظ واحد ذرّي مع السند والقيد) فلا يمكن أن يقع أثر جزئي.
        if (voucher.Customer is not null)
        {
            // إصلاح 2026-10-23: قفل صف العميل ثم قراءة رصيده الطازج قبل الكتابة (منع lost update).
            // (كان القيد وحده يعكس التحصيل والرصيد التشغيلي يُكتب من قيمة قديمة ⇒ رصيد مفقود
            //  عند سندَي قبض متزامنين لنفس العميل.)
            await PartyRowLock.LockCustomerAsync(_context, voucher.Customer.Id);
            await _context.Entry(voucher.Customer).ReloadAsync();
            voucher.Customer.CurrentBalance = MoneyRounding.Round(voucher.Customer.CurrentBalance - voucher.Amount);
            voucher.Customer.UpdatedAt = DateTime.UtcNow;
        }

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityReceipt, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionPosted,
            $"ترحيل سند قبض بمبلغ {voucher.Amount:0.00} وإنشاء القيد {journal.EntryNumber}",
            user, journal.Id);

        // ═══ P0-2 (المرحلة B): تخصيص المقبوضات (تحليلي — **بلا قيد محاسبي إضافي**) ═══
        // يُطبَّق داخل نفس المعاملة/الحفظ ⇒ ذرّي مع الترحيل، وأي رفض (تجاوز المتاح/عميل مختلف/
        // فاتورة غير مرحَّلة) يُبطل الترحيل بالكامل ولا يبقى سند مرحّل بلا تخصيص صحيح.
        if (request?.Allocations is { Count: > 0 })
            await ApplyAllocationsInternalAsync(voucher, request.Allocations, request.IdempotencyKey);

        // حفظ واحد ذرّي: السند + القيد وبنوده + الأرصدة + سجل التدقيق + صفوف التخصيص
        await SaveAsync();
        return await GetByIdAsync(voucher.Id) ?? MapToDto(voucher);
    }

    /// <summary>
    /// إلغاء سند القبض — **داخل معاملة صريحة** (إصلاح 2026-10-23): قفل صف السند يُحرَّر فور
    /// انتهاء الجملة بلا معاملة، فكان الإلغاء يقرأ الحالة القديمة ويكتب فوق قرار الترحيل.
    /// </summary>
    public Task<ReceiptVoucherDto> CancelAsync(Guid id, string? reason)
        => DbTransactionHelper.ExecuteAsync(_context, () => CancelCoreAsync(id, reason));

    private async Task<ReceiptVoucherDto> CancelCoreAsync(Guid id, string? reason)
    {
        EnsureAllowed(_authorization.CanCancel(), VoucherRules.Permission(VoucherRules.CancelAction));

        var voucher = await QueryWithDetails().FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new InvalidOperationException("سند القبض غير موجود.");

        // ═══ إصلاح R3 (جذر R2-F1): انتقال حالة ذرّي مشروط على القاعدة ═══
        // أثبت مسبار R3 أن قفل الصف (FOR UPDATE) في هذا المسار **لا يحجب** كاتبًا آخر فعليًا،
        // لذلك لا يجوز بناء القرار عليه. الانتقال نفسه يصبح شرطيًا: لا تُكتب «ملغى» إلا إذا كانت
        // الحالة ما زالت «معتمد» وبلا قيد. جملة UPDATE تقفل الصف ضمنيًا ⇒ الطلبان يتسلسلان والفائز واحد.
        var cancelledStatus = (int)VoucherStatus.Cancelled;
        var approvedStatus = (int)VoucherStatus.Approved;
        var claimed = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ReceiptVouchers\" SET \"Status\" = {cancelledStatus} WHERE \"Id\" = {voucher.Id} AND \"Status\" <> {(int)VoucherStatus.Posted} AND \"Status\" <> {cancelledStatus} AND \"JournalEntryId\" IS NULL");

        if (claimed == 0)
        {
            // خسر السباق: نعيد قراءة الحالة الحقيقية ونقرر وفقها (لا كتابة عمياء).
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
            _context, VoucherAuditTrailWriter.EntityReceipt, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionCancelled,
            string.IsNullOrWhiteSpace(reason) ? "إلغاء السند قبل الترحيل (لا حذف)" : $"إلغاء السند: {reason}",
            user);

        await SaveAsync();
        return MapToDto(voucher);
    }

    internal static ReceiptVoucherDto MapToDto(ReceiptVoucher v) => new()
    {
        Id = v.Id,
        VoucherNumber = v.VoucherNumber,
        VoucherDate = v.VoucherDate,
        Status = (int)v.Status,
        StatusName = StatusName(v.Status),
        CustomerId = v.CustomerId,
        CustomerCode = v.Customer?.Code ?? string.Empty,
        CustomerName = v.Customer?.NameAr ?? string.Empty,
        PaymentMethod = (int)v.PaymentMethod,
        CashOrBankAccountId = v.CashOrBankAccountId,
        CashOrBankAccountLabel = Label(v.CashOrBankAccount),
        ReceivableAccountId = v.ReceivableAccountId,
        ReceivableAccountLabel = Label(v.ReceivableAccount),
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
}


