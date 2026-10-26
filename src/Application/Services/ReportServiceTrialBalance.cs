using System.Globalization;
using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.DTOs.Reports;

namespace ERPSystem.Application.Services;

/// <summary>
/// ميزان المراجعة الهرمي حسب مستويات دليل الحسابات (أب/ابن/حفيد/… ديناميكيًا بلا حدّ ثابت).
///
/// الأساس المحاسبي المعتمد (مطابق لسياسة النظام الحالية):
///  • القيود وبنود القيود <b>غير المحذوفة</b> فقط — لا تُحتسب الحركات الملغاة.
///  • الرصيد الافتتاحي = صافي رصيد الحساب قبل تاريخ «من» (صفر عند عدم تحديد فترة).
///  • حركة الفترة = مجموع المدين/الدائن داخل الفترة [من، إلى] شاملة الطرفين.
///  • الرصيد الختامي = الافتتاحي + حركة الفترة (مجمّعًا هرميًا).
///  • كل بند قيد يُحتسب <b>مرة واحدة</b> على حسابه ثم يصعد إلى أبيه ⇒ لا ازدواجية في الإجماليات.
///  • الإجمالي العام يُحسب من الحسابات <b>الجذرية</b> فقط (والحسابات الفرعية داخل آبائها).
///
/// المصدر: نفس محرّك الأرصدة الموحّد <see cref="AccountBalanceService"/> —
/// لا يوجد أي منطق أرصدة مكرر هنا، والتقرير عرضٌ للنتيجة فقط.
/// </summary>
public partial class ReportService
{
    /// <summary>ثقافة ثابتة للمبالغ: لاتينية دائمًا (منزلتان عشريتان + فاصل آلاف) بغض النظر عن لغة الواجهة.</summary>
    private static readonly CultureInfo MoneyCulture = CultureInfo.InvariantCulture;

    /// <summary>تنسيق موحّد للمبالغ المالية: 1,234.50</summary>
    internal static string Money(decimal value) => value.ToString("#,##0.00", MoneyCulture);

    /// <summary>قراءة مفتاح منطقي (true/1/on) من معاملات التقرير مع قيمة افتراضية.</summary>
    private static bool BoolParam(Dictionary<string, string?> pars, string name, bool fallback)
    {
        if (!pars.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
        var v = raw.Trim();
        return v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v == "1"
            || v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>إزاحة بادئة حسب المستوى (RTL آمن) — أب بلا إزاحة، ابن 3، حفيد 6 …</summary>
    private static string Indent(int level) => new string('\u00A0', Math.Max(0, level - 1) * 3);

    /// <summary>مجموع الأعمدة الستة (افتتاحي مدين/دائن، حركة مدين/دائن، ختامي مدين/دائن) لمجموعة عقد.</summary>
    private static (decimal OpenDebit, decimal OpenCredit, decimal PeriodDebit, decimal PeriodCredit, decimal CloseDebit, decimal CloseCredit)
        SumColumns(IEnumerable<AccountBalanceNodeDto> nodes)
    {
        decimal od = 0m, oc = 0m, pd = 0m, pc = 0m, cd = 0m, cc = 0m;
        foreach (var n in nodes)
        {
            // الرصيد الافتتاحي يُعرض بطبيعته (مدين/دائن) — نفس منطق الرصيد الختامي.
            od += n.OpeningBalance > 0m ? n.OpeningBalance : 0m;
            oc += n.OpeningBalance < 0m ? -n.OpeningBalance : 0m;
            pd += n.PeriodDebit;
            pc += n.PeriodCredit;
            cd += n.DebitBalance;
            cc += n.CreditBalance;
        }
        return (od, oc, pd, pc, cd, cc);
    }

    /// <summary>كل الحسابات الورقية (القابلة للترحيل) التابعة لعقدة — العقدة نفسها إن كانت ورقة.</summary>
    private static IEnumerable<AccountBalanceNodeDto> LeavesOf(AccountBalanceNodeDto node)
        => node.Children.Count == 0 ? new[] { node } : node.Children.SelectMany(LeavesOf);

    /// <summary>
    /// تفسير رقمي صريح لفرق عمودي الرصيد بين «إجمالي الحسابات القابلة للترحيل» و«الإجمالي العام من الجذور»:
    /// الإجمالي العام يعرض رصيد كل جذر <b>صافيًا</b> في عمود واحد (مدين أو دائن)، فإذا حمل الجذر الواحد
    /// أرصدة متعاكسة (مدين ودائن) تُقاصّ بمقدارها من العمودين معًا ⇒ الفرق بين الإجماليين = مجموع التقاصّ فقط،
    /// ولا يمسّ عمودي حركة الفترة (لأنهما مجموع خام) ولا التوازن (مدين = دائن في الصفّين).
    /// </summary>
    private static (decimal DebitOffset, decimal CreditOffset, List<string> Details)
        RootNettingBreakdown(AccountBalanceTreeDto tree)
    {
        decimal debitOffset = 0m, creditOffset = 0m;
        var details = new List<string>();
        foreach (var root in tree.Roots)
        {
            var leaves = LeavesOf(root).ToList();
            decimal grossDebit = leaves.Sum(l => l.DebitBalance);
            decimal grossCredit = leaves.Sum(l => l.CreditBalance);
            decimal net = MoneyRounding.Round(grossDebit - grossCredit);
            decimal shownDebit = net > 0m ? net : 0m;
            decimal shownCredit = net < 0m ? -net : 0m;

            debitOffset += grossDebit - shownDebit;
            creditOffset += grossCredit - shownCredit;

            if (grossDebit - shownDebit != 0m)
            {
                var opposites = shownDebit > 0m
                    ? leaves.Where(l => l.CreditBalance != 0m).Select(l => $"{l.Code} دائن {Money(l.CreditBalance)}")
                    : leaves.Where(l => l.DebitBalance != 0m).Select(l => $"{l.Code} مدين {Money(l.DebitBalance)}");
                details.Add($"{root.Code} - {root.NameAr}: {string.Join(", ", opposites)}");
            }
        }
        return (MoneyRounding.Round(debitOffset), MoneyRounding.Round(creditOffset), details);
    }

    /// <summary>
    /// يبني ميزان المراجعة الهرمي: صف لكل حساب في موضعه الشجري الصحيح،
    /// مع أعمدة (افتتاحي مدين/دائن، حركة الفترة مدين/دائن، ختامي مدين/دائن).
    /// </summary>
    /// <param name="from">بداية الفترة (شمولاً) — null = بلا رصيد افتتاحي.</param>
    /// <param name="to">نهاية الفترة (شمولاً) — null = حتى آخر حركة.</param>
    /// <param name="rootAccountId">تصفية على فرع حساب واحد (مع الحفاظ على مستواه الحقيقي).</param>
    /// <param name="includeZero">إظهار الحسابات بلا حركة/رصيد (الحسابات الصفرية).</param>
    /// <param name="levelsOnly">
    /// الاكتفاء بمستويات التجميع (الحسابات التي لها أبناء) وإخفاء الصفوف التفصيلية القابلة للترحيل.
    /// لا يغيّر المعنى المحاسبي: أعمدة الحساب التجميعي وصفوف الإجماليات تُحتسب من الشجرة كاملة
    /// (وليس من الصفوف المعروضة)، وكل بند قيد يبقى محتسبًا مرة واحدة تحت أول جدّ ظاهر له.
    /// </param>
    internal async Task<GenericReportTable> BuildHierarchicalTrialBalanceAsync(
        DateTime? from, DateTime? to, Guid? rootAccountId, bool includeZero, bool levelsOnly = false)
    {
        var tree = await new AccountBalanceService(_context).GetBalanceTreeAsync(new AccountBalanceQuery
        {
            From = from,
            To = to,
            RootAccountId = rootAccountId,
            IncludeZeroBalance = includeZero
        });

        // ── الصفوف: ترتيب هرمي (أب ثم أبناؤه ثم أحفاده) + مسافات بادئة حسب المستوى ──
        // «مستويات التجميع فقط» يُخفي الصفوف التفصيلية من العرض، لكن الأرصدة والإجماليات
        // تُحتسب من الشجرة كاملة (لا تغيير في المعنى المحاسبي ولا ازدواج في الأرقام).
        var displayed = levelsOnly
            ? tree.Flattened.Where(n => n.Children.Count > 0).ToList()
            : tree.Flattened;
        var rows = new List<List<string>>();
        var rowKinds = new List<string>();
        // مستوى كل صف (1 = أب، 2 = ابن، 3 = حفيد، 4+ = أعمق) — مشتق من الشجرة الفعلية (ParentAccountId)
        // ويستهلكه العرض الموحّد في الشاشة وPDF لتطبيق معيار الألوان/الأحجام/السماكات نفسه.
        var rowLevels = new List<int>();
        foreach (var n in displayed)
        {
            var openDebit = n.OpeningBalance > 0m ? n.OpeningBalance : 0m;
            var openCredit = n.OpeningBalance < 0m ? -n.OpeningBalance : 0m;
            rows.Add(new List<string>
            {
                Indent(n.Level) + n.Code,
                Indent(n.Level) + n.NameAr,
                n.LevelNameAr,
                Money(openDebit),
                Money(openCredit),
                Money(n.PeriodDebit),
                Money(n.PeriodCredit),
                Money(n.DebitBalance),
                Money(n.CreditBalance)
            });
            rowKinds.Add(n.Children.Count > 0 ? "group" : "detail");
            rowLevels.Add(n.Level);
        }

        // ── الإجماليات: صفّان — إجمالي الحسابات القابلة للترحيل، ثم الإجمالي العام من الجذور ──
        //  (أ) الأوراق فقط: كل بند قيد يُحتسب مرة واحدة على الحساب الذي سُجّل عليه ⇒ هذا الرقم هو
        //      إجمالي ميزان المراجعة التقليدي، وهو المطابق لدفتر الأستاذ للفترة نفسها.
        //  (ب) الجذور: تجميع صاعد بنفس القاعدة (مباشر + الأبناء) — عمودا حركة الفترة فيه يجب أن
        //      يساويا (أ) تمامًا، وهذا هو الإثبات العددي على عدم احتساب أي حركة مرتين.
        var details = SumColumns(tree.Flattened.Where(n => n.Children.Count == 0));
        var grand = SumColumns(tree.Roots);

        var detailsLabel = rootAccountId.HasValue && tree.Roots.Count == 1
            ? $"إجمالي الحسابات القابلة للترحيل (الفرع: {tree.Roots[0].Code})"
            : "إجمالي الحسابات القابلة للترحيل (التفصيلية = إجمالي ميزان المراجعة)";
        if (levelsOnly) detailsLabel += " — محتسب من الشجرة كاملة";
        var grandLabel = rootAccountId.HasValue && tree.Roots.Count == 1
            ? $"الإجمالي العام من الجذور (الفرع: {tree.Roots[0].Code} - {tree.Roots[0].NameAr})"
            : "الإجمالي العام من الجذور (صافي كل جذر بعد تقاصّ الأرصدة المتعاكسة)";

        var totalsDetails = new List<string>
        {
            "", detailsLabel, "",
            Money(details.OpenDebit), Money(details.OpenCredit),
            Money(details.PeriodDebit), Money(details.PeriodCredit),
            Money(details.CloseDebit), Money(details.CloseCredit)
        };
        var totalsGrand = new List<string>
        {
            "", grandLabel, "",
            Money(grand.OpenDebit), Money(grand.OpenCredit),
            Money(grand.PeriodDebit), Money(grand.PeriodCredit),
            Money(grand.CloseDebit), Money(grand.CloseCredit)
        };

        // ── ملاحظة التقرير: الأساس + إثبات منع الازدواجية + فحص التوازن (والفرق إن وُجد — لا يُخفى) ──
        var note = "الأساس: القيود المرحّلة غير المحذوفة فقط (لا تُحتسب القيود الملغاة). "
                 + "رصيد الحساب الأب = حركته المباشرة + مجموع أرصدة أبنائه، وكل بند قيد يُحتسب مرة واحدة على حسابه ثم يصعد إلى أبيه.";
        if (tree.AggregateAccountsWithDirectMovements.Count > 0)
            note += " | تنبيه: حركة مباشرة على حسابات تجميعية: "
                  + string.Join(", ", tree.AggregateAccountsWithDirectMovements);

        // (1) إثبات منع الازدواجية: المصدر واحد، لذا يجب تطابق عمودي حركة الفترة بين الصفّين.
        note += details.PeriodDebit == grand.PeriodDebit && details.PeriodCredit == grand.PeriodCredit
            ? $" | تحقّق عدم الازدواجية: حركة الفترة متطابقة من التفصيلي ومن الجذور (مدين {Money(grand.PeriodDebit)} / دائن {Money(grand.PeriodCredit)})."
            : $" | تنبيه: حركة الفترة غير متطابقة — التفصيلي (مدين {Money(details.PeriodDebit)} / دائن {Money(details.PeriodCredit)}) ≠ الجذور (مدين {Money(grand.PeriodDebit)} / دائن {Money(grand.PeriodCredit)}).";

        // (2) التوازن: إجمالي الأرصدة الختامية المدينة = الدائنة (على أساس الحسابات القابلة للترحيل).
        note += $" | الرصيد الختامي: مدين {Money(details.CloseDebit)} = دائن {Money(details.CloseCredit)}";
        note += details.CloseDebit == details.CloseCredit
            ? " — متوازن."
            : $" — غير متوازن، الفرق {Money(Math.Abs(details.CloseDebit - details.CloseCredit))} (يُعرض ولا يُخفى).";

        // (3) تفسير رقمي صريح لفرق عمودي الرصيد الختامي بين صفّي الإجماليات:
        //     الإجمالي العام يعرض كل جذر صافيًا في عمود واحد ⇒ يُقاصّ داخل الجذر الواحد أي رصيد معاكس،
        //     والفرق يجب أن يطابق تمامًا مجموع التقاصّ المحتسب من الشجرة (وإلا فتنبيه مراجعة صريح).
        var netting = RootNettingBreakdown(tree);
        var closingGap = MoneyRounding.Round(details.CloseDebit - grand.CloseDebit);
        var closingGapCredit = MoneyRounding.Round(details.CloseCredit - grand.CloseCredit);
        if (closingGap != 0m || closingGapCredit != 0m)
        {
            note += $" | فرق عمودي الرصيد الختامي بين الصفّين = {Money(closingGap)} مدين / {Money(closingGapCredit)} دائن،"
                  + $" وهو تقاصّ أرصدة متعاكسة داخل الجذور ({Money(netting.DebitOffset)} مدين / {Money(netting.CreditOffset)} دائن)";
            if (netting.Details.Count > 0) note += " [" + string.Join(" | ", netting.Details) + "]";
            note += " — لا ازدواج ولا أثر على عمودي حركة الفترة ولا على التوازن (مدين = دائن في الصفّين)."
                  + " إجمالي ميزان المراجعة المطابق لدفتر الأستاذ هو صفّ «إجمالي الحسابات القابلة للترحيل».";

            if (closingGap != netting.DebitOffset || closingGapCredit != netting.CreditOffset)
                note += $" تنبيه: الفرق لا يطابق التقاصّ المحتسب من الشجرة ({Money(netting.DebitOffset)} / {Money(netting.CreditOffset)}) — يستوجب مراجعة محاسبية.";
        }

        // نفس المبدأ لأعمدة الرصيد الافتتاحي (يظهر عادةً متطابقًا؛ ويُفسَّر بنفس القاعدة إن اختلف).
        var openingGap = MoneyRounding.Round(details.OpenDebit - grand.OpenDebit);
        var openingGapCredit = MoneyRounding.Round(details.OpenCredit - grand.OpenCredit);
        if (openingGap != 0m || openingGapCredit != 0m)
            note += $" | فرق عمودي الرصيد الافتتاحي بين الصفّين = {Money(openingGap)} مدين / {Money(openingGapCredit)} دائن،"
                  + " بنفس قاعدة التقاصّ داخل الجذور (رصيد الجذر صافيًا في عمود واحد).";

        // ── وصف الفترة والفلاتر المطبقة ──
        var paramSummary = from.HasValue && to.HasValue ? $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}"
                         : to.HasValue ? $"حتى {to:yyyy-MM-dd}"
                         : from.HasValue ? $"من {from:yyyy-MM-dd}"
                         : "كامل السجل (كل الفترات)";
        if (rootAccountId.HasValue && tree.Roots.Count == 1)
            paramSummary += $" | الحساب: {tree.Roots[0].Code} - {tree.Roots[0].NameAr}";
        paramSummary += includeZero ? " | يشمل الحسابات الصفرية" : " | الحسابات ذات الحركة فقط";
        if (levelsOnly) paramSummary += " | مستويات التجميع فقط";

        // شفافية وضع «مستويات التجميع فقط»: الصفوف التفصيلية مخفية عرضًا فقط، والأرقام كاملة.
        if (levelsOnly)
            note += " | الوضع المعروض: مستويات التجميع فقط — الصفوف التفصيلية مخفية للعرض، "
                  + "وأعمدة الحسابات التجميعية وصفوف الإجماليات محتسبة من الشجرة كاملة كما هي.";

        return new GenericReportTable
        {
            TitleAr = "ميزان المراجعة — حسب مستويات دليل الحسابات",
            CssClass = "report-tb",
            Landscape = true,
            ShowCurrency = true,   // الواجهة تعرض رمز العملة المعتمدة في ترويسة التقرير
            Headers = new List<string>
            {
                "رقم الحساب", "اسم الحساب", "المستوى",
                "مدين", "دائن",      // الأرصدة الافتتاحية
                "مدين", "دائن",      // حركة الفترة
                "مدين", "دائن"       // الأرصدة الختامية
            },
            HeaderGroups = new List<ReportHeaderGroup>
            {
                new("البيان", 3),
                new("الأرصدة الافتتاحية (قبل الفترة)", 2),
                new("حركة الفترة", 2),
                new("الأرصدة الختامية (حتى نهاية الفترة)", 2)
            },
            Rows = rows,
            RowKinds = rowKinds,
            RowLevels = rowLevels,
            TotalsRows = new List<List<string>> { totalsDetails, totalsGrand },
            Totals = totalsGrand,
            FooterNote = note,
            ParamSummary = paramSummary,
            // اسم موحّد لملف التصدير (Excel/PDF) — نفس الاسم في مركز التقارير وفي الصفحة القديمة
            ExportFileName = $"trial-balance-{(to ?? DateTime.Today):yyyyMMdd}"
        };
    }

    /// <inheritdoc />
    public Task<GenericReportTable> GetTrialBalanceReportAsync(DateTime? from = null, DateTime? to = null,
        Guid? rootAccountId = null, bool includeZero = false, bool levelsOnly = false)
        => BuildHierarchicalTrialBalanceAsync(from, to, rootAccountId, includeZero, levelsOnly);
}

