using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for the SalesInvoiceLine entity (بنود فواتير المبيعات).
/// </summary>
public class SalesInvoiceLineConfiguration : IEntityTypeConfiguration<SalesInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLine> builder)
    {
        builder.ToTable("SalesInvoiceLines");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Quantity).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(l => l.UnitPrice).HasColumnType("decimal(18,2)").IsRequired();
        // UnitCost هنا = لقطة تكلفة البضاعة لحظة البيع (متوسط التكلفة الداخلي الموزون من Item.CostPrice)
        // ⇒ أربع خانات (18,4) مثل Item.CostPrice، وإلا اختُصرت اللقطة قبل حساب COGS.
        // (أما UnitPrice فهو سعر البيع النقدي → يُبقى بقرشين.)
        builder.Property(l => l.UnitCost).HasColumnType("decimal(18,4)").IsRequired();
        builder.Property(l => l.LineTotal).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(l => l.TaxRate).HasColumnType("decimal(5,2)").HasDefaultValue(0);
        builder.Property(l => l.TaxAmount).HasColumnType("decimal(18,2)").HasDefaultValue(0);

        builder.HasOne(l => l.Item)
            .WithMany()
            .HasForeignKey(l => l.ItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.SalesInvoiceId);
        builder.HasIndex(l => l.ItemId);

        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.CreatedBy).HasMaxLength(100);
        builder.Property(l => l.UpdatedBy).HasMaxLength(100);

        builder.HasQueryFilter(l => !l.IsDeleted);
    }
}
