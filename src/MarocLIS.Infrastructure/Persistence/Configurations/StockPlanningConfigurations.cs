using MarocLIS.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// Stock and consumption (purchasing lot B): the consumption policy of an item, the periodic
// inventories and the temperature monitoring. Kept apart from OperationsConfigurations like the
// other purchasing lots; EF applies every configuration class of an entity.

public sealed class StockItemConsumptionConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.Property(e => e.DeadVolumePercent).HasColumnType("decimal(5,2)");
        builder.Property(e => e.DeadVolumeTestsPerOpening).HasColumnType("decimal(18,3)");
        builder.Property(e => e.TestsPerResultCsv).HasMaxLength(500);
        builder.Ignore(e => e.ConsumptionPolicy);
    }
}

public sealed class InventoryCountConfiguration : IEntityTypeConfiguration<InventoryCount>
{
    public void Configure(EntityTypeBuilder<InventoryCount> builder)
    {
        builder.ToTable("InventoryCounts", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Number).AsCode(40);
        builder.HasIndex(e => e.Number).IsUnique().HasDatabaseName("UX_InventoryCounts_Number");
        builder.Property(e => e.StorageLocation).HasMaxLength(100);
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Ignore(e => e.IsClosed);
        builder.Ignore(e => e.AdjustmentReason);

        // A count is always worked on whole: the sheet, the variances, the closing.
        builder.HasMany(e => e.Lines).WithOne()
               .HasForeignKey(l => l.InventoryCountId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Lines).AutoInclude();

        builder.HasIndex(e => new { e.SiteId, e.Status, e.CountDate })
               .HasDatabaseName("IX_InventoryCounts_Site_Status");
    }
}

public sealed class InventoryCountLineConfiguration : IEntityTypeConfiguration<InventoryCountLine>
{
    public void Configure(EntityTypeBuilder<InventoryCountLine> builder)
    {
        builder.ToTable("InventoryCountLines", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ItemCode).AsCode(40);
        builder.Property(e => e.LotNumber).AsCode(60);
        builder.Property(e => e.StorageLocation).HasMaxLength(100);
        builder.Property(e => e.Comment).HasMaxLength(500);
        builder.Property(e => e.ExpectedQuantity).HasColumnType("decimal(18,3)");
        builder.Property(e => e.CountedQuantity).HasColumnType("decimal(18,3)");
        builder.Ignore(e => e.Variance);

        builder.HasIndex(e => new { e.InventoryCountId, e.ReagentLotId })
               .IsUnique()
               .HasDatabaseName("UX_InventoryCountLines_Count_Lot");
    }
}

public sealed class StorageUnitConfiguration : IEntityTypeConfiguration<StorageUnit>
{
    public void Configure(EntityTypeBuilder<StorageUnit> builder)
    {
        builder.ToTable("StorageUnits", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Location).HasMaxLength(100);
        builder.Property(e => e.SerialNumber).HasMaxLength(60);
        builder.Property(e => e.MinimumCelsius).HasColumnType("decimal(6,2)");
        builder.Property(e => e.MaximumCelsius).HasColumnType("decimal(6,2)");

        builder.HasIndex(e => new { e.SiteId, e.Name })
               .IsUnique()
               .HasDatabaseName("UX_StorageUnits_Site_Name");
    }
}

public sealed class TemperatureReadingConfiguration : IEntityTypeConfiguration<TemperatureReading>
{
    public void Configure(EntityTypeBuilder<TemperatureReading> builder)
    {
        builder.ToTable("TemperatureReadings", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Celsius).HasColumnType("decimal(6,2)");
        builder.Property(e => e.PeriodMinimumCelsius).HasColumnType("decimal(6,2)");
        builder.Property(e => e.PeriodMaximumCelsius).HasColumnType("decimal(6,2)");
        builder.Property(e => e.Comment).HasMaxLength(500);

        builder.HasOne<StorageUnit>().WithMany().HasForeignKey(e => e.StorageUnitId)
               .OnDelete(DeleteBehavior.Restrict);

        // The monthly sheet and the "no reading today" alert read a unit's readings by date.
        builder.HasIndex(e => new { e.StorageUnitId, e.ReadAt })
               .HasDatabaseName("IX_TemperatureReadings_Unit_ReadAt");
    }
}
