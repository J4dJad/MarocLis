using MarocLIS.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

public sealed class SupplierInvoiceExportConfiguration : IEntityTypeConfiguration<SupplierInvoiceExport>
{
    public void Configure(EntityTypeBuilder<SupplierInvoiceExport> builder)
    {
        builder.ToTable("SupplierInvoiceExports", "inv");
        builder.HasKey(e => e.Id);

        // An invoice is recorded once, at its first export.
        builder.HasIndex(e => e.SupplierInvoiceId)
               .IsUnique()
               .HasDatabaseName("UX_SupplierInvoiceExports_Invoice");

        builder.HasOne<SupplierInvoice>().WithMany().HasForeignKey(e => e.SupplierInvoiceId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
