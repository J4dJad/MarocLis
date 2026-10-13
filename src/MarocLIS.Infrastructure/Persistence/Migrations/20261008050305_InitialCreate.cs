using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cat");

            migrationBuilder.EnsureSchema(
                name: "aud");

            migrationBuilder.EnsureSchema(
                name: "eqp");

            migrationBuilder.EnsureSchema(
                name: "bil");

            migrationBuilder.EnsureSchema(
                name: "qc");

            migrationBuilder.EnsureSchema(
                name: "res");

            migrationBuilder.EnsureSchema(
                name: "ord");

            migrationBuilder.EnsureSchema(
                name: "rpt");

            migrationBuilder.EnsureSchema(
                name: "cfg");

            migrationBuilder.EnsureSchema(
                name: "sec");

            migrationBuilder.EnsureSchema(
                name: "pat");

            migrationBuilder.EnsureSchema(
                name: "inv");

            migrationBuilder.EnsureSchema(
                name: "smp");

            migrationBuilder.EnsureSchema(
                name: "org");

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                schema: "aud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Workstation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OsUser = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Changes = table.Column<string>(type: "nvarchar(max)", maxLength: 400, nullable: true),
                    ChangedProperties = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AggregateName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashSessions",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperatorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Workstation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ExpectedCashReceipts = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CashRefunds = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CashWithdrawals = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CountedCash = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    VarianceExplanation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ChequeReceipts = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CardReceipts = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TransferReceipts = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContainerTypes",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CapColourHex = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: false),
                    AdditiveFr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AdditiveAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AdditiveEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NominalVolumeMl = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    MinimumVolumeMl = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    DrawOrder = table.Column<int>(type: "int", nullable: false),
                    RequiresProtectionFromLight = table.Column<bool>(type: "bit", nullable: false),
                    RequiresIceTransport = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ControlledDocuments",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    TitleFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TitleAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TitleEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProcessCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewPeriodMonths = table.Column<int>(type: "int", nullable: false),
                    RequiresAcknowledgement = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlledDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CriticalResultNotifications",
                schema: "res",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportedValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParameterName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    NotifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ContactUsed = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PrescriberId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RecipientRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ReadBackConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    ReadBackValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    EscalatedToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CriticalResultNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataAccessLog",
                schema: "aud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LabReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordCount = table.Column<int>(type: "int", nullable: true),
                    QueryDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Context = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Workstation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsBreakGlass = table.Column<bool>(type: "bit", nullable: false),
                    BreakGlassJustification = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataAccessLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsInAccreditationScope = table.Column<bool>(type: "bit", nullable: false),
                    SupervisorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EqaParticipations",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchemeName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    RoundReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RoundDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SubmissionDeadline = table.Column<DateOnly>(type: "date", nullable: true),
                    SubmittedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReportedValue = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    AssignedValue = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PeerGroupMean = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PeerGroupSd = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ZScore = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    BiasPercent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    NonConformityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvestigationFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    InvestigationAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    InvestigationEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EqaParticipations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstrumentInterfaces",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Protocol = table.Column<int>(type: "int", nullable: false),
                    Transport = table.Column<int>(type: "int", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SerialPortName = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BaudRate = table.Column<int>(type: "int", nullable: false),
                    DataBits = table.Column<int>(type: "int", nullable: false),
                    Parity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    StopBits = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Handshake = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    TcpPort = table.Column<int>(type: "int", nullable: true),
                    ListenAsServer = table.Column<bool>(type: "bit", nullable: false),
                    InboundDirectory = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OutboundDirectory = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FilePattern = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ProcessedDirectory = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Encoding = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AcknowledgementTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    MaxRetries = table.Column<int>(type: "int", nullable: false),
                    AutoAcceptResults = table.Column<bool>(type: "bit", nullable: false),
                    RaiseIssueOnUnmappedCode = table.Column<bool>(type: "bit", nullable: false),
                    RouteControlResultsToQc = table.Column<bool>(type: "bit", nullable: false),
                    LastConnectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentInterfaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstrumentMessageLog",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentInterfaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RawContent = table.Column<string>(type: "nvarchar(max)", maxLength: 400, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SpecimenIds = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResultCount = table.Column<int>(type: "int", nullable: true),
                    ImportedCount = table.Column<int>(type: "int", nullable: true),
                    Issues = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ReplayedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ReplayedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentMessageLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instruments",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Manufacturer = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SoftwareVersion = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    PurchaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CommissioningDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DecommissioningDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MaintenanceContractReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MaintenanceContractValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    SupportPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    PreventiveMaintenanceIntervalDays = table.Column<int>(type: "int", nullable: true),
                    LastPreventiveMaintenanceOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CalibrationIntervalDays = table.Column<int>(type: "int", nullable: true),
                    LastCalibrationOn = table.Column<DateOnly>(type: "date", nullable: true),
                    VerificationIntervalDays = table.Column<int>(type: "int", nullable: true),
                    LastVerificationOn = table.Column<DateOnly>(type: "date", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LastStatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    IssuedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BilledToName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    BilledToIce = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    BilledToFiscalId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BilledToAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    BilledToCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CoveredOrderNumbersCsv = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    GrossAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NetAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    VatAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PayerShareAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PatientShareAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AmountInWords = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CorrectsInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrectsInvoiceNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CorrectionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HasCreditNote = table.Column<bool>(type: "bit", nullable: false),
                    ClaimReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ClaimSubmittedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ClaimStatus = table.Column<int>(type: "int", nullable: false),
                    ClaimRejectionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BiologistInpe = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PrescriberInpe = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PrescriberName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RenderedPdf = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LabReports",
                schema: "rpt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Completeness = table.Column<int>(type: "int", nullable: false),
                    Language = table.Column<int>(type: "int", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ReleasedByBiologistUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignatoryName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SignatoryQualification = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    SignatoryOrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    RenderedPdf = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    PageCount = table.Column<int>(type: "int", nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AccessToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    SupersedesReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersededByReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AmendmentKind = table.Column<int>(type: "int", nullable: false),
                    AmendmentReasonFr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AmendmentReasonAr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AmendmentReasonEn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AmendedParameterCodesCsv = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ContainsSubcontractedResults = table.Column<bool>(type: "bit", nullable: false),
                    ContainsReservations = table.Column<bool>(type: "bit", nullable: false),
                    ContainsCriticalResults = table.Column<bool>(type: "bit", nullable: false),
                    AllResultsAccredited = table.Column<bool>(type: "bit", nullable: false),
                    ConclusionFr = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ConclusionAr = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ConclusionEn = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    IncludedOrderItemIdsCsv = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NonConformities",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DescriptionFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DescriptionAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DescriptionEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DetectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    DetectedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DetectionSource = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TestResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReagentLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QcResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AffectedReleasedResults = table.Column<bool>(type: "bit", nullable: false),
                    AffectedPatientCount = table.Column<int>(type: "int", nullable: true),
                    ClinicalImpactFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ClinicalImpactAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ClinicalImpactEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AmendedReportsIssued = table.Column<bool>(type: "bit", nullable: false),
                    ImmediateActionFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ImmediateActionAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ImmediateActionEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RootCauseFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RootCauseAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RootCauseEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RootCause = table.Column<int>(type: "int", nullable: true),
                    IsRecurrence = table.Column<bool>(type: "bit", nullable: false),
                    PreviousOccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EffectivenessFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EffectivenessAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EffectivenessEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EffectivenessVerifiedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectivenessVerifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NonConformities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSequences",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Year = table.Column<int>(type: "int", nullable: true),
                    Format = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false),
                    LastAllocatedValue = table.Column<long>(type: "bigint", nullable: false),
                    LastAllocatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    IsClosed = table.Column<bool>(type: "bit", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PasswordHistory",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Patients",
                schema: "pat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MedicalRecordNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    FirstName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    LastNameArabic = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    FirstNameArabic = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    SearchKey = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    Gender = table.Column<int>(type: "int", nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    DeclaredAgeYears = table.Column<int>(type: "int", nullable: true),
                    PlaceOfBirth = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Cin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PassportNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    NationalityCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    GuardianCin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    GuardianFullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    GuardianRelationship = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AddressLine = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    Region = table.Column<int>(type: "int", nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    MobilePhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AlternatePhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PreferredLanguage = table.Column<int>(type: "int", nullable: false),
                    IsPregnant = table.Column<bool>(type: "bit", nullable: false),
                    LastMenstrualPeriod = table.Column<DateOnly>(type: "date", nullable: true),
                    WeightKg = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    HeightCm = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    BloodGroup = table.Column<int>(type: "int", nullable: false),
                    CurrentTreatment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    KnownAllergies = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetentionUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    IsAnonymized = table.Column<bool>(type: "bit", nullable: false),
                    AnonymizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    OptedOutOfSecondaryUse = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MergedIntoPatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payers",
                schema: "pat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    DefaultReimbursementRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    UsesNationalReferenceTariff = table.Column<bool>(type: "bit", nullable: false),
                    ThirdPartyPaymentAgreement = table.Column<bool>(type: "bit", nullable: false),
                    AgreementNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AgreementValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentTermsDays = table.Column<int>(type: "int", nullable: false),
                    RequiresPrescription = table.Column<bool>(type: "bit", nullable: false),
                    RequiresInpeOnClaim = table.Column<bool>(type: "bit", nullable: false),
                    Ice = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddressLine = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Prescribers",
                schema: "ord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    FirstName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    FullNameArabic = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    SearchKey = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SpecialityFr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    SpecialityAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    SpecialityEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ProfessionalOrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Inpe = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Ice = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    MobilePhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Fax = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    AddressLine = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    Region = table.Column<int>(type: "int", nullable: false),
                    InstitutionName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    DeliveryPreference = table.Column<int>(type: "int", nullable: false),
                    ReportLanguage = table.Column<int>(type: "int", nullable: false),
                    SendPartialReports = table.Column<bool>(type: "bit", nullable: false),
                    CriticalResultPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DiscountPercent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    DefaultPayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prescribers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RaisedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExpectedDeliveryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    SupplierReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QcMaterials",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Manufacturer = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CatalogueReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OpenedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    StabilityAfterOpeningDays = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QcMaterials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QcResults",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QcTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QcRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Value = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    MeasuredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    EnteredByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsAutoTransmitted = table.Column<bool>(type: "bit", nullable: false),
                    TargetMean = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    TargetSd = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ZScore = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    ViolatedRules = table.Column<int>(type: "int", nullable: false),
                    ReagentLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CalibrationRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrectiveActionTaken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    NonConformityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WasOverridden = table.Column<bool>(type: "bit", nullable: false),
                    OverrideJustification = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QcResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QcTargets",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QcMaterialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedMean = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    AssignedSd = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    LaboratoryMean = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    LaboratorySd = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    LaboratoryPointCount = table.Column<int>(type: "int", nullable: false),
                    StatisticsFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    StatisticsTo = table.Column<DateOnly>(type: "date", nullable: true),
                    MaxAcceptableCvPercent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ActiveRules = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QcTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReflexRules",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TriggerParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisToAddId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DescriptionFr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DescriptionAr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DescriptionEn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Condition = table.Column<int>(type: "int", nullable: false),
                    Threshold = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ThresholdUpper = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    TriggerCodedValue = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AddAutomatically = table.Column<bool>(type: "bit", nullable: false),
                    IsBillable = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReflexRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DescriptionFr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DescriptionAr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DescriptionEn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsBuiltIn = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Samples",
                schema: "smp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpecimenTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContainerTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SequenceInOrder = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    CollectedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CollectionLocation = table.Column<int>(type: "int", nullable: false),
                    CollectionSite = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    VolumeMl = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CollectionAttempts = table.Column<int>(type: "int", nullable: false),
                    CollectionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    TransportRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ReceivedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceptionTemperatureCelsius = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ColdChainRespected = table.Column<bool>(type: "bit", nullable: true),
                    IsAcceptedWithReservation = table.Column<bool>(type: "bit", nullable: false),
                    Defects = table.Column<int>(type: "int", nullable: false),
                    RejectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RejectedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RejectionReason = table.Column<int>(type: "int", nullable: true),
                    RejectionComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecollectionRequested = table.Column<bool>(type: "bit", nullable: false),
                    ReplacementSampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StorageLocation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StoragePosition = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    KeepUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    DiscardedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DiscardedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Samples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                schema: "aud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttemptedUserName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Workstation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OsUser = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FailureReasonKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsSecuritySensitive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                schema: "org",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Ice = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    FiscalId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CommercialRegisterNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CommercialRegisterCourt = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProfessionalTaxNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CnssAffiliationNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Inpe = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    HealthMinistryAuthorizationNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    HealthMinistryAuthorizationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsAccredited = table.Column<bool>(type: "bit", nullable: false),
                    AccreditationNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AccreditationValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    AddressFr = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AddressAr = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AddressEn = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Region = table.Column<int>(type: "int", nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Fax = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LogoPng = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ResponsibleBiologistUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpecimenTypes",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CollectionInstructionsFr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CollectionInstructionsAr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CollectionInstructionsEn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PatientInstructionsFr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PatientInstructionsAr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PatientInstructionsEn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FastingHours = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecimenTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockItems",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TestsPerPack = table.Column<int>(type: "int", nullable: true),
                    PreferredSupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierCatalogueReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Manufacturer = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UsedByAnalysisCodesCsv = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReorderPoint = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ReorderQuantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    MinimumStock = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    MaximumStock = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    LastUnitPriceMad = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    StorageCondition = table.Column<int>(type: "int", nullable: false),
                    DefaultStorageLocation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsHazardous = table.Column<bool>(type: "bit", nullable: false),
                    HazardClass = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SafetyDataSheetDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsCritical = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReagentLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    Ice = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    FiscalId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CommercialRegisterNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddressLine = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Region = table.Column<int>(type: "int", nullable: false),
                    LeadTimeDays = table.Column<int>(type: "int", nullable: false),
                    PaymentTermsDays = table.Column<int>(type: "int", nullable: false),
                    IsApproved = table.Column<bool>(type: "bit", nullable: false),
                    LastEvaluationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EvaluationScore = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TestPanels",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    SearchAliases = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    PackagePrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ItemizeOnInvoice = table.Column<bool>(type: "bit", nullable: false),
                    GroupOnReport = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestPanels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Units",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    DisplaySymbol = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DisplaySymbolArabic = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    UcumCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    FactorToSi = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    SiUnitCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Units", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    FullNameArabic = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SecurityStamp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    PasswordChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PreferredLanguage = table.Column<int>(type: "int", nullable: false),
                    Qualification = table.Column<int>(type: "int", nullable: false),
                    ProfessionalOrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Inpe = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    QualificationValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    SignaturePng = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    AuthorizedDepartmentCodesCsv = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FailedLoginAttempts = table.Column<int>(type: "int", nullable: false),
                    LockedOutUntil = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    LastLoginWorkstation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentAcknowledgements",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ControlledDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentAcknowledgements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentAcknowledgements_ControlledDocuments_ControlledDocumentId",
                        column: x => x.ControlledDocumentId,
                        principalSchema: "qc",
                        principalTable: "ControlledDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentVersions",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ControlledDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChangeSummaryFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangeSummaryAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangeSummaryEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    WithdrawnOn = table.Column<DateOnly>(type: "date", nullable: true),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentVersions_ControlledDocuments_ControlledDocumentId",
                        column: x => x.ControlledDocumentId,
                        principalSchema: "qc",
                        principalTable: "ControlledDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstrumentTestMappings",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentInterfaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentTestCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentUnit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ConversionFactor = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ConversionOffset = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CodedValueMap = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentTestMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstrumentTestMappings_InstrumentInterfaces_InstrumentInterfaceId",
                        column: x => x.InstrumentInterfaceId,
                        principalSchema: "eqp",
                        principalTable: "InstrumentInterfaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalibrationRecords",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PerformedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PerformedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalibratorLotNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CalibratorExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CalibratorManufacturer = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ReagentLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Slope = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Intercept = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CorrelationCoefficient = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CurveData = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    VerifiedByQc = table.Column<bool>(type: "bit", nullable: false),
                    VerifyingQcResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalibrationRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalibrationRecords_Instruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalSchema: "eqp",
                        principalTable: "Instruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceRecords",
                schema: "eqp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    PerformedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DescriptionFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DescriptionAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DescriptionEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PerformedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalTechnicianName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SupplierInterventionReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DowntimeHours = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CostMad = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    PartsReplaced = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PostInterventionCheckPassed = table.Column<bool>(type: "bit", nullable: false),
                    PostInterventionCheckNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NonConformityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttachedReport = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    AttachedReportContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceRecords_Instruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalSchema: "eqp",
                        principalTable: "Instruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DescriptionArabic = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LabOrderItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AnalysisTestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AnalysisCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    NationalActCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    LetterKey = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: true),
                    Coefficient = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    LetterKeyValue = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ReferenceTariffBase = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AllocatedDiscount = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    IsVatExempt = table.Column<bool>(type: "bit", nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ReimbursementRate = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PayerShare = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "bil",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "bil",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReceivedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    MaturityDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RemittanceReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    BouncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    BounceReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RefundedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RefundReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "bil",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportDeliveries",
                schema: "rpt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    RecipientKind = table.Column<int>(type: "int", nullable: false),
                    RecipientIdentifier = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    RecipientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PrescriberId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HandedOverByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BearerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BearerCin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AuthorizingConsentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportDeliveries_LabReports_LabReportId",
                        column: x => x.LabReportId,
                        principalSchema: "rpt",
                        principalTable: "LabReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CorrectiveActions",
                schema: "qc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NonConformityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    DescriptionFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DescriptionAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DescriptionEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResponsibleUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CompletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EvidenceAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EvidenceEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrectiveActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CorrectiveActions_NonConformities_NonConformityId",
                        column: x => x.NonConformityId,
                        principalSchema: "qc",
                        principalTable: "NonConformities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatientAlerts",
                schema: "pat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    MessageFr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    MessageAr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MessageEn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientAlerts_Patients_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "pat",
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatientConsents",
                schema: "pat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    GivenAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CollectedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SignedByCin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SignedByRelationship = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    PresentedInLanguage = table.Column<int>(type: "int", nullable: false),
                    FormVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    EvidenceDocument = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    EvidenceDocumentContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    WithdrawnByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WithdrawalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientConsents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientConsents_Patients_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "pat",
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatientCoverages",
                schema: "pat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    InsuredPersonName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    InsuredPersonCin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Relationship = table.Column<int>(type: "int", nullable: false),
                    EmployerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    ReimbursementRateOverride = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    IsChronicDiseaseScheme = table.Column<bool>(type: "bit", nullable: false),
                    PriorAgreementNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    PriorAgreementValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    DirectBillingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientCoverages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientCoverages_Patients_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "pat",
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PatientCoverages_Payers_PayerId",
                        column: x => x.PayerId,
                        principalSchema: "pat",
                        principalTable: "Payers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LabOrders",
                schema: "ord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccessionNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrescriberId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrescriberFreeText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RegisteredByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrescriptionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PrescriptionScan = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    PrescriptionScanContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    WardOrUnit = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    RoomOrBed = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ClinicalInformation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DiagnosisCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CurrentTreatment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PatientFasted = table.Column<bool>(type: "bit", nullable: true),
                    DeclaredFastingHours = table.Column<int>(type: "int", nullable: true),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PatientCoverageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReimbursementRateAtRegistration = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PriorAgreementNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DiscountPercent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    DiscountReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstCollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    AllReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    FullyValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    FirstReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ReportLanguage = table.Column<int>(type: "int", nullable: false),
                    InternalNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabOrders_Patients_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "pat",
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LabOrders_Prescribers_PrescriberId",
                        column: x => x.PrescriberId,
                        principalSchema: "ord",
                        principalTable: "Prescribers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLines",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    QuantityOrdered = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    QuantityReceived = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    UnitPriceMad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalSchema: "inv",
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                schema: "sec",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "sec",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SampleAliquots",
                schema: "smp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentSampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    VolumeMl = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    StorageLocation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SampleAliquots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SampleAliquots_Samples_ParentSampleId",
                        column: x => x.ParentSampleId,
                        principalSchema: "smp",
                        principalTable: "Samples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SampleEvents",
                schema: "smp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TemperatureCelsius = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Workstation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SampleEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SampleEvents_Samples_SampleId",
                        column: x => x.SampleId,
                        principalSchema: "smp",
                        principalTable: "Samples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisTests",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ShortNameFr = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ShortNameAr = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ShortNameEn = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SearchAliases = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true, collation: "Latin1_General_100_CI_AI_SC_UTF8"),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpecimenTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContainerTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequiredVolumeMl = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    LoincCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    NationalActCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    MethodFr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MethodAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MethodEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReagentReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsAccredited = table.Column<bool>(type: "bit", nullable: false),
                    IsSubcontracted = table.Column<bool>(type: "bit", nullable: false),
                    SubcontractorSiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RoutineTurnaroundHours = table.Column<int>(type: "int", nullable: false),
                    UrgentTurnaroundMinutes = table.Column<int>(type: "int", nullable: false),
                    IsAvailableUrgently = table.Column<bool>(type: "bit", nullable: false),
                    ScheduledDaysCsv = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    FastingHoursOverride = table.Column<int>(type: "int", nullable: true),
                    StabilityAtRoomTemperatureHours = table.Column<int>(type: "int", nullable: true),
                    StabilityRefrigeratedHours = table.Column<int>(type: "int", nullable: true),
                    ClinicalInfoPromptFr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClinicalInfoPromptAr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClinicalInfoPromptEn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConsentRequirement = table.Column<int>(type: "int", nullable: false),
                    IsNotifiableDisease = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisTests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalysisTests_ContainerTypes_ContainerTypeId",
                        column: x => x.ContainerTypeId,
                        principalSchema: "cat",
                        principalTable: "ContainerTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisTests_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "cat",
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisTests_SpecimenTypes_SpecimenTypeId",
                        column: x => x.SpecimenTypeId,
                        principalSchema: "cat",
                        principalTable: "SpecimenTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReagentLots",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    QuantityReceived = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    QuantityRemaining = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ReceivedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryNoteReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SupplierInvoiceReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    UnitPriceMad = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    OpenedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    StabilityAfterOpeningDays = table.Column<int>(type: "int", nullable: true),
                    StorageLocation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsQuarantined = table.Column<bool>(type: "bit", nullable: false),
                    ReleasedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReleasedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcceptanceEvidence = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsRejected = table.Column<bool>(type: "bit", nullable: false),
                    RejectionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsRecalled = table.Column<bool>(type: "bit", nullable: false),
                    RecalledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    RecallReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReagentLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReagentLots_StockItems_StockItemId",
                        column: x => x.StockItemId,
                        principalSchema: "inv",
                        principalTable: "StockItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                schema: "sec",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "sec",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSiteAccess",
                schema: "sec",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsDefaultSite = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSiteAccess", x => new { x.UserId, x.SiteId });
                    table.ForeignKey(
                        name: "FK_UserSiteAccess_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LabOrderItems",
                schema: "ord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisTestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestPanelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AddedByReflexRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChargedPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ReferenceTariffBase = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    BilledLetterKey = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: true),
                    BilledCoefficient = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    BilledActCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PayerShare = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PatientShare = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsSubcontracted = table.Column<bool>(type: "bit", nullable: false),
                    SubcontractorSiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SentToSubcontractorAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    SubcontractorReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ResultEnteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    TechnicallyValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    TechnicallyValidatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ValidatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RunCount = table.Column<int>(type: "int", nullable: false),
                    RepeatReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabOrderItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabOrderItems_AnalysisTests_AnalysisTestId",
                        column: x => x.AnalysisTestId,
                        principalSchema: "cat",
                        principalTable: "AnalysisTests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LabOrderItems_LabOrders_LabOrderId",
                        column: x => x.LabOrderId,
                        principalSchema: "ord",
                        principalTable: "LabOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tariffs",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisTestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    LetterKey = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Coefficient = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    LetterKeyValue = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsVatExempt = table.Column<bool>(type: "bit", nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tariffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tariffs_AnalysisTests_AnalysisTestId",
                        column: x => x.AnalysisTestId,
                        principalSchema: "cat",
                        principalTable: "AnalysisTests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestPanelItems",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestPanelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisTestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsOptional = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestPanelItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestPanelItems_AnalysisTests_AnalysisTestId",
                        column: x => x.AnalysisTestId,
                        principalSchema: "cat",
                        principalTable: "AnalysisTests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TestPanelItems_TestPanels_TestPanelId",
                        column: x => x.TestPanelId,
                        principalSchema: "cat",
                        principalTable: "TestPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestParameters",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisTestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    NameFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ShortNameFr = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ShortNameAr = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ShortNameEn = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ValueType = table.Column<int>(type: "int", nullable: false),
                    LoincCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Decimals = table.Column<int>(type: "int", nullable: false),
                    MeasuringRangeLow = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    MeasuringRangeHigh = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    TechnicalLimitLow = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    TechnicalLimitHigh = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CriticalLow = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CriticalHigh = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    AnalyticalCvPercent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    DeltaCheckPercent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    DeltaCheckWindowDays = table.Column<int>(type: "int", nullable: true),
                    CalculationFormula = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsInternalOnly = table.Column<bool>(type: "bit", nullable: false),
                    AlwaysShowOnReport = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IndentLevel = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestParameters_AnalysisTests_AnalysisTestId",
                        column: x => x.AnalysisTestId,
                        principalSchema: "cat",
                        principalTable: "AnalysisTests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TestParameters_Units_UnitId",
                        column: x => x.UnitId,
                        principalSchema: "cat",
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CodedResultOptions",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_CS_AS_SC_UTF8"),
                    LabelFr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    LabelAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LabelEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsAbnormal = table.Column<bool>(type: "bit", nullable: false),
                    IsCritical = table.Column<bool>(type: "bit", nullable: false),
                    IsExpectedNormal = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodedResultOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CodedResultOptions_TestParameters_TestParameterId",
                        column: x => x.TestParameterId,
                        principalSchema: "cat",
                        principalTable: "TestParameters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceRanges",
                schema: "cat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppliesToGender = table.Column<int>(type: "int", nullable: false),
                    AgeFromDays = table.Column<int>(type: "int", nullable: true),
                    AgeToDays = table.Column<int>(type: "int", nullable: true),
                    AppliesToPregnancy = table.Column<bool>(type: "bit", nullable: true),
                    PregnancyTrimester = table.Column<int>(type: "int", nullable: true),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LowerLimit = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    UpperLimit = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    DisplayTextFr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DisplayTextAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DisplayTextEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExpectedCodedValue = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    IsDecisionThreshold = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LastReviewedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceRanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferenceRanges_TestParameters_TestParameterId",
                        column: x => x.TestParameterId,
                        principalSchema: "cat",
                        principalTable: "TestParameters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestResults",
                schema: "res",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabOrderItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    NumericValue = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    ValueOperator = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    TextValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CodedValue = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    TitreDenominator = table.Column<int>(type: "int", nullable: true),
                    RawInstrumentValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DilutionFactor = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Interpretation = table.Column<int>(type: "int", nullable: false),
                    Flags = table.Column<int>(type: "int", nullable: false),
                    AppliedReferenceRangeText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AppliedReferenceRangeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppliedLowerLimit = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    AppliedUpperLimit = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    AppliedUnitSymbol = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    AppliedDecimals = table.Column<int>(type: "int", nullable: false),
                    PreviousNumericValue = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PreviousResultDate = table.Column<DateOnly>(type: "date", nullable: true),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReagentLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CalibrationRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CoveringQcRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MeasuredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    EnteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    EnteredByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsAutoTransmitted = table.Column<bool>(type: "bit", nullable: false),
                    TechnicallyValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    TechnicallyValidatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ValidatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TechnicalComment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BiologistCommentFr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BiologistCommentAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BiologistCommentEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PrintTechnicalComment = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestResults_TestParameters_TestParameterId",
                        column: x => x.TestParameterId,
                        principalSchema: "cat",
                        principalTable: "TestParameters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResultRevisions",
                schema: "res",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    RevisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    RevisedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PreviousNumericValue = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PreviousValueOperator = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    PreviousTextValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PreviousCodedValue = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    PreviousTitreDenominator = table.Column<int>(type: "int", nullable: true),
                    PreviousInterpretation = table.Column<int>(type: "int", nullable: false),
                    WasValidated = table.Column<bool>(type: "bit", nullable: false),
                    PreviousValidatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PreviousValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResultRevisions_TestResults_TestResultId",
                        column: x => x.TestResultId,
                        principalSchema: "res",
                        principalTable: "TestResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisTests_ActCode",
                schema: "cat",
                table: "AnalysisTests",
                column: "NationalActCode");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisTests_ContainerTypeId",
                schema: "cat",
                table: "AnalysisTests",
                column: "ContainerTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisTests_DepartmentId",
                schema: "cat",
                table: "AnalysisTests",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisTests_SpecimenTypeId",
                schema: "cat",
                table: "AnalysisTests",
                column: "SpecimenTypeId");

            migrationBuilder.CreateIndex(
                name: "UX_AnalysisTests_Code",
                schema: "cat",
                table: "AnalysisTests",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Correlation",
                schema: "aud",
                table: "AuditEntries",
                column: "CorrelationId",
                filter: "[CorrelationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Entity",
                schema: "aud",
                table: "AuditEntries",
                columns: new[] { "EntityName", "EntityId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Patient",
                schema: "aud",
                table: "AuditEntries",
                columns: new[] { "PatientId", "OccurredAt" },
                filter: "[PatientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_User",
                schema: "aud",
                table: "AuditEntries",
                columns: new[] { "UserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CalibrationRecords_Instrument_Parameter",
                schema: "eqp",
                table: "CalibrationRecords",
                columns: new[] { "InstrumentId", "TestParameterId", "PerformedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_CashSessions_OpenPerOperator",
                schema: "bil",
                table: "CashSessions",
                columns: new[] { "OperatorUserId", "Status" },
                unique: true,
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_CodedResultOptions_Parameter_Code",
                schema: "cat",
                table: "CodedResultOptions",
                columns: new[] { "TestParameterId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ContainerTypes_Code",
                schema: "cat",
                table: "ContainerTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ControlledDocuments_Reference",
                schema: "qc",
                table: "ControlledDocuments",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorrectiveActions_NonConformityId",
                schema: "qc",
                table: "CorrectiveActions",
                column: "NonConformityId");

            migrationBuilder.CreateIndex(
                name: "IX_CorrectiveActions_Owner",
                schema: "qc",
                table: "CorrectiveActions",
                columns: new[] { "ResponsibleUserId", "Status", "TargetDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CriticalNotifications_Pending",
                schema: "res",
                table: "CriticalResultNotifications",
                columns: new[] { "SiteId", "Outcome", "AttemptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CriticalNotifications_Result",
                schema: "res",
                table: "CriticalResultNotifications",
                column: "TestResultId");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLog_BreakGlass",
                schema: "aud",
                table: "DataAccessLog",
                column: "IsBreakGlass",
                filter: "[IsBreakGlass] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLog_Patient",
                schema: "aud",
                table: "DataAccessLog",
                columns: new[] { "PatientId", "OccurredAt" },
                filter: "[PatientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLog_User",
                schema: "aud",
                table: "DataAccessLog",
                columns: new[] { "UserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Departments_Code",
                schema: "cat",
                table: "Departments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DocumentAcks_Document_User_Version",
                schema: "qc",
                table: "DocumentAcknowledgements",
                columns: new[] { "ControlledDocumentId", "UserId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DocumentVersions_Document_Version",
                schema: "qc",
                table: "DocumentVersions",
                columns: new[] { "ControlledDocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DocumentVersions_OneEffective",
                schema: "qc",
                table: "DocumentVersions",
                column: "ControlledDocumentId",
                unique: true,
                filter: "[Status] = 4");

            migrationBuilder.CreateIndex(
                name: "IX_EqaParticipations_Site_Round",
                schema: "qc",
                table: "EqaParticipations",
                columns: new[] { "SiteId", "RoundDate", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "UX_InstrumentInterfaces_Instrument",
                schema: "eqp",
                table: "InstrumentInterfaces",
                column: "InstrumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentMessageLog_Interface_Occurred",
                schema: "eqp",
                table: "InstrumentMessageLog",
                columns: new[] { "InstrumentInterfaceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentMessageLog_Status",
                schema: "eqp",
                table: "InstrumentMessageLog",
                columns: new[] { "Status", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_Site_Status",
                schema: "eqp",
                table: "Instruments",
                columns: new[] { "SiteId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Instruments_Code",
                schema: "eqp",
                table: "Instruments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_InstrumentTestMappings_Interface_Code",
                schema: "eqp",
                table: "InstrumentTestMappings",
                columns: new[] { "InstrumentInterfaceId", "InstrumentTestCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_OrderItem",
                schema: "bil",
                table: "InvoiceLines",
                column: "LabOrderItemId",
                filter: "[LabOrderItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_InvoiceLines_Invoice_Line",
                schema: "bil",
                table: "InvoiceLines",
                columns: new[] { "InvoiceId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Patient",
                schema: "bil",
                table: "Invoices",
                columns: new[] { "PatientId", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Receivables",
                schema: "bil",
                table: "Invoices",
                columns: new[] { "PayerId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Site_Date",
                schema: "bil",
                table: "Invoices",
                columns: new[] { "SiteId", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "UX_Invoices_Site_Kind_Number",
                schema: "bil",
                table: "Invoices",
                columns: new[] { "SiteId", "Kind", "Number" },
                unique: true,
                filter: "[Status] <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_LabOrderItems_AnalysisTestId",
                schema: "ord",
                table: "LabOrderItems",
                column: "AnalysisTestId");

            migrationBuilder.CreateIndex(
                name: "IX_LabOrderItems_DepartmentWorklist",
                schema: "ord",
                table: "LabOrderItems",
                columns: new[] { "DepartmentId", "Status", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_LabOrderItems_Order_Analysis",
                schema: "ord",
                table: "LabOrderItems",
                columns: new[] { "LabOrderId", "AnalysisTestId" });

            migrationBuilder.CreateIndex(
                name: "IX_LabOrderItems_Sample",
                schema: "ord",
                table: "LabOrderItems",
                column: "SampleId",
                filter: "[SampleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LabOrders_Overdue",
                schema: "ord",
                table: "LabOrders",
                column: "DueAt",
                filter: "[DueAt] IS NOT NULL AND [Status] < 7");

            migrationBuilder.CreateIndex(
                name: "IX_LabOrders_Patient_History",
                schema: "ord",
                table: "LabOrders",
                columns: new[] { "PatientId", "RegisteredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LabOrders_PrescriberId",
                schema: "ord",
                table: "LabOrders",
                column: "PrescriberId");

            migrationBuilder.CreateIndex(
                name: "IX_LabOrders_Worklist",
                schema: "ord",
                table: "LabOrders",
                columns: new[] { "SiteId", "Status", "Priority", "RegisteredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_LabOrders_AccessionNumber",
                schema: "ord",
                table: "LabOrders",
                column: "AccessionNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_LabReports_AccessToken",
                schema: "rpt",
                table: "LabReports",
                column: "AccessToken",
                unique: true,
                filter: "[AccessToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_LabReports_Order_Version",
                schema: "rpt",
                table: "LabReports",
                columns: new[] { "LabOrderId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRecords_Instrument_Date",
                schema: "eqp",
                table: "MaintenanceRecords",
                columns: new[] { "InstrumentId", "PerformedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_NonConformities_Analysis",
                schema: "qc",
                table: "NonConformities",
                columns: new[] { "Category", "Severity", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NonConformities_Open",
                schema: "qc",
                table: "NonConformities",
                columns: new[] { "SiteId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "UX_NonConformities_Reference",
                schema: "qc",
                table: "NonConformities",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_NumberSequences_Scope",
                schema: "cfg",
                table: "NumberSequences",
                columns: new[] { "Purpose", "SiteId", "Year" },
                unique: true,
                filter: "[SiteId] IS NOT NULL AND [Year] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordHistory_User",
                schema: "sec",
                table: "PasswordHistory",
                columns: new[] { "UserId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientAlerts_PatientId",
                schema: "pat",
                table: "PatientAlerts",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientConsents_Patient_Purpose",
                schema: "pat",
                table: "PatientConsents",
                columns: new[] { "PatientId", "Purpose", "GivenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientCoverages_PatientId",
                schema: "pat",
                table: "PatientCoverages",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientCoverages_Payer_Member",
                schema: "pat",
                table: "PatientCoverages",
                columns: new[] { "PayerId", "MembershipNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Patients_Cin",
                schema: "pat",
                table: "Patients",
                column: "Cin",
                filter: "[Cin] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_Mobile",
                schema: "pat",
                table: "Patients",
                column: "MobilePhone",
                filter: "[MobilePhone] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_Retention",
                schema: "pat",
                table: "Patients",
                column: "RetentionUntil",
                filter: "[RetentionUntil] IS NOT NULL AND [IsAnonymized] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_Search",
                schema: "pat",
                table: "Patients",
                columns: new[] { "SiteId", "SearchKey" });

            migrationBuilder.CreateIndex(
                name: "UX_Patients_RecordNumber",
                schema: "pat",
                table: "Patients",
                column: "MedicalRecordNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Payers_Code",
                schema: "pat",
                table: "Payers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CashSession",
                schema: "bil",
                table: "Payments",
                column: "CashSessionId",
                filter: "[CashSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_InvoiceId",
                schema: "bil",
                table: "Payments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Site_Date_Method",
                schema: "bil",
                table: "Payments",
                columns: new[] { "SiteId", "PaymentDate", "Method" });

            migrationBuilder.CreateIndex(
                name: "IX_Prescribers_Inpe",
                schema: "ord",
                table: "Prescribers",
                column: "Inpe",
                filter: "[Inpe] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Prescribers_Search",
                schema: "ord",
                table: "Prescribers",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrderLines_Order_Line",
                schema: "inv",
                table: "PurchaseOrderLines",
                columns: new[] { "PurchaseOrderId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_Outstanding",
                schema: "inv",
                table: "PurchaseOrders",
                columns: new[] { "SiteId", "Status", "ExpectedDeliveryDate" });

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrders_Number",
                schema: "inv",
                table: "PurchaseOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QcMaterials_Lot_Level",
                schema: "qc",
                table: "QcMaterials",
                columns: new[] { "LotNumber", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QcResults_Review",
                schema: "qc",
                table: "QcResults",
                columns: new[] { "SiteId", "Outcome", "ReviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QcResults_Run",
                schema: "qc",
                table: "QcResults",
                column: "QcRunId",
                filter: "[QcRunId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QcResults_Target_Measured",
                schema: "qc",
                table: "QcResults",
                columns: new[] { "QcTargetId", "MeasuredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_QcTargets_Material_Parameter_Instrument",
                schema: "qc",
                table: "QcTargets",
                columns: new[] { "QcMaterialId", "TestParameterId", "InstrumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReagentLots_Item_Expiry",
                schema: "inv",
                table: "ReagentLots",
                columns: new[] { "StockItemId", "ExpiryDate" });

            migrationBuilder.CreateIndex(
                name: "UX_ReagentLots_Item_Lot",
                schema: "inv",
                table: "ReagentLots",
                columns: new[] { "StockItemId", "LotNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceRanges_Parameter",
                schema: "cat",
                table: "ReferenceRanges",
                columns: new[] { "TestParameterId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ReflexRules_Trigger",
                schema: "cat",
                table: "ReflexRules",
                columns: new[] { "TriggerParameterId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveries_Report",
                schema: "rpt",
                table: "ReportDeliveries",
                columns: new[] { "LabReportId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_ResultRevisions_Result_Number",
                schema: "res",
                table: "ResultRevisions",
                columns: new[] { "TestResultId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Roles_Code",
                schema: "sec",
                table: "Roles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SampleAliquots_ParentSampleId",
                schema: "smp",
                table: "SampleAliquots",
                column: "ParentSampleId");

            migrationBuilder.CreateIndex(
                name: "UX_SampleAliquots_Barcode",
                schema: "smp",
                table: "SampleAliquots",
                column: "Barcode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SampleEvents_Sample",
                schema: "smp",
                table: "SampleEvents",
                columns: new[] { "SampleId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Samples_KeepUntil",
                schema: "smp",
                table: "Samples",
                column: "KeepUntil",
                filter: "[KeepUntil] IS NOT NULL AND [Status] <> 9");

            migrationBuilder.CreateIndex(
                name: "IX_Samples_Order",
                schema: "smp",
                table: "Samples",
                columns: new[] { "LabOrderId", "SequenceInOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Samples_Status",
                schema: "smp",
                table: "Samples",
                columns: new[] { "SiteId", "Status", "CollectedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Samples_Barcode",
                schema: "smp",
                table: "Samples",
                column: "Barcode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Kind",
                schema: "aud",
                table: "SecurityEvents",
                columns: new[] { "Kind", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_UserName",
                schema: "aud",
                table: "SecurityEvents",
                columns: new[] { "AttemptedUserName", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Settings_Key_Site",
                schema: "cfg",
                table: "Settings",
                columns: new[] { "Key", "SiteId" },
                unique: true,
                filter: "[SiteId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Sites_Code",
                schema: "org",
                table: "Sites",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Sites_Ice",
                schema: "org",
                table: "Sites",
                column: "Ice",
                unique: true,
                filter: "[Ice] IS NOT NULL AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_SpecimenTypes_Code",
                schema: "cat",
                table: "SpecimenTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_StockItems_Site_Code",
                schema: "inv",
                table: "StockItems",
                columns: new[] { "SiteId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_Item_Occurred",
                schema: "inv",
                table: "StockMovements",
                columns: new[] { "StockItemId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_Lot",
                schema: "inv",
                table: "StockMovements",
                column: "ReagentLotId",
                filter: "[ReagentLotId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Suppliers_Code",
                schema: "inv",
                table: "Suppliers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tariffs_Analysis_Payer_From",
                schema: "cat",
                table: "Tariffs",
                columns: new[] { "AnalysisTestId", "PayerId", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_TestPanelItems_AnalysisTestId",
                schema: "cat",
                table: "TestPanelItems",
                column: "AnalysisTestId");

            migrationBuilder.CreateIndex(
                name: "UX_TestPanelItems_Panel_Analysis",
                schema: "cat",
                table: "TestPanelItems",
                columns: new[] { "TestPanelId", "AnalysisTestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TestPanels_Code",
                schema: "cat",
                table: "TestPanels",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestParameters_UnitId",
                schema: "cat",
                table: "TestParameters",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "UX_TestParameters_Analysis_Code",
                schema: "cat",
                table: "TestParameters",
                columns: new[] { "AnalysisTestId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestResults_Flagged",
                schema: "res",
                table: "TestResults",
                column: "Flags",
                filter: "[Flags] <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_TestResults_Order",
                schema: "res",
                table: "TestResults",
                columns: new[] { "LabOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TestResults_Parameter_History",
                schema: "res",
                table: "TestResults",
                columns: new[] { "TestParameterId", "ValidatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TestResults_Validation",
                schema: "res",
                table: "TestResults",
                columns: new[] { "SiteId", "DepartmentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_TestResults_Item_Parameter",
                schema: "res",
                table: "TestResults",
                columns: new[] { "LabOrderItemId", "TestParameterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Units_Code",
                schema: "cat",
                table: "Units",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                schema: "sec",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "UX_Users_UserName",
                schema: "sec",
                table: "Users",
                column: "UserName",
                unique: true);

            ApplyAppendOnlyProtection(migrationBuilder);
        }

        /// <summary>
        /// Makes the traceability tables append-only at the database level.
        /// <para>
        /// Hand-written because EF cannot express a trigger, and placed in the migration rather than
        /// in application startup code because it is schema. The only copy of this used to run from
        /// <c>DatabaseInitializer</c>'s <c>EnsureCreated</c> branch — so a site deploying the way
        /// <c>docs/DEPLOYMENT.md</c> tells it to, by <c>dotnet ef database update</c>, would have
        /// received all 65 tables and none of the audit protection. The control the architecture
        /// notes describe would have been missing from precisely the installations that get audited.
        /// </para>
        /// <para>
        /// The application never updates or deletes these rows, but "the application never does it"
        /// is not a control an auditor can test — a DBA holding the application's own credentials
        /// could. An <c>INSTEAD OF</c> trigger refuses the operation outright. A deliberate
        /// correction is still possible: a member of <c>db_owner</c> can disable the trigger, and
        /// that act is itself recorded in the SQL Server error log.
        /// </para>
        /// <para>
        /// Re-applied by hand after every <c>migrations remove</c> / <c>migrations add</c> cycle.
        /// <c>DatabaseInitializer.GetUnprotectedTraceabilityTablesAsync</c> is the check that it did
        /// not get lost in one.
        /// </para>
        /// </summary>
        private static void ApplyAppendOnlyProtection(MigrationBuilder migrationBuilder)
        {
            var tables = new (string Schema, string Table, int ErrorNumber, string Message)[]
            {
                ("aud", "AuditEntries",    51001, "The audit trail is append-only and cannot be modified."),
                ("aud", "DataAccessLog",   51002, "The data access log is append-only and cannot be modified."),
                ("aud", "SecurityEvents",  51003, "The security event log is append-only and cannot be modified."),
                ("res", "ResultRevisions", 51004, "A result revision is a permanent record and cannot be modified.")
            };

            foreach (var (schema, table, errorNumber, message) in tables)
            {
                // Wrapped in EXEC because CREATE TRIGGER must be the first statement of its batch,
                // and a migration sends the whole of Up as one batch.
                migrationBuilder.Sql($"""
                    IF OBJECT_ID('{schema}.TR_{table}_NoUpdate', 'TR') IS NULL
                    EXEC('CREATE TRIGGER [{schema}].[TR_{table}_NoUpdate] ON [{schema}].[{table}]
                          INSTEAD OF UPDATE, DELETE AS
                          BEGIN
                              THROW {errorNumber}, ''{message}'', 1;
                          END');
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropped ahead of their tables. Dropping a table takes its triggers with it, so this is
            // belt and braces — but it keeps Down correct if the generated drop order ever changes,
            // and it makes the revert explicit about removing a stated regulatory control.
            foreach (var trigger in new[]
            {
                "[aud].[TR_AuditEntries_NoUpdate]",
                "[aud].[TR_DataAccessLog_NoUpdate]",
                "[aud].[TR_SecurityEvents_NoUpdate]",
                "[res].[TR_ResultRevisions_NoUpdate]"
            })
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {trigger};");
            }

            migrationBuilder.DropTable(
                name: "AuditEntries",
                schema: "aud");

            migrationBuilder.DropTable(
                name: "CalibrationRecords",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "CashSessions",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "CodedResultOptions",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "CorrectiveActions",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "CriticalResultNotifications",
                schema: "res");

            migrationBuilder.DropTable(
                name: "DataAccessLog",
                schema: "aud");

            migrationBuilder.DropTable(
                name: "DocumentAcknowledgements",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "DocumentVersions",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "EqaParticipations",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "InstrumentMessageLog",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "InstrumentTestMappings",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "InvoiceLines",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "LabOrderItems",
                schema: "ord");

            migrationBuilder.DropTable(
                name: "MaintenanceRecords",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "NumberSequences",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PasswordHistory",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "PatientAlerts",
                schema: "pat");

            migrationBuilder.DropTable(
                name: "PatientConsents",
                schema: "pat");

            migrationBuilder.DropTable(
                name: "PatientCoverages",
                schema: "pat");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLines",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "QcMaterials",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "QcResults",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "QcTargets",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "ReagentLots",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "ReferenceRanges",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "ReflexRules",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "ReportDeliveries",
                schema: "rpt");

            migrationBuilder.DropTable(
                name: "ResultRevisions",
                schema: "res");

            migrationBuilder.DropTable(
                name: "RolePermissions",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "SampleAliquots",
                schema: "smp");

            migrationBuilder.DropTable(
                name: "SampleEvents",
                schema: "smp");

            migrationBuilder.DropTable(
                name: "SecurityEvents",
                schema: "aud");

            migrationBuilder.DropTable(
                name: "Settings",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "Sites",
                schema: "org");

            migrationBuilder.DropTable(
                name: "StockMovements",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "Suppliers",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "Tariffs",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "TestPanelItems",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "UserRoles",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "UserSiteAccess",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "NonConformities",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "ControlledDocuments",
                schema: "qc");

            migrationBuilder.DropTable(
                name: "InstrumentInterfaces",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "LabOrders",
                schema: "ord");

            migrationBuilder.DropTable(
                name: "Instruments",
                schema: "eqp");

            migrationBuilder.DropTable(
                name: "Payers",
                schema: "pat");

            migrationBuilder.DropTable(
                name: "Invoices",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "PurchaseOrders",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "StockItems",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "LabReports",
                schema: "rpt");

            migrationBuilder.DropTable(
                name: "TestResults",
                schema: "res");

            migrationBuilder.DropTable(
                name: "Samples",
                schema: "smp");

            migrationBuilder.DropTable(
                name: "TestPanels",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "Patients",
                schema: "pat");

            migrationBuilder.DropTable(
                name: "Prescribers",
                schema: "ord");

            migrationBuilder.DropTable(
                name: "TestParameters",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "AnalysisTests",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "Units",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "ContainerTypes",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "Departments",
                schema: "cat");

            migrationBuilder.DropTable(
                name: "SpecimenTypes",
                schema: "cat");
        }
    }
}
