using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// P0-2 (المرحلة B) — تخصيص مقبوضات العملاء على فواتيرهم (جزء من خدمة سند القبض).
///
/// المبادئ الملزمة:
///  • **لا يُنشئ التخصيص أي قيد محاسبي** — القيد الوحيد هو قيد سند القبض نفسه؛
///    التخصيص توزيع تحليلي لتسديد موجود في الأستاذ (لا تكرار للقيد الأصلي).
///  • لا يُخصَّص إلا سند قبض **مُرحَّل** على فاتورة بيع **مُرحَّلة آجلة** لنفس العميل.
///  • لا يُسمح بتجاوز «المتاح على السند» ولا «المتبقي المتاح للفاتورة» — تُفرض **كمجاميع**
///    داخل معاملة واحدة بعد قفل صف العميل (منع التزامن والتخصيص المكرر).
///  • الإبطال **بالعكس** (صفوف `−1` مرتبطة بالأصل) — لا حذف ولا تعديل مبلغ صف قائم.
///  • كل عملية تُسجَّل في AuditTrail (append-only) داخل نفس المعاملة.
/// </summary>
public partial class ReceiptVoucherService
{
    // ────────────────────────────── قراءة ──────────────────────────────

    /// <summary>حالة تخصيص سند قبض (قراءة) + الفواتير المؤهَّلة — للواجهة والتقارير.</summary>
    public async Task<ReceiptAllocationStateDto?> GetAllocationStateAsync(Guid voucherId, DateTime? asOf = null)
        => await new CustomerAllocationQueries(_context)
            .GetVoucherAllocationStateAsync(voucherId, asOf?.Date ?? DateTime.Today);

    /// <summary>فواتير عميل قابلة للتخصيص حتى تاريخ (قراءة) — تُستخدم في شاشة سند القبض.</summary>
    public async Task<List<CustomerOpenInvoiceDto>> GetEligibleInvoicesAsync(Guid customerId, DateTime? asOf = null)
        => await new CustomerAllocationQueries(_context)
            .GetEligibleInvoicesAsync(customerId, asOf?.Date ?? DateTime.Today);

    /// <summary>
    /// اقتراح FIFO **قراءة فقط** (لا كتابة ولا تنفيذ تلقائي): ترتيب حتمي `InvoiceDate` ثم `Id`،
    /// والتوزيع `min(Available، المتبقي)`، وفرق القرش إلى أكبر سطر لا يتجاوز متاحه.
    /// </summary>
    public async Task<CustomerFifoSuggestionDto?> SuggestFifoAsync(
        Guid voucherId, decimal? amount = null, DateTime? asOf = null)
    {
        var date = asOf?.Date ?? DateTime.Today;
        var voucher = await _context.Set<ReceiptVoucher>().AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == voucherId && !v.IsDeleted);
        if (voucher is null) return null;

        var queries = new CustomerAllocationQueries(_context);
        var netByVoucher = await queries.GetNetAllocatedByVoucherAsync(date);
        var availableOnVoucher = MoneyRounding.Round(voucher.Amount - netByVoucher.GetValueOrDefault(voucherId, 0m));
        if (availableOnVoucher <= 0m)
            return new CustomerFifoSuggestionDto
            {
                ReceiptVoucherId = voucherId, Amount = voucher.Amount,
                SuggestedTotal = 0m, RemainingUnallocated = 0m
            };

        var toDistribute = MoneyRounding.Round(Math.Min(amount ?? availableOnVoucher, availableOnVoucher));
        if (toDistribute <= 0m)
            return new CustomerFifoSuggestionDto
            {
                ReceiptVoucherId = voucherId, Amount = voucher.Amount,
                SuggestedTotal = 0m, RemainingUnallocated = availableOnVoucher
            };

        var invoices = (await queries.GetEligibleInvoicesAsync(voucher.CustomerId, date))
            .Where(i => i.IsEligible)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceId)   // ترتيب FIFO الحتمي
            .ToList();

        var lines = new List<CustomerAllocationInput>();
        var remaining = toDistribute;
        foreach (var inv in invoices)
        {
            if (remaining <= 0m) break;
            var take = MoneyRounding.Round(Math.Min(inv.Available, remaining));
            if (take <= 0m) continue;
            lines.Add(new CustomerAllocationInput { SalesInvoiceId = inv.InvoiceId, Amount = take });
            remaining = MoneyRounding.Round(remaining - take);
        }

        // فرق التقريب: يُضاف إلى أكبر سطر لا يتجاوز متاحه.
        if (remaining > 0m && lines.Count > 0)
        {
            var biggest = lines
                .Select(l => new { Line = l, Invoice = invoices.First(i => i.InvoiceId == l.SalesInvoiceId) })
                .Where(x => x.Invoice.Available - x.Line.Amount >= remaining)
                .OrderByDescending(x => x.Line.Amount).ThenBy(x => x.Line.SalesInvoiceId)
                .FirstOrDefault();
            if (biggest is not null)
            {
                biggest.Line.Amount = MoneyRounding.Round(biggest.Line.Amount + remaining);
                remaining = 0m;
            }
        }

        var suggestedTotal = MoneyRounding.Round(lines.Sum(l => l.Amount));
        return new CustomerFifoSuggestionDto
        {
            ReceiptVoucherId = voucherId,
            Amount = voucher.Amount,
            SuggestedTotal = suggestedTotal,
            RemainingUnallocated = MoneyRounding.Round(availableOnVoucher - suggestedTotal),
            Lines = lines
        };
    }

    // ────────────────────────────── كتابة ──────────────────────────────

    /// <summary>
    /// ضبط تخصيصات سند قبض **مُرحَّل**: يُعكس الصافي القائم (بصفوف `−1`) ثم تُضاف الصفوف الجديدة —
    /// كل ذلك في معاملة واحدة مع تدقيق، فلا يُحذف أي أثر تاريخي.
    /// </summary>
    public Task<ReceiptAllocationStateDto> SetAllocationsAsync(Guid voucherId, SetReceiptAllocationsRequest request)
        => DbTransactionHelper.ExecuteAsync(_context, () => SetAllocationsCoreAsync(voucherId, request));

    private async Task<ReceiptAllocationStateDto> SetAllocationsCoreAsync(
        Guid voucherId, SetReceiptAllocationsRequest request)
    {
        EnsureAllowed(_authorization.CanAllocate(), VoucherRules.Permission(VoucherRules.AllocateAction));

        // منع تكرار الطلب: نفس المفتاح ⇒ لا كتابة ثانية.
        if (request.IdempotencyKey is { } key && await AllocationKeyExistsAsync(key))
            return await StateOrThrowAsync(voucherId);

        var voucher = await LockVoucherAsync(voucherId);
        if (voucher.Status != VoucherStatus.Posted)
            throw new InvalidOperationException(
                $"لا يُخصَّص إلا سند قبض مُرحَّل (الحالة الحالية: {StatusName(voucher.Status)}).");

        // عكس الصافي القائم أولًا حتى يصبح «المتاح» كامل مبلغ السند قبل التحقق من المدخلات الجديدة.
        await ReverseNetAllocationsAsync(voucher, request.Note ?? "استبدال توزيع السند");
        // بعد العكس الكامل، سقف التخصيص هو مبلغ السند نفسه (لا مبلغ السند ناقص التخصيص القائم).
        var inputs = await MergeAndValidateInputsAsync(voucher, request.Allocations, voucherCapOverride: voucher.Amount);
        // G-1: يُثبَّت مفتاح منع التكرار على أول صف مُضاف (كما في مسار العكس ReverseAllocationCoreAsync)
        // حتى يعمل فحص «نفس المفتاح ⇒ لا كتابة ثانية» أعلاه فعليًا عند إعادة الطلب. بدونه كان
        // المفتاح لا يُحفظ أبدًا فيبقى الحارس ميتًا وتتكرر صفوف العكس/الإضافة وسجل التدقيق.
        AddAllocationRows(voucher, inputs, request.Note, request.IdempotencyKey);

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityReceipt, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionAllocationsReplaced,
            $"ضبط تخصيصات السند: {inputs.Count} فاتورة بمجموع {MoneyRounding.Round(inputs.Sum(i => i.Amount)):0.00}",
            CurrentUser());

        await SaveAsync();
        return await StateOrThrowAsync(voucherId);
    }

    /// <summary>عكس صف تخصيص (كلي أو جزئي) — صف `−1` مرتبط بالأصل، مع تدقيق.</summary>
    public Task<ReceiptAllocationStateDto> ReverseAllocationAsync(
        Guid voucherId, ReverseCustomerAllocationRequest request)
        => DbTransactionHelper.ExecuteAsync(_context, () => ReverseAllocationCoreAsync(voucherId, request));

    private async Task<ReceiptAllocationStateDto> ReverseAllocationCoreAsync(
        Guid voucherId, ReverseCustomerAllocationRequest request)
    {
        EnsureAllowed(_authorization.CanReverse(), VoucherRules.Permission(VoucherRules.ReverseAction));

        if (request.IdempotencyKey is { } key && await AllocationKeyExistsAsync(key))
            return await StateOrThrowAsync(voucherId);

        var voucher = await LockVoucherAsync(voucherId);

        var original = await _context.Set<CustomerPaymentAllocation>()
            .FirstOrDefaultAsync(a => a.Id == request.AllocationId && !a.IsDeleted)
            ?? throw new InvalidOperationException("صف التخصيص غير موجود.");
        if (original.ReceiptVoucherId != voucher.Id)
            throw new InvalidOperationException("صف التخصيص لا يتبع هذا السند.");
        if (original.Direction != 1)
            throw new InvalidOperationException("لا يُعكس إلا صف تخصيص أصلي (+1).");

        var alreadyReversed = await _context.Set<CustomerPaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReversalOfAllocationId == original.Id && a.Direction == -1)
            .SumAsync(a => (decimal?)a.Amount) ?? 0m;

        var remainingOnOriginal = MoneyRounding.Round(original.Amount - alreadyReversed);
        if (remainingOnOriginal <= 0m)
            throw new InvalidOperationException("هذا الصف معكوس بالكامل — لا يُعكس مرتين.");

        var amount = MoneyRounding.Round(request.Amount is > 0m ? request.Amount!.Value : remainingOnOriginal);
        if (amount <= 0m)
            throw new InvalidOperationException("مبلغ العكس يجب أن يكون أكبر من صفر.");
        if (amount > remainingOnOriginal)
            throw new InvalidOperationException(
                $"مبلغ العكس ({amount:0.00}) يتجاوز المتبقي من الصف الأصلي ({remainingOnOriginal:0.00}).");

        var reversal = BuildAllocationRow(voucher, original.SalesInvoiceId, amount, direction: -1,
            reversalOf: original.Id,
            note: string.IsNullOrWhiteSpace(request.Note) ? "عكس تخصيص" : request.Note);
        reversal.IdempotencyKey = request.IdempotencyKey;
        _context.Set<CustomerPaymentAllocation>().Add(reversal);

        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityReceipt, voucher.Id, voucher.VoucherNumber,
            VoucherAuditTrailWriter.ActionAllocationReversed,
            $"عكس تخصيص بمبلغ {amount:0.00} على الفاتورة {await InvoiceNumberAsync(original.SalesInvoiceId)} (صف أصلي {original.Id})",
            CurrentUser());

        await SaveAsync();
        return await StateOrThrowAsync(voucherId);
    }

    // ────────────────────────────── أجزاء داخلية ──────────────────────────────

    /// <summary>يقفل صف العميل ثم صف السند داخل المعاملة الجارية ويُعيد السند.</summary>
    private async Task<ReceiptVoucher> LockVoucherAsync(Guid voucherId)
    {
        var voucher = await _context.Set<ReceiptVoucher>()
            .Include(v => v.Customer)
            .FirstOrDefaultAsync(v => v.Id == voucherId && !v.IsDeleted)
            ?? throw new InvalidOperationException("سند القبض غير موجود.");

        // القفل الأول: صف العميل (كل عمليات تخصيص العميل تتحول إلى تسلسل) ثم صف السند.
        await PartyRowLock.LockCustomerAsync(_context, voucher.CustomerId);
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"ReceiptVouchers\" WHERE \"Id\" = {voucherId} FOR UPDATE");
        return voucher;
    }

    private async Task<ReceiptAllocationStateDto> StateOrThrowAsync(Guid voucherId)
        => await GetAllocationStateAsync(voucherId)
           ?? throw new InvalidOperationException("سند القبض غير موجود.");

    private Task<bool> AllocationKeyExistsAsync(Guid key)
        => _context.Set<CustomerPaymentAllocation>().AnyAsync(a => a.IdempotencyKey == key);

    /// <summary>
    /// دمج المدخلات (نفس الفاتورة مرتين ⇒ مجموع) + التحقق الكامل:
    /// مرحَّل/غير محذوف/نفس العميل/فاتورة آجلة/مبالغ صحيحة/عدم تجاوز متاح السند أو متاح الفاتورة.
    /// </summary>
    private async Task<List<CustomerAllocationInput>> MergeAndValidateInputsAsync(
        ReceiptVoucher voucher, IReadOnlyList<CustomerAllocationInput> allocations, decimal? voucherCapOverride = null)
    {
        var merged = allocations
            .Where(a => a.Amount > 0m)
            .GroupBy(a => a.SalesInvoiceId)
            .Select(g => new CustomerAllocationInput
            {
                SalesInvoiceId = g.Key,
                Amount = MoneyRounding.Round(g.Sum(x => x.Amount))
            })
            .ToList();

        if (merged.Count == 0)
            throw new InvalidOperationException("لا توجد تخصيصات صالحة (المبالغ يجب أن تكون أكبر من صفر).");

        foreach (var line in merged)
        {
            if (Math.Abs(MoneyRounding.Round(line.Amount) - line.Amount) > 0.005m)
                throw new InvalidOperationException(
                    $"مبلغ التخصيص {line.Amount} يتجاوز منزلتين عشريتين (سياسة التقريب النقدي).");
        }

        var queries = new CustomerAllocationQueries(_context);
        var netByVoucher = await queries.GetNetAllocatedByVoucherAsync(DateTime.Today);
        var netOnVoucher = netByVoucher.GetValueOrDefault(voucher.Id, 0m);
        var requestTotal = MoneyRounding.Round(merged.Sum(x => x.Amount));
        var availableOnVoucher = voucherCapOverride ?? MoneyRounding.Round(voucher.Amount - netOnVoucher);
        if (requestTotal > availableOnVoucher)
            throw new InvalidOperationException(
                $"إجمالي التخصيص ({requestTotal:0.00}) يتجاوز المبلغ المتاح على السند ({availableOnVoucher:0.00}).");

        var eligible = (await queries.GetEligibleInvoicesAsync(voucher.CustomerId, DateTime.Today))
            .ToDictionary(i => i.InvoiceId);

        foreach (var line in merged)
        {
            if (!eligible.TryGetValue(line.SalesInvoiceId, out var invoice))
                throw new InvalidOperationException("الفاتورة غير موجودة أو لا تخص هذا العميل.");

            if (!invoice.IsEligible)
                throw new InvalidOperationException(
                    $"الفاتورة {invoice.InvoiceNumber} غير مؤهَّلة للتخصيص: {invoice.IneligibilityReason}");

            if (line.Amount > invoice.Available)
                throw new InvalidOperationException(
                    $"مبلغ التخصيص للفاتورة {invoice.InvoiceNumber} ({line.Amount:0.00}) يتجاوز المتبقي المتاح ({invoice.Available:0.00}).");
        }

        return merged;
    }

    /// <summary>يعكس الصافي القائم لسند (بصفوف `−1`) — بلا حذف ولا تعديل للأصل.</summary>
    private async Task ReverseNetAllocationsAsync(ReceiptVoucher voucher, string reason)
    {
        var positives = await _context.Set<CustomerPaymentAllocation>()
            .Where(a => !a.IsDeleted && a.ReceiptVoucherId == voucher.Id && a.Direction == 1)
            .OrderBy(a => a.Id)
            .ToListAsync();
        if (positives.Count == 0) return;

        var reversals = await _context.Set<CustomerPaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReceiptVoucherId == voucher.Id && a.Direction == -1)
            .GroupBy(a => a.ReversalOfAllocationId)
            .Select(g => new { OriginalId = g.Key, Total = g.Sum(x => x.Amount) })
            .ToListAsync();

        foreach (var p in positives)
        {
            var already = reversals.FirstOrDefault(r => r.OriginalId == p.Id)?.Total ?? 0m;
            var net = MoneyRounding.Round(p.Amount - already);
            if (net <= 0m) continue;

            _context.Set<CustomerPaymentAllocation>().Add(
                BuildAllocationRow(voucher, p.SalesInvoiceId, net, direction: -1, reversalOf: p.Id, note: reason));
        }
    }

    private void AddAllocationRows(
        ReceiptVoucher voucher, IEnumerable<CustomerAllocationInput> inputs, string? note,
        Guid? idempotencyKey = null)
    {
        var first = true;
        foreach (var line in inputs)
        {
            var row = BuildAllocationRow(voucher, line.SalesInvoiceId, line.Amount, direction: 1, reversalOf: null, note: note);
            // مفتاح منع التكرار يُثبَّت على أول صف فقط (الفهرس فريد على العمود).
            if (first) { row.IdempotencyKey = idempotencyKey; first = false; }
            _context.Set<CustomerPaymentAllocation>().Add(row);
        }
    }

    private static CustomerPaymentAllocation BuildAllocationRow(
        ReceiptVoucher voucher, Guid invoiceId, decimal amount, short direction, Guid? reversalOf,
        string? note, DateTime? allocatedAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = voucher.CustomerId,
            ReceiptVoucherId = voucher.Id,
            SalesInvoiceId = invoiceId,
            Amount = MoneyRounding.Round(amount),
            Direction = direction,
            ReversalOfAllocationId = reversalOf,
            AllocatedAt = allocatedAt ?? DateTime.Now,
            Note = note,
            CreatedAt = DateTime.UtcNow
        };

    private async Task<string> InvoiceNumberAsync(Guid invoiceId)
        => await _context.Set<SalesInvoice>().AsNoTracking()
               .Where(i => i.Id == invoiceId).Select(i => i.InvoiceNumber).FirstOrDefaultAsync()
           ?? invoiceId.ToString();

    private async Task<List<string>> DescribeAsync(IEnumerable<CustomerAllocationInput> inputs)
    {
        var list = new List<string>();
        foreach (var i in inputs)
            list.Add($"{await InvoiceNumberAsync(i.SalesInvoiceId)} = {i.Amount:0.00}");
        return list;
    }

    /// <summary>تخصيص مباشر (إضافة صفوف) على سند **مُرحَّل** — تُستدعى أيضًا أثناء الترحيل.</summary>
    internal async Task ApplyAllocationsInternalAsync(
        ReceiptVoucher voucher, IReadOnlyList<CustomerAllocationInput> allocations, Guid? idempotencyKey)
    {
        if (allocations.Count == 0) return;
        var inputs = await MergeAndValidateInputsAsync(voucher, allocations);
        AddAllocationRows(voucher, inputs, note: null, idempotencyKey: idempotencyKey);
        await LogAllocationRowsAsync(voucher, VoucherAuditTrailWriter.ActionAllocated,
            "تخصيص السند على " + inputs.Count + " فاتورة بمجموع " +
            $"{MoneyRounding.Round(inputs.Sum(i => i.Amount)):0.00}");
    }

    /// <summary>تخصيص بأسلوب FIFO بعد تأكيد المستخدم (يُسجَّل الترتيب والمبالغ في التدقيق).</summary>
    internal async Task ApplyFifoInternalAsync(
        ReceiptVoucher voucher, IReadOnlyList<CustomerAllocationInput> orderedLines, Guid? idempotencyKey)
    {
        if (orderedLines.Count == 0) return;
        var inputs = await MergeAndValidateInputsAsync(voucher, orderedLines);
        AddAllocationRows(voucher, inputs,
            note: "تخصيص FIFO (بترتيب تاريخ الفاتورة ثم المعرّف)", idempotencyKey: idempotencyKey);
        await LogAllocationRowsAsync(voucher, VoucherAuditTrailWriter.ActionFifoApplied,
            "تطبيق FIFO: " + string.Join(" | ", await DescribeAsync(inputs)));
    }

    private Task LogAllocationRowsAsync(ReceiptVoucher voucher, string action, string details)
    {
        VoucherAuditTrailWriter.Write(
            _context, VoucherAuditTrailWriter.EntityReceipt, voucher.Id, voucher.VoucherNumber,
            action, details, CurrentUser());
        return Task.CompletedTask;
    }
}
