using System.Data;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERPSystem.Application.Services;

/// <summary>
/// PU-04 — مستند عكس سند صرف (D7-ب).
/// القواعد: سند Posted فقط · لا عكس لعكس · Σ عكوس الصف ≤ الأصل · قيد عكسي متوازن ·
/// العكس الجزئي يتطلب إعادة توزيع بنفس المبلغ (H-1) · العكس الكلي يجعل حالة الأصل Reversed.
/// </summary>
public sealed class PaymentVoucherReversalService : IPaymentVoucherReversalService
{
    private readonly DbContext _context;
    private readonly IJournalEntryService _journalService;
    private readonly IVoucherAuthorization _authorization;

    public PaymentVoucherReversalService(
        DbContext context,
        IJournalEntryService journalService,
        IVoucherAuthorization authorization)
    {
        _context = context;
        _journalService = journalService;
        _authorization = authorization;
    }

    public async Task<PaymentVoucherReversalDto> ReverseAsync(CreatePaymentVoucherReversalDto dto)
    {
        if (!_authorization.CanReverse())
            throw new UnauthorizedAccessException(
                $"غير مصرّح بعكس سندات الصرف. الصلاحية المطلوبة: {VoucherRules.Permission(VoucherRules.ReverseAction)}");

        // SUB-2b/P3: التخصيصات تُلزم لسند المورد فقط. سند رد العميل لا يملك صفوف تخصيص أصلًا.
        var party = await _context.Set<PaymentVoucher>().AsNoTracking()
            .Where(v => v.Id == dto.OriginalPaymentVoucherId && !v.IsDeleted)
            .Select(v => new { v.SupplierId, v.CustomerId })
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("سند الصرف الأصلي غير موجود.");

        if (party.CustomerId is null && (dto.Allocations is null || dto.Allocations.Count == 0))
            throw new InvalidOperationException("يجب تحديد التخصيصات المطلوب عكسها.");
        if (!JournalEntryService.IsValidAccountingDate(dto.VoucherDate))
            throw new InvalidOperationException("تاريخ مستند العكس غير صالح.");

        try
        {
            return await DbTransactionHelper.ExecuteAsync(_context, () => ReverseCoreAsync(dto));
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOnIndex(ex, IndexReversalIdempotencyKey)
                                           || UniqueViolation.IsOnIndex(ex, IndexReversalOriginal))
        {
            // PU-04 (P0-3): سباق متزامن على العكس (نفس المفتاح، أو عكس نفس السند من طلبين).
            // المعاملة تراجعت كاملة ⇒ لا مستند عكس ولا قيد ولا صفوف `-1` ولا تغيير أرصدة.
            var key = NormalizeKey(dto.IdempotencyKey);
            DetachFailedReversalWork();

            // (1) نفس الطلب أُعيد إرساله ⇒ نُعيد المستند العكسي الأصلي لا إنشاء ثانٍ.
            if (key is { } sameKey)
            {
                var sameRequest = await FindReversalByKeyAsync(sameKey);
                if (sameRequest is not null) return sameRequest;
            }

            // (2) فهرس «عكس واحد لكل سند»: وجود مستند عكس لهذا السند معناه «مُعكَس سابقًا» لا تكرار طلب.
            var existingForVoucher = await FindReversalByOriginalAsync(dto.OriginalPaymentVoucherId);
            if (existingForVoucher is not null)
                throw new InvalidOperationException(
                    "لهذا السند مستند عكس قائم — لا يُسمح بعكس ثانٍ (استخدم عكوسًا جزئية متعددة داخل المستند نفسه).");

            throw; // انتهاك مرشّح بلا مستند مطابق ⇒ خطأ غير متوقع: لا يُخفى.
        }
    }

    private async Task<PaymentVoucherReversalDto> ReverseCoreAsync(CreatePaymentVoucherReversalDto dto)
    {
        // N-1: نفس طلب العكس بمفتاح موجود ⇒ إعادة المستند بلا أثر جديد.
        if (dto.IdempotencyKey is { } key)
        {
            var existing = await _context.Set<PaymentVoucherReversal>()
                .Include(r => r.Allocations)
                .FirstOrDefaultAsync(r => r.IdempotencyKey == key && !r.IsDeleted);
            if (existing is not null) return await MapAsync(existing);
        }

        var voucher = await _context.Set<PaymentVoucher>()
            .Include(v => v.Supplier)
            .Include(v => v.Customer)
            .Include(v => v.SalesReturn)
            .FirstOrDefaultAsync(v => v.Id == dto.OriginalPaymentVoucherId && !v.IsDeleted)
            ?? throw new InvalidOperationException("سند الصرف الأصلي غير موجود.");

        if (voucher.Status != VoucherStatus.Posted || voucher.JournalEntryId is null)
        {
            // إصلاح 2026-10-23 (سباق مُثبت باختبار تزامن): قد يكون الطلب **نفسه** قد فاز في معاملة
            // متزامنة فصار السند `Reversed` بعد قراءتنا ⇒ نُعيد مستند العكس المحفوظ بالمفتاح نفسه
            // بدل رفض الطلب برسالة «غير مُرحَّل/معكوس سابقًا». لا يُطبَّق إلا بمفتاح مطابق.
            if (await TryReturnConcurrentSameKeyAsync(dto) is { } sameRequest) return sameRequest;

            throw new InvalidOperationException("لا يُعكس إلا سند صرف مُرحَّل وغير معكوس سابقًا.");
        }

        var alreadyReversed = await _context.Set<PaymentVoucherReversal>()
            .AnyAsync(r => r.OriginalPaymentVoucherId == voucher.Id && !r.IsDeleted);
        if (alreadyReversed)
        {
            // سباق متزامن: قد يكون المستند القائم هو **نفس هذا الطلب** (نفس المفتاح) ⇒ نُعيده لا نرفضه.
            if (await TryReturnConcurrentSameKeyAsync(dto) is { } sameRequest) return sameRequest;

            throw new InvalidOperationException(
                "لهذا السند مستند عكس قائم — لا يُسمح بعكس ثانٍ (استخدم عكوسًا جزئية متعددة داخل المستند نفسه).");
        }

        // SUB-2b/P3 — عكس سند رد عميل: مبلغ كامل فقط، بلا تخصيصات، بقيد Dr 1100/1101 · Cr 1200.
        if (voucher.CustomerId is { } customerId)
            return await ReverseCustomerRefundCoreAsync(dto, voucher, customerId);

        if (voucher.SupplierId is not { } supplierId)
            throw new InvalidOperationException("سند الصرف بلا طرف — لا يمكن عكسه.");

        // ترتيب الأقفال: المورد ← التخصيصات ← الفواتير.
        var supplierBalance = await LockSupplierAsync(supplierId);
        if (voucher.Supplier is not null) await _context.Entry(voucher.Supplier).ReloadAsync();

        var allocationIds = dto.Allocations.Select(a => a.AllocationId).Distinct().OrderBy(x => x).ToList();
        foreach (var allocationId in allocationIds)
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"PaymentAllocations\" WHERE \"Id\" = {allocationId} FOR UPDATE");

        var originalAllocations = await _context.Set<PaymentAllocation>()
            .Where(a => allocationIds.Contains(a.Id) && !a.IsDeleted)
            .ToListAsync();

        var rows = new List<PaymentAllocation>();
        var total = 0m;
        var now = DateTime.UtcNow;

        foreach (var input in dto.Allocations.OrderBy(a => a.AllocationId))
        {
            var amount = MoneyRounding.Round(input.Amount);
            if (amount <= 0m)
                throw new InvalidOperationException("مبلغ العكس يجب أن يكون أكبر من صفر.");

            var allocation = originalAllocations.FirstOrDefault(a => a.Id == input.AllocationId)
                ?? throw new InvalidOperationException("صف التخصيص غير موجود.");

            if (allocation.PaymentVoucherId != voucher.Id)
                throw new InvalidOperationException("صف التخصيص لا يتبع السند الأصلي.");
            if (allocation.Direction != 1)
                throw new InvalidOperationException("لا يُعكس صف عكس (Direction = −1).");

            var already = await _context.Set<PaymentAllocation>().AsNoTracking()
                .Where(a => a.ReversalOfAllocationId == allocation.Id && a.Direction == -1 && !a.IsDeleted)
                .SumAsync(a => (decimal?)a.Amount) ?? 0m;

            if (MoneyRounding.Round(already + amount) > allocation.Amount)
            {
                // سباق متزامن على نفس الطلب: إن حُفظ المستند بالمفتاح نفسه ⇒ نُعيده بلا أثر جديد.
                if (await TryReturnConcurrentSameKeyAsync(dto) is { } sameRequest) return sameRequest;

                throw new InvalidOperationException(
                    $"مجموع عكوس صف التخصيص ({MoneyRounding.Round(already + amount):N2}) يتجاوز مبلغه الأصلي ({allocation.Amount:N2}).");
            }

            total = MoneyRounding.Round(total + amount);
            rows.Add(new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                PaymentVoucherId = voucher.Id,
                PurchaseInvoiceId = allocation.PurchaseInvoiceId,
                SupplierId = supplierId,
                Amount = amount,
                Direction = -1,
                ReversalOfAllocationId = allocation.Id,
                AllocatedAt = now,
                CreatedAt = now,
                CreatedBy = _authorization.CurrentUserName(),
                Note = dto.Reason
            });
        }

                // D5: العكس الجزئي يخلق مبلغًا غير موزع ⇒ مرفوض؛ التصحيح الجزئي يتم بإعادة التوزيع.
        var isFull = true;
        if (total != voucher.Amount)
            throw new InvalidOperationException(
                $"العكس الجزئي ({total:N2} من {voucher.Amount:N2}) غير مسموح لأنه يترك مبلغًا غير موزع على السند. " +
                "للتصحيح الجزئي استخدم إعادة التوزيع (Reallocate)، أو اعكس السند كاملًا.");

        var reversal = new PaymentVoucherReversal
        {
            Id = Guid.NewGuid(),
            ReversalNumber = await NumberSequenceHelper.NextAsync(_context, "PVR"),
            OriginalPaymentVoucherId = voucher.Id,
            SupplierId = supplierId,
            VoucherDate = dto.VoucherDate,
            Status = VoucherStatus.Posted,
            Amount = total,
            CashOrBankAccountId = voucher.CashOrBankAccountId,
            PayableAccountId = voucher.PayableAccountId,
            Reason = dto.Reason,
            IdempotencyKey = dto.IdempotencyKey,
            CreatedAt = now,
            CreatedBy = _authorization.CurrentUserName()
        };

        foreach (var row in rows) row.ReversalDocumentId = reversal.Id;
        _context.Set<PaymentVoucherReversal>().Add(reversal);
        _context.Set<PaymentAllocation>().AddRange(rows);

        var entry = await _journalService.PrepareEntryAsync(
            JournalEntryType.PaymentVoucher,
            dto.VoucherDate,
            reversal.ReversalNumber,
            $"عكس سند صرف {voucher.VoucherNumber} - {voucher.Supplier?.NameAr}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = voucher.CashOrBankAccountId, DebitAmount = total, Note = "عكس صرف" },
                new() { AccountId = voucher.PayableAccountId, CreditAmount = total, Note = "عكس صرف" }
            });
        reversal.JournalEntryId = entry.Id;

        if (voucher.Supplier is not null)
        {
            voucher.Supplier.CurrentBalance = MoneyRounding.Round(supplierBalance + total);
            voucher.Supplier.UpdatedAt = now;
        }

        if (isFull)
        {
            voucher.Status = VoucherStatus.Reversed;
            voucher.UpdatedAt = now;
            voucher.UpdatedBy = _authorization.CurrentUserName();
        }

        VoucherAuditTrailWriter.Write(
            _context, "PaymentVoucherReversal", reversal.Id, reversal.ReversalNumber,
            VoucherAuditTrailWriter.ActionPosted,
            $"عكس {(isFull ? "كلي" : "جزئي")} للسند {voucher.VoucherNumber} بمبلغ {total:0.00}",
            _authorization.CurrentUserName(), entry.Id);

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityPayment, voucher.Id, voucher.VoucherNumber,
            "Reversed",
            $"إنشاء مستند عكس {reversal.ReversalNumber} بمبلغ {total:0.00}",
            _authorization.CurrentUserName(), entry.Id);

        await ConcurrencyHelper.SaveChangesWithFriendlyErrorAsync(
            _context, "تعارض تزامني أثناء عكس السند — أعد المحاولة.");

        return await MapAsync(reversal);
    }

    /// <summary>
    /// SUB-2b/P3 — عكس كامل لسند رد عميل (لا عكس جزئي):
    /// قيد عكسي Dr الصندوق/البنك · Cr 1200 بنوع CustomerRefund، وإرجاع رصيد العميل مرة واحدة،
    /// وجعل السند Reversed حتى يعود المبلغ قابلًا للرد من المردود (قاعدة السقف).
    /// </summary>
    private async Task<PaymentVoucherReversalDto> ReverseCustomerRefundCoreAsync(
        CreatePaymentVoucherReversalDto dto, PaymentVoucher voucher, Guid customerId)
    {
        var total = MoneyRounding.Round(voucher.Amount);
        if (total <= 0m)
            throw new InvalidOperationException("سند الرد بلا مبلغ قابل للعكس.");

        var customer = await _context.Set<Customer>()
            .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted)
            ?? throw new InvalidOperationException("العميل غير موجود.");

        await PartyRowLock.LockCustomerAsync(_context, customerId);
        await _context.Entry(customer).ReloadAsync();

        var now = DateTime.UtcNow;
        var user = _authorization.CurrentUserName();

        var reversal = new PaymentVoucherReversal
        {
            Id = Guid.NewGuid(),
            ReversalNumber = await NumberSequenceHelper.NextAsync(_context, "PVR"),
            OriginalPaymentVoucherId = voucher.Id,
            SupplierId = null,
            CustomerId = customerId,
            VoucherDate = dto.VoucherDate,
            Status = VoucherStatus.Posted,
            Amount = total,
            CashOrBankAccountId = voucher.CashOrBankAccountId,
            PayableAccountId = voucher.PayableAccountId,
            Reason = dto.Reason,
            IdempotencyKey = NormalizeKey(dto.IdempotencyKey),
            CreatedAt = now,
            CreatedBy = user
        };

        _context.Set<PaymentVoucherReversal>().Add(reversal);

        var entry = await _journalService.PrepareEntryAsync(
            JournalEntryType.CustomerRefund,
            dto.VoucherDate,
            reversal.ReversalNumber,
            $"عكس رد مبلغ للعميل {customer.NameAr} - السند {voucher.VoucherNumber}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = voucher.CashOrBankAccountId, DebitAmount = total, Note = "عكس رد عميل" },
                new() { AccountId = voucher.PayableAccountId, CreditAmount = total, Note = "عكس رد عميل" }
            });
        reversal.JournalEntryId = entry.Id;

        // إلغاء أثر الرد على ذمة العميل: الرصيد يعود دائنًا بمقدار المبلغ (قفل + Reload يمنع lost update).
        customer.CurrentBalance = MoneyRounding.Round(customer.CurrentBalance - total);
        customer.UpdatedAt = now;

        voucher.Status = VoucherStatus.Reversed;
        voucher.UpdatedAt = now;
        voucher.UpdatedBy = user;

        VoucherAuditTrailWriter.Write(
            _context, "PaymentVoucherReversal", reversal.Id, reversal.ReversalNumber,
            VoucherAuditTrailWriter.ActionPosted,
            $"عكس كلي لرد مبلغ للعميل {customer.NameAr} بمبلغ {total:0.00} (السند {voucher.VoucherNumber})",
            user, entry.Id);

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityPayment, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionReversed,
            $"إنشاء مستند عكس {reversal.ReversalNumber} بمبلغ {total:0.00} — عكس رد عميل",
            user, entry.Id);

        await ConcurrencyHelper.SaveChangesWithFriendlyErrorAsync(
            _context, "تعارض تزامني أثناء عكس سند الرد — أعد المحاولة.");

        return await MapAsync(reversal);
    }

    public async Task<List<PaymentAllocationDto>> ReallocateAsync(ReallocateAllocationsDto dto)
    {
        if (!_authorization.CanReverse())
            throw new UnauthorizedAccessException(
                $"غير مصرّح بإعادة توزيع تخصيصات سندات الصرف. الصلاحية المطلوبة: {VoucherRules.Permission(VoucherRules.ReverseAction)}");

        return await DbTransactionHelper.ExecuteAsync(_context, () => ReallocateCoreAsync(dto));
    }

    private async Task<List<PaymentAllocationDto>> ReallocateCoreAsync(ReallocateAllocationsDto dto)
    {
        var voucher = await _context.Set<PaymentVoucher>()
            .FirstOrDefaultAsync(v => v.Id == dto.PaymentVoucherId && !v.IsDeleted)
            ?? throw new InvalidOperationException("سند الصرف غير موجود.");

        if (voucher.CustomerId is not null)
            throw new InvalidOperationException(
                "إعادة التوزيع خاصة بسندات الموردين — سند رد العميل لا يملك تخصيصات فواتير مشتريات.");

        if (voucher.Status != VoucherStatus.Posted || voucher.JournalEntryId is null)
            throw new InvalidOperationException("إعادة التوزيع متاحة فقط لسند مُرحَّل وغير معكوس.");

        if (voucher.SupplierId is not { } supplierId)
            throw new InvalidOperationException("سند الصرف بلا مورد — لا يمكن إعادة توزيعه.");

        await LockSupplierAsync(supplierId);

        var fromIds = dto.FromAllocations.Select(a => a.AllocationId).Distinct().OrderBy(x => x).ToList();
        foreach (var id in fromIds)
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"PaymentAllocations\" WHERE \"Id\" = {id} FOR UPDATE");

        var sources = await _context.Set<PaymentAllocation>()
            .Where(a => fromIds.Contains(a.Id) && !a.IsDeleted)
            .ToListAsync();

        var now = DateTime.UtcNow;
        var rows = new List<PaymentAllocation>();
        var movedTotal = 0m;

        foreach (var input in dto.FromAllocations.OrderBy(a => a.AllocationId))
        {
            var amount = MoneyRounding.Round(input.Amount);
            if (amount <= 0m)
                throw new InvalidOperationException("مبلغ السحب يجب أن يكون أكبر من صفر.");

            var source = sources.FirstOrDefault(a => a.Id == input.AllocationId)
                ?? throw new InvalidOperationException("صف التخصيص المصدر غير موجود.");
            if (source.PaymentVoucherId != voucher.Id)
                throw new InvalidOperationException("صف التخصيص المصدر لا يتبع السند.");
            if (source.Direction != 1)
                throw new InvalidOperationException("لا يمكن السحب من صف عكس (Direction = −1).");

            var already = await _context.Set<PaymentAllocation>().AsNoTracking()
                .Where(a => a.ReversalOfAllocationId == source.Id && a.Direction == -1 && !a.IsDeleted)
                .SumAsync(a => (decimal?)a.Amount) ?? 0m;

            if (MoneyRounding.Round(already + amount) > source.Amount)
                throw new InvalidOperationException(
                    $"مجموع المسحوب من صف التخصيص يتجاوز مبلغه الأصلي ({source.Amount:N2}).");

            movedTotal = MoneyRounding.Round(movedTotal + amount);
            rows.Add(new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                PaymentVoucherId = voucher.Id,
                PurchaseInvoiceId = source.PurchaseInvoiceId,
                SupplierId = supplierId,
                Amount = amount,
                Direction = -1,
                ReversalOfAllocationId = source.Id,
                AllocatedAt = now,
                CreatedAt = now,
                CreatedBy = _authorization.CurrentUserName(),
                Note = dto.Reason ?? "إعادة توزيع (سحب)"
            });
        }

        var targets = dto.ToInvoices
            .GroupBy(x => x.PurchaseInvoiceId)
            .Select(g => new PaymentAllocationInput
            {
                PurchaseInvoiceId = g.Key,
                Amount = MoneyRounding.Round(g.Sum(y => y.Amount))
            })
            .Where(x => x.Amount > 0m)
            .ToList();

        var targetTotal = MoneyRounding.Round(targets.Sum(t => t.Amount));
        if (targetTotal != movedTotal)
            throw new InvalidOperationException(
                $"مجموع إعادة التوزيع ({targetTotal:N2}) يجب أن يساوي المبلغ المسحوب ({movedTotal:N2}) للحفاظ على مجموع تخصيصات السند.");

        var residualQueries = new SupplierAllocationQueries(_context);
        foreach (var invoiceId in targets.Select(t => t.PurchaseInvoiceId).Distinct().OrderBy(x => x))
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"PurchaseInvoices\" WHERE \"Id\" = {invoiceId} FOR UPDATE");

        foreach (var target in targets.OrderBy(t => t.PurchaseInvoiceId))
        {
            var invoice = await _context.Set<PurchaseInvoice>()
                .FirstOrDefaultAsync(i => i.Id == target.PurchaseInvoiceId && !i.IsDeleted)
                ?? throw new InvalidOperationException("فاتورة الوجهة غير موجودة.");
            if (invoice.SupplierId != voucher.SupplierId)
                throw new InvalidOperationException("فاتورة الوجهة لا تخص المورد نفسه.");

            var residual = await residualQueries.GetResidualAsync(target.PurchaseInvoiceId);
            if (residual is null || !residual.IsEligible)
                throw new InvalidOperationException(
                    $"الفاتورة {invoice.InvoiceNumber} غير مؤهلة لإعادة التوزيع: {residual?.IneligibilityReason}");
            if (target.Amount > residual.Available)
                throw new InvalidOperationException(
                    $"إعادة التوزيع ({target.Amount:N2}) تتجاوز الرصيد المتاح للفاتورة {invoice.InvoiceNumber} ({residual.Available:N2}).");

            rows.Add(new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                PaymentVoucherId = voucher.Id,
                PurchaseInvoiceId = invoice.Id,
                SupplierId = supplierId,
                Amount = target.Amount,
                Direction = 1,
                AllocatedAt = now,
                CreatedAt = now,
                CreatedBy = _authorization.CurrentUserName(),
                Note = dto.Reason ?? "إعادة توزيع (إضافة)"
            });
        }

        _context.Set<PaymentAllocation>().AddRange(rows);

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityPayment, voucher.Id, voucher.VoucherNumber,
            "AllocationReallocated",
            $"إعادة توزيع {movedTotal:0.00} بين فواتير السند (تصحيح بحت بلا قيد محاسبي)",
            _authorization.CurrentUserName());

        await ConcurrencyHelper.SaveChangesWithFriendlyErrorAsync(
            _context, "تعارض تزامني أثناء إعادة التوزيع — أعد المحاولة.");

        return await GetAllocationsAsync(voucher.Id);
    }

    public async Task<List<PaymentVoucherReversalDto>> GetByVoucherAsync(Guid originalVoucherId)
    {
        var items = await _context.Set<PaymentVoucherReversal>().AsNoTracking()
            .Include(r => r.Allocations)
            .Where(r => r.OriginalPaymentVoucherId == originalVoucherId && !r.IsDeleted)
            .OrderByDescending(r => r.VoucherDate)
            .ToListAsync();

        var result = new List<PaymentVoucherReversalDto>(items.Count);
        foreach (var item in items) result.Add(await MapAsync(item));
        return result;
    }

    public async Task<List<PaymentAllocationDto>> GetAllocationsAsync(Guid paymentVoucherId)
    {
        var rows = await _context.Set<PaymentAllocation>().AsNoTracking()
            .Include(a => a.PurchaseInvoice)
            .Where(a => a.PaymentVoucherId == paymentVoucherId && !a.IsDeleted)
            .OrderBy(a => a.AllocatedAt).ThenBy(a => a.Id)
            .ToListAsync();

        return rows.Select(a => new PaymentAllocationDto
        {
            Id = a.Id,
            PaymentVoucherId = a.PaymentVoucherId,
            PurchaseInvoiceId = a.PurchaseInvoiceId,
            InvoiceNumber = a.PurchaseInvoice?.InvoiceNumber ?? string.Empty,
            Amount = a.Amount,
            Direction = a.Direction,
            ReversalOfAllocationId = a.ReversalOfAllocationId,
            ReversalDocumentId = a.ReversalDocumentId,
            AllocatedAt = a.AllocatedAt,
            Note = a.Note
        }).ToList();
    }

    private async Task<PaymentVoucherReversalDto> MapAsync(PaymentVoucherReversal reversal)
    {
        if (reversal.Allocations.Count == 0)
            await _context.Entry(reversal).Collection(r => r.Allocations).LoadAsync();

        var original = await _context.Set<PaymentVoucher>().AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == reversal.OriginalPaymentVoucherId);

        return new PaymentVoucherReversalDto
        {
            Id = reversal.Id,
            ReversalNumber = reversal.ReversalNumber,
            OriginalPaymentVoucherId = reversal.OriginalPaymentVoucherId,
            SupplierId = reversal.SupplierId,
            CustomerId = reversal.CustomerId,
            VoucherDate = reversal.VoucherDate,
            Amount = reversal.Amount,
            Status = (int)reversal.Status,
            JournalEntryId = reversal.JournalEntryId,
            OriginalFullyReversed = original?.Status == VoucherStatus.Reversed,
            Allocations = reversal.Allocations.Select(a => new PaymentAllocationDto
            {
                Id = a.Id,
                PaymentVoucherId = a.PaymentVoucherId,
                PurchaseInvoiceId = a.PurchaseInvoiceId,
                Amount = a.Amount,
                Direction = a.Direction,
                ReversalOfAllocationId = a.ReversalOfAllocationId,
                ReversalDocumentId = a.ReversalDocumentId,
                AllocatedAt = a.AllocatedAt,
                Note = a.Note
            }).ToList()
        };
    }

    private async Task<decimal> LockSupplierAsync(Guid supplierId)
    {
        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
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
            var raw = await cmd.ExecuteScalarAsync();
            if (raw is null or DBNull)
                throw new InvalidOperationException("المورد غير موجود.");
            return Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open) await connection.CloseAsync();
        }
    }

    private static List<PaymentAllocationInput> NormalizeReallocations(IEnumerable<PaymentAllocationInput> items)
        => items
            .GroupBy(a => a.PurchaseInvoiceId)
            .Select(g => new PaymentAllocationInput
            {
                PurchaseInvoiceId = g.Key,
                Amount = MoneyRounding.Round(g.Sum(x => x.Amount))
            })
            .Where(x => x.Amount > 0m)
            .ToList();

    // ═══════════ PU-04 (P0-3) — معالجة تعارض الفهرس الفريد في مسار العكس ═══════════

    private const string IndexReversalIdempotencyKey = "UX_PaymentVoucherReversals_IdempotencyKey";
    private const string IndexReversalOriginal = "UX_PaymentVoucherReversals_Original";

    /// <summary>`Guid.Empty` تُعامَل كعدم إرسال مفتاح (توافق خلفي).</summary>
    private static Guid? NormalizeKey(Guid? key)
        => key is { } k && k != Guid.Empty ? k : null;

    /// <summary>
    /// سباق متزامن على نفس الطلب: إن حُفظ مستند العكس بالمفتاح نفسه من طلب آخر أثناء تنفيذ هذا الطلب،
    /// نُعيد المستند المحفوظ (نجاح) بدل رفض الطلب برسالة «مُعكَس سابقًا» أو «تجاوز الأصل».
    /// لا يُطبَّق إلا بمفتاح **مطابق** ⇒ لا يُخفي محاولة عكس مختلفة.
    /// </summary>
    private async Task<PaymentVoucherReversalDto?> TryReturnConcurrentSameKeyAsync(CreatePaymentVoucherReversalDto dto)
        => NormalizeKey(dto.IdempotencyKey) is { } key ? await FindReversalByKeyAsync(key) : null;

    /// <summary>مستند العكس المحفوظ بنفس مفتاح الطلب ⇒ نتيجة «إعادة إرسال الطلب نفسه».</summary>
    private async Task<PaymentVoucherReversalDto?> FindReversalByKeyAsync(Guid key)
    {
        var row = await _context.Set<PaymentVoucherReversal>()
            .Include(r => r.Allocations)
            .FirstOrDefaultAsync(r => r.IdempotencyKey == key && !r.IsDeleted);

        return row is null ? null : await MapAsync(row);
    }

    /// <summary>مستند العكس القائم لأصل معيّن (يُستخدم لتمييز «مُعكَس سابقًا» عن «تكرار طلب»).</summary>
    private async Task<PaymentVoucherReversalDto?> FindReversalByOriginalAsync(Guid originalVoucherId)
    {
        var row = await _context.Set<PaymentVoucherReversal>()
            .Include(r => r.Allocations)
            .FirstOrDefaultAsync(r => r.OriginalPaymentVoucherId == originalVoucherId && !r.IsDeleted);

        return row is null ? null : await MapAsync(row);
    }

    /// <summary>
    /// تفريغ آثار محاولة العكس الفاشلة من السياق (لا تفريغ شامل) ⇒ لا كيانات معلّقة تُحفظ لاحقًا،
    /// ولا يرفض حاجز «لا إعادة محاولة صامتة» العملية التالية في نفس الجلسة.
    /// </summary>
    private void DetachFailedReversalWork()
    {
        DocumentFailureCleanup.DetachPendingDocuments(_context);

        foreach (var entry in _context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is PaymentVoucherReversal or PaymentAllocation or PaymentVoucher or AuditTrail)
                entry.State = EntityState.Detached;
        }
    }
}
