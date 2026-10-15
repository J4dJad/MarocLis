using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinalSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Variables_LeaveIndemnity",
                schema: "hr",
                table: "Payslips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Variables_NoticeIndemnity",
                schema: "hr",
                table: "Payslips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Variables_SeverancePay",
                schema: "hr",
                table: "Payslips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "FinalSettlements",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExitDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExitReason = table.Column<int>(type: "int", nullable: false),
                    ReferenceSalary = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 2, nullable: false),
                    LeaveDays = table.Column<decimal>(type: "decimal(18,6)", precision: 9, scale: 2, nullable: false),
                    LeaveIndemnity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 2, nullable: false),
                    NoticeNotWorked = table.Column<bool>(type: "bit", nullable: false),
                    NoticeIndemnity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 2, nullable: false),
                    SeverancePay = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SignedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinalSettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinalSettlements_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_FinalSettlements_Employee_Exit",
                schema: "hr",
                table: "FinalSettlements",
                columns: new[] { "EmployeeId", "ExitDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinalSettlements",
                schema: "hr");

            migrationBuilder.DropColumn(
                name: "Variables_LeaveIndemnity",
                schema: "hr",
                table: "Payslips");

            migrationBuilder.DropColumn(
                name: "Variables_NoticeIndemnity",
                schema: "hr",
                table: "Payslips");

            migrationBuilder.DropColumn(
                name: "Variables_SeverancePay",
                schema: "hr",
                table: "Payslips");
        }
    }
}
