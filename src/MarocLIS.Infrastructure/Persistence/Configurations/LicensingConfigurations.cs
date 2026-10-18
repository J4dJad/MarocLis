using MarocLIS.Domain.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// The licence and the workstations counted under it. In cfg with the settings: they belong to the
// installation, not to a site.

public sealed class LicenceRecordConfiguration : IEntityTypeConfiguration<LicenceRecord>
{
    public void Configure(EntityTypeBuilder<LicenceRecord> builder)
    {
        builder.ToTable("Licences", "cfg");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Number).HasMaxLength(LicenceRecord.NumberMaxLength).IsRequired();

        // The signed file whole: its modules list and texts can outgrow any fixed length.
        builder.Property(e => e.Content).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(e => e.ImportedByUserName).HasMaxLength(100);
        builder.Property(e => e.Workstation).HasMaxLength(Workstation.MachineNameMaxLength);

        builder.HasIndex(e => new { e.Number, e.Version }).HasDatabaseName("IX_Licences_Number_Version");
        builder.HasIndex(e => e.ImportedAt).HasDatabaseName("IX_Licences_Imported");
    }
}

public sealed class WorkstationConfiguration : IEntityTypeConfiguration<Workstation>
{
    public void Configure(EntityTypeBuilder<Workstation> builder)
    {
        builder.ToTable("Workstations", "cfg");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.MachineName).HasMaxLength(Workstation.MachineNameMaxLength).IsRequired();
        builder.Property(e => e.LastUserName).HasMaxLength(100);
        builder.Property(e => e.AppVersion).HasMaxLength(40);

        // One row per computer, kept from one sign-in to the next.
        builder.HasIndex(e => e.MachineName).IsUnique().HasDatabaseName("UX_Workstations_Machine");
    }
}
