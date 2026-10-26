using ERPSystem.Application.DTOs.Accounting;
using ERPSystem.Application.Interfaces;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// مطابقة الحسابات الرقابية مع السجلات المساعدة — **قراءة فقط بلا أي تسوية**.
///
/// ما يقيسه:
///  • <b>1200 العملاء</b>: الرصيد الرقابي من دفتر القيود مقابل Σ أرصدة العملاء التفصيلية.
///  • <b>2200 الموردون</b>: نفس المنهج مقابل Σ أرصدة الموردين التفصيلية.
///  • <b>1300 المخزون</b>: كتلة الأرصدة الافتتاحية المخزنية (قيمة القيود مقابل قيمة الأسطر
///    ومجموع حركات المخزون الافتتاحية) — صف **إعلامي** لأن تقييم المخزون الكامل يحتاج سياسة تقييم معتمدة.
///
/// ما تعنيه القيم:
///  • الفرق = الرقابي − التفصيلي.
///  • المُفسَّر = صافي أثر القيود **غير المستندية** على الحساب (مثل قيد يدوي) — وهي المصدر المتوقع للفرق.
///  • غير المُفسَّر = الفرق − المُفسَّر ⇒ يجب أن يكون 0.00؛ أي قيمة أخرى = موضع تحقيق (لا تسوية).
/// </summary>
public class SubLedgerReconciliationService : ISubLedgerReconciliationService
{
    private const string ReceivableAccount = "1200";
    private const string PayableAccount = "2200";
    private const string InventoryAccount = "1300";

    /// <summary>أنواع القيود المستندية: أثرها على الرقابي يُقابل في السجل المساعد تلقائيًا.</summary>
    private static readonly HashSet<JournalEntryType> DocumentDriven = new()
    {
        JournalEntryType.SalesInvoice,
        JournalEntryType.SalesReturn,
        JournalEntryType.ReceiptVoucher,
        JournalEntryType.PaymentVoucher,
        JournalEntryType.CustomerRefund,
        JournalEntryType.PurchaseInvoice,
        JournalEntryType.PurchaseReturn
    };

    private readonly DbContext _context;

    public SubLedgerReconciliationService(DbContext context) => _context = context;

    public async Task<SubLedgerReconciliationDto> GetReportAsync()
    {
        var report = new SubLedgerReconciliationDto { GeneratedAt = DateTime.UtcNow };

        report.Rows.Add(await BuildPartyRowAsync(
            ReceivableAccount,
            "أرصدة العملاء التفصيلية (Customers.CurrentBalance)",
            async () => await _context.Set<Customer>().Where(c => !c.IsDeleted)
                .SumAsync(c => (decimal?)c.CurrentBalance) ?? 0m,
            async () => await _context.Set<Customer>().CountAsync(c => !c.IsDeleted && c.CurrentBalance != 0m),
            customerSide: true,
            unattributedSink: report.UnattributedControlLines));

        report.Rows.Add(await BuildPartyRowAsync(
            PayableAccount,
            "أرصدة الموردين التفصيلية (Suppliers.CurrentBalance)",
            async () => await _context.Set<Supplier>().Where(s => !s.IsDeleted)
                .SumAsync(s => (decimal?)s.CurrentBalance) ?? 0m,
            async () => await _context.Set<Supplier>().CountAsync(s => !s.IsDeleted && s.CurrentBalance != 0m),
            customerSide: false,
            unattributedSink: report.UnattributedControlLines));

        report.Rows.Add(await BuildInventoryOpeningRowAsync());

        report.TotalUnattributedAmount = MoneyRounding.Round(
            report.UnattributedControlLines.Sum(l => Math.Abs(l.SignedAmount)));

        report.Notes.Add("التقرير قراءة فقط ولا يسوّي أي فرق: يعرض الرقابي والتفصيلي والفرق ومصدره.");
        report.Notes.Add("الفروق المُفسَّرة مصدرها قيود غير مستندية (يدوية/افتتاحية بلا تفصيل) على الحساب الرقابي.");
        report.Notes.Add("تقييم المخزون الكامل (Σ كمية × تكلفة لكل الحركات) مقابل 1300 يحتاج سياسة تقييم معتمدة — الصف 1300 إعلامي.");

        return report;
    }

    // ─────────────────────────── صفوف الأطراف ───────────────────────────

    private async Task<SubLedgerReconciliationRowDto> BuildPartyRowAsync(
        string accountCode,
        string detailSourceAr,
        Func<Task<decimal>> detailSumAsync,
        Func<Task<int>> detailCountAsync,
        bool customerSide,
        List<UnattributedControlLineDto> unattributedSink)
    {
        var account = await _context.Set<Account>().AsNoTracking()
            .Where(a => a.Code == accountCode && !a.IsDeleted)
            .Select(a => new { a.Id, a.NameAr, a.NormalBalance })
            .FirstOrDefaultAsync();

        var row = new SubLedgerReconciliationRowDto
        {
            AccountCode = accountCode,
            AccountNameAr = account?.NameAr ?? "—",
            DetailSourceAr = detailSourceAr
        };

        if (account is null)
        {
            row.StatusAr = "تعذر التحقق (الحساب غير موجود في الشجرة)";
            row.Note = $"لا يوجد حساب بالرقم {accountCode} في شجرة الحسابات.";
            return row;
        }

        // (1) الرصيد الرقابي من دفتر القيود — **وفق طبيعة الحساب**:
        //   مدين (الأصول: 1200/1300) ⇒ (مدين − دائن) · دائن (الخصوم: 2200) ⇒ (دائن − مدين)
        //   ليكون الرقم قابلًا للمقارنة مباشرة مع الرصيد التفصيلي المخزَّن بنفس الاصطلاح.
        var isDebitNature = account.NormalBalance == NormalBalance.Debit;

        // نقرأ الإسناد **من بند القيد** (CustomerId/SupplierId) — أثر تدقيقي دائم يُكتب عند الإنشاء
        // (قيود يدوية مُسندة + أرصدة افتتاحية مُنسَبة) ⇒ حساب «غير المُنسَب» دقيق لا تقديري.
        var lines = await _context.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == account.Id && !l.IsDeleted
                        && l.JournalEntry != null && !l.JournalEntry.IsDeleted)
            .Select(l => new
            {
                l.DebitAmount,
                l.CreditAmount,
                l.CustomerId,
                l.SupplierId,
                EntryType = l.JournalEntry!.EntryType,
                EntryNumber = l.JournalEntry!.EntryNumber,
                l.JournalEntry!.EntryDate,
                l.JournalEntry!.ReferenceNumber,
                l.CreatedAt
            })
            .ToListAsync();

        decimal Signed(decimal debit, decimal credit) => isDebitNature ? debit - credit : credit - debit;

        bool IsAttributed(Guid? customerId, Guid? supplierId)
            => customerSide ? customerId is not null : supplierId is not null;

        row.ControlBalance = MoneyRounding.Round(lines.Sum(l => Signed(l.DebitAmount, l.CreditAmount)));

        // (2) الرصيد التفصيلي من الأطراف.
        row.DetailBalance = MoneyRounding.Round(await detailSumAsync());
        row.DetailRowCount = await detailCountAsync();

        // (3) تفسير الفرق بأنواع القيود + تصنيف الإسناد من بنود القيود نفسها.
        var byType = lines
            .GroupBy(l => l.EntryType)
            .Select(g => new SubLedgerEffectDto
            {
                EntryType = (int)g.Key,
                EntryTypeNameAr = EntryTypeNameAr(g.Key),
                NetAmount = MoneyRounding.Round(g.Sum(x => Signed(x.DebitAmount, x.CreditAmount))),
                LineCount = g.Count(),
                IsDocumentDriven = DocumentDriven.Contains(g.Key)
            })
            .OrderByDescending(e => Math.Abs(e.NetAmount))
            .ToList();

        row.NonDocumentEffects = byType.Where(e => !e.IsDocumentDriven).ToList();

        // الفرق المُفسَّر = أثر القيود **غير المستندية بلا إسناد طرف** على هذا الحساب.
        // القيد غير المستندي **المُسند** إلى طرف لا يُنتج فرقًا (الرقابي والتفصيلي تحرّكا معًا)
        // فيُستثنى من التفسير ⇒ «غير المُفسَّر» يبقى دقيقًا (لا تقديريًا).
        var nonDocumentLines = lines.Where(l => !DocumentDriven.Contains(l.EntryType)).ToList();

        var unattributedLines = nonDocumentLines
            .Where(l => !IsAttributed(l.CustomerId, l.SupplierId))
            .ToList();

        foreach (var l in unattributedLines)
        {
            unattributedSink.Add(new UnattributedControlLineDto
            {
                AccountCode = accountCode,
                AccountNameAr = row.AccountNameAr,
                EntryNumber = l.EntryNumber,
                EntryType = (int)l.EntryType,
                EntryTypeNameAr = EntryTypeNameAr(l.EntryType),
                EntryDate = l.EntryDate,
                Reference = l.ReferenceNumber,
                SignedAmount = MoneyRounding.Round(Signed(l.DebitAmount, l.CreditAmount)),
                CreatedAt = l.CreatedAt
            });
        }

        var explained = MoneyRounding.Round(
            unattributedLines.Sum(l => Signed(l.DebitAmount, l.CreditAmount)));

        var attributedAmount = MoneyRounding.Round(lines
            .Where(l => IsAttributed(l.CustomerId, l.SupplierId))
            .Sum(l => Signed(l.DebitAmount, l.CreditAmount)));

        row.Difference = MoneyRounding.Round(row.ControlBalance - row.DetailBalance);
        row.ExplainedAmount = explained;
        row.UnexplainedAmount = MoneyRounding.Round(row.Difference - explained);

        if (Math.Abs(attributedAmount) > 0.005m)
            row.Note = $"أثر البنود المُسندة إلى طرف (مُزامَنة مع السجل المساعد): {attributedAmount:N2}.";
        if (unattributedLines.Count > 0)
            row.Note = (row.Note is null ? string.Empty : row.Note + " ") +
                       $"حركات غير مُنسَبة على الرقابي: {unattributedLines.Count} بمجموع صافٍ {explained:N2} " +
                       "(مسموحة ولا تُسوّى تلقائيًا، وتحتاج معالجة يدوية موثَّقة عند الحاجة).";

        row.StatusAr = Math.Abs(row.UnexplainedAmount) < 0.005m
            ? (Math.Abs(row.Difference) < 0.005m ? "متطابق" : "فرق مُفسَّر بالمصدر")
            : "فرق غير مُفسَّر — يحتاج تحقيقًا";

        return row;
    }

    // ─────────────────────────── صف المخزون الافتتاحي (إعلامي) ───────────────────────────

    /// <summary>
    /// كتلة الأرصدة الافتتاحية المخزنية: قيمة القيد على 1300 مقابل قيمة بنود الأصناف
    /// (Σ كمية × تكلفة) — تناظر ثلاثي مفروض: بند المخزون = حركة المخزون = القيد.
    /// لا يقيس تقييم المخزون الكامل (يحتاج سياسة تقييم معتمدة) — لذلك الصف إعلامي.
    /// </summary>
    private async Task<SubLedgerReconciliationRowDto> BuildInventoryOpeningRowAsync()
    {
        var account = await _context.Set<Account>().AsNoTracking()
            .Where(a => a.Code == InventoryAccount && !a.IsDeleted)
            .Select(a => new { a.Id, a.NameAr, a.NormalBalance })
            .FirstOrDefaultAsync();

        var row = new SubLedgerReconciliationRowDto
        {
            AccountCode = InventoryAccount,
            AccountNameAr = account?.NameAr ?? "—",
            DetailSourceAr = "قيمة بنود الأرصدة الافتتاحية المخزنية (Σ كمية × تكلفة)"
        };

        if (account is null)
        {
            row.StatusAr = "تعذر التحقق (الحساب غير موجود في الشجرة)";
            return row;
        }

        var lines = await _context.Set<OpeningBalanceLine>().AsNoTracking()
            .Where(l => l.AccountId == account.Id && !l.IsDeleted && l.ItemId != null)
            .Select(l => new { l.DebitAmount, l.CreditAmount, l.StockMovementId })
            .ToListAsync();

        var isDebitNature = account.NormalBalance == NormalBalance.Debit;
        decimal Signed(decimal debit, decimal credit) => isDebitNature ? debit - credit : credit - debit;

        // الرصيد الرقابي هنا = أثر **قيود دفعات الأرصدة الافتتاحية** فقط على حساب المخزون
        // (تُستثنى أي قيود افتتاحية أخرى خارج الدفعات الرسمية حتى لا يختلط القياس).
        var batchEntryIds = await _context.Set<OpeningBalanceBatch>().AsNoTracking()
            .Where(b => b.JournalEntryId != null)
            .Select(b => b.JournalEntryId!.Value)
            .ToListAsync();

        var ledgerBlockLines = await _context.Set<JournalEntryLine>().AsNoTracking()
            .Where(l => l.AccountId == account.Id && !l.IsDeleted
                        && batchEntryIds.Contains(l.JournalEntryId))
            .Select(l => new { l.DebitAmount, l.CreditAmount })
            .ToListAsync();

        row.ControlBalance = MoneyRounding.Round(ledgerBlockLines.Sum(l => Signed(l.DebitAmount, l.CreditAmount)));
        row.DetailBalance = MoneyRounding.Round(lines.Sum(l => Signed(l.DebitAmount, l.CreditAmount)));
        row.Difference = MoneyRounding.Round(row.ControlBalance - row.DetailBalance);
        row.ExplainedAmount = 0m;
        row.UnexplainedAmount = row.Difference;
        row.DetailRowCount = lines.Count;
        row.StatusAr = Math.Abs(row.Difference) < 0.005m
            ? "متطابق (كتلة الأرصدة الافتتاحية المخزنية)"
            : "فرق في كتلة الأرصدة الافتتاحية المخزنية";
        row.Note = $"عدد بنود الأصناف الافتتاحية: {lines.Count} · " +
                   $"منها بلا حركة مخزون: {lines.Count(l => l.StockMovementId is null)}. " +
                   "مطابقة رصيد 1300 الكامل مع تقييم المخزون تحتاج سياسة تقييم معتمدة. " +
                   // P7/G1 (2026-10-26): توضيح مصدر الفرق حتى لا يُفسَّر خطأً كعيب في الدفعات الرسمية.
                   "ملاحظة: حركات «رصيد افتتاحي» المسجَّلة من شاشة حركات المخزون مسار **كميّ/تكلفة** " +
                   "لا يُنشئ قيدًا على 1300 (المسار الرسمي للإثبات المالي هو دفعة الأرصدة الافتتاحية) " +
                   "⇒ أي فرق هنا قد يكون مصدره تلك الحركات.";
        return row;
    }

    private static string EntryTypeNameAr(JournalEntryType type) => type switch
    {
        JournalEntryType.Manual => "قيد يدوي",
        JournalEntryType.SalesInvoice => "فاتورة بيع",
        JournalEntryType.SalesReturn => "مردود مبيعات",
        JournalEntryType.PurchaseInvoice => "فاتورة مشتريات",
        JournalEntryType.PurchaseReturn => "مردود مشتريات",
        JournalEntryType.StockAdjustment => "تسوية مخزون",
        JournalEntryType.Payroll => "رواتب",
        JournalEntryType.Expense => "مصروف",
        JournalEntryType.StockWriteOff => "سند إتلاف",
        JournalEntryType.OpeningBalance => "أرصدة افتتاحية",
        JournalEntryType.ReceiptVoucher => "سند قبض",
        JournalEntryType.PaymentVoucher => "سند صرف",
        JournalEntryType.CustomerRefund => "رد عميل",
        _ => type.ToString()
    };
}
