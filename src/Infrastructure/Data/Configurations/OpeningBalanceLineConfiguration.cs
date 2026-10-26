using ERPSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERPSystem.Infrastructure.Data.Configurations;

/// <summary>إعداد Fluent API لكيان بند الأرصدة الافتتاحية (OpeningBalanceLines).</summary>
public class OpeningBalanceLineConfiguration : IEntityTypeConfiguration<OpeningBalanceLine>
{
    public void Configure(EntityTypeBuilder<OpeningBalanceLine> builder)
    {
        builder.ToTable("OpeningBalanceLines");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.DebitAmount).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(l => l.CreditAmount).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(l => l.Note).HasMaxLength(500);
        builder.Property(l => l.CreatedBy).HasMaxLength(450);
        builder.Property(l => l.UpdatedBy).HasMaxLength(450);

        builder.HasOne(l => l.Batch)
            .WithMany(b => b.Lines)
            .HasForeignKey(l => l.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        // الحساب المتحرك المستهدف — لا حذف متتالٍ (تدقيق كامل)
        builder.HasOne(l => l.Account)
            .WithMany()
            .HasForeignKey(l => l.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // ── تفصيل الرصيد الافتتاحي (2026-10-25): طرف الذمم / الصنف المخزني ──
        // كلها اختيارية على مستوى العمود (توافق خلفي: البنود القائمة تبقى صحيحة)، لكن
        // الخدمة تُلزم بها على الحسابات الرقابية 1200/2200/1300 عند الإدخال.
        builder.Property(l => l.Quantity).HasColumnType("decimal(18,4)");
        builder.Property(l => l.UnitCost).HasColumnType("decimal(18,4)");

        builder.HasOne(l => l.Customer).WithMany()
            .HasForeignKey(l => l.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Supplier).WithMany()
            .HasForeignKey(l => l.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Item).WithMany()
            .HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Warehouse).WithMany()
            .HasForeignKey(l => l.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.StockMovement).WithMany()
            .HasForeignKey(l => l.StockMovementId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.BatchId);
        builder.HasIndex(l => l.AccountId);
        builder.HasIndex(l => l.CustomerId);
        builder.HasIndex(l => l.SupplierId);
        builder.HasIndex(l => l.ItemId);
        builder.HasIndex(l => l.StockMovementId);

        builder.HasQueryFilter(l => !l.IsDeleted);
    }
}
