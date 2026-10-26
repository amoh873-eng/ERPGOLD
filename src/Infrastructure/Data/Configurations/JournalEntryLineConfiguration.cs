using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for the JournalEntryLine entity (بنود القيد المحاسبي).
/// </summary>
public class JournalEntryLineConfiguration : IEntityTypeConfiguration<JournalEntryLine>
{
    public void Configure(EntityTypeBuilder<JournalEntryLine> builder)
    {
        builder.ToTable("JournalEntryLines");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.DebitAmount).HasColumnType("decimal(18,2)").HasDefaultValue(0);
        builder.Property(l => l.CreditAmount).HasColumnType("decimal(18,2)").HasDefaultValue(0);
        builder.Property(l => l.Note).HasMaxLength(500);

        builder.HasOne(l => l.Account)
            .WithMany(a => a.JournalEntryLines)
            .HasForeignKey(l => l.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // إسناد الطرف على البند (2026-10-25): اختياري، ولا حذف متتالٍ (تدقيق كامل).
        builder.HasOne(l => l.Customer).WithMany()
            .HasForeignKey(l => l.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Supplier).WithMany()
            .HasForeignKey(l => l.SupplierId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.AccountId);
        builder.HasIndex(l => l.JournalEntryId);
        builder.HasIndex(l => l.CustomerId);
        builder.HasIndex(l => l.SupplierId);

        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.CreatedBy).HasMaxLength(100);
        builder.Property(l => l.UpdatedBy).HasMaxLength(100);

        builder.HasQueryFilter(l => !l.IsDeleted);
    }
}
