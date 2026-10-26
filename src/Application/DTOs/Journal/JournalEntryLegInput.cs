namespace ERPSystem.Application.DTOs.Journal;

/// <summary>
/// One debit/credit leg passed to the journal posting helper.
/// Each leg must have exactly one of DebitAmount or CreditAmount equal to zero
/// (a single leg cannot be both a debit and a credit).
/// </summary>
public class JournalEntryLegInput
{
    public Guid AccountId { get; set; }
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public string? Note { get; set; }

    // ───────────────── إسناد الطرف (2026-10-25) — للقيود **اليدوية** فقط ─────────────────
    // لماذا؟ الحسابان الرقابيان 1200/2200 يجب أن يبقيا مساويين لمجموع الأرصدة التفصيلية.
    // مسارات المستندات تُحدّث أرصدة الأطراف داخلها already، فلا تحتاج هذين الحقلين (تبقى null).
    // أما القيد اليدوي: إن أسند المستخدم طرفًا هنا يُزامن رصيده مع الرصيد الرقابي في نفس وحدة
    // العمل ⇒ لا ينشأ فرق. وإن لم يُسند (قيود تصحيحية مشروعة بلا طرف) يبقى القيد مسموحًا
    // ويُعرض أثره صريحًا في تقرير المطابقة (لا يُخفى ولا يُسوّى تلقائيًا).
    // القواعد: العميل على 1200 فقط · المورد على 2200 فقط · ولا يجتمعان في بند واحد.

    /// <summary>العميل المُسند إليه البند (مقبول على حساب العملاء 1200 فقط).</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>المورد المُسند إليه البند (مقبول على حساب الموردين 2200 فقط).</summary>
    public Guid? SupplierId { get; set; }
}
