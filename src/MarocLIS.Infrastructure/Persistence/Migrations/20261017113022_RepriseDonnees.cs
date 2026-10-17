using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepriseDonnees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mig");

            migrationBuilder.CreateTable(
                name: "HistoricalResults",
                schema: "mig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultDate = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ValueText = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    NumericValue = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ValueOperator = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ReferenceRange = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OldAccessionNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SourceCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricalResults_TestParameters_TestParameterId",
                        column: x => x.TestParameterId,
                        principalSchema: "cat",
                        principalTable: "TestParameters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MigrationBatches",
                schema: "mig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SourceLabel = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<int>(type: "int", nullable: false),
                    Updated = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    IsInterrupted = table.Column<bool>(type: "bit", nullable: false),
                    IsUndone = table.Column<bool>(type: "bit", nullable: false),
                    UndoneAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    UndoneCount = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationRecords",
                schema: "mig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByBatch = table.Column<bool>(type: "bit", nullable: false),
                    OldNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpeningBalances",
                schema: "mig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    OldInvoiceNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    OldInvoiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningBalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpeningBalances_Payers_PayerId",
                        column: x => x.PayerId,
                        principalSchema: "pat",
                        principalTable: "Payers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalResults_Batch",
                schema: "mig",
                table: "HistoricalResults",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalResults_OldAccession",
                schema: "mig",
                table: "HistoricalResults",
                column: "OldAccessionNumber",
                filter: "[OldAccessionNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalResults_Patient_Parameter",
                schema: "mig",
                table: "HistoricalResults",
                columns: new[] { "PatientId", "TestParameterId", "ResultDate" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalResults_TestParameterId",
                schema: "mig",
                table: "HistoricalResults",
                column: "TestParameterId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationBatches_Started",
                schema: "mig",
                table: "MigrationBatches",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRecords_Batch",
                schema: "mig",
                table: "MigrationRecords",
                column: "LastBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRecords_Entity",
                schema: "mig",
                table: "MigrationRecords",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRecords_OldNumber",
                schema: "mig",
                table: "MigrationRecords",
                column: "OldNumber",
                filter: "[OldNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_MigrationRecords_Key",
                schema: "mig",
                table: "MigrationRecords",
                columns: new[] { "Kind", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalances_Batch",
                schema: "mig",
                table: "OpeningBalances",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalances_Patient",
                schema: "mig",
                table: "OpeningBalances",
                column: "PatientId",
                filter: "[PatientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBalances_PayerId",
                schema: "mig",
                table: "OpeningBalances",
                column: "PayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricalResults",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "MigrationBatches",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "MigrationRecords",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "OpeningBalances",
                schema: "mig");
        }
    }
}
