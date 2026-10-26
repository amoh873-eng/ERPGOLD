using ERPSystem.Domain.Common;

namespace ERPSystem.Domain.Entities;

/// <summary>
/// Represents a single debit or credit leg of a journal entry (بند مدين/دائن في القيد).
/// لكل بند: إما DebitAmount موجبة وإما CreditAmount موجبة (نادراً الاثنان معاً).
/// القيد متوازن إذا كان Σ(debits) == Σ(credits).
/// </summary>
public class JournalEntryLine : BaseEntity
{
    /// <summary>
    /// Foreign key to the parent journal entry.
    /// </summary>
    public Guid JournalEntryId { get; set; }

    /// <summary>
    /// Navigation property to the parent journal entry.
    /// </summary>
    public JournalEntry? JournalEntry { get; set; }

    /// <summary>
    /// Foreign key to the account this leg affects.
    /// </summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// Navigation property to the account.
    /// </summary>
    public Account? Account { get; set; }

    /// <summary>
    /// Debit amount (> 0 when this leg is a debit).
    /// </summary>
    public decimal DebitAmount { get; set; }

    /// <summary>
    /// Credit amount (> 0 when this leg is a credit).
    /// </summary>
    public decimal CreditAmount { get; set; }

    /// <summary>
    /// Optional note describing this specific leg.
    /// </summary>
    public string? Note { get; set; }

    // ─────────────── إسناد الطرف على بند القيد (2026-10-25) ───────────────
    // يُخزَّن الإسناد **على بند القيد** ليكون أثرًا تدقيقيًا دائمًا (لا مجرد حالة عابرة عند الإنشاء):
    //  • بند 1200 مُسند إلى عميل ⇒ رصيد العميل يُزامَن مع الرصيد الرقابي ⇒ لا فرق بينهما.
    //  • بند 1200 **بلا** إسناد (قيد يدوي/افتتاحي بلا طرف) ⇒ يبقى مسموحًا ويُعرض صريحًا في تقرير
    //    المطابقة ضمن «غير المُنسَب» — لا حظر ولا تسوية تلقائية.
    // مسارات المستندات (فواتير/سندات/مردودات) تُحدّث أرصدة الأطراف داخلها ولا تُسند هذه الحقول
    // (تبقى null) فلا يتغيّر سلوكها ولا تتحقق مطابقة مزدوجة.

    /// <summary>العميل المُسند إليه هذا البند (على حساب العملاء 1200 فقط) — أو null.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>المورد المُسند إليه هذا البند (على حساب الموردين 2200 فقط) — أو null.</summary>
    public Guid? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
}
