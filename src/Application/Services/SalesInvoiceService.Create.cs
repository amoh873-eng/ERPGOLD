using ERPSystem.Application.DTOs.BankChecks;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Application.Security;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// SalesInvoiceService — الإنشاء والترحيل (CreateAsync).
/// </summary>
public partial class SalesInvoiceService
{
    public async Task<SalesInvoiceDto> CreateAsync(CreateSalesInvoiceDto dto)
    {
        // 1) التحقق من البيانات المرجعية
        var customer = await _context.Set<Customer>()
            .FirstOrDefaultAsync(c => c.Id == dto.CustomerId && !c.IsDeleted);
        if (customer is null)
            throw new InvalidOperationException("العميل غير موجود");

        var warehouse = await _context.Set<Warehouse>()
            .FirstOrDefaultAsync(w => w.Id == dto.WarehouseId && !w.IsDeleted);
        if (warehouse is null)
            throw new InvalidOperationException("المخزن غير موجود");

        if (dto.Lines is null || dto.Lines.Count == 0)
            throw new InvalidOperationException("يجب إضافة بند واحد على الأقل.");

        var type = (SalesInvoiceType)dto.InvoiceType;
        if (type != SalesInvoiceType.Cash && type != SalesInvoiceType.OnAccount)
            throw new InvalidOperationException("نوع الفاتورة غير صالح.");

        var paymentMethod = (SalesPaymentMethod)dto.PaymentMethod;
        if (paymentMethod != SalesPaymentMethod.Cash
            && paymentMethod != SalesPaymentMethod.Card
            && paymentMethod != SalesPaymentMethod.OnAccount
            && paymentMethod != SalesPaymentMethod.Check)
            throw new InvalidOperationException("أسلوب السداد غير صالح.");

        // إلزامية رقم الموافقة للبطاقة: يمنع تسجيل عملية بطاقة بلا مرجع يمكن مطابقته لاحقاً مع كشف البنك
        if (paymentMethod == SalesPaymentMethod.Card && string.IsNullOrWhiteSpace(dto.CardApprovalCode))
            throw new InvalidOperationException("رقم الموافقة/المرجع مطلوب عند الدفع بالبطاقة — انسخه من إيصال الطرفية.");

        // السداد بشيك: رقم الشيك واسم البنك وتاريخ الاستحقاق إلزامية (شيكات مؤجلة عادةً)
        if (paymentMethod == SalesPaymentMethod.Check)
        {
            if (string.IsNullOrWhiteSpace(dto.CheckNumber) || string.IsNullOrWhiteSpace(dto.CheckBankName))
                throw new InvalidOperationException("رقم الشيك واسم البنك مطلوبان عند السداد بشيك.");
            if (dto.CheckDueDate is null)
                throw new InvalidOperationException("تاريخ استحقاق الشيك مطلوب عند السداد بشيك.");
            if (DateOnly.FromDateTime(dto.CheckDueDate.Value) < DateOnly.FromDateTime(dto.InvoiceDate))
                throw new InvalidOperationException("تاريخ استحقاق الشيك لا يمكن أن يسبق تاريخ الفاتورة.");
        }

        // الدمج المنطقي مع نوع الفاتورة: فاتورة آجلة بالطريقة القديمة (InvoiceType=2) تساوي آجل دائماً
        if (type == SalesInvoiceType.OnAccount && paymentMethod == SalesPaymentMethod.Cash)
            paymentMethod = SalesPaymentMethod.OnAccount;

        // 2) جلب الأصناف مرة واحدة والتحقق من وجودها وعدم تكرارها
        var distinctItemIds = dto.Lines.Select(l => l.ItemId).Distinct().ToList();
        if (distinctItemIds.Count != dto.Lines.Count)
            throw new InvalidOperationException("لا يمكن تكرار نفس الصنف في فاتورة واحدة.");

        var items = await _context.Set<Item>()
            .Where(i => distinctItemIds.Contains(i.Id) && !i.IsDeleted)
            .ToListAsync();

        if (items.Count != distinctItemIds.Count)
            throw new InvalidOperationException("أحد الأصناف المحددة غير موجود.");

        var itemDict = items.ToDictionary(i => i.Id);

        // ── Requirement 3: رفض السعر اليدوي غير المصرّح به قبل أي ترقيم/كتابة ──
        // التحقق هنا (وليس داخل حلقة البناء) لسببين:
        //  1) فشل سريع بلا أي أثر على قاعدة البيانات (لا رقم فاتورة محروق، ولا حركة، ولا قيد).
        //  2) الحارس الخلفي هو الحماية الفعلية — أي استدعاء مباشر للخدمة (تجاوز الواجهة) يُرفض.
        // POS مستثنى صراحةً (dto.IsPos): لا يُمنح تصريح سعر يدوي، ولا يتأثر بهذا الفحص إطلاقًا
        // (الأسعار المدمجة/الموزونة وفواتير نقطة البيع تبقى كما هي اليوم).
        if (!dto.IsPos)
        {
            foreach (var input in dto.Lines)
            {
                if (!itemDict.TryGetValue(input.ItemId, out var pricedItem))
                    continue; // يلتقطه التحقق العام للأصناف أدناه/أعلاه

                ManualPriceGuard.EnsureCanOverride(_priceAuthorization, ManualPriceModules.Sales,
                    input.UnitPrice, pricedItem.SalePrice, "سعر الوحدة");
            }
        }

        // 3) بناء الفاتورة وحساب المجاميع (خصم فوري ونسبة ضريبة على رأس الفاتورة)
        var invoice = new SalesInvoice
        {
            Id = Guid.NewGuid(),
            InvoiceNumber = string.Empty, // إصلاح R1 (2026-10-21): يُعيَّن داخل المعاملة حتى لا يُستهلك رقم في محاولة مرفوضة
            CustomerId = customer.Id,
            WarehouseId = warehouse.Id,
            InvoiceDate = dto.InvoiceDate,
            DueDate = dto.DueDate ?? dto.InvoiceDate.AddDays(30),
            InvoiceType = type,
            PaymentMethod = paymentMethod,
            CardApprovalCode = paymentMethod == SalesPaymentMethod.Card ? dto.CardApprovalCode : null,
            CardLast4 = dto.CardLast4,
            CardNetwork = dto.CardNetwork,
            CardTransactionAt = dto.CardTransactionAt,
            Status = DocumentStatus.Posted, // هذا الموديول يرحّل الفاتورة فوراً عند الإنشاء
            DiscountPercentage = dto.DiscountPercentage,
            TaxRate = dto.TaxRate,
            Note = dto.Note,
            IsPos = dto.IsPos,
            CreatedAt = DateTime.UtcNow
        };

        var subTotal = 0m;
        var lines = new List<SalesInvoiceLine>();

        foreach (var input in dto.Lines)
        {
            if (input.Quantity <= 0)
                throw new InvalidOperationException("الكمية يجب أن تكون أكبر من صفر.");

            var item = itemDict[input.ItemId];
            // لقطة تكلفة البيع = متوسط التكلفة الداخلي للصنف (Item.CostPrice بأربع خانات)
            // تُحفظ في SalesInvoiceLine.UnitCost (18,4) و StockMovements.UnitCost (18,4)
            // فلا تُختصر قبل احتساب COGS، ويبقى القيد قابلًا لإعادة الاحتساب من السطر نفسه.
            var unitCost = item.CostPrice;

            var line = new SalesInvoiceLine
            {
                Id = Guid.NewGuid(),
                SalesInvoiceId = invoice.Id,
                ItemId = item.Id,
                Quantity = input.Quantity,
                UnitPrice = input.UnitPrice,
                UnitCost = unitCost,
                LineTotal = Math.Round(input.Quantity * input.UnitPrice, 2),
                TaxRate = item.TaxRate, // لقطة من نسبة الصنف لحظة الإنشاء
                CreatedAt = DateTime.UtcNow
            };
            lines.Add(line);
            subTotal += line.LineTotal;
        }

        invoice.SubTotal = Math.Round(subTotal, 2);
        invoice.DiscountAmount = Math.Round(invoice.SubTotal * (dto.DiscountPercentage / 100m), 2);
        var netBeforeTax = invoice.SubTotal - invoice.DiscountAmount;

        // ── ضريبة المبيعات لكل سطر حسب نسبة الصنف (وليس نسبة واحدة على رأس الفاتورة) ──
        // نوزّع الخصم تناسبياً على البنود ثم نطبّق نسبة كل صنف على صافي سطره،
        // بحيث يبقى منطق «الضريبة على الصافي» قائماً، ومجموع ضريبة البنود = إجمالي الضريبة.
        var lineTaxTotal = 0m;
        foreach (var line in lines)
        {
            var lineNet = invoice.SubTotal > 0m
                ? line.LineTotal - (line.LineTotal * invoice.DiscountAmount / invoice.SubTotal)
                : line.LineTotal;
            line.TaxAmount = Math.Round(lineNet * (line.TaxRate / 100m), 2);
            lineTaxTotal += line.TaxAmount;
        }

        invoice.TaxAmount = Math.Round(lineTaxTotal, 2);
        // نسبة فعالة مرجّحة (قيمة مرجعية على رأس الفاتورة — تُستخدم في التقارير)
        invoice.TaxRate = netBeforeTax > 0m
            ? Math.Round(invoice.TaxAmount / netBeforeTax * 100m, 2)
            : 0m;
        invoice.TotalAmount = Math.Round(netBeforeTax + invoice.TaxAmount, 2);
        invoice.PaidAmount = type == SalesInvoiceType.Cash || paymentMethod == SalesPaymentMethod.Check
            ? invoice.TotalAmount
            : 0m;
        invoice.Lines = lines;

        // إصلاح R6 (2026-10-21): لا نبدأ عملية مستند جديدة وسياق الجلسة يحمل كيانات معلّقة من محاولة فاشلة
        DocumentFailureCleanup.EnsureNoPendingDocuments(_context);

        _context.Set<SalesInvoice>().Add(invoice);
        _context.Set<SalesInvoiceLine>().AddRange(lines);

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                // الكتابة غير idempotent (كيانات + أرصدة): نرفض إعادة المحاولة الصامتة بعد فشل عابر
                DbTransactionHelper.EnsureNotASilentRetry(_context);

                await using var tx = await _context.Database.BeginTransactionAsync();

                // إصلاح R1: الترقيم داخل المعاملة ⇒ لا يُستهلك رقم مستند في محاولة ترفضها القاعدة لاحقاً
                invoice.InvoiceNumber = await NextInvoiceNumberAsync();
                try
                {
            // 4) حركات المخزون: "صادر مبيعات" لكل بند (مع التحقق من كفاية الرصيد)
            foreach (var line in lines)
            {
                await StockAvailabilityHelper.EnsureEnoughStockAsync(_context, line.ItemId, warehouse.Id, line.Quantity);

            _context.Set<StockMovement>().Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ItemId = line.ItemId,
                WarehouseId = warehouse.Id,
                MovementType = MovementType.SalesIssue,
                Quantity = -line.Quantity,
                UnitCost = line.UnitCost,
                ReferenceNumber = invoice.InvoiceNumber,
                Note = $"فاتورة بيع {invoice.InvoiceNumber}",
                MovementDate = invoice.InvoiceDate,
                CreatedAt = DateTime.UtcNow
            });

            // تحديث الرصيد الكلي المكرر للصنف (للأداء في لوحة التحكم)
            itemDict[line.ItemId].CurrentStock -= line.Quantity;
            itemDict[line.ItemId].UpdatedAt = DateTime.UtcNow;

            // ── تتبّع انتهاء الصلاحية: خصم FEFO (الأقرب انتهاءً أولاً) ──
            // للأصناف التي تتتبّع الصلاحية فقط، نخصم من أقدم دفعة تاريخ انتهاء.
            // تتم داخل نفس المعاملة/القفل الحالي (قفل صف الصنف من EnsureEnoughStock).
            if (itemDict[line.ItemId].TracksExpiry)
                await StockBatchHelper.AllocateFefoAsync(_context, line.ItemId, warehouse.Id, line.Quantity);

            // ── تتبّع الدُفعات (ItemBatch): FEFO مع منع بيع المنتهي + تكامل الرصيد ──
            if (itemDict[line.ItemId].TracksBatches)
                await AllocateItemBatchesAsync(line.ItemId, warehouse.Id, line.Quantity);
        }

        // 5) القيد المحاسبي للبيع: مدين حسب أسلوب السداد (صندوق أو ذمم بطاقات أو عملاء) ، دائن (إيراد + ضريبة)
        //    البطاقة ليست نقداً فورياً — البنك يسوّيها لاحقاً، لذا تُدين "ذمم البطاقات" بدل "الصندوق".
        var receivableAccount = (type == SalesInvoiceType.OnAccount || paymentMethod == SalesPaymentMethod.OnAccount)
            ? await GetAccountByCodeAsync(AccountReceivable)
            : paymentMethod == SalesPaymentMethod.Card
                ? await GetAccountByCodeAsync(AccountCardReceivables)
                : paymentMethod == SalesPaymentMethod.Check
                    ? await GetAccountByCodeAsync(AccountReceivable)
                    // الشيك يُدين «ذمم العملاء» هنا وقيد تسجيل الشيك (1102/1200) يسويه فوراً في
                    // نفس المعاملة — فلا يُدين 1102 مرتين (كان يفعل ذلك سابقاً).
                    : invoice.IsPos
                        ? await GetAccountByCodeAsync(AccountTillDrawer) // مبيعات نقطة البيع النقدية → درج الكاش (1105)
                        : await GetAccountByCodeAsync(AccountCash);     // مبيعات الوحدة النقدية → الخزنة الرئيسية (1100)
        // SUB-1 (سياسة الذمم المعتمدة 2026-10-24): الرصيد التشغيلي للعميل يجب أن يطابق حساب الذمم (1200).
        // البيع النقدي/درج الكاش/البطاقة يُدين 1100/1105/1205 (لا 1200) ⇒ لا يُنشئ ذمة على العميل.
        // يُنشأ الأثر فقط عندما يُدين قيد البيع ذمم العملاء: الآجل، أو الشيك (يُدين 1200 مؤقتًا ثم
        // يسوّيه قيد تسجيل الشيك فورًا في نفس المعاملة ⇒ صافي الأثر صفر).
        var createsCustomerReceivable = type == SalesInvoiceType.OnAccount
            || paymentMethod == SalesPaymentMethod.OnAccount
            || paymentMethod == SalesPaymentMethod.Check;

        var revenueAccount = await GetAccountByCodeAsync(AccountSalesRevenue);
        var taxPayableAccount = await GetAccountByCodeAsync(AccountSalesTaxPayable);

        var saleEntry = await _journalService.PrepareEntryAsync(
            JournalEntryType.SalesInvoice,
            invoice.InvoiceDate,
            invoice.InvoiceNumber,
            $"فاتورة بيع {invoice.InvoiceNumber} - {customer.NameAr}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = receivableAccount.Id, DebitAmount = invoice.TotalAmount, Note = $"فاتورة {invoice.InvoiceNumber}" },
                new() { AccountId = revenueAccount.Id, CreditAmount = netBeforeTax },
                new() { AccountId = taxPayableAccount.Id, CreditAmount = invoice.TaxAmount }
            });

        // 6) قيد التكلفة: مدين (تكلفة البضاعة) ، دائن (المخزون)
        //    COGS = تقريب نقدي **مرة واحدة** على إجمالي (الكمية × لقطة تكلفة السطر ذي الأربع خانات)،
        //    وفق سياسة التقريب النقدي الصريحة (MoneyRounding). لا تقريب وسيط لكل سطر.
        var cogsAccount = await GetAccountByCodeAsync(AccountCostOfGoodsSold);
        var inventoryAccount = await GetAccountByCodeAsync(AccountInventory);
        var cogsTotal = MoneyRounding.Round(lines.Sum(l => l.Quantity * l.UnitCost));

        var cogsEntry = await _journalService.PrepareEntryAsync(
            JournalEntryType.SalesInvoice,
            invoice.InvoiceDate,
            invoice.InvoiceNumber,
            $"تكلفة فاتورة بيع {invoice.InvoiceNumber}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = cogsAccount.Id, DebitAmount = cogsTotal },
                new() { AccountId = inventoryAccount.Id, CreditAmount = cogsTotal }
            });

        // ربط القيود بالفاتورة (ليسهل تتبّعها لاحقاً)
        invoice.SalesJournalEntryId = saleEntry.Id;
        invoice.CogsJournalEntryId = cogsEntry.Id;

        // (7) تحديث رصيد العميل المكرر — يُنفَّذ **بعد** خطوة تسجيل الشيك (7ب) أدناه:
        //     تسجيل الشيك يُعيد قراءة صف العميل من القاعدة (Reload) فيُسقط أي تعديل غير محفوظ قبله،
        //     لذا يجب أن يكون تعديل الفاتورة بعده حتى يبقى صافي أثر الشيك صفرًا (كان يُسقطه سابقًا).

        // 7ب) السداد بشيك: ننشئ سجل الشيك (مستلم من العميل) + قيد فتح «شيكات برسم التحصيل» (1102)
        //     ذات المُعامَلة: الفاتورة دائمًا تُدين حساب العميل ثم الشيك يخفضه فورًا (صافي = صفر
        //     شيكًا بدل الدفع النقدي) عبر نفس الوضع الذري في PrepareRegisteredCheckAsync.
        if (paymentMethod == SalesPaymentMethod.Check)
        {
            var checkDto = new CreateBankCheckDto
            {
                CheckNumber = dto.CheckNumber ?? string.Empty,
                BankName = dto.CheckBankName ?? string.Empty,
                BranchName = dto.CheckBranch,
                Direction = BankCheckDirection.ReceivedFromCustomer,
                CustomerId = customer.Id,
                RelatedInvoiceId = invoice.Id,
                Amount = invoice.TotalAmount,
                IssueDate = dto.CheckIssueDate.HasValue ? dto.CheckIssueDate.Value : invoice.InvoiceDate,
                DueDate = dto.CheckDueDate.HasValue ? dto.CheckDueDate.Value : invoice.InvoiceDate.AddDays(30),
                ReceivedOrIssuedDate = invoice.InvoiceDate,
                Notes = dto.Note
            };
            var check = await BankCheckService.PrepareRegisteredCheckAsync(
                _context, _journalService, checkDto, customer, null);
            _context.Set<BankCheck>().Add(check);
        }

        // 7) تحديث رصيد العميل المكرر ليطابق ذمم 1200 (SUB-1 — سياسة الذمم 2026-10-24).
        //    يُزاد فقط عندما يُدين قيد البيع ذمم العملاء (الآجل أو الشيك). أثر الشيك صافٍ صفرًا:
        //    خطوة (7ب) سجّلت −الإجمالي وهذه تضيف +الإجمالي. النقدي/الدرج/البطاقة لا يمسّ 1200 ⇒ لا تُنفَّذ.
        //    القفل/إعادة القراءة: خارج مسار الشيك نقرأ طازجًا تحت القفل (منع lost update)؛ وفي مسار
        //    الشيك الصف مقفول ومقروء طازجًا في (7ب) فلا نُعيد القراءة كي لا نمحو أثرها.
        if (createsCustomerReceivable)
        {
            if (paymentMethod != SalesPaymentMethod.Check)
            {
                await PartyRowLock.LockCustomerAsync(_context, customer.Id);
                await _context.Entry(customer).ReloadAsync();
            }
            customer.CurrentBalance = MoneyRounding.Round(customer.CurrentBalance + invoice.TotalAmount);
            customer.UpdatedAt = DateTime.UtcNow;
        }

            // SaveChanges واحدة تحفظ كل شيء معاً (ذرية): الفاتورة بنودها حركاتها وقيودها وأرصدتها
            await _context.SaveChangesAsync();
            await tx.CommitAsync();

            // JoFotara hook removed from here — call it from Program.cs or a domain event instead
            // to avoid circular dependency (Application -> Infrastructure). This keeps the sale non-blocking.

            return MapToDto(invoice);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync();
                throw new InvalidOperationException("تعارض في تحديث بيانات الفاتورة — حاول مرة أخرى.");
            }
            });
        }
        catch
        {
            // إصلاح R1/R5 (2026-10-21): بعد أي فشل — تنظيف محدود للكيانات المعلّقة حتى لا يحفظها
            // أي SaveChanges لاحق في نفس دائرة Blazor Server (منع المستندات الجزئية/الشبحيّة).
            DocumentFailureCleanup.DetachPendingDocuments(_context);
            throw;
        }
    }

    /// <summary>يخصّص كمية البيع FEFO من دُفعات ItemBatch (مع منع بيع المنتهي) ثم يتحقق من التزامن.</summary>
    private async Task AllocateItemBatchesAsync(Guid itemId, Guid warehouseId, decimal quantity)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        var batches = await _context.Set<ItemBatch>()
            .Where(b => b.ItemId == itemId && b.WarehouseId == warehouseId && b.Quantity > 0m)
            .ToListAsync();

        // نستثني الدُفعات المنتهية فعلياً (ExpiryDate < اليوم) — لا تُبيع أبداً
        var usable = batches.Where(b => b.ExpiryDate is null || b.ExpiryDate >= today).ToList();
        if (usable.Count == 0)
            throw new InvalidOperationException(
                "لا توجد دُفعات صالحة غير منتهية هذا الصنف — لا يمكن بيع كمية من دُفعة منتهية.");

        var ordered = usable
            .OrderBy(b => b.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(b => b.ExpiryDate)
            .ToList();

        var total = ordered.Sum(b => b.Quantity);
        if (total < quantity)
            throw new InvalidOperationException(
                $"رصيد الدُفعات الصالحة غير كافٍ للصنف. المتاح غير المنتهي: {total:N0}، المطلوب: {quantity:N0}.");

        var remaining = quantity;
        foreach (var batch in ordered)
        {
            if (remaining <= 0m) break;
            var take = Math.Min(batch.Quantity, remaining);
            batch.Quantity -= take;
            remaining -= take;
        }

        // ضمانة التزامن: مجموع الدُفعات == رصيد حركات المخزون (نفس المعاملة)
        var batchTotal = await _context.Set<ItemBatch>()
            .Where(b => b.ItemId == itemId && b.WarehouseId == warehouseId)
            .SumAsync(b => (decimal?)b.Quantity) ?? 0m;
        var movementTotal = await _context.Set<StockMovement>()
            .Where(m => m.ItemId == itemId && m.WarehouseId == warehouseId)
            .SumAsync(m => (decimal?)m.Quantity) ?? 0m;
        if (batchTotal != movementTotal)
            throw new InvalidOperationException(
                $"تعارض تكامل دُفعات البيع: مجاميع الدُفعات ({batchTotal:N2}) ≠ حركات المخزون ({movementTotal:N2}).");
    }
}