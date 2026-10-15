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

        builder.Property(e => e.Code).HasMaxLength(FixedAsset.CodeMaxLength).IsRequired();
        builder.Property(e => e.Label).HasMaxLength(FixedAsset.LabelMaxLength).IsRequired();
        builder.Property(e => e.InvoiceReference).HasMaxLength(FixedAsset.InvoiceReferenceMaxLength);
        builder.Property(e => e.DisposalReason).HasMaxLength(FixedAsset.DisposalReasonMaxLength);
        builder.Property(e => e.Notes).HasMaxLength(FixedAsset.NotesMaxLength);

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

public sealed class FixedAssetDepreciationPostingConfiguration : IEntityTypeConfiguration<FixedAssetDepreciationPosting>
{
    public void Configure(EntityTypeBuilder<FixedAssetDepreciationPosting> builder)
    {
        builder.ToTable("FixedAssetDepreciationPostings", "fin");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.AmountMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.ChargeAccount).HasMaxLength(FixedAssetDepreciationPosting.AccountMaxLength);
        builder.Property(e => e.DepreciationAccount).HasMaxLength(FixedAssetDepreciationPosting.AccountMaxLength);

        // A month of an asset goes out once; exporting it again gives the same row back.
        builder.HasIndex(e => new { e.FixedAssetId, e.Year, e.Month })
               .IsUnique()
               .HasDatabaseName("UX_FixedAssetDepreciationPostings_Asset_Month");

        builder.HasOne<FixedAsset>().WithMany().HasForeignKey(e => e.FixedAssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FixedAssetTransferConfiguration : IEntityTypeConfiguration<FixedAssetTransfer>
{
    public void Configure(EntityTypeBuilder<FixedAssetTransfer> builder)
    {
        builder.ToTable("FixedAssetTransfers", "fin");
        builder.HasKey(e => e.Id);

        const int account = FixedAssetDepreciationPosting.AccountMaxLength;
        builder.Property(e => e.FromAssetAccount).HasMaxLength(account).IsRequired();
        builder.Property(e => e.FromDepreciationAccount).HasMaxLength(account).IsRequired();
        builder.Property(e => e.FromChargeAccount).HasMaxLength(account).IsRequired();
        builder.Property(e => e.ToAssetAccount).HasMaxLength(account).IsRequired();
        builder.Property(e => e.ToDepreciationAccount).HasMaxLength(account).IsRequired();
        builder.Property(e => e.GrossMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.DepreciationMad).HasColumnType("decimal(18,2)");

        builder.HasIndex(e => e.FixedAssetId).HasDatabaseName("IX_FixedAssetTransfers_Asset");
        builder.HasOne<FixedAsset>().WithMany().HasForeignKey(e => e.FixedAssetId).OnDelete(DeleteBehavior.Cascade);
    }
}
