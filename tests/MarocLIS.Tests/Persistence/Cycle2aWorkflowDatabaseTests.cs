using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the defect-hunt cycle 2 (lot a), created from the model.</summary>
public sealed class Cycle2aScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle2a_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle2aScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// Defect-hunt cycle 2 (lot a), the pre- and post-analytical circuit replayed further against SQL
/// Server: a tube refused after part of its analysis was printed, a tube discarded while its
/// analyses still waited, an aliquot larger than its tube, and who may close a recollection or
/// correct a released value.
/// </summary>
public sealed class Cycle2aWorkflowDatabaseTests(Cycle2aScratchDatabase database)
    : IClassFixture<Cycle2aScratchDatabase>
{
    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; set; }
        public string UserName => "operateur";
        public string FullName => "Opérateur cycle";
        public Guid SiteId { get; set; }
        public string SiteCode { get; set; } = "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds { get; set; } = [];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();

        /// <summary>Null grants everything; otherwise only these.</summary>
        public HashSet<string>? Granted { get; set; }

        public bool Has(string permission) => Granted is null || Granted.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "PAILLASSE-C2A";
    }

    private sealed class Renderer : IReportRenderer
    {
        public byte[] Render(ReportModel model) => [0x25, 0x50, 0x44, 0x46];
        public int CountPages(byte[] pdf) => 1;
    }

    private sealed class AllFonts : IFontAvailability
    {
        public string ArabicFamily => "Arabic";
        public string LatinFamily => "Latin";
        public bool IsAvailable(string family) => true;
        public IReadOnlyList<string> MissingFrom(IEnumerable<string> families) => [];
    }

    private sealed record Lab(LabSite Site, AppUser Biologist, AnalysisTest Glucose, AnalysisTest Crp, Patient Patient);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static Operator OperatorOf(Lab lab, HashSet<string>? granted = null) => new()
    {
        UserId = lab.Biologist.Id,
        SiteId = lab.Site.Id,
        SiteCode = lab.Site.Code,
        AccessibleSiteIds = [lab.Site.Id],
        Granted = granted
    };

    private ServiceProvider Services(Operator user)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(user);
        services.AddSingleton<IReportRenderer>(new Renderer());
        services.AddSingleton<IFontAvailability, AllFonts>();
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IValidationQueries, ValidationQueries>();
        services.AddIsolatedQueries<IReportQueries, ReportQueries>();
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ISampleQueries, SampleQueries>();
        services.AddIsolatedQueries<IRecollectionQueries, RecollectionQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static LabSite Laboratory(Guid biologistId) => new(Unique("L"), new MultilingualText("Laboratoire cycle"),
                                                               LabSiteKind.MainLaboratory)
    {
        LegalName = "LABORATOIRE CYCLE SARL",
        Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
        FiscalId = "45128796",
        AddressLine = new MultilingualText("1, rue du Test"),
        City = "Rabat",
        HealthMinistryAuthorizationNumber = "AUT-C2A-1",
        ResponsibleBiologistUserId = biologistId
    };

    private static AnalysisTest Analysis(string code, string name, Department department, SpecimenType specimen,
                                         DateOnly validFrom, decimal? criticalHigh = null)
    {
        var analysis = new AnalysisTest(Unique(code), new MultilingualText(name), department.Id, specimen.Id)
        {
            RoutineTurnaroundHours = 4
        };

        var parameter = new TestParameter(Unique(code), new MultilingualText(name), ResultValueType.Numeric)
        {
            Decimals = 1,
            CriticalHigh = criticalHigh
        };
        parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
        analysis.AddParameter(parameter);

        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom)
        {
            LetterKey = "B",
            Coefficient = 20m,
            LetterKeyValue = 1m,
            Price = 20m,
            IsVatExempt = true
        });

        analysis.Validate().IsSuccess.Should().BeTrue();
        return analysis;
    }

    private async Task<Lab> LabAsync()
    {
        var biologist = new AppUser(Unique("bio"), "Dr Cycle", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-C2A-1"
        };

        var site = Laboratory(biologist.Id);
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var glucose = Analysis("GLY", "Glycémie", department, specimen, validFrom, criticalHigh: 500m);
        var crp = Analysis("CRP", "Protéine C réactive", department, specimen, validFrom);
        var patient = new Patient(Unique("P"), "BENNANI", "Karim", new DateOnly(1975, 3, 2), Gender.Male, site.Id)
        {
            MobilePhone = "0612345678",
            SearchKey = PatientSearchKey.Build("BENNANI", "Karim")
        };

        await using var context = database.Context();

        context.Users.Add(biologist);
        context.Sites.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.AddRange(glucose, crp);
        context.Patients.Add(patient);
        context.SampleStorageLocations.Add(SampleStorageLocation.Create(site.Id, "Réfrigérateur R1").Value);
        await context.SaveChangesAsync();

        return new Lab(site, biologist, glucose, crp, patient);
    }

    private static async Task<RegisteredOrder> RegisterAsync(ServiceProvider provider, Patient patient,
                                                             params AnalysisTest[] analyses)
    {
        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(provider,
            s => s.RegisterAsync(new RegisterOrderRequest
            {
                PatientId = patient.Id,
                AnalysisTestIds = [.. analyses.Select(a => a.Id)],
                PrescriberFreeText = "Dr Prescripteur"
            }));

        registered.IsSuccess.Should().BeTrue(string.Join(", ", registered.Errors.Select(e => e.ResourceKey)));
        return registered.Value;
    }

    private async Task<List<Guid>> ResultIdsAsync(Guid orderId)
    {
        await using var context = database.Context();
        return await context.TestResults.AsNoTracking().Where(r => r.LabOrderId == orderId)
                            .Select(r => r.Id).ToListAsync();
    }

    private async Task<AnalysisTest> TwoParameterAnalysisAsync(Lab lab)
    {
        var lipids = new AnalysisTest(Unique("LIP"), new MultilingualText("Bilan lipidique"),
                                      lab.Glucose.DepartmentId, lab.Glucose.SpecimenTypeId) { RoutineTurnaroundHours = 4 };
        foreach (var name in new[] { "Cholestérol", "Triglycérides" })
        {
            var parameter = new TestParameter(Unique("P"), new MultilingualText(name), ResultValueType.Numeric) { Decimals = 2 };
            parameter.AddReferenceRange(ReferenceRange.Numeric(0.1m, 10m));
            lipids.AddParameter(parameter);
        }
        lipids.AddTariff(new AnalysisTestTariff(lipids.Id, payerId: null, DateOnly.FromDateTime(DateTime.Today).AddYears(-1))
        {
            LetterKey = "B", Coefficient = 60m, LetterKeyValue = 1m, Price = 60m, IsVatExempt = true
        });

        await using var context = database.Context();
        context.AnalysisTests.Add(lipids);
        await context.SaveChangesAsync();
        return lipids;
    }

    private static async Task DrawAndReceiveAsync(ServiceProvider provider, Guid sampleId, decimal? volumeMl = null)
    {
        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = sampleId, VolumeMl = volumeMl }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = sampleId }]))).Value.Received.Should().Be(1);
    }

    // ---- 1. A tube refused after part of its analysis was printed ----------------------------------

    [SqlServerFact]
    public async Task A_ATubeWhoseAnalysisIsPartlyOnAnIssuedReport_IsNotRefused_AndStaysAsItWas()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));
        var lipids = await TwoParameterAnalysisAsync(lab);

        // A glucose and a lipid panel on one tube.
        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose, lipids);
        var tube = order.Labels.Should().ContainSingle().Subject;
        await DrawAndReceiveAsync(provider, tube.SampleId);

        var ids = await ResultIdsAsync(order.OrderId);
        ids.Should().HaveCount(3);
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [.. ids.Select(id => new ResultEntry(id, "2.5"))]))).Value.Accepted.Should().Be(3);
        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        // The biologist releases the glucose and the cholesterol, not the triglycerides, and a
        // partial report carries them: the lipid panel is partly printed.
        Guid glucoseResult, cholesterolResult;
        await using (var lookup = database.Context())
        {
            var glucoseItem = await lookup.LabOrderItems.AsNoTracking()
                .SingleAsync(i => i.LabOrderId == order.OrderId && i.AnalysisTestId == lab.Glucose.Id);
            glucoseResult = await lookup.TestResults.AsNoTracking()
                .Where(r => r.LabOrderItemId == glucoseItem.Id).Select(r => r.Id).SingleAsync();
            cholesterolResult = ids.First(id => id != glucoseResult);
        }

        (await On<ValidationService, Result<ValidationOutcome>>(provider, s => s.ValidateAsync(order.OrderId,
            [new ValidationDecision(glucoseResult, true), new ValidationDecision(cholesterolResult, true)])))
            .Value.Validated.Should().Be(2);
        var partial = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId));
        partial.IsSuccess.Should().BeTrue(partial.IsFailure ? partial.FirstError.ResourceKey : null);

        // A clot is then found: the refusal is declined, with or without a new draw, and nothing moves.
        foreach (var recollect in new[] { true, false })
        {
            var refused = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.RejectAsync(
                [new RejectSampleRequest(tube.SampleId, SampleRejectionReason.Clotted, "Caillot", recollect)]));
            refused.Value.Rejected.Should().Be(0);
            refused.Value.ReplacementLabels.Should().BeEmpty();
            refused.Value.Problems.Should().ContainSingle()
                .Which.ResourceKey.Should().Be("Sample_RejectHasReportedResults");
        }

        await using var context = database.Context();
        (await context.Samples.AsNoTracking().SingleAsync(s => s.Id == tube.SampleId))
            .Status.Should().Be(SampleStatus.Received);
        (await context.Samples.AsNoTracking().CountAsync(s => s.LabOrderId == order.OrderId)).Should().Be(1);
        (await context.LabOrderItems.AsNoTracking().Where(i => i.LabOrderId == order.OrderId).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(i => !i.IsCancelled);
    }

    [SqlServerFact]
    public async Task B_ATubeWhoseAnalysesAreNotPrinted_IsStillRefusedAfterEntry_WithItsReplacement()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();
        await DrawAndReceiveAsync(provider, tube.SampleId);

        var resultId = (await ResultIdsAsync(order.OrderId)).Single();
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "5")]))).Value.Accepted.Should().Be(1);

        var refused = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.RejectAsync(
            [new RejectSampleRequest(tube.SampleId, SampleRejectionReason.Haemolysed, null, true)]));
        refused.Value.Rejected.Should().Be(1);
        refused.Value.Problems.Should().BeEmpty();
        refused.Value.ReplacementLabels.Should().ContainSingle();
    }

    // ---- 2. The serum bank: discard and aliquots --------------------------------------------------

    [SqlServerFact]
    public async Task C_ATubeIsNotDiscarded_WhileAnAnalysisAwaitsItsValue_NorByThePurge()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose, lab.Crp);
        var tube = order.Labels.Single();
        await DrawAndReceiveAsync(provider, tube.SampleId);

        (await On<SampleWorkflowService, Result>(provider, s => s.DiscardAsync(tube.SampleId, "Rack purgé")))
            .FirstError.ResourceKey.Should().Be("Sample_DiscardAnalysesPending");

        // One of the two entered: the other still waits.
        var ids = await ResultIdsAsync(order.OrderId);
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(ids[0], "5")]))).Value.Accepted.Should().Be(1);
        (await On<SampleWorkflowService, Result>(provider, s => s.DiscardAsync(tube.SampleId, "Rack purgé")))
            .FirstError.ResourceKey.Should().Be("Sample_DiscardAnalysesPending");

        await using (var context = database.Context())
            (await context.Samples.AsNoTracking().SingleAsync(s => s.Id == tube.SampleId))
                .Status.Should().Be(SampleStatus.Received);

        // Both entered: the tube has served, and goes.
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(ids[1], "6")]))).Value.Accepted.Should().Be(1);
        (await On<SampleWorkflowService, Result>(provider, s => s.DiscardAsync(tube.SampleId, "Rack purgé")))
            .IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
            (await context.Samples.AsNoTracking().SingleAsync(s => s.Id == tube.SampleId))
                .Status.Should().Be(SampleStatus.Discarded);
    }

    [SqlServerFact]
    public async Task D_AliquotsCannotTakeMoreThanTheTubeHolds()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();
        await DrawAndReceiveAsync(provider, tube.SampleId, volumeMl: 5m);

        (await On<SampleWorkflowService, Result<string>>(provider,
            s => s.CreateAliquotAsync(tube.SampleId, 6m, null, null)))
            .FirstError.ResourceKey.Should().Be("Sample_AliquotVolumeExceedsTube");

        (await On<SampleWorkflowService, Result<string>>(provider,
            s => s.CreateAliquotAsync(tube.SampleId, 3m, "Sérothèque", null))).IsSuccess.Should().BeTrue();

        var over = await On<SampleWorkflowService, Result<string>>(provider,
            s => s.CreateAliquotAsync(tube.SampleId, 2.5m, "Sous-traitance", null));
        over.FirstError.ResourceKey.Should().Be("Sample_AliquotVolumeExceedsTube");
        over.FirstError.Args.Should().Equal("2");

        (await On<SampleWorkflowService, Result<string>>(provider,
            s => s.CreateAliquotAsync(tube.SampleId, 2m, "Sous-traitance", null))).IsSuccess.Should().BeTrue();

        // With no volume given, an aliquot is still split off: the volume is optional.
        (await On<SampleWorkflowService, Result<string>>(provider,
            s => s.CreateAliquotAsync(tube.SampleId, null, null, null))).IsSuccess.Should().BeTrue();

        await using var context = database.Context();
        (await context.SampleAliquots.AsNoTracking().CountAsync(a => a.ParentSampleId == tube.SampleId)).Should().Be(3);
    }

    // ---- 3. Who closes a recollection, who corrects a released value --------------------------------

    private static readonly HashSet<string> Phlebotomist =
    [
        Permissions.Patients.View, Permissions.Orders.View, Permissions.Samples.View,
        Permissions.Samples.Collect, Permissions.Samples.Reprint, Permissions.Samples.Dispatch
    ];

    private static readonly HashSet<string> Technician =
    [
        Permissions.Patients.View, Permissions.Orders.View, Permissions.Orders.Edit,
        Permissions.Samples.View, Permissions.Samples.Collect, Permissions.Samples.Receive,
        Permissions.Samples.Reject, Permissions.Samples.Dispatch,
        Permissions.Results.View, Permissions.Results.Enter, Permissions.Results.TechnicalValidate,
        Permissions.Reports.View
    ];

    [SqlServerFact]
    public async Task E_ANurseNeitherCallsBackNorAbandons_ATechnicianCallsButNeitherAbandonsNorCorrectsAReleasedValue()
    {
        var lab = await LabAsync();
        using var admin = Services(OperatorOf(lab));
        using var nurse = Services(OperatorOf(lab, Phlebotomist));
        using var technician = Services(OperatorOf(lab, Technician));

        var order = await RegisterAsync(admin, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();
        (await On<SampleWorkflowService, Result>(nurse, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId }]))).IsSuccess.Should().BeTrue();
        var refused = await On<SampleWorkflowService, Result<ReceptionOutcome>>(technician, s => s.RejectAsync(
            [new RejectSampleRequest(tube.SampleId, SampleRejectionReason.Clotted, null, true)]));
        var replacement = refused.Value.ReplacementLabels.Single();

        (await On<RecollectionService, Result>(nurse, s => s.RecordContactAsync(replacement.SampleId, RecallChannel.Phone, "Pas de réponse")))
            .FirstError.Code.Should().Be("forbidden");
        (await On<RecollectionService, Result>(technician, s => s.RecordContactAsync(replacement.SampleId, RecallChannel.Phone, "Pas de réponse")))
            .IsSuccess.Should().BeTrue();
        (await On<RecollectionService, Result>(technician, s => s.AbandonAsync(replacement.SampleId, "Patient injoignable")))
            .FirstError.Code.Should().Be("forbidden");

        // The patient comes back; the value is released, then a technician tries to correct it.
        await DrawAndReceiveAsync(admin, replacement.SampleId);
        var resultId = await ResultOfReplacementAsync(order.OrderId);
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(technician,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "5")]))).Value.Accepted.Should().Be(1);
        (await On<ResultEntryService, Result>(technician, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();
        (await On<ValidationService, Result<ValidationOutcome>>(admin,
            s => s.ValidateAsync(order.OrderId, [new ValidationDecision(resultId, true)]))).Value.Validated.Should().Be(1);

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(technician,
                s => s.CorrectReleasedAsync(order.OrderId, resultId, "6", "Erreur de saisie constatée au contrôle")))
            .FirstError.Code.Should().Be("forbidden");
        (await On<ReportReleaseService, Result<ReleasedReport>>(technician, s => s.ReleaseAsync(order.OrderId)))
            .FirstError.Code.Should().Be("forbidden");
    }

    // ---- 4. A report handed to a third party ------------------------------------------------------

    [SqlServerFact]
    public async Task F_AThirdPartyDelivery_NeedsThisPatientsDisclosureConsent_StillInForce()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();
        await DrawAndReceiveAsync(provider, tube.SampleId);
        var resultId = (await ResultIdsAsync(order.OrderId)).Single();
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "5")]))).Value.Accepted.Should().Be(1);
        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();
        (await On<ValidationService, Result<ValidationOutcome>>(provider,
            s => s.ValidateAsync(order.OrderId, [new ValidationDecision(resultId, true)]))).Value.Validated.Should().Be(1);
        var report = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId));
        report.IsSuccess.Should().BeTrue(report.IsFailure ? report.FirstError.ResourceKey : null);

        // Consents on file: another patient's, this patient's for another purpose, a withdrawn one,
        // and the right one.
        var now = DateTimeOffset.Now;
        var other = new Patient(Unique("P"), "ALAOUI", "Sara", new DateOnly(1980, 1, 1), Gender.Female, lab.Site.Id)
        {
            SearchKey = PatientSearchKey.Build("ALAOUI", "Sara")
        };
        var othersConsent = new PatientConsent(other.Id, ConsentPurpose.ThirdPartyDisclosure, ConsentChannel.WrittenForm, now, lab.Biologist.Id);
        var wrongPurpose = new PatientConsent(lab.Patient.Id, ConsentPurpose.ElectronicDelivery, ConsentChannel.WrittenForm, now, lab.Biologist.Id);
        var withdrawn = new PatientConsent(lab.Patient.Id, ConsentPurpose.ThirdPartyDisclosure, ConsentChannel.WrittenForm, now.AddDays(-30), lab.Biologist.Id);
        withdrawn.Withdraw(now.AddDays(-1), lab.Biologist.Id, "Retrait par le patient");
        var valid = new PatientConsent(lab.Patient.Id, ConsentPurpose.ThirdPartyDisclosure, ConsentChannel.WrittenForm, now, lab.Biologist.Id);

        await using (var context = database.Context())
        {
            context.Patients.Add(other);
            context.PatientConsents.AddRange(othersConsent, wrongPurpose, withdrawn, valid);
            await context.SaveChangesAsync();
        }

        Task<Result<DeliveryOutcome>> Deliver(Guid consent) =>
            On<ReportReleaseService, Result<DeliveryOutcome>>(provider, s => s.RecordDeliveryAsync(
                report.Value.ReportId, DeliveryChannel.PrintedAtCounter, ReportRecipientKind.AuthorizedThirdParty,
                "Employeur", null, "EL FASSI Ali", "AB123456", consent));

        foreach (var refused in new[] { Guid.NewGuid(), othersConsent.Id, wrongPurpose.Id, withdrawn.Id })
            (await Deliver(refused)).FirstError.ResourceKey.Should().Be("Report_ThirdPartyConsentInvalid");

        (await Deliver(valid.Id)).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
            (await context.ReportDeliveries.AsNoTracking().Where(d => d.LabReportId == report.Value.ReportId).ToListAsync())
                .Should().ContainSingle().Which.AuthorizingConsentId.Should().Be(valid.Id);
    }

    private async Task<Guid> ResultOfReplacementAsync(Guid orderId)
    {
        await using var context = database.Context();
        var live = await context.LabOrderItems.AsNoTracking()
            .Where(i => i.LabOrderId == orderId && i.Status != LabOrderItemStatus.Cancelled)
            .Select(i => i.Id).SingleAsync();
        return await context.TestResults.AsNoTracking().Where(r => r.LabOrderItemId == live).Select(r => r.Id).SingleAsync();
    }
}
