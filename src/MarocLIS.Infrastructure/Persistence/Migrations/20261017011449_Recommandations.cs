using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Recommandations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                schema: "inv",
                table: "PurchaseOrders",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BillOfExchangeReceipts",
                schema: "bil",
                table: "CashSessions",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MobileReceipts",
                schema: "bil",
                table: "CashSessions",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            // Reprise : le motif d'annulation des bons déjà annulés était écrit dans Notes.
            migrationBuilder.Sql("UPDATE inv.PurchaseOrders SET CancellationReason = LEFT(Notes, 1000) WHERE Status = 9 AND CancellationReason IS NULL AND Notes IS NOT NULL;");

            // Reprise des encaissements par effet (4) et paiement mobile (5) des sessions de caisse existantes,
            // calculés comme BillingService les cumule (paiements hors acompte + acomptes, moins remboursements et impayés pendant la session).
            migrationBuilder.Sql(@"UPDATE s SET
    BillOfExchangeReceipts = ROUND(t.Boe, 2),
    MobileReceipts = ROUND(t.Mobile, 2)
FROM bil.CashSessions s
CROSS APPLY (
    SELECT
        Boe    = SUM(CASE WHEN m.Method = 4 THEN m.Total ELSE 0 END),
        Mobile = SUM(CASE WHEN m.Method = 5 THEN m.Total ELSE 0 END)
    FROM (VALUES (4), (5)) AS v(Method)
    CROSS APPLY (
        SELECT Method = v.Method, Total =
              ISNULL((SELECT SUM(p.Amount) FROM bil.Payments p
                      WHERE p.CashSessionId = s.Id AND p.Method = v.Method AND p.DepositId IS NULL), 0)
            + ISNULL((SELECT SUM(d.Amount) FROM bil.Deposits d
                      WHERE d.CashSessionId = s.Id AND d.Method = v.Method), 0)
            - ISNULL((SELECT SUM(p.Amount) FROM bil.Payments p
                      WHERE p.CashSessionId = s.Id AND p.Method = v.Method
                        AND ((p.Status = 3 AND (s.ClosedAt IS NULL OR p.RefundedAt <= s.ClosedAt))
                          OR (p.Status = 2 AND (s.ClosedAt IS NULL OR p.BouncedAt <= s.ClosedAt)))), 0)
            - ISNULL((SELECT SUM(d.RefundedAmount) FROM bil.Deposits d
                      WHERE d.CashSessionId = s.Id AND d.Method = v.Method AND d.RefundedAt IS NOT NULL
                        AND (s.ClosedAt IS NULL OR d.RefundedAt <= s.ClosedAt)), 0)
    ) m
) t
WHERE t.Boe <> 0 OR t.Mobile <> 0;");

            // Rôles créés par le laboratoire : ceux qui réceptionnaient et expédiaient gardent la sous-traitance,
            // désormais sous son propre droit (les rôles livrés le reçoivent au démarrage).
            migrationBuilder.Sql(@"INSERT sec.RolePermissions (RoleId, Permission)
SELECT r.Id, 'samples.subcontract' FROM sec.Roles r
WHERE r.IsBuiltIn = 0
  AND EXISTS (SELECT 1 FROM sec.RolePermissions p WHERE p.RoleId = r.Id AND p.Permission = 'samples.receive')
  AND EXISTS (SELECT 1 FROM sec.RolePermissions p WHERE p.RoleId = r.Id AND p.Permission = 'samples.dispatch')
  AND NOT EXISTS (SELECT 1 FROM sec.RolePermissions p WHERE p.RoleId = r.Id AND p.Permission = 'samples.subcontract');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationReason",
                schema: "inv",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "BillOfExchangeReceipts",
                schema: "bil",
                table: "CashSessions");

            migrationBuilder.DropColumn(
                name: "MobileReceipts",
                schema: "bil",
                table: "CashSessions");
        }
    }
}
