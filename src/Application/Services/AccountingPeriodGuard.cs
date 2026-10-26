using System.Collections.Concurrent;
using System.Data;
using ERPSystem.Application.Exceptions;
using ERPSystem.Domain.Entities;
using ERPSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERPSystem.Application.Services;

/// <summary>
/// الحارس المركزي لقفل الفترات المحاسبية.
///
/// يُستدعى من <c>JournalEntryService.PrepareEntryAsync</c> — نقطة الاختناق الوحيدة التي يمر
/// عبرها كل قيد (فواتير/مردودات/مشتريات/سندات/مصاريف/رواتب/شيتات/أرصدة افتتاحية/قيود يدوية)
/// — فيمنع أي ترحيل بتاريخ يقع داخل شهر مُغلق. لا يوجد مسار بديل لإنشاء قيد بدون هذا الفحص.
///
/// القاعدة: **غياب صفّ الفترة = مفتوحة** (توافق خلفي كامل: لا يتغيّر أي سلوك حتى يُغلق مستخدم فترة).
/// </summary>
internal static class AccountingPeriodGuard
{
    /// <summary>قواعد بيانات تأكد لدينا وجود جدول الفترات فيها (كاش إيجابي فقط: الوجود لا يُلغى).</summary>
    private static readonly ConcurrentDictionary<string, bool> KnownExistingTables =
        new(StringComparer.Ordinal);

    /// <summary>يُرمي <see cref="PeriodClosedException"/> إذا كان الشهر مغلقًا.</summary>
    public static async Task EnsurePostingAllowedAsync(DbContext context, DateTime entryDate)
    {
        // إن لم يكن جدول الفترات موجودًا أصلًا (مخطط قديم/مسار ترقية) ⇒ لا فترات مغلقة ⇒ الترحيل مسموح.
        // يُفحص **قبل** الاستعلام على الجدول، لأن أي خطأ داخل معاملة المستدعي يُبطلها في PostgreSQL،
        // والاستعلام على information_schema آمن لا يمسّ الجدول الناقص.
        if (!await PeriodTableExistsAsync(context))
            return;

        var year = entryDate.Year;
        var month = entryDate.Month;

        var closed = await context.Set<AccountingPeriod>()
            .AsNoTracking()
            .AnyAsync(p => p.FiscalYear == year
                           && p.Month == month
                           && p.Status == AccountingPeriodStatus.Closed);

        if (closed)
            throw new PeriodClosedException(year, month);
    }

    private static async Task<bool> PeriodTableExistsAsync(DbContext context)
    {
        if (!context.Database.IsRelational())
            return true; // مزوّد في الذاكرة: الكيان موجود في النموذج دائمًا.

        var key = context.Database.GetConnectionString() ?? string.Empty;
        if (KnownExistingTables.ContainsKey(key))
            return true;

        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                "SELECT 1 FROM information_schema.tables WHERE table_name = 'AccountingPeriods' LIMIT 1";

            var exists = await command.ExecuteScalarAsync() is not null;
            if (exists)
                KnownExistingTables.TryAdd(key, true);

            return exists;
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }
}
