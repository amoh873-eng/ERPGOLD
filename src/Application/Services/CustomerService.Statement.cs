using ERPSystem.Application.DTOs.Customers;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// CustomerService — الجزء الثالث: كشف حساب العميل + أدوات التحويل.
///
/// SUB-2b/P3 (قرار #7): الكشف يعرض فواتير البيع والمردودات وسندات القبض وسندات رد الأموال
/// ومستندات عكس الرد — أي «حركة مستندية كاملة» تؤثر في ذمة العميل. لا يساوي رصيد 1200
/// تلقائيًا لأن الكشف لا يشمل القيود اليدوية/العهد/التسويات خارج المستندات.
/// </summary>
public partial class CustomerService
{
    public async Task<CustomerStatementDto> GetStatementAsync(Guid customerId, DateTime from, DateTime to)
    {
        var customer = await _context.Set<Customer>()
            .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted);
        if (customer is null)
            throw new InvalidOperationException("العميل غير موجود");

        var invoices = await _context.Set<SalesInvoice>()
            .Where(i => i.CustomerId == customerId
                        && i.Status == DocumentStatus.Posted
                        && i.InvoiceDate.Date >= from.Date
                        && i.InvoiceDate.Date <= to.Date)
            .OrderBy(i => i.InvoiceDate)
            .ToListAsync();

        var returns = await _context.Set<SalesReturn>()
            .Where(r => r.CustomerId == customerId
                        && r.Status == DocumentStatus.Posted
                        && r.ReturnDate.Date >= from.Date
                        && r.ReturnDate.Date <= to.Date)
            .OrderBy(r => r.ReturnDate)
            .ToListAsync();

        // SUB-2b/P3 (قرار #7) — الكشف يعرض **كل الحركات المؤثرة في الذمة**:
        // سندات القبض (دائن) وسندات رد الأموال (مدين) ومستندات عكس الرد (دائن)،
        // وإلا ظهر مستحق للعميل بعد تسويته. هذا عرض مستندي كامل ولا يساوي 1200 تلقائيًا
        // (قد توجد قيود/عهد خارج الكشف) — التنبيه موثّق في ترويسة هذا الملف.
        var receipts = await _context.Set<ReceiptVoucher>()
            .Where(r => r.CustomerId == customerId
                        && r.Status == VoucherStatus.Posted
                        && r.VoucherDate.Date >= from.Date
                        && r.VoucherDate.Date <= to.Date)
            .Select(r => new { r.VoucherDate, r.VoucherNumber, r.Amount })
            .ToListAsync();

        // عكس سند الرد يجعل حالة السند `Reversed` (مع بقاء قيده الأصلي قائمًا في الأستاذ) — ولو قُصِر
        // الكشف على `Posted` لاختفى **المدين** وبقي **الدائن** (مستند العكس) ⇒ انحراف الرصيد الختامي
        // عن الأستاذ. لذلك يُدرج السند بحالتي `Posted` و`Reversed` معًا، ويظل التناظر قائمًا مع صف العكس.
        // (اكتُشف بفحص واجهي فعلي في المرحلة الرابعة: كشف أظهر عكوسًا 40+30+10+20 دائنًا بلا سنداتها المدينة.)
        var refunds = await _context.Set<PaymentVoucher>()
            .Where(v => v.CustomerId == customerId
                        && (v.Status == VoucherStatus.Posted || v.Status == VoucherStatus.Reversed)
                        && v.VoucherDate.Date >= from.Date
                        && v.VoucherDate.Date <= to.Date)
            .Select(v => new { v.VoucherDate, v.VoucherNumber, v.Amount })
            .ToListAsync();

        var refundReversals = await _context.Set<PaymentVoucherReversal>()
            .Where(r => r.CustomerId == customerId
                        && r.Status == VoucherStatus.Posted
                        && r.VoucherDate.Date >= from.Date
                        && r.VoucherDate.Date <= to.Date)
            .Select(r => new { r.VoucherDate, r.ReversalNumber, r.Amount })
            .ToListAsync();

        // نمرّر العمليات مرتبة حسب التاريخ والوقت، ونحدّث الرصيد الجاري
        var operations = new List<(DateTime Date, int Seq, string FNumber, string TypeNameAr, decimal Debit, decimal Credit)>();

        operations.AddRange(invoices.Select(i => (
            i.InvoiceDate, 0, i.InvoiceNumber, "فاتورة بيع", i.TotalAmount, 0m)));

        operations.AddRange(returns.Select(r => (
            r.ReturnDate, 1, r.ReturnNumber, "مردود مبيعات", 0m, r.TotalAmount)));

        operations.AddRange(receipts.Select(r => (
            r.VoucherDate, 2, r.VoucherNumber, "سند قبض", 0m, r.Amount)));

        operations.AddRange(refunds.Select(v => (
            v.VoucherDate, 3, v.VoucherNumber, "رد مبلغ عميل", v.Amount, 0m)));

        operations.AddRange(refundReversals.Select(r => (
            r.VoucherDate, 4, r.ReversalNumber, "عكس رد مبلغ عميل", 0m, r.Amount)));

        var ordered = operations
            .OrderBy(o => o.Date)
            .ThenBy(o => o.Seq)
            .ThenBy(o => o.FNumber);

        var lines = new List<CustomerStatementLineDto>();
        var running = 0m;

        foreach (var op in ordered)
        {
            running += op.Debit - op.Credit;
            lines.Add(new CustomerStatementLineDto
            {
                Date = op.Date,
                DocumentNumber = op.FNumber,
                TypeNameAr = op.TypeNameAr,
                Debit = op.Debit,
                Credit = op.Credit,
                Balance = Math.Round(running, 2)
            });
        }

        return new CustomerStatementDto
        {
            CustomerId = customer.Id,
            CustomerCode = customer.Code,
            CustomerName = customer.NameAr,
            From = from,
            To = to,
            TotalDebit = Math.Round(lines.Sum(l => l.Debit), 2),
            TotalCredit = Math.Round(lines.Sum(l => l.Credit), 2),
            ClosingBalance = Math.Round(running, 2),
            Lines = lines
        };
    }

    private static CustomerDto MapToDto(Customer customer)
    {
        return new CustomerDto
        {
            Id = customer.Id,
            Code = customer.Code,
            NameAr = customer.NameAr,
            NameEn = customer.NameEn,
            Phone = customer.Phone,
            Email = customer.Email,
            Address = customer.Address,
            TaxNumber = customer.TaxNumber,
            CreditLimit = customer.CreditLimit,
            CurrentBalance = customer.CurrentBalance,
            IsActive = customer.IsActive,
            IsSystem = customer.IsSystem,
            Notes = customer.Notes
        };
    }
}