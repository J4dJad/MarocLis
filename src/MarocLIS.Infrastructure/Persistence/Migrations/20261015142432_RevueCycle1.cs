using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RevueCycle1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PaymentCount",
                schema: "inv",
                table: "SupplierInvoices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "EvaluationInstrumentId",
                schema: "inv",
                table: "ReagentLots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastOpenedOn",
                schema: "inv",
                table: "ReagentLots",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLotEvaluation",
                schema: "qc",
                table: "QcResults",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DirectBillingAtRegistration",
                schema: "ord",
                table: "LabOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "StockAtCount",
                schema: "inv",
                table: "InventoryCountLines",
                type: "decimal(18,6)",
                nullable: true);

            // Orders already registered: record whether the payer was billed directly at the counter.
            migrationBuilder.Sql(@"
UPDATE o SET o.DirectBillingAtRegistration =
  CASE WHEN c.DirectBillingEnabled = 1 AND p.ThirdPartyPaymentAgreement = 1
        AND (p.AgreementValidUntil IS NULL OR p.AgreementValidUntil >= CAST(o.RegisteredAt AS date))
       THEN 1 ELSE 0 END
FROM ord.LabOrders o
JOIN pat.PatientCoverages c ON c.Id = o.PatientCoverageId
JOIN pat.Payers p ON p.Id = o.PayerId
WHERE o.PayerId IS NOT NULL;");

            // On-board stability runs from the last kit opened: take the last issue of each lot.
            migrationBuilder.Sql(@"
UPDATE l SET LastOpenedOn = m.LastIssue
FROM inv.ReagentLots l
JOIN (SELECT ReagentLotId, MAX(CAST(OccurredAt AS date)) AS LastIssue
      FROM inv.StockMovements WHERE Kind = 1 AND ReagentLotId IS NOT NULL
      GROUP BY ReagentLotId) m ON m.ReagentLotId = l.Id;");

            migrationBuilder.Sql(@"
UPDATE i SET PaymentCount = (SELECT COUNT(*) FROM inv.SupplierPayments p WHERE p.SupplierInvoiceId = i.Id)
FROM inv.SupplierInvoices i;");

            // Receipts made before the purchasing upgrade have no delivery date on their order line.
            migrationBuilder.Sql(@"
UPDATE pl SET LastReceivedOn = x.ReceivedOn
FROM inv.PurchaseOrderLines pl
CROSS APPLY (SELECT MAX(l.ReceivedOn) AS ReceivedOn FROM inv.ReagentLots l
             WHERE l.PurchaseOrderId = pl.PurchaseOrderId AND l.StockItemId = pl.StockItemId) x
WHERE pl.LastReceivedOn IS NULL AND x.ReceivedOn IS NOT NULL;");

            // Counts still being counted during the upgrade: their lines already counted were measured
            // against the quantity frozen at the start, which takes the issues made since off the lot a
            // second time at closing. Their reference becomes the stock when the shelf was counted: the
            // start quantity plus the lot's movements between the start and the count.
            migrationBuilder.Sql(@"
UPDATE cl SET StockAtCount = cl.ExpectedQuantity + ISNULL(x.Net, 0)
FROM inv.InventoryCountLines cl
JOIN inv.InventoryCounts c ON c.Id = cl.InventoryCountId
OUTER APPLY (SELECT SUM(CASE WHEN m.Kind IN (0, 3, 7, 9) THEN m.Quantity ELSE -m.Quantity END) AS Net
             FROM inv.StockMovements m
             WHERE m.ReagentLotId = cl.ReagentLotId AND m.OccurredAt > c.StartedAt
               AND m.OccurredAt <= cl.CountedAt) x
WHERE c.Status = 1 AND c.StartedAt IS NOT NULL
  AND cl.CountedQuantity IS NOT NULL AND cl.CountedAt IS NOT NULL AND cl.StockAtCount IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentCount",
                schema: "inv",
                table: "SupplierInvoices");

            migrationBuilder.DropColumn(
                name: "EvaluationInstrumentId",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "LastOpenedOn",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "IsLotEvaluation",
                schema: "qc",
                table: "QcResults");

            migrationBuilder.DropColumn(
                name: "DirectBillingAtRegistration",
                schema: "ord",
                table: "LabOrders");

            migrationBuilder.DropColumn(
                name: "StockAtCount",
                schema: "inv",
                table: "InventoryCountLines");
        }
    }
}
