using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AvoirReportAcompte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CarriedFromInvoiceId",
                schema: "bil",
                table: "Deposits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CarriedFromInvoiceNumber",
                schema: "bil",
                table: "Deposits",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            // An invoice reversed in full by credit notes read « Réglée »: it is « Annulée par
            // avoir » now. What was paid on one before this version stays on it, to refund.
            migrationBuilder.Sql(
                "UPDATE [bil].[Invoices] SET [Status] = 5 "
                + "WHERE [Kind] = 0 AND [Status] = 3 AND [CreditedAmount] > 0 AND [CreditedAmount] >= [TotalAmount];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [bil].[Invoices] SET [Status] = 3 WHERE [Status] = 5;");

            migrationBuilder.DropColumn(
                name: "CarriedFromInvoiceId",
                schema: "bil",
                table: "Deposits");

            migrationBuilder.DropColumn(
                name: "CarriedFromInvoiceNumber",
                schema: "bil",
                table: "Deposits");
        }
    }
}
