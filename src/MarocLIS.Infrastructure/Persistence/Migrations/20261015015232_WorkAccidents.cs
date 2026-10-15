using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkAccidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Variables_WorkAccidentAllowance",
                schema: "hr",
                table: "Payslips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Variables_WorkAccidentDays",
                schema: "hr",
                table: "Payslips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkAccidentId",
                schema: "hr",
                table: "LeaveRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkAccidents",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Place = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Circumstances = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Injuries = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Witnesses = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FirstAidGiven = table.Column<bool>(type: "bit", nullable: false),
                    Exposure = table.Column<int>(type: "int", nullable: true),
                    SourceIdentified = table.Column<bool>(type: "bit", nullable: false),
                    ConsultedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InsurerName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    InsurerClaimNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DeclaredToInsurerOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Subrogation = table.Column<bool>(type: "bit", nullable: false),
                    ReturnedToWorkOn = table.Column<DateOnly>(type: "date", nullable: true),
                    IncapacityRate = table.Column<decimal>(type: "decimal(18,6)", precision: 5, scale: 2, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsClosed = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAccidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkAccidents_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccidentEvents",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkAccidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    On = table.Column<DateOnly>(type: "date", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccidentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccidentEvents_WorkAccidents_WorkAccidentId",
                        column: x => x.WorkAccidentId,
                        principalSchema: "hr",
                        principalTable: "WorkAccidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SerologyChecks",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkAccidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DoneOn = table.Column<DateOnly>(type: "date", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SerologyChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SerologyChecks_WorkAccidents_WorkAccidentId",
                        column: x => x.WorkAccidentId,
                        principalSchema: "hr",
                        principalTable: "WorkAccidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_WorkAccident",
                schema: "hr",
                table: "LeaveRequests",
                column: "WorkAccidentId",
                filter: "[WorkAccidentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccidentEvents_WorkAccidentId",
                schema: "hr",
                table: "AccidentEvents",
                column: "WorkAccidentId");

            migrationBuilder.CreateIndex(
                name: "IX_SerologyChecks_Due",
                schema: "hr",
                table: "SerologyChecks",
                columns: new[] { "DoneOn", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_SerologyChecks_WorkAccidentId",
                schema: "hr",
                table: "SerologyChecks",
                column: "WorkAccidentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAccidents_Employee_Date",
                schema: "hr",
                table: "WorkAccidents",
                columns: new[] { "EmployeeId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAccidents_Open",
                schema: "hr",
                table: "WorkAccidents",
                columns: new[] { "IsClosed", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkAccidents_Number",
                schema: "hr",
                table: "WorkAccidents",
                column: "Number",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LeaveRequests_WorkAccidents_WorkAccidentId",
                schema: "hr",
                table: "LeaveRequests",
                column: "WorkAccidentId",
                principalSchema: "hr",
                principalTable: "WorkAccidents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LeaveRequests_WorkAccidents_WorkAccidentId",
                schema: "hr",
                table: "LeaveRequests");

            migrationBuilder.DropTable(
                name: "AccidentEvents",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "SerologyChecks",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "WorkAccidents",
                schema: "hr");

            migrationBuilder.DropIndex(
                name: "IX_LeaveRequests_WorkAccident",
                schema: "hr",
                table: "LeaveRequests");

            migrationBuilder.DropColumn(
                name: "Variables_WorkAccidentAllowance",
                schema: "hr",
                table: "Payslips");

            migrationBuilder.DropColumn(
                name: "Variables_WorkAccidentDays",
                schema: "hr",
                table: "Payslips");

            migrationBuilder.DropColumn(
                name: "WorkAccidentId",
                schema: "hr",
                table: "LeaveRequests");
        }
    }
}
