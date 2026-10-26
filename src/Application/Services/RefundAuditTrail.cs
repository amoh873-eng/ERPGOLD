using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERPSystem.Application.Services;

/// <summary>
/// SUB-2b/P3 (المرحلة الثانية) — تدقيق **محاولات الرد المرفوضة** (R19).
///
/// لماذا كتابة مستقلة (Raw SQL) ولا يُستخدم <see cref="VoucherAuditTrailWriter"/>؟
///  • المحاولة المرفوضة تحدث غالبًا **داخل** معاملة ترحيل أُلغيت (Rollback) ⇒ أي سطر يُضاف إلى نفس
///    السياق/المعاملة سيُمحى معها ولن يبقى أثر للرفض.
///  • بعد الـRollback لا توجد معاملة نشطة، فنكتب السطر على الاتصال مباشرةً بلا ChangeTracker،
///    ثم نُغلق الاتصال إن كنا فتحناه. أي فشل في التدقيق **لا يُخفي** سبب الرفض الأصلي ولا يُحوَّل لغيره.
///
/// ما يُسجَّل: هوية المنفّذ · التوقيت UTC · مرجع السند/المردود · المبلغ عند توفره · سبب الرفض · النتيجة.
/// ما لا يُسجَّل: كلمات مرور أو أسرار أو محتوى مرفقات أو أي بيانات اعتماد.
/// </summary>
internal static class RefundAuditTrail
{
    /// <summary>اسم الفعل الثابت لمحاولة رد مرفوضة (بدل نص حر).</summary>
    public const string ActionRejected = "RefundRejected";

    private const string EntityName = "PaymentVoucher";
    private const int MaxDetailsLength = 500;

    /// <summary>يكتب سطر تدقيق للرفض بأفضل جهد؛ لا يرمي استثناءً أبدًا.</summary>
    public static async Task TryWriteRejectionAsync(
        DbContext context,
        Guid? voucherId,
        string? voucherNumber,
        Guid? customerId,
        Guid? salesReturnId,
        decimal? amount,
        string reason,
        string? performedBy)
    {
        try
        {
            var details = BuildDetails(reason, customerId, salesReturnId, amount);
            var now = DateTime.UtcNow;
            var user = string.IsNullOrWhiteSpace(performedBy) ? "غير معروف" : performedBy;

            var connection = context.Database.GetDbConnection();
            var wasClosed = connection.State != ConnectionState.Open;
            if (wasClosed) await connection.OpenAsync();
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                cmd.CommandText =
                    "INSERT INTO \"AuditTrails\" " +
                    "(\"Id\",\"EntityName\",\"EntityId\",\"DocumentNumber\",\"Action\",\"Details\",\"PerformedBy\"," +
                    "\"PerformedAt\",\"JournalEntryId\",\"CreatedAt\",\"CreatedBy\",\"UpdatedAt\",\"UpdatedBy\",\"IsDeleted\") " +
                    "VALUES (@id,@entity,@entityId,@doc,@action,@details,@by,@at,NULL,@at,@by,NULL,NULL,false)";

                AddParameter(cmd, "@id", DbType.Guid, Guid.NewGuid());
                AddParameter(cmd, "@entity", DbType.String, EntityName);
                AddParameter(cmd, "@entityId", DbType.Guid, voucherId ?? Guid.Empty);
                AddParameter(cmd, "@doc", DbType.String, (object?)Truncate(voucherNumber, 30) ?? DBNull.Value);
                AddParameter(cmd, "@action", DbType.String, ActionRejected);
                AddParameter(cmd, "@details", DbType.String, details);
                AddParameter(cmd, "@by", DbType.String, Truncate(user, 100));
                AddParameter(cmd, "@at", DbType.DateTime, now);

                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open) await connection.CloseAsync();
            }
        }
        catch
        {
            // التدقيق لا يُفشل العملية: سبب الرفض الأصلي يُعاد رميه من المستدعي كما هو.
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand cmd, string name, DbType type, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string BuildDetails(string reason, Guid? customerId, Guid? salesReturnId, decimal? amount)
    {
        var parts = new List<string>
        {
            "محاولة رد مرفوضة",
            "السبب=" + (string.IsNullOrWhiteSpace(reason) ? "غير محدد" : reason.Trim())
        };

        if (customerId is { } cid && cid != Guid.Empty) parts.Add("العميل=" + cid);
        if (salesReturnId is { } rid && rid != Guid.Empty) parts.Add("المردود=" + rid);
        if (amount is { } value) parts.Add("المبلغ=" + value.ToString("0.00", CultureInfo.InvariantCulture));
        parts.Add("النتيجة=Rejected");

        return Truncate(string.Join(" | ", parts), MaxDetailsLength)!;
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
