using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonnelFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hr");

            migrationBuilder.CreateTable(
                name: "Employees",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    FullNameArabic = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Gender = table.Column<int>(type: "int", nullable: false),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Cin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CnssNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FamilyStatus = table.Column<int>(type: "int", nullable: false),
                    DependentChildren = table.Column<int>(type: "int", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Job = table.Column<int>(type: "int", nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsExposedToBiologicalRisk = table.Column<bool>(type: "bit", nullable: false),
                    ConfidentialityClauseSignedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ContractType = table.Column<int>(type: "int", nullable: false),
                    HireDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ContractEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TrialPeriodEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    WeeklyHours = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    BaseSalary = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Rib = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExitDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExitReason = table.Column<int>(type: "int", nullable: true),
                    ExitComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeCredentials",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    IssuedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ObtainedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Fitness = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeCredentials_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeHabilitations",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Activity = table.Column<int>(type: "int", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Level = table.Column<int>(type: "int", nullable: false),
                    GrantedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ReviewDueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    GrantedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    WithdrawnOn = table.Column<DateOnly>(type: "date", nullable: true),
                    WithdrawalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeHabilitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeHabilitations_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeCredentials_EmployeeId",
                schema: "hr",
                table: "EmployeeCredentials",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeCredentials_Expiry",
                schema: "hr",
                table: "EmployeeCredentials",
                column: "ExpiresOn");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeHabilitations_Activity",
                schema: "hr",
                table: "EmployeeHabilitations",
                columns: new[] { "EmployeeId", "Activity", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Cin",
                schema: "hr",
                table: "Employees",
                column: "Cin");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Site_Exit",
                schema: "hr",
                table: "Employees",
                columns: new[] { "SiteId", "ExitDate" });

            migrationBuilder.CreateIndex(
                name: "UX_Employees_Number",
                schema: "hr",
                table: "Employees",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Employees_User",
                schema: "hr",
                table: "Employees",
                column: "UserId",
                unique: true,
                filter: "[UserId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeCredentials",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "EmployeeHabilitations",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "Employees",
                schema: "hr");
        }
    }
}
