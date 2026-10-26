using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Domain.Enums;
using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERPSystem.Application.Services;

/// <summary>
/// ملف تقارير تحليلية للذمم والمؤشرات المالية — إضافة فقط، لا تعدل أي خدمة إيداع.
/// </summary>
public static class AgingReportQueries
{
    /// <summary>
    /// يجمع الفواتير المفتوحة (Total - Paid &gt; 0) حسب الطرف (عميل/مورد)
    /// ويوزّعها على فئات عمرية مبنية على تاريخ الاستحقاق (DueDate ?? InvoiceDate).
    /// IsReceivable: AR على SalesInvoice، و AP على PurchaseInvoice.
    /// </summary>
    public static async Task<AgingReportDto> GetAgingAsync(DbContext db, bool isReceivable, DateTime asOf)
    {
        var dto = new AgingReportDto { AsOf = asOf.Date, IsReceivable = isReceivable };
        var parties = new List<AgingPartyRow>();
        var asOfDate = asOf.Date;

        if (isReceivable)
        {
            // ═══ P0-2H (المرحلة A) — قراءة فقط، بلا Migration وبلا كيان تخصيص مقبوضات ═══
            //  (1) «متبقٍّ محسوب» لكل فاتورة مرحَّلة: الإجمالي − المدفوع على الفاتورة − مردوداتها
            //      المرحَّلة المرتبطة بها حتى تاريخ القطع. **لا يُطرح أي سند قبض من أي فاتورة**
            //      لعدم وجود علاقة تخصيص موثقة للعملاء (سند القبض لا يحمل أي حقل ربط بفاتورة).
            //  (2) المقبوضات بلا تخصيص موثق تُعرض **بندًا مستقلًا** ولا تدخل متبقي أي فاتورة:
            //      سندات قبض مرحَّلة + شيكات مقبوضة بلا رابط فاتورة (وصف صريح: «بلا تخصيص موثق»
            //      وليس «غير مخصص مؤكد» — لا يوجد كيان تخصيص للعملاء بعد).
            //  (3) الشيكات ذات الرابط الموثق بفاتورة تُعرض منفصلة **ولا تُخصم** في هذه المرحلة
            //      (منعًا لعدّ الشيك مرتين مع PaidAmount في الفواتير المسدَّدة بشيك).
            //  ملاحظة: القيمة القديمة (Total − PaidAmount) لم تكن مؤكدة (P0-2B/P0-2E) ⇒ تُعرض الآن
            //  بوصفها «متبقٍّ محسوبًا من المستندات المتاحة» لا رصيدًا نهائيًا.
            var allocation = new CustomerAllocationQueries(db);
            var residuals = await allocation.GetComputedResidualsAsync(asOf);
            var receiptsByCustomer = await allocation.GetReceiptsByCustomerAsync(asOf);

            // ═══ R13 (SUB-2b/P3 — المرحلة الثانية): الردود المرحَّلة تُخصم من الرصيد الدائن مرة واحدة ═══
            // نموذج التقرير يبقى «قائمًا على الفواتير»: لا تُنشأ تخصيصات ولا تُمسّ الفئات العمرية المدينة.
            // الرد يُنسب إلى فاتورة مردوده (سند الرد يحمل SalesReturnId إلزاميًا) ويُطبَّق على رصيدها
            // الدائن فقط، بسقف (رصيد الفاتورة الدائن) و(سقف المردود نفسه) و(إجمالي ردود العميل).
            var refunds = await allocation.GetRefundActivityAsync(asOf);

            // ═══ D‑C / D‑D: مخالفات سقف الرد (قراءة فقط) — تُعرض تعارضًا يحتاج مراجعة ═══
            // لا يمكن أن تنشأ عبر الخدمة (سقف مزدوج + قفلان)، فهي دليل على كتابة خارج الخدمة.
            var refundCapViolations = await allocation.GetRefundCapViolationsAsync(asOf);

            var refundPool = new Dictionary<Guid, decimal>();   // متبقٍّ من ردود العميل لم يُطبَّق بعد
            foreach (var kv in refunds.ByCustomer)
            {
                var positive = MoneyRounding.Round(Math.Max(0m, kv.Value));
                if (positive > 0m) refundPool[kv.Key] = positive;
            }
            var appliedByInvoice = new Dictionary<Guid, decimal>();
            var appliedByCustomer = new Dictionary<Guid, decimal>();

            // الإجماليات على مستوى التقرير (كل العملاء حتى تاريخ القطع) — بند مستقل عن أرصدة الفواتير.
            foreach (var r in receiptsByCustomer.Values)
            {
                dto.UnallocatedReceiptsTotal += r.ReceiptVouchers;
                dto.UnallocatedChecksTotal += r.UnlinkedChecks;
                dto.InvoiceLinkedChecksTotal += r.InvoiceLinkedChecks;
            }
            dto.UnallocatedReceiptsTotal = Math.Round(dto.UnallocatedReceiptsTotal, 2);
            dto.UnallocatedChecksTotal = Math.Round(dto.UnallocatedChecksTotal, 2);
            dto.InvoiceLinkedChecksTotal = Math.Round(dto.InvoiceLinkedChecksTotal, 2);

            foreach (var s in residuals)
            {
                var original = s.ComputedResidual;

                // R13: تطبيق الرد على الرصيد الدائن لهذه الفاتورة فقط (لا على الرصيد المدين أبدًا).
                var applied = 0m;
                if (original < -0.005m
                    && refundPool.TryGetValue(s.CustomerId, out var pool) && pool > 0m)
                {
                    var invoiceQuota = MoneyRounding.Round(
                        refunds.ByInvoice.GetValueOrDefault(s.InvoiceId)
                        - appliedByInvoice.GetValueOrDefault(s.InvoiceId));
                    var credit = MoneyRounding.Round(-original);
                    // لا تطبيق سالب أبدًا: صافي ردود الفاتورة لا يكون سالبًا إلا إذا زادت عكوسها
                    // عن ردودها (حالة غير مسموحة حاليًا) ⇒ تُعامل كصفر لا كزيادة رصيد دائن.
                    applied = invoiceQuota <= 0m
                        ? 0m
                        : MoneyRounding.Round(Math.Min(credit, Math.Min(pool, invoiceQuota)));

                    if (applied > 0m)
                    {
                        refundPool[s.CustomerId] = MoneyRounding.Round(pool - applied);
                        appliedByInvoice[s.InvoiceId] =
                            MoneyRounding.Round(appliedByInvoice.GetValueOrDefault(s.InvoiceId) + applied);
                        appliedByCustomer[s.CustomerId] =
                            MoneyRounding.Round(appliedByCustomer.GetValueOrDefault(s.CustomerId) + applied);
                    }
                }

                var bal = MoneyRounding.Round(original + applied);

                // صف الطرف يُنشأ فقط للفواتير ذات أثر فعلي:
                // متبقٍّ موجب (مدين) أو رصيد دائن (مطالبة) ⇒ لا صفوف صفرية للفواتير المسدَّدة بالكامل.
                // استثناء R13: فاتورة صفّرها الرد تُعرض بسطر صفري (Balance = 0 + RefundsApplied) للتفسير.
                var isCredit = bal < -0.005m;
                if (!isCredit && bal <= 0.01m && applied <= 0m) continue;

                var due = s.DueDate;
                var days = (asOfDate - due).Days;

                var row = parties.FirstOrDefault(p => p.PartyId == s.CustomerId);
                if (row is null)
                {
                    row = new AgingPartyRow
                    {
                        PartyId = s.CustomerId,
                        PartyCode = s.CustomerCode ?? "",
                        PartyNameAr = s.CustomerNameAr ?? "زبون نقدي",
                        IsComputedResidual = true
                    };
                    parties.Add(row);

                    // حصة الطرف من المقبوضات (بند مستقل — لا يُخصم من أي فاتورة إلا بصفوف تخصيص فعلية).
                    if (receiptsByCustomer.TryGetValue(s.CustomerId, out var receipts))
                    {
                        row.UnallocatedReceipts = receipts.ReceiptVouchers;
                        row.UnallocatedChecks = receipts.UnlinkedChecks;
                        row.InvoiceLinkedChecks = receipts.InvoiceLinkedChecks;
                    }
                }

                // مكوّنات الطرف — كل بند في حقل واضح (للفواتير المؤثرة فقط).
                row.InvoicedTotal = MoneyRounding.Round(row.InvoicedTotal + s.TotalAmount);
                row.AllocatedTotal = MoneyRounding.Round(row.AllocatedTotal + s.AllocatedAmount);
                row.EffectiveReturnsTotal = MoneyRounding.Round(row.EffectiveReturnsTotal + s.EffectiveReturns);

                if (isCredit)
                {
                    // P0-2: رصيد دائن للعميل (مطالبة) — يُعرض في عمود مستقل ولا يُخفى ولا يُصفَّر.
                    row.CreditBalance = MoneyRounding.Round(row.CreditBalance + bal);

                    // ═══ D‑D: إظهار الرصيد الدائن **الخاص بالفاتورة** في تفصيل الفواتير ═══
                    // لا يُضاف إلى أي فئة عمرية ولا إلى Total (مستحق على العميل) ⇒ لا يغيّر معنى التقرير
                    // ولا يكرر الرصيد الدائن العام (كل سطر يحمل رصيد فاتورته فقط).
                    row.Invoices.Add(new AgingInvoiceLine
                    {
                        PartyId = s.CustomerId,
                        InvoiceNumber = s.InvoiceNumber,
                        InvoiceDate = s.InvoiceDate,
                        DueDate = due,
                        DaysOverdue = days,
                        Balance = bal,                                 // سالب = دائن
                        TotalAmount = s.TotalAmount,
                        PaidAmount = s.PaidAmount,
                        EffectiveReturns = s.EffectiveReturns,
                        AllocatedAmount = s.AllocatedAmount,
                        RefundsApplied = applied,
                        CreditBeforeRefunds = MoneyRounding.Round(-original),
                        IsCreditLine = true
                    });
                    continue;
                }

                var line = new AgingInvoiceLine
                {
                    PartyId = s.CustomerId,
                    InvoiceNumber = s.InvoiceNumber,
                    InvoiceDate = s.InvoiceDate,
                    DueDate = due,
                    DaysOverdue = days,
                    Balance = bal,
                    TotalAmount = s.TotalAmount,
                    PaidAmount = s.PaidAmount,
                    EffectiveReturns = s.EffectiveReturns,
                    AllocatedAmount = s.AllocatedAmount,
                    RefundsApplied = applied,
                    // فاتورة رصيدها الدائن صفّره الرد بالكامل: تبقى في التفصيل للتفسير.
                    CreditBeforeRefunds = original < -0.005m ? MoneyRounding.Round(-original) : 0m,
                    IsCreditLine = original < -0.005m
                };

                if (!isCredit && bal > 0.01m) AddBucket(row, days, bal);
                row.Invoices.Add(line);
            }

            // ═══ P0-2 G-2: عملاء لديهم مقبوضات/شيكات **غير مخصصة** بلا فاتورة مفتوحة ═══
            // كان صف الطرف يُنشأ حصرًا من الفواتير ذات الرصيد، فيغيب تفصيل عميل ليس له فاتورة مفتوحة
            // رغم احتساب مبلغه في إجماليات UnallocatedReceipts/UnallocatedChecks/InvoiceLinkedChecks أعلاه.
            // يُضاف هنا صف **مستقل** يحمل بنود «غير المخصص» فقط: بلا فواتير وبلا مساهمة في الفئات العمرية
            // ولا في أرصدة الفواتير (InvoicedTotal/AllocatedTotal/EffectiveReturnsTotal تبقى صفرًا)، فلا ازدواج.
            var receiptOnlyCustomers = receiptsByCustomer.Values
                .Where(r => (r.ReceiptVouchers != 0m || r.UnlinkedChecks != 0m || r.InvoiceLinkedChecks != 0m)
                            && !parties.Any(p => p.PartyId == r.CustomerId))
                .ToList();

            // R13: عملاء لديهم ردود مرحَّلة بلا صف (فاتورتهم صفّرها الرد أو لا فاتورة مفتوحة لهم).
            var refundOnlyCustomerIds = refunds.ByCustomer
                .Where(kv => kv.Value > 0m && !parties.Any(p => p.PartyId == kv.Key))
                .Select(kv => kv.Key)
                .ToList();

            var extraIds = receiptOnlyCustomers.Select(r => r.CustomerId)
                .Concat(refundOnlyCustomerIds)
                .Distinct()
                .ToList();
            if (extraIds.Count > 0)
            {
                var names = await db.Set<Customer>().AsNoTracking()
                    .Where(c => extraIds.Contains(c.Id) && !c.IsDeleted)
                    .Select(c => new { c.Id, c.Code, c.NameAr })
                    .ToDictionaryAsync(x => x.Id);

                foreach (var r in receiptOnlyCustomers)
                {
                    names.TryGetValue(r.CustomerId, out var info);
                    parties.Add(new AgingPartyRow
                    {
                        PartyId = r.CustomerId,
                        PartyCode = info?.Code ?? "",
                        PartyNameAr = info?.NameAr ?? "زبون نقدي",
                        UnallocatedReceipts = r.ReceiptVouchers,
                        UnallocatedChecks = r.UnlinkedChecks,
                        InvoiceLinkedChecks = r.InvoiceLinkedChecks,
                        IsComputedResidual = true
                    });
                }

                foreach (var customerId in refundOnlyCustomerIds)
                {
                    names.TryGetValue(customerId, out var info);
                    parties.Add(new AgingPartyRow
                    {
                        PartyId = customerId,
                        PartyCode = info?.Code ?? "",
                        PartyNameAr = info?.NameAr ?? "زبون نقدي",
                        IsComputedResidual = true
                    });
                }
            }

            // ═══ R13: تفكيك الرصيد الدائن لكل طرف — مستحق على العميل / مستحق للعميل / تم رده / قابل للرد ═══
            // • الفئات العمرية (أ) لا تُمسّ: لا يُخصم رد من رصيد مدين.
            // • CreditBeforeRefunds (ب) = |الرصيد الدائن الصافي| + الردود المطبَّقة عليه.
            // • RefundsPaid (ج) = سندات الرد المرحَّلة صافي العكوس المرحَّلة (≤ تاريخ القطع).
            // • RemainingRefundableCredit (د) = (ب) − (ج)، لا سالب.
            foreach (var party in parties)
            {
                refunds.ByCustomer.TryGetValue(party.PartyId, out var paidRaw);
                var paid = MoneyRounding.Round(Math.Max(0m, paidRaw));
                var applied = appliedByCustomer.GetValueOrDefault(party.PartyId);
                party.RefundsPaid = paid;
                party.CreditBeforeRefunds = MoneyRounding.Round(-party.CreditBalance + applied);
                party.RemainingRefundableCredit =
                    MoneyRounding.Round(Math.Max(0m, party.CreditBeforeRefunds - paid));
                party.RefundsBeyondInvoiceCredit =
                    MoneyRounding.Round(Math.Max(0m, paid - party.CreditBeforeRefunds));

                // ═══ D‑A / D‑D: مصادر الرصيد الدائن والتعارضات (عرض فقط — بلا أي ربط بفاتورة) ═══
                // • الردود تُطبَّق أولًا على رصيد الفواتير الدائن، وأي تجاوز يُعامل «ممولًا من الرصيد
                //   غير المخصص» ويُستهلك من بنده المستقل (وليس من أي فاتورة).
                // • إن تجاوزت الردود مجموع المصدرين معًا ⇒ تعارض يحتاج مراجعة (لا مصدر موثق).
                party.RefundsAppliedToInvoiceCredit = applied;
                var unallocated = MoneyRounding.Round(party.UnallocatedReceipts + party.UnallocatedChecks);
                party.UnallocatedCreditTotal = unallocated;
                var consumed = MoneyRounding.Round(Math.Min(unallocated, party.RefundsBeyondInvoiceCredit));
                party.UnallocatedCreditConsumedByRefunds = consumed;
                party.UnallocatedCreditRemaining = MoneyRounding.Round(unallocated - consumed);
                party.RefundsWithNoDocumentedCredit =
                    MoneyRounding.Round(Math.Max(0m, party.RefundsBeyondInvoiceCredit - consumed));
                party.RefundsBeyondReturnCap =
                    MoneyRounding.Round(Math.Max(0m, refundCapViolations.GetValueOrDefault(party.PartyId)));
            }

            // إجماليات المكوّنات + الأرصدة الدائنة (لا تُدمج في مجاميع الفئات).
            dto.TotalInvoiced = MoneyRounding.Round(parties.Sum(p => p.InvoicedTotal));
            dto.TotalAllocated = MoneyRounding.Round(parties.Sum(p => p.AllocatedTotal));
            dto.TotalEffectiveReturns = MoneyRounding.Round(parties.Sum(p => p.EffectiveReturnsTotal));

            // R13 — الأربعة مفصولة: مستحق على العميل (الفئات) · مستحق للعميل · تم رده · قابل للرد.
            dto.TotalCreditBeforeRefunds = MoneyRounding.Round(parties.Sum(p => p.CreditBeforeRefunds));
            dto.TotalRefundsPaid = MoneyRounding.Round(parties.Sum(p => p.RefundsPaid));
            dto.TotalRefundsAppliedToInvoiceCredit =
                MoneyRounding.Round(parties.Sum(p => p.RefundsAppliedToInvoiceCredit));
            dto.TotalRefundableCredit = MoneyRounding.Round(parties.Sum(p => p.RemainingRefundableCredit));
            dto.TotalRefundsBeyondInvoiceCredit =
                MoneyRounding.Round(parties.Sum(p => p.RefundsBeyondInvoiceCredit));

            // D‑A / D‑D — البنود المستقلة والتعارضات (لا تُدمج في مجاميع الفئات العمرية).
            dto.TotalUnallocatedCredit = MoneyRounding.Round(parties.Sum(p => p.UnallocatedCreditTotal));
            dto.TotalUnallocatedCreditConsumedByRefunds =
                MoneyRounding.Round(parties.Sum(p => p.UnallocatedCreditConsumedByRefunds));
            dto.TotalUnallocatedCreditRemaining =
                MoneyRounding.Round(parties.Sum(p => p.UnallocatedCreditRemaining));
            dto.TotalRefundsWithNoDocumentedCredit =
                MoneyRounding.Round(parties.Sum(p => p.RefundsWithNoDocumentedCredit));
            dto.TotalRefundsBeyondReturnCap =
                MoneyRounding.Round(parties.Sum(p => p.RefundsBeyondReturnCap));

            dto.ComputedResidualNote =
                "المتبقي لكل فاتورة = الإجمالي − المدفوع المسجَّل على الفاتورة − مردوداتها المؤثرة على الذمم " +
                "− المخصَّص من سندات القبض (تخصيص موثق). كل بند معروض في عمود مستقل: الإجمالي · المدفوع المسجَّل · " +
                "المردودات المؤثرة · المخصَّص · المتبقي. " +
                "المقبوضات التي لا تخصيص موثق لها تُعرض بندًا مستقلًا (سندات قبض بلا تخصيص + شيكات بلا رابط فاتورة) " +
                "ولا تُخصم من أي فاتورة؛ والشيكات المرتبطة بفاتورة تُعرض منفصلة ولا تُخصم لأن أثرها محتسب في مساره " +
                "(PaidAmount/1102) فلا يُحتسب مرتين. " +
                "المردود يُخصم **مرة واحدة** ويُقيَّد فقط إذا كانت الفاتورة آجلة أو مسدَّدة بشيك " +
                "(الحالتان اللتان يُقيَّد فيهما دائن 1200 — R16)، أما مردود النقد/البطاقة فلا يخفض الذمم. " +
                "الأرصدة الدائنة للعملاء تُعرض في عمود مستقل ولا تُدمج مع مجاميع الفئات. " +
                "الردود المرحَّلة (سندات صرف بطرف عميل) تُخصم **مرة واحدة** من الرصيد الدائن لفاتورة مردودها " +
                "(R13): الرصيد الدائن قبل الردود والردود المدفوعة والمتبقي القابل للرد في ثلاثة أعمدة منفصلة، " +
                "والفئات العمرية المدينة لا تتأثر بأي رد. الرد المعكوس الكامل يخرج تلقائيًا (حالة الأصل Reversed). " +
                "ما تجاوز رصيد الفواتير الدائن من ردود يُعرض بندًا تدقيقيًا منفصلًا ولا يُخصم من أي فاتورة. " +
                "الرصيد الدائن غير المخصص (سندات قبض بلا تخصيص موثق + شيكات بلا رابط فاتورة) يبقى بندًا مستقلًا، " +
                "ويُستهلك منه فقط ما موّل ردودًا تجاوزت رصيد الفواتير الدائن — بلا إنشاء أي تخصيص وبلا مساس بأي فاتورة " +
                "(D‑A). كل سطر فاتورة في التفصيل يحمل رصيده الدائن **الخاص** قبل الردود (لا الرصيد العام للعميل) " +
                "ويُوسم أنه سطر دائن فلا يدخل الفئات المدينة (D‑D). أي رد يتجاوز المصدرين معًا، أو يتجاوز سقف مردوده " +
                "المرجعي، يُعرض **تعارضًا يحتاج مراجعة** ولا يُصحَّح تلقائيًا. " +
                "هذه الأرقام **ليست مطابقة كاملة** لرصيد حساب الذمم 1200: قد توجد قيود يدوية/افتتاحية، وعهدة مندوبين " +
                "(1100/1110 ↔ 1200)، وحركات بلا مرجع عميل — تُعرض فروقًا قابلة للتتبع في تقرير المطابقة ولا تُسوَّى تلقائيًا.";
        }
        else
        {
            // PU-04 (D12): أرصدة الموردين تُبنى على الرصيد المتبقي R (وليس Total − Paid)،
            // مع تطبيق asOf على مجموعة المستندات، واستبعاد المسودات/الملغيات،
            // وعرض الأرصدة السالبة صراحةً (CreditBalance) بدل إسقاطها أو تصفيرها.
            var invs = await db.Set<PurchaseInvoice>()
                .Where(s => !s.IsDeleted
                            && s.Status == DocumentStatus.Posted
                            && s.InvoiceType == PurchaseInvoiceType.OnAccount
                            && s.InvoiceDate <= asOfDate)
                .Include(s => s.Supplier)
                .ToListAsync();

            var residualQueries = new SupplierAllocationQueries(db);

            foreach (var s in invs)
            {
                var residual = await residualQueries.GetResidualAsync(s.Id);
                var bal = residual?.Residual ?? 0m;
                if (bal == 0m) continue;

                var due = (s.DueDate ?? s.InvoiceDate).Date;
                var days = (asOfDate - due).Days;
                var line = new AgingInvoiceLine
                {
                    InvoiceNumber = s.InvoiceNumber,
                    InvoiceDate = s.InvoiceDate,
                    DueDate = due,
                    DaysOverdue = days,
                    Balance = bal
                };

                var row = parties.FirstOrDefault(p => p.PartyId == s.SupplierId);
                if (row is null)
                {
                    row = new AgingPartyRow
                    {
                        PartyId = s.SupplierId,
                        PartyCode = s.Supplier?.Code ?? "",
                        PartyNameAr = s.Supplier?.NameAr ?? "مورد"
                    };
                    parties.Add(row);
                }

                if (bal < 0m)
                    row.CreditBalance += bal;   // لا يُصفَّر ولا يُخفى
                else
                    AddBucket(row, days, bal);

                row.Invoices.Add(line);
            }
        }

        // ترتيب تنازلي بالإجمالي
        dto.Rows = parties.OrderByDescending(p => p.Total).ToList();
        dto.Total0_30 = Math.Round(parties.Sum(p => p.Bucket0_30), 2);
        dto.Total31_60 = Math.Round(parties.Sum(p => p.Bucket31_60), 2);
        dto.Total61_90 = Math.Round(parties.Sum(p => p.Bucket61_90), 2);
        dto.Total90Plus = Math.Round(parties.Sum(p => p.Bucket90Plus), 2);
        dto.TotalCredit = Math.Round(parties.Sum(p => p.CreditBalance), 2);
        return dto;
    }

    private static void AddBucket(AgingPartyRow row, int days, decimal bal)
    {
        if (days <= 30) row.Bucket0_30 += bal;
        else if (days <= 60) row.Bucket31_60 += bal;
        else if (days <= 90) row.Bucket61_90 += bal;
        else row.Bucket90Plus += bal;
    }

    /// <summary>
    /// أعلى N من العملاء حسب إجمالي صافي المبيعات (فواتير غير ملغاة حتى asOf)
    /// مع نسبة كل منهم من إجمالي المبيعات، ويعيد أيضاً ما إذا كان أعلى 5
    /// يتجاوزون 70% من الإجمالي (خطر تركيز مرتفع).
    /// </summary>
    public static async Task<(List<TopCustomerRow> Top, bool Warning)> GetTopCustomersAsync(
        DbContext db, DateTime asOf, int topN = 5, decimal warningThresholdPct = 70m)
    {
        var invoices = await db.Set<SalesInvoice>()
            .Where(s => !s.IsDeleted && s.Status != DocumentStatus.Cancelled
                        && s.InvoiceDate < asOf.Date.AddDays(1))
            .Include(s => s.Customer)
            .ToListAsync();

        var perCustomer = invoices
            .GroupBy(s => new { Id = s.CustomerId, Name = s.Customer?.NameAr ?? "زبون نقدي" })
            .Select(g => new TopCustomerRow
            {
                Name = g.Key.Name,
                Amount = Math.Round(g.Sum(x => x.TotalAmount), 2)
            })
            .OrderByDescending(x => x.Amount)
            .ToList();

        var totalSales = perCustomer.Sum(x => x.Amount);
        foreach (var r in perCustomer)
            r.Percent = totalSales > 0 ? Math.Round(r.Amount / totalSales * 100m, 1) : 0m;

        var top = perCustomer.Take(topN).ToList();
        var topShare = top.Sum(x => x.Percent);
        return (top, topShare > warningThresholdPct);
    }

    /// <summary>صف بسيط لأعلى العملاء (إجمالي + نسبة).</summary>
    public sealed class TopCustomerRow
    {
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
        public decimal Percent { get; set; }
    }
}

/// <summary>
/// مساعد ميزان المراجعة حتى تاريخ محدد (بداية/نهاية الفترة) — بنفس نمط
/// GetBalanceSheetAsync/GetAccountLedgerAsync: يجمع حركات JournalEntryLine قبل
/// تاريخ محدد ثم يجمعها لكل حساب.
/// </summary>
public static class TrialBalanceAsOfHelper
{
    public static async Task<Dictionary<Guid, decimal>> BalancesAsOfAsync(DbContext db, DateTime asOf)
    {
        var lines = await db.Set<JournalEntryLine>()
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry != null && l.JournalEntry.EntryDate < asOf.Date.AddDays(1))
            .ToListAsync();

        var dict = new Dictionary<Guid, decimal>();
        foreach (var l in lines)
        {
            if (l.Account is null) continue;
            var effect = l.Account.NormalBalance == NormalBalance.Credit
                ? l.CreditAmount - l.DebitAmount
                : l.DebitAmount - l.CreditAmount;
            dict.TryGetValue(l.AccountId, out var cur);
            dict[l.AccountId] = cur + effect;
        }
        return dict;
    }

    /// <summary>يعيد قيمة حساب في ميزان تاريخ محدد (0 إن لم يوجد).</summary>
    public static decimal Value(Dictionary<Guid, decimal> bal, Guid accountId)
        => bal.TryGetValue(accountId, out var v) ? v : 0m;

    /// <summary>
    /// يجمع أرصدة عدة حسابات (مثل كل الأصول المتداولة) من ميزان تاريخ محدد.
    /// يتجاوز الحسابات الأب (التي لا سطر حركات لها) ويستخدم الحسابات النهائية.
    /// </summary>
    public static decimal SumOf(Dictionary<Guid, decimal> bal, IEnumerable<Guid> accountIds)
        => Math.Round(accountIds.Sum(id => Value(bal, id)), 2);
}