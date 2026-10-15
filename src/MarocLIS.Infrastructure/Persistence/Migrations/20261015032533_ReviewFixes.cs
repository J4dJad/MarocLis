using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PrescriberId",
                schema: "sec",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeededPermissions",
                schema: "sec",
                table: "Roles",
                type: "nvarchar(max)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Variables_ContractWorkingDays",
                schema: "hr",
                table: "Payslips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Variables_ManualAbsenceDays",
                schema: "hr",
                table: "Payslips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Variables_MonthWorkingDays",
                schema: "hr",
                table: "Payslips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "RatesSnapshot",
                schema: "hr",
                table: "PayrollPeriods",
                type: "nvarchar(max)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditedAmount",
                schema: "bil",
                table: "Invoices",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            // Credit notes issued before CreditedAmount existed: carry what they reversed onto the
            // invoice they correct, then settle what is no longer owed. A credit note is a reversal
            // settled by its own issue.
            migrationBuilder.Sql("""
                UPDATE i SET CreditedAmount = c.Total
                FROM bil.Invoices i
                JOIN (SELECT CorrectsInvoiceId, SUM(ABS(TotalAmount)) AS Total
                      FROM bil.Invoices
                      WHERE Kind = 1 AND Status <> 0 AND CorrectsInvoiceId IS NOT NULL
                      GROUP BY CorrectsInvoiceId) c ON c.CorrectsInvoiceId = i.Id;

                UPDATE bil.Invoices SET Status = 3
                WHERE Kind <> 1 AND Status NOT IN (0, 4, 9)
                  AND TotalAmount - PaidAmount - CreditedAmount <= 0;

                UPDATE bil.Invoices SET Status = 3 WHERE Kind = 1 AND Status <> 0 AND Status <> 9;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Prescriber",
                schema: "sec",
                table: "Users",
                column: "PrescriberId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Prescribers_PrescriberId",
                schema: "sec",
                table: "Users",
                column: "PrescriberId",
                principalSchema: "ord",
                principalTable: "Prescribers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Prescribers_PrescriberId",
                schema: "sec",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Prescriber",
                schema: "sec",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PrescriberId",
                schema: "sec",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SeededPermissions",
                schema: "sec",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "Variables_ContractWorkingDays",
                schema: "hr",
                table: "Payslips");

            migrationBuilder.DropColumn(
                name: "Variables_ManualAbsenceDays",
                schema: "hr",
                table: "Payslips");

            migrationBuilder.DropColumn(
                name: "Variables_MonthWorkingDays",
                schema: "hr",
                table: "Payslips");

            migrationBuilder.DropColumn(
                name: "RatesSnapshot",
                schema: "hr",
                table: "PayrollPeriods");

            migrationBuilder.DropColumn(
                name: "CreditedAmount",
                schema: "bil",
                table: "Invoices");
        }
    }
}
