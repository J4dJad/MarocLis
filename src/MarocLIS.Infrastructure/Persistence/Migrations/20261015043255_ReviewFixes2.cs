using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixes2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AgeUnitDays",
                schema: "cat",
                table: "ReferenceRanges",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PreviousExitDate",
                schema: "hr",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PreviousHireDate",
                schema: "hr",
                table: "Employees",
                type: "date",
                nullable: true);

            // Credit notes: what an invoice was credited, never more than its total, and its status
            // derived from the amounts both ways, so an invoice left Paid after a bounced cheque or a
            // refund reopens. Drafts, cancelled drafts and write-offs keep their status. Rerunnable.
            migrationBuilder.Sql("""
                UPDATE i SET CreditedAmount = CASE WHEN c.Total > i.TotalAmount THEN i.TotalAmount ELSE c.Total END,
                             HasCreditNote = 1
                FROM bil.Invoices i
                JOIN (SELECT CorrectsInvoiceId, SUM(ABS(TotalAmount)) AS Total
                      FROM bil.Invoices
                      WHERE Kind = 1 AND Status NOT IN (0, 9) AND CorrectsInvoiceId IS NOT NULL
                      GROUP BY CorrectsInvoiceId) c ON c.CorrectsInvoiceId = i.Id
                WHERE i.Kind <> 1;

                UPDATE bil.Invoices
                SET Status = CASE WHEN TotalAmount - PaidAmount - CreditedAmount <= 0 THEN 3
                                  WHEN PaidAmount > 0 THEN 2
                                  ELSE 1 END
                WHERE Kind <> 1 AND Status NOT IN (0, 4, 9);

                UPDATE bil.Invoices SET Status = 3 WHERE Kind = 1 AND Status NOT IN (0, 4, 9);
                """);

            // Habilitations ended by a departure now end the day after the last day worked.
            migrationBuilder.Sql("""
                UPDATE h SET h.WithdrawnOn = DATEADD(day, 1, e.ExitDate)
                FROM hr.EmployeeHabilitations h JOIN hr.Employees e ON e.Id = h.EmployeeId
                WHERE e.ExitDate IS NOT NULL AND h.WithdrawalReason = N'Sortie du laboratoire' AND h.WithdrawnOn = e.ExitDate;
                """);

            // Leave paid by a settlement whose exit month is closed but whose receipt was never signed.
            migrationBuilder.Sql("""
                INSERT INTO hr.LeaveLedger (Id, EmployeeId, [Date], Kind, Days, Reference, Comment, CreatedAt)
                SELECT NEWID(), s.EmployeeId, s.ExitDate, 3, -s.LeaveDays, CONCAT('SOLDE-', FORMAT(s.ExitDate, 'yyyyMMdd')),
                       CONCAT(N'Congés payés par l''indemnité compensatrice du solde de tout compte (sortie le ', FORMAT(s.ExitDate, 'dd/MM/yyyy'), N')'),
                       SYSDATETIMEOFFSET()
                FROM hr.FinalSettlements s
                WHERE s.LeaveDays > 0
                  AND EXISTS (SELECT 1 FROM hr.Payslips ps JOIN hr.PayrollPeriods p ON p.Id = ps.PayrollPeriodId
                              WHERE ps.EmployeeId = s.EmployeeId AND p.Status = 1 AND p.Year = YEAR(s.ExitDate) AND p.Month = MONTH(s.ExitDate))
                  AND NOT EXISTS (SELECT 1 FROM hr.LeaveLedger l WHERE l.EmployeeId = s.EmployeeId
                                  AND l.Reference = CONCAT('SOLDE-', FORMAT(s.ExitDate, 'yyyyMMdd')));
                """);

            // An analysis with results already printed on a partial report is marked reported, so it
            // can no longer be cancelled out of later report versions.
            migrationBuilder.Sql("""
                UPDATE i SET i.ReportedAt = COALESCE(o.FirstReportedAt, SYSDATETIMEOFFSET())
                FROM ord.LabOrderItems i
                JOIN ord.LabOrders o ON o.Id = i.LabOrderId
                WHERE i.ReportedAt IS NULL AND i.Status <> 99
                  AND EXISTS (SELECT 1 FROM res.TestResults r WHERE r.LabOrderItemId = i.Id AND r.Status = 4);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgeUnitDays",
                schema: "cat",
                table: "ReferenceRanges");

            migrationBuilder.DropColumn(
                name: "PreviousExitDate",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PreviousHireDate",
                schema: "hr",
                table: "Employees");
        }
    }
}
