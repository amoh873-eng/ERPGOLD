using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.DTOs.Purchases;
using ERPSystem.Application.Interfaces;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace ERPSystem.Application.Services;

/// <summary>
/// Implements purchase return business logic (مردودات المشتريات): يعيد البضاعة للمورد،
/// ويسجّل حركة مخزون "صادر مردود مشتريات" ويعكس القيد المحاسبي.
/// </summary>
public class PurchaseReturnService : IPurchaseReturnService
{
    private const string AccountInventory = "1300";
    private const string AccountCash = "1100";
    private const string AccountPayable = "2200";

    private readonly DbContext _context;
    private readonly IJournalEntryService _journalService;

    public PurchaseReturnService(DbContext context, IJournalEntryService journalService)
    {
        _context = context;
        _journalService = journalService;
    }

    public async Task<List<PurchaseReturnDto>> GetReturnsAsync()
    {
        var returns = await _context.Set<PurchaseReturn>()
            .Include(r => r.Supplier)
            .Include(r => r.Warehouse)
            .Include(r => r.PurchaseInvoice)
            .OrderByDescending(r => r.ReturnDate)
            .ThenByDescending(r => r.CreatedAt)
            .ToListAsync();
        return returns.Select(MapToDto).ToList();
    }

    public async Task<PurchaseReturnDto?> GetByIdAsync(Guid id)
    {
        var purchaseReturn = await _context.Set<PurchaseReturn>()
            .Include(r => r.Supplier)
            .Include(r => r.Warehouse)
            .Include(r => r.PurchaseInvoice)
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted);
        return purchaseReturn is null ? null : MapToDto(purchaseReturn);
    }

    public async Task<PurchaseReturnDto> CreateAsync(CreatePurchaseReturnDto dto)
    {
        var invoice = await _context.Set<PurchaseInvoice>()
            .Include(i => i.Lines)
            .Include(i => i.Supplier)
            .FirstOrDefaultAsync(i => i.Id == dto.PurchaseInvoiceId && !i.IsDeleted);
        if (invoice is null) throw new InvalidOperationException("الفاتورة الأصلية غير موجودة.");
        if (invoice.Status != DocumentStatus.Posted)
            throw new InvalidOperationException("لا يمكن عمل مردود لفاتورة غير مرحّلة.");

        // PU-04 (N-3/G1): لا يُثبت نقد غير مستلم، ولا يخفض رصيد المورد بلا مستند.
        var settlement = dto.SettlementType is { } st
            ? (PurchaseReturnSettlementType)st
            : (invoice.InvoiceType == PurchaseInvoiceType.Cash
                ? PurchaseReturnSettlementType.CashRefund
                : PurchaseReturnSettlementType.OnAccountCredit);

        if (invoice.InvoiceType == PurchaseInvoiceType.Cash)
        {
            if (settlement != PurchaseReturnSettlementType.CashRefund)
                throw new InvalidOperationException(
                    "مردود الفاتورة النقدية يتطلب تسوية CashRefund مع إثبات رد النقد؛ " +
                    "لا يُسجَّل نقد مدين ولا مطالبة على المورد دون مستند معتمد (G1/N-4).");
            if (string.IsNullOrWhiteSpace(dto.RefundReference))
                throw new InvalidOperationException(
                    "مرجع إثبات رد النقد إلزامي لمردود الفاتورة النقدية (رقم إيصال/كشف).");
        }
        else if (settlement != PurchaseReturnSettlementType.OnAccountCredit)
        {
            throw new InvalidOperationException(
                "مردود الفاتورة الآجلة يُسوّى على حساب المورد (OnAccountCredit) فقط في هذا النطاق.");
        }

        var warehouse = await _context.Set<Warehouse>()
            .FirstOrDefaultAsync(w => w.Id == dto.WarehouseId && !w.IsDeleted);
        if (warehouse is null) throw new InvalidOperationException("المخزن غير موجود.");

        if (dto.Lines is null || dto.Lines.Count == 0)
            throw new InvalidOperationException("يجب إضافة بند واحد على الأقل.");

        var invoiceLineByItem = invoice.Lines.ToDictionary(l => l.ItemId);

        var purchaseReturn = new PurchaseReturn
        {
            Id = Guid.NewGuid(),
            ReturnNumber = await NextReturnNumberAsync(),
            PurchaseInvoiceId = invoice.Id,
            SupplierId = invoice.SupplierId,
            WarehouseId = warehouse.Id,
            ReturnDate = dto.ReturnDate,
            Status = DocumentStatus.Posted,
            Note = dto.Note,
            SettlementType = settlement,
            RefundReference = dto.RefundReference,
            CreatedAt = DateTime.UtcNow
        };

        var subTotal = 0m;
        var taxTotal = 0m; // إصلاح D-04-01: نصيب الضريبة للكمية المرتجعة (عكس الضريبة المرسملة في المخزون)
        var lines = new List<PurchaseReturnLine>();

        foreach (var input in dto.Lines)
        {
            if (input.Quantity <= 0)
                throw new InvalidOperationException("الكمية يجب أن تكون أكبر من صفر.");

            if (!invoiceLineByItem.TryGetValue(input.ItemId, out var invoiceLine))
                throw new InvalidOperationException("أحد الأصناف غير موجود في الفاتورة الأصلية.");

            var alreadyReturned = await AlreadyReturnedQuantityAsync(invoice.Id, input.ItemId);
            if (alreadyReturned + input.Quantity > invoiceLine.Quantity)
                throw new InvalidOperationException($"لا يمكن رد أكثر مما اشتُري. الكمية المشتراة {invoiceLine.Quantity:N0}.");

            var line = new PurchaseReturnLine
            {
                Id = Guid.NewGuid(),
                PurchaseReturnId = purchaseReturn.Id,
                ItemId = input.ItemId,
                Quantity = input.Quantity,
                UnitCost = invoiceLine.UnitCost,
                LineTotal = Math.Round(input.Quantity * invoiceLine.UnitCost, 2),
                CreatedAt = DateTime.UtcNow
            };
            lines.Add(line);
            subTotal += line.LineTotal;

            // إصلاح D-04-01 (تدقيق 2026-10-20): عكس نصيب الضريبة للكمية المرتجعة.
            // فاتورة المشتريات تُرحَّل بإجمالي **شامل الضريبة** على حساب المخزون (تُرسمل الضريبة)،
            // فكان مردود المشتريات يعكس الصافي فقط ⇒ يبقى فرق دائم بقيمة الضريبة في المخزون
            // وفي رصيد المورد عند المردود الكامل. الآن يُعكس الإجمالي شاملاً نصيب الضريبة،
            // فيتطابق المردود الكامل مع الفاتورة تمامًا (بلا أي تغيير في المخطط).
            if (invoiceLine.Quantity != 0m)
                taxTotal += Math.Round(invoiceLine.TaxAmount * (input.Quantity / invoiceLine.Quantity), 2);
        }

        purchaseReturn.SubTotal = Math.Round(subTotal, 2);
        purchaseReturn.TotalAmount = Math.Round(subTotal + taxTotal, 2);
        purchaseReturn.Lines = lines;

        _context.Set<PurchaseReturn>().Add(purchaseReturn);
        _context.Set<PurchaseReturnLine>().AddRange(lines);

        var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await _context.Set<Item>().Where(i => itemIds.Contains(i.Id)).ToListAsync();
        var itemDict = items.ToDictionary(i => i.Id);

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // الكتابة غير idempotent (كيانات + أرصدة): نرفض إعادة المحاولة الصامتة بعد فشل عابر
            DbTransactionHelper.EnsureNotASilentRetry(_context);

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
            foreach (var line in lines)
            {
                // تحقق من كفاية رصيد الصنف في المخزن قبل إخراج البضاعة (مردود للمورد)
                await StockAvailabilityHelper.EnsureEnoughStockAsync(_context, line.ItemId, warehouse.Id, line.Quantity);

                _context.Set<StockMovement>().Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ItemId = line.ItemId,
                WarehouseId = warehouse.Id,
                MovementType = MovementType.PurchaseReturnOut,
                Quantity = -line.Quantity,
                UnitCost = line.UnitCost,
                ReferenceNumber = purchaseReturn.ReturnNumber,
                Note = $"مردود فاتورة مشتريات {invoice.InvoiceNumber}",
                MovementDate = purchaseReturn.ReturnDate,
                CreatedAt = DateTime.UtcNow
            });

            itemDict[line.ItemId].CurrentStock -= line.Quantity;
            itemDict[line.ItemId].UpdatedAt = DateTime.UtcNow;
        }

        var inventoryAccount = await GetAccountByCodeAsync(AccountInventory);
        // M1 (OnAccountCredit) ⇒ مدين 2200 · M2 (CashRefund) ⇒ مدين 1100 (بإثبات رد نقد).
        var isCashRefund = settlement == PurchaseReturnSettlementType.CashRefund;
        var debitAccount = await GetAccountByCodeAsync(isCashRefund ? AccountCash : AccountPayable);

        var entry = await _journalService.PrepareEntryAsync(
            JournalEntryType.PurchaseReturn,
            purchaseReturn.ReturnDate,
            purchaseReturn.ReturnNumber,
            $"مردود فاتورة مشتريات {invoice.InvoiceNumber}",
            new List<JournalEntryLegInput>
            {
                new() { AccountId = debitAccount.Id, DebitAmount = purchaseReturn.TotalAmount, Note = $"مردود {purchaseReturn.ReturnNumber}" },
                new() { AccountId = inventoryAccount.Id, CreditAmount = purchaseReturn.TotalAmount }
            });

        purchaseReturn.JournalEntryId = entry.Id;

        // G1: الرد النقدي لا يخفض مديونية المورد — خُفِّضت فعليًا بمجرد قبض النقد.
        // OnAccountCredit فقط هو ما يخفض الرصيد التشغيلي (M1)، وبعد قفل صف المورد.
        if (!isCashRefund)
        {
            await LockSupplierAsync(invoice.SupplierId);
            var supplier = invoice.Supplier;
            if (supplier is not null)
            {
                await _context.Entry(supplier).ReloadAsync();
                supplier.CurrentBalance = MoneyRounding.Round(supplier.CurrentBalance - purchaseReturn.TotalAmount);
                supplier.UpdatedAt = DateTime.UtcNow;
            }
        }

            await _context.SaveChangesAsync();
            await tx.CommitAsync();
            return MapToDto(purchaseReturn);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync();
                throw new InvalidOperationException("تعارض في تحديث بيانات المخزون — حاول مرة أخرى.");
            }
        });
    }

    /// <summary>
    /// PU-04 (N-5) — قفل صف المورد قبل أي تعديل على CurrentBalance،
    /// فلا يُبنى التحديث على قيمة قديمة (lost update) في مسار المردود.
    /// </summary>
    private async Task LockSupplierAsync(Guid supplierId)
    {
        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = "SELECT \"CurrentBalance\" FROM \"Suppliers\" WHERE \"Id\" = @id FOR UPDATE";
            var p = cmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = supplierId;
            cmd.Parameters.Add(p);
            if (await cmd.ExecuteScalarAsync() is null or DBNull)
                throw new InvalidOperationException("المورد غير موجود — تعذّر قفل رصيده.");
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open) await connection.CloseAsync();
        }
    }

    private Task<string> NextReturnNumberAsync()
        => NumberSequenceHelper.NextAsync(_context, "PR");

    private async Task<decimal> AlreadyReturnedQuantityAsync(Guid invoiceId, Guid itemId)
    {
        var query =
            from r in _context.Set<PurchaseReturn>()
            from rl in r.Lines
            where r.PurchaseInvoiceId == invoiceId
                  && r.Status == DocumentStatus.Posted
                  && rl.ItemId == itemId
            select rl.Quantity;
        return await query.SumAsync();
    }

    /// <summary>
    /// يبحث عن الحساب النظامي بالكود ثم يحلّه ديناميكياً إلى الحساب المتحرك (Leaf) حسب بنية الشجرة،
    /// فلا يُرحَّل أبداً إلى حساب رئيسي: Production (1100 ورقة) → 1100،
    /// Commercial (1100 رئيسي) → الورقة الصحيحة تحته عبر PostingAccountResolver.
    /// </summary>
    private async Task<Account> GetAccountByCodeAsync(string code)
        => await PostingAccountResolver.ResolveAsync(_context, code);

    private static PurchaseReturnDto MapToDto(PurchaseReturn r) => new()
    {
        Id = r.Id,
        ReturnNumber = r.ReturnNumber,
        PurchaseInvoiceId = r.PurchaseInvoiceId,
        InvoiceNumber = r.PurchaseInvoice?.InvoiceNumber ?? "—",
        SupplierId = r.SupplierId,
        SupplierName = r.Supplier?.NameAr ?? "—",
        WarehouseId = r.WarehouseId,
        WarehouseName = r.Warehouse?.NameAr ?? "—",
        ReturnDate = r.ReturnDate,
        Status = (int)r.Status,
        SubTotal = r.SubTotal,
        TotalAmount = r.TotalAmount,
        Note = r.Note,
        Lines = r.Lines.Select(l => new PurchaseReturnLineDto
        {
            Id = l.Id,
            ItemId = l.ItemId,
            ItemCode = l.Item?.Code ?? "—",
            ItemNameAr = l.Item?.NameAr ?? "—",
            Quantity = l.Quantity,
            UnitCost = l.UnitCost,
            LineTotal = l.LineTotal
        }).ToList()
    };
}