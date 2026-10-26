using ERPSystem.Application.DTOs.Vouchers;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// P0-2H (المرحلة A) — استعلامات **قراءة فقط** لذمم العملاء.
///
/// النطاق المعتمد لهذه المرحلة (بلا Migration وبلا كيان تخصيص):
///   • «متبقٍّ محسوب» لكل فاتورة بيع مرحَّلة = الإجمالي − المدفوع المسجَّل على الفاتورة
///     − مردوداتها **المرحَّلة المرتبطة بها** حتى تاريخ القطع.
///     ولا يُطرح أي سند قبض من أي فاتورة، لعدم وجود علاقة تخصيص موثقة للعملاء.
///   • المقبوضات التي لا يوجد لها تخصيص موثق تُجمَّع بندًا مستقلًا لكل عميل:
///       - سندات قبض مرحَّلة (لا يوجد لها أي حقل ربط بفاتورة ⇒ «بلا تخصيص موثق» وليس «غير مخصص مؤكد»).
///       - شيكات مستلمة غير مرتجعة **بلا رابط فاتورة** (<see cref="BankCheck.RelatedInvoiceId"/> = null).
///       - شيكات مستلمة لها رابط فاتورة موثق تُعرض منفصلة **ولا تُخصم** (منعًا لعدّ الشيك مرتين
///         مع <see cref="SalesInvoice.PaidAmount"/> الذي يشمل الشيك في الفواتير المسدَّدة بشيك).
///
/// ⚠️ هذه الخدمة **لا تكتب** أي شيء: لا قيود · لا أرصدة · لا مستندات · ولا تُنشئ تخصيصات ولا تفترضها.
/// الاستعلامات **مجمَّعة** (GROUP BY) وعددها ثابت مهما كان عدد الفواتير ⇒ بلا N+1 (انظر اختبار P0-2H).
///
/// ═══ P0-2J — قاعدة تاريخ القطع الموحَّدة (إصلاح M1) ═══
/// «حتى تاريخ القطع asOf» تعني: **كل مستندات يوم `asOf` كاملةً + ما قبله**، ولا شيء بعده.
/// السبب الجذري للخلل السابق: أنواع أعمدة التواريخ **مختلطة** في المخطط —
///   • `SalesInvoice.InvoiceDate` = `date` (بلا وقت)
///   • `JournalEntry.EntryDate` = `date` (مرجع الترحيل المحاسبي)
///   • `SalesReturn.ReturnDate` / `ReceiptVoucher.VoucherDate` / `BankCheck.ReceivedOrIssuedDate` = `timestamp without time zone`
/// فالمقارنة `X <= asOf.Date` (منتصف الليل) كانت **تُسقط مستندات نفس اليوم** إذا حملت جزء وقت
/// (يحدث فعليًا: شاشة الشيكات تُهيّئ `ReceivedOrIssuedDate = DateTime.Now`).
/// القاعدة المطبَّقة الآن: حد أعلى **حصري** عند بداية اليوم التالي — `< asOf.Date.AddDays(1)`:
///   • يتعامل صحيحًا مع `timestamp` (يشمل اليوم كاملًا حتى 23:59:59.999…)
///   • ومطابق تمامًا لـ`<= asOf.Date` عند الأعمدة من نوع `date` (لأن قيمتها منتصف ليل يومها)
///   • ولا يستخدم `23:59:59` ولا مقارنة زمنية اعتباطية، ولا يغيّر دلالة تاريخ المستند إلى تاريخ إنشاء تقني.
/// </summary>
public sealed class CustomerAllocationQueries
{
    private readonly DbContext _context;

    public CustomerAllocationQueries(DbContext context) => _context = context;

    /// <summary>
    /// حد تاريخ القطع **الحصري** (P0-2J): بداية اليوم التالي لتاريخ القطع (00:00) — يُستخدم مع `&lt;`.
    /// </summary>
    private static DateTime ExclusiveCutoff(DateTime asOf) => asOf.Date.AddDays(1);

    /// <summary>نتيجة احتساب «متبقٍّ محسوب» لفاتورة بيع واحدة (بلا أي تخصيص مقبوضات).</summary>
    public sealed class CustomerInvoiceResidual
    {
        public Guid InvoiceId { get; init; }
        public Guid CustomerId { get; init; }
        public string InvoiceNumber { get; init; } = string.Empty;
        public string? CustomerCode { get; init; }
        public string? CustomerNameAr { get; init; }
        public DateTime InvoiceDate { get; init; }
        public DateTime DueDate { get; init; }
        public decimal TotalAmount { get; init; }
        public decimal PaidAmount { get; init; }

        /// <summary>مردودات مبيعات **مرحَّلة مرتبطة بهذه الفاتورة** وبتاريخ لا يتجاوز تاريخ القطع.</summary>
        public decimal PostedLinkedReturns { get; init; }

        /// <summary>
        /// المردودات **المؤثرة على حساب الذمم** فقط (تُخصم مرة واحدة) — أي مردودات الفواتير التي
        /// يُقيَّد مردودها دائن **1200** فعلًا:
        ///  • فاتورة <c>OnAccount</c> (آجلة) — القيد الافتراضي.
        ///  • فاتورة <c>Check</c> (سُدِّدت بشيك) — `SalesReturnService.Create.cs:163-172` يُقيّد دائن
        ///    1200 لها أيضًا، ويخفض `Customer.CurrentBalance` بنفس المنطق.
        /// أما مردود النقد/الدرج/البطاقة فيُقيَّد ضد 1105/1100/1205 (لا يخفض الذمم) ⇒ لا يُخصم من المتبقي.
        ///
        /// ═══ R16 (SUB-2b/P3 — المرحلة الثانية) ═══
        /// قبل الإصلاح كان الشرط `OnAccount` فقط ⇒ مردود فاتورة مسدَّدة بشيك كان يُقيَّد دائن 1200
        /// في الأستاذ ويُهمَل في التقرير ⇒ مبالغ مدينة مبالغ فيها بقيمة المردود. أُضيف شرط الشيك
        /// ليطابق التقرير الأستاذ (لا تغيير في معنى «مردود مؤثر» بل تصحيح مطابقته).
        /// </summary>
        public decimal EffectiveReturns { get; init; }

        /// <summary>نوع الفاتورة (SalesInvoiceType) — يستعمل لفحص نوع التسوية أعلاه.</summary>
        public int InvoiceType { get; init; }

        /// <summary>طريقة دفع الفاتورة (SalesPaymentMethod) — يستعمل لفحص R16 (مردود فاتورة الشيك).</summary>
        public int PaymentMethod { get; init; }

        /// <summary>صافي المخصَّص من سندات القبض: Σ(+1) − Σ(−1) حتى تاريخ القطع.</summary>
        public decimal AllocatedAmount { get; init; }

        /// <summary>الإجمالي − المدفوع على الفاتورة − المردودات المؤثرة − المخصَّص (قد تكون سالبة).</summary>
        public decimal ComputedResidual { get; init; }
    }

    /// <summary>
    /// مقبوضات عميل لا يوجد لها تخصيص موثق حتى تاريخ القطع — مفصولة بحسب ما يمكن إثباته من البيانات.
    /// </summary>
    public sealed class CustomerUnallocatedReceipts
    {
        public Guid CustomerId { get; init; }

        /// <summary>سندات قبض **مرحَّلة** (Draft/Approved/Cancelled/Reversed مستبعدة) — لا كيان/حقل ربط بفاتورة.</summary>
        public decimal ReceiptVouchers { get; init; }

        /// <summary>شيكات مستلمة **غير مرتجعة** بلا رابط فاتورة (لا تخصيص موثق يمكن إثباته).</summary>
        public decimal UnlinkedChecks { get; init; }

        /// <summary>شيكات مستلمة غير مرتجعة **لها رابط فاتورة موثق** — للعرض فقط ولا تُخصم في المرحلة A.</summary>
        public decimal InvoiceLinkedChecks { get; init; }

        /// <summary>المجموع الذي يمكن إثبات أنه «بلا تخصيص موثق» = سندات قبض + شيكات بلا رابط فاتورة.</summary>
        public decimal WithoutDocumentedAllocation => ReceiptVouchers + UnlinkedChecks;

        /// <summary>إجمالي كل ما قُبض من الطرف (بما فيه الشيكات المرتبطة بفاتورة) — للتدقيق فقط.</summary>
        public decimal TotalReceived => ReceiptVouchers + UnlinkedChecks + InvoiceLinkedChecks;
    }

    /// <summary>
    /// فواتير البيع **المرحَّلة** حتى تاريخ القطع، كلٌّ بـ«متبقٍّ محسوب» من المستندات المتاحة.
    /// استعلامان مجمَّعان فقط (فواتير + مردودات مجمَّعة بـGROUP BY) ⇒ لا استعلام لكل فاتورة.
    /// </summary>
    public async Task<List<CustomerInvoiceResidual>> GetComputedResidualsAsync(
        DateTime asOf, CancellationToken ct = default)
    {
        // P0-2J: حد أعلى حصري (بداية اليوم التالي) ⇒ يشمل يوم القطع كاملًا مهما كان وقت المستند.
        var cutoffExclusive = ExclusiveCutoff(asOf);

        // مردودات مبيعات مرحَّلة مرتبطة بفاتورة، بتجميع واحد لكل فاتورة (لا N+1).
        // تاريخ القطع يُطبَّق على تاريخ المردود نفسه ⇒ مردود لاحق لا يُخصَم من تقرير تاريخي سابق.
        var returnsByInvoice = (await _context.Set<SalesReturn>().AsNoTracking()
                .Where(r => !r.IsDeleted
                            && r.Status == DocumentStatus.Posted
                            && r.ReturnDate < cutoffExclusive)
                .GroupBy(r => r.SalesInvoiceId)
                .Select(g => new { InvoiceId = g.Key, Total = g.Sum(x => x.TotalAmount) })
                .ToListAsync(ct))
            .ToDictionary(x => x.InvoiceId, x => x.Total);

        var invoices = await _context.Set<SalesInvoice>().AsNoTracking()
            .Where(s => !s.IsDeleted
                        && s.Status == DocumentStatus.Posted
                        && s.InvoiceDate < cutoffExclusive)
            .OrderBy(s => s.InvoiceDate).ThenBy(s => s.InvoiceNumber)
            .Select(s => new
            {
                s.Id,
                s.CustomerId,
                s.InvoiceNumber,
                s.InvoiceDate,
                s.DueDate,
                s.TotalAmount,
                s.PaidAmount,
                s.InvoiceType,
                s.PaymentMethod,
                CustomerCode = s.Customer != null ? s.Customer.Code : null,
                CustomerNameAr = s.Customer != null ? s.Customer.NameAr : null
            })
            .ToListAsync(ct);

        // P0-2 (المرحلة B): صافي المخصَّص لكل فاتورة — تجميع واحد (لا N+1).
        // تاريخ القطع يُطبَّق على **تاريخ إنشاء التخصيص** (AllocatedAt) فلا يظهر تخصيص لاحق في تقرير سابق.
        var netAllocatedByInvoice = await GetNetAllocatedByInvoiceAsync(asOf, ct);

        var result = new List<CustomerInvoiceResidual>(invoices.Count);
        foreach (var s in invoices)
        {
            var linkedReturns = returnsByInvoice.GetValueOrDefault(s.Id, 0m);
            var isOnAccount = s.InvoiceType == SalesInvoiceType.OnAccount;
            // R16: المردود يخفض الذمم في حالتين فقط لأنهما الحالتان اللتان يُقيَّد فيهما دائن 1200:
            //   آجلة (OnAccount) أو مسدَّدة بشيك (Check — SUB-2b). مردود النقد/البطاقة يعكس 1100/1105/1205.
            var hitsReceivable = isOnAccount || s.PaymentMethod == SalesPaymentMethod.Check;
            var effectiveReturns = hitsReceivable ? linkedReturns : 0m;
            var allocated = netAllocatedByInvoice.GetValueOrDefault(s.Id, 0m);

            result.Add(new CustomerInvoiceResidual
            {
                InvoiceId = s.Id,
                CustomerId = s.CustomerId,
                InvoiceNumber = s.InvoiceNumber,
                CustomerCode = s.CustomerCode,
                CustomerNameAr = s.CustomerNameAr,
                InvoiceDate = s.InvoiceDate,
                DueDate = (s.DueDate ?? s.InvoiceDate).Date,
                TotalAmount = s.TotalAmount,
                PaidAmount = s.PaidAmount,
                InvoiceType = (int)s.InvoiceType,
                PaymentMethod = (int)s.PaymentMethod,
                PostedLinkedReturns = linkedReturns,
                EffectiveReturns = effectiveReturns,
                AllocatedAmount = allocated,
                ComputedResidual = MoneyRounding.Round(s.TotalAmount - s.PaidAmount - effectiveReturns - allocated)
            });
        }
        return result;
    }

    /// <summary>
    /// صافي المخصَّص لكل فاتورة (Σ+1 − Σ−1) حتى تاريخ القطع بتجميع واحد (بلا N+1).
    /// </summary>
    public async Task<Dictionary<Guid, decimal>> GetNetAllocatedByInvoiceAsync(
        DateTime asOf, CancellationToken ct = default)
    {
        var cutoffExclusive = ExclusiveCutoff(asOf);

        var rows = await _context.Set<CustomerPaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.AllocatedAt < cutoffExclusive)
            .GroupBy(a => new { a.SalesInvoiceId, a.Direction })
            .Select(g => new { g.Key.SalesInvoiceId, g.Key.Direction, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var map = new Dictionary<Guid, decimal>();
        foreach (var r in rows)
        {
            map.TryGetValue(r.SalesInvoiceId, out var current);
            var delta = r.Direction == 1 ? r.Total : -r.Total;
            map[r.SalesInvoiceId] = current + delta;
        }
        return map;
    }

    /// <summary>صافي المخصَّص لكل سند قبض (Σ+1 − Σ−1) حتى تاريخ القطع — لتحديد «المتاح للتخصيص».</summary>
    public async Task<Dictionary<Guid, decimal>> GetNetAllocatedByVoucherAsync(
        DateTime asOf, CancellationToken ct = default)
    {
        var cutoffExclusive = ExclusiveCutoff(asOf);

        var rows = await _context.Set<CustomerPaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.AllocatedAt < cutoffExclusive)
            .GroupBy(a => new { a.ReceiptVoucherId, a.Direction })
            .Select(g => new { g.Key.ReceiptVoucherId, g.Key.Direction, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var map = new Dictionary<Guid, decimal>();
        foreach (var r in rows)
        {
            map.TryGetValue(r.ReceiptVoucherId, out var current);
            var delta = r.Direction == 1 ? r.Total : -r.Total;
            map[r.ReceiptVoucherId] = current + delta;
        }
        return map;
    }

    /// <summary>
    /// المقبوضات (سندات قبض/شيكات) لكل عميل حتى تاريخ القطع، مفصولة إلى:
    /// ما هو بلا تخصيص موثق · شيكات بلا رابط فاتورة · شيكات مرتبطة بفاتورة.
    /// استعلامان مجمَّعان فقط (لا استعلام لكل عميل).
    /// </summary>
    public async Task<Dictionary<Guid, CustomerUnallocatedReceipts>> GetReceiptsByCustomerAsync(
        DateTime asOf, CancellationToken ct = default)
    {
        // P0-2J: حد أعلى حصري (بداية اليوم التالي) ⇒ يشمل يوم القطع كاملًا مهما كان وقت المستند.
        var cutoffExclusive = ExclusiveCutoff(asOf);

        // سندات القبض: المرحَّلة فقط (لا مسودة/معتمد/ملغى/معكوس) وحتى تاريخ السند.
        var receipts = await _context.Set<ReceiptVoucher>().AsNoTracking()
            .Where(v => !v.IsDeleted
                        && v.Status == VoucherStatus.Posted
                        && v.VoucherDate < cutoffExclusive)
            .GroupBy(v => v.CustomerId)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        // P0-2: ما تم تخصيصه فعليًا لكل عميل ⇒ يُخصم من بند «بلا تخصيص موثق» (منع الازدواج:
        // المبلغ المخصَّص يُخصم من متبقي الفاتورة، فلا يجوز أن يظهر أيضًا كمقبوض غير مخصص).
        var netAllocatedByCustomer = await GetNetAllocatedByCustomerAsync(asOf, ct);

        // الشيكات: مستلمة من عميل · غير مرتجعة (المرتجع أثره معكوس على 1200) · حتى تاريخ الدخول.
        // ملاحظة: BankCheck ليس BaseEntity ⇒ لا حذف منطقي عليه (بلا مرشّح IsDeleted).
        var checks = await _context.Set<BankCheck>().AsNoTracking()
            .Where(c => c.CustomerId != null
                        && c.Direction == BankCheckDirection.ReceivedFromCustomer
                        && c.Status != BankCheckStatus.Bounced
                        && c.ReceivedOrIssuedDate < cutoffExclusive)
            .Select(c => new { c.CustomerId, c.Amount, c.RelatedInvoiceId })
            .ToListAsync(ct);

        var map = new Dictionary<Guid, CustomerUnallocatedReceipts>();
        foreach (var r in receipts)
        {
            var allocated = netAllocatedByCustomer.GetValueOrDefault(r.CustomerId, 0m);
            var unallocated = MoneyRounding.Round(r.Total - allocated);
            if (unallocated < 0m) unallocated = 0m;   // لا يُعرض «غير مخصص» سالب (تخصيص تجاوز لا يمر بفحص المجاميع)
            map[r.CustomerId] = new CustomerUnallocatedReceipts
            {
                CustomerId = r.CustomerId,
                ReceiptVouchers = unallocated
            };
        }

        // كل شيك يُحتسب **مرة واحدة** وفي بند واحد فقط: إما «بلا رابط فاتورة» أو «مرتبط بفاتورة».
        foreach (var c in checks)
        {
            var customerId = c.CustomerId!.Value;
            map.TryGetValue(customerId, out var current);
            var linked = c.RelatedInvoiceId is not null;

            map[customerId] = new CustomerUnallocatedReceipts
            {
                CustomerId = customerId,
                ReceiptVouchers = current?.ReceiptVouchers ?? 0m,
                UnlinkedChecks = MoneyRounding.Round((current?.UnlinkedChecks ?? 0m) + (linked ? 0m : c.Amount)),
                InvoiceLinkedChecks = MoneyRounding.Round((current?.InvoiceLinkedChecks ?? 0m) + (linked ? c.Amount : 0m))
            };
        }

        return map;
    }

    /// <summary>صافي المخصَّص لكل عميل (Σ+1 − Σ−1) حتى تاريخ القطع — بتجميع واحد.</summary>
    public async Task<Dictionary<Guid, decimal>> GetNetAllocatedByCustomerAsync(
        DateTime asOf, CancellationToken ct = default)
    {
        var cutoffExclusive = ExclusiveCutoff(asOf);

        var rows = await _context.Set<CustomerPaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.AllocatedAt < cutoffExclusive)
            .GroupBy(a => new { a.CustomerId, a.Direction })
            .Select(g => new { g.Key.CustomerId, g.Key.Direction, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var map = new Dictionary<Guid, decimal>();
        foreach (var r in rows)
        {
            map.TryGetValue(r.CustomerId, out var current);
            map[r.CustomerId] = current + (r.Direction == 1 ? r.Total : -r.Total);
        }
        return map;
    }

    /// <summary>
    /// R13 — نشاط رد أموال العملاء حتى تاريخ القطع (قراءة فقط، مجمَّع، بلا N+1).
    ///
    /// المصدر: سندات صرف بطرف عميل (`PaymentVouchers.CustomerId`) **المرحَّلة** فقط، مربوطة
    /// بمردودها (`SalesReturnId` ⇒ `SalesReturns.SalesInvoiceId`) لتنسب الرد إلى فاتورة المردود.
    /// الرد المعكوس الكامل يخرج تلقائيًا لأن حالة أصله تصبح `Reversed` (ليست Posted)، ويُطرح أي
    /// مستند عكس **مرحَّل** من الأصلي المرحَّل (تحصين للعكس الجزئي إن سُمح به مستقبلًا) ⇒ لا ازدواج.
    ///
    /// ⚠️ لا تُستخدم `Customer.CurrentBalance` هنا: التقرير «حتى تاريخ» يُبنى من المستندات لا من رصيد لحظي.
    /// </summary>
    public async Task<CustomerRefundActivity> GetRefundActivityAsync(
        DateTime asOf, CancellationToken ct = default)
    {
        var cutoffExclusive = ExclusiveCutoff(asOf);
        var activity = new CustomerRefundActivity();

        // (1) الردود المرحَّلة: كل سند يُنسب لفاتورة مردوده وللعميل نفسه.
        var posted = await (
            from v in _context.Set<PaymentVoucher>().AsNoTracking()
            join r in _context.Set<SalesReturn>().AsNoTracking() on v.SalesReturnId equals r.Id
            where v.CustomerId != null
                  && v.SalesReturnId != null
                  && !v.IsDeleted
                  && v.Status == VoucherStatus.Posted
                  && v.VoucherDate < cutoffExclusive
            select new { v.CustomerId, InvoiceId = r.SalesInvoiceId, v.Amount })
            .ToListAsync(ct);

        // (2) العكوس المرحَّلة لأصل مرحَّل: تُطرح من نفس الفاتورة/العميل (تصحيح لا ازدواج).
        var reversed = await (
            from rev in _context.Set<PaymentVoucherReversal>().AsNoTracking()
            join v in _context.Set<PaymentVoucher>().AsNoTracking() on rev.OriginalPaymentVoucherId equals v.Id
            join r in _context.Set<SalesReturn>().AsNoTracking() on v.SalesReturnId equals r.Id
            where rev.CustomerId != null
                  && !rev.IsDeleted
                  && rev.Status == VoucherStatus.Posted
                  && rev.VoucherDate < cutoffExclusive
            select new { rev.CustomerId, InvoiceId = r.SalesInvoiceId, rev.Amount })
            .ToListAsync(ct);

        foreach (var row in posted)
        {
            if (row.CustomerId is not { } customerId) continue;
            activity.ByInvoice[row.InvoiceId] =
                MoneyRounding.Round(activity.ByInvoice.GetValueOrDefault(row.InvoiceId) + row.Amount);
            activity.ByCustomer[customerId] =
                MoneyRounding.Round(activity.ByCustomer.GetValueOrDefault(customerId) + row.Amount);
        }

        foreach (var row in reversed)
        {
            if (row.CustomerId is not { } customerId) continue;
            activity.ByInvoice[row.InvoiceId] =
                MoneyRounding.Round(activity.ByInvoice.GetValueOrDefault(row.InvoiceId) - row.Amount);
            activity.ByCustomer[customerId] =
                MoneyRounding.Round(activity.ByCustomer.GetValueOrDefault(customerId) - row.Amount);
        }

        return activity;
    }

    /// <summary>
    /// نشاط ردود الأموال المجمَّع: لكل فاتورة (مفتاح = معرّف فاتورة المردود) ولكل عميل.
    /// القيم موجبة = مبالغ خرجت فعلًا للعميل؛ تُطرح من الرصيد الدائن في تقرير الأعمار مرة واحدة.
    /// </summary>
    public sealed class CustomerRefundActivity
    {
        public Dictionary<Guid, decimal> ByInvoice { get; } = new();
        public Dictionary<Guid, decimal> ByCustomer { get; } = new();

        public decimal Total => MoneyRounding.Round(ByInvoice.Values.Sum());
    }

    /// <summary>
    /// D‑C / D‑D — فحص **قراءة فقط** لمخالفات سقف الرد على مستوى المستندات:
    /// لكل مردود، مجموع الردود **المرحَّلة** (حالة السند `Posted` فقط ⇒ المعكوس خارج تلقائيًا)
    /// يُقارن بـ`SalesReturn.TotalAmount`. أي زيادة تُعاد مجمَّعة لكل عميل.
    ///
    /// الغرض: كشف أي كتابة **خارج الخدمة** (SQL/إصلاح بيانات) تجاوزت السقف الذي يفرضه
    /// `PaymentVoucherService.PostCustomerRefundCoreAsync` (سقف مزدوج + قفلان + معاملة).
    /// لا يُعدَّل أي صف، ولا يُنشأ تخصيص، ولا تُسوَّى أي قيمة.
    ///
    /// القيود المعلنة:
    ///  • مردود محذوف/غير مرحَّل مع ردود مرحَّلة عليه ⇒ يُعامَل سقفه كصفر (مخالفة كاملة).
    ///  • العكس الجزئي غير مدعوم في الخدمة؛ أي عكس مرحَّل يجعل حالة الأصل `Reversed` فيخرج كاملًا.
    ///  • لا يُستخدم `Customer.CurrentBalance` هنا (الفحص مستندي «حتى تاريخ القطع»).
    /// </summary>
    public async Task<Dictionary<Guid, decimal>> GetRefundCapViolationsAsync(
        DateTime asOf, CancellationToken ct = default)
    {
        var cutoffExclusive = ExclusiveCutoff(asOf);

        var posted = await _context.Set<PaymentVoucher>().AsNoTracking()
            .Where(v => v.CustomerId != null
                        && v.SalesReturnId != null
                        && !v.IsDeleted
                        && v.Status == VoucherStatus.Posted
                        && v.VoucherDate < cutoffExclusive)
            .GroupBy(v => new { ReturnId = v.SalesReturnId!.Value, CustomerId = v.CustomerId!.Value })
            .Select(g => new { g.Key.ReturnId, g.Key.CustomerId, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, decimal>();
        if (posted.Count == 0) return result;

        var returnIds = posted.Select(x => x.ReturnId).Distinct().ToList();
        var returns = await _context.Set<SalesReturn>().AsNoTracking()
            .Where(r => returnIds.Contains(r.Id))
            .Select(r => new { r.Id, r.TotalAmount, r.Status, r.IsDeleted })
            .ToDictionaryAsync(x => x.Id, ct);

        foreach (var g in posted)
        {
            // مردود غير موجود (مستحيل مع FK) أو غير مرحَّل/محذوف ⇒ لا سقف موثق يغطي الرد.
            var cap = returns.TryGetValue(g.ReturnId, out var ret)
                      && !ret.IsDeleted
                      && ret.Status == DocumentStatus.Posted
                ? ret.TotalAmount
                : 0m;

            var excess = MoneyRounding.Round(g.Total - cap);
            if (excess <= 0.005m) continue;

            result[g.CustomerId] = MoneyRounding.Round(result.GetValueOrDefault(g.CustomerId) + excess);
        }

        return result;
    }

    /// <summary>
    /// فواتير عميل **قابلة للتخصيص** (مرحَّلة آجلة بمتاح موجب) مع كل مكوّن في حقل مستقل —
    /// تُستخدم في شاشة سند القبض. الاستعلامات مجمَّعة (بلا N+1).
    /// </summary>
    public async Task<List<CustomerOpenInvoiceDto>> GetEligibleInvoicesAsync(
        Guid customerId, DateTime asOf, CancellationToken ct = default)
    {
        var all = await GetComputedResidualsAsync(asOf, ct);

        return all.Where(x => x.CustomerId == customerId)
            .OrderBy(x => x.InvoiceDate).ThenBy(x => x.InvoiceId)   // ترتيب FIFO الحتمي نفسه
            .Select(x =>
            {
                var onAccount = x.InvoiceType == (int)SalesInvoiceType.OnAccount;
                var available = x.ComputedResidual > 0.01m ? MoneyRounding.Round(x.ComputedResidual) : 0m;
                var eligible = onAccount && available > 0m;

                string? reason = null;
                if (!onAccount) reason = "فاتورة غير آجلة (مسدَّدة نقدًا/ببطاقة/بشيك) — لا تُخصَّص.";
                else if (x.ComputedResidual < -0.005m) reason = "رصيد دائن/مطالبة — يحتاج مراجعة قبل التخصيص.";
                else if (available <= 0m) reason = "لا يوجد متبقٍّ قابل للتخصيص (مدفوعة بالكامل).";

                return new CustomerOpenInvoiceDto
                {
                    InvoiceId = x.InvoiceId,
                    InvoiceNumber = x.InvoiceNumber,
                    InvoiceDate = x.InvoiceDate,
                    DueDate = x.DueDate,
                    TotalAmount = x.TotalAmount,
                    PaidAmount = x.PaidAmount,
                    EffectiveReturns = x.EffectiveReturns,
                    AllocatedAmount = x.AllocatedAmount,
                    Residual = x.ComputedResidual,
                    Available = available,
                    IsEligible = eligible,
                    IneligibilityReason = reason
                };
            })
            .ToList();
    }

    /// <summary>
    /// حالة تخصيص سند قبض واحد: صفوفه (مع الحالة المشتقّة) + إجمالي المخصَّص + غير المخصص
    /// + الفواتير المؤهَّلة للعميل. تُستخدم في شاشة سند القبض وفي سجل التدقيق.
    /// </summary>
    public async Task<ReceiptAllocationStateDto?> GetVoucherAllocationStateAsync(
        Guid voucherId, DateTime asOf, CancellationToken ct = default)
    {
        var cutoffExclusive = ExclusiveCutoff(asOf);

        var voucher = await _context.Set<ReceiptVoucher>().AsNoTracking()
            .Where(v => v.Id == voucherId && !v.IsDeleted)
            .Select(v => new { v.Id, v.VoucherNumber, v.Status, v.CustomerId, v.VoucherDate, v.Amount })
            .FirstOrDefaultAsync(ct);
        if (voucher is null) return null;

        var allocations = await _context.Set<CustomerPaymentAllocation>().AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReceiptVoucherId == voucherId)
            .OrderBy(a => a.AllocatedAt).ThenBy(a => a.Id)
            .Select(a => new CustomerAllocationDto
            {
                Id = a.Id,
                ReceiptVoucherId = a.ReceiptVoucherId,
                SalesInvoiceId = a.SalesInvoiceId,
                Amount = a.Amount,
                Direction = a.Direction,
                ReversalOfAllocationId = a.ReversalOfAllocationId,
                AllocatedAt = a.AllocatedAt,
                Note = a.Note
            })
            .ToListAsync(ct);

        // أرقام الفواتير للعرض (استعلام واحد).
        var invoiceIds = allocations.Select(a => a.SalesInvoiceId).Distinct().ToList();
        var numbers = await _context.Set<SalesInvoice>().AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.InvoiceNumber })
            .ToDictionaryAsync(x => x.Id, x => x.InvoiceNumber, ct);
        foreach (var a in allocations)
            a.InvoiceNumber = numbers.GetValueOrDefault(a.SalesInvoiceId, string.Empty);

        // صفوف أُنشئت **بعد** تاريخ القطع: موجودة في القاعدة لكن لا تدخل احتساب «حتى تاريخ».
        var allocatedAsOf = allocations
            .Where(a => a.AllocatedAt < cutoffExclusive)
            .Sum(a => a.Direction == 1 ? a.Amount : -a.Amount);

        return new ReceiptAllocationStateDto
        {
            ReceiptVoucherId = voucher.Id,
            VoucherNumber = voucher.VoucherNumber,
            Status = (int)voucher.Status,
            CustomerId = voucher.CustomerId,
            VoucherDate = voucher.VoucherDate,
            Amount = voucher.Amount,
            AllocatedAmount = MoneyRounding.Round(allocatedAsOf),
            UnallocatedAmount = MoneyRounding.Round(voucher.Amount - allocatedAsOf),
            Allocations = allocations,
            OpenInvoices = await GetEligibleInvoicesAsync(voucher.CustomerId, asOf, ct)
        };
    }
}

