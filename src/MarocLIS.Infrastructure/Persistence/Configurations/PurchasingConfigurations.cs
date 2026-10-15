using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// ---- Requests for quotation --------------------------------------------------------------------

public sealed class QuoteRequestConfiguration : IEntityTypeConfiguration<QuoteRequest>
{
    public void Configure(EntityTypeBuilder<QuoteRequest> builder)
    {
        builder.ToTable("QuoteRequests", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Number).AsCode(40);
        builder.HasIndex(e => e.Number).IsUnique().HasDatabaseName("UX_QuoteRequests_Number");
        builder.Property(e => e.Subject).HasMaxLength(200);
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Property(e => e.CancellationReason).HasMaxLength(500);
        builder.Ignore(e => e.IsOpen);

        // A request is always worked on whole — the grid compares every offer on every line — and
        // it is small, so its parts come with it wherever it is read.
        builder.HasMany(e => e.Items).WithOne()
               .HasForeignKey(i => i.QuoteRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Suppliers).WithOne()
               .HasForeignKey(s => s.QuoteRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Offers).WithOne()
               .HasForeignKey(o => o.QuoteRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Items).AutoInclude();
        builder.Navigation(e => e.Suppliers).AutoInclude();
        builder.Navigation(e => e.Offers).AutoInclude();

        builder.HasIndex(e => new { e.SiteId, e.Status, e.RequestedOn })
               .HasDatabaseName("IX_QuoteRequests_Site_Status");
    }
}

public sealed class QuoteRequestItemConfiguration : IEntityTypeConfiguration<QuoteRequestItem>
{
    public void Configure(EntityTypeBuilder<QuoteRequestItem> builder)
    {
        builder.ToTable("QuoteRequestItems", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Description).HasMaxLength(300);
        builder.Property(e => e.Quantity).HasColumnType("decimal(18,3)");
        builder.HasIndex(e => new { e.QuoteRequestId, e.StockItemId })
               .IsUnique()
               .HasDatabaseName("UX_QuoteRequestItems_Request_Item");
    }
}

public sealed class QuoteRequestSupplierConfiguration : IEntityTypeConfiguration<QuoteRequestSupplier>
{
    public void Configure(EntityTypeBuilder<QuoteRequestSupplier> builder)
    {
        builder.ToTable("QuoteRequestSuppliers", "inv");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.QuoteRequestId, e.SupplierId })
               .IsUnique()
               .HasDatabaseName("UX_QuoteRequestSuppliers_Request_Supplier");
    }
}

public sealed class SupplierQuoteConfiguration : IEntityTypeConfiguration<SupplierQuote>
{
    public void Configure(EntityTypeBuilder<SupplierQuote> builder)
    {
        builder.ToTable("SupplierQuotes", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SupplierReference).HasMaxLength(60);
        builder.Property(e => e.Conditions).HasMaxLength(1000);

        builder.HasMany(e => e.Lines).WithOne()
               .HasForeignKey(l => l.SupplierQuoteId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Lines).AutoInclude();

        builder.HasIndex(e => new { e.QuoteRequestId, e.SupplierId })
               .IsUnique()
               .HasDatabaseName("UX_SupplierQuotes_Request_Supplier");
    }
}

public sealed class SupplierQuoteLineConfiguration : IEntityTypeConfiguration<SupplierQuoteLine>
{
    public void Configure(EntityTypeBuilder<SupplierQuoteLine> builder)
    {
        builder.ToTable("SupplierQuoteLines", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.UnitPriceMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.DiscountRate).HasColumnType("decimal(6,4)");
        builder.Property(e => e.VatRate).HasColumnType("decimal(6,4)");
        builder.Ignore(e => e.NetUnitPriceMad);

        // No foreign key to the request item: an item and the quote lines pricing it belong to the
        // same aggregate, and two cascade paths from the request would be refused by SQL Server.
        builder.HasIndex(e => new { e.SupplierQuoteId, e.QuoteRequestItemId })
               .IsUnique()
               .HasDatabaseName("UX_SupplierQuoteLines_Quote_Item");
    }
}

public sealed class PurchasingDocumentConfiguration : IEntityTypeConfiguration<PurchasingDocument>
{
    public void Configure(EntityTypeBuilder<PurchasingDocument> builder)
    {
        builder.ToTable("PurchasingDocuments", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.FileName).HasMaxLength(260).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Content).HasColumnType("varbinary(max)").IsRequired();
        builder.HasIndex(e => new { e.OwnerKind, e.OwnerId, e.AttachedAt })
               .HasDatabaseName("IX_PurchasingDocuments_Owner");
    }
}

// ---- Instrument contracts ----------------------------------------------------------------------

public sealed class InstrumentContractConfiguration : IEntityTypeConfiguration<InstrumentContract>
{
    public void Configure(EntityTypeBuilder<InstrumentContract> builder)
    {
        builder.ToTable("InstrumentContracts", "eqp");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Reference).HasMaxLength(60).IsRequired();
        builder.HasIndex(e => new { e.SupplierId, e.Reference })
               .IsUnique()
               .HasDatabaseName("UX_InstrumentContracts_Supplier_Reference");

        // A number of tests or an amount in dirhams, according to the basis.
        builder.Property(e => e.CommittedVolume).HasColumnType("decimal(18,2)");
        builder.Property(e => e.PenaltyAmountMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.CountedAnalysisCodesCsv).HasMaxLength(1000);
        builder.Property(e => e.MaintenanceTerms).HasMaxLength(2000);
        builder.Property(e => e.TrainingTerms).HasMaxLength(1000);
        builder.Property(e => e.ExitConditions).HasMaxLength(2000);
        builder.Property(e => e.PenaltyTerms).HasMaxLength(2000);
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.Ignore(e => e.EndDate);
        builder.Ignore(e => e.CountedAnalysisCodes);

        builder.HasMany(e => e.Instruments).WithOne()
               .HasForeignKey(i => i.InstrumentContractId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Reagents).WithOne()
               .HasForeignKey(r => r.InstrumentContractId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Instruments).AutoInclude();
        builder.Navigation(e => e.Reagents).AutoInclude();

        builder.HasIndex(e => new { e.SiteId, e.IsActive }).HasDatabaseName("IX_InstrumentContracts_Site_Active");
    }
}

public sealed class InstrumentContractInstrumentConfiguration : IEntityTypeConfiguration<InstrumentContractInstrument>
{
    public void Configure(EntityTypeBuilder<InstrumentContractInstrument> builder)
    {
        builder.ToTable("InstrumentContractInstruments", "eqp");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.InstrumentContractId, e.InstrumentId })
               .IsUnique()
               .HasDatabaseName("UX_InstrumentContractInstruments_Contract_Instrument");
        builder.HasIndex(e => e.InstrumentId).HasDatabaseName("IX_InstrumentContractInstruments_Instrument");
    }
}

public sealed class InstrumentContractReagentConfiguration : IEntityTypeConfiguration<InstrumentContractReagent>
{
    public void Configure(EntityTypeBuilder<InstrumentContractReagent> builder)
    {
        builder.ToTable("InstrumentContractReagents", "eqp");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ContractPriceMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.CostPerTestMad).HasColumnType("decimal(18,2)");
        builder.HasIndex(e => new { e.InstrumentContractId, e.StockItemId })
               .IsUnique()
               .HasDatabaseName("UX_InstrumentContractReagents_Contract_Item");
        // "Is this article captive?" is asked on every quotation and purchase-order line.
        builder.HasIndex(e => e.StockItemId).HasDatabaseName("IX_InstrumentContractReagents_Item");
    }
}
