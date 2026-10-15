using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AchatsStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeadVolumePercent",
                schema: "inv",
                table: "StockItems",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadVolumeTestsPerOpening",
                schema: "inv",
                table: "StockItems",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReorderPointComputed",
                schema: "inv",
                table: "StockItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SafetyStockDays",
                schema: "inv",
                table: "StockItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TestsPerCalibration",
                schema: "inv",
                table: "StockItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TestsPerResultCsv",
                schema: "inv",
                table: "StockItems",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TypicalShelfLifeDays",
                schema: "inv",
                table: "StockItems",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryCounts",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CountDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    StorageLocation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Category = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageUnits",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    MinimumCelsius = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    MaximumCelsius = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ReadingsPerDay = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCountLines",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventoryCountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReagentLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    LotNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StorageLocation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExpectedQuantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CountedQuantity = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CountedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CountedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AdjustmentMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCountLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCountLines_InventoryCounts_InventoryCountId",
                        column: x => x.InventoryCountId,
                        principalSchema: "inv",
                        principalTable: "InventoryCounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TemperatureReadings",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    Celsius = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    PeriodMinimumCelsius = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PeriodMaximumCelsius = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsOutOfRange = table.Column<bool>(type: "bit", nullable: false),
                    NonConformityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemperatureReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemperatureReadings_StorageUnits_StorageUnitId",
                        column: x => x.StorageUnitId,
                        principalSchema: "inv",
                        principalTable: "StorageUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_InventoryCountLines_Count_Lot",
                schema: "inv",
                table: "InventoryCountLines",
                columns: new[] { "InventoryCountId", "ReagentLotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCounts_Site_Status",
                schema: "inv",
                table: "InventoryCounts",
                columns: new[] { "SiteId", "Status", "CountDate" });

            migrationBuilder.CreateIndex(
                name: "UX_InventoryCounts_Number",
                schema: "inv",
                table: "InventoryCounts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_StorageUnits_Site_Name",
                schema: "inv",
                table: "StorageUnits",
                columns: new[] { "SiteId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemperatureReadings_Unit_ReadAt",
                schema: "inv",
                table: "TemperatureReadings",
                columns: new[] { "StorageUnitId", "ReadAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryCountLines",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "TemperatureReadings",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "InventoryCounts",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "StorageUnits",
                schema: "inv");

            migrationBuilder.DropColumn(
                name: "DeadVolumePercent",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "DeadVolumeTestsPerOpening",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "ReorderPointComputed",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "SafetyStockDays",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "TestsPerCalibration",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "TestsPerResultCsv",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "TypicalShelfLifeDays",
                schema: "inv",
                table: "StockItems");
        }
    }
}
