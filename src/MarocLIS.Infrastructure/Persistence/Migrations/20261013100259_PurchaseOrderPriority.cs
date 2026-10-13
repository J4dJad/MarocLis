using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseOrderPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPriority",
                schema: "inv",
                table: "PurchaseOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RaisedAutomatically",
                schema: "inv",
                table: "PurchaseOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPriority",
                schema: "inv",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "RaisedAutomatically",
                schema: "inv",
                table: "PurchaseOrders");
        }
    }
}
