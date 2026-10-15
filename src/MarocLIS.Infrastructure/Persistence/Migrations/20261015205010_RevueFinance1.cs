using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RevueFinance1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "MaturityDate",
                schema: "inv",
                table: "SupplierPayments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RefundSiteId",
                schema: "bil",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RefundSiteId",
                schema: "bil",
                table: "Deposits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "bil",
                table: "BankStatementLines",
                type: "datetimeoffset(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedByUserId",
                schema: "bil",
                table: "BankStatementLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                schema: "bil",
                table: "BankStatementLines",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "bil",
                table: "BankStatementLines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CashWithdrawals",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashWithdrawals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashWithdrawals_CashSessions_CashSessionId",
                        column: x => x.CashSessionId,
                        principalSchema: "bil",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FixedAssetDepreciationPostings",
                schema: "fin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FixedAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetDepreciationPostings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FixedAssetDepreciationPostings_FixedAssets_FixedAssetId",
                        column: x => x.FixedAssetId,
                        principalSchema: "fin",
                        principalTable: "FixedAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PayerShortfallTransfers",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TransferredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ReversedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayerShortfallTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayerShortfallTransfers_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "bil",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementLines_Import",
                schema: "bil",
                table: "BankStatementLines",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_CashWithdrawals_Session",
                schema: "bil",
                table: "CashWithdrawals",
                column: "CashSessionId");

            migrationBuilder.CreateIndex(
                name: "UX_FixedAssetDepreciationPostings_Asset_Month",
                schema: "fin",
                table: "FixedAssetDepreciationPostings",
                columns: new[] { "FixedAssetId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayerShortfallTransfers_Invoice",
                schema: "bil",
                table: "PayerShortfallTransfers",
                column: "InvoiceId");

            // Refusals already left to the patient before this version: one transfer each, dated as the
            // export used to date it, so the next export does not book them again.
            migrationBuilder.Sql(@"INSERT INTO bil.PayerShortfallTransfers (Id, InvoiceId, Amount, TransferredOn, ReversedOn)
SELECT NEWID(), i.Id,
       i.PayerShareAmount - i.PayerAcceptedAmount - ISNULL(w.Amount, 0),
       COALESCE(s.LastSettled, w.LastWritten, CAST(COALESCE(i.ModifiedAt, i.CreatedAt) AS date)), NULL
FROM bil.Invoices i
OUTER APPLY (SELECT SUM(p.Amount) AS Amount, MAX(p.PaymentDate) AS LastWritten FROM bil.Payments p
             WHERE p.InvoiceId = i.Id AND p.Method = 9 AND p.Status = 1) w
OUTER APPLY (SELECT MAX(p.PaymentDate) AS LastSettled FROM bil.Payments p
             WHERE p.InvoiceId = i.Id AND p.Method = 6 AND p.Status = 1) s
WHERE i.Kind = 0 AND i.ClaimStatus <> 0 AND i.PayerAcceptedAmount IS NOT NULL
  AND i.PayerShareAmount - i.PayerAcceptedAmount - ISNULL(w.Amount, 0) > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashWithdrawals",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "FixedAssetDepreciationPostings",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "PayerShortfallTransfers",
                schema: "bil");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementLines_Import",
                schema: "bil",
                table: "BankStatementLines");

            migrationBuilder.DropColumn(
                name: "MaturityDate",
                schema: "inv",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "RefundSiteId",
                schema: "bil",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundSiteId",
                schema: "bil",
                table: "Deposits");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "bil",
                table: "BankStatementLines");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                schema: "bil",
                table: "BankStatementLines");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                schema: "bil",
                table: "BankStatementLines");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "bil",
                table: "BankStatementLines");
        }
    }
}
