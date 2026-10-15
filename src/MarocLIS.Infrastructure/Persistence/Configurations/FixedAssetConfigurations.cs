using MarocLIS.Domain.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

public sealed class FixedAssetConfiguration : IEntityTypeConfiguration<FixedAsset>
{
    public void Configure(EntityTypeBuilder<FixedAsset> builder)
    {
        builder.ToTable("FixedAssets", "fin");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Code).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Label).HasMaxLength(200).IsRequired();
        builder.Property(e => e.InvoiceReference).HasMaxLength(60);
        builder.Property(e => e.DisposalReason).HasMaxLength(500);
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.Property(e => e.CostExcludingVatMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.VatAmountMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.ResidualValueMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.DisposalPriceMad).HasColumnType("decimal(18,2)");

        builder.Ignore(e => e.IsDisposed);
        builder.Ignore(e => e.GrossValueMad);
        builder.Ignore(e => e.Terms);

        // Like the other modules, the supplier, invoice and instrument are referenced by id without a
        // foreign key: each is its own aggregate, soft-deleted or site-filtered on its own terms.
        builder.HasIndex(e => new { e.SiteId, e.Code })
               .IsUnique()
               .HasDatabaseName("UX_FixedAssets_Site_Code");

        // An instrument is capitalised once.
        builder.HasIndex(e => e.InstrumentId)
               .IsUnique()
               .HasFilter("[InstrumentId] IS NOT NULL")
               .HasDatabaseName("UX_FixedAssets_Instrument");
    }
}
