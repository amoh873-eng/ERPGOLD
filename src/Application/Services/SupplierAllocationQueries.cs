using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// PU-04 — مصدر الحقيقة الوحيد لرصيد الفاتورة القابل للتخصيص.
///
/// المعادلة (الفاتورة الآجلة):
///     R = TotalAmount − PaidAmount − ReturnsOnAccount − NetAllocated
/// حيث ReturnsOnAccount = مردودات M1 فقط، وNetAllocated = Σ(+1) − Σ(−1).
/// الفاتورة النقدية: R = 0 دائمًا (غير مؤهلة للتخصيص).
/// </summary>
public sealed class SupplierAllocationQueries
{
    private readonly DbContext _context;

    public SupplierAllocationQueries(DbContext context) => _context = context;

    public async Task<decimal> GetNetAllocatedAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var q = _context.Set<PaymentAllocation>().AsNoTracking()
            .Where(a => a.PurchaseInvoiceId == invoiceId && !a.IsDeleted);

        var plus = await q.Where(a => a.Direction == 1).SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        var minus = await q.Where(a => a.Direction == -1).SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        return plus - minus;
    }

    public async Task<decimal> GetReturnsOnAccountAsync(Guid invoiceId, CancellationToken ct = default)
        => await _context.Set<PurchaseReturn>().AsNoTracking()
            .Where(r => r.PurchaseInvoiceId == invoiceId
                        && !r.IsDeleted
                        && r.Status == DocumentStatus.Posted
                        && r.SettlementType == PurchaseReturnSettlementType.OnAccountCredit)
            .SumAsync(r => (decimal?)r.TotalAmount, ct) ?? 0m;

    /// <summary>رصيد فاتورة واحدة وفق المعادلة المعتمدة.</summary>
    public async Task<SupplierInvoiceResidualDto?> GetResidualAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await _context.Set<PurchaseInvoice>().AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, ct);
        if (invoice is null) return null;
        return await MapAsync(invoice, ct);
    }

    /// <summary>
    /// فواتير المورد مع أرصدتها. onlyEligible = true تُرجع المؤهلة فقط (OnAccount, Posted, R > 0).
    /// الفواتير ذات الرصيد السالب تُعرض بوسم (لا تُخفى) عندما onlyEligible = false.
    /// </summary>
    public async Task<List<SupplierInvoiceResidualDto>> GetInvoicesAsync(
        Guid supplierId, DateTime asOf, bool onlyEligible = false, CancellationToken ct = default)
    {
        var invoices = await _context.Set<PurchaseInvoice>().AsNoTracking()
            .Where(i => i.SupplierId == supplierId
                        && !i.IsDeleted
                        && i.InvoiceDate <= asOf.Date)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceNumber)
            .ToListAsync(ct);

        var result = new List<SupplierInvoiceResidualDto>(invoices.Count);
        foreach (var invoice in invoices)
        {
            var dto = await MapAsync(invoice, ct);
            if (onlyEligible && !dto.IsEligible) continue;
            result.Add(dto);
        }
        return result;
    }

    /// <summary>
    /// اقتراح FIFO (D3): توزيع على أقدم الفواتير المؤهلة بترتيب حتمي،
    /// مع معالجة فرق التقريب (D8) بلا تجاوز المتاح. لا يُعتمد إلا بتأكيد المستخدم.
    /// </summary>
    public async Task<List<PaymentAllocationInput>> SuggestFifoAsync(
        Guid supplierId, decimal amount, DateTime asOf, CancellationToken ct = default)
    {
        if (amount <= 0m) return new List<PaymentAllocationInput>();

        var eligible = (await GetInvoicesAsync(supplierId, asOf, onlyEligible: true, ct))
            .Where(i => i.Available > 0m)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceNumber)
            .ToList();

        var result = new List<PaymentAllocationInput>();
        var remaining = MoneyRounding.Round(amount);

        foreach (var invoice in eligible)
        {
            if (remaining <= 0m) break;
            var take = MoneyRounding.Round(Math.Min(invoice.Available, remaining));
            if (take <= 0m) continue;
            result.Add(new PaymentAllocationInput { PurchaseInvoiceId = invoice.InvoiceId, Amount = take });
            remaining = MoneyRounding.Round(remaining - take);
        }

        // D8: القرش المتبقي يُضاف إلى أكبر سطر (ترتيب حتمي) بشرط ألا يتجاوز المتاح.
        if (remaining > 0m && result.Count > 0)
        {
            var target = result
                .Select((a, index) => new { Allocation = a, Index = index })
                .OrderByDescending(x => x.Allocation.Amount).ThenBy(x => x.Index)
                .First();
            var invoice = eligible.First(i => i.InvoiceId == target.Allocation.PurchaseInvoiceId);
            if (invoice.Available >= MoneyRounding.Round(target.Allocation.Amount + remaining))
                target.Allocation.Amount = MoneyRounding.Round(target.Allocation.Amount + remaining);
        }

        return result;
    }

    /// <summary>مجموع المتبقي (ΣR) لكل فواتير المورد الآجلة — قد يكون سالبًا ولا يُصفَّر.</summary>
    public async Task<decimal> GetSupplierResidualTotalAsync(Guid supplierId, DateTime asOf, CancellationToken ct = default)
    {
        var invoices = await GetInvoicesAsync(supplierId, asOf, onlyEligible: false, ct);
        return invoices.Where(i => i.InvoiceType == (int)PurchaseInvoiceType.OnAccount)
            .Sum(i => i.Residual);
    }

    private async Task<SupplierInvoiceResidualDto> MapAsync(PurchaseInvoice invoice, CancellationToken ct)
    {
        var returns = await GetReturnsOnAccountAsync(invoice.Id, ct);
        var net = await GetNetAllocatedAsync(invoice.Id, ct);

        var isCash = invoice.InvoiceType == PurchaseInvoiceType.Cash;
        var residual = isCash
            ? 0m
            : MoneyRounding.Round(invoice.TotalAmount - invoice.PaidAmount - returns - net);

        var eligible = !isCash
                       && invoice.Status == DocumentStatus.Posted
                       && !invoice.IsDeleted
                       && residual > 0m;

        string? reason = null;
        if (isCash) reason = "فاتورة نقدية — لا يوجد رصيد مستحق قابل للتخصيص.";
        else if (invoice.Status == DocumentStatus.Cancelled) reason = "فاتورة ملغاة.";
        else if (invoice.Status == DocumentStatus.Draft) reason = "فاتورة غير مرحّلة.";
        else if (residual < 0m) reason = "رصيد دائن/مطالبة — يحتاج مراجعة أو تسوية معتمدة (لا يُخصَّص).";
        else if (residual == 0m) reason = "لا يوجد رصيد متبقٍ.";

        return new SupplierInvoiceResidualDto
        {
            InvoiceId = invoice.Id,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            DueDate = invoice.DueDate,
            InvoiceType = (int)invoice.InvoiceType,
            TotalAmount = invoice.TotalAmount,
            PaidAmount = invoice.PaidAmount,
            ReturnsOnAccount = MoneyRounding.Round(returns),
            NetAllocated = MoneyRounding.Round(net),
            Residual = residual,
            Available = residual > 0m ? residual : 0m,
            IsEligible = eligible,
            IneligibilityReason = eligible ? null : reason
        };
    }

    /// <summary>
    /// PU-04 (M-3) — **فحص اتساق التخصيصات** (قراءة فقط، لا يعدّل شيئًا ولا يُسوّي فرقًا).
    ///
    /// الأنواع الأربعة المفحوصة — منقولة من R3 §9.1 («صفوف شاذة»):
    ///  1) <see cref="AllocationAnomalyKinds.SumMismatch"/>: سند بحالة `Posted` لا يساوي مجموع
    ///     تخصيصاته الصافية مبلغَه. (السند `Reversed` صافي تخصيصاته = 0 بحكم العكس الكامل ⇒ ليس شذوذًا.)
    ///  2) <see cref="AllocationAnomalyKinds.ExceedsAvailable"/>: مجموع التخصيصات الموجبة لفاتورة
    ///     يتجاوز متاحها `Available = max(0, R)` (نفس مقياس الحارس في مسار الترحيل).
    ///  3) <see cref="AllocationAnomalyKinds.MissingVoucher"/>: صف تخصيص بلا سند أو بسند محذوف منطقيًا.
    ///  4) <see cref="AllocationAnomalyKinds.MissingInvoice"/>: صف تخصيص بفاتورة غير موجودة أو محذوفة منطقيًا.
    ///
    /// ملاحظة مقصودة: الاستعلام يتجاوز مرشّح «غير محذوف» العام **لكشف** الحذف المنطقي
    /// (المطلوب في النص: «فاتورة محذوفة») بدل إخفائه.
    /// </summary>
    public async Task<List<AllocationAnomalyDto>> GetConsistencyAnomaliesAsync(CancellationToken ct = default)
    {
        var anomalies = new List<AllocationAnomalyDto>();

        var rows = await _context.Set<PaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted)
            .Select(a => new { a.PaymentVoucherId, a.PurchaseInvoiceId, a.Amount, a.Direction })
            .ToListAsync(ct);

        if (rows.Count == 0) return anomalies;

        // ── السندات (مع تجاهل المرشّح العام لكشف المحذوف منطقيًا) ──
        var voucherIds = rows.Select(r => r.PaymentVoucherId).Distinct().ToList();
        var vouchers = await _context.Set<PaymentVoucher>().IgnoreQueryFilters().AsNoTracking()
            .Where(v => voucherIds.Contains(v.Id))
            .Select(v => new { v.Id, v.VoucherNumber, v.Status, v.Amount, v.IsDeleted })
            .ToListAsync(ct);
        var voucherById = vouchers.ToDictionary(v => v.Id);

        // ── (1) ΣA ≠ Amount على السند المُرحَّل ──
        foreach (var group in rows.GroupBy(r => r.PaymentVoucherId))
        {
            if (!voucherById.TryGetValue(group.Key, out var voucher) || voucher.IsDeleted) continue;
            if (voucher.Status != VoucherStatus.Posted) continue;

            var net = MoneyRounding.Round(
                group.Where(x => x.Direction == 1).Sum(x => x.Amount)
                - group.Where(x => x.Direction == -1).Sum(x => x.Amount));

            if (net != MoneyRounding.Round(voucher.Amount))
                anomalies.Add(new AllocationAnomalyDto
                {
                    Kind = AllocationAnomalyKinds.SumMismatch,
                    Description = $"مجموع تخصيصات السند ({net:N2}) لا يساوي مبلغ السند ({voucher.Amount:N2}).",
                    PaymentVoucherId = voucher.Id,
                    VoucherNumber = voucher.VoucherNumber,
                    Actual = net,
                    Expected = MoneyRounding.Round(voucher.Amount)
                });
        }

        // ── (3) صف بلا سند (غير موجود أو محذوف منطقيًا) ──
        foreach (var row in rows)
        {
            if (!voucherById.TryGetValue(row.PaymentVoucherId, out var voucher))
                anomalies.Add(new AllocationAnomalyDto
                {
                    Kind = AllocationAnomalyKinds.MissingVoucher,
                    Description = "صف تخصيص يشير إلى سند صرف غير موجود.",
                    PaymentVoucherId = row.PaymentVoucherId,
                    PurchaseInvoiceId = row.PurchaseInvoiceId,
                    Actual = MoneyRounding.Round(row.Amount)
                });
            else if (voucher.IsDeleted)
                anomalies.Add(new AllocationAnomalyDto
                {
                    Kind = AllocationAnomalyKinds.MissingVoucher,
                    Description = "صف تخصيص يشير إلى سند صرف محذوف منطقيًا.",
                    PaymentVoucherId = voucher.Id,
                    VoucherNumber = voucher.VoucherNumber,
                    PurchaseInvoiceId = row.PurchaseInvoiceId,
                    Actual = MoneyRounding.Round(row.Amount)
                });
        }

        // ── الفواتير (مع تجاهل المرشّح العام لكشف المحذوف منطقيًا) ──
        var invoiceIds = rows.Select(r => r.PurchaseInvoiceId).Distinct().ToList();
        var invoices = await _context.Set<PurchaseInvoice>().IgnoreQueryFilters().AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.InvoiceNumber, i.IsDeleted })
            .ToListAsync(ct);
        var invoiceById = invoices.ToDictionary(i => i.Id);

        // ── (4) صف بلا فاتورة (غير موجودة أو محذوفة) و(2) تجاوز المتاح ──
        foreach (var group in rows.GroupBy(r => r.PurchaseInvoiceId))
        {
            if (!invoiceById.TryGetValue(group.Key, out var invoice))
            {
                anomalies.Add(new AllocationAnomalyDto
                {
                    Kind = AllocationAnomalyKinds.MissingInvoice,
                    Description = "صف تخصيص يشير إلى فاتورة مشتريات غير موجودة.",
                    PurchaseInvoiceId = group.Key,
                    Actual = MoneyRounding.Round(group.Where(x => x.Direction == 1).Sum(x => x.Amount))
                });
                continue;
            }

            if (invoice.IsDeleted)
            {
                anomalies.Add(new AllocationAnomalyDto
                {
                    Kind = AllocationAnomalyKinds.MissingInvoice,
                    Description = "صف تخصيص يشير إلى فاتورة مشتريات محذوفة منطقيًا.",
                    PurchaseInvoiceId = invoice.Id,
                    InvoiceNumber = invoice.InvoiceNumber,
                    Actual = MoneyRounding.Round(group.Where(x => x.Direction == 1).Sum(x => x.Amount))
                });
                continue;
            }

            var positiveTotal = MoneyRounding.Round(group.Where(x => x.Direction == 1).Sum(x => x.Amount));
            var negativeTotal = MoneyRounding.Round(group.Where(x => x.Direction == -1).Sum(x => x.Amount));
            if (positiveTotal <= 0m) continue;

            // مقياس الحارس في مسار الترحيل هو المتاح **قبل** تخصيصات هذه المعاملة:
            // `availableBefore = max(0, R) + N` حيث `R` من `GetResidualAsync` يشمل التخصيصات المسجَّلة.
            // لذلك تُقارَن التخصيصات الموجبة بالمتاح قبلها، لا بالمتاح بعدها
            // (وإلا لأُبلغ عن كل فاتورة سُدِّدت بالكامل كمخالفة).
            var residual = await GetResidualAsync(invoice.Id, ct);
            var availableBefore = MoneyRounding.Round((residual?.Residual ?? 0m) + positiveTotal - negativeTotal);

            if (positiveTotal > availableBefore)
                anomalies.Add(new AllocationAnomalyDto
                {
                    Kind = AllocationAnomalyKinds.ExceedsAvailable,
                    Description = $"مجموع تخصيصات الفاتورة ({positiveTotal:N2}) يتجاوز المتاح قبل التخصيص ({availableBefore:N2}).",
                    PurchaseInvoiceId = invoice.Id,
                    InvoiceNumber = invoice.InvoiceNumber,
                    Actual = positiveTotal,
                    Expected = availableBefore
                });
        }

        return anomalies;
    }
}
