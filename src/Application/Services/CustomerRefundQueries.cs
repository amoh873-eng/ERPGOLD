using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// SUB-2b/P3 — استعلامات قراءة فقط لأهلية رد أموال العملاء (بلا أي قفل أو كتابة).
///
/// القاعدة المحاسبية: لا يُرد إلا ما نشأ عن مردود مبيعات مرحّل على فاتورة **آجلة** أو **شيك**،
/// لأن مردود النقد/الدرج/البطاقة عُوّض وقته في وسيلة الدفع الأصلية (SalesReturnService.Create.cs:163-172).
/// السقف الكلي = min(الرصيد الدائن للعميل، Σ المتبقي القابل للرد لكل مردود).
/// الحارس الحاسم يبقى داخل معاملة الترحيل (هذه الاستعلامات للعرض فقط — منع TOCTOU).
/// </summary>
public sealed class CustomerRefundQueries
{
    private readonly DbContext _context;

    public CustomerRefundQueries(DbContext context) => _context = context;

    /// <summary>الرصيد التشغيلي الحالي للعميل (قراءة بلا قفل).</summary>
    public async Task<decimal> GetCreditBalanceAsync(Guid customerId)
    {
        var balance = await _context.Set<Customer>()
            .AsNoTracking()
            .Where(c => c.Id == customerId && !c.IsDeleted)
            .Select(c => (decimal?)c.CurrentBalance)
            .FirstOrDefaultAsync();

        if (balance is null)
            throw new InvalidOperationException("العميل غير موجود.");

        return balance.Value < 0m ? MoneyRounding.Round(-balance.Value) : 0m;
    }

    /// <summary>ملخص كامل لأهلية الرد (الرصيد + المردودات المؤهلة) — استعلامات مجمّعة بلا N+1.</summary>
    public async Task<CustomerRefundAvailabilityDto> GetAvailabilityAsync(Guid customerId)
    {
        var customer = await _context.Set<Customer>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted)
            ?? throw new InvalidOperationException("العميل غير موجود.");

        return new CustomerRefundAvailabilityDto
        {
            CustomerId = customer.Id,
            CurrentBalance = customer.CurrentBalance,
            CreditBalance = customer.CurrentBalance < 0m ? MoneyRounding.Round(-customer.CurrentBalance) : 0m,
            Returns = await GetRefundableReturnsAsync(customerId)
        };
    }

    /// <summary>
    /// المردودات المرحّلة المؤهلة للرد مع المتبقي القابل للرد لكل منها.
    /// تُستبعد المردودات التي لا أصل آجل/شيك لها، وتُستبعد السندات غير المرحّلة (لا أثر مالي).
    /// </summary>
    public async Task<List<CustomerRefundableReturnDto>> GetRefundableReturnsAsync(Guid customerId)
    {
        var returns = await (
            from r in _context.Set<SalesReturn>().AsNoTracking()
            join i in _context.Set<SalesInvoice>().AsNoTracking() on r.SalesInvoiceId equals i.Id
            where r.CustomerId == customerId
                  && !r.IsDeleted
                  && r.Status == DocumentStatus.Posted
                  && i.Status == DocumentStatus.Posted
                  && (i.InvoiceType == SalesInvoiceType.OnAccount || i.PaymentMethod == SalesPaymentMethod.Check)
            orderby r.ReturnDate descending, r.ReturnNumber descending
            select new
            {
                r.Id,
                r.ReturnNumber,
                r.ReturnDate,
                InvoiceNumber = i.InvoiceNumber,
                r.TotalAmount,
                InvoiceType = i.InvoiceType,
                InvoicePaymentMethod = i.PaymentMethod
            }).ToListAsync();

        if (returns.Count == 0)
            return new List<CustomerRefundableReturnDto>();

        var ids = returns.Select(r => r.Id).ToList();

        // مجموع الردود المرحّلة فقط (المعكوس خارج المجموع ⇒ يعود قابلًا للرد — القاعدة المعتمدة).
        var refunded = await _context.Set<PaymentVoucher>()
            .AsNoTracking()
            .Where(v => v.SalesReturnId != null
                        && ids.Contains(v.SalesReturnId.Value)
                        && v.Status == VoucherStatus.Posted
                        && !v.IsDeleted)
            .GroupBy(v => v.SalesReturnId!.Value)
            .Select(g => new { ReturnId = g.Key, Total = g.Sum(v => v.Amount) })
            .ToDictionaryAsync(x => x.ReturnId, x => x.Total);

        return returns.Select(r =>
        {
            refunded.TryGetValue(r.Id, out var done);
            var remaining = MoneyRounding.Round(r.TotalAmount - done);
            return new CustomerRefundableReturnDto
            {
                ReturnId = r.Id,
                ReturnNumber = r.ReturnNumber,
                ReturnDate = r.ReturnDate,
                InvoiceNumber = r.InvoiceNumber,
                TotalAmount = r.TotalAmount,
                RefundedAmount = MoneyRounding.Round(done),
                RemainingAmount = remaining < 0m ? 0m : remaining,
                InvoiceType = r.InvoiceType.ToString(),
                InvoicePaymentMethod = r.InvoicePaymentMethod.ToString()
            };
        }).ToList();
    }

    /// <summary>مردود واحد مؤهل — يُستخدم للعرض في نموذج الإنشاء؛ الترحيل يعيد التحقق بعد القفل.</summary>
    public async Task<CustomerRefundableReturnDto?> GetRefundableReturnAsync(Guid returnId, Guid customerId)
    {
        var all = await GetRefundableReturnsAsync(customerId);
        return all.FirstOrDefault(r => r.ReturnId == returnId);
    }
}
