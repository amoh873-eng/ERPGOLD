using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// PU-04 — تكوين جدول تخصيصات سندات دفع الموردين.
///
/// ملاحظات محاسبية/تقنية:
///  • لا يوجد فهرس فريد على (سند، فاتورة) لأن D13-A المعتمد يسمح بصفوف موجبة متعددة.
///  • لا يوجد فهرس فريد على ReversalOfAllocationId لأن D13-B يسمح بعكوس جزئية متعددة؛
///    منع تجاوز الأصل يُفرض داخل المعاملة بعد قفل الصف (كمجموع، وليس كقيد صف واحد).
///  • لا مرشّح استعلام عام (HasQueryFilter) حتى لا يختفي أثر مالي بسبب IsDeleted.
///  • لا حذف متسلسل: الأثر التدقيقي محفوظ.
/// </summary>
public class PaymentAllocationConfiguration : IEntityTypeConfiguration<PaymentAllocation>
{
    public void Configure(EntityTypeBuilder<PaymentAllocation> builder)
    {
        builder.ToTable("PaymentAllocations", t =>
        {
            t.HasCheckConstraint("ck_alloc_amount_positive", "\"Amount\" > 0");
            t.HasCheckConstraint("ck_alloc_direction", "\"Direction\" IN (1, -1)");
            t.HasCheckConstraint(
                "ck_alloc_reversal_shape",
                "((\"Direction\" = -1) = (\"ReversalOfAllocationId\" IS NOT NULL))");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Amount).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(a => a.Direction).HasColumnType("smallint").IsRequired();
        builder.Property(a => a.AllocatedAt).IsRequired();
        builder.Property(a => a.Note).HasMaxLength(500);

        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.CreatedBy).HasMaxLength(100);
        builder.Property(a => a.UpdatedBy).HasMaxLength(100);

        // حساب الرصيد المتبقي لكل فاتورة (N_i) — أهم مسار قراءة.
        builder.HasIndex(a => new { a.PurchaseInvoiceId, a.Direction })
            .HasDatabaseName("IX_PaymentAllocations_Invoice_Direction");

        builder.HasIndex(a => a.PaymentVoucherId)
            .HasDatabaseName("IX_PaymentAllocations_Voucher");

        builder.HasIndex(a => new { a.SupplierId, a.AllocatedAt })
            .HasDatabaseName("IX_PaymentAllocations_Supplier_Date");

        builder.HasIndex(a => a.ReversalOfAllocationId)
            .HasDatabaseName("IX_PaymentAllocations_ReversalOf");

        builder.HasOne(a => a.PaymentVoucher)
            .WithMany(v => v.Allocations)
            .HasForeignKey(a => a.PaymentVoucherId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.PurchaseInvoice)
            .WithMany()
            .HasForeignKey(a => a.PurchaseInvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Supplier)
            .WithMany()
            .HasForeignKey(a => a.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.ReversalOfAllocation)
            .WithMany()
            .HasForeignKey(a => a.ReversalOfAllocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.ReversalDocument)
            .WithMany(r => r.Allocations)
            .HasForeignKey(a => a.ReversalDocumentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
