namespace ERPSystem.Application.DTOs.Reports;

/// <summary>عنوان مجموعة أعمدة في رأس التقرير (مثال: «الأرصدة الافتتاحية» يغطي عمودين).</summary>
public class ReportHeaderGroup
{
    public string Title { get; set; } = "";

    /// <summary>عدد الأعمدة التي يغطيها العنوان. مجموع كل Spans يجب أن يساوي Headers.Count.</summary>
    public int Span { get; set; }

    public ReportHeaderGroup() { }
    public ReportHeaderGroup(string title, int span) { Title = title; Span = span; }
}

public class GenericReportTable
{
    public string TitleAr { get; set; } = "";
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public List<string>? Totals { get; set; }
    public string ParamSummary { get; set; } = "";
    public string? FooterNote { get; set; }
    public bool Landscape { get; set; } = true;

    // ── إضافات اختيارية للتقارير الهرمية — متوافقة رجعيًا (null = السلوك السابق حرفيًا) ──

    /// <summary>صف رأس ثانٍ يجمع الأعمدة تحت عناوين مجموعات (أرصدة افتتاحية/حركة/ختامية).</summary>
    public List<ReportHeaderGroup>? HeaderGroups { get; set; }

    /// <summary>صفوف إجماليات متعددة (إجمالي التفصيلي + الإجمالي العام). إن وُجد يتقدّم على <see cref="Totals"/>.</summary>
    public List<List<string>>? TotalsRows { get; set; }

    /// <summary>تصنيف أي صف بالتوازي مع <see cref="Rows"/>: "group" = تجميعي، "detail" = تفصيلي (null = بلا تصنيف).</summary>
    public List<string>? RowKinds { get; set; }

    /// <summary>
    /// مستوى دليل الحسابات لكل صف بالتوازي مع <see cref="Rows"/> (1 = أب، 2 = ابن، 3 = حفيد، 4+ = أعمق من حفيد).
    /// يُستخدم لتطبيق معيار الألوان/الأحجام/السماكات الموحّد نفسه في الشاشة وPDF.
    /// القيمة مشتقة من علاقة الحساب بأبيه (ParentAccountId) في طبقة البيانات، لا من طول الكود. null = بلا تنسيق حسب المستوى.
    /// </summary>
    public List<int>? RowLevels { get; set; }

    /// <summary>فئة CSS اختيارية على جدول التقرير (تنسيق خاص بتقرير واحد دون التأثير على بقية التقارير).</summary>
    public string? CssClass { get; set; }

    /// <summary>عرض رمز العملة المعتمدة في ترويسة التقرير (تقرأه الواجهة من إعدادات النظام) — افتراضي: لا.</summary>
    public bool ShowCurrency { get; set; }

    /// <summary>اسم ملف التصدير المقترح (بلا امتداد) — مثال: trial-balance-20261017. null = الافتراضي.</summary>
    public string? ExportFileName { get; set; }
}

