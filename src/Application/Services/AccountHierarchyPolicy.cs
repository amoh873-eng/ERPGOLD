using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Application.Services;

/// <summary>
/// سياسة هرمية دليل الحسابات — أربعة مستويات فعلية (الجد ← الأب ← الحفيد ← ابن الحفيد).
///
/// المستوى **يُشتق من علاقة الأب الفعلية** (<c>Account.ParentAccountId</c>) — لا من اسم الحساب
/// ولا من طول رقمه، ولا من عمود مخزَّن (العمودان Level/HierarchyPath غير مستخدمين بالتصميم).
///
/// القواعد الملزمة التي تطبّقها هذه السياسة (نقطة واحدة لكل مسارات الكتابة):
///   1) حساب بلا أب ⇒ المستوى الأول (الجد).
///   2) حساب تحت مستوى n ⇒ المستوى n+1.
///   3) الحد الأقصى أربعة مستويات: أي إسناد يُنتج مستوى خامسًا يُرفض.
///   4) يُرفض أن يكون الحساب أبًا لنفسه.
///   5) يُرفض إنشاء علاقة دائرية (نقل حساب تحت أحد أحفاده/أبنائه).
///   6) يُرفض أبٌ غير موجود أو محذوف أو معطَّل (لتفادي سلاسل مقطوعة).
///
/// نقاط الاستخدام: AccountService.CreateAsync / UpdateAsync (مسار الإنشاء والتعديل الرسمي).
/// </summary>
public static class AccountHierarchyPolicy
{
    /// <summary>الحد الأقصى لعمق الشجرة (أربعة مستويات) — مصدره تعريف أسماء المستويات نفسه.</summary>
    public const int MaxLevel = AccountTreeLevels.MaxLevel;

    // ────────────────────────── قراءة الشجرة ──────────────────────────

    /// <summary>خريطة (معرّف الحساب ⇒ معرّف أبيه) لكل الحسابات غير المحذوفة — استعلام واحد.</summary>
    public static async Task<Dictionary<Guid, Guid?>> LoadParentMapAsync(DbContext context)
        => await context.Set<Account>().AsNoTracking()
            .Select(a => new { a.Id, a.ParentAccountId })
            .ToDictionaryAsync(x => x.Id, x => x.ParentAccountId);

    /// <summary>
    /// مستوى الحساب الحقيقي من خريطة الأبوة: بلا أب ⇒ 1، وكل أب في السلسلة يزيد واحدًا.
    /// محمي من الدوران في بيانات قديمة (كل حساب يُزار مرة واحدة).
    /// </summary>
    public static int LevelOf(Guid accountId, IReadOnlyDictionary<Guid, Guid?> parentMap)
    {
        var level = 1;
        var visited = new HashSet<Guid> { accountId };
        var current = accountId;

        while (parentMap.TryGetValue(current, out var parentId)
               && parentId.HasValue
               && visited.Add(parentId.Value))
        {
            level++;
            current = parentId.Value;
        }

        return level;
    }

    /// <summary>مستوى الحساب الحقيقي (يقرأ الشجرة كاملة — استعلام واحد).</summary>
    public static async Task<int> LevelOfAsync(DbContext context, Guid accountId)
        => LevelOf(accountId, await LoadParentMapAsync(context));

    /// <summary>مستويات كل الحسابات غير المحذوفة (معرّف ⇒ مستوى) — لتفادي N+1 في الشاشات.</summary>
    public static async Task<Dictionary<Guid, int>> LevelMapAsync(DbContext context)
    {
        var map = await LoadParentMapAsync(context);
        return map.Keys.ToDictionary(id => id, id => LevelOf(id, map));
    }

    /// <summary>كل أحفاد الحساب (أبناؤه وأبناء أبنائه...) — تُستخدم لمنع العلاقات الدائرية.</summary>
    public static HashSet<Guid> DescendantsOf(Guid accountId, IReadOnlyDictionary<Guid, Guid?> parentMap)
    {
        var childrenByParent = parentMap
            .Where(kv => kv.Value.HasValue)
            .GroupBy(kv => kv.Value!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());

        var descendants = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(accountId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!childrenByParent.TryGetValue(current, out var children)) continue;

            foreach (var child in children)
                if (descendants.Add(child)) stack.Push(child);   // Add=false ⇒ زُور سابقًا (حماية من الدوران)
        }

        return descendants;
    }

    /// <summary>هل الحساب تجميعي (له أبناء غير محذوفين)؟ ⇒ لا يقبل حركات مباشرة.</summary>
    public static async Task<bool> IsAggregateAsync(DbContext context, Guid accountId)
        => await context.Set<Account>().AnyAsync(a => a.ParentAccountId == accountId && !a.IsDeleted);

    /// <summary>
    /// الحسابات الصالحة كأب (تُستخدم في منسدلة الشاشة لمنع اختيار حساب غير صالح):
    /// كل الحسابات إلا الحساب نفسه وأحفاده، وإلا الحسابات في المستوى الرابع (آخر مستوى).
    /// </summary>
    public static async Task<HashSet<Guid>> ValidParentIdsAsync(DbContext context, Guid? editingAccountId)
    {
        var map = await LoadParentMapAsync(context);
        var excluded = editingAccountId.HasValue
            ? DescendantsOf(editingAccountId.Value, map)
            : new HashSet<Guid>();

        if (editingAccountId.HasValue) excluded.Add(editingAccountId.Value);

        var valid = new HashSet<Guid>();
        foreach (var id in map.Keys)
        {
            if (excluded.Contains(id)) continue;
            if (!AccountTreeLevels.CanHaveChildren(LevelOf(id, map))) continue;
            valid.Add(id);
        }
        return valid;
    }

    /// <summary>
    /// ارتفاع الشجرة تحت الحساب المعطى (1 = بلا أبناء، 2 = له أبناء فقط، ... إلخ).
    /// يُستخدم لمنع تجاوز المستوى الرابع عند **نقل فرع كامل** تحت أب جديد.
    /// </summary>
    public static int HeightOf(Guid accountId, IReadOnlyDictionary<Guid, Guid?> parentMap)
    {
        var childrenByParent = parentMap
            .Where(kv => kv.Value.HasValue)
            .GroupBy(kv => kv.Value!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());

        var height = 0;
        var stack = new Stack<(Guid Id, int Depth)>();
        stack.Push((accountId, 1));

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (depth > height) height = depth;
            if (childrenByParent.TryGetValue(current, out var children))
                foreach (var child in children)
                    stack.Push((child, depth + 1));
        }

        return height;
    }

    // ────────────────────────── التحقق قبل الكتابة ──────────────────────────

    /// <summary>
    /// يتحقق من صحة إسناد الأب لحساب (إنشاءً أو تعديلًا) ويرمي <see cref="InvalidOperationException"/>
    /// برسالة عربية واضحة عند أي مخالفة. لا يكتب شيئًا ولا يحفظ.
    /// </summary>
    /// <param name="context">سياق قاعدة البيانات.</param>
    /// <param name="accountId">معرّف الحساب عند التعديل، أو null عند الإنشاء.</param>
    /// <param name="parentAccountId">الأب المطلوب، أو null لحساب في المستوى الأول (الجد).</param>
    /// <returns>مستوى الحساب الناتج عن الإسناد (1..4).</returns>
    public static async Task<int> EnsureParentAssignmentAllowedAsync(
        DbContext context, Guid? accountId, Guid? parentAccountId)
    {
        // (1) بلا أب ⇒ المستوى الأول (الجد) — مسموح دائمًا.
        if (parentAccountId is null)
            return AccountTreeLevels.MinLevel;

        // (2) لا يكون الحساب أبًا لنفسه.
        if (accountId.HasValue && accountId.Value == parentAccountId.Value)
            throw new InvalidOperationException("لا يمكن أن يكون الحساب أباً لنفسه.");

        // (3) الأب موجود وغير محذوف.
        var parent = await context.Set<Account>().AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == parentAccountId.Value && !a.IsDeleted);
        if (parent is null)
            throw new InvalidOperationException("الحساب الأب غير موجود.");

        var parentMap = await LoadParentMapAsync(context);
        var parentLevel = LevelOf(parent.Id, parentMap);

        // (4) منع المستوى الخامس: لا يُضاف ابن تحت حساب في المستوى الرابع (آخر مستوى).
        if (!AccountTreeLevels.CanHaveChildren(parentLevel))
            throw new InvalidOperationException(
                $"لا يمكن إضافة حساب تحت «{parent.Code} — {parent.NameAr}» لأن مستواه " +
                $"«{AccountTreeLevels.NameAr(parentLevel)}» وهو آخر مستوى مسموح " +
                $"(الحد الأقصى {MaxLevel} مستويات). اربط الحساب الجديد بمستوى أعلى.");

        // (5) منع العلاقة الدائرية: الأب لا يكون من أبناء/أحفاد الحساب نفسه.
        if (accountId.HasValue)
        {
            var descendants = DescendantsOf(accountId.Value, parentMap);
            if (descendants.Contains(parent.Id))
                throw new InvalidOperationException(
                    $"لا يمكن جعل «{parent.Code} — {parent.NameAr}» أبًا لهذا الحساب " +
                    "لأنه من أبنائه أو أحفاده (علاقة دائرية مرفوضة).");
        }

        // (5b) منع تجاوز المستوى الرابع عند نقل فرع كامل: أعمق حساب في الفرع المنقول
        //      سيصبح على المستوى (parentLevel + ارتفاع الفرع) ويجب ألا يتجاوز الحد الأقصى.
        if (accountId.HasValue)
        {
            var subtreeHeight = HeightOf(accountId.Value, parentMap);
            var deepestLevel = parentLevel + subtreeHeight;
            if (deepestLevel > MaxLevel)
                throw new InvalidOperationException(
                    $"نقل هذا الحساب تحت «{parent.Code} — {parent.NameAr}» " +
                    $"(مستواه «{AccountTreeLevels.NameAr(parentLevel)}») يجعل أعمق حساب في فرعه " +
                    $"بالمستوى {deepestLevel} وهو خارج الحد المسموح ({MaxLevel} مستويات). " +
                    "اختر أبًا في مستوى أعلى أو انقل الفروع أولًا.");
        }

        // (6) حساب معطَّل لا يُستخدم كأب (يمنع سلاسل غير صالحة للترحيل).
        if (!parent.IsActive)
            throw new InvalidOperationException(
                $"لا يمكن ربط الحساب بحساب أب معطَّل: «{parent.Code} — {parent.NameAr}».");

        var childLevel = AccountTreeLevels.ChildLevelOf(parentLevel);
        if (!AccountTreeLevels.IsValidLevel(childLevel))
            throw new InvalidOperationException(
                $"إسناد هذا الأب يُنتج «{AccountTreeLevels.NameAr(childLevel)}» وهو خارج الحد المسموح " +
                $"(الحد الأقصى {MaxLevel} مستويات).");

        return childLevel;
    }

    /// <summary>المستوى الناتج عن إسناد أب دون أي تحقق — للعرض الفوري في الواجهة فقط.</summary>
    public static int PreviewChildLevel(int? parentLevel) => AccountTreeLevels.ChildLevelOf(parentLevel);
}
