using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.Reporting;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// المسار الهرمي للحسابات (يُستخدم في عرض/اختيار الحساب داخل شاشة القيد اليومي):
/// مشتق من الشجرة الفعلية لا من طول الكود، بلا تكرار أو دوران، ويحافظ على الأب/الابن/الحفيد والأعمق.
/// </summary>
public class AccountHierarchyPathsTests
{
    private static AccountDto Node(string code, string name, params AccountDto[] children)
    {
        var account = new AccountDto { Id = Guid.NewGuid(), Code = code, NameAr = name };
        account.Children.AddRange(children);
        return account;
    }

    [Fact]
    public void Builds_full_paths_parent_child_grandchild()
    {
        var grand = Node("11001", "صندوق نقدية");
        var child = Node("1100", "الصندوق", grand);
        var parent = Node("1", "الأصول", child);

        var paths = AccountHierarchyPaths.Build(new[] { parent });

        Assert.Equal("1 — الأصول", paths[parent.Id]);
        Assert.Equal("1 — الأصول / 1100 — الصندوق", paths[child.Id]);
        Assert.Equal("1 — الأصول / 1100 — الصندوق / 11001 — صندوق نقدية", paths[grand.Id]);
        Assert.Equal(3, paths.Count);
    }

    [Fact]
    public void Wide_tree_keeps_each_account_once_with_its_own_path()
    {
        var g1 = Node("9110", "حفيد أول");
        var g2 = Node("9120", "حفيد ثانٍ");
        var c1 = Node("910", "ابن أول", g1, g2);
        var c2 = Node("920", "ابن ثانٍ");
        var root = Node("900", "أب", c1, c2);

        var paths = AccountHierarchyPaths.Build(new[] { root });

        Assert.Equal(5, paths.Count);
        Assert.Equal("900 — أب / 910 — ابن أول / 9110 — حفيد أول", paths[g1.Id]);
        Assert.Equal("900 — أب / 910 — ابن أول / 9120 — حفيد ثانٍ", paths[g2.Id]);
        Assert.Equal("900 — أب / 920 — ابن ثانٍ", paths[c2.Id]);
        Assert.Equal(paths.Keys.Distinct().Count(), paths.Count);
    }

    [Fact]
    public void Deeper_than_grandchild_is_kept_safely()
    {
        var l4 = Node("2100101", "أعمق من حفيد");
        var l3 = Node("21001", "حفيد", l4);
        var l2 = Node("2100", "ابن", l3);
        var root = Node("2", "الخصوم", l2);

        var paths = AccountHierarchyPaths.Build(new[] { root });

        Assert.Equal("2 — الخصوم / 2100 — ابن / 21001 — حفيد / 2100101 — أعمق من حفيد", paths[l4.Id]);
    }

    [Fact]
    public void Duplicate_or_cyclic_nodes_never_loop_or_duplicate()
    {
        var child = Node("1100", "الصندوق");
        var parent = Node("1", "الأصول", child);
        child.Children.Add(parent);                       // علاقة دائرية مُتعمَّدة
        parent.Children.Add(child);                       // تكرار الابن نفسه

        var paths = AccountHierarchyPaths.Build(new[] { parent, parent });   // الجذر مكرر أيضاً

        Assert.Equal(2, paths.Count);
        Assert.Equal("1 — الأصول", paths[parent.Id]);
        Assert.Equal("1 — الأصول / 1100 — الصندوق", paths[child.Id]);
    }

    [Fact]
    public void For_falls_back_to_code_and_name_when_no_path_exists()
    {
        var account = Node("4999", "حساب بلا مسار");
        var paths = AccountHierarchyPaths.Build(Array.Empty<AccountDto>());

        Assert.Equal("4999 — حساب بلا مسار", AccountHierarchyPaths.For(account, paths));
    }
}
