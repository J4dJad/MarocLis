using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// The purchasing-quality columns (reception checks, GS1, regulatory registration, QC-gated
// release) are configured here rather than in OperationsConfigurations: EF applies every
// configuration class of an entity, and keeping them apart keeps that file's history readable.

public sealed class SupplierRegulatoryConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.Property(e => e.RegulatoryRegistrationNumber).HasMaxLength(60);
    }
}

public sealed class StockItemGs1Configuration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.Property(e => e.Gtin).HasMaxLength(14).IsUnicode(false);

        // A scan at reception looks the article up by GTIN.
        builder.HasIndex(e => new { e.SiteId, e.Gtin })
               .HasFilter("[Gtin] IS NOT NULL")
               .HasDatabaseName("IX_StockItems_Site_Gtin");
    }
}

public sealed class ReagentLotReceptionConfiguration : IEntityTypeConfiguration<ReagentLot>
{
    public void Configure(EntityTypeBuilder<ReagentLot> builder)
    {
        builder.Property(e => e.QcOverrideJustification).HasMaxLength(1000);

        // The supplier evaluation counts deliveries per order.
        builder.HasIndex(e => e.PurchaseOrderId)
               .HasFilter("[PurchaseOrderId] IS NOT NULL")
               .HasDatabaseName("IX_ReagentLots_PurchaseOrder");
    }
}

public sealed class NonConformitySupplierConfiguration : IEntityTypeConfiguration<NonConformity>
{
    public void Configure(EntityTypeBuilder<NonConformity> builder)
    {
        builder.HasIndex(e => e.SupplierId)
               .HasFilter("[SupplierId] IS NOT NULL")
               .HasDatabaseName("IX_NonConformities_Supplier");
    }
}

public sealed class RefusedDeliveryConfiguration : IEntityTypeConfiguration<RefusedDelivery>
{
    public void Configure(EntityTypeBuilder<RefusedDelivery> builder)
    {
        builder.ToTable("RefusedDeliveries", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.DeliveryNoteReference).HasMaxLength(60);
        builder.Property(e => e.LotNumber).HasMaxLength(60);

        builder.HasIndex(e => new { e.SupplierId, e.RefusedOn })
               .HasDatabaseName("IX_RefusedDeliveries_Supplier_Date");
    }
}

public sealed class LotCertificateFileConfiguration : IEntityTypeConfiguration<LotCertificateFile>
{
    public void Configure(EntityTypeBuilder<LotCertificateFile> builder)
    {
        builder.ToTable("LotCertificateFiles", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.FileName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Content).HasColumnType("varbinary(max)").IsRequired();
        builder.HasIndex(e => e.ReagentLotId).HasDatabaseName("IX_LotCertificateFiles_Lot");
        // The certificates of a lot go with it.
        builder.HasOne<ReagentLot>().WithMany().HasForeignKey(e => e.ReagentLotId).OnDelete(DeleteBehavior.Cascade);
    }
}
