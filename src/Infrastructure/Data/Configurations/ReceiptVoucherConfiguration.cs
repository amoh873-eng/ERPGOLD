using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for ReceiptVoucher (سندات القبض) — كيان جديد لا يمس أي جدول قائم.
/// </summary>
public class ReceiptVoucherConfiguration : IEntityTypeConfiguration<ReceiptVoucher>
{
    public void Configure(EntityTypeBuilder<ReceiptVoucher> builder)
    {
        builder.ToTable("ReceiptVouchers");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.VoucherNumber)
            .IsRequired()
            .HasMaxLength(30);

        // رقم السند فريد بين غير المحذوفة
        builder.HasIndex(v => v.VoucherNumber)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        // حماية قاعدة البيانات ضد الترحيل المزدوج: لا يمكن ربط قيدين بنفس السند
        builder.HasIndex(v => v.JournalEntryId)
            .IsUnique()
            .HasFilter("\"JournalEntryId\" IS NOT NULL AND \"IsDeleted\" = false");

        builder.Property(v => v.Amount).HasPrecision(18, 2);
        builder.Property(v => v.Description).HasMaxLength(500);
        builder.Property(v => v.ExternalReference).HasMaxLength(100);
        builder.Property(v => v.AttachmentPath).HasMaxLength(500);
        builder.Property(v => v.ApprovedBy).HasMaxLength(100);
        builder.Property(v => v.PostedBy).HasMaxLength(100);
        builder.Property(v => v.CancelledBy).HasMaxLength(100);
        builder.Property(v => v.CancelReason).HasMaxLength(300);

        // منع الترحيل المتزامن (نمط RowVersion المستخدم في Account)
        builder.Property(v => v.RowVersion).IsRowVersion();

        builder.HasOne(v => v.Customer)
            .WithMany()
            .HasForeignKey(v => v.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.CashOrBankAccount)
            .WithMany()
            .HasForeignKey(v => v.CashOrBankAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.ReceivableAccount)
            .WithMany()
            .HasForeignKey(v => v.ReceivableAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.JournalEntry)
            .WithMany()
            .HasForeignKey(v => v.JournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(v => v.CreatedAt).IsRequired();
        builder.Property(v => v.CreatedBy).HasMaxLength(100);
        builder.Property(v => v.UpdatedBy).HasMaxLength(100);

        builder.HasQueryFilter(v => !v.IsDeleted);
    }
}
