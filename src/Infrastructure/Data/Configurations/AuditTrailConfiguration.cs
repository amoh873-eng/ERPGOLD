using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for AuditTrail (سجل التدقيق) — D3-A.
/// ملحق فقط، ولا يُخزّن أي أسرار أو كلمات مرور.
/// </summary>
public class AuditTrailConfiguration : IEntityTypeConfiguration<AuditTrail>
{
    public void Configure(EntityTypeBuilder<AuditTrail> builder)
    {
        builder.ToTable("AuditTrails");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.EntityName).IsRequired().HasMaxLength(100);
        builder.Property(a => a.DocumentNumber).HasMaxLength(30);
        builder.Property(a => a.Action).IsRequired().HasMaxLength(30);
        builder.Property(a => a.Details).HasMaxLength(500);
        builder.Property(a => a.PerformedBy).HasMaxLength(100);

        // استعلام سريع: سجل مستند واحد مرتّبًا زمنيًا
        builder.HasIndex(a => new { a.EntityName, a.EntityId, a.PerformedAt });

        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.CreatedBy).HasMaxLength(100);
        builder.Property(a => a.UpdatedBy).HasMaxLength(100);

        builder.HasQueryFilter(a => !a.IsDeleted);
    }
}
