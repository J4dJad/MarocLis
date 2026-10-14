using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DepositsAndPayerAcceptedAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DepositId",
                schema: "bil",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PayerAcceptedAmount",
                schema: "bil",
                table: "Invoices",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Deposits",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ReceivedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    MaturityDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppliedInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppliedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RefundedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    RefundedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RefundReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deposits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deposits_CashSession",
                schema: "bil",
                table: "Deposits",
                column: "CashSessionId",
                filter: "[CashSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Deposits_Order_Status",
                schema: "bil",
                table: "Deposits",
                columns: new[] { "LabOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Deposits_Number",
                schema: "bil",
                table: "Deposits",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Deposits",
                schema: "bil");

            migrationBuilder.DropColumn(
                name: "DepositId",
                schema: "bil",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PayerAcceptedAmount",
                schema: "bil",
                table: "Invoices");
        }
    }
}
