using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// JournalEntryService — إنشاء وحفظ القيد اليدوي (Manual Entry).
///
/// إضافة 2026-10-25 — **إسناد الطرف** (اختياري) لمنع انفصال الحسابات الرقابية عن السجل المساعد:
///   • بند على 1200 يمكن إسناده لعميل، وبند على 2200 يمكن إسناده لمورد.
///   • عند الإسناد يُزامن الرصيد التفصيلي مع الرصيد الرقابي **في نفس وحدة العمل** وبقفل صفّ
///     الطرف ⇒ لا فرق بين 1200 ومجموع أرصدة العملاء (وكذلك 2200).
///   • القيد بلا إسناد يبقى **مسموحًا** (قيود تصحيحية مشروعة، وسلوك موثَّق قائم)، ويُعرض أثره
///     صريحًا في تقرير المطابقة (بند «غير مُنسَب») — لا حظر ولا تسوية تلقائية.
///   • محاولة إسناد غير مطابق (عميل على 2200، أو طرف على حساب غير رقابي) تُرفض قبل أي كتابة.
/// </summary>
public partial class JournalEntryService
{
    /// <summary>حساب العملاء الرقابي — بنوده تقبل إسناد عميل.</summary>
    private const string ReceivableAccountCode = "1200";

    /// <summary>حساب الموردين الرقابي — بنوده تقبل إسناد مورد.</summary>
    private const string PayableAccountCode = "2200";

    public async Task<JournalEntryDto> CreateManualEntryAsync(
        DateTime entryDate,
        string? reference,
        string? description,
        IReadOnlyCollection<JournalEntryLegInput> legs)
    {
        // التحقق من الإسناد **قبل** أي كتابة (فشل مبكر برسالة واضحة، بلا قيد جزئي).
        await ValidatePartyAttributionAsync(legs);

        JournalEntry? entry = null;

        // معاملة واحدة: القيد + بنوده + أرصدة الحسابات + أرصدة الأطراف المُسندة — كلها أو لا شيء.
        await DbTransactionHelper.ExecuteAsync(_context, async () =>
        {
            // PrepareEntryAsync يتحقق من التاريخ/الفترة/التوازن/الحسابات المتحركة ويحدّث أرصدة الحسابات.
            entry = await PrepareEntryAsync(
                JournalEntryType.Manual, entryDate, reference, description, legs);

            // مزامنة الأرصدة التفصيلية للأطراف المُسندة (قفل صفّ الطرف + قراءة طازجة).
            await ApplyPartyAttributionAsync(legs);

            await _context.SaveChangesAsync();
        });

        // نعيد قراءة القيد مع أسماء الحسابات لعرضه في الواجهة.
        var dto = await GetByIdAsync(entry!.Id);
        return dto ?? throw new InvalidOperationException("فشل إنشاء القيد.");
    }

    /// <summary>
    /// قواعد الإسناد: طرف واحد لكل بند · العميل على 1200 فقط · المورد على 2200 فقط ·
    /// الطرف يجب أن يكون موجودًا وغير محذوف. أي مخالفة ⇒ رفض صريح قبل الكتابة.
    /// </summary>
    private async Task ValidatePartyAttributionAsync(IReadOnlyCollection<JournalEntryLegInput>? legs)
    {
        if (legs is null || legs.Count == 0 || legs.All(l => l.CustomerId is null && l.SupplierId is null))
            return;

        var accountIds = legs.Where(l => l.CustomerId is not null || l.SupplierId is not null)
            .Select(l => l.AccountId).Distinct().ToList();

        var codes = await _context.Set<Account>().AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Code })
            .ToListAsync();

        var codeById = codes.ToDictionary(x => x.Id, x => x.Code);

        foreach (var leg in legs)
        {
            if (leg.CustomerId is null && leg.SupplierId is null)
                continue;

            if (leg.CustomerId is not null && leg.SupplierId is not null)
                throw new InvalidOperationException(
                    "لا يمكن إسناد البند إلى عميل ومورد معًا — بند واحد يحمل طرفًا واحدًا فقط.");

            var code = codeById.TryGetValue(leg.AccountId, out var c) ? c : "—";

            if (leg.CustomerId is not null && !string.Equals(code, ReceivableAccountCode, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"إسناد العميل مقبول على حساب العملاء ({ReceivableAccountCode}) فقط، والبند على الحساب '{code}'.");

            if (leg.SupplierId is not null && !string.Equals(code, PayableAccountCode, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"إسناد المورد مقبول على حساب الموردين ({PayableAccountCode}) فقط، والبند على الحساب '{code}'.");

            if (leg.CustomerId is Guid customerId
                && !await _context.Set<Customer>().AnyAsync(x => x.Id == customerId && !x.IsDeleted))
                throw new InvalidOperationException("العميل المُسند إلى البند غير موجود في سجل العملاء.");

            if (leg.SupplierId is Guid supplierId
                && !await _context.Set<Supplier>().AnyAsync(x => x.Id == supplierId && !x.IsDeleted))
                throw new InvalidOperationException("المورد المُسند إلى البند غير موجود في سجل الموردين.");
        }
    }

    /// <summary>يطبّق دلتا الطرف لكل بند مُسند (مصدر واحد: <see cref="PartyBalanceSync"/>).</summary>
    private async Task ApplyPartyAttributionAsync(IReadOnlyCollection<JournalEntryLegInput> legs)
    {
        foreach (var leg in legs)
        {
            if (leg.CustomerId is Guid customerId)
                await PartyBalanceSync.ApplyCustomerDeltaAsync(
                    _context, customerId, PartyBalanceSync.CustomerDelta(leg.DebitAmount, leg.CreditAmount));

            if (leg.SupplierId is Guid supplierId)
                await PartyBalanceSync.ApplySupplierDeltaAsync(
                    _context, supplierId, PartyBalanceSync.SupplierDelta(leg.DebitAmount, leg.CreditAmount));
        }
    }
}