using ERPSystem.Application.DTOs.Accounts;

namespace ERPSystem.Application.Interfaces;

/// <summary>
/// مصدر الاحتساب الموحّد لأرصدة شجرة الحسابات (أب/ابن/حفيد).
///
/// لماذا خدمة مشتركة؟
///  - شاشة شجرة الحسابات والتقرير الموحّد يجب أن يعرضا **نفس الأرقام** بلا منطق مكرر.
///  - منطق الاحتساب محاسبي ومركزي، وليس مسؤولية مكوّنات الواجهة (Blazor).
///
/// سياسة الأرصدة المعتمدة: الحركات المعتمدة (بنود القيود غير المحذوفة، والقيود غير المحذوفة)
/// مقسّمة على الحساب الذي سُجّلت عليه، ثم تُجمَّع هرمياً صعوداً (مباشر + الأبناء) بلا ازدواجية.
/// </summary>
public interface IAccountBalanceService
{
    /// <summary>
    /// شجرة الأرصدة كاملة (مع الإجماليات) وفق النطاق المطلوب.
    /// التنفيذ يستخدم استعلامات تجميعية محدودة ثابتة العدد (لا استعلام لكل حساب ⇒ لا N+1).
    /// </summary>
    Task<AccountBalanceTreeDto> GetBalanceTreeAsync(AccountBalanceQuery? query = null);

    /// <summary>أرصدة مسطّحة بترتيب هرمي (أب ثم أبناؤه ثم أحفاده) — لبناء التقارير.</summary>
    Task<List<AccountBalanceNodeDto>> GetFlatBalancesAsync(AccountBalanceQuery? query = null);
}
