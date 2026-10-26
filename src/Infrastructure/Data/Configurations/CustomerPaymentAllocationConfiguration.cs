using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// P0-2 — تكوين جدول تخصيصات سندات قبض العملاء.
///
/// ملاحظات محاسبية/تقنية (مطابقة لنمط PU-04 المرجعي مع تعديلات مقصودة موثَّقة):
///  • **لا يوجد فهرس فريد على (ReceiptVoucherId, SalesInvoiceId)** — يسمح بصفوف موجبة متعددة
///    (تخصيص جزئي متعدد) وبإعادة التخصيص بعد العكس؛ منع التجاوز يُفرض **كمجموع** داخل المعاملة.
///  • **لا فهرس فريد على ReversalOfAllocationId** — يسمح بعكوس جزئية متعددة؛ منع تجاوز الأصل
///    يُفرض داخل المعاملة بعد قفل الصف (كمجموع).
///  • **لا مرشّح استعلام عام (HasQueryFilter)**: لا يختفي أثر مالي بسبب IsDeleted.
///  • **لا حذف متسلسل** (Restrict) — الأثر التدقيقي محفوظ.
///  • قيود CHECK على مستوى القاعدة لحماية الشكل (مبلغ موجب · اتجاه · شكل العكس).
/// </summary>
public class CustomerPaymentAllocationConfiguration : IEntityTypeConfiguration<CustomerPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<CustomerPaymentAllocation> builder)
    {
        builder.ToTable("CustomerPaymentAllocations", t =>
        {
            t.HasCheckConstraint("ck_cpa_amount_positive", "\"Amount\" > 0");
            t.HasCheckConstraint("ck_cpa_direction", "\"Direction\" IN (1, -1)");
            t.HasCheckConstraint(
                "ck_cpa_reversal_shape",
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

        // أهم مسار قراءة: احتساب المتبقي/المخصَّص لكل فاتورة (NetAllocated).
        builder.HasIndex(a => new { a.SalesInvoiceId, a.Direction })
            .HasDatabaseName("IX_CustomerPaymentAllocations_Invoice_Direction");

        // «المتاح للتخصيص» لكل سند قبض.
        builder.HasIndex(a => a.ReceiptVoucherId)
            .HasDatabaseName("IX_CustomerPaymentAllocations_Voucher");

        // تقارير تاريخية/مراجعة لكل عميل.
        builder.HasIndex(a => new { a.CustomerId, a.AllocatedAt })
            .HasDatabaseName("IX_CustomerPaymentAllocations_Customer_Date");

        builder.HasIndex(a => a.ReversalOfAllocationId)
            .HasDatabaseName("IX_CustomerPaymentAllocations_ReversalOf");

        // منع تكرار «نية» الطلب عند إعادة المحاولة (NULL مسموح — توافق خلفي).
        builder.HasIndex(a => a.IdempotencyKey)
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName("UX_CustomerPaymentAllocations_IdempotencyKey");

        builder.HasOne(a => a.Customer)
            .WithMany()
            .HasForeignKey(a => a.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.ReceiptVoucher)
            .WithMany(v => v.Allocations)
            .HasForeignKey(a => a.ReceiptVoucherId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.SalesInvoice)
            .WithMany()
            .HasForeignKey(a => a.SalesInvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.ReversalOfAllocation)
            .WithMany()
            .HasForeignKey(a => a.ReversalOfAllocationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
