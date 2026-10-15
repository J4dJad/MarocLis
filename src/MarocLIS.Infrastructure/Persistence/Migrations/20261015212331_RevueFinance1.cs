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

            migrationBuilder.AddColumn<DateOnly>(
                name: "TerminatedOn",
                schema: "eqp",
                table: "InstrumentContracts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupplierInvoiceLinkedAt",
                schema: "fin",
                table: "FixedAssets",
                type: "datetimeoffset(3)",
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
                    ChargeAccount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DepreciationAccount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
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
                name: "FixedAssetTransfers",
                schema: "fin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FixedAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    FromCategory = table.Column<int>(type: "int", nullable: false),
                    ToCategory = table.Column<int>(type: "int", nullable: false),
                    FromAssetAccount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FromDepreciationAccount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FromChargeAccount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ToAssetAccount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ToDepreciationAccount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    GrossMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DepreciationMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FixedAssetTransfers_FixedAssets_FixedAssetId",
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

            migrationBuilder.CreateTable(
                name: "SupplierInvoiceExports",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoiceExports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierInvoiceExports_SupplierInvoices_SupplierInvoiceId",
                        column: x => x.SupplierInvoiceId,
                        principalSchema: "inv",
                        principalTable: "SupplierInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "IX_FixedAssetTransfers_Asset",
                schema: "fin",
                table: "FixedAssetTransfers",
                column: "FixedAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PayerShortfallTransfers_Invoice",
                schema: "bil",
                table: "PayerShortfallTransfers",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierInvoiceExports_Invoice",
                schema: "inv",
                table: "SupplierInvoiceExports",
                column: "SupplierInvoiceId",
                unique: true);

            // Refusals already left to the patient before this version: one transfer each, dated on the payer's answer
            // (audit trail), so the next export does not book them again.
            migrationBuilder.Sql(@"INSERT INTO bil.PayerShortfallTransfers (Id, InvoiceId, Amount, TransferredOn, ReversedOn)
SELECT NEWID(), i.Id, t.Amount,
       COALESCE(a.AnsweredOn, s.LastSettled, w.LastWritten, CAST(COALESCE(i.ModifiedAt, i.CreatedAt) AS date)), NULL
FROM bil.Invoices i
OUTER APPLY (SELECT SUM(p.Amount) AS Amount, MAX(p.PaymentDate) AS LastWritten FROM bil.Payments p
             WHERE p.InvoiceId = i.Id AND p.Method = 9 AND p.Status = 1) w
OUTER APPLY (SELECT MAX(p.PaymentDate) AS LastSettled FROM bil.Payments p
             WHERE p.InvoiceId = i.Id AND p.Method = 6 AND p.Status = 1) s
OUTER APPLY (SELECT TOP (1) e.OccurredAt AS AnsweredAt, CAST(e.OccurredAt AS date) AS AnsweredOn
             FROM aud.AuditEntries e
             WHERE e.EntityName = N'Invoice' AND e.EntityId = i.Id
               AND e.ChangedProperties LIKE N'%PayerAcceptedAmount%'
               AND CASE WHEN ISJSON(e.Changes) = 1
                        THEN JSON_VALUE(e.Changes, '$.PayerAcceptedAmount.new') END IS NOT NULL
             ORDER BY e.OccurredAt DESC) a
OUTER APPLY (SELECT SUM(c.TotalAmount) AS Amount FROM bil.Invoices c
             WHERE c.CorrectsInvoiceId = i.Id AND c.Kind = 1 AND c.Status NOT IN (0, 9)
               AND (a.AnsweredAt IS NULL OR COALESCE(c.IssuedAt, c.CreatedAt) < a.AnsweredAt)) c
CROSS APPLY (SELECT ISNULL(c.Amount, 0) - (i.TotalAmount - i.PayerShareAmount) AS OnPayer) k
CROSS APPLY (SELECT i.PayerShareAmount
                    - CASE WHEN k.OnPayer <= 0 THEN 0
                           WHEN k.OnPayer >= i.PayerShareAmount THEN i.PayerShareAmount
                           ELSE k.OnPayer END
                    - i.PayerAcceptedAmount - ISNULL(w.Amount, 0) AS Amount) t
WHERE i.Kind = 0 AND i.ClaimStatus <> 0 AND i.PayerAcceptedAmount IS NOT NULL
  AND t.Amount > 0;");

            // Till withdrawals recorded before this version: one row per shift total, keyed on the shift as the
            // old movement was, so earlier bank matches still hold.
            migrationBuilder.Sql(@"INSERT INTO [bil].[CashWithdrawals] ([Id], [CashSessionId], [Amount], [At], [Reason])
SELECT s.[Id], s.[Id], s.[CashWithdrawals], s.[OpenedAt], NULL
FROM [bil].[CashSessions] s
WHERE s.[CashWithdrawals] > 0
  AND NOT EXISTS (SELECT 1 FROM [bil].[CashWithdrawals] w WHERE w.[CashSessionId] = s.[Id]);");

            // Contracts deactivated before this version: their last change is taken as the termination date.
            migrationBuilder.Sql(@"UPDATE eqp.InstrumentContracts SET TerminatedOn = CAST(COALESCE(ModifiedAt, CreatedAt) AS date)
WHERE IsActive = 0 AND TerminatedOn IS NULL;");
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
                name: "FixedAssetTransfers",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "PayerShortfallTransfers",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "SupplierInvoiceExports",
                schema: "inv");

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
                name: "TerminatedOn",
                schema: "eqp",
                table: "InstrumentContracts");

            migrationBuilder.DropColumn(
                name: "SupplierInvoiceLinkedAt",
                schema: "fin",
                table: "FixedAssets");

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
