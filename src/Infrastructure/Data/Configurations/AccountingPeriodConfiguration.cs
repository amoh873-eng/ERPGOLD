using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// إعداد Fluent API لكيان الفترة المحاسبية (AccountingPeriods).
///
/// الفهرس الفريد (السنة المالية، الشهر) يمنع صفّين لنفس الشهر ⇒ لا ازدواج حالة (مغلقة/مفتوحة)
/// لنفس الفترة. ويُستثنى المحذوف ناعمًا. **غياب الصف = الفترة مفتوحة** (توافق خلفي: لا صفوف مزروعة).
/// </summary>
public class AccountingPeriodConfiguration : IEntityTypeConfiguration<AccountingPeriod>
{
    public void Configure(EntityTypeBuilder<AccountingPeriod> builder)
    {
        builder.ToTable("AccountingPeriods");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.FiscalYear).IsRequired();
        builder.Property(p => p.Month).IsRequired();
        builder.Property(p => p.Status).IsRequired();
        builder.Property(p => p.ClosedBy).HasMaxLength(450);
        builder.Property(p => p.ReopenedBy).HasMaxLength(450);
        builder.Property(p => p.CreatedBy).HasMaxLength(450);
        builder.Property(p => p.UpdatedBy).HasMaxLength(450);
        builder.Property(p => p.Note).HasMaxLength(500);

        builder.HasIndex(p => new { p.FiscalYear, p.Month })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        builder.HasIndex(p => p.Status);

        builder.HasQueryFilter(p => !p.IsDeleted);
    }
}
