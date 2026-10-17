using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Migration;
using MarocLIS.Domain.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// « Reprise de données »: the journal of the runs, the source keys, the antériorités and the open
// balances taken over from the previous application. Their own schema, so the take-over's tables
// can be told apart — and dropped one day — without reading every module's.

public sealed class MigrationBatchConfiguration : IEntityTypeConfiguration<MigrationBatch>
{
    public void Configure(EntityTypeBuilder<MigrationBatch> builder)
    {
        builder.ToTable("MigrationBatches", "mig");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SourceSystem).HasMaxLength(MigrationBatch.SourceSystemMaxLength).IsRequired();
        builder.Property(e => e.SourceLabel).HasMaxLength(MigrationBatch.SourceLabelMaxLength).IsRequired();
        builder.Property(e => e.UserName).HasMaxLength(100);
        builder.HasIndex(e => e.StartedAt).HasDatabaseName("IX_MigrationBatches_Started");
    }
}

public sealed class MigrationRecordConfiguration : IEntityTypeConfiguration<MigrationRecord>
{
    public void Configure(EntityTypeBuilder<MigrationRecord> builder)
    {
        builder.ToTable("MigrationRecords", "mig");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SourceKey).HasMaxLength(MigrationRecord.SourceKeyMaxLength).IsRequired();
        builder.Property(e => e.OldNumber).HasMaxLength(MigrationRecord.OldNumberMaxLength);

        // What makes a re-run update rather than duplicate: one record per row of the old application.
        builder.HasIndex(e => new { e.Kind, e.SourceKey }).IsUnique().HasDatabaseName("UX_MigrationRecords_Key");
        builder.HasIndex(e => e.EntityId).HasDatabaseName("IX_MigrationRecords_Entity");
        builder.HasIndex(e => e.LastBatchId).HasDatabaseName("IX_MigrationRecords_Batch");
        builder.HasIndex(e => e.OldNumber).HasFilter("[OldNumber] IS NOT NULL")
               .HasDatabaseName("IX_MigrationRecords_OldNumber");
    }
}

public sealed class HistoricalResultConfiguration : IEntityTypeConfiguration<HistoricalResult>
{
    public void Configure(EntityTypeBuilder<HistoricalResult> builder)
    {
        builder.ToTable("HistoricalResults", "mig");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ValueText).HasMaxLength(HistoricalResult.ValueMaxLength).IsRequired();
        builder.Property(e => e.NumericValue).HasColumnType("decimal(18,6)");
        builder.Property(e => e.ValueOperator).HasMaxLength(2);
        builder.Property(e => e.Unit).HasMaxLength(HistoricalResult.UnitMaxLength);
        builder.Property(e => e.ReferenceRange).HasMaxLength(HistoricalResult.RangeMaxLength);
        builder.Property(e => e.OldAccessionNumber).HasMaxLength(HistoricalResult.AccessionMaxLength);
        builder.Property(e => e.SourceCode).HasMaxLength(HistoricalResult.CodeMaxLength);

        builder.HasOne<TestParameter>().WithMany().HasForeignKey(e => e.TestParameterId)
               .OnDelete(DeleteBehavior.Restrict);

        // The antériorité lookup: this patient's values for these parameters, latest first.
        builder.HasIndex(e => new { e.PatientId, e.TestParameterId, e.ResultDate })
               .HasDatabaseName("IX_HistoricalResults_Patient_Parameter");
        builder.HasIndex(e => e.OldAccessionNumber).HasFilter("[OldAccessionNumber] IS NOT NULL")
               .HasDatabaseName("IX_HistoricalResults_OldAccession");
        builder.HasIndex(e => e.BatchId).HasDatabaseName("IX_HistoricalResults_Batch");
    }
}

public sealed class OpeningBalanceConfiguration : IEntityTypeConfiguration<OpeningBalance>
{
    public void Configure(EntityTypeBuilder<OpeningBalance> builder)
    {
        builder.ToTable("OpeningBalances", "mig");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.OldInvoiceNumber).HasMaxLength(OpeningBalance.ReferenceMaxLength);
        builder.Property(e => e.Label).HasMaxLength(OpeningBalance.LabelMaxLength);

        builder.HasOne<Payer>().WithMany().HasForeignKey(e => e.PayerId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.PatientId).HasFilter("[PatientId] IS NOT NULL").HasDatabaseName("IX_OpeningBalances_Patient");
        builder.HasIndex(e => e.BatchId).HasDatabaseName("IX_OpeningBalances_Batch");
    }
}
