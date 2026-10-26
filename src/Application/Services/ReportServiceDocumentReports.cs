using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// المرحلة D — تقارير سندات القبض/الصرف وقيود المستندات الأربعة (فواتير/مردودات مبيعات ومشتريات).
///
/// مبادئ ملزمة مطبَّقة هنا:
///   • مصدر واحد للقيود: سطور JournalEntryLines — لا إعادة بناء قيود ولا أي كتابة.
///   • كل تقرير يشترط نوعه: لا يظهر أي نوع في تقرير نوع آخر (تصفية صريحة بالنوع/المعرّفات المرتبطة).
///   • الإجماليات على مستوى البند لتفادي تضخيم مبلغ الرأس عند تعدد البنود.
///   • فاتورة المبيعات تُعرض بقيدين (بيع/تكلفة) بوسم واضح، وكل بند مرة واحدة في الإجماليات.
///   • فتح التقرير لا يُنشئ أي قيد ولا يغيّر أي رصيد (قراءة فقط).
/// </summary>
public partial class ReportService
{
    /// <summary>هل المفتاح من مفاتيح المرحلة D؟</summary>
    internal static bool IsDocumentReportKey(string key) => key is
        "receipt-vouchers" or "receipt-voucher-entries" or "payment-vouchers" or "payment-voucher-entries"
        or "sales-invoice-entries" or "sales-return-entries" or "purchase-invoice-entries" or "purchase-return-entries";

    private static string? T(Dictionary<string, string?> pars, string name)
        => pars.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v!.Trim() : null;

    /// <summary>اسم الحالة بالعربية — قيم VoucherStatus و DocumentStatus متقاطعة (1/2/3)، لذا نفرّق بالسياق.</summary>
    private static string StatusAr(int status, bool isVoucher = false)
    {
        if (isVoucher)
            return status switch
            {
                (int)VoucherStatus.Draft => "مسودة",
                (int)VoucherStatus.Approved => "معتمد",
                (int)VoucherStatus.Posted => "مرحّل",
                (int)VoucherStatus.Cancelled => "ملغى",
                _ => status.ToString()
            };

        return status switch
        {
            (int)DocumentStatus.Draft => "مسودة",
            (int)DocumentStatus.Posted => "مرحّل",
            (int)DocumentStatus.Cancelled => "ملغى",
            _ => status.ToString()
        };
    }

    /// <summary>نقطة الدخول الموحّدة لتقارير المرحلة D (تُستدعى من GetReportAsync).</summary>
    internal async Task<GenericReportTable> GetDocumentReportAsync(
        string key, Dictionary<string, string?> pars, DateTime from, DateTime to)
    {
        var docNumber = T(pars, "docNumber");
        var entryNumber = T(pars, "entryNumber");
        var party = T(pars, "party");
        var statusText = T(pars, "status");
        int? status = int.TryParse(statusText, out var st) ? st : (int?)null;

        return key switch
        {
            "receipt-vouchers" => await VoucherListAsync(true, from, to, docNumber, entryNumber, party, status),
            "payment-vouchers" => await VoucherListAsync(false, from, to, docNumber, entryNumber, party, status),
            "receipt-voucher-entries" => await VoucherEntriesAsync(true, from, to, docNumber, entryNumber, party, status),
            "payment-voucher-entries" => await VoucherEntriesAsync(false, from, to, docNumber, entryNumber, party, status),
            "sales-invoice-entries" => await SalesInvoiceEntriesAsync(from, to, docNumber, entryNumber, party, status),
            "sales-return-entries" => await SalesReturnEntriesAsync(from, to, docNumber, entryNumber, party, status),
            "purchase-invoice-entries" => await PurchaseInvoiceEntriesAsync(from, to, docNumber, entryNumber, party, status),
            "purchase-return-entries" => await PurchaseReturnEntriesAsync(from, to, docNumber, entryNumber, party, status),
            _ => new GenericReportTable { TitleAr = key, Headers = new(), Rows = new() }
        };
    }

    /// <summary>إجماليات على مستوى البند (مدين/دائن/الفرق) — تُستخدم في تقارير القيود.</summary>
    private static List<string> EntryTotals(int count, decimal debit, decimal credit)
        => new() { "الإجمالي", $"عدد={count}", "", "", "", "", "", debit.ToString("N2"), credit.ToString("N2"), $"فرق={Math.Round(debit - credit, 2):N2}", "" };

    private static readonly List<string> EntryHeaders = new()
        { "رقم القيد", "التاريخ", "نوع القيد", "رقم المستند", "الطرف", "الحساب", "البيان", "مدين", "دائن", "الحالة", "المستخدم" };

    private static string Acc(Account? a) => a is null ? "—" : $"{a.Code} - {a.NameAr}";

    // ───────────────────────── (1) سندات القبض/الصرف: مستوى السند ─────────────────────────

    private async Task<GenericReportTable> VoucherListAsync(
        bool isReceipt, DateTime from, DateTime to, string? docNumber, string? entryNumber, string? party, int? status)
    {
        var rows = new List<List<string>>();
        decimal total = 0m;

        if (isReceipt)
        {
            var q = _context.Set<ReceiptVoucher>().Include(v => v.Customer).Include(v => v.JournalEntry)
                .Where(v => v.VoucherDate >= from.Date && v.VoucherDate < to.Date.AddDays(1));
            if (party is not null)
                q = q.Where(v => v.Customer!.NameAr.Contains(party) || v.Customer!.Code.Contains(party));
            if (status.HasValue) { var s = (VoucherStatus)status.Value; q = q.Where(v => v.Status == s); }
            if (docNumber is not null) q = q.Where(v => v.VoucherNumber.Contains(docNumber));
            if (entryNumber is not null)
                q = q.Where(v => v.JournalEntry != null && v.JournalEntry.EntryNumber.Contains(entryNumber));

            var list = await q.OrderBy(v => v.VoucherDate).ThenBy(v => v.VoucherNumber).AsNoTracking().ToListAsync();
            foreach (var v in list)
            {
                total += v.Amount;
                rows.Add(new List<string>
                {
                    v.VoucherNumber, v.VoucherDate.ToString("yyyy-MM-dd"),
                    $"{v.Customer?.NameAr} ({v.Customer?.Code})",
                    v.PaymentMethod == PaymentMethod.Cash ? "نقدي" : "بنكي",
                    v.Amount.ToString("N2"), StatusAr((int)v.Status, true),
                    v.JournalEntry?.EntryNumber ?? "—", v.PostedBy ?? v.ApprovedBy ?? v.CreatedBy ?? "—"
                });
            }
        }
        else
        {
            var q = _context.Set<PaymentVoucher>().Include(v => v.Supplier).Include(v => v.Customer).Include(v => v.JournalEntry)
                .Where(v => v.VoucherDate >= from.Date && v.VoucherDate < to.Date.AddDays(1));
            if (party is not null)
                q = q.Where(v => (v.Supplier != null && (v.Supplier.NameAr.Contains(party) || v.Supplier.Code.Contains(party)))
                                 || (v.Customer != null && (v.Customer.NameAr.Contains(party) || v.Customer.Code.Contains(party)))
                                 || (v.BeneficiaryName != null && v.BeneficiaryName.Contains(party)));
            if (status.HasValue) { var s = (VoucherStatus)status.Value; q = q.Where(v => v.Status == s); }
            if (docNumber is not null) q = q.Where(v => v.VoucherNumber.Contains(docNumber));
            if (entryNumber is not null)
                q = q.Where(v => v.JournalEntry != null && v.JournalEntry.EntryNumber.Contains(entryNumber));

            var list = await q.OrderBy(v => v.VoucherDate).ThenBy(v => v.VoucherNumber).AsNoTracking().ToListAsync();
            foreach (var v in list)
            {
                total += v.Amount;
                rows.Add(new List<string>
                {
                    v.VoucherNumber, v.VoucherDate.ToString("yyyy-MM-dd"),
                    v.CustomerId is not null
                        ? $"{v.Customer?.NameAr} ({v.Customer?.Code}) — رد"
                        : $"{v.Supplier?.NameAr} ({v.Supplier?.Code})",
                    v.PaymentMethod == PaymentMethod.Cash ? "نقدي" : "بنكي",
                    v.Amount.ToString("N2"), StatusAr((int)v.Status, true),
                    v.JournalEntry?.EntryNumber ?? "—", v.PostedBy ?? v.ApprovedBy ?? v.CreatedBy ?? "—"
                });
            }
        }

        return new GenericReportTable
        {
            TitleAr = isReceipt ? "سندات القبض" : "سندات الصرف",
            Headers = new() { "رقم السند", "التاريخ", isReceipt ? "العميل" : "الطرف", "طريقة الدفع", "المبلغ", "الحالة", "رقم القيد", "المستخدم" },
            Rows = rows,
            Totals = new() { "الإجمالي", $"عدد={rows.Count}", "", "", total.ToString("N2"), "", "", "" },
            ParamSummary = $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}",
            FooterNote = "الإجمالي على مستوى السند (مبلغ واحد لكل سند) — لا يُضاعف مع تعدد بنود القيد.",
            ExportFileName = $"{(isReceipt ? "receipt" : "payment")}-vouchers-{DateTime.Now:yyyyMMdd}",
            Landscape = true
        };
    }

    // ───────────────────────── (2) قيود سندات القبض/الصرف: مستوى البند ─────────────────────────

    private async Task<GenericReportTable> VoucherEntriesAsync(
        bool isReceipt, DateTime from, DateTime to, string? docNumber, string? entryNumber, string? party, int? status)
    {
        // SUB-2b/P3: قيود رد العميل نوعها CustomerRefund — تُضم إلى تقرير سندات الصرف وإلا اختفت.
        var types = isReceipt
            ? new[] { JournalEntryType.ReceiptVoucher }
            : new[] { JournalEntryType.PaymentVoucher, JournalEntryType.CustomerRefund };

        // (أ) سندات النوع المطلوب فقط — وتُطبَّق فلاتر الطرف/الحالة على السندات أولًا (مصدر واحد للحقيقة)
        Dictionary<string, (string Party, int Status, string? By)> map = new();
        if (isReceipt)
        {
            var vq = _context.Set<ReceiptVoucher>().Include(v => v.Customer).AsNoTracking()
                .Where(v => v.VoucherDate >= from.Date && v.VoucherDate < to.Date.AddDays(1));
            if (party is not null) vq = vq.Where(v => v.Customer!.NameAr.Contains(party) || v.Customer!.Code.Contains(party));
            if (status.HasValue) { var s = (VoucherStatus)status.Value; vq = vq.Where(v => v.Status == s); }
            if (docNumber is not null) vq = vq.Where(v => v.VoucherNumber.Contains(docNumber));
            foreach (var v in await vq.ToListAsync())
                map[v.VoucherNumber] = ($"{v.Customer?.NameAr} ({v.Customer?.Code})", (int)v.Status, v.PostedBy ?? v.ApprovedBy ?? v.CreatedBy);
        }
        else
        {
            var vq = _context.Set<PaymentVoucher>().Include(v => v.Supplier).Include(v => v.Customer).AsNoTracking()
                .Where(v => v.VoucherDate >= from.Date && v.VoucherDate < to.Date.AddDays(1));
            if (party is not null)
                vq = vq.Where(v => (v.Supplier != null && (v.Supplier.NameAr.Contains(party) || v.Supplier.Code.Contains(party)))
                                   || (v.Customer != null && (v.Customer.NameAr.Contains(party) || v.Customer.Code.Contains(party)))
                                   || (v.BeneficiaryName != null && v.BeneficiaryName.Contains(party)));
            if (status.HasValue) { var s = (VoucherStatus)status.Value; vq = vq.Where(v => v.Status == s); }
            if (docNumber is not null) vq = vq.Where(v => v.VoucherNumber.Contains(docNumber));
            foreach (var v in await vq.ToListAsync())
                map[v.VoucherNumber] = (v.CustomerId is not null
                    ? $"{v.Customer?.NameAr} ({v.Customer?.Code}) — رد"
                    : $"{v.Supplier?.NameAr} ({v.Supplier?.Code})", (int)v.Status, v.PostedBy ?? v.ApprovedBy ?? v.CreatedBy);
        }

        var numbers = map.Keys.ToList();

        // (ب) بنود القيود من نوع السند المطلوب وبنفس مرجع السند — بلا أي نوع آخر
        var lines = await _context.Set<JournalEntryLine>()
            .Include(l => l.Account).Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry != null && types.Contains(l.JournalEntry.EntryType)
                        && l.JournalEntry.ReferenceNumber != null && numbers.Contains(l.JournalEntry.ReferenceNumber)
                        && l.JournalEntry.EntryDate >= from.Date && l.JournalEntry.EntryDate < to.Date.AddDays(1)
                        && (entryNumber == null || l.JournalEntry.EntryNumber.Contains(entryNumber)))
            .OrderBy(l => l.JournalEntry!.EntryDate).ThenBy(l => l.JournalEntry!.EntryNumber)
            .AsNoTracking().ToListAsync();

        var rows = new List<List<string>>();
        foreach (var l in lines)
        {
            var info = map[l.JournalEntry!.ReferenceNumber!];
            rows.Add(new List<string>
            {
                l.JournalEntry.EntryNumber,
                l.JournalEntry.EntryDate.ToString("yyyy-MM-dd"),
                isReceipt ? "قيد سند قبض" : "قيد سند صرف",
                l.JournalEntry.ReferenceNumber!,
                info.Party,
                Acc(l.Account),
                l.Note ?? l.JournalEntry.Description ?? "—",
                l.DebitAmount.ToString("N2"),
                l.CreditAmount.ToString("N2"),
                StatusAr(info.Status, true),
                info.By ?? "—"
            });
        }

        var debit = lines.Sum(l => l.DebitAmount);
        var credit = lines.Sum(l => l.CreditAmount);

        return new GenericReportTable
        {
            TitleAr = isReceipt ? "قيود سندات القبض" : "قيود سندات الصرف",
            Headers = new(EntryHeaders),
            Rows = rows,
            Totals = EntryTotals(rows.Count, debit, credit),
            ParamSummary = $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}",
            FooterNote = "الإجماليات على مستوى البند (لا تضخيم لمبلغ الرأس) — وتُعادل دائمًا: مدين = دائن لنفس القيد.",
            ExportFileName = $"{(isReceipt ? "receipt" : "payment")}-voucher-entries-{DateTime.Now:yyyyMMdd}",
            Landscape = true
        };
    }

    // ───────────────────────── (3) قيود المستندات الأربعة: مستوى البند ─────────────────────────

    /// <summary>
    /// تقرير بنود قيود بعضوية صريحة: تُمرَّر خريطة (معرّف القيد → رقم المستند/الطرف/الحالة/الوسم)،
    /// ثم تُقرأ سطور هذه القيود فقط — فلا يتسرّب أي نوع آخر إلى التقرير.
    /// </summary>
    private async Task<GenericReportTable> EntryLinesReportAsync(
        string title, string exportName, string typeLabel,
        Dictionary<Guid, (string Doc, string Party, int Status, string Kind)> tag,
        DateTime from, DateTime to, string? entryNumber, string footer)
    {
        var rows = new List<List<string>>();
        var ids = tag.Keys.ToList();
        decimal debit = 0m, credit = 0m;

        if (ids.Count > 0)
        {
            var lines = await _context.Set<JournalEntryLine>()
                .Include(l => l.Account).Include(l => l.JournalEntry)
                .Where(l => ids.Contains(l.JournalEntryId) && l.JournalEntry != null
                            && l.JournalEntry.EntryDate >= from.Date && l.JournalEntry.EntryDate < to.Date.AddDays(1)
                            && (entryNumber == null || l.JournalEntry.EntryNumber.Contains(entryNumber)))
                .OrderBy(l => l.JournalEntry!.EntryDate).ThenBy(l => l.JournalEntry!.EntryNumber)
                .AsNoTracking().ToListAsync();

            foreach (var l in lines)
            {
                debit += l.DebitAmount;
                credit += l.CreditAmount;
                var info = tag[l.JournalEntryId];
                rows.Add(new List<string>
                {
                    l.JournalEntry!.EntryNumber,
                    l.JournalEntry.EntryDate.ToString("yyyy-MM-dd"),
                    $"{typeLabel} ({info.Kind})",
                    info.Doc,
                    info.Party,
                    Acc(l.Account),
                    l.Note ?? l.JournalEntry.Description ?? "—",
                    l.DebitAmount.ToString("N2"),
                    l.CreditAmount.ToString("N2"),
                    StatusAr(info.Status),
                    l.JournalEntry.CreatedBy ?? "—"
                });
            }
        }

        return new GenericReportTable
        {
            TitleAr = title,
            Headers = new(EntryHeaders),
            Rows = rows,
            Totals = EntryTotals(rows.Count, debit, credit),
            ParamSummary = $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}",
            FooterNote = footer,
            ExportFileName = $"{exportName}-{DateTime.Now:yyyyMMdd}",
            Landscape = true
        };
    }

    // ───────────────────────── (4) قيود الفواتير والمردودات (مبيعات/مشتريات) ─────────────────────────

    /// <summary>قيود فواتير المبيعات — قيد البيع + قيد التكلفة موسومان بوضوح، وكل بند مرة واحدة.</summary>
    private async Task<GenericReportTable> SalesInvoiceEntriesAsync(
        DateTime from, DateTime to, string? docNumber, string? entryNumber, string? party, int? status)
    {
        var invoices = await _context.Set<SalesInvoice>().Include(i => i.Customer).AsNoTracking()
            .Where(i => i.InvoiceDate >= from.Date && i.InvoiceDate < to.Date.AddDays(1))
            .ToListAsync();

        if (docNumber is not null) invoices = invoices.Where(i => i.InvoiceNumber.Contains(docNumber)).ToList();
        if (party is not null)
            invoices = invoices.Where(i => i.Customer != null
                && (i.Customer.NameAr.Contains(party) || i.Customer.Code.Contains(party))).ToList();
        if (status.HasValue) invoices = invoices.Where(i => (int)i.Status == status.Value).ToList();

        var tag = new Dictionary<Guid, (string Doc, string Party, int Status, string Kind)>();
        foreach (var i in invoices)
        {
            var info = (Doc: i.InvoiceNumber, Party: $"{i.Customer?.NameAr} ({i.Customer?.Code})", Status: (int)i.Status);
            if (i.SalesJournalEntryId.HasValue) tag[i.SalesJournalEntryId.Value] = (info.Doc, info.Party, info.Status, "بيع");
            if (i.CogsJournalEntryId.HasValue) tag[i.CogsJournalEntryId.Value] = (info.Doc, info.Party, info.Status, "تكلفة");
        }

        return await EntryLinesReportAsync("قيود فواتير المبيعات", "sales-invoice-entries", "فاتورة مبيعات",
            tag, from, to, entryNumber,
            "فاتورة المبيعات تظهر بقيدين (بيع/تكلفة) بوسم واضح — الإجماليات على مستوى البند بلا تكرار لمبلغ الرأس.");
    }

    /// <summary>قيود مردودات المبيعات — قيد المردود + قيد التكلفة موسومان.</summary>
    private async Task<GenericReportTable> SalesReturnEntriesAsync(
        DateTime from, DateTime to, string? docNumber, string? entryNumber, string? party, int? status)
    {
        var returns = await _context.Set<SalesReturn>().Include(r => r.Customer).AsNoTracking()
            .Where(r => r.ReturnDate >= from.Date && r.ReturnDate < to.Date.AddDays(1))
            .ToListAsync();

        if (docNumber is not null) returns = returns.Where(r => r.ReturnNumber.Contains(docNumber)).ToList();
        if (party is not null)
            returns = returns.Where(r => r.Customer != null
                && (r.Customer.NameAr.Contains(party) || r.Customer.Code.Contains(party))).ToList();
        if (status.HasValue) returns = returns.Where(r => (int)r.Status == status.Value).ToList();

        var tag = new Dictionary<Guid, (string Doc, string Party, int Status, string Kind)>();
        foreach (var r in returns)
        {
            var info = (Doc: r.ReturnNumber, Party: $"{r.Customer?.NameAr} ({r.Customer?.Code})", Status: (int)r.Status);
            if (r.SalesJournalEntryId.HasValue) tag[r.SalesJournalEntryId.Value] = (info.Doc, info.Party, info.Status, "مردود");
            if (r.CogsJournalEntryId.HasValue) tag[r.CogsJournalEntryId.Value] = (info.Doc, info.Party, info.Status, "تكلفة");
        }

        return await EntryLinesReportAsync("قيود مردودات المبيعات", "sales-return-entries", "مردود مبيعات",
            tag, from, to, entryNumber,
            "كل مردود يظهر بقيده (وأي قيد تكلفة مرتبط) — الإجماليات على مستوى البند.");
    }

    /// <summary>قيود فواتير المشتريات (قيد واحد لكل فاتورة).</summary>
    private async Task<GenericReportTable> PurchaseInvoiceEntriesAsync(
        DateTime from, DateTime to, string? docNumber, string? entryNumber, string? party, int? status)
    {
        var invoices = await _context.Set<PurchaseInvoice>().Include(i => i.Supplier).AsNoTracking()
            .Where(i => i.InvoiceDate >= from.Date && i.InvoiceDate < to.Date.AddDays(1))
            .ToListAsync();

        if (docNumber is not null) invoices = invoices.Where(i => i.InvoiceNumber.Contains(docNumber)).ToList();
        if (party is not null)
            invoices = invoices.Where(i => i.Supplier != null
                && (i.Supplier.NameAr.Contains(party) || i.Supplier.Code.Contains(party))).ToList();
        if (status.HasValue) invoices = invoices.Where(i => (int)i.Status == status.Value).ToList();

        var tag = new Dictionary<Guid, (string Doc, string Party, int Status, string Kind)>();
        foreach (var i in invoices)
            if (i.JournalEntryId.HasValue)
                tag[i.JournalEntryId.Value] = (i.InvoiceNumber, $"{i.Supplier?.NameAr} ({i.Supplier?.Code})", (int)i.Status, "فاتورة");

        return await EntryLinesReportAsync("قيود فواتير المشتريات", "purchase-invoice-entries", "فاتورة مشتريات",
            tag, from, to, entryNumber,
            "كل فاتورة مشتريات تظهر بقيدها المرتبط فقط — الإجماليات على مستوى البند.");
    }

    /// <summary>قيود مردودات المشتريات (قيد واحد لكل مردود).</summary>
    private async Task<GenericReportTable> PurchaseReturnEntriesAsync(
        DateTime from, DateTime to, string? docNumber, string? entryNumber, string? party, int? status)
    {
        var returns = await _context.Set<PurchaseReturn>().Include(r => r.Supplier).AsNoTracking()
            .Where(r => r.ReturnDate >= from.Date && r.ReturnDate < to.Date.AddDays(1))
            .ToListAsync();

        if (docNumber is not null) returns = returns.Where(r => r.ReturnNumber.Contains(docNumber)).ToList();
        if (party is not null)
            returns = returns.Where(r => r.Supplier != null
                && (r.Supplier.NameAr.Contains(party) || r.Supplier.Code.Contains(party))).ToList();
        if (status.HasValue) returns = returns.Where(r => (int)r.Status == status.Value).ToList();

        var tag = new Dictionary<Guid, (string Doc, string Party, int Status, string Kind)>();
        foreach (var r in returns)
            if (r.JournalEntryId.HasValue)
                tag[r.JournalEntryId.Value] = (r.ReturnNumber, $"{r.Supplier?.NameAr} ({r.Supplier?.Code})", (int)r.Status, "مردود");

        return await EntryLinesReportAsync("قيود مردودات المشتريات", "purchase-return-entries", "مردود مشتريات",
            tag, from, to, entryNumber,
            "كل مردود مشتريات يظهر بقيده المرتبط فقط — الإجماليات على مستوى البند.");
    }
}
