using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TvaActesDossier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsVatExempt",
                schema: "ord",
                table: "LabOrderItems",
                type: "bit",
                nullable: false,
                // Requests registered before were invoiced exempt, whatever their tariff: they keep
                // that, so an invoice issued on them now matches the ones issued before.
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatRate",
                schema: "ord",
                table: "LabOrderItems",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            // A tariff's rate is a fraction (0.20 = 20 %). The editor took it as typed, so « 20 »
            // was stored as 20 and charged 2 000 %: a stored rate above 1 was a percentage.
            migrationBuilder.Sql(
                "UPDATE [cat].[Tariffs] SET [VatRate] = [VatRate] / 100 WHERE [IsVatExempt] = 0 AND [VatRate] > 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsVatExempt",
                schema: "ord",
                table: "LabOrderItems");

            migrationBuilder.DropColumn(
                name: "VatRate",
                schema: "ord",
                table: "LabOrderItems");
        }
    }
}
