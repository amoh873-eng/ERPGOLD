using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Application.Reporting;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ERPSystem.Web.Services;

/// <summary>
/// يولّد ملفات PDF من بيانات التقارير المالية باستخدام QuestPDF.
/// يسجّل خطاً عربياً من نظام ويندوز لعرض النصوص العربية بشكل صحيح.
/// </summary>
public static class PdfExporter
{
    public const string PdfContentType = "application/pdf";

    private const string ArabicFont = "Arabic";

    static PdfExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        TryRegisterArabicFont();
    }

    /// <summary>مسار ملف الخط العربي الذي سُجّل فعلاً (null = لم يُعثر على خط عربي في هذا المضيف).</summary>
    public static string? RegisteredArabicFontPath { get; private set; }

    /// <summary>هل سُجّل خط عربي حقيقي؟ (بدونه تخرج الحروف العربية مفقودة/منفصلة أو بديلة).</summary>
    public static bool HasArabicFont => !string.IsNullOrWhiteSpace(RegisteredArabicFontPath);

    // مسارات مرشّحة معروفة: ويندوز (بيئة التطوير) ثم لينكس (صورة الحاوية بعد تثبيت fonts-noto-core/fonts-dejavu-core).
    private static readonly string[] ArabicFontCandidates =
    {
        @"C:\Windows\Fonts\arial.ttf",
        @"C:\Windows\Fonts\tahoma.ttf",
        @"C:\Windows\Fonts\segoeui.ttf",
        "/usr/share/fonts/truetype/noto/NotoNaskhArabic-Regular.ttf",
        "/usr/share/fonts/truetype/noto/NotoNaskhArabicUI-Regular.ttf",
        "/usr/share/fonts/truetype/noto/NotoSansArabic-Regular.ttf",
        "/usr/share/fonts/truetype/noto/NotoKufiArabic-Regular.ttf",
        "/usr/share/fonts/truetype/amiri/Amiri-Regular.ttf",
        "/usr/share/fonts/truetype/hosny-amiri/Amiri-Regular.ttf",
        "/usr/share/fonts/truetype/sil-scheherazade/Scheherazade-Regular.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
    };

    private static readonly string[] ArabicFontSearchDirs =
    {
        "/usr/share/fonts",
        "/usr/local/share/fonts",
        @"C:\Windows\Fonts"
    };

    private static void TryRegisterArabicFont()
    {
        foreach (var path in EnumerateArabicFontCandidates())
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                RegisterArabicFamily(path);
                RegisteredArabicFontPath = path;
                return;
            }
            catch
            {
                // نتجاهل ونجرب الخط التالي
            }
        }
    }

    /// <summary>
    /// يسجّل عائلة الخط العربي باسم <see cref="ArabicFont"/> بكل الأوجه المتاحة
    /// (Regular/Bold/SemiBold/Medium) — فبدون وجه عريض تخرج الحروف العربية في العناوين ناقصة.
    /// </summary>
    private static void RegisterArabicFamily(string regularPath)
    {
        var dir = Path.GetDirectoryName(regularPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(regularPath)
            .Replace("-Regular", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-regular", string.Empty, StringComparison.OrdinalIgnoreCase);

        var faces = new[]
        {
            regularPath,
            Path.Combine(dir, stem + "-Bold.ttf"),
            Path.Combine(dir, stem + "-Bold.otf"),
            Path.Combine(dir, stem + "-SemiBold.ttf"),
            Path.Combine(dir, stem + "-Medium.ttf"),
            Path.Combine(dir, stem + ".ttf")
        };

        var registered = false;
        foreach (var face in faces)
        {
            if (!File.Exists(face)) continue;
            try
            {
                using var stream = File.OpenRead(face);
                FontManager.RegisterFontWithCustomName(ArabicFont, stream);
                registered = true;
            }
            catch
            {
                // نتجاهل هذا الوجه ونكمل
            }
        }

        if (!registered) throw new InvalidOperationException("No usable Arabic font face: " + regularPath);
    }

    /// <summary>تعداد مرشّحي الخطوط العربية: المسارات المعروفة أولاً ثم مسح مجلدات الخطوط بحثاً عن عربية.</summary>
    private static IEnumerable<string> EnumerateArabicFontCandidates()
    {
        foreach (var candidate in ArabicFontCandidates) yield return candidate;

        string[] patterns =
        {
            "*NaskhArabic*.ttf", "*SansArabic*.ttf", "*KufiArabic*.ttf", "*Arabic*.ttf",
            "Amiri*.ttf", "Scheherazade*.ttf", "Kacst*.ttf", "DejaVuSans.ttf"
        };

        foreach (var dir in ArabicFontSearchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pattern in patterns)
            {
                string[] found;
                try { found = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var file in found) yield return file;
            }
        }
    }

    public static byte[] ExportTrialBalance(TrialBalanceDto dto)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontFamily(ArabicFont).FontSize(9));

                page.Content().Column(column =>
                {
                    column.Item().Text("ميزان المراجعة").SemiBold().FontSize(16);
                    column.Item().Height(10);

                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(50);
                            columns.RelativeColumn();
                            columns.ConstantColumn(80);
                            columns.ConstantColumn(80);
                            columns.ConstantColumn(80);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Text("الكود").SemiBold();
                            header.Cell().Text("الحساب").SemiBold();
                            header.Cell().Text("مدين").SemiBold();
                            header.Cell().Text("دائن").SemiBold();
                            header.Cell().Text("الرصيد").SemiBold();
                        });

                        foreach (var line in dto.Lines)
                        {
                            table.Cell().Text(line.AccountCode);
                            table.Cell().Text(line.AccountNameAr);
                            table.Cell().Text(line.TotalDebit.ToString("N2"));
                            table.Cell().Text(line.TotalCredit.ToString("N2"));
                            table.Cell().Text(line.Balance.ToString("N2"));
                        }

                        table.Cell().Text("الإجمالي").SemiBold();
                        table.Cell().Text("");
                        table.Cell().Text(dto.TotalDebit.ToString("N2")).SemiBold();
                        table.Cell().Text(dto.TotalCredit.ToString("N2")).SemiBold();
                        table.Cell().Text("");
                    });
                });
            });
        }).GeneratePdf();
    }

    public static byte[] ExportIncomeStatement(IncomeStatementDto dto)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontFamily(ArabicFont).FontSize(10));

                page.Content().Column(column =>
                {
                    column.Item().Text("قائمة الدخل").SemiBold().FontSize(16);
                    column.Item().Text($"من {dto.From:yyyy-MM-dd} إلى {dto.To:yyyy-MM-dd}").FontSize(10);
                    column.Item().Height(10);

                    WriteSection(column, "الإيرادات", dto.Revenues, dto.TotalRevenue);
                    column.Item().Height(10);
                    WriteSection(column, "المصروفات", dto.Expenses, dto.TotalExpenses);
                    column.Item().Height(10);

                    column.Item().Row(row =>
                    {
                        row.RelativeItem().Text("صافي الدخل").SemiBold();
                        row.ConstantItem(100).Text(dto.NetIncome.ToString("N2")).SemiBold();
                    });
                });
            });
        }).GeneratePdf();
    }

    public static byte[] ExportBalanceSheet(BalanceSheetDto dto)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontFamily(ArabicFont).FontSize(10));

                page.Content().Column(column =>
                {
                    column.Item().Text("الميزانية العمومية").SemiBold().FontSize(16);
                    column.Item().Text($"كما في {dto.AsOf:yyyy-MM-dd}").FontSize(10);
                    column.Item().Height(10);

                    WriteSection(column, "الأصول", dto.Assets, dto.TotalAssets);
                    column.Item().Height(10);
                    WriteSection(column, "الخصوم", dto.Liabilities, dto.TotalLiabilities);
                    column.Item().Height(10);
                    WriteSection(column, "حقوق الملكية", dto.Equity, dto.TotalEquity);
                });
            });
        }).GeneratePdf();
    }

    private static void WriteSection(
        ColumnDescriptor column, string title,
        List<StatementLineDto> lines, decimal total)
    {
        column.Item().Text(title).SemiBold().FontSize(12);
        column.Item().Height(4);

        foreach (var line in lines)
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Text(line.AccountNameAr);
                row.ConstantItem(120).Text(line.Amount.ToString("N2"));
            });
        }

        column.Item().Row(row =>
        {
            row.RelativeItem().Text($"إجمالي {title}").SemiBold();
            row.ConstantItem(120).Text(total.ToString("N2")).SemiBold();
        });
    }

    /// <summary>
    /// تصدير PDF لأي جدول تقرير موحّد <see cref="GenericReportTable"/> — بما فيه ميزان المراجعة الهرمي:
    /// ترويسة (اسم التقرير + الفترة والفلاتر) ثم صف مجموعات الأعمدة ثم العناوين ثم الصفوف بترتيبها
    /// الهرمي (بتصنيفها تجميعي/تفصيلي) ثم صفوف الإجماليات. لا يُعاد احتساب أي رقم — نفس جدول الشاشة.
    /// </summary>
    /// <summary>
    /// يبني مستند PDF لأي جدول تقرير موحّد (نفس جدول الشاشة) — نقطة بناء واحدة:
    /// اتجاه المحتوى من اليمين لليسار + خط عربي مسجّل، وتنسيق مستويات الحسابات من المعيار الموحّد.
    /// تُستخدم للتصدير النهائي وللمعاينة البصرية (توليد صور الصفحات) بلا تكرار منطق.
    /// </summary>
    public static IDocument BuildGenericTableDocument(GenericReportTable t)
    {
        var hasGroups = t.HeaderGroups != null && t.HeaderGroups.Count > 0;
        var totalsRows = t.TotalsRows != null && t.TotalsRows.Count > 0
            ? t.TotalsRows
            : (t.Totals != null ? new List<List<string>> { t.Totals } : null);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(t.Landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.Margin(24);
                // اتجاه الصفحة من اليمين لليسار: عمود «البيان» أولاً من اليمين، وسياق كتابة عربي صحيح.
                page.ContentFromRightToLeft();
                page.DefaultTextStyle(x => x.FontFamily(ArabicFont).FontSize(8).LineHeight(1.4f));

                page.Content().Column(column =>
                {
                    column.Item().Text(t.TitleAr).SemiBold().FontSize(14);
                    if (!string.IsNullOrWhiteSpace(t.ParamSummary))
                        column.Item().Text(t.ParamSummary).FontSize(9);
                    column.Item().Height(8);

                    // ── صف مجموعات الأعمدة (الأرصدة الافتتاحية / حركة الفترة / الأرصدة الختامية) ──
                    if (hasGroups)
                    {
                        column.Item().Row(row =>
                        {
                            foreach (var g in t.HeaderGroups!)
                            {
                                var span = g.Span < 1 ? 1 : g.Span;
                                row.RelativeItem(span)
                                   .BorderBottom(1).BorderColor(Colors.Grey.Medium)
                                   .Background(Colors.Purple.Lighten4)
                                   .Padding(2)
                                   .Text(g.Title).SemiBold();
                            }
                        });
                    }

                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            for (var i = 0; i < t.Headers.Count; i++)
                            {
                                if (i == 0) columns.ConstantColumn(84);        // رقم الحساب (يحتمل الإزاحة البادئة للعمق)
                                else if (i == 1) columns.RelativeColumn(2);    // اسم الحساب
                                else if (i == 2) columns.ConstantColumn(50);   // المستوى
                                else columns.ConstantColumn(56);               // الأعمدة المالية
                            }
                        });

                        table.Header(header =>
                        {
                            foreach (var h in t.Headers)
                                header.Cell().Background(Colors.Grey.Lighten3).Padding(2).Text(h).SemiBold();
                        });

                        for (var r = 0; r < t.Rows.Count; r++)
                        {
                            var kind = t.RowKinds != null && r < t.RowKinds.Count ? t.RowKinds[r] : null;
                            var isGroup = kind == "group";
                            // مستوى الصف من البيانات (مشتق من ParentAccountId) — نفس المعيار المستخدم في الشاشة.
                            var level = t.RowLevels != null && r < t.RowLevels.Count ? t.RowLevels[r] : 0;
                            AccountLevelTokens? tokens = level > 0 ? AccountLevelStyle.For(level) : (AccountLevelTokens?)null;

                            for (var c = 0; c < t.Headers.Count; c++)
                            {
                                var value = c < t.Rows[r].Count ? t.Rows[r][c] : "";
                                var cell = table.Cell().Padding(2);
                                if (isGroup) cell = cell.Background(Colors.Grey.Lighten5);

                                if (tokens.HasValue && c < 2)
                                {
                                    // عمودا «رقم الحساب» و«اسم الحساب»: لون/حجم/سماكة المستوى من المعيار الموحّد
                                    var tk = tokens.Value;
                                    var accountText = cell.Text(value)
                                        .FontSize(AccountLevelStyle.FontSizePt(level))
                                        .FontColor(HexColor(tk.ColorHex));
                                    if (tk.Weight >= 700) accountText.Bold();
                                    else if (tk.Weight >= 600) accountText.SemiBold();
                                    else if (tk.Weight >= 500) accountText.Medium();
                                }
                                else if (c >= 3)
                                {
                                    // الأعمدة المالية: LTR دائماً حتى لا يختل ترتيب الأرقام والفاصلة بين الآلاف
                                    var numericText = cell.ContentFromLeftToRight().Text(value).FontSize(8);
                                    if (isGroup) numericText.SemiBold();
                                }
                                else if (isGroup)
                                {
                                    cell.Text(value).SemiBold().FontSize(8);
                                }
                                else
                                {
                                    cell.Text(value).FontSize(8);
                                }
                            }
                        }

                        if (totalsRows != null)
                        {
                            foreach (var row in totalsRows)
                            {
                                for (var c = 0; c < t.Headers.Count; c++)
                                {
                                    var value = c < row.Count ? row[c] : "";
                                    table.Cell().Background(Colors.BlueGrey.Lighten4).Padding(2)
                                         .Text(value).SemiBold().FontSize(8);
                                }
                            }
                        }
                    });

                    if (!string.IsNullOrWhiteSpace(t.FooterNote))
                    {
                        column.Item().Height(6);
                        // ملاحظة/تحذير التقرير: أصغر وأفتح مع حدّ أعلى — لا يُقرأ كصفّ حساب من المستويات
                        column.Item()
                            .BorderTop(1).BorderColor(Colors.Grey.Lighten2)
                            .PaddingTop(3)
                            .Text(t.FooterNote).FontSize(7).FontColor(Colors.Grey.Darken1);
                    }
                });
            });
        });
    }

    /// <summary>تصدير PDF لجدول تقرير موحّد — يستخدم نفس مستند المعاينة (بلا تكرار منطق).</summary>
    public static byte[] ExportGenericTable(GenericReportTable t)
        => BuildGenericTableDocument(t).GeneratePdf();

    /// <summary>
    /// صور PNG لصفحات مستند التقرير — للمعاينة البصرية: اتصال الحروف العربية، اتجاه RTL،
    /// ألوان/أحجام/سماكات المستويات، وعدم فقدان البيانات. مسار المستخدم يبقى ExportGenericTable.
    /// </summary>
    public static IReadOnlyList<byte[]> RenderGenericTablePageImages(GenericReportTable t, int rasterDpi = 110)
        => BuildGenericTableDocument(t)
            .GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = rasterDpi })
            .ToList();

    /// <summary>تحويل لون #RRGGBB إلى لون QuestPDF (بلا اعتماد على واجهات تتغير بين الإصدارات).</summary>
    private static Color HexColor(string hex)
    {
        var h = (hex ?? string.Empty).TrimStart('#');
        if (h.Length != 6) return Colors.Black;
        try
        {
            return Color.FromRGB(
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16));
        }
        catch { return Colors.Black; }
    }
}