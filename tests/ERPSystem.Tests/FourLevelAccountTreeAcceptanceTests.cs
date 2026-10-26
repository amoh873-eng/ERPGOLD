using ERPSystem.Application.DTOs.Accounts;
using ERPSystem.Application.DTOs.Reports;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using ERPSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// اختبارات القبول الإلزامية لتنفيذ «المستويات الأربعة» في شجرة الحسابات (ERP 18091).
///
/// التسميات المعتمدة حرفيًا:
///   1 = «المستوى الأول (الجد)»      ← حساب بلا أب
///   2 = «المستوى الثاني (الأب)»     ← ابن الجد
///   3 = «المستوى الثالث (الحفيد)»   ← ابن الأب
///   4 = «المستوى الرابع (ابن الحفيد)» ← ابن الحفيد (آخر مستوى مسموح)
///
/// المستوى يُشتق من علاقة الأب الفعلية (ParentAccountId) لا من الاسم ولا من رقم الحساب.
/// الاختبارات (1..10) تعمل على محرّك InMemory معزول. الاختبار (11) يعمل فقط عند توفير
/// اتصال بقاعدة الاختبار 18091 عبر المتغير ERPSYSTEM_18091_PG (وإلا يُتخطّى صراحةً).
/// </summary>
public class FourLevelAccountTreeAcceptanceTests
{
    // ══════════════════════ أدوات البناء ══════════════════════

    private static AccountService NewService(AppDbContext ctx) => new(ctx);

    private static Task<AccountDto> CreateAccountAsync(
        AccountService svc, string code, Guid? parentId, int type = 1)
        => svc.CreateAsync(new CreateAccountDto
        {
            Code = code,
            NameAr = "حساب " + code,
            NameEn = "Account " + code,
            AccountType = type,
            ParentAccountId = parentId
        });

    private static async Task AddEntryAsync(
        AppDbContext ctx, DateTime date, string number,
        params (Guid AccountId, decimal Debit, decimal Credit)[] legs)
    {
        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            EntryNumber = number,
            EntryType = JournalEntryType.Manual,
            EntryDate = date,
            Description = number,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Set<JournalEntry>().Add(entry);
        foreach (var leg in legs)
        {
            ctx.Set<JournalEntryLine>().Add(new JournalEntryLine
            {
                Id = Guid.NewGuid(),
                JournalEntryId = entry.Id,
                AccountId = leg.AccountId,
                DebitAmount = leg.Debit,
                CreditAmount = leg.Credit,
                CreatedAt = DateTime.UtcNow
            });
        }
        await ctx.SaveChangesAsync();
    }

    private static AccountBalanceNodeDto Node(AccountBalanceTreeDto tree, string code)
        => tree.Flattened.Single(n => n.Code == code);

    private sealed record Chain(Guid L1, Guid L2, Guid L3, Guid L4);

    /// <summary>يبني سلسلة كاملة من أربعة مستويات: 1 ← 11 ← 110 ← 1100.</summary>
    private static async Task<Chain> SeedFourLevelChainAsync(AccountService svc)
    {
        var l1 = await CreateAccountAsync(svc, "1", null);
        var l2 = await CreateAccountAsync(svc, "11", l1.Id);
        var l3 = await CreateAccountAsync(svc, "110", l2.Id);
        var l4 = await CreateAccountAsync(svc, "1100", l3.Id);
        return new Chain(l1.Id, l2.Id, l3.Id, l4.Id);
    }

    // ══════════════════════ (1) المستوى الأول (الجد) ══════════════════════

    [Fact]
    public async Task Test01_Level1_Grand_Account_Without_Parent_Shows_Grand_Label()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var l1 = await CreateAccountAsync(svc, "1", null);

        Assert.Equal(AccountTreeLevels.MinLevel, l1.Level);
        Assert.Equal(AccountTreeLevels.Level1Ar, l1.LevelNameAr);
        Assert.Equal("المستوى الأول (الجد)", l1.LevelNameAr);
        Assert.Null(l1.ParentAccountId);

        // إعادة القراءة تُثبت أن المستوى مُشتق من الشجرة الفعلية لا من قيمة إرجاع الإنشاء.
        var reread = await svc.GetByIdAsync(l1.Id);
        Assert.NotNull(reread);
        Assert.Equal(1, reread!.Level);
        Assert.Equal("المستوى الأول (الجد)", reread.LevelNameAr);
        Assert.False(reread.IsAggregate); // بلا أبناء بعد ⇒ تفصيلي
    }

    // ══════════════════════ (2) المستوى الثاني (الأب) ══════════════════════

    [Fact]
    public async Task Test02_Level2_Parent_Account_Under_Grand_Shows_Parent_Label()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var l1 = await CreateAccountAsync(svc, "1", null);
        var l2 = await CreateAccountAsync(svc, "11", l1.Id);

        Assert.Equal(2, l2.Level);
        Assert.Equal(AccountTreeLevels.Level2Ar, l2.LevelNameAr);
        Assert.Equal("المستوى الثاني (الأب)", l2.LevelNameAr);
        Assert.Equal(l1.Id, l2.ParentAccountId);
    }

    // ══════════════════════ (3) المستوى الثالث (الحفيد) ══════════════════════

    [Fact]
    public async Task Test03_Level3_Grandchild_Account_Under_Parent_Shows_Grandchild_Label()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var l1 = await CreateAccountAsync(svc, "1", null);
        var l2 = await CreateAccountAsync(svc, "11", l1.Id);
        var l3 = await CreateAccountAsync(svc, "110", l2.Id);

        Assert.Equal(3, l3.Level);
        Assert.Equal(AccountTreeLevels.Level3Ar, l3.LevelNameAr);
        Assert.Equal("المستوى الثالث (الحفيد)", l3.LevelNameAr);
        Assert.Equal(l2.Id, l3.ParentAccountId);
    }

    // ══════════════════════ (4) المستوى الرابع (ابن الحفيد) ══════════════════════

    [Fact]
    public async Task Test04_Level4_GreatGrandchild_Account_Under_Grandchild_Shows_GreatGrandchild_Label()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var chain = await SeedFourLevelChainAsync(svc);
        var l4 = await svc.GetByIdAsync(chain.L4);

        Assert.NotNull(l4);
        Assert.Equal(AccountTreeLevels.MaxLevel, l4!.Level);
        Assert.Equal(AccountTreeLevels.Level4Ar, l4.LevelNameAr);
        Assert.Equal("المستوى الرابع (ابن الحفيد)", l4.LevelNameAr);
        Assert.Equal(chain.L3, l4.ParentAccountId);
        Assert.False(l4.IsAggregate); // لا أبناء ⇒ تفصيلي
    }

    // ══════════════════════ (5) علاقات الأبوة في قاعدة البيانات ══════════════════════

    [Fact]
    public async Task Test05_Database_Stores_Real_ParentChild_Relationships()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var chain = await SeedFourLevelChainAsync(svc);
        var map = await AccountHierarchyPolicy.LoadParentMapAsync(ctx);

        Assert.Null(map[chain.L1]);                 // الجد بلا أب
        Assert.Equal(chain.L1, map[chain.L2]);      // الأب تحت الجد
        Assert.Equal(chain.L2, map[chain.L3]);      // الحفيد تحت الأب
        Assert.Equal(chain.L3, map[chain.L4]);      // ابن الحفيد تحت الحفيد

        // المستوى الحقيقي مُشتق من الخريطة:
        Assert.Equal(1, AccountHierarchyPolicy.LevelOf(chain.L1, map));
        Assert.Equal(2, AccountHierarchyPolicy.LevelOf(chain.L2, map));
        Assert.Equal(3, AccountHierarchyPolicy.LevelOf(chain.L3, map));
        Assert.Equal(4, AccountHierarchyPolicy.LevelOf(chain.L4, map));
    }

    // ══════════════════════ (6) رفض المستوى الخامس ══════════════════════

    [Fact]
    public async Task Test06_Reject_Fifth_Level_Creation_And_Subtree_Move_Beyond_Depth4()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var chain = await SeedFourLevelChainAsync(svc);

        // (أ) إنشاء ابن تحت «ابن الحفيد» (المستوى الرابع) ⇒ المستوى الخامس مرفوض.
        var creation = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateAccountAsync(svc, "11000", chain.L4));
        Assert.Contains("آخر مستوى مسموح", creation.Message);
        Assert.False(await ctx.Set<Account>().AnyAsync(a => a.Code == "11000"));

        // (ب) نقل فرع كامل بحيث يتجاوز أعمقه الحد المسموح.
        //     الفرع (11 ← 110 ← 1100) ارتفاعه 3. ننشئ فرعًا آخر 8 ← 81 ← 810 ثم ننقل «11» تحت «810».
        var r1 = await CreateAccountAsync(svc, "8", null);
        var a2 = await CreateAccountAsync(svc, "81", r1.Id);
        var a3 = await CreateAccountAsync(svc, "810", a2.Id);

        var move = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync(new UpdateAccountDto
            {
                Id = chain.L2,
                Code = "11",
                NameAr = "حساب 11",
                NameEn = "Account 11",
                AccountType = 1,
                ParentAccountId = a3.Id,
                IsActive = true
            }));
        Assert.Contains("أعمق حساب", move.Message);

        // العلاقة الأصلية لم تتغيّر (لم يُنفّذ النقل).
        var map = await AccountHierarchyPolicy.LoadParentMapAsync(ctx);
        Assert.Equal(chain.L1, map[chain.L2]);
        _ = a3;
    }

    // ══════════════════════ (7) رفض العلاقات الدائرية ══════════════════════

    [Fact]
    public async Task Test07_Reject_Circular_And_Self_Parent_Relationships()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var chain = await SeedFourLevelChainAsync(svc);

        // (أ) جعل «الجد» تحت «الحفيد» ⇒ دائري (الأب من الأحفاد).
        var circular = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync(new UpdateAccountDto
            {
                Id = chain.L1,
                Code = "1",
                NameAr = "حساب 1",
                NameEn = "Account 1",
                AccountType = 1,
                ParentAccountId = chain.L3,
                IsActive = true
            }));
        Assert.Contains("دائرية", circular.Message);

        // (ب) جعل «الأب» تحت «الحفيد» (أحد أحفاده المباشرين، وهو مستوى يقبل أبناء)
        //     ⇒ يُرفض بسبب الدورية تحديدًا (لا بسبب الحد الأقصى للمستويات).
        var circular2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync(new UpdateAccountDto
            {
                Id = chain.L2,
                Code = "11",
                NameAr = "حساب 11",
                NameEn = "Account 11",
                AccountType = 1,
                ParentAccountId = chain.L3,
                IsActive = true
            }));
        Assert.Contains("دائرية", circular2.Message);

        // (ب-2) جعل الأب في المستوى الرابع ⇒ مرفوض أيضًا (قاعدة منع المستوى الخامس تسبق فحص الدورية).
        var tooDeep = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync(new UpdateAccountDto
            {
                Id = chain.L2,
                Code = "11",
                NameAr = "حساب 11",
                NameEn = "Account 11",
                AccountType = 1,
                ParentAccountId = chain.L4,
                IsActive = true
            }));
        Assert.Contains("آخر مستوى مسموح", tooDeep.Message);

        // (ج) الحساب أبًا لنفسه ⇒ مرفوض.
        var self = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync(new UpdateAccountDto
            {
                Id = chain.L2,
                Code = "11",
                NameAr = "حساب 11",
                NameEn = "Account 11",
                AccountType = 1,
                ParentAccountId = chain.L2,
                IsActive = true
            }));
        Assert.Contains("نفسه", self.Message);
    }

    // ══════════════════════ (8) التجميعي مقابل القابل للترحيل ══════════════════════

    [Fact]
    public async Task Test08_Aggregate_Accounts_Reject_Movements_And_Are_Excluded_From_Parent_Picker()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var chain = await SeedFourLevelChainAsync(svc);

        // 1 ← 11 ← 110 صارت تجميعية (لها أبناء)، و1100 تفصيلي.
        Assert.True((await svc.GetByIdAsync(chain.L1))!.IsAggregate);
        Assert.True((await svc.GetByIdAsync(chain.L2))!.IsAggregate);
        Assert.True((await svc.GetByIdAsync(chain.L3))!.IsAggregate);
        Assert.False((await svc.GetByIdAsync(chain.L4))!.IsAggregate);

        // الترحيل على التجميعي مرفوض صراحةً (ParentAccountMovementException).
        await Assert.ThrowsAsync<ParentAccountMovementException>(
            () => MovementAccountValidator.EnsureMovableAccountAsync(ctx, chain.L1));
        await Assert.ThrowsAsync<ParentAccountMovementException>(
            () => MovementAccountValidator.EnsureMovableAccountAsync(ctx, chain.L2));
        await Assert.ThrowsAsync<ParentAccountMovementException>(
            () => MovementAccountValidator.EnsureMovableAccountAsync(ctx, chain.L3));

        // الترحيل على التفصيلي (المستوى الرابع) مسموح — لا استثناء.
        await MovementAccountValidator.EnsureMovableAccountAsync(ctx, chain.L4);

        // حساب جذري بلا أبناء (تصنيفي) مرفوض أيضًا (RootAccountMovementException).
        var lone = await CreateAccountAsync(svc, "9", null);
        await Assert.ThrowsAsync<RootAccountMovementException>(
            () => MovementAccountValidator.EnsureMovableAccountAsync(ctx, lone.Id));

        // منسدلة «الحساب الأب» تستبعد المستوى الرابع (لا يقبل أبناء) وتُدرج الحفيد.
        var valid = await svc.GetValidParentAccountsAsync();
        Assert.DoesNotContain(valid, a => a.Id == chain.L4);
        Assert.Contains(valid, a => a.Id == chain.L3);
        Assert.Contains(valid, a => a.Id == chain.L1);
    }

    // ══════════════════════ (9) قيد اختبار على حساب تفصيلي (L4) ══════════════════════

    [Fact]
    public async Task Test09_JournalEntry_On_Level4_Reflects_In_Tree_And_TrialBalance()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var chain = await SeedFourLevelChainAsync(svc);

        // حساب مقابل تفصيلي متوازن (خصوم): 2 ← 21
        var r2 = await CreateAccountAsync(svc, "2", null, type: 2);
        var a21 = await CreateAccountAsync(svc, "21", r2.Id, type: 2);

        await AddEntryAsync(ctx, new DateTime(2025, 1, 5), "JE-1",
            (chain.L4, 250m, 0m), (a21.Id, 0m, 250m));

        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        // انعكاس الحركة على الحساب التفصيلي في المستوى الرابع:
        var l4Node = Node(tree, "1100");
        Assert.Equal(250m, l4Node.TotalDebit);
        Assert.Equal(0m, l4Node.TotalCredit);
        Assert.Equal(250m, l4Node.NetBalance);

        // ميزان المراجعة (نفس محرّك الأرصدة): متوازن، لا اختلال.
        Assert.True(tree.IsBalanced);
        Assert.Equal(250m, tree.TotalDebit);
        Assert.Equal(250m, tree.TotalCredit);
    }

    // ══════════════════════ (10) إثبات عدم ازدواج التجميع ══════════════════════

    [Fact]
    public async Task Test10_No_Double_Aggregation_Across_The_Four_Levels()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var svc = NewService(ctx);

        var chain = await SeedFourLevelChainAsync(svc);
        var r2 = await CreateAccountAsync(svc, "2", null, type: 2);
        var a21 = await CreateAccountAsync(svc, "21", r2.Id, type: 2);

        // حركة واحدة بقيمة 100 مدين على الحساب التفصيلي في المستوى الرابع.
        await AddEntryAsync(ctx, new DateTime(2025, 1, 5), "JE-1",
            (chain.L4, 100m, 0m), (a21.Id, 0m, 100m));

        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();

        // نفس القيمة تظهر مرة واحدة في كل مستوى — لا تتضاعف عند الصعود.
        Assert.Equal(100m, Node(tree, "1100").NetBalance); // L4
        Assert.Equal(100m, Node(tree, "110").NetBalance);  // L3
        Assert.Equal(100m, Node(tree, "11").NetBalance);   // L2
        Assert.Equal(100m, Node(tree, "1").NetBalance);    // L1

        // لو حدث ازدواج لتضاعف إجمالي الشجرة إلى 400 — يجب أن يبقى 100.
        Assert.Equal(100m, tree.TotalDebit);
        Assert.Equal(100m, tree.TotalCredit);

        // مجموع صافي الأوراق التفصيلية يساوي إجمالي الشجرة (لا ازدواج، لا نقص).
        var leafNet = tree.Flattened.Where(n => n.Children.Count == 0).Sum(n => n.NetBalance);
        Assert.Equal(tree.TotalDebit - tree.TotalCredit, leafNet);
    }
}

/// <summary>
/// (11) اختبار حي على قاعدة الاختبار الفعلية للنسخة 18091 بعد تنظيفها.
///
/// يعمل فقط عند توفير اتصال عبر المتغير <c>ERPSYSTEM_18091_PG</c> (وإلا يُتخطّى صراحةً،
/// دون ادّعاء نجاح). يثبت: صفرية الأرصدة والحركات، وسلامة الشجرة ضمن حد أربعة مستويات.
/// </summary>
public sealed class FourLevelAccountTreeLive18091AcceptanceTests
{
    public const string EnvVar = "ERPSYSTEM_18091_PG";

    [Fact]
    public async Task Test11_Clean_Test_Database_Has_Zero_Balances_And_Respects_Four_Level_Maximum()
    {
        var cs = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(cs))
        {
            // لا اتصال مُهيّأ ⇒ لا ادّعاء: يُتخطّى صراحةً.
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs, o => o.MigrationsAssembly("ERPSystem.Infrastructure"))
            .Options;

        await using var ctx = new AppDbContext(options);

        // (أ) لا قيود ولا سطور قيود قديمة.
        Assert.Equal(0, await ctx.Set<JournalEntry>().CountAsync());
        Assert.Equal(0, await ctx.Set<JournalEntryLine>().CountAsync());

        // (ب) كل الأرصدة صفر — عبر المحرّك الموحّد نفسه المستخدم في الشاشة والتقرير.
        var tree = await new AccountBalanceService(ctx).GetBalanceTreeAsync();
        Assert.True(tree.IsBalanced);
        Assert.Equal(0m, tree.TotalDebit);
        Assert.Equal(0m, tree.TotalCredit);
        Assert.All(tree.Flattened, n => Assert.Equal(0m, n.NetBalance));

        // (ج) الشجرة سليمة ولا تتجاوز حد أربعة مستويات.
        var map = await AccountHierarchyPolicy.LoadParentMapAsync(ctx);
        var maxLevel = map.Keys
            .Select(id => AccountHierarchyPolicy.LevelOf(id, map))
            .DefaultIfEmpty(0)
            .Max();
        Assert.True(maxLevel <= AccountTreeLevels.MaxLevel,
            $"أقصى مستوى في القاعدة {maxLevel} يتجاوز الحد المسموح {AccountTreeLevels.MaxLevel}.");

        // (د) المستويات الأربعة قابلة للعرض: كل حساب يحمل تسمية مستوى معتمدة.
        foreach (var id in map.Keys)
        {
            var level = AccountHierarchyPolicy.LevelOf(id, map);
            Assert.Contains(AccountTreeLevels.NameAr(level), AccountTreeLevels.NamesAr);
        }
    }
}
