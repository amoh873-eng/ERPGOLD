using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>Fluent API configuration for the PurchaseInvoice entity (فواتير المشتريات).</summary>
public class PurchaseInvoiceConfiguration : IEntityTypeConfiguration<PurchaseInvoice>
{
    public void Configure(EntityTypeBuilder<PurchaseInvoice> builder)
    {
        builder.ToTable("PurchaseInvoices");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.InvoiceNumber).IsRequired().HasMaxLength(50);
        builder.HasIndex(i => i.InvoiceNumber).IsUnique().HasFilter("\"IsDeleted\" = false");

        builder.HasIndex(i => new { i.SupplierId, i.InvoiceDate });

        // D-01: حماية منع التكرار على مستوى قاعدة البيانات.
        // فهرس فريد جزئي: يسمح بعدة صفوف بـ NULL (توافق خلفي) ويمنع تكرار أي مفتاح غير فارغ.
        builder.Property(i => i.IdempotencyKey);
        builder.HasIndex(i => i.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("IX_PurchaseInvoices_IdempotencyKey")
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");

        builder.Property(i => i.SubTotal).HasColumnType("decimal(18,2)");
        builder.Property(i => i.TotalAmount).HasColumnType("decimal(18,2)");
        builder.Property(i => i.PaidAmount).HasColumnType("decimal(18,2)");
        builder.Property(i => i.TaxAmount).HasColumnType("decimal(18,2)");
        builder.Property(i => i.TaxRate).HasColumnType("decimal(5,2)").HasDefaultValue(0);

        builder.Property(i => i.Note).HasMaxLength(1000);
        // D-TZ (الخيار ب): تاريخ الفاتورة والاستحقاق أيام محاسبية خالصة ⇒ date.
        builder.Property(i => i.InvoiceDate).HasColumnType("date").IsRequired();
        builder.Property(i => i.DueDate).HasColumnType("date");

        builder.HasOne(i => i.Supplier)
            .WithMany(s => s.PurchaseInvoices)
            .HasForeignKey(i => i.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.Warehouse)
            .WithMany()
            .HasForeignKey(i => i.WarehouseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(i => i.Lines)
            .WithOne(l => l.PurchaseInvoice)
            .HasForeignKey(l => l.PurchaseInvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<JournalEntry>()
            .WithMany()
            .HasForeignKey(i => i.JournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(i => i.CreatedAt).IsRequired();
        builder.Property(i => i.CreatedBy).HasMaxLength(100);
        builder.Property(i => i.UpdatedBy).HasMaxLength(100);

        builder.HasQueryFilter(i => !i.IsDeleted);
    }
}