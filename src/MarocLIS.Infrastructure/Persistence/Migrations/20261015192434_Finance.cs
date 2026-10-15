using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Finance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rel");

            migrationBuilder.EnsureSchema(
                name: "fin");

            migrationBuilder.AddColumn<string>(
                name: "AccountingAccount",
                schema: "pat",
                table: "Payers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReconciledAt",
                schema: "bil",
                table: "CashSessions",
                type: "datetimeoffset(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReconciledByUserId",
                schema: "bil",
                table: "CashSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationComment",
                schema: "bil",
                table: "CashSessions",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankAccounts",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Rib = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    AccountingAccount = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    OpeningBalanceMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    OpeningBalanceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CsvMapping = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DelegateVisits",
                schema: "rel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PrescriberId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    NextVisitOn = table.Column<DateOnly>(type: "date", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelegateVisits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FixedAssets",
                schema: "fin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcquisitionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    InServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CostExcludingVatMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    VatAmountMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    VatRecoverable = table.Column<bool>(type: "bit", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SupplierInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Method = table.Column<int>(type: "int", nullable: false),
                    UsefulLifeYears = table.Column<int>(type: "int", nullable: false),
                    ResidualValueMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DisposalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DisposalPriceMad = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    DisposalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PartnershipDocuments",
                schema: "rel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartnershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    AttachedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    AttachedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnershipDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Partnerships",
                schema: "rel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartnerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    PartnerKind = table.Column<int>(type: "int", nullable: false),
                    PrescriberId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PartnerIce = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Terms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DiscountPercent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PaymentTermsDays = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Partnerships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PatientMessages",
                schema: "rel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ReminderRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceResultDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SurveyInvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    SentByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Failure = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReminderRules",
                schema: "rel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisTestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TestPanelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DelayMonths = table.Column<int>(type: "int", nullable: false),
                    WindowDays = table.Column<int>(type: "int", nullable: false),
                    TemplateFr = table.Column<string>(type: "nvarchar(480)", maxLength: 480, nullable: false),
                    TemplateAr = table.Column<string>(type: "nvarchar(480)", maxLength: 480, nullable: true),
                    TemplateEn = table.Column<string>(type: "nvarchar(480)", maxLength: 480, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SurveyInvitations",
                schema: "rel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    InvitedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    Delivered = table.Column<bool>(type: "bit", nullable: false),
                    Score = table.Column<int>(type: "int", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    AnswerChannel = table.Column<int>(type: "int", nullable: true),
                    AnswerRecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyInvitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryForecastItems",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AmountMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Recurrence = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryForecastItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankStatementLines",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OperationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ValueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AmountMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatementLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankStatementLines_BankAccounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalSchema: "bil",
                        principalTable: "BankAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankMatches",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankStatementLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SourceKey = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    MovementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsAutomatic = table.Column<bool>(type: "bit", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    MatchedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnmatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    UnmatchedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankMatches_BankStatementLines_BankStatementLineId",
                        column: x => x.BankStatementLineId,
                        principalSchema: "bil",
                        principalTable: "BankStatementLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_Site_Active",
                schema: "bil",
                table: "BankAccounts",
                columns: new[] { "SiteId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "UX_BankAccounts_Rib",
                schema: "bil",
                table: "BankAccounts",
                column: "Rib",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankMatches_Line",
                schema: "bil",
                table: "BankMatches",
                column: "BankStatementLineId");

            migrationBuilder.CreateIndex(
                name: "UX_BankMatches_ActiveSource",
                schema: "bil",
                table: "BankMatches",
                columns: new[] { "Kind", "SourceKey" },
                unique: true,
                filter: "[UnmatchedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementLines_Account_Date",
                schema: "bil",
                table: "BankStatementLines",
                columns: new[] { "BankAccountId", "OperationDate" });

            migrationBuilder.CreateIndex(
                name: "UX_BankStatementLines_Account_Fingerprint",
                schema: "bil",
                table: "BankStatementLines",
                columns: new[] { "BankAccountId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DelegateVisits_Prescriber",
                schema: "rel",
                table: "DelegateVisits",
                columns: new[] { "PrescriberId", "VisitDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DelegateVisits_Site_Date",
                schema: "rel",
                table: "DelegateVisits",
                columns: new[] { "SiteId", "VisitDate" });

            migrationBuilder.CreateIndex(
                name: "UX_FixedAssets_Instrument",
                schema: "fin",
                table: "FixedAssets",
                column: "InstrumentId",
                unique: true,
                filter: "[InstrumentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_FixedAssets_Site_Code",
                schema: "fin",
                table: "FixedAssets",
                columns: new[] { "SiteId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartnershipDocuments_Partnership",
                schema: "rel",
                table: "PartnershipDocuments",
                columns: new[] { "PartnershipId", "AttachedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Partnerships_Site",
                schema: "rel",
                table: "Partnerships",
                columns: new[] { "SiteId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientMessages_Patient",
                schema: "rel",
                table: "PatientMessages",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientMessages_Rule_Order",
                schema: "rel",
                table: "PatientMessages",
                columns: new[] { "ReminderRuleId", "SourceOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientMessages_Site_SentAt",
                schema: "rel",
                table: "PatientMessages",
                columns: new[] { "SiteId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReminderRules_Analysis",
                schema: "rel",
                table: "ReminderRules",
                column: "AnalysisTestId");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderRules_Panel",
                schema: "rel",
                table: "ReminderRules",
                column: "TestPanelId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyInvitations_Order",
                schema: "rel",
                table: "SurveyInvitations",
                column: "LabOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyInvitations_Patient",
                schema: "rel",
                table: "SurveyInvitations",
                columns: new[] { "PatientId", "InvitedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyInvitations_Site_InvitedAt",
                schema: "rel",
                table: "SurveyInvitations",
                columns: new[] { "SiteId", "InvitedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SurveyInvitations_Code",
                schema: "rel",
                table: "SurveyInvitations",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryForecastItems_Site_Active",
                schema: "bil",
                table: "TreasuryForecastItems",
                columns: new[] { "SiteId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankMatches",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "DelegateVisits",
                schema: "rel");

            migrationBuilder.DropTable(
                name: "FixedAssets",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "PartnershipDocuments",
                schema: "rel");

            migrationBuilder.DropTable(
                name: "Partnerships",
                schema: "rel");

            migrationBuilder.DropTable(
                name: "PatientMessages",
                schema: "rel");

            migrationBuilder.DropTable(
                name: "ReminderRules",
                schema: "rel");

            migrationBuilder.DropTable(
                name: "SurveyInvitations",
                schema: "rel");

            migrationBuilder.DropTable(
                name: "TreasuryForecastItems",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "BankStatementLines",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "BankAccounts",
                schema: "bil");

            migrationBuilder.DropColumn(
                name: "AccountingAccount",
                schema: "pat",
                table: "Payers");

            migrationBuilder.DropColumn(
                name: "ReconciledAt",
                schema: "bil",
                table: "CashSessions");

            migrationBuilder.DropColumn(
                name: "ReconciledByUserId",
                schema: "bil",
                table: "CashSessions");

            migrationBuilder.DropColumn(
                name: "ReconciliationComment",
                schema: "bil",
                table: "CashSessions");
        }
    }
}
