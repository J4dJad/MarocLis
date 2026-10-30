using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemisesConventions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                schema: "ord",
                table: "LabOrders",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "DiscountApprovedByUserId",
                schema: "ord",
                table: "LabOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DiscountProfileId",
                schema: "ord",
                table: "LabOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscountSource",
                schema: "ord",
                table: "LabOrders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PartnershipId",
                schema: "ord",
                table: "LabOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DiscountApprovedByUserId",
                schema: "bil",
                table: "Invoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DiscountProfiles",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PartnershipPrices",
                schema: "rel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartnershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisTestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FixedPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    DiscountPercent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnershipPrices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LabOrders_Discount",
                schema: "ord",
                table: "LabOrders",
                columns: new[] { "SiteId", "DiscountSource", "RegisteredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_DiscountProfiles_Name",
                schema: "bil",
                table: "DiscountProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartnershipPrices_Partnership",
                schema: "rel",
                table: "PartnershipPrices",
                columns: new[] { "PartnershipId", "AnalysisTestId", "ValidFrom" });

            // The orders already registered with a rate typed at the counter: their discount was the
            // counter's, and its amount is found again from what was charged (net = gross × (1 − rate)).
            migrationBuilder.Sql(@"
UPDATE o SET
    o.DiscountSource = 4,
    o.DiscountAmount = CASE WHEN o.DiscountPercent >= 100 THEN 0
                            ELSE ROUND(ISNULL(i.Charged, 0) * o.DiscountPercent / (100 - o.DiscountPercent), 2) END
FROM ord.LabOrders o
OUTER APPLY (SELECT SUM(x.ChargedPrice) AS Charged FROM ord.LabOrderItems x WHERE x.LabOrderId = o.Id) i
WHERE o.DiscountPercent > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscountProfiles",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "PartnershipPrices",
                schema: "rel");

            migrationBuilder.DropIndex(
                name: "IX_LabOrders_Discount",
                schema: "ord",
                table: "LabOrders");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                schema: "ord",
                table: "LabOrders");

            migrationBuilder.DropColumn(
                name: "DiscountApprovedByUserId",
                schema: "ord",
                table: "LabOrders");

            migrationBuilder.DropColumn(
                name: "DiscountProfileId",
                schema: "ord",
                table: "LabOrders");

            migrationBuilder.DropColumn(
                name: "DiscountSource",
                schema: "ord",
                table: "LabOrders");

            migrationBuilder.DropColumn(
                name: "PartnershipId",
                schema: "ord",
                table: "LabOrders");

            migrationBuilder.DropColumn(
                name: "DiscountApprovedByUserId",
                schema: "bil",
                table: "Invoices");
        }
    }
}
