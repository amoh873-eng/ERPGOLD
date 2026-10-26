using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// مزامنة **الرصيد التفصيلي** للطرف (عميل/مورد) مع الرصيد الرقابي في الأستاذ — مصدر واحد
/// لكل المسارات التي تُغيّر رصيد طرف مقابل حساب رقابي (1200/2200):
///   • الأرصدة الافتتاحية المُنسَبة إلى طرف (OpeningBalanceService).
///   • القيود اليدوية المُسندة إلى طرف (JournalEntryService.CreateManualEntryAsync).
///
/// لماذا قفل + إعادة تحميل؟ الرصيد التفصيلي عمود مخزَّن يُقرأ ثم يُكتب؛ بلا قفل صفّ
/// (<c>SELECT … FOR UPDATE</c>) تضيع تحديثات متزامنة (lost update) فينفصل الرقابي عن التفصيلي.
/// ويجب أن تُستدعى داخل معاملة مفتوحة (القفل يبقى حتى الـcommit).
///
/// الاصطلاح: `دلتا` موجبة = زيادة **بطبيعة الطرف** (عميل مدين / مورد دائن).
/// </summary>
internal static class PartyBalanceSync
{
    /// <summary>يطبّق دلتا على رصيد العميل (بالقفل ثم القراءة الطازجة) ويقاربها نقديًا.</summary>
    public static async Task ApplyCustomerDeltaAsync(DbContext context, Guid customerId, decimal delta)
    {
        await PartyRowLock.LockCustomerAsync(context, customerId);

        var customer = await context.Set<Customer>().FirstAsync(c => c.Id == customerId);
        await context.Entry(customer).ReloadAsync();

        customer.CurrentBalance = MoneyRounding.Round(customer.CurrentBalance + delta);
        customer.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>يطبّق دلتا على رصيد المورد (بالقفل ثم القراءة الطازجة) ويقاربها نقديًا.</summary>
    public static async Task ApplySupplierDeltaAsync(DbContext context, Guid supplierId, decimal delta)
    {
        await PartyRowLock.LockSupplierAsync(context, supplierId);

        var supplier = await context.Set<Supplier>().FirstAsync(s => s.Id == supplierId);
        await context.Entry(supplier).ReloadAsync();

        supplier.CurrentBalance = MoneyRounding.Round(supplier.CurrentBalance + delta);
        supplier.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>دلتا العميل من مدين/دائن البند: `مدين − دائن` (طبيعته مدينة).</summary>
    public static decimal CustomerDelta(decimal debit, decimal credit) => debit - credit;

    /// <summary>دلتا المورد من مدين/دائن البند: `دائن − مدين` (طبيعته دائنة).</summary>
    public static decimal SupplierDelta(decimal debit, decimal credit) => credit - debit;
}
