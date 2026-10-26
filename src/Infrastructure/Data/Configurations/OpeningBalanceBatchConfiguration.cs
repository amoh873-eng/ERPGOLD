using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// إعداد Fluent API لكيان دفعة الأرصدة الافتتاحية (OpeningBalanceBatches).
///
/// أهم عنصر هنا هو الفهرس الفريد الجزئي الذي يمنع وجود أكثر من دفعة افتتاحية فعّالة
/// لنفس السنة المالية (النطاق المالي = السنة المالية، فالنظام كيان واحد لكل قاعدة بيانات):
///   - الشرط يستثني دفعات العكس (ReversalOfBatchId != null) — فالعكس لا يُحجب.
///   - ويستثني الدفعات المعكوسة (Status = 3) — فيمكن إنشاء دفعة جديدة بعد عكس القديمة.
///   - ويستثني المحذوفة ناعماً — فحذف المسودة يفسح المجال لإنشاء غيرها.
/// </summary>
public class OpeningBalanceBatchConfiguration : IEntityTypeConfiguration<OpeningBalanceBatch>
{
    public void Configure(EntityTypeBuilder<OpeningBalanceBatch> builder)
    {
        builder.ToTable("OpeningBalanceBatches");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.FiscalYear).IsRequired();
        builder.Property(b => b.EntryDate).IsRequired();
        builder.Property(b => b.Status).IsRequired();
        builder.Property(b => b.Reference).HasMaxLength(100);
        builder.Property(b => b.Description).HasMaxLength(500);
        builder.Property(b => b.PostedBy).HasMaxLength(450);
        builder.Property(b => b.CreatedBy).HasMaxLength(450);
        builder.Property(b => b.UpdatedBy).HasMaxLength(450);

        // القيد المحاسبي الناتج — لا حذف متتالٍ (تدقيق كامل)
        builder.HasOne(b => b.JournalEntry)
            .WithMany()
            .HasForeignKey(b => b.JournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        // فهرس فريد جزئي: دفعة افتتاحية فعّالة واحدة لكل سنة مالية.
        builder.HasIndex(b => b.FiscalYear)
            .IsUnique()
            .HasFilter("\"ReversalOfBatchId\" IS NULL AND \"Status\" <> 3 AND \"IsDeleted\" = false");

        builder.HasIndex(b => b.Status);
        builder.HasIndex(b => b.JournalEntryId);
        builder.HasIndex(b => b.ReversalOfBatchId);

        builder.HasQueryFilter(b => !b.IsDeleted);
    }
}
