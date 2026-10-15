using MarocLIS.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// ---- Supplier invoices and payments ------------------------------------------------------------

public sealed class SupplierInvoiceConfiguration : IEntityTypeConfiguration<SupplierInvoice>
{
    public void Configure(EntityTypeBuilder<SupplierInvoice> builder)
    {
        builder.ToTable("SupplierInvoices", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.InvoiceNumber).HasMaxLength(60).IsRequired();
        builder.Property(e => e.UnblockJustification).HasMaxLength(1000);
        builder.Property(e => e.CancellationReason).HasMaxLength(500);
        builder.Property(e => e.ScanFileName).HasMaxLength(200);
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.Ignore(e => e.TotalExcludingVat);
        builder.Ignore(e => e.TotalVat);
        builder.Ignore(e => e.TotalIncludingVat);
        builder.Ignore(e => e.AmountPaid);
        builder.Ignore(e => e.AmountOutstanding);
        builder.Ignore(e => e.IsOpen);

        builder.HasMany(e => e.Lines).WithOne()
               .HasForeignKey(l => l.SupplierInvoiceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Variances).WithOne()
               .HasForeignKey(v => v.SupplierInvoiceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Payments).WithOne()
               .HasForeignKey(p => p.SupplierInvoiceId).OnDelete(DeleteBehavior.Cascade);

        // One invoice number per supplier. A cancelled entry does not count, so an invoice keyed in
        // error can be entered again correctly under its own number.
        builder.HasIndex(e => new { e.SupplierId, e.InvoiceNumber })
               .IsUnique()
               .HasFilter("[Status] <> 9")
               .HasDatabaseName("UX_SupplierInvoices_Supplier_Number");

        // The due-date dashboard and the late-payment declaration.
        builder.HasIndex(e => new { e.SiteId, e.Status, e.DueDate })
               .HasDatabaseName("IX_SupplierInvoices_Site_Status_Due");
    }
}

public sealed class SupplierInvoiceLineConfiguration : IEntityTypeConfiguration<SupplierInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SupplierInvoiceLine> builder)
    {
        builder.ToTable("SupplierInvoiceLines", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Description).HasMaxLength(300);
        builder.Property(e => e.Quantity).HasColumnType("decimal(18,3)");
        builder.Property(e => e.UnitPriceMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.VatRate).HasColumnType("decimal(6,4)");

        builder.Ignore(e => e.LineTotalExcludingVat);
        builder.Ignore(e => e.VatAmount);

        builder.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(e => e.PurchaseOrderLineId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.SupplierInvoiceId, e.LineNumber })
               .IsUnique()
               .HasDatabaseName("UX_SupplierInvoiceLines_Invoice_Line");

        // "Already invoiced on this order line", summed at every match.
        builder.HasIndex(e => e.PurchaseOrderLineId)
               .HasDatabaseName("IX_SupplierInvoiceLines_OrderLine");
    }
}

public sealed class SupplierInvoiceVarianceConfiguration : IEntityTypeConfiguration<SupplierInvoiceVariance>
{
    public void Configure(EntityTypeBuilder<SupplierInvoiceVariance> builder)
    {
        builder.ToTable("SupplierInvoiceVariances", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Expected).HasColumnType("decimal(18,4)");
        builder.Property(e => e.Actual).HasColumnType("decimal(18,4)");
        builder.HasIndex(e => e.SupplierInvoiceId).HasDatabaseName("IX_SupplierInvoiceVariances_Invoice");
    }
}

public sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> builder)
    {
        builder.ToTable("SupplierPayments", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AmountMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.Reference).HasMaxLength(60);

        builder.HasIndex(e => new { e.SupplierInvoiceId, e.PaidOn })
               .HasDatabaseName("IX_SupplierPayments_Invoice_Date");
        // The bank journal of the accounting export.
        builder.HasIndex(e => e.PaidOn).HasDatabaseName("IX_SupplierPayments_Date");
    }
}

public sealed class SupplierInvoiceFileConfiguration : IEntityTypeConfiguration<SupplierInvoiceFile>
{
    public void Configure(EntityTypeBuilder<SupplierInvoiceFile> builder)
    {
        builder.ToTable("SupplierInvoiceFiles", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.FileName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Content).HasColumnType("varbinary(max)").IsRequired();
        builder.HasIndex(e => e.SupplierInvoiceId).IsUnique().HasDatabaseName("UX_SupplierInvoiceFiles_Invoice");
    }
}
