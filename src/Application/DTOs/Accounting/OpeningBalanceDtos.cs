namespace ERPSystem.Application.DTOs.Accounting;

/// <summary>بند إدخال في دفعة الأرصدة الافتتاحية: حساب متحرك + مبلغ مدين أو دائن.</summary>
public class OpeningBalanceLineInput
{
    /// <summary>رقم الحساب كما في شجرة الحسابات (يجب أن يكون حساباً متحركاً Leaf موجوداً).</summary>
    public string AccountCode { get; set; } = string.Empty;

    /// <summary>المبلغ المدين (0 إذا كان البند دائناً).</summary>
    public decimal DebitAmount { get; set; }

    /// <summary>المبلغ الدائن (0 إذا كان البند مديناً).</summary>
    public decimal CreditAmount { get; set; }

    public string? Note { get; set; }

    // ────────────────── تفصيل الرصيد الافتتاحي (2026-10-25) ──────────────────
    // البنود على الحسابات الرقابية تُلزم بتفصيلها:
    //  • 1200 (العملاء)   ⇒ CustomerCode إلزامي.
    //  • 2200 (الموردين)  ⇒ SupplierCode إلزامي.
    //  • 1300 (المخزون)   ⇒ ItemCode + WarehouseCode + Quantity + UnitCost إلزامية،
    //                        وقيمة البند تُحسب = الكمية × التكلفة (وتُقارن بالمبلغ المُدخل).

    /// <summary>كود العميل (إلزامي على بنود حساب العملاء 1200).</summary>
    public string? CustomerCode { get; set; }

    /// <summary>كود المورد (إلزامي على بنود حساب الموردين 2200).</summary>
    public string? SupplierCode { get; set; }

    /// <summary>كود الصنف (إلزامي على بنود حساب المخزون 1300).</summary>
    public string? ItemCode { get; set; }

    /// <summary>كود المخزن (إلزامي مع بند الصنف).</summary>
    public string? WarehouseCode { get; set; }

    /// <summary>الكمية الافتتاحية للصنف (موجبة، إلزامية مع بند الصنف).</summary>
    public decimal? Quantity { get; set; }

    /// <summary>تكلفة الوحدة الافتتاحية للصنف (إلزامية مع بند الصنف).</summary>
    public decimal? UnitCost { get; set; }
}

/// <summary>
/// إنشاء دفعة أرصدة افتتاحية (مسودة). لا يوجد حساب مقابل مفترض: على المستخدم إدخال البنود
/// التي يتساوى فيها مجموع المدين ومجموع الدائن، وإلا رُفض الإنشاء مع بيان الفرق.
/// </summary>
public class CreateOpeningBalanceBatchDto
{
    /// <summary>السنة المالية (نطاق التفرد: دفعة فعّالة واحدة لكل سنة مالية).</summary>
    public int FiscalYear { get; set; }

    /// <summary>تاريخ الترحيل المحاسبي (يجب أن يقع داخل السنة المالية). لا قيمة افتراضية عن قصد.</summary>
    public DateTime EntryDate { get; set; }

    public string? Reference { get; set; }
    public string? Description { get; set; }

    public List<OpeningBalanceLineInput> Lines { get; set; } = new();
}

/// <summary>تعديل مسودة دفعة افتتاحية (مسموح فقط أثناء حالة Draft).</summary>
public class UpdateOpeningBalanceBatchDto
{
    public DateTime EntryDate { get; set; }
    public string? Reference { get; set; }
    public string? Description { get; set; }
    public List<OpeningBalanceLineInput> Lines { get; set; } = new();
}

/// <summary>بند عرض في دفعة الأرصدة الافتتاحية (يشمل تفصيل الطرف/الصنف عند وجوده).</summary>
public class OpeningBalanceLineDto
{
    public Guid AccountId { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public string AccountNameAr { get; set; } = string.Empty;
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public string? Note { get; set; }

    /// <summary>نوع تفصيل البند: «حساب» · «عميل» · «مورد» · «صنف مخزني» (للعرض).</summary>
    public string DetailKindAr { get; set; } = "حساب";

    public Guid? CustomerId { get; set; }
    public string? CustomerCode { get; set; }
    public string? CustomerNameAr { get; set; }

    public Guid? SupplierId { get; set; }
    public string? SupplierCode { get; set; }
    public string? SupplierNameAr { get; set; }

    public Guid? ItemId { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemNameAr { get; set; }

    public Guid? WarehouseId { get; set; }
    public string? WarehouseNameAr { get; set; }

    /// <summary>الكمية الافتتاحية (للبنود المخزنية).</summary>
    public decimal? Quantity { get; set; }

    /// <summary>تكلفة الوحدة الافتتاحية (للبنود المخزنية).</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>قيمة البند (مدين − دائن) — وللبنود المخزنية = الكمية × التكلفة.</summary>
    public decimal Value { get; set; }

    /// <summary>هل أُنشئت حركة مخزون لهذا البند؟ (بعد الترحيل فقط)</summary>
    public bool HasStockMovement { get; set; }
}

/// <summary>عرض دفعة الأرصدة الافتتاحية مع القيد المرتبط وحقول التدقيق.</summary>
public class OpeningBalanceBatchDto
{
    public Guid Id { get; set; }
    public int FiscalYear { get; set; }
    public int Status { get; set; }
    public string StatusNameAr { get; set; } = string.Empty;
    public DateTime EntryDate { get; set; }
    public string? Reference { get; set; }
    public string? Description { get; set; }

    public Guid? JournalEntryId { get; set; }
    public string? EntryNumber { get; set; }

    public Guid? ReversalOfBatchId { get; set; }
    public Guid? ReversedByBatchId { get; set; }

    /// <summary>من أنشأ المسودة وتوقيتها.</summary>
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>من رحّل الدفعة وتوقيت الترحيل.</summary>
    public string? PostedBy { get; set; }
    public DateTime? PostedAt { get; set; }

    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }

    public List<OpeningBalanceLineDto> Lines { get; set; } = new();
}
