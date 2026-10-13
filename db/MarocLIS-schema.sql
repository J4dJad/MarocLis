IF OBJECT_ID(N'[cfg].[__MigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'cfg') IS NULL EXEC(N'CREATE SCHEMA [cfg];');
    CREATE TABLE [cfg].[__MigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___MigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'cat') IS NULL EXEC(N'CREATE SCHEMA [cat];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'aud') IS NULL EXEC(N'CREATE SCHEMA [aud];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'eqp') IS NULL EXEC(N'CREATE SCHEMA [eqp];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'bil') IS NULL EXEC(N'CREATE SCHEMA [bil];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'qc') IS NULL EXEC(N'CREATE SCHEMA [qc];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'res') IS NULL EXEC(N'CREATE SCHEMA [res];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'ord') IS NULL EXEC(N'CREATE SCHEMA [ord];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'rpt') IS NULL EXEC(N'CREATE SCHEMA [rpt];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'cfg') IS NULL EXEC(N'CREATE SCHEMA [cfg];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'sec') IS NULL EXEC(N'CREATE SCHEMA [sec];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'pat') IS NULL EXEC(N'CREATE SCHEMA [pat];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'inv') IS NULL EXEC(N'CREATE SCHEMA [inv];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'smp') IS NULL EXEC(N'CREATE SCHEMA [smp];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'org') IS NULL EXEC(N'CREATE SCHEMA [org];');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [aud].[AuditEntries] (
        [Id] uniqueidentifier NOT NULL,
        [EntityName] nvarchar(100) NOT NULL,
        [EntityId] uniqueidentifier NOT NULL,
        [Operation] int NOT NULL,
        [OccurredAt] datetimeoffset(3) NOT NULL,
        [UserId] uniqueidentifier NULL,
        [UserName] nvarchar(60) NULL,
        [Workstation] nvarchar(100) NULL,
        [OsUser] nvarchar(100) NULL,
        [SiteId] uniqueidentifier NULL,
        [CorrelationId] uniqueidentifier NULL,
        [Changes] nvarchar(max) NULL,
        [ChangedProperties] nvarchar(2000) NULL,
        [AggregateId] uniqueidentifier NULL,
        [AggregateName] nvarchar(100) NULL,
        [Reason] nvarchar(1000) NULL,
        [PatientId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_AuditEntries] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [bil].[CashSessions] (
        [Id] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [OperatorUserId] uniqueidentifier NOT NULL,
        [Workstation] nvarchar(100) NULL,
        [OpenedAt] datetimeoffset(3) NOT NULL,
        [ClosedAt] datetimeoffset(3) NULL,
        [ClosedByUserId] uniqueidentifier NULL,
        [Status] int NOT NULL,
        [OpeningFloat] decimal(18,6) NOT NULL,
        [ExpectedCashReceipts] decimal(18,6) NOT NULL,
        [CashRefunds] decimal(18,6) NOT NULL,
        [CashWithdrawals] decimal(18,6) NOT NULL,
        [CountedCash] decimal(18,6) NULL,
        [VarianceExplanation] nvarchar(1000) NULL,
        [ChequeReceipts] decimal(18,6) NOT NULL,
        [CardReceipts] decimal(18,6) NOT NULL,
        [TransferReceipts] decimal(18,6) NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_CashSessions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[ContainerTypes] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [CapColourHex] nvarchar(9) NOT NULL,
        [AdditiveFr] nvarchar(200) NULL,
        [AdditiveAr] nvarchar(200) NULL,
        [AdditiveEn] nvarchar(200) NULL,
        [NominalVolumeMl] decimal(18,6) NULL,
        [MinimumVolumeMl] decimal(18,6) NULL,
        [DrawOrder] int NOT NULL,
        [RequiresProtectionFromLight] bit NOT NULL,
        [RequiresIceTransport] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_ContainerTypes] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[ControlledDocuments] (
        [Id] uniqueidentifier NOT NULL,
        [Reference] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [TitleFr] nvarchar(300) NOT NULL,
        [TitleAr] nvarchar(300) NULL,
        [TitleEn] nvarchar(300) NULL,
        [Kind] int NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [DepartmentId] uniqueidentifier NULL,
        [ProcessCode] nvarchar(30) NULL,
        [OwnerUserId] uniqueidentifier NULL,
        [ReviewPeriodMonths] int NOT NULL,
        [RequiresAcknowledgement] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_ControlledDocuments] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [res].[CriticalResultNotifications] (
        [Id] uniqueidentifier NOT NULL,
        [TestResultId] uniqueidentifier NOT NULL,
        [LabOrderId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [ReportedValue] nvarchar(100) NOT NULL,
        [ParameterName] nvarchar(200) NULL,
        [AttemptedAt] datetimeoffset(3) NOT NULL,
        [NotifiedByUserId] uniqueidentifier NOT NULL,
        [Channel] int NOT NULL,
        [ContactUsed] nvarchar(100) NULL,
        [PrescriberId] uniqueidentifier NULL,
        [RecipientName] nvarchar(200) NULL,
        [RecipientRole] nvarchar(100) NULL,
        [Outcome] int NOT NULL,
        [CompletedAt] datetimeoffset(3) NULL,
        [ReadBackConfirmed] bit NOT NULL,
        [ReadBackValue] nvarchar(100) NULL,
        [AttemptNumber] int NOT NULL,
        [EscalatedToUserId] uniqueidentifier NULL,
        [Notes] nvarchar(1000) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_CriticalResultNotifications] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [aud].[DataAccessLog] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [UserName] nvarchar(60) NULL,
        [Kind] int NOT NULL,
        [OccurredAt] datetimeoffset(3) NOT NULL,
        [PatientId] uniqueidentifier NULL,
        [LabOrderId] uniqueidentifier NULL,
        [LabReportId] uniqueidentifier NULL,
        [RecordCount] int NULL,
        [QueryDescription] nvarchar(1000) NULL,
        [Context] nvarchar(150) NULL,
        [Workstation] nvarchar(100) NULL,
        [SiteId] uniqueidentifier NULL,
        [IsBreakGlass] bit NOT NULL,
        [BreakGlassJustification] nvarchar(1000) NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_DataAccessLog] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[Departments] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(20) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [DisplayOrder] int NOT NULL,
        [IsInAccreditationScope] bit NOT NULL,
        [SupervisorUserId] uniqueidentifier NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Departments] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[EqaParticipations] (
        [Id] uniqueidentifier NOT NULL,
        [SchemeName] nvarchar(150) NOT NULL,
        [RoundReference] nvarchar(60) NOT NULL,
        [TestParameterId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [InstrumentId] uniqueidentifier NULL,
        [RoundDate] date NOT NULL,
        [SubmissionDeadline] date NULL,
        [SubmittedOn] date NULL,
        [ReportedValue] decimal(18,6) NULL,
        [AssignedValue] decimal(18,6) NULL,
        [PeerGroupMean] decimal(18,6) NULL,
        [PeerGroupSd] decimal(18,6) NULL,
        [ZScore] decimal(18,6) NULL,
        [BiasPercent] decimal(18,6) NULL,
        [Outcome] int NOT NULL,
        [NonConformityId] uniqueidentifier NULL,
        [InvestigationFr] nvarchar(2000) NULL,
        [InvestigationAr] nvarchar(2000) NULL,
        [InvestigationEn] nvarchar(2000) NULL,
        [ReviewedOn] date NULL,
        [ReviewedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_EqaParticipations] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [eqp].[InstrumentInterfaces] (
        [Id] uniqueidentifier NOT NULL,
        [InstrumentId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [Protocol] int NOT NULL,
        [Transport] int NOT NULL,
        [Direction] int NOT NULL,
        [IsEnabled] bit NOT NULL,
        [SerialPortName] nvarchar(20) NULL,
        [BaudRate] int NOT NULL,
        [DataBits] int NOT NULL,
        [Parity] nvarchar(10) NOT NULL,
        [StopBits] nvarchar(15) NOT NULL,
        [Handshake] nvarchar(20) NOT NULL,
        [Host] nvarchar(150) NULL,
        [TcpPort] int NULL,
        [ListenAsServer] bit NOT NULL,
        [InboundDirectory] nvarchar(300) NULL,
        [OutboundDirectory] nvarchar(300) NULL,
        [FilePattern] nvarchar(60) NULL,
        [ProcessedDirectory] nvarchar(300) NULL,
        [Encoding] nvarchar(30) NOT NULL,
        [AcknowledgementTimeoutSeconds] int NOT NULL,
        [MaxRetries] int NOT NULL,
        [AutoAcceptResults] bit NOT NULL,
        [RaiseIssueOnUnmappedCode] bit NOT NULL,
        [RouteControlResultsToQc] bit NOT NULL,
        [LastConnectedAt] datetimeoffset(3) NULL,
        [LastMessageAt] datetimeoffset(3) NULL,
        [LastError] nvarchar(1000) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_InstrumentInterfaces] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [eqp].[InstrumentMessageLog] (
        [Id] uniqueidentifier NOT NULL,
        [InstrumentInterfaceId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [Direction] int NOT NULL,
        [OccurredAt] datetimeoffset(3) NOT NULL,
        [RawContent] nvarchar(max) NOT NULL,
        [Status] int NOT NULL,
        [SpecimenIds] nvarchar(1000) NULL,
        [ResultCount] int NULL,
        [ImportedCount] int NULL,
        [Issues] nvarchar(4000) NULL,
        [ProcessedAt] datetimeoffset(3) NULL,
        [ReplayedAt] datetimeoffset(3) NULL,
        [ReplayedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_InstrumentMessageLog] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [eqp].[Instruments] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [Kind] int NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [DepartmentId] uniqueidentifier NULL,
        [Manufacturer] nvarchar(150) NULL,
        [Model] nvarchar(150) NULL,
        [SerialNumber] nvarchar(100) NULL,
        [SoftwareVersion] nvarchar(60) NULL,
        [PurchaseDate] date NULL,
        [CommissioningDate] date NULL,
        [DecommissioningDate] date NULL,
        [SupplierId] uniqueidentifier NULL,
        [MaintenanceContractReference] nvarchar(100) NULL,
        [MaintenanceContractValidUntil] date NULL,
        [SupportPhone] nvarchar(30) NULL,
        [Status] int NOT NULL,
        [Location] nvarchar(150) NULL,
        [PreventiveMaintenanceIntervalDays] int NULL,
        [LastPreventiveMaintenanceOn] date NULL,
        [CalibrationIntervalDays] int NULL,
        [LastCalibrationOn] date NULL,
        [VerificationIntervalDays] int NULL,
        [LastVerificationOn] date NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [LastStatusReason] nvarchar(500) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Instruments] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [bil].[Invoices] (
        [Id] uniqueidentifier NOT NULL,
        [Number] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [Status] int NOT NULL,
        [IssueDate] date NOT NULL,
        [IssuedAt] datetimeoffset(3) NULL,
        [IssuedByUserId] uniqueidentifier NOT NULL,
        [PatientId] uniqueidentifier NULL,
        [PayerId] uniqueidentifier NULL,
        [BilledToName] nvarchar(250) NOT NULL,
        [BilledToIce] nvarchar(15) NULL,
        [BilledToFiscalId] nvarchar(20) NULL,
        [BilledToAddress] nvarchar(400) NULL,
        [BilledToCity] nvarchar(100) NULL,
        [CoveredOrderNumbersCsv] nvarchar(4000) NULL,
        [GrossAmount] decimal(18,2) NOT NULL,
        [DiscountAmount] decimal(18,2) NOT NULL,
        [DiscountReason] nvarchar(300) NULL,
        [NetAmount] decimal(18,2) NOT NULL,
        [VatAmount] decimal(18,2) NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [PayerShareAmount] decimal(18,2) NOT NULL,
        [PatientShareAmount] decimal(18,2) NOT NULL,
        [PaidAmount] decimal(18,2) NOT NULL,
        [AmountInWords] nvarchar(500) NULL,
        [DueDate] date NULL,
        [CorrectsInvoiceId] uniqueidentifier NULL,
        [CorrectsInvoiceNumber] nvarchar(40) NULL,
        [CorrectionReason] nvarchar(500) NULL,
        [HasCreditNote] bit NOT NULL,
        [ClaimReference] nvarchar(60) NULL,
        [ClaimSubmittedOn] date NULL,
        [ClaimStatus] int NOT NULL,
        [ClaimRejectionReason] nvarchar(500) NULL,
        [BiologistInpe] nvarchar(20) NULL,
        [PrescriberInpe] nvarchar(20) NULL,
        [PrescriberName] nvarchar(200) NULL,
        [Notes] nvarchar(1000) NULL,
        [RenderedPdf] varbinary(max) NULL,
        [ContentHash] nvarchar(64) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Invoices] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [rpt].[LabReports] (
        [Id] uniqueidentifier NOT NULL,
        [LabOrderId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [VersionNumber] int NOT NULL,
        [Completeness] int NOT NULL,
        [Language] int NOT NULL,
        [ReleasedAt] datetimeoffset(3) NOT NULL,
        [ReleasedByBiologistUserId] uniqueidentifier NOT NULL,
        [SignatoryName] nvarchar(200) NULL,
        [SignatoryQualification] nvarchar(120) NULL,
        [SignatoryOrderNumber] nvarchar(40) NULL,
        [RenderedPdf] varbinary(max) NULL,
        [PageCount] int NULL,
        [ContentHash] nvarchar(64) NULL,
        [AccessToken] nvarchar(64) NULL,
        [AccessTokenExpiresAt] datetimeoffset(3) NULL,
        [SupersedesReportId] uniqueidentifier NULL,
        [SupersededByReportId] uniqueidentifier NULL,
        [AmendmentKind] int NOT NULL,
        [AmendmentReasonFr] nvarchar(1000) NULL,
        [AmendmentReasonAr] nvarchar(1000) NULL,
        [AmendmentReasonEn] nvarchar(1000) NULL,
        [AmendedParameterCodesCsv] nvarchar(1000) NULL,
        [ContainsSubcontractedResults] bit NOT NULL,
        [ContainsReservations] bit NOT NULL,
        [ContainsCriticalResults] bit NOT NULL,
        [AllResultsAccredited] bit NOT NULL,
        [ConclusionFr] nvarchar(4000) NULL,
        [ConclusionAr] nvarchar(4000) NULL,
        [ConclusionEn] nvarchar(4000) NULL,
        [IncludedOrderItemIdsCsv] nvarchar(4000) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_LabReports] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[NonConformities] (
        [Id] uniqueidentifier NOT NULL,
        [Reference] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [Category] int NOT NULL,
        [Severity] int NOT NULL,
        [Status] int NOT NULL,
        [DescriptionFr] nvarchar(2000) NOT NULL,
        [DescriptionAr] nvarchar(2000) NULL,
        [DescriptionEn] nvarchar(2000) NULL,
        [DetectedAt] datetimeoffset(3) NOT NULL,
        [DetectedByUserId] uniqueidentifier NOT NULL,
        [DetectionSource] int NOT NULL,
        [DepartmentId] uniqueidentifier NULL,
        [LabOrderId] uniqueidentifier NULL,
        [SampleId] uniqueidentifier NULL,
        [TestResultId] uniqueidentifier NULL,
        [InstrumentId] uniqueidentifier NULL,
        [ReagentLotId] uniqueidentifier NULL,
        [QcResultId] uniqueidentifier NULL,
        [AffectedReleasedResults] bit NOT NULL,
        [AffectedPatientCount] int NULL,
        [ClinicalImpactFr] nvarchar(2000) NULL,
        [ClinicalImpactAr] nvarchar(2000) NULL,
        [ClinicalImpactEn] nvarchar(2000) NULL,
        [AmendedReportsIssued] bit NOT NULL,
        [ImmediateActionFr] nvarchar(2000) NULL,
        [ImmediateActionAr] nvarchar(2000) NULL,
        [ImmediateActionEn] nvarchar(2000) NULL,
        [RootCauseFr] nvarchar(2000) NULL,
        [RootCauseAr] nvarchar(2000) NULL,
        [RootCauseEn] nvarchar(2000) NULL,
        [RootCause] int NULL,
        [IsRecurrence] bit NOT NULL,
        [PreviousOccurrenceId] uniqueidentifier NULL,
        [AssignedToUserId] uniqueidentifier NULL,
        [DueDate] date NULL,
        [ClosedAt] datetimeoffset(3) NULL,
        [ClosedByUserId] uniqueidentifier NULL,
        [EffectivenessFr] nvarchar(2000) NULL,
        [EffectivenessAr] nvarchar(2000) NULL,
        [EffectivenessEn] nvarchar(2000) NULL,
        [EffectivenessVerifiedOn] date NULL,
        [EffectivenessVerifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_NonConformities] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cfg].[NumberSequences] (
        [Id] uniqueidentifier NOT NULL,
        [Purpose] int NOT NULL,
        [SiteId] uniqueidentifier NULL,
        [Year] int NULL,
        [Format] nvarchar(100) NOT NULL,
        [NextValue] bigint NOT NULL,
        [LastAllocatedValue] bigint NOT NULL,
        [LastAllocatedAt] datetimeoffset(3) NULL,
        [IsClosed] bit NOT NULL,
        [ClosedAt] datetimeoffset(3) NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_NumberSequences] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [sec].[PasswordHistory] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [PasswordHash] nvarchar(200) NOT NULL,
        [ChangedAt] datetimeoffset(3) NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PasswordHistory] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [pat].[Patients] (
        [Id] uniqueidentifier NOT NULL,
        [MedicalRecordNumber] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [LastName] nvarchar(120) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NOT NULL,
        [FirstName] nvarchar(120) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NOT NULL,
        [LastNameArabic] nvarchar(120) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [FirstNameArabic] nvarchar(120) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [SearchKey] nvarchar(260) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NOT NULL,
        [Gender] int NOT NULL,
        [DateOfBirth] date NULL,
        [DeclaredAgeYears] int NULL,
        [PlaceOfBirth] nvarchar(120) NULL,
        [Cin] nvarchar(20) NULL,
        [PassportNumber] nvarchar(30) NULL,
        [NationalityCode] nvarchar(2) NOT NULL,
        [GuardianCin] nvarchar(20) NULL,
        [GuardianFullName] nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [GuardianRelationship] nvarchar(60) NULL,
        [AddressLine] nvarchar(400) NULL,
        [City] nvarchar(100) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [Region] int NOT NULL,
        [PostalCode] nvarchar(10) NULL,
        [MobilePhone] nvarchar(20) NULL,
        [AlternatePhone] nvarchar(20) NULL,
        [Email] nvarchar(200) NULL,
        [PreferredLanguage] int NOT NULL,
        [IsPregnant] bit NOT NULL,
        [LastMenstrualPeriod] date NULL,
        [WeightKg] decimal(18,6) NULL,
        [HeightCm] decimal(18,6) NULL,
        [BloodGroup] int NOT NULL,
        [CurrentTreatment] nvarchar(1000) NULL,
        [KnownAllergies] nvarchar(500) NULL,
        [RetentionUntil] date NULL,
        [IsAnonymized] bit NOT NULL,
        [AnonymizedAt] datetimeoffset(3) NULL,
        [OptedOutOfSecondaryUse] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [MergedIntoPatientId] uniqueidentifier NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Patients] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [pat].[Payers] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [Kind] int NOT NULL,
        [DefaultReimbursementRate] decimal(18,6) NOT NULL,
        [UsesNationalReferenceTariff] bit NOT NULL,
        [ThirdPartyPaymentAgreement] bit NOT NULL,
        [AgreementNumber] nvarchar(60) NULL,
        [AgreementValidUntil] date NULL,
        [PaymentTermsDays] int NOT NULL,
        [RequiresPrescription] bit NOT NULL,
        [RequiresInpeOnClaim] bit NOT NULL,
        [Ice] nvarchar(15) NULL,
        [ContactName] nvarchar(200) NULL,
        [Phone] nvarchar(30) NULL,
        [Email] nvarchar(200) NULL,
        [AddressLine] nvarchar(400) NULL,
        [City] nvarchar(100) NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Payers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [ord].[Prescribers] (
        [Id] uniqueidentifier NOT NULL,
        [LastName] nvarchar(120) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NOT NULL,
        [FirstName] nvarchar(120) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NOT NULL,
        [FullNameArabic] nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [SearchKey] nvarchar(260) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NOT NULL,
        [Kind] int NOT NULL,
        [SpecialityFr] nvarchar(150) NULL,
        [SpecialityAr] nvarchar(150) NULL,
        [SpecialityEn] nvarchar(150) NULL,
        [ProfessionalOrderNumber] nvarchar(40) NULL,
        [Inpe] nvarchar(20) NULL,
        [Ice] nvarchar(15) NULL,
        [Phone] nvarchar(30) NULL,
        [MobilePhone] nvarchar(20) NULL,
        [Email] nvarchar(200) NULL,
        [Fax] nvarchar(30) NULL,
        [AddressLine] nvarchar(400) NULL,
        [City] nvarchar(100) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [Region] int NOT NULL,
        [InstitutionName] nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [DeliveryPreference] int NOT NULL,
        [ReportLanguage] int NOT NULL,
        [SendPartialReports] bit NOT NULL,
        [CriticalResultPhone] nvarchar(20) NULL,
        [DiscountPercent] decimal(18,6) NULL,
        [DefaultPayerId] uniqueidentifier NULL,
        [Notes] nvarchar(1000) NULL,
        [IsActive] bit NOT NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Prescribers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [inv].[PurchaseOrders] (
        [Id] uniqueidentifier NOT NULL,
        [Number] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [SupplierId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [OrderDate] date NOT NULL,
        [RaisedByUserId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [ExpectedDeliveryDate] date NULL,
        [ApprovedByUserId] uniqueidentifier NULL,
        [ApprovedAt] datetimeoffset(3) NULL,
        [SentAt] datetimeoffset(3) NULL,
        [SupplierReference] nvarchar(60) NULL,
        [Notes] nvarchar(1000) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_PurchaseOrders] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[QcMaterials] (
        [Id] uniqueidentifier NOT NULL,
        [LotNumber] nvarchar(60) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [Level] int NOT NULL,
        [Manufacturer] nvarchar(150) NULL,
        [CatalogueReference] nvarchar(100) NULL,
        [ExpiryDate] date NOT NULL,
        [OpenedOn] date NULL,
        [StabilityAfterOpeningDays] int NULL,
        [IsActive] bit NOT NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_QcMaterials] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[QcResults] (
        [Id] uniqueidentifier NOT NULL,
        [QcTargetId] uniqueidentifier NOT NULL,
        [TestParameterId] uniqueidentifier NOT NULL,
        [InstrumentId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [QcRunId] uniqueidentifier NULL,
        [Value] decimal(18,6) NOT NULL,
        [MeasuredAt] datetimeoffset(3) NOT NULL,
        [EnteredByUserId] uniqueidentifier NOT NULL,
        [IsAutoTransmitted] bit NOT NULL,
        [TargetMean] decimal(18,6) NULL,
        [TargetSd] decimal(18,6) NULL,
        [ZScore] decimal(18,6) NULL,
        [Outcome] int NOT NULL,
        [ViolatedRules] int NOT NULL,
        [ReagentLotId] uniqueidentifier NULL,
        [CalibrationRecordId] uniqueidentifier NULL,
        [ReviewedAt] datetimeoffset(3) NULL,
        [ReviewedByUserId] uniqueidentifier NULL,
        [CorrectiveActionTaken] nvarchar(2000) NULL,
        [NonConformityId] uniqueidentifier NULL,
        [WasOverridden] bit NOT NULL,
        [OverrideJustification] nvarchar(1000) NULL,
        [Comment] nvarchar(1000) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_QcResults] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[QcTargets] (
        [Id] uniqueidentifier NOT NULL,
        [QcMaterialId] uniqueidentifier NOT NULL,
        [TestParameterId] uniqueidentifier NOT NULL,
        [InstrumentId] uniqueidentifier NOT NULL,
        [AssignedMean] decimal(18,6) NULL,
        [AssignedSd] decimal(18,6) NULL,
        [LaboratoryMean] decimal(18,6) NULL,
        [LaboratorySd] decimal(18,6) NULL,
        [LaboratoryPointCount] int NOT NULL,
        [StatisticsFrom] date NULL,
        [StatisticsTo] date NULL,
        [MaxAcceptableCvPercent] decimal(18,6) NULL,
        [ActiveRules] int NOT NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_QcTargets] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[ReflexRules] (
        [Id] uniqueidentifier NOT NULL,
        [TriggerParameterId] uniqueidentifier NOT NULL,
        [AnalysisToAddId] uniqueidentifier NOT NULL,
        [DescriptionFr] nvarchar(500) NOT NULL,
        [DescriptionAr] nvarchar(500) NULL,
        [DescriptionEn] nvarchar(500) NULL,
        [Condition] int NOT NULL,
        [Threshold] decimal(18,6) NULL,
        [ThresholdUpper] decimal(18,6) NULL,
        [TriggerCodedValue] nvarchar(60) NULL,
        [AddAutomatically] bit NOT NULL,
        [IsBillable] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_ReflexRules] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [sec].[Roles] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [DescriptionFr] nvarchar(500) NULL,
        [DescriptionAr] nvarchar(500) NULL,
        [DescriptionEn] nvarchar(500) NULL,
        [IsBuiltIn] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [smp].[Samples] (
        [Id] uniqueidentifier NOT NULL,
        [Barcode] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [LabOrderId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [SpecimenTypeId] uniqueidentifier NOT NULL,
        [ContainerTypeId] uniqueidentifier NULL,
        [SequenceInOrder] int NOT NULL,
        [Status] int NOT NULL,
        [CollectedAt] datetimeoffset(3) NULL,
        [CollectedByUserId] uniqueidentifier NULL,
        [CollectionLocation] int NOT NULL,
        [CollectionSite] nvarchar(120) NULL,
        [VolumeMl] decimal(18,6) NULL,
        [CollectionAttempts] int NOT NULL,
        [CollectionNote] nvarchar(500) NULL,
        [DispatchedAt] datetimeoffset(3) NULL,
        [TransportRunId] uniqueidentifier NULL,
        [ReceivedAt] datetimeoffset(3) NULL,
        [ReceivedByUserId] uniqueidentifier NULL,
        [ReceptionTemperatureCelsius] decimal(18,6) NULL,
        [ColdChainRespected] bit NULL,
        [IsAcceptedWithReservation] bit NOT NULL,
        [Defects] int NOT NULL,
        [RejectedAt] datetimeoffset(3) NULL,
        [RejectedByUserId] uniqueidentifier NULL,
        [RejectionReason] int NULL,
        [RejectionComment] nvarchar(500) NULL,
        [RecollectionRequested] bit NOT NULL,
        [ReplacementSampleId] uniqueidentifier NULL,
        [StorageLocation] nvarchar(100) NULL,
        [StoragePosition] nvarchar(40) NULL,
        [KeepUntil] date NULL,
        [DiscardedAt] datetimeoffset(3) NULL,
        [DiscardedByUserId] uniqueidentifier NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Samples] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [aud].[SecurityEvents] (
        [Id] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [OccurredAt] datetimeoffset(3) NOT NULL,
        [Succeeded] bit NOT NULL,
        [UserId] uniqueidentifier NULL,
        [AttemptedUserName] nvarchar(60) NULL,
        [Workstation] nvarchar(100) NULL,
        [OsUser] nvarchar(100) NULL,
        [SiteId] uniqueidentifier NULL,
        [FailureReasonKey] nvarchar(100) NULL,
        [TargetUserId] uniqueidentifier NULL,
        [Details] nvarchar(2000) NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_SecurityEvents] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cfg].[Settings] (
        [Id] uniqueidentifier NOT NULL,
        [Key] nvarchar(120) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [Value] nvarchar(2000) NULL,
        [SiteId] uniqueidentifier NULL,
        [Description] nvarchar(500) NULL,
        [IsSecuritySensitive] bit NOT NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Settings] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [org].[Sites] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(20) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [Kind] int NOT NULL,
        [LegalName] nvarchar(250) NOT NULL,
        [Ice] nvarchar(15) NULL,
        [FiscalId] nvarchar(20) NULL,
        [CommercialRegisterNumber] nvarchar(40) NULL,
        [CommercialRegisterCourt] nvarchar(100) NULL,
        [ProfessionalTaxNumber] nvarchar(40) NULL,
        [CnssAffiliationNumber] nvarchar(20) NULL,
        [Inpe] nvarchar(20) NULL,
        [HealthMinistryAuthorizationNumber] nvarchar(60) NULL,
        [HealthMinistryAuthorizationDate] date NULL,
        [IsAccredited] bit NOT NULL,
        [AccreditationNumber] nvarchar(60) NULL,
        [AccreditationValidUntil] date NULL,
        [AddressFr] nvarchar(400) NULL,
        [AddressAr] nvarchar(400) NULL,
        [AddressEn] nvarchar(400) NULL,
        [City] nvarchar(100) NOT NULL,
        [Region] int NOT NULL,
        [PostalCode] nvarchar(10) NULL,
        [Phone] nvarchar(400) NULL,
        [Fax] nvarchar(400) NULL,
        [Email] nvarchar(400) NULL,
        [Website] nvarchar(400) NULL,
        [LogoPng] varbinary(max) NULL,
        [ResponsibleBiologistUserId] uniqueidentifier NULL,
        [IsActive] bit NOT NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Sites] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[SpecimenTypes] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [CollectionInstructionsFr] nvarchar(1000) NULL,
        [CollectionInstructionsAr] nvarchar(1000) NULL,
        [CollectionInstructionsEn] nvarchar(1000) NULL,
        [PatientInstructionsFr] nvarchar(1000) NULL,
        [PatientInstructionsAr] nvarchar(1000) NULL,
        [PatientInstructionsEn] nvarchar(1000) NULL,
        [FastingHours] int NOT NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_SpecimenTypes] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [inv].[StockItems] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [Kind] int NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [UnitOfMeasure] nvarchar(30) NOT NULL,
        [TestsPerPack] int NULL,
        [PreferredSupplierId] uniqueidentifier NULL,
        [SupplierCatalogueReference] nvarchar(100) NULL,
        [Manufacturer] nvarchar(150) NULL,
        [DepartmentId] uniqueidentifier NULL,
        [UsedByAnalysisCodesCsv] nvarchar(2000) NULL,
        [ReorderPoint] decimal(18,6) NOT NULL,
        [ReorderQuantity] decimal(18,6) NOT NULL,
        [MinimumStock] decimal(18,6) NOT NULL,
        [MaximumStock] decimal(18,6) NULL,
        [LastUnitPriceMad] decimal(18,2) NULL,
        [StorageCondition] int NOT NULL,
        [DefaultStorageLocation] nvarchar(100) NULL,
        [IsHazardous] bit NOT NULL,
        [HazardClass] nvarchar(60) NULL,
        [SafetyDataSheetDocumentId] uniqueidentifier NULL,
        [IsCritical] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_StockItems] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [inv].[StockMovements] (
        [Id] uniqueidentifier NOT NULL,
        [StockItemId] uniqueidentifier NOT NULL,
        [ReagentLotId] uniqueidentifier NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [Quantity] decimal(18,6) NOT NULL,
        [OccurredAt] datetimeoffset(3) NOT NULL,
        [ByUserId] uniqueidentifier NOT NULL,
        [BalanceAfter] decimal(18,6) NULL,
        [InstrumentId] uniqueidentifier NULL,
        [DepartmentId] uniqueidentifier NULL,
        [Reason] nvarchar(500) NULL,
        [Reference] nvarchar(60) NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_StockMovements] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [inv].[Suppliers] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [Name] nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NOT NULL,
        [Ice] nvarchar(15) NULL,
        [FiscalId] nvarchar(20) NULL,
        [CommercialRegisterNumber] nvarchar(40) NULL,
        [ContactName] nvarchar(200) NULL,
        [Phone] nvarchar(30) NULL,
        [Email] nvarchar(200) NULL,
        [AddressLine] nvarchar(400) NULL,
        [City] nvarchar(100) NULL,
        [Region] int NOT NULL,
        [LeadTimeDays] int NOT NULL,
        [PaymentTermsDays] int NOT NULL,
        [IsApproved] bit NOT NULL,
        [LastEvaluationDate] date NULL,
        [EvaluationScore] int NULL,
        [Notes] nvarchar(1000) NULL,
        [IsActive] bit NOT NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Suppliers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[TestPanels] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [SearchAliases] nvarchar(500) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [PackagePrice] decimal(18,2) NULL,
        [ItemizeOnInvoice] bit NOT NULL,
        [GroupOnReport] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [IsActive] bit NOT NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_TestPanels] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[Units] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [DisplaySymbol] nvarchar(40) NOT NULL,
        [DisplaySymbolArabic] nvarchar(40) NULL,
        [UcumCode] nvarchar(40) NULL,
        [FactorToSi] decimal(18,6) NULL,
        [SiUnitCode] nvarchar(30) NULL,
        [IsActive] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Units] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [sec].[Users] (
        [Id] uniqueidentifier NOT NULL,
        [UserName] nvarchar(60) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [FullName] nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NOT NULL,
        [FullNameArabic] nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [Email] nvarchar(200) NULL,
        [Phone] nvarchar(30) NULL,
        [PasswordHash] nvarchar(200) NOT NULL,
        [SecurityStamp] nvarchar(64) NOT NULL,
        [MustChangePassword] bit NOT NULL,
        [PasswordChangedAt] datetimeoffset(3) NULL,
        [IsActive] bit NOT NULL,
        [PreferredLanguage] int NOT NULL,
        [Qualification] int NOT NULL,
        [ProfessionalOrderNumber] nvarchar(40) NULL,
        [Inpe] nvarchar(20) NULL,
        [QualificationValidUntil] date NULL,
        [SignaturePng] varbinary(max) NULL,
        [AuthorizedDepartmentCodesCsv] nvarchar(500) NULL,
        [FailedLoginAttempts] int NOT NULL,
        [LockedOutUntil] datetimeoffset(3) NULL,
        [LastLoginAt] datetimeoffset(3) NULL,
        [LastLoginWorkstation] nvarchar(100) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[DocumentAcknowledgements] (
        [Id] uniqueidentifier NOT NULL,
        [ControlledDocumentId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [VersionNumber] int NOT NULL,
        [AcknowledgedAt] datetimeoffset(3) NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_DocumentAcknowledgements] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DocumentAcknowledgements_ControlledDocuments_ControlledDocumentId] FOREIGN KEY ([ControlledDocumentId]) REFERENCES [qc].[ControlledDocuments] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[DocumentVersions] (
        [Id] uniqueidentifier NOT NULL,
        [ControlledDocumentId] uniqueidentifier NOT NULL,
        [VersionNumber] int NOT NULL,
        [Status] int NOT NULL,
        [AuthorUserId] uniqueidentifier NOT NULL,
        [ChangeSummaryFr] nvarchar(2000) NULL,
        [ChangeSummaryAr] nvarchar(2000) NULL,
        [ChangeSummaryEn] nvarchar(2000) NULL,
        [Content] varbinary(max) NULL,
        [ContentType] nvarchar(100) NULL,
        [FileName] nvarchar(260) NULL,
        [ContentHash] nvarchar(64) NULL,
        [ReviewedByUserId] uniqueidentifier NULL,
        [ReviewedAt] datetimeoffset(3) NULL,
        [ApprovedByUserId] uniqueidentifier NULL,
        [ApprovedAt] datetimeoffset(3) NULL,
        [EffectiveFrom] date NOT NULL,
        [WithdrawnOn] date NULL,
        [WithdrawnAt] datetimeoffset(3) NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_DocumentVersions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DocumentVersions_ControlledDocuments_ControlledDocumentId] FOREIGN KEY ([ControlledDocumentId]) REFERENCES [qc].[ControlledDocuments] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [eqp].[InstrumentTestMappings] (
        [Id] uniqueidentifier NOT NULL,
        [InstrumentInterfaceId] uniqueidentifier NOT NULL,
        [InstrumentTestCode] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [TestParameterId] uniqueidentifier NOT NULL,
        [InstrumentUnit] nvarchar(40) NULL,
        [ConversionFactor] decimal(18,6) NULL,
        [ConversionOffset] decimal(18,6) NULL,
        [CodedValueMap] nvarchar(1000) NULL,
        [IsEnabled] bit NOT NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_InstrumentTestMappings] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_InstrumentTestMappings_InstrumentInterfaces_InstrumentInterfaceId] FOREIGN KEY ([InstrumentInterfaceId]) REFERENCES [eqp].[InstrumentInterfaces] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [eqp].[CalibrationRecords] (
        [Id] uniqueidentifier NOT NULL,
        [InstrumentId] uniqueidentifier NOT NULL,
        [TestParameterId] uniqueidentifier NULL,
        [PerformedOn] date NOT NULL,
        [PerformedAt] datetimeoffset(3) NOT NULL,
        [PerformedByUserId] uniqueidentifier NOT NULL,
        [CalibratorLotNumber] nvarchar(60) NULL,
        [CalibratorExpiryDate] date NULL,
        [CalibratorManufacturer] nvarchar(150) NULL,
        [ReagentLotId] uniqueidentifier NULL,
        [Outcome] int NOT NULL,
        [Slope] decimal(18,6) NULL,
        [Intercept] decimal(18,6) NULL,
        [CorrelationCoefficient] decimal(18,6) NULL,
        [CurveData] nvarchar(4000) NULL,
        [ValidUntil] date NULL,
        [Comment] nvarchar(1000) NULL,
        [VerifiedByQc] bit NOT NULL,
        [VerifyingQcResultId] uniqueidentifier NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_CalibrationRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CalibrationRecords_Instruments_InstrumentId] FOREIGN KEY ([InstrumentId]) REFERENCES [eqp].[Instruments] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [eqp].[MaintenanceRecords] (
        [Id] uniqueidentifier NOT NULL,
        [InstrumentId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [PerformedOn] date NOT NULL,
        [DescriptionFr] nvarchar(2000) NOT NULL,
        [DescriptionAr] nvarchar(2000) NULL,
        [DescriptionEn] nvarchar(2000) NULL,
        [PerformedByUserId] uniqueidentifier NULL,
        [ExternalTechnicianName] nvarchar(200) NULL,
        [SupplierInterventionReference] nvarchar(60) NULL,
        [DowntimeHours] decimal(18,6) NULL,
        [CostMad] decimal(18,2) NULL,
        [PartsReplaced] nvarchar(1000) NULL,
        [PostInterventionCheckPassed] bit NOT NULL,
        [PostInterventionCheckNotes] nvarchar(1000) NULL,
        [NonConformityId] uniqueidentifier NULL,
        [AttachedReport] varbinary(max) NULL,
        [AttachedReportContentType] nvarchar(100) NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_MaintenanceRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MaintenanceRecords_Instruments_InstrumentId] FOREIGN KEY ([InstrumentId]) REFERENCES [eqp].[Instruments] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [bil].[InvoiceLines] (
        [Id] uniqueidentifier NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [LineNumber] int NOT NULL,
        [Description] nvarchar(300) NOT NULL,
        [DescriptionArabic] nvarchar(300) NULL,
        [LabOrderId] uniqueidentifier NULL,
        [LabOrderItemId] uniqueidentifier NULL,
        [AnalysisTestId] uniqueidentifier NULL,
        [AnalysisCode] nvarchar(30) NULL,
        [NationalActCode] nvarchar(30) NULL,
        [LetterKey] nvarchar(5) NULL,
        [Coefficient] decimal(18,6) NULL,
        [LetterKeyValue] decimal(18,6) NULL,
        [ReferenceTariffBase] decimal(18,6) NOT NULL,
        [ServiceDate] date NULL,
        [Quantity] int NOT NULL,
        [UnitPrice] decimal(18,2) NOT NULL,
        [AllocatedDiscount] decimal(18,6) NOT NULL,
        [IsVatExempt] bit NOT NULL,
        [VatRate] decimal(18,6) NOT NULL,
        [ReimbursementRate] decimal(18,6) NULL,
        [PayerShare] decimal(18,2) NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_InvoiceLines] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_InvoiceLines_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [bil].[Invoices] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [bil].[Payments] (
        [Id] uniqueidentifier NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Method] int NOT NULL,
        [Status] int NOT NULL,
        [PaymentDate] date NOT NULL,
        [ReceivedByUserId] uniqueidentifier NOT NULL,
        [Reference] nvarchar(60) NULL,
        [BankName] nvarchar(120) NULL,
        [MaturityDate] date NULL,
        [CashSessionId] uniqueidentifier NULL,
        [RemittanceReference] nvarchar(60) NULL,
        [BouncedAt] datetimeoffset(3) NULL,
        [BounceReason] nvarchar(500) NULL,
        [RefundedAt] datetimeoffset(3) NULL,
        [RefundReason] nvarchar(500) NULL,
        [Notes] nvarchar(1000) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Payments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Payments_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [bil].[Invoices] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [rpt].[ReportDeliveries] (
        [Id] uniqueidentifier NOT NULL,
        [LabReportId] uniqueidentifier NOT NULL,
        [Channel] int NOT NULL,
        [RecipientKind] int NOT NULL,
        [RecipientIdentifier] nvarchar(250) NULL,
        [RecipientName] nvarchar(200) NULL,
        [PrescriberId] uniqueidentifier NULL,
        [RequestedAt] datetimeoffset(3) NOT NULL,
        [CompletedAt] datetimeoffset(3) NULL,
        [Status] int NOT NULL,
        [AttemptCount] int NOT NULL,
        [FailureReason] nvarchar(500) NULL,
        [HandedOverByUserId] uniqueidentifier NULL,
        [BearerName] nvarchar(200) NULL,
        [BearerCin] nvarchar(20) NULL,
        [AuthorizingConsentId] uniqueidentifier NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ReportDeliveries] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ReportDeliveries_LabReports_LabReportId] FOREIGN KEY ([LabReportId]) REFERENCES [rpt].[LabReports] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [qc].[CorrectiveActions] (
        [Id] uniqueidentifier NOT NULL,
        [NonConformityId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [DescriptionFr] nvarchar(2000) NOT NULL,
        [DescriptionAr] nvarchar(2000) NULL,
        [DescriptionEn] nvarchar(2000) NULL,
        [ResponsibleUserId] uniqueidentifier NOT NULL,
        [TargetDate] date NOT NULL,
        [Status] int NOT NULL,
        [CompletedOn] date NULL,
        [CompletedByUserId] uniqueidentifier NULL,
        [EvidenceFr] nvarchar(2000) NULL,
        [EvidenceAr] nvarchar(2000) NULL,
        [EvidenceEn] nvarchar(2000) NULL,
        [Notes] nvarchar(1000) NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_CorrectiveActions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CorrectiveActions_NonConformities_NonConformityId] FOREIGN KEY ([NonConformityId]) REFERENCES [qc].[NonConformities] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [pat].[PatientAlerts] (
        [Id] uniqueidentifier NOT NULL,
        [PatientId] uniqueidentifier NOT NULL,
        [Severity] int NOT NULL,
        [MessageFr] nvarchar(500) NOT NULL,
        [MessageAr] nvarchar(500) NULL,
        [MessageEn] nvarchar(500) NULL,
        [ExpiresOn] date NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PatientAlerts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PatientAlerts_Patients_PatientId] FOREIGN KEY ([PatientId]) REFERENCES [pat].[Patients] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [pat].[PatientConsents] (
        [Id] uniqueidentifier NOT NULL,
        [PatientId] uniqueidentifier NOT NULL,
        [Purpose] int NOT NULL,
        [Channel] int NOT NULL,
        [GivenAt] datetimeoffset(3) NOT NULL,
        [CollectedByUserId] uniqueidentifier NOT NULL,
        [SignedByName] nvarchar(200) NULL,
        [SignedByCin] nvarchar(20) NULL,
        [SignedByRelationship] nvarchar(60) NULL,
        [PresentedInLanguage] int NOT NULL,
        [FormVersion] nvarchar(40) NULL,
        [EvidenceDocument] varbinary(max) NULL,
        [EvidenceDocumentContentType] nvarchar(100) NULL,
        [ExpiresOn] date NULL,
        [WithdrawnAt] datetimeoffset(3) NULL,
        [WithdrawnByUserId] uniqueidentifier NULL,
        [WithdrawalReason] nvarchar(500) NULL,
        [Notes] nvarchar(1000) NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PatientConsents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PatientConsents_Patients_PatientId] FOREIGN KEY ([PatientId]) REFERENCES [pat].[Patients] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [pat].[PatientCoverages] (
        [Id] uniqueidentifier NOT NULL,
        [PatientId] uniqueidentifier NOT NULL,
        [PayerId] uniqueidentifier NOT NULL,
        [MembershipNumber] nvarchar(40) NOT NULL,
        [InsuredPersonName] nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [InsuredPersonCin] nvarchar(20) NULL,
        [Relationship] int NOT NULL,
        [EmployerName] nvarchar(200) NULL,
        [ValidFrom] date NOT NULL,
        [ValidUntil] date NULL,
        [IsPrimary] bit NOT NULL,
        [ReimbursementRateOverride] decimal(18,6) NULL,
        [IsChronicDiseaseScheme] bit NOT NULL,
        [PriorAgreementNumber] nvarchar(60) NULL,
        [PriorAgreementValidUntil] date NULL,
        [DirectBillingEnabled] bit NOT NULL,
        [Notes] nvarchar(1000) NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PatientCoverages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PatientCoverages_Patients_PatientId] FOREIGN KEY ([PatientId]) REFERENCES [pat].[Patients] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_PatientCoverages_Payers_PayerId] FOREIGN KEY ([PayerId]) REFERENCES [pat].[Payers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [ord].[LabOrders] (
        [Id] uniqueidentifier NOT NULL,
        [AccessionNumber] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [PatientId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [PrescriberId] uniqueidentifier NULL,
        [PrescriberFreeText] nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [RegisteredAt] datetimeoffset(3) NOT NULL,
        [RegisteredByUserId] uniqueidentifier NOT NULL,
        [PrescriptionDate] date NULL,
        [PrescriptionScan] varbinary(max) NULL,
        [PrescriptionScanContentType] nvarchar(100) NULL,
        [Priority] int NOT NULL,
        [Status] int NOT NULL,
        [Origin] int NOT NULL,
        [WardOrUnit] nvarchar(120) NULL,
        [RoomOrBed] nvarchar(60) NULL,
        [ClinicalInformation] nvarchar(2000) NULL,
        [DiagnosisCode] nvarchar(20) NULL,
        [CurrentTreatment] nvarchar(1000) NULL,
        [PatientFasted] bit NULL,
        [DeclaredFastingHours] int NULL,
        [PayerId] uniqueidentifier NULL,
        [PatientCoverageId] uniqueidentifier NULL,
        [ReimbursementRateAtRegistration] decimal(18,6) NULL,
        [PriorAgreementNumber] nvarchar(60) NULL,
        [DiscountPercent] decimal(18,6) NULL,
        [DiscountReason] nvarchar(300) NULL,
        [InvoiceId] uniqueidentifier NULL,
        [FirstCollectedAt] datetimeoffset(3) NULL,
        [AllReceivedAt] datetimeoffset(3) NULL,
        [FullyValidatedAt] datetimeoffset(3) NULL,
        [FirstReportedAt] datetimeoffset(3) NULL,
        [DueAt] datetimeoffset(3) NULL,
        [CancelledAt] datetimeoffset(3) NULL,
        [CancelledByUserId] uniqueidentifier NULL,
        [CancellationReason] nvarchar(400) NULL,
        [ReportLanguage] int NOT NULL,
        [InternalNote] nvarchar(1000) NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedAt] datetimeoffset(3) NULL,
        [DeletedByUserId] uniqueidentifier NULL,
        [DeletionReason] nvarchar(400) NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_LabOrders] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_LabOrders_Patients_PatientId] FOREIGN KEY ([PatientId]) REFERENCES [pat].[Patients] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_LabOrders_Prescribers_PrescriberId] FOREIGN KEY ([PrescriberId]) REFERENCES [ord].[Prescribers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [inv].[PurchaseOrderLines] (
        [Id] uniqueidentifier NOT NULL,
        [PurchaseOrderId] uniqueidentifier NOT NULL,
        [LineNumber] int NOT NULL,
        [StockItemId] uniqueidentifier NOT NULL,
        [Description] nvarchar(300) NULL,
        [QuantityOrdered] decimal(18,6) NOT NULL,
        [QuantityReceived] decimal(18,6) NOT NULL,
        [UnitPriceMad] decimal(18,2) NOT NULL,
        [DiscountRate] decimal(18,6) NOT NULL,
        [VatRate] decimal(18,6) NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PurchaseOrderLines] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PurchaseOrderLines_PurchaseOrders_PurchaseOrderId] FOREIGN KEY ([PurchaseOrderId]) REFERENCES [inv].[PurchaseOrders] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [sec].[RolePermissions] (
        [RoleId] uniqueidentifier NOT NULL,
        [Permission] nvarchar(80) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([RoleId], [Permission]),
        CONSTRAINT [FK_RolePermissions_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [sec].[Roles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [smp].[SampleAliquots] (
        [Id] uniqueidentifier NOT NULL,
        [ParentSampleId] uniqueidentifier NOT NULL,
        [Barcode] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [VolumeMl] decimal(18,6) NULL,
        [CreatedAtUtc] datetimeoffset(3) NOT NULL,
        [StorageLocation] nvarchar(100) NULL,
        [Purpose] nvarchar(200) NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_SampleAliquots] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SampleAliquots_Samples_ParentSampleId] FOREIGN KEY ([ParentSampleId]) REFERENCES [smp].[Samples] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [smp].[SampleEvents] (
        [Id] uniqueidentifier NOT NULL,
        [SampleId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [OccurredAt] datetimeoffset(3) NOT NULL,
        [ByUserId] uniqueidentifier NULL,
        [Detail] nvarchar(500) NULL,
        [TemperatureCelsius] decimal(18,6) NULL,
        [Workstation] nvarchar(100) NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_SampleEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SampleEvents_Samples_SampleId] FOREIGN KEY ([SampleId]) REFERENCES [smp].[Samples] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[AnalysisTests] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [ShortNameFr] nvarchar(60) NULL,
        [ShortNameAr] nvarchar(60) NULL,
        [ShortNameEn] nvarchar(60) NULL,
        [SearchAliases] nvarchar(500) COLLATE Latin1_General_100_CI_AI_SC_UTF8 NULL,
        [DepartmentId] uniqueidentifier NOT NULL,
        [SpecimenTypeId] uniqueidentifier NOT NULL,
        [ContainerTypeId] uniqueidentifier NULL,
        [RequiredVolumeMl] decimal(18,6) NULL,
        [LoincCode] nvarchar(20) NULL,
        [NationalActCode] nvarchar(30) NULL,
        [MethodFr] nvarchar(200) NULL,
        [MethodAr] nvarchar(200) NULL,
        [MethodEn] nvarchar(200) NULL,
        [ReagentReference] nvarchar(200) NULL,
        [IsAccredited] bit NOT NULL,
        [IsSubcontracted] bit NOT NULL,
        [SubcontractorSiteId] uniqueidentifier NULL,
        [RoutineTurnaroundHours] int NOT NULL,
        [UrgentTurnaroundMinutes] int NOT NULL,
        [IsAvailableUrgently] bit NOT NULL,
        [ScheduledDaysCsv] nvarchar(30) NULL,
        [FastingHoursOverride] int NULL,
        [StabilityAtRoomTemperatureHours] int NULL,
        [StabilityRefrigeratedHours] int NULL,
        [ClinicalInfoPromptFr] nvarchar(500) NULL,
        [ClinicalInfoPromptAr] nvarchar(500) NULL,
        [ClinicalInfoPromptEn] nvarchar(500) NULL,
        [ConsentRequirement] int NOT NULL,
        [IsNotifiableDisease] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_AnalysisTests] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AnalysisTests_ContainerTypes_ContainerTypeId] FOREIGN KEY ([ContainerTypeId]) REFERENCES [cat].[ContainerTypes] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_AnalysisTests_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [cat].[Departments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_AnalysisTests_SpecimenTypes_SpecimenTypeId] FOREIGN KEY ([SpecimenTypeId]) REFERENCES [cat].[SpecimenTypes] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [inv].[ReagentLots] (
        [Id] uniqueidentifier NOT NULL,
        [StockItemId] uniqueidentifier NOT NULL,
        [LotNumber] nvarchar(60) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [ExpiryDate] date NOT NULL,
        [QuantityReceived] decimal(18,6) NOT NULL,
        [QuantityRemaining] decimal(18,6) NOT NULL,
        [ReceivedOn] date NOT NULL,
        [ReceivedByUserId] uniqueidentifier NULL,
        [SupplierId] uniqueidentifier NULL,
        [PurchaseOrderId] uniqueidentifier NULL,
        [DeliveryNoteReference] nvarchar(60) NULL,
        [SupplierInvoiceReference] nvarchar(60) NULL,
        [UnitPriceMad] decimal(18,2) NULL,
        [OpenedOn] date NULL,
        [StabilityAfterOpeningDays] int NULL,
        [StorageLocation] nvarchar(100) NULL,
        [IsQuarantined] bit NOT NULL,
        [ReleasedOn] date NULL,
        [ReleasedByUserId] uniqueidentifier NULL,
        [AcceptanceEvidence] nvarchar(1000) NULL,
        [IsRejected] bit NOT NULL,
        [RejectionReason] nvarchar(500) NULL,
        [IsRecalled] bit NOT NULL,
        [RecalledOn] date NULL,
        [RecallReference] nvarchar(100) NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ReagentLots] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ReagentLots_StockItems_StockItemId] FOREIGN KEY ([StockItemId]) REFERENCES [inv].[StockItems] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [sec].[UserRoles] (
        [UserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_UserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_UserRoles_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [sec].[Roles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_UserRoles_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [sec].[Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [sec].[UserSiteAccess] (
        [UserId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [IsDefaultSite] bit NOT NULL,
        CONSTRAINT [PK_UserSiteAccess] PRIMARY KEY ([UserId], [SiteId]),
        CONSTRAINT [FK_UserSiteAccess_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [sec].[Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [ord].[LabOrderItems] (
        [Id] uniqueidentifier NOT NULL,
        [LabOrderId] uniqueidentifier NOT NULL,
        [AnalysisTestId] uniqueidentifier NOT NULL,
        [TestPanelId] uniqueidentifier NULL,
        [AddedByReflexRuleId] uniqueidentifier NULL,
        [SampleId] uniqueidentifier NULL,
        [Priority] int NOT NULL,
        [Status] int NOT NULL,
        [DepartmentId] uniqueidentifier NOT NULL,
        [ChargedPrice] decimal(18,2) NOT NULL,
        [ReferenceTariffBase] decimal(18,6) NOT NULL,
        [BilledLetterKey] nvarchar(5) NULL,
        [BilledCoefficient] decimal(18,6) NULL,
        [BilledActCode] nvarchar(30) NULL,
        [PayerShare] decimal(18,2) NOT NULL,
        [PatientShare] decimal(18,2) NOT NULL,
        [IsSubcontracted] bit NOT NULL,
        [SubcontractorSiteId] uniqueidentifier NULL,
        [SentToSubcontractorAt] datetimeoffset(3) NULL,
        [SubcontractorReference] nvarchar(60) NULL,
        [CollectedAt] datetimeoffset(3) NULL,
        [ReceivedAt] datetimeoffset(3) NULL,
        [StartedAt] datetimeoffset(3) NULL,
        [ResultEnteredAt] datetimeoffset(3) NULL,
        [TechnicallyValidatedAt] datetimeoffset(3) NULL,
        [TechnicallyValidatedByUserId] uniqueidentifier NULL,
        [ValidatedAt] datetimeoffset(3) NULL,
        [ValidatedByUserId] uniqueidentifier NULL,
        [ReportedAt] datetimeoffset(3) NULL,
        [DueAt] datetimeoffset(3) NULL,
        [RunCount] int NOT NULL,
        [RepeatReason] nvarchar(500) NULL,
        [CancelledAt] datetimeoffset(3) NULL,
        [CancelledByUserId] uniqueidentifier NULL,
        [CancellationReason] nvarchar(500) NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_LabOrderItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_LabOrderItems_AnalysisTests_AnalysisTestId] FOREIGN KEY ([AnalysisTestId]) REFERENCES [cat].[AnalysisTests] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_LabOrderItems_LabOrders_LabOrderId] FOREIGN KEY ([LabOrderId]) REFERENCES [ord].[LabOrders] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[Tariffs] (
        [Id] uniqueidentifier NOT NULL,
        [AnalysisTestId] uniqueidentifier NOT NULL,
        [PayerId] uniqueidentifier NULL,
        [ValidFrom] date NOT NULL,
        [ValidUntil] date NULL,
        [LetterKey] nvarchar(5) NOT NULL,
        [Coefficient] decimal(18,6) NOT NULL,
        [LetterKeyValue] decimal(18,6) NOT NULL,
        [Price] decimal(18,2) NOT NULL,
        [IsVatExempt] bit NOT NULL,
        [VatRate] decimal(18,6) NOT NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Tariffs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Tariffs_AnalysisTests_AnalysisTestId] FOREIGN KEY ([AnalysisTestId]) REFERENCES [cat].[AnalysisTests] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[TestPanelItems] (
        [Id] uniqueidentifier NOT NULL,
        [TestPanelId] uniqueidentifier NOT NULL,
        [AnalysisTestId] uniqueidentifier NOT NULL,
        [IsOptional] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_TestPanelItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TestPanelItems_AnalysisTests_AnalysisTestId] FOREIGN KEY ([AnalysisTestId]) REFERENCES [cat].[AnalysisTests] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TestPanelItems_TestPanels_TestPanelId] FOREIGN KEY ([TestPanelId]) REFERENCES [cat].[TestPanels] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[TestParameters] (
        [Id] uniqueidentifier NOT NULL,
        [AnalysisTestId] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [NameFr] nvarchar(300) NOT NULL,
        [NameAr] nvarchar(300) NULL,
        [NameEn] nvarchar(300) NULL,
        [ShortNameFr] nvarchar(60) NULL,
        [ShortNameAr] nvarchar(60) NULL,
        [ShortNameEn] nvarchar(60) NULL,
        [ValueType] int NOT NULL,
        [LoincCode] nvarchar(20) NULL,
        [UnitId] uniqueidentifier NULL,
        [Decimals] int NOT NULL,
        [MeasuringRangeLow] decimal(18,6) NULL,
        [MeasuringRangeHigh] decimal(18,6) NULL,
        [TechnicalLimitLow] decimal(18,6) NULL,
        [TechnicalLimitHigh] decimal(18,6) NULL,
        [CriticalLow] decimal(18,6) NULL,
        [CriticalHigh] decimal(18,6) NULL,
        [AnalyticalCvPercent] decimal(18,6) NULL,
        [DeltaCheckPercent] decimal(18,6) NULL,
        [DeltaCheckWindowDays] int NULL,
        [CalculationFormula] nvarchar(1000) NULL,
        [IsInternalOnly] bit NOT NULL,
        [AlwaysShowOnReport] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [IndentLevel] int NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_TestParameters] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TestParameters_AnalysisTests_AnalysisTestId] FOREIGN KEY ([AnalysisTestId]) REFERENCES [cat].[AnalysisTests] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_TestParameters_Units_UnitId] FOREIGN KEY ([UnitId]) REFERENCES [cat].[Units] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[CodedResultOptions] (
        [Id] uniqueidentifier NOT NULL,
        [TestParameterId] uniqueidentifier NOT NULL,
        [Code] nvarchar(40) COLLATE Latin1_General_100_CS_AS_SC_UTF8 NOT NULL,
        [LabelFr] nvarchar(300) NOT NULL,
        [LabelAr] nvarchar(300) NULL,
        [LabelEn] nvarchar(300) NULL,
        [DisplayOrder] int NOT NULL,
        [IsAbnormal] bit NOT NULL,
        [IsCritical] bit NOT NULL,
        [IsExpectedNormal] bit NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_CodedResultOptions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CodedResultOptions_TestParameters_TestParameterId] FOREIGN KEY ([TestParameterId]) REFERENCES [cat].[TestParameters] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [cat].[ReferenceRanges] (
        [Id] uniqueidentifier NOT NULL,
        [TestParameterId] uniqueidentifier NOT NULL,
        [AppliesToGender] int NOT NULL,
        [AgeFromDays] int NULL,
        [AgeToDays] int NULL,
        [AppliesToPregnancy] bit NULL,
        [PregnancyTrimester] int NULL,
        [InstrumentId] uniqueidentifier NULL,
        [LowerLimit] decimal(18,6) NULL,
        [UpperLimit] decimal(18,6) NULL,
        [DisplayTextFr] nvarchar(200) NULL,
        [DisplayTextAr] nvarchar(200) NULL,
        [DisplayTextEn] nvarchar(200) NULL,
        [ExpectedCodedValue] nvarchar(60) NULL,
        [IsDecisionThreshold] bit NOT NULL,
        [Source] nvarchar(300) NULL,
        [LastReviewedOn] date NULL,
        [ValidFrom] date NOT NULL,
        [ValidUntil] date NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ReferenceRanges] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ReferenceRanges_TestParameters_TestParameterId] FOREIGN KEY ([TestParameterId]) REFERENCES [cat].[TestParameters] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [res].[TestResults] (
        [Id] uniqueidentifier NOT NULL,
        [LabOrderId] uniqueidentifier NOT NULL,
        [LabOrderItemId] uniqueidentifier NOT NULL,
        [TestParameterId] uniqueidentifier NOT NULL,
        [SiteId] uniqueidentifier NOT NULL,
        [DepartmentId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [NumericValue] decimal(18,6) NULL,
        [ValueOperator] nvarchar(2) NULL,
        [TextValue] nvarchar(4000) NULL,
        [CodedValue] nvarchar(60) NULL,
        [TitreDenominator] int NULL,
        [RawInstrumentValue] nvarchar(200) NULL,
        [DilutionFactor] decimal(18,6) NULL,
        [Interpretation] int NOT NULL,
        [Flags] int NOT NULL,
        [AppliedReferenceRangeText] nvarchar(200) NULL,
        [AppliedReferenceRangeId] uniqueidentifier NULL,
        [AppliedLowerLimit] decimal(18,6) NULL,
        [AppliedUpperLimit] decimal(18,6) NULL,
        [AppliedUnitSymbol] nvarchar(40) NULL,
        [AppliedDecimals] int NOT NULL,
        [PreviousNumericValue] decimal(18,6) NULL,
        [PreviousResultDate] date NULL,
        [InstrumentId] uniqueidentifier NULL,
        [ReagentLotId] uniqueidentifier NULL,
        [CalibrationRecordId] uniqueidentifier NULL,
        [CoveringQcRunId] uniqueidentifier NULL,
        [MeasuredAt] datetimeoffset(3) NULL,
        [EnteredAt] datetimeoffset(3) NULL,
        [EnteredByUserId] uniqueidentifier NULL,
        [IsAutoTransmitted] bit NOT NULL,
        [TechnicallyValidatedAt] datetimeoffset(3) NULL,
        [TechnicallyValidatedByUserId] uniqueidentifier NULL,
        [ValidatedAt] datetimeoffset(3) NULL,
        [ValidatedByUserId] uniqueidentifier NULL,
        [TechnicalComment] nvarchar(2000) NULL,
        [BiologistCommentFr] nvarchar(2000) NULL,
        [BiologistCommentAr] nvarchar(2000) NULL,
        [BiologistCommentEn] nvarchar(2000) NULL,
        [PrintTechnicalComment] bit NOT NULL,
        [RowVersion] rowversion NULL,
        [CreatedAt] datetimeoffset(3) NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAt] datetimeoffset(3) NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_TestResults] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TestResults_TestParameters_TestParameterId] FOREIGN KEY ([TestParameterId]) REFERENCES [cat].[TestParameters] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE TABLE [res].[ResultRevisions] (
        [Id] uniqueidentifier NOT NULL,
        [TestResultId] uniqueidentifier NOT NULL,
        [RevisionNumber] int NOT NULL,
        [RevisedAt] datetimeoffset(3) NOT NULL,
        [RevisedByUserId] uniqueidentifier NOT NULL,
        [Reason] nvarchar(500) NOT NULL,
        [PreviousNumericValue] decimal(18,6) NULL,
        [PreviousValueOperator] nvarchar(2) NULL,
        [PreviousTextValue] nvarchar(4000) NULL,
        [PreviousCodedValue] nvarchar(60) NULL,
        [PreviousTitreDenominator] int NULL,
        [PreviousInterpretation] int NOT NULL,
        [WasValidated] bit NOT NULL,
        [PreviousValidatedByUserId] uniqueidentifier NULL,
        [PreviousValidatedAt] datetimeoffset(3) NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ResultRevisions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ResultRevisions_TestResults_TestResultId] FOREIGN KEY ([TestResultId]) REFERENCES [res].[TestResults] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AnalysisTests_ActCode] ON [cat].[AnalysisTests] ([NationalActCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AnalysisTests_ContainerTypeId] ON [cat].[AnalysisTests] ([ContainerTypeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AnalysisTests_DepartmentId] ON [cat].[AnalysisTests] ([DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AnalysisTests_SpecimenTypeId] ON [cat].[AnalysisTests] ([SpecimenTypeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_AnalysisTests_Code] ON [cat].[AnalysisTests] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_AuditEntries_Correlation] ON [aud].[AuditEntries] ([CorrelationId]) WHERE [CorrelationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditEntries_Entity] ON [aud].[AuditEntries] ([EntityName], [EntityId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_AuditEntries_Patient] ON [aud].[AuditEntries] ([PatientId], [OccurredAt]) WHERE [PatientId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditEntries_User] ON [aud].[AuditEntries] ([UserId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CalibrationRecords_Instrument_Parameter] ON [eqp].[CalibrationRecords] ([InstrumentId], [TestParameterId], [PerformedOn]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CashSessions_OpenPerOperator] ON [bil].[CashSessions] ([OperatorUserId], [Status]) WHERE [Status] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CodedResultOptions_Parameter_Code] ON [cat].[CodedResultOptions] ([TestParameterId], [Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_ContainerTypes_Code] ON [cat].[ContainerTypes] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_ControlledDocuments_Reference] ON [qc].[ControlledDocuments] ([Reference]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CorrectiveActions_NonConformityId] ON [qc].[CorrectiveActions] ([NonConformityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CorrectiveActions_Owner] ON [qc].[CorrectiveActions] ([ResponsibleUserId], [Status], [TargetDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CriticalNotifications_Pending] ON [res].[CriticalResultNotifications] ([SiteId], [Outcome], [AttemptedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CriticalNotifications_Result] ON [res].[CriticalResultNotifications] ([TestResultId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_DataAccessLog_BreakGlass] ON [aud].[DataAccessLog] ([IsBreakGlass]) WHERE [IsBreakGlass] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_DataAccessLog_Patient] ON [aud].[DataAccessLog] ([PatientId], [OccurredAt]) WHERE [PatientId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_DataAccessLog_User] ON [aud].[DataAccessLog] ([UserId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Departments_Code] ON [cat].[Departments] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_DocumentAcks_Document_User_Version] ON [qc].[DocumentAcknowledgements] ([ControlledDocumentId], [UserId], [VersionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_DocumentVersions_Document_Version] ON [qc].[DocumentVersions] ([ControlledDocumentId], [VersionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_DocumentVersions_OneEffective] ON [qc].[DocumentVersions] ([ControlledDocumentId]) WHERE [Status] = 4');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_EqaParticipations_Site_Round] ON [qc].[EqaParticipations] ([SiteId], [RoundDate], [Outcome]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_InstrumentInterfaces_Instrument] ON [eqp].[InstrumentInterfaces] ([InstrumentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InstrumentMessageLog_Interface_Occurred] ON [eqp].[InstrumentMessageLog] ([InstrumentInterfaceId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InstrumentMessageLog_Status] ON [eqp].[InstrumentMessageLog] ([Status], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Instruments_Site_Status] ON [eqp].[Instruments] ([SiteId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Instruments_Code] ON [eqp].[Instruments] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_InstrumentTestMappings_Interface_Code] ON [eqp].[InstrumentTestMappings] ([InstrumentInterfaceId], [InstrumentTestCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_InvoiceLines_OrderItem] ON [bil].[InvoiceLines] ([LabOrderItemId]) WHERE [LabOrderItemId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_InvoiceLines_Invoice_Line] ON [bil].[InvoiceLines] ([InvoiceId], [LineNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Invoices_Patient] ON [bil].[Invoices] ([PatientId], [IssueDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Invoices_Receivables] ON [bil].[Invoices] ([PayerId], [Status], [DueDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Invoices_Site_Date] ON [bil].[Invoices] ([SiteId], [IssueDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Invoices_Site_Kind_Number] ON [bil].[Invoices] ([SiteId], [Kind], [Number]) WHERE [Status] <> 0');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_LabOrderItems_AnalysisTestId] ON [ord].[LabOrderItems] ([AnalysisTestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_LabOrderItems_DepartmentWorklist] ON [ord].[LabOrderItems] ([DepartmentId], [Status], [Priority]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_LabOrderItems_Order_Analysis] ON [ord].[LabOrderItems] ([LabOrderId], [AnalysisTestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_LabOrderItems_Sample] ON [ord].[LabOrderItems] ([SampleId]) WHERE [SampleId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_LabOrders_Overdue] ON [ord].[LabOrders] ([DueAt]) WHERE [DueAt] IS NOT NULL AND [Status] < 7');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_LabOrders_Patient_History] ON [ord].[LabOrders] ([PatientId], [RegisteredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_LabOrders_PrescriberId] ON [ord].[LabOrders] ([PrescriberId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_LabOrders_Worklist] ON [ord].[LabOrders] ([SiteId], [Status], [Priority], [RegisteredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_LabOrders_AccessionNumber] ON [ord].[LabOrders] ([AccessionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_LabReports_AccessToken] ON [rpt].[LabReports] ([AccessToken]) WHERE [AccessToken] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_LabReports_Order_Version] ON [rpt].[LabReports] ([LabOrderId], [VersionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaintenanceRecords_Instrument_Date] ON [eqp].[MaintenanceRecords] ([InstrumentId], [PerformedOn]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NonConformities_Analysis] ON [qc].[NonConformities] ([Category], [Severity], [DetectedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_NonConformities_Open] ON [qc].[NonConformities] ([SiteId], [Status], [DueDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_NonConformities_Reference] ON [qc].[NonConformities] ([Reference]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_NumberSequences_Scope] ON [cfg].[NumberSequences] ([Purpose], [SiteId], [Year]) WHERE [SiteId] IS NOT NULL AND [Year] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_PasswordHistory_User] ON [sec].[PasswordHistory] ([UserId], [ChangedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_PatientAlerts_PatientId] ON [pat].[PatientAlerts] ([PatientId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_PatientConsents_Patient_Purpose] ON [pat].[PatientConsents] ([PatientId], [Purpose], [GivenAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_PatientCoverages_PatientId] ON [pat].[PatientCoverages] ([PatientId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_PatientCoverages_Payer_Member] ON [pat].[PatientCoverages] ([PayerId], [MembershipNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Patients_Cin] ON [pat].[Patients] ([Cin]) WHERE [Cin] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Patients_Mobile] ON [pat].[Patients] ([MobilePhone]) WHERE [MobilePhone] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Patients_Retention] ON [pat].[Patients] ([RetentionUntil]) WHERE [RetentionUntil] IS NOT NULL AND [IsAnonymized] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Patients_Search] ON [pat].[Patients] ([SiteId], [SearchKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Patients_RecordNumber] ON [pat].[Patients] ([MedicalRecordNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Payers_Code] ON [pat].[Payers] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Payments_CashSession] ON [bil].[Payments] ([CashSessionId]) WHERE [CashSessionId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Payments_InvoiceId] ON [bil].[Payments] ([InvoiceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Payments_Site_Date_Method] ON [bil].[Payments] ([SiteId], [PaymentDate], [Method]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Prescribers_Inpe] ON [ord].[Prescribers] ([Inpe]) WHERE [Inpe] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Prescribers_Search] ON [ord].[Prescribers] ([SearchKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_PurchaseOrderLines_Order_Line] ON [inv].[PurchaseOrderLines] ([PurchaseOrderId], [LineNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_PurchaseOrders_Outstanding] ON [inv].[PurchaseOrders] ([SiteId], [Status], [ExpectedDeliveryDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_PurchaseOrders_Number] ON [inv].[PurchaseOrders] ([Number]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QcMaterials_Lot_Level] ON [qc].[QcMaterials] ([LotNumber], [Level]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_QcResults_Review] ON [qc].[QcResults] ([SiteId], [Outcome], [ReviewedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_QcResults_Run] ON [qc].[QcResults] ([QcRunId]) WHERE [QcRunId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_QcResults_Target_Measured] ON [qc].[QcResults] ([QcTargetId], [MeasuredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QcTargets_Material_Parameter_Instrument] ON [qc].[QcTargets] ([QcMaterialId], [TestParameterId], [InstrumentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ReagentLots_Item_Expiry] ON [inv].[ReagentLots] ([StockItemId], [ExpiryDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_ReagentLots_Item_Lot] ON [inv].[ReagentLots] ([StockItemId], [LotNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ReferenceRanges_Parameter] ON [cat].[ReferenceRanges] ([TestParameterId], [IsActive]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ReflexRules_Trigger] ON [cat].[ReflexRules] ([TriggerParameterId], [IsActive]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ReportDeliveries_Report] ON [rpt].[ReportDeliveries] ([LabReportId], [RequestedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_ResultRevisions_Result_Number] ON [res].[ResultRevisions] ([TestResultId], [RevisionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Roles_Code] ON [sec].[Roles] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SampleAliquots_ParentSampleId] ON [smp].[SampleAliquots] ([ParentSampleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_SampleAliquots_Barcode] ON [smp].[SampleAliquots] ([Barcode]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SampleEvents_Sample] ON [smp].[SampleEvents] ([SampleId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Samples_KeepUntil] ON [smp].[Samples] ([KeepUntil]) WHERE [KeepUntil] IS NOT NULL AND [Status] <> 9');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Samples_Order] ON [smp].[Samples] ([LabOrderId], [SequenceInOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Samples_Status] ON [smp].[Samples] ([SiteId], [Status], [CollectedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Samples_Barcode] ON [smp].[Samples] ([Barcode]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SecurityEvents_Kind] ON [aud].[SecurityEvents] ([Kind], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SecurityEvents_UserName] ON [aud].[SecurityEvents] ([AttemptedUserName], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Settings_Key_Site] ON [cfg].[Settings] ([Key], [SiteId]) WHERE [SiteId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Sites_Code] ON [org].[Sites] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Sites_Ice] ON [org].[Sites] ([Ice]) WHERE [Ice] IS NOT NULL AND [IsDeleted] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_SpecimenTypes_Code] ON [cat].[SpecimenTypes] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_StockItems_Site_Code] ON [inv].[StockItems] ([SiteId], [Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_StockMovements_Item_Occurred] ON [inv].[StockMovements] ([StockItemId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_StockMovements_Lot] ON [inv].[StockMovements] ([ReagentLotId]) WHERE [ReagentLotId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Suppliers_Code] ON [inv].[Suppliers] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Tariffs_Analysis_Payer_From] ON [cat].[Tariffs] ([AnalysisTestId], [PayerId], [ValidFrom]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TestPanelItems_AnalysisTestId] ON [cat].[TestPanelItems] ([AnalysisTestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TestPanelItems_Panel_Analysis] ON [cat].[TestPanelItems] ([TestPanelId], [AnalysisTestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TestPanels_Code] ON [cat].[TestPanels] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TestParameters_UnitId] ON [cat].[TestParameters] ([UnitId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TestParameters_Analysis_Code] ON [cat].[TestParameters] ([AnalysisTestId], [Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_TestResults_Flagged] ON [res].[TestResults] ([Flags]) WHERE [Flags] <> 0');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TestResults_Order] ON [res].[TestResults] ([LabOrderId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TestResults_Parameter_History] ON [res].[TestResults] ([TestParameterId], [ValidatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TestResults_Validation] ON [res].[TestResults] ([SiteId], [DepartmentId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TestResults_Item_Parameter] ON [res].[TestResults] ([LabOrderItemId], [TestParameterId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Units_Code] ON [cat].[Units] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserRoles_RoleId] ON [sec].[UserRoles] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Users_UserName] ON [sec].[Users] ([UserName]);
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF OBJECT_ID('aud.TR_AuditEntries_NoUpdate', 'TR') IS NULL
    EXEC('CREATE TRIGGER [aud].[TR_AuditEntries_NoUpdate] ON [aud].[AuditEntries]
          INSTEAD OF UPDATE, DELETE AS
          BEGIN
              THROW 51001, ''The audit trail is append-only and cannot be modified.'', 1;
          END');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF OBJECT_ID('aud.TR_DataAccessLog_NoUpdate', 'TR') IS NULL
    EXEC('CREATE TRIGGER [aud].[TR_DataAccessLog_NoUpdate] ON [aud].[DataAccessLog]
          INSTEAD OF UPDATE, DELETE AS
          BEGIN
              THROW 51002, ''The data access log is append-only and cannot be modified.'', 1;
          END');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF OBJECT_ID('aud.TR_SecurityEvents_NoUpdate', 'TR') IS NULL
    EXEC('CREATE TRIGGER [aud].[TR_SecurityEvents_NoUpdate] ON [aud].[SecurityEvents]
          INSTEAD OF UPDATE, DELETE AS
          BEGIN
              THROW 51003, ''The security event log is append-only and cannot be modified.'', 1;
          END');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    IF OBJECT_ID('res.TR_ResultRevisions_NoUpdate', 'TR') IS NULL
    EXEC('CREATE TRIGGER [res].[TR_ResultRevisions_NoUpdate] ON [res].[ResultRevisions]
          INSTEAD OF UPDATE, DELETE AS
          BEGIN
              THROW 51004, ''A result revision is a permanent record and cannot be modified.'', 1;
          END');
END;

IF NOT EXISTS (
    SELECT * FROM [cfg].[__MigrationsHistory]
    WHERE [MigrationId] = N'20261008050305_InitialCreate'
)
BEGIN
    INSERT INTO [cfg].[__MigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261008050305_InitialCreate', N'10.0.0');
END;

COMMIT;
GO

