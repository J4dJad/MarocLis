using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RevueCycle3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseOrderId",
                schema: "inv",
                table: "StockMovements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EvaluationStartedAt",
                schema: "inv",
                table: "ReagentLots",
                type: "datetimeoffset(3)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LatePaymentDeclarations",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    ExportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ExportedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LatePaymentDeclarations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_PurchaseOrder",
                schema: "inv",
                table: "StockMovements",
                column: "PurchaseOrderId",
                filter: "[PurchaseOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_LatePaymentDeclarations_Site_Period",
                schema: "inv",
                table: "LatePaymentDeclarations",
                columns: new[] { "SiteId", "PeriodFrom", "PeriodTo" },
                unique: true);

            // Receipts already recorded: link each receipt movement to the purchase order of its lot.
            migrationBuilder.Sql(@"
UPDATE m SET m.PurchaseOrderId = l.PurchaseOrderId
FROM inv.StockMovements m JOIN inv.ReagentLots l ON l.Id = m.ReagentLotId
WHERE m.Kind = 0 AND m.PurchaseOrderId IS NULL AND l.PurchaseOrderId IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LatePaymentDeclarations",
                schema: "inv");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_PurchaseOrder",
                schema: "inv",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "PurchaseOrderId",
                schema: "inv",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "EvaluationStartedAt",
                schema: "inv",
                table: "ReagentLots");
        }
    }
}
