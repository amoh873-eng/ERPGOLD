using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// PU-04 (D7-ب) — تكوين مستند عكس سند الصرف.
/// قيود أساسية:
///  • فريد على OriginalPaymentVoucherId ⇒ لا أكثر من مستند عكس واحد لكل سند.
///  • فريد على JournalEntryId ⇒ لا قيدان لنفس مستند العكس.
///  • فريد جزئي على IdempotencyKey ⇒ إعادة إرسال الطلب لا تُنشئ مستندًا ثانيًا.
/// </summary>
public class PaymentVoucherReversalConfiguration : IEntityTypeConfiguration<PaymentVoucherReversal>
{
    public void Configure(EntityTypeBuilder<PaymentVoucherReversal> builder)
    {
        builder.ToTable("PaymentVoucherReversals");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.ReversalNumber).IsRequired().HasMaxLength(30);
        builder.HasIndex(r => r.ReversalNumber)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false")
            .HasDatabaseName("UX_PaymentVoucherReversals_Number");

        builder.HasIndex(r => r.OriginalPaymentVoucherId)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false")
            .HasDatabaseName("UX_PaymentVoucherReversals_Original");

        builder.HasIndex(r => r.JournalEntryId)
            .IsUnique()
            .HasFilter("\"JournalEntryId\" IS NOT NULL AND \"IsDeleted\" = false")
            .HasDatabaseName("UX_PaymentVoucherReversals_JournalEntry");

        builder.HasIndex(r => r.IdempotencyKey)
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName("UX_PaymentVoucherReversals_IdempotencyKey");

        builder.Property(r => r.Amount).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(500);

        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.CreatedBy).HasMaxLength(100);
        builder.Property(r => r.UpdatedBy).HasMaxLength(100);

        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.HasOne(r => r.OriginalPaymentVoucher)
            .WithOne(v => v.Reversal)
            .HasForeignKey<PaymentVoucherReversal>(r => r.OriginalPaymentVoucherId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Supplier)
            .WithMany()
            .HasForeignKey(r => r.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        // SUB-2b/P3 — عكس سند رد عميل: طرف العميل بديل المورد، مع قيد طرف واحد فقط.
        builder.HasOne(r => r.Customer)
            .WithMany()
            .HasForeignKey(r => r.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_pvr_party_xor",
            "((\"SupplierId\" IS NULL) AND (\"CustomerId\" IS NOT NULL)) OR ((\"SupplierId\" IS NOT NULL) AND (\"CustomerId\" IS NULL))"));

        builder.HasOne(r => r.CashOrBankAccount)
            .WithMany()
            .HasForeignKey(r => r.CashOrBankAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.PayableAccount)
            .WithMany()
            .HasForeignKey(r => r.PayableAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.JournalEntry)
            .WithMany()
            .HasForeignKey(r => r.JournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(r => !r.IsDeleted);
    }
}
