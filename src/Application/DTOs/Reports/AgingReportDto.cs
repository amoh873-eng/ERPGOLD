using ERPSystem.Application.Services;

namespace ERPSystem.Application.DTOs.Reports;

/// <summary>
/// تقرير أعمار الذمم (AR أو AP) — يجمع الفواتير المفتوحة حسب العميل/المورد
/// في فئات عمرية (0-30، 31-60، 61-90، +90) مع إجماليات كل عمود.
/// </summary>
public sealed class AgingReportDto
{
    public DateTime AsOf { get; set; }

    /// <summary>true = أعمار العملاء (AR) ، false = أعمار الموردين (AP).</summary>
    public bool IsReceivable { get; set; }

    public List<AgingPartyRow> Rows { get; set; } = new();

    public decimal Total0_30 { get; set; }
    public decimal Total31_60 { get; set; }
    public decimal Total61_90 { get; set; }
    public decimal Total90Plus { get; set; }

    /// <summary>PU-04 (D12): إجمالي الأرصدة الدائنة/السلبية — تُعرض صراحةً ولا تُصفَّر ولا تُخفى.</summary>
    public decimal TotalCredit { get; set; }

    /// <summary>P0-2: إجمالي إجماليات الفواتير المشمولة قبل أي خصم.</summary>
    public decimal TotalInvoiced { get; set; }

    /// <summary>P0-2: إجمالي المخصَّص فعليًا من سندات القبض على فواتير هذا التقرير.</summary>
    public decimal TotalAllocated { get; set; }

    /// <summary>P0-2: إجمالي المردودات **المؤثرة على الذمم** (تُخصم مرة واحدة فقط).</summary>
    public decimal TotalEffectiveReturns { get; set; }

    /// <summary>
    /// P0-2H (المرحلة A): إجمالي **سندات القبض المرحَّلة** التي لا يوجد لها تخصيص موثق لكل عملاء التقرير
    /// حتى تاريخ القطع. بند مستقل **لا يُخصم من أي فاتورة**.
    /// وصف صريح: لا يوجد كيان/حقل تخصيص للعملاء ⇒ «بلا تخصيص موثق» وليس «غير مخصص مؤكد».
    /// </summary>
    public decimal UnallocatedReceiptsTotal { get; set; }

    /// <summary>
    /// P0-2H (المرحلة A): إجمالي **الشيكات المقبوضة غير المرتجعة بلا رابط فاتورة**
    /// (بلا تخصيص موثق يمكن إثباته) — بند مستقل لا يُخصم من أي فاتورة ولا يُضاف مرتين.
    /// </summary>
    public decimal UnallocatedChecksTotal { get; set; }

    /// <summary>
    /// P0-2H (المرحلة A): إجمالي الشيكات المقبوضة غير المرتجعة **التي لها رابط فاتورة موثق**
    /// — يُعرض للتدقيق فقط، ولا يُخصم من متبقي الفاتورة في هذه المرحلة (سياسة التخصيص = مرحلة لاحقة).
    /// </summary>
    public decimal InvoiceLinkedChecksTotal { get; set; }

    // ═══ R13 (SUB-2b/P3 — المرحلة الثانية): ردود أموال العملاء المرحَّلة ═══

    /// <summary>
    /// إجمالي **الرصيد الدائن قبل الردود** (مردودات/مدفوعات زائدة مولَّدة أرصدة سالبة على الفواتير).
    /// موجب دائمًا؛ لا يُدمج مع مجاميع الفئات العمرية.
    /// </summary>
    public decimal TotalCreditBeforeRefunds { get; set; }

    /// <summary>إجمالي **الردود المرحَّلة فعلًا** (سندات صرف بطرف عميل، صافي العكوس المرحَّلة) حتى تاريخ القطع.</summary>
    public decimal TotalRefundsPaid { get; set; }

    /// <summary>D‑D: الجزء من الردود الذي طُبِّق على رصيد الفواتير الدائن (الباقي = RefundsBeyondInvoiceCredit).</summary>
    public decimal TotalRefundsAppliedToInvoiceCredit { get; set; }

    /// <summary>إجمالي **الرصيد الدائن المتبقي القابل للرد** بعد خصم الردود المرحَّلة (لا سالب).</summary>
    public decimal TotalRefundableCredit { get; set; }

    /// <summary>
    /// جزء من الردود تجاوز رصيد الفواتير الدائن (ممول من مقبوضات/شيكات غير مخصصة معروضة بندًا
    /// مستقلًا) — للتدقيق فقط؛ لا يُخصم من أي فاتورة لأن ذلك سيعني اختراع تخصيص غير موثق.
    /// </summary>
    public decimal TotalRefundsBeyondInvoiceCredit { get; set; }

    // ═══ SUB-2b/P3 — المرحلة الثالثة (D‑A / D‑D): مصادر الرصيد الدائن والتعارضات ═══

    /// <summary>
    /// إجمالي **الأرصدة الدائنة غير المخصصة** المعروضة بندًا مستقلًا = سندات قبض بلا تخصيص موثق
    /// + شيكات مقبوضة بلا رابط فاتورة. **لا تُخصم من أي فاتورة** (سياسة «لا تخصيص غير موثق» قائمة).
    /// </summary>
    public decimal TotalUnallocatedCredit { get; set; }

    /// <summary>
    /// الجزء من الأرصدة غير المخصصة الذي **موّل ردودًا فعلية** (لأن الردود تجاوزت رصيد الفواتير الدائن).
    /// عرض فقط — لا يُنشئ تخصيصًا ولا يغيّر أي فاتورة.
    /// </summary>
    public decimal TotalUnallocatedCreditConsumedByRefunds { get; set; }

    /// <summary>المتبقي من الأرصدة غير المخصصة بعد الردود المموّلة منها (لا سالب).</summary>
    public decimal TotalUnallocatedCreditRemaining { get; set; }

    /// <summary>
    /// تعارض يحتاج مراجعة: ردود مرحَّلة تجاوزت كل مصادر الرصيد الموثقة (فواتير + غير مخصص)
    /// ⇒ لا مصدر موثق يغطيها (قيود يدوية/افتتاحية أو خطأ بيانات).
    /// </summary>
    public decimal TotalRefundsWithNoDocumentedCredit { get; set; }

    /// <summary>
    /// تعارض يحتاج مراجعة: ردود مرحَّلة تجاوزت **سقف مردودها المرجعي** (المبلغ الإجمالي للمردود).
    /// لا يمكن أن يحدث عبر الخدمة (سقف مزدوج) ⇒ دليل على كتابة خارج الخدمة (G1).
    /// </summary>
    public decimal TotalRefundsBeyondReturnCap { get; set; }

    /// <summary>true = يوجد تعارض واحد على الأقل يستوجب مراجعة بشرية قبل اعتماد أرقام الرصيد الدائن.</summary>
    public bool HasCreditConflicts =>
        TotalRefundsWithNoDocumentedCredit > 0.005m || TotalRefundsBeyondReturnCap > 0.005m;

    /// <summary>تنبيه صريح عن حدود دقة التقرير (لا يُوصف أي رقم بأنه رصيد نهائي مؤكد).</summary>
    public string? ComputedResidualNote { get; set; }

    public decimal TotalAll => Total0_30 + Total31_60 + Total61_90 + Total90Plus;
}

/// <summary>صف طرف (عميل/مورد) في تقرير الأعمار + فواتيره المفتوحة للتعمّق.</summary>
public sealed class AgingPartyRow
{
    public Guid PartyId { get; set; }
    public string PartyCode { get; set; } = string.Empty;
    public string PartyNameAr { get; set; } = string.Empty;

    /// <summary>0-30 يوم من الاستحقاق.</summary>
    public decimal Bucket0_30 { get; set; }
    /// <summary>31-60 يوم.</summary>
    public decimal Bucket31_60 { get; set; }
    /// <summary>61-90 يوم.</summary>
    public decimal Bucket61_90 { get; set; }
    /// <summary>أكثر من 90 يوم.</summary>
    public decimal Bucket90Plus { get; set; }

    /// <summary>PU-04 (D12): رصيد دائن/مطالبة (قيمة سالبة) — يحتاج مراجعة، ولا يُخفى ولا يُصفَّر.</summary>
    public decimal CreditBalance { get; set; }

    /// <summary>
    /// P0-2H (المرحلة A): **سندات القبض المرحَّلة** لهذا الطرف بلا تخصيص موثق حتى تاريخ القطع.
    /// بند مستقل **لا يُخصم من أرصدة فواتيره** (لا علاقة تخصيص موثقة بين سند القبض والفاتورة).
    /// </summary>
    public decimal UnallocatedReceipts { get; set; }

    /// <summary>
    /// P0-2H (المرحلة A): **شيكات مقبوضة غير مرتجعة بلا رابط فاتورة** لهذا الطرف (بلا تخصيص موثق).
    /// بند مستقل لا يُخصم من أرصدة الفواتير ولا يُحتسب مرتين.
    /// </summary>
    public decimal UnallocatedChecks { get; set; }

    /// <summary>
    /// P0-2H (المرحلة A): شيكات مقبوضة غير مرتجعة **لها رابط فاتورة موثق** — عرض/تدقيق فقط،
    /// ولا تُخصم من متبقي الفاتورة في هذه المرحلة.
    /// </summary>
    public decimal InvoiceLinkedChecks { get; set; }

    /// <summary>true = رصيد الصف **متبقٍّ محسوب** من المستندات المتاحة (لا رصيد نهائي مؤكد).</summary>
    public bool IsComputedResidual { get; set; }

    // ═══ R13 — الردود المرحَّلة وتفكيك الرصيد الدائن للطرف ═══

    /// <summary>
    /// الرصيد الدائن **قبل** الردود (موجب دائمًا) = مجموع الأرصدة السالبة على فواتير الطرف
    /// قبل خصم أي رد مرحَّل.
    /// </summary>
    public decimal CreditBeforeRefunds { get; set; }

    /// <summary>الردود **المرحَّلة فعلًا** لهذا الطرف (صافي العكوس المرحَّلة) حتى تاريخ القطع.</summary>
    public decimal RefundsPaid { get; set; }

    /// <summary>الرصيد الدائن المتبقي القابل للرد = CreditBeforeRefunds − RefundsPaid (لا سالب).</summary>
    public decimal RemainingRefundableCredit { get; set; }

    /// <summary>
    /// الجزء من الردود الذي تجاوز رصيد الفواتير الدائن (ممول من مقبوضات/شيكات غير مخصصة معروضة
    /// بندًا مستقلًا) — للتدقيق فقط، ولا يُخصم من أي فاتورة.
    /// </summary>
    public decimal RefundsBeyondInvoiceCredit { get; set; }

    // ═══ D‑A / D‑D: تفكيك مصدر الرصيد الدائن لكل طرف (عرض فقط — لا يغيّر أي فاتورة) ═══

    /// <summary>الجزء من الردود الذي طُبِّق فعلًا على رصيد فواتير الطرف الدائن (لا يشمل ما تجاوزها).</summary>
    public decimal RefundsAppliedToInvoiceCredit { get; set; }

    /// <summary>إجمالي الأرصدة الدائنة غير المخصصة للطرف = مقبوضات بلا تخصيص موثق + شيكات بلا رابط فاتورة.</summary>
    public decimal UnallocatedCreditTotal { get; set; }

    /// <summary>الجزء الذي موّل ردودًا تجاوزت رصيد الفواتير الدائن (بلا أي ربط بفاتورة).</summary>
    public decimal UnallocatedCreditConsumedByRefunds { get; set; }

    /// <summary>المتبقي من الرصيد غير المخصص بعد الردود المموّلة منه (لا سالب).</summary>
    public decimal UnallocatedCreditRemaining { get; set; }

    /// <summary>تعارض: ردود بلا أي مصدر رصيد موثق يغطيها (تحتاج مراجعة).</summary>
    public decimal RefundsWithNoDocumentedCredit { get; set; }

    /// <summary>تعارض: ردود تجاوزت سقف مردودها المرجعي (كتابة خارج الخدمة — G1).</summary>
    public decimal RefundsBeyondReturnCap { get; set; }

    /// <summary>true = هذا الطرف يحمل تعارضًا يحتاج مراجعة قبل اعتماد أرقامه.</summary>
    public bool HasCreditConflict => RefundsWithNoDocumentedCredit > 0.005m || RefundsBeyondReturnCap > 0.005m;

    /// <summary>
    /// **إجمالي** الرصيد الدائن الموثق المتبقي (بعد الردود) = رصيد الفواتير الدائن المتبقي
    /// + المتبقي غير المخصص. موجب دائمًا، **ولا يُطرح من أي مديونية مدينة** ⇒ ليس «صافي مركز العميل».
    /// </summary>
    public decimal CreditPoolRemaining =>
        MoneyRounding.Round(-CreditBalance + UnallocatedCreditRemaining);

    /// <summary>
    /// صافي المركز الموثق للعميل حتى تاريخ القطع (سالب = له عندنا، موجب = مدين لنا):
    /// فئات مدينة + رصيد الفواتير الدائن (سالب) − الرصيد غير المخصص المتبقي.
    /// لا يدخل فيه أي قيد يدوي/افتتاحي/عهدة (خارج مصادر هذا التقرير) ⇒ قد يختلف عن `Customer.CurrentBalance`.
    /// </summary>
    public decimal NetDocumentedPosition =>
        MoneyRounding.Round(Total + CreditBalance - UnallocatedCreditRemaining);

    // ═══ P0-2: مكوّنات الصف مفصولةً — كل بند في حقل واضح (بلا دمج وبلا ازدواج) ═══

    /// <summary>إجمالي الفواتير المشمولة لهذا الطرف (قبل أي خصم).</summary>
    public decimal InvoicedTotal { get; set; }

    /// <summary>المخصَّص فعليًا من سندات القبض على فواتير هذا الطرف (يُخصم من المتبقي مرة واحدة).</summary>
    public decimal AllocatedTotal { get; set; }

    /// <summary>المردودات **المؤثرة على الذمم** لهذا الطرف (مرحَّلة · مرتبطة · لفاتورة آجلة).</summary>
    public decimal EffectiveReturnsTotal { get; set; }

    public decimal Total => Bucket0_30 + Bucket31_60 + Bucket61_90 + Bucket90Plus;

    /// <summary>الفواتير المفتوحة المكوّنة للصف (للتعمّق عند الطلب).</summary>
    public List<AgingInvoiceLine> Invoices { get; set; } = new();
}

/// <summary>سطر فاتورة مفتوحة داخل عمق الأعمار.</summary>
public sealed class AgingInvoiceLine
{
    public Guid PartyId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public DateTime DueDate { get; set; }
    public int DaysOverdue { get; set; }
    /// <summary>المتبقي المحسوب = الإجمالي − المدفوع المسجَّل − المردودات المؤثرة − المخصَّص.</summary>
    public decimal Balance { get; set; }

    /// <summary>إجمالي الفاتورة (قبل أي خصم).</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>المدفوع المسجَّل على الفاتورة (PaidAmount) — للعرض والتفسير فقط.</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>المردودات المؤثرة على الذمم (مرة واحدة).</summary>
    public decimal EffectiveReturns { get; set; }

    /// <summary>المخصَّص من سندات القبض على هذه الفاتورة (صافي العكوس).</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>R13: جزء من الردود المرحَّلة طُبِّق على رصيد هذه الفاتورة الدائن (يُضاف إلى Balance مرة واحدة).</summary>
    public decimal RefundsApplied { get; set; }

    /// <summary>
    /// D‑D: الرصيد الدائن **الخاص بهذه الفاتورة** قبل أي رد (موجب؛ صفر للفواتير المدينة).
    /// ليس الرصيد الدائن العام للعميل ⇒ لا تكرار بين الفواتير.
    /// </summary>
    public decimal CreditBeforeRefunds { get; set; }

    /// <summary>
    /// D‑D: true = سطر رصيد دائن (رصيد الفاتورة سالب أو صفّره رد) — يُعرض في التفصيل
    /// **ولا يدخل فئات الأعمار المدينة** ولا يُجمع في «إجمالي المستحق على العميل».
    /// </summary>
    public bool IsCreditLine { get; set; }

}