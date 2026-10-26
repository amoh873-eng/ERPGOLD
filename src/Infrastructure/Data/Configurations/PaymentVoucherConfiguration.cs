using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for PaymentVoucher (سندات الصرف) — كيان جديد لا يمس أي جدول قائم.
/// </summary>
public class PaymentVoucherConfiguration : IEntityTypeConfiguration<PaymentVoucher>
{
    public void Configure(EntityTypeBuilder<PaymentVoucher> builder)
    {
        builder.ToTable("PaymentVouchers");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.VoucherNumber)
            .IsRequired()
            .HasMaxLength(30);

        builder.HasIndex(v => v.VoucherNumber)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        // حماية قاعدة البيانات ضد الترحيل المزدوج
        builder.HasIndex(v => v.JournalEntryId)
            .IsUnique()
            .HasFilter("\"JournalEntryId\" IS NOT NULL AND \"IsDeleted\" = false");

        // PU-04 (N-1): منع تكرار إنشاء السند عند إعادة إرسال الطلب نفسه.
        builder.HasIndex(v => v.IdempotencyKey)
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName("UX_PaymentVouchers_IdempotencyKey");

        builder.Property(v => v.Amount).HasPrecision(18, 2);
        builder.Property(v => v.BeneficiaryName).HasMaxLength(200);
        builder.Property(v => v.Description).HasMaxLength(500);
        builder.Property(v => v.ExternalReference).HasMaxLength(100);
        builder.Property(v => v.AttachmentPath).HasMaxLength(500);
        builder.Property(v => v.ApprovedBy).HasMaxLength(100);
        builder.Property(v => v.PostedBy).HasMaxLength(100);
        builder.Property(v => v.CancelledBy).HasMaxLength(100);
        builder.Property(v => v.CancelReason).HasMaxLength(300);

        builder.Property(v => v.RowVersion).IsRowVersion();

        builder.HasOne(v => v.Supplier)
            .WithMany()
            .HasForeignKey(v => v.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        // SUB-2b/P3 — طرف العميل + المردود المرجعي (سند رد أموال العملاء).
        builder.HasOne(v => v.Customer)
            .WithMany()
            .HasForeignKey(v => v.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.SalesReturn)
            .WithMany()
            .HasForeignKey(v => v.SalesReturnId)
            .OnDelete(DeleteBehavior.Restrict);

        // طرف واحد فقط: مورد أو عميل (كل الصفوف القائمة مورد ⇒ القيد صحيح بلا فقدان بيانات).
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_pv_party_xor",
            "((\"SupplierId\" IS NULL) AND (\"CustomerId\" IS NOT NULL)) OR ((\"SupplierId\" IS NOT NULL) AND (\"CustomerId\" IS NULL))"));

        // لا سند عميل بلا مردود مرجعي (سقف الرد ومصدره).
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_pv_customer_requires_return",
            "(\"CustomerId\" IS NULL) OR (\"SalesReturnId\" IS NOT NULL)"));

        // استعلامات كشف العميل وحساب السقف (فهارس جزئية — لا تُثقل مسار المورد).
        builder.HasIndex(v => new { v.CustomerId, v.VoucherDate })
            .HasFilter("\"CustomerId\" IS NOT NULL")
            .HasDatabaseName("IX_PaymentVouchers_Customer_Date");

        builder.HasIndex(v => v.SalesReturnId)
            .HasFilter("\"CustomerId\" IS NOT NULL")
            .HasDatabaseName("IX_PaymentVouchers_SalesReturnId");

        builder.HasOne(v => v.CashOrBankAccount)
            .WithMany()
            .HasForeignKey(v => v.CashOrBankAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.PayableAccount)
            .WithMany()
            .HasForeignKey(v => v.PayableAccountId)
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
