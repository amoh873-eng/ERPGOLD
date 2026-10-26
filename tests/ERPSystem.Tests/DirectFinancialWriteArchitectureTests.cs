using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// الدفعة E — حارس معماري (2026-10-23): لا يجوز أن تُنشئ طبقات الواجهة/الـAPI/الموبايل
/// مستندات مالية «مرحّلة» بالكتابة المباشرة على <c>DbContext</c> (فاتورة/حركة مخزون/قيد)،
/// لأن ذلك يُنتج مستندًا مرحّلًا بلا أثر محاسبي أو بلا تحديث أرصدة (كما كان في المسار التجريبي).
/// الكتابة المسموحة حصرًا عبر الخدمات المركزية (SalesInvoiceService / PurchaseInvoiceService /
/// JournalEntryService …) وهي التي تُنشئ القيد والحركة والرصيد معًا.
/// </summary>
public class DirectFinancialWriteArchitectureTests
{
    private static readonly string[] ForbiddenPatterns =
    {
        // الشكل 1: الوصول عبر DbSet مباشرة (استُخدم في المسار التجريبي — عيب مثبت 2026-10-23)
        "SalesInvoices.Add(",
        "PurchaseInvoices.Add(",
        "SalesReturns.Add(",
        "PurchaseReturns.Add(",
        "StockMovements.Add(",
        "JournalEntries.Add(",
        "JournalEntryLines.Add(",
        "PaymentVouchers.Add(",
        "ReceiptVouchers.Add(",
        // الشكل 2 (توسيع AUDIT-R2): الاصطلاح السائد في الخدمات `_context.Set<T>().Add(...)`
        // — بلا هذا الشكل يمرّ تجاوز خدمة المجال دون كشف.
        "Set<SalesInvoice>",
        "Set<PurchaseInvoice>",
        "Set<SalesReturn>",
        "Set<PurchaseReturn>",
        "Set<StockMovement>",
        "Set<JournalEntry>",
        "Set<JournalEntryLine>",
        "Set<PaymentVoucher>",
        "Set<ReceiptVoucher>",
        "Set<BankCheck>",
        "Set<PayrollRun>",
        "Set<OpeningBalanceBatch>",
        // الشكل 3: إضافة كيان مالي مباشرة داخل تعبير Add
        "Add(new SalesInvoice",
        "Add(new PurchaseInvoice",
        "Add(new StockMovement",
        "Add(new JournalEntry",
        "Add(new PaymentVoucher",
        "Add(new ReceiptVoucher"
    };

    [Fact]
    public void Ui_Api_Mobile_layers_must_not_write_financial_documents_directly()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var relative in new[] { "src\\Web", "src\\Api", "src\\Mobile" })
        {
            var dir = Path.Combine(root, relative);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var ext = Path.GetExtension(file);
                if (ext != ".cs" && ext != ".razor") continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (var pattern in ForbiddenPatterns)
                    {
                        if (lines[i].Contains(pattern, StringComparison.Ordinal))
                            offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {pattern}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "كتابة مباشرة لمستندات مالية من طبقة الواجهة/الـAPI (يجب استخدام الخدمات المركزية): " +
            string.Join(" ; ", offenders));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("لم يُعثر على جذر المستودع (src + tests).");
    }
}
