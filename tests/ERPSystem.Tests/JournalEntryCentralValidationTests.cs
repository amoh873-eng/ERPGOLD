using System.Reflection;
using ERPSystem.Application.DTOs.Journal;
using ERPSystem.Application.Exceptions;
using ERPSystem.Application.Interfaces;
using ERPSystem.Application.Security;
using ERPSystem.Application.Services;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERPSystem.Tests;

/// <summary>
/// التحقق المركزي في JournalEntryService.PrepareEntryAsync (Requirement 2):
/// Leaf → Allow، Parent → Reject، ولجميع المسارات (اليدوي + التلقائي + API/Application).
/// </summary>
public class JournalEntryCentralValidationTests
{
    /// <summary>Requirement 3: مصدر تصريح السعر اليدوي — تصريح كامل (لا علاقة له بفحوصات هذا الملف).</summary>
    private sealed class AllowAllManualPriceAuthorization : IManualPriceAuthorization
    {
        public bool CanEditManualPrice(string moduleKey) => true;
    }

    private static List<JournalEntryLegInput> BalancedLegs(Guid debit, Guid credit)
        => new()
        {
            new JournalEntryLegInput { AccountId = debit, DebitAmount = 100m },
            new JournalEntryLegInput { AccountId = credit, CreditAmount = 100m }
        };

    [Fact]
    public async Task PrepareEntryAsync_rejects_posting_to_parent_1100_in_commercial()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedCommercialTree(ctx);
        var service = new JournalEntryService(ctx);

        var ex = await Assert.ThrowsAsync<ParentAccountMovementException>(() =>
            service.PrepareEntryAsync(
                JournalEntryType.Manual, DateTime.UtcNow, null, null,
                BalancedLegs(ids["1100"], ids["1101"])));

        Assert.Equal(ParentAccountMovementException.Key, ex.ResourceKey);
        Assert.Equal(
            "لا يمكن تسجيل حركة مالية مباشرة على حساب رئيسي، يرجى اختيار حساب متحرك.",
            ex.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    public async Task PrepareEntryAsync_rejects_posting_to_parent_roots(string code)
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        var ids = AccountingSafetyFixtures.SeedCommercialTree(ctx);
        var service = new JournalEntryService(ctx);

        await Assert.ThrowsAsync<ParentAccountMovementException>(() =>
            service.PrepareEntryAsync(
                JournalEntryType.Manual, DateTime.UtcNow, null, null,
                BalancedLegs(ids[code], ids["1101"])));
    }

    /// <summary>
    /// جميع خدمات القيود التلقائية تحل حسابها الافتراضي عبر PostingAccountResolver،
    /// لذا في Commercial (حيث 1100 رئيسي) لا تُرحّل إلى Parent 1100 بل إلى الورقة 11001.
    /// </summary>
    [Fact]
    public async Task All_automatic_journal_services_resolve_default_1100_to_leaf_in_commercial()
    {
        using var ctx = AccountingSafetyFixtures.NewContext();
        AccountingSafetyFixtures.SeedCommercialTree(ctx);
        var journal = new JournalEntryService(ctx);

        var services = typeof(PostingAccountResolver).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract
                        && t.Namespace == "ERPSystem.Application.Services")
            .Where(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(m => m.Name is "GetAccountByCodeAsync" or "GetAcc" && !m.IsStatic))
            .ToList();

        var tested = new List<string>();

        foreach (var svc in services)
        {
            // ننشئ الخدمة فقط إن كان بناؤها متاحاً (DbContext + IJournalEntryService + مصدر تصريح السعر).
            var ctor = svc.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p =>
                p.ParameterType == typeof(DbContext) ||
                p.ParameterType == typeof(IJournalEntryService) ||
                p.ParameterType == typeof(IManualPriceAuthorization)));
            if (ctor is null) continue;

            // Requirement 3: الخدمات صارت تأخذ مصدر تصريح السعر اليدوي (يُحقَن خلفيًا).
            // هذا الاختبار يخصّ حلّ الحسابات الافتراضية إلى أوراق — لا يخصّ الصلاحيات — لذا نمنح تصريحًا كاملاً.
            var priceAuthorization = new AllowAllManualPriceAuthorization();
            var args = ctor.GetParameters()
                .Select(p => p.ParameterType == typeof(DbContext) ? (object)ctx
                           : p.ParameterType == typeof(IJournalEntryService) ? journal
                           : priceAuthorization)
                .ToArray();
            var instance = ctor.Invoke(args);

            var method = svc.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => !m.IsStatic && m.Name is "GetAccountByCodeAsync" or "GetAcc");
            Assert.NotNull(method);

            var task = (Task<Account>)method!.Invoke(instance, new object?[] { "1100" })!;
            var resolved = await task;

            Assert.Equal("11001", resolved.Code);
            // الورقة المحلولة تمرّ التحقق المركزي → لا رفض.
            await MovementAccountValidator.EnsureMovementAccountsAsync(ctx, new[] { resolved.Id });

            tested.Add(svc.Name);
        }

        // نتأكد أن اختبار الانعكاس غطّى فعلاً جميع خدمات الترحيل التلقائي المعروفة.
        Assert.Contains("SalesInvoiceService", tested);
        Assert.Contains("SalesReturnService", tested);
        Assert.Contains("PurchaseInvoiceService", tested);
        Assert.Contains("PurchaseReturnService", tested);
        Assert.Contains("ExpenseService", tested);
        Assert.Contains("CashDrawerService", tested);
        Assert.Contains("CashierShiftService", tested);
        Assert.Contains("RepCustodyService", tested);
        Assert.Contains("BankCheckService", tested);
        Assert.Contains("StockWriteOffService", tested);
    }
}
