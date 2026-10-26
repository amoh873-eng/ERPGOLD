using ERPSystem.Application.DTOs.Accounts;

namespace ERPSystem.Application.Reporting;

/// <summary>
/// بناء المسار الهرمي للحسابات من الشجرة الفعلية (ParentAccountId) — بلا أي حساب أرصدة ولا تعديل بيانات:
///   1 — الأصول / 1100 — الصندوق / 11001 — صندوق نقدية
/// يُستخدم في عرض واختيار الحساب داخل شاشة القيد اليومي (ولأي شاشة تحتاج المسار).
/// محمي ضد العلاقات المكررة أو الدائرية: كل حساب يُزار مرة واحدة فقط.
/// </summary>
public static class AccountHierarchyPaths
{
    /// <summary>الفاصل بين مستويات المسار.</summary>
    public const string Separator = " / ";

    /// <summary>الفاصل بين رمز الحساب واسمه.</summary>
    public const string CodeNameSeparator = " — ";

    /// <summary>
    /// يبني قاموس «معرّف الحساب ⇒ مساره الهرمي الكامل» انطلاقًا من جذور الشجرة.
    /// الجذور أولاً ثم الأبناء ثم الأحفاد (Pre-Order)، وأي عقدة مكررة/دائرية تُتجاهل بأمان.
    /// </summary>
    public static Dictionary<Guid, string> Build(IEnumerable<AccountDto> roots)
    {
        var paths = new Dictionary<Guid, string>();
        var visited = new HashSet<Guid>();

        void Walk(IEnumerable<AccountDto> nodes, List<string> chain)
        {
            foreach (var node in nodes)
            {
                if (node is null) continue;
                if (!visited.Add(node.Id)) continue;   // يمنع الدوران والتكرار في الشجرة

                var next = new List<string>(chain) { node.Code + CodeNameSeparator + node.NameAr };
                paths[node.Id] = string.Join(Separator, next);

                if (node.Children.Count > 0) Walk(node.Children, next);
            }
        }

        Walk(roots, new List<string>());
        return paths;
    }

    /// <summary>مسار الحساب من القاموس المُبنى، أو رمزه واسمه فقط إن لم يوجد مسار.</summary>
    public static string For(AccountDto account, IReadOnlyDictionary<Guid, string> paths)
        => paths.TryGetValue(account.Id, out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : account.Code + CodeNameSeparator + account.NameAr;
}
