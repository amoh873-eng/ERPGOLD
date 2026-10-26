using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Sales;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// SalesReturnService — إنشاء وترحيل المردود (CreateAsync).
/// </summary>
public partial class SalesReturnService
{
    public async Task<SalesReturnDto> CreateAsync(CreateSalesReturnDto dto)
    {
        // 1) الفاتورة الأصلية يجب أن تكون مرحّلة
        var invoice = await _context.Set<SalesInvoice>()
            .Include(i => i.Lines)
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == dto.SalesInvoiceId && !i.IsDeleted);
        if (invoice is null)
            throw new InvalidOperationException("الفاتورة الأصلية غير موجودة.");
        if (invoice.Status != DocumentStatus.Posted)
            throw new InvalidOperationException("لا يمكن عمل مردود لفاتورة غير مرحّلة.");

        var warehouse = await _context.Set<Warehouse>()
            .FirstOrDefaultAsync(w => w.Id == dto.WarehouseId && !w.IsDeleted);
        if (warehouse is null)
            throw new InvalidOperationException("المخزن غير موجود.");

        if (dto.Lines is null || dto.Lines.Count == 0)
            throw new InvalidOperationException("يجب إضافة بند واحد على الأقل.");

        var distinctItemIds = dto.Lines.Select(l => l.ItemId).Distinct().ToList();
        if (distinctItemIds.Count != dto.Lines.Count)
            throw new InvalidOperationException("لا يمكن تكرار نفس الصنف في مردود واحد.");

        var invoiceLineByItem = invoice.Lines.ToDictionary(l => l.ItemId);

        // 2) بناء المردود واعتماد أسعار/تكاليف الفاتورة الأصلية لضمان التطابق
        var salesReturn = new SalesReturn
        {
            Id = Guid.NewGuid(),
            ReturnNumber = string.Empty, // إصلاح R3 (2026-10-21): يُعيَّن داخل المعاملة حتى لا يُستهلك رقم مردود في محاولة مرفوضة
            SalesInvoiceId = invoice.Id,
            CustomerId = invoice.CustomerId,
            WarehouseId = warehouse.Id,
            ReturnDate = dto.ReturnDate,
            Status = DocumentStatus.Posted,
            DiscountPercentage = invoice.DiscountPercentage,
            TaxRate = invoice.TaxRate,
            Note = dto.Note,
            CreatedAt = DateTime.UtcNow
        };

        var subTotal = 0m;
        var lines = new List<SalesReturnLine>();
        var cogsTotal = 0m;
        var lineTaxTotal = 0m; // الضريبة المعكوسة من لقطة السطر الأصلي

        foreach (var input in dto.Lines)
        {
            if (input.Quantity <= 0)
                throw new InvalidOperationException("الكمية يجب أن تكون أكبر من صفر.");

            if (!invoiceLineByItem.TryGetValue(input.ItemId, out var invoiceLine))
                throw new InvalidOperationException("أحد الأصناف غير موجود في الفاتورة الأصلية.");

            // التحقق ألا يتجاوز المردود الكمية المباعة لكل صنف (بعد خصم ما سبق ردّه)
            var alreadyReturned = await AlreadyReturnedQuantityAsync(invoice.Id, input.ItemId);
            if (alreadyReturned + input.Quantity > invoiceLine.Quantity)
                throw new InvalidOperationException(
                    $"لا يمكن رد أكثر مما بيع. الصنف كمية مباعة {invoiceLine.Quantity:N0}، سبق ردّه {alreadyReturned:N0}.");

            // نستخدم سعر وتكلفة الفاتورة الأصلية حتى يعكس المردود البيع تماماً
            var line = new SalesReturnLine
            {
                Id = Guid.NewGuid(),
                SalesReturnId = salesReturn.Id,
                ItemId = input.ItemId,
                Quantity = input.Quantity,
                UnitPrice = invoiceLine.UnitPrice,
                UnitCost = invoiceLine.UnitCost,
                LineTotal = Math.Round(input.Quantity * invoiceLine.UnitPrice, 2),
                CreatedAt = DateTime.UtcNow
            };
            lines.Add(line);
            subTotal += line.LineTotal;
            cogsTotal += input.Quantity * invoiceLine.UnitCost;
            // الضريبة المعكوسة = حصة هذا السطر من لقطة الضريبة الأصلية (نسبة إلى الكمية المردودة)
            lineTaxTotal += Math.Round(invoiceLine.TaxAmount * (input.Quantity / invoiceLine.Quantity), 2);
        }

        salesReturn.SubTotal = Math.Round(subTotal, 2);
        salesReturn.DiscountAmount = Math.Round(salesReturn.SubTotal * (invoice.DiscountPercentage / 100m), 2);
        var netBeforeTax = salesReturn.SubTotal - salesReturn.DiscountAmount;
        // نعكس ضريبة الفاتورة من لقطة السطر الأصلي (لا من نسبة الرأس) لتطابق القيد تماماً
        salesReturn.TaxAmount = Math.Round(lineTaxTotal, 2);
        salesReturn.TaxRate = netBeforeTax > 0m
            ? Math.Round(salesReturn.TaxAmount / netBeforeTax * 100m, 2)
            : 0m;
        salesReturn.TotalAmount = Math.Round(netBeforeTax + salesReturn.TaxAmount, 2);
        salesReturn.Lines = lines;
        // COGS العكسي: تقريب نقدي مرة واحدة على الإجمالي وفق السياسة الصريحة (MoneyRounding)،
        // واللقطة (invoiceLine.UnitCost) بأربع خانات فتعكس البيع الأصلي تمامًا.
        cogsTotal = MoneyRounding.Round(cogsTotal);

        // إصلاح R3/R5/R6 (2026-10-21): دورة مردود المبيعات داخل معاملة صريحة —
        //  • قفل الصف (FOR UPDATE) يبقى فعّالاً حتى الـcommit (كان يُحرَّر فوراً بلا معاملة).
        //  • تحديث الرصيد يمرّ بفرع المعاملة في JournalEntryService (تحديث ذرّي + RETURNING) فلا يُفقد تحديث متزامن.
        //  • أي فشل ⇒ تراجع كامل + تنظيف محدود للكيانات المعلّقة.
        DocumentFailureCleanup.EnsureNoPendingDocuments(_context);

        return await DbTransactionHelper.ExecuteAsync(_context, async () =>
        {
            try
            {
                // إصلاح R3: الترقيم داخل المعاملة (لا يُستهلك رقم في محاولة ترفضها القاعدة)
                salesReturn.ReturnNumber = await NextReturnNumberAsync();

                _context.Set<SalesReturn>().Add(salesReturn);
                _context.Set<SalesReturnLine>().AddRange(lines);

        // 3) إعادة البضاعة إلى المخزن: حركة "وارد مردود" (موجبة) + تحديث رصيد الصنف
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await _context.Set<Item>()
            .Where(i => itemIds.Contains(i.Id))
            .ToListAsync();
        var itemDict = items.ToDictionary(i => i.Id);

        foreach (var line in lines)
        {
            _context.Set<StockMovement>().Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ItemId = line.ItemId,
                WarehouseId = warehouse.Id,
                MovementType = MovementType.SalesReturnIn,
                Quantity = line.Quantity,
                UnitCost = line.UnitCost,
                ReferenceNumber = salesReturn.ReturnNumber,
                Note = $"مردود فاتورة بيع {invoice.InvoiceNumber}",
                MovementDate = salesReturn.ReturnDate,
                CreatedAt = DateTime.UtcNow
            });

            await StockAvailabilityHelper.LockAndRefreshItemAsync(_context, line.ItemId);
            itemDict[line.ItemId].CurrentStock += line.Quantity;
            itemDict[line.ItemId].UpdatedAt = DateTime.UtcNow;
        }

        // 4) القيد العكسي للبيع: مدين (مردودات + ضريبة) ، دائن (صندوق/عملاء)
        var returnsAccount = await GetAccountByCodeAsync(AccountSalesReturns);
        var taxPayableAccount = await GetAccountByCodeAsync(AccountSalesTaxPayable);
        // SUB-2 / SUB-2b (سياسة مردود المبيعات 2026-10-24): اعتماد المردود يُثبت **المبلغ المستحق للعميل**
        // ولا يُثبت نقدًا لم يُقبض ولا يخفض أصلًا بنكيًا لم يخرج منه. دائن المردود:
        //  • آجل ⇒ 1200 (ذمم العميل) — تخفيض ما هو مستحق عليه.
        //  • **شيك (أي حالة: مسجَّل/مودع/محصَّل/مرتد) ⇒ 1200** — المردود يُنشئ/يزيد رصيدًا دائنًا للعميل
        //    (صافي المستحق)، ولا يمسّ 1102 (الشيك ما زال أصلًا) ولا 1101 (النقد البنكي) ولا 1100 (النقد).
        //    التسوية الفعلية (رد نقدي/بنكي) حدث منفصل لاحق غير مدعوم بمستند في النظام ⇒ بند مفتوح موثَّق.
        //  • بطاقة ⇒ 1205 (ذمم البطاقات) كما أدين قيد البيع الأصلي.
        //  • نقدي/POS ⇒ 1105/1100.
        var payableAccount = await GetAccountByCodeAsync(
            invoice.InvoiceType == SalesInvoiceType.OnAccount
                ? AccountReceivable
                : invoice.PaymentMethod == SalesPaymentMethod.Card
                    ? AccountCardReceivables
                    : invoice.PaymentMethod == SalesPaymentMethod.Check
                        ? AccountReceivable // SUB-2b: مبلغ مستحق للعميل (رصيد دائن) — لا نقد ولا بنك
                        : invoice.IsPos && invoice.PaymentMethod == SalesPaymentMethod.Cash
                            ? AccountTillDrawer // مردود مبيعات نقدية من نقطة البيع → درج الكاش (1105)
                            : AccountCash);     // مردود مبيعات نقدية من الوحدة → الخزنة الرئيسية (1100)

        var reverseSaleEntry = await _journalService.PrepareEntryAsync(
            JournalEntryType.SalesReturn,
            salesReturn.ReturnDate,
            salesReturn.ReturnNumber,
            $"مردود فاتورة بيع {invoice.InvoiceNumber}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = returnsAccount.Id, DebitAmount = netBeforeTax },
                new() { AccountId = taxPayableAccount.Id, DebitAmount = salesReturn.TaxAmount },
                new() { AccountId = payableAccount.Id, CreditAmount = salesReturn.TotalAmount, Note = $"مردود {salesReturn.ReturnNumber}" }
            });

        // 5) القيد العكسي للتكلفة: مدين (مخزون) ، دائن (تكلفة البضاعة)
        var inventoryAccount = await GetAccountByCodeAsync(AccountInventory);
        var cogsAccount = await GetAccountByCodeAsync(AccountCostOfGoodsSold);

        var reverseCogsEntry = await _journalService.PrepareEntryAsync(
            JournalEntryType.SalesReturn,
            salesReturn.ReturnDate,
            salesReturn.ReturnNumber,
            $"عكس تكلفة مردود فاتورة بيع {invoice.InvoiceNumber}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = inventoryAccount.Id, DebitAmount = cogsTotal },
                new() { AccountId = cogsAccount.Id, CreditAmount = cogsTotal }
            });

        salesReturn.SalesJournalEntryId = reverseSaleEntry.Id;
        salesReturn.CogsJournalEntryId = reverseCogsEntry.Id;

        // 6) تعديل رصيد العميل التشغيلي ليطابق ذمم 1200 (SUB-1/SUB-2/SUB-2b — سياسة الذمم 2026-10-24):
        //    يُخفض عندما يُقيَّد المردود دائن 1200: الفاتورة الآجلة **أو** فاتورة الشيك (أي حالة).
        //    مردود النقد/الدرج/البطاقة لا يمسّ 1200 فلا يخفض الرصيد (يعكس حسابه الأصلي 1105/1100/1205).
        //    إصلاح 2026-10-23 محفوظ: قفل صف العميل ثم قراءة الرصيد الطازج قبل الكتابة (منع lost update).
        if (invoice.InvoiceType == SalesInvoiceType.OnAccount
            || invoice.PaymentMethod == SalesPaymentMethod.Check)
        {
            var customer = invoice.Customer;
            await PartyRowLock.LockCustomerAsync(_context, customer!.Id);
            await _context.Entry(customer).ReloadAsync();
            customer.CurrentBalance = MoneyRounding.Round(customer.CurrentBalance - salesReturn.TotalAmount);
            customer.UpdatedAt = DateTime.UtcNow;
        }

        // SaveChanges واحدة: المردود + بنوده + حركاته + قيوده + الأرصدة ذرياً (داخل المعاملة)
        await _context.SaveChangesAsync();

        return MapToDto(salesReturn);
            }
            catch
            {
                // إصلاح R3/R5: تراجع المعاملة (تلقائي) + تنظيف محدود للكيانات المعلّقة
                DocumentFailureCleanup.DetachPendingDocuments(_context);
                throw;
            }
        });
    }
}