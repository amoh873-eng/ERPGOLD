namespace ERPSystem.Application.DTOs.Accounting;

/// <summary>
/// تقرير مطابقة الحساب الرقابي مع السجل المساعد (قراءة فقط — لا يعدّل أي بيانات ولا يسوّي فروقًا).
///
/// الاصطلاح:
///  • <see cref="ControlBalance"/> = الرصيد من دفتر القيود (سطور غير محذوفة، بلا فلترة نوع).
///  • <see cref="DetailBalance"/> = مجموع الأرصدة التفصيلية (أرصدة العملاء/الموردين المخزَّنة).
///  • <see cref="Difference"/> = الرقابي − التفصيلي.
///  • <see cref="ExplainedAmount"/> = مجموع صافي أثر القيود **غير المستندية** على الحساب
///    (قيود يدوية/افتتاحية/أخرى) — وهي المصدر المتوقع الوحيد للفرق بعد ربط الأرصدة الافتتاحية بالتفصيل.
///  • <see cref="UnexplainedAmount"/> = الفرق − المُفسَّر ⇒ يجب أن يكون صفرًا؛ أي قيمة غير صفرية
///    تعني فرقًا يحتاج تحقيقًا (مثل دفعة افتتاحية قديمة بلا تفصيل، أو طرف محذوف/غير مُزامَن).
/// </summary>
public class SubLedgerReconciliationDto
{
    public DateTime GeneratedAt { get; set; }
    public List<SubLedgerReconciliationRowDto> Rows { get; set; } = new();

    /// <summary>
    /// سرد **صريح** للحركات غير المُنسَبة على الحسابات الرقابية (قيود يدوية/افتتاحية بلا طرف)
    /// — لا يُخفى ولا يُسوّى تلقائيًا؛ يُمكّن المدقّق من معالجتها يدويًا بقرار موثَّق.
    /// </summary>
    public List<UnattributedControlLineDto> UnattributedControlLines { get; set; } = new();

    public List<string> Notes { get; set; } = new();

    /// <summary>مجموع |الأثر| غير المُنسَب على كل الحسابات الرقابية (مؤشر سريع).</summary>
    public decimal TotalUnattributedAmount { get; set; }
}

/// <summary>حركة على حساب رقابي بلا إسناد طرف — للعرض والتحقيق اليدوي.</summary>
public class UnattributedControlLineDto
{
    public string AccountCode { get; set; } = string.Empty;
    public string AccountNameAr { get; set; } = string.Empty;

    /// <summary>رقم القيد (للرجوع إليه في شاشة القيود).</summary>
    public string EntryNumber { get; set; } = string.Empty;

    public int EntryType { get; set; }
    public string EntryTypeNameAr { get; set; } = string.Empty;
    public DateTime EntryDate { get; set; }
    public string? Reference { get; set; }

    /// <summary>الأثر المُوقَّع على الحساب بطبيعته (مدين − دائن أو دائن − مدين).</summary>
    public decimal SignedAmount { get; set; }

    /// <summary>تاريخ الإنشاء (لترتيب الأحدث أولًا).</summary>
    public DateTime CreatedAt { get; set; }
}

public class SubLedgerReconciliationRowDto
{
    public string AccountCode { get; set; } = string.Empty;
    public string AccountNameAr { get; set; } = string.Empty;

    /// <summary>وصف مصدر الرصيد التفصيلي (مثال: «أرصدة العملاء التفصيلية»).</summary>
    public string DetailSourceAr { get; set; } = string.Empty;

    public decimal ControlBalance { get; set; }
    public decimal DetailBalance { get; set; }
    public decimal Difference { get; set; }
    public decimal ExplainedAmount { get; set; }
    public decimal UnexplainedAmount { get; set; }

    /// <summary>عدد الأطراف/الأصناف ذات رصيد تفصيلي غير صفري.</summary>
    public int DetailRowCount { get; set; }

    /// <summary>أثر القيود غير المستندية على الحساب مُفصَّلًا بنوع القيد (للتفسير لا للتسوية).</summary>
    public List<SubLedgerEffectDto> NonDocumentEffects { get; set; } = new();

    /// <summary>حالة المطابقة بلغة مفهومة (متطابق · فرق مُفسَّر · فرق غير مُفسَّر).</summary>
    public string StatusAr { get; set; } = string.Empty;

    /// <summary>ملاحظات خاصة بالصف (مثل: تقييم المخزون يحتاج سياسة معتمدة).</summary>
    public string? Note { get; set; }
}

/// <summary>أثر نوع قيد على حساب (صافي مدين − دائن) — للعرض والتفسير.</summary>
public class SubLedgerEffectDto
{
    public int EntryType { get; set; }
    public string EntryTypeNameAr { get; set; } = string.Empty;
    public decimal NetAmount { get; set; }
    public int LineCount { get; set; }
    public bool IsDocumentDriven { get; set; }
}
