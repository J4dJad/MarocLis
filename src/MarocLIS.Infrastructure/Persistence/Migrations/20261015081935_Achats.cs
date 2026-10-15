using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Achats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountingAccount",
                schema: "inv",
                table: "Suppliers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegulatoryRegistrationNumber",
                schema: "inv",
                table: "Suppliers",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RegulatoryRegistrationValidUntil",
                schema: "inv",
                table: "Suppliers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gtin",
                schema: "inv",
                table: "StockItems",
                type: "varchar(14)",
                unicode: false,
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCeMarked",
                schema: "inv",
                table: "StockItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ArrivalTemperatureCelsius",
                schema: "inv",
                table: "ReagentLots",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CertificateOfAnalysisReceived",
                schema: "inv",
                table: "ReagentLots",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QcOverrideJustification",
                schema: "inv",
                table: "ReagentLots",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceptionFrenchLabelling",
                schema: "inv",
                table: "ReagentLots",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceptionMatchesOrder",
                schema: "inv",
                table: "ReagentLots",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceptionPackagingIntact",
                schema: "inv",
                table: "ReagentLots",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReleasedWithQcOverride",
                schema: "inv",
                table: "ReagentLots",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastReceivedOn",
                schema: "inv",
                table: "PurchaseOrderLines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                schema: "qc",
                table: "NonConformities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArrivedAt",
                schema: "eqp",
                table: "MaintenanceRecords",
                type: "datetimeoffset(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequestedAt",
                schema: "eqp",
                table: "MaintenanceRecords",
                type: "datetimeoffset(3)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InstrumentContracts",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DurationMonths = table.Column<int>(type: "int", nullable: false),
                    CommitmentBasis = table.Column<int>(type: "int", nullable: false),
                    CommittedVolume = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CountedAnalysisCodesCsv = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PreventiveVisitsPerYear = table.Column<int>(type: "int", nullable: true),
                    MaxInterventionDelayHours = table.Column<int>(type: "int", nullable: true),
                    MaintenanceTerms = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TrainingTerms = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExitConditions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PenaltyTerms = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PenaltyAmountMad = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentContracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LotCertificateFiles",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReagentLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LotCertificateFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LotCertificateFiles_ReagentLots_ReagentLotId",
                        column: x => x.ReagentLotId,
                        principalSchema: "inv",
                        principalTable: "ReagentLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PurchasingDocuments",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerKind = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_PurchasingDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuoteRequests",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    RaisedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResponseDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AwardedQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AwardedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AwardedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RefusedDeliveries",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RefusedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    RefusedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RefusedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ArrivalTemperatureCelsius = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    DeliveryNoteReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LotNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    NonConformityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefusedDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoiceFiles",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoiceFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoices",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EnteredByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DeliveryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentTermsDays = table.Column<int>(type: "int", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    UnblockedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnblockedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    UnblockJustification = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ScanFileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstrumentContractInstruments",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentContractInstruments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstrumentContractInstruments_InstrumentContracts_InstrumentContractId",
                        column: x => x.InstrumentContractId,
                        principalSchema: "eqp",
                        principalTable: "InstrumentContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstrumentContractReagents",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractPriceMad = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CostPerTestMad = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentContractReagents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstrumentContractReagents_InstrumentContracts_InstrumentContractId",
                        column: x => x.InstrumentContractId,
                        principalSchema: "eqp",
                        principalTable: "InstrumentContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuoteRequestItems",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteRequestItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteRequestItems_QuoteRequests_QuoteRequestId",
                        column: x => x.QuoteRequestId,
                        principalSchema: "inv",
                        principalTable: "QuoteRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuoteRequestSuppliers",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteRequestSuppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteRequestSuppliers_QuoteRequests_QuoteRequestId",
                        column: x => x.QuoteRequestId,
                        principalSchema: "inv",
                        principalTable: "QuoteRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierQuotes",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    SupplierReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    DeliveryDelayDays = table.Column<int>(type: "int", nullable: true),
                    Conditions = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierQuotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierQuotes_QuoteRequests_QuoteRequestId",
                        column: x => x.QuoteRequestId,
                        principalSchema: "inv",
                        principalTable: "QuoteRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoiceLines",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    UnitPriceMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierInvoiceLines_PurchaseOrderLines_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalSchema: "inv",
                        principalTable: "PurchaseOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierInvoiceLines_SupplierInvoices_SupplierInvoiceId",
                        column: x => x.SupplierInvoiceId,
                        principalSchema: "inv",
                        principalTable: "SupplierInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoiceVariances",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Expected = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Actual = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoiceVariances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierInvoiceVariances_SupplierInvoices_SupplierInvoiceId",
                        column: x => x.SupplierInvoiceId,
                        principalSchema: "inv",
                        principalTable: "SupplierInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPayments",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_SupplierInvoices_SupplierInvoiceId",
                        column: x => x.SupplierInvoiceId,
                        principalSchema: "inv",
                        principalTable: "SupplierInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierQuoteLines",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteRequestItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitPriceMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierQuoteLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierQuoteLines_SupplierQuotes_SupplierQuoteId",
                        column: x => x.SupplierQuoteId,
                        principalSchema: "inv",
                        principalTable: "SupplierQuotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_Site_Gtin",
                schema: "inv",
                table: "StockItems",
                columns: new[] { "SiteId", "Gtin" },
                filter: "[Gtin] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReagentLots_PurchaseOrder",
                schema: "inv",
                table: "ReagentLots",
                column: "PurchaseOrderId",
                filter: "[PurchaseOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NonConformities_Supplier",
                schema: "qc",
                table: "NonConformities",
                column: "SupplierId",
                filter: "[SupplierId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentContractInstruments_Instrument",
                schema: "eqp",
                table: "InstrumentContractInstruments",
                column: "InstrumentId");

            migrationBuilder.CreateIndex(
                name: "UX_InstrumentContractInstruments_Contract_Instrument",
                schema: "eqp",
                table: "InstrumentContractInstruments",
                columns: new[] { "InstrumentContractId", "InstrumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentContractReagents_Item",
                schema: "eqp",
                table: "InstrumentContractReagents",
                column: "StockItemId");

            migrationBuilder.CreateIndex(
                name: "UX_InstrumentContractReagents_Contract_Item",
                schema: "eqp",
                table: "InstrumentContractReagents",
                columns: new[] { "InstrumentContractId", "StockItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentContracts_Site_Active",
                schema: "eqp",
                table: "InstrumentContracts",
                columns: new[] { "SiteId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "UX_InstrumentContracts_Supplier_Reference",
                schema: "eqp",
                table: "InstrumentContracts",
                columns: new[] { "SupplierId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LotCertificateFiles_Lot",
                schema: "inv",
                table: "LotCertificateFiles",
                column: "ReagentLotId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchasingDocuments_Owner",
                schema: "inv",
                table: "PurchasingDocuments",
                columns: new[] { "OwnerKind", "OwnerId", "AttachedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteRequestItems_Request_Item",
                schema: "inv",
                table: "QuoteRequestItems",
                columns: new[] { "QuoteRequestId", "StockItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRequests_Site_Status",
                schema: "inv",
                table: "QuoteRequests",
                columns: new[] { "SiteId", "Status", "RequestedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteRequests_Number",
                schema: "inv",
                table: "QuoteRequests",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QuoteRequestSuppliers_Request_Supplier",
                schema: "inv",
                table: "QuoteRequestSuppliers",
                columns: new[] { "QuoteRequestId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefusedDeliveries_Supplier_Date",
                schema: "inv",
                table: "RefusedDeliveries",
                columns: new[] { "SupplierId", "RefusedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_SupplierInvoiceFiles_Invoice",
                schema: "inv",
                table: "SupplierInvoiceFiles",
                column: "SupplierInvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoiceLines_OrderLine",
                schema: "inv",
                table: "SupplierInvoiceLines",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierInvoiceLines_Invoice_Line",
                schema: "inv",
                table: "SupplierInvoiceLines",
                columns: new[] { "SupplierInvoiceId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoices_Site_Status_Due",
                schema: "inv",
                table: "SupplierInvoices",
                columns: new[] { "SiteId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "UX_SupplierInvoices_Supplier_Number",
                schema: "inv",
                table: "SupplierInvoices",
                columns: new[] { "SupplierId", "InvoiceNumber" },
                unique: true,
                filter: "[Status] <> 9");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoiceVariances_Invoice",
                schema: "inv",
                table: "SupplierInvoiceVariances",
                column: "SupplierInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_Date",
                schema: "inv",
                table: "SupplierPayments",
                column: "PaidOn");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_Invoice_Date",
                schema: "inv",
                table: "SupplierPayments",
                columns: new[] { "SupplierInvoiceId", "PaidOn" });

            migrationBuilder.CreateIndex(
                name: "UX_SupplierQuoteLines_Quote_Item",
                schema: "inv",
                table: "SupplierQuoteLines",
                columns: new[] { "SupplierQuoteId", "QuoteRequestItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SupplierQuotes_Request_Supplier",
                schema: "inv",
                table: "SupplierQuotes",
                columns: new[] { "QuoteRequestId", "SupplierId" },
                unique: true);
            // Loi n° 69-21: agreed terms cannot exceed 120 days.
            migrationBuilder.Sql("UPDATE inv.Suppliers SET PaymentTermsDays = 120 WHERE PaymentTermsDays > 120;");

            // Older lots received against an order: link them so supplier punctuality counts them.
            migrationBuilder.Sql(@"
UPDATE l SET PurchaseOrderId = po.Id
FROM inv.ReagentLots l JOIN inv.PurchaseOrders po ON po.Number COLLATE DATABASE_DEFAULT = l.DeliveryNoteReference COLLATE DATABASE_DEFAULT AND po.SupplierId = l.SupplierId
WHERE l.PurchaseOrderId IS NULL;");
            migrationBuilder.Sql(@"
UPDATE l SET PurchaseOrderId = c.Id
FROM inv.ReagentLots l
CROSS APPLY (SELECT TOP (1) po.Id FROM inv.PurchaseOrders po JOIN inv.PurchaseOrderLines pl ON pl.PurchaseOrderId = po.Id
             WHERE pl.StockItemId = l.StockItemId AND po.SupplierId = l.SupplierId AND po.OrderDate <= l.ReceivedOn
               AND pl.QuantityReceived > 0) c
WHERE l.PurchaseOrderId IS NULL
  AND (SELECT COUNT(DISTINCT po.Id) FROM inv.PurchaseOrders po JOIN inv.PurchaseOrderLines pl ON pl.PurchaseOrderId = po.Id
       WHERE pl.StockItemId = l.StockItemId AND po.SupplierId = l.SupplierId AND po.OrderDate <= l.ReceivedOn
         AND pl.QuantityReceived > 0) = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstrumentContractInstruments",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "InstrumentContractReagents",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "LotCertificateFiles",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "PurchasingDocuments",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "QuoteRequestItems",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "QuoteRequestSuppliers",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "RefusedDeliveries",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "SupplierInvoiceFiles",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "SupplierInvoiceLines",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "SupplierInvoiceVariances",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "SupplierPayments",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "SupplierQuoteLines",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "InstrumentContracts",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "SupplierInvoices",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "SupplierQuotes",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "QuoteRequests",
                schema: "inv");

            migrationBuilder.DropIndex(
                name: "IX_StockItems_Site_Gtin",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropIndex(
                name: "IX_ReagentLots_PurchaseOrder",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropIndex(
                name: "IX_NonConformities_Supplier",
                schema: "qc",
                table: "NonConformities");

            migrationBuilder.DropColumn(
                name: "AccountingAccount",
                schema: "inv",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "RegulatoryRegistrationNumber",
                schema: "inv",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "RegulatoryRegistrationValidUntil",
                schema: "inv",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "Gtin",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "IsCeMarked",
                schema: "inv",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "ArrivalTemperatureCelsius",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "CertificateOfAnalysisReceived",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "QcOverrideJustification",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "ReceptionFrenchLabelling",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "ReceptionMatchesOrder",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "ReceptionPackagingIntact",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "ReleasedWithQcOverride",
                schema: "inv",
                table: "ReagentLots");

            migrationBuilder.DropColumn(
                name: "LastReceivedOn",
                schema: "inv",
                table: "PurchaseOrderLines");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                schema: "qc",
                table: "NonConformities");

            migrationBuilder.DropColumn(
                name: "ArrivedAt",
                schema: "eqp",
                table: "MaintenanceRecords");

            migrationBuilder.DropColumn(
                name: "RequestedAt",
                schema: "eqp",
                table: "MaintenanceRecords");
        }
    }
}
