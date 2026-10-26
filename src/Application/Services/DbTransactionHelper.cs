using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERPSystem.Application.Services;

/// <summary>
/// أداة تنفيذ معاملة داخل استراتيجية إعادة المحاولة (ExecutionStrategy) الخاصة بـ EF Core.
///
/// عند تفعيل EnableRetryOnFailure على DbContext، يرفض EF Core المعاملات اليدوية
/// (BeginTransaction) إلا إذا نُفّذت عبر CreateExecutionStrategy().ExecuteAsync(...).
/// هذه الأداة تغلّف المعاملة القياسية (begin → action → commit / rollback) داخل
/// الاستراتيجية، فتسمح ببقاء منطق الخدمات كما هو مع إبقاء مرونة إعادة المحاولة.
///
/// ⚠️ ملاحظة محاسبية (Phase A/2): معاملات كتابة المستندات ليست idempotent على نفس السياق
/// (تُضاف كيانات وتُحدَّث أرصدة)، وإعادة المحاولة التلقائية تُنتج قيوداً/أرقاماً مكررة أو أرصدة
/// مُضاعفة (مثبّت باختبار تكامل). لذلك تُستخدم هنا استراتيجية بلا إعادة محاولة تلقائية،
/// ويُعالَج الخطأ العابر بإعادة الطلب بسياق/معاملة جديدة.
/// </summary>
public static class DbTransactionHelper
{
    /// <summary>
    /// الكتابة غير idempotent (كيانات + أرصدة): إن أُعيد تنفيذ المندوب تلقائياً بعد فشل عابر (Retry)
    /// فسنجد كيانات محاولة سابقة غير محفوظة (قيود/حركات) ⇒ نفشل بوضوح بدل إنتاج قيود مكررة أو كتابة جزئية.
    /// </summary>
    public static void EnsureNotASilentRetry(DbContext context)
    {
        var leftover = context.ChangeTracker.Entries().Any(e => e.State == EntityState.Added
            && (e.Entity is ERPSystem.Domain.Entities.JournalEntry
                || e.Entity is ERPSystem.Domain.Entities.StockMovement));

        if (leftover)
            throw new InvalidOperationException(
                "فشل عابر أثناء حفظ المستند — لم تُحفظ أي بيانات جزئية. أعد تنفيذ العملية.");
    }

    /// <summary>تنفيذ معاملة (بلا قيمة إرجاع) داخل استراتيجية التنفيذ (مع حماية من إعادة المحاولة الصامتة).</summary>
    public static async Task ExecuteAsync(DbContext context, Func<Task> action)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            EnsureNotASilentRetry(context);
            await using var tx = await context.Database.BeginTransactionAsync();
            try
            {
                await action();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });
    }

    /// <summary>تنفيذ معاملة وإرجاع نتيجة داخل استراتيجية التنفيذ (مع حماية من إعادة المحاولة الصامتة).</summary>
    public static async Task<T> ExecuteAsync<T>(DbContext context, Func<Task<T>> action)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            EnsureNotASilentRetry(context);
            await using var tx = await context.Database.BeginTransactionAsync();
            try
            {
                var result = await action();
                await tx.CommitAsync();
                return result;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });
    }
}
