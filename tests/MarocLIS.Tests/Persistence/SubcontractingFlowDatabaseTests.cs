using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Application.Steering;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own for the subcontracting circuit, created from the model and dropped
/// at the end. Foreign keys are switched off, as in <see cref="Clinical4ScratchDatabase"/>.
/// </summary>
public sealed class SubcontractingScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Soustraitance_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public SubcontractingScratchDatabase()
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

    /// <summary>A context with no signed-in user: no site filter, as a maintenance routine reads.</summary>
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
/// The send-out circuit end to end, against SQL Server and the services as the desktop composes
/// them: an analysis the catalogue sends to a partner laboratory is registered and invoiced as any
/// other, collected and received, listed to send, sent on a numbered slip (one per partner), its
/// partner reference kept, its results entered from the partner's report — which takes it off the
/// list and flags each value — validated and released on a report that names the partner. Then the
/// refusals, cancellation after sending, site scoping and the dispatch right.
/// </summary>
public sealed class SubcontractingFlowDatabaseTests(SubcontractingScratchDatabase database)
    : IClassFixture<SubcontractingScratchDatabase>
{
    /// <summary>A signed-in operator whose site, sites and rights each test sets.</summary>
    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; set; }
        public string UserName => "biologiste";
        public string FullName => "Dr Sous-traitance";
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
        public string Workstation => "PAILLASSE-ST";
    }

    /// <summary>Keeps the model of each report released, so its content can be read back.</summary>
    private sealed class CapturingRenderer : IReportRenderer
    {
        public List<ReportModel> Models { get; } = [];

        public byte[] Render(ReportModel model)
        {
            Models.Add(model);
            return [0x25, 0x50, 0x44, 0x46, (byte)Models.Count];
        }

        public int CountPages(byte[] pdf) => 1;
    }

    private sealed class AllFonts : IFontAvailability
    {
        public string ArabicFamily => "Arabic";
        public string LatinFamily => "Latin";
        public bool IsAvailable(string family) => true;
        public IReadOnlyList<string> MissingFrom(IEnumerable<string> families) => [];
    }

    /// <summary>One laboratory, its biologist, two partners and a small catalogue.</summary>
    private sealed record Lab(
        LabSite Site,
        AppUser Biologist,
        LabSite Partner,
        LabSite OtherPartner,
        AnalysisTest VitaminD,
        AnalysisTest Allergy,
        AnalysisTest Glucose,
        Patient Patient);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static Operator OperatorOf(Lab lab) => new()
    {
        UserId = lab.Biologist.Id,
        SiteId = lab.Site.Id,
        SiteCode = lab.Site.Code,
        AccessibleSiteIds = [lab.Site.Id]
    };

    /// <summary>The desktop's composition: application services, and queries behind the proxy.</summary>
    private ServiceProvider Services(Operator user, CapturingRenderer? renderer = null)
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
        services.AddSingleton<IReportRenderer>(renderer ?? new CapturingRenderer());
        services.AddSingleton<IFontAvailability, AllFonts>();
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IValidationQueries, ValidationQueries>();
        services.AddIsolatedQueries<IReportQueries, ReportQueries>();
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ISampleQueries, SampleQueries>();
        services.AddIsolatedQueries<ISubcontractQueries, SubcontractQueries>();
        services.AddIsolatedQueries<IBillingQueries, BillingQueries>();
        services.AddIsolatedQueries<ICostQueries, CostQueries>();
        return services.BuildServiceProvider();
    }

    /// <summary>Runs one use case in a scope of its own, as a screen does.</summary>
    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static LabSite Laboratory(Guid biologistId) => new(Unique("L"), new MultilingualText("Laboratoire test"),
                                                               LabSiteKind.MainLaboratory)
    {
        LegalName = "LABORATOIRE TEST SARL",
        // Unique per site: the ICE carries a unique index.
        Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
        FiscalId = "45128796",
        AddressLine = new MultilingualText("1, rue du Test"),
        City = "Casablanca",
        HealthMinistryAuthorizationNumber = "AUT-TEST-1",
        ResponsibleBiologistUserId = biologistId
    };

    private static LabSite Partner(string name) =>
        new(Unique("P"), new MultilingualText(name), LabSiteKind.ReferralPartner);

    private static AnalysisTest Analysis(string code, string name, Department department, SpecimenType specimen,
                                         LabSite? partner, decimal price, DateOnly validFrom)
    {
        var analysis = new AnalysisTest(Unique(code), new MultilingualText(name), department.Id, specimen.Id)
        {
            RoutineTurnaroundHours = partner is null ? 4 : 96,
            IsSubcontracted = partner is not null,
            SubcontractorSiteId = partner?.Id
        };

        var parameter = new TestParameter(Unique(code), new MultilingualText(name), ResultValueType.Numeric)
        {
            Decimals = 1
        };
        parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
        analysis.AddParameter(parameter);

        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom)
        {
            LetterKey = "B",
            Coefficient = price,
            LetterKeyValue = 1m,
            Price = price,
            IsVatExempt = true
        });

        analysis.Validate().IsSuccess.Should().BeTrue();
        return analysis;
    }

    private async Task<Lab> LabAsync()
    {
        var biologist = new AppUser(Unique("bio"), "Dr Sous-traitance", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-TEST-1"
        };

        var site = Laboratory(biologist.Id);
        var partner = Partner("Laboratoire partenaire A");
        var other = Partner("Laboratoire partenaire B");

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var vitaminD = Analysis("VITD", "25-OH vitamine D", department, specimen, partner, 300m, validFrom);
        var allergy = Analysis("IGE", "IgE spécifiques (panel)", department, specimen, other, 450m, validFrom);
        var glucose = Analysis("GLY", "Glycémie", department, specimen, null, 20m, validFrom);

        var patient = new Patient(Unique("P"), "BENNANI", "Karim", new DateOnly(1975, 3, 2), Gender.Male, site.Id);

        await using var context = database.Context();

        context.Users.Add(biologist);
        context.Sites.AddRange(site, partner, other);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.AddRange(vitaminD, allergy, glucose);
        context.Patients.Add(patient);
        await context.SaveChangesAsync();

        return new Lab(site, biologist, partner, other, vitaminD, allergy, glucose, patient);
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

    private static async Task CollectAndReceiveAsync(ServiceProvider provider, RegisteredOrder order)
    {
        var tubes = order.Labels.Select(l => l.SampleId).ToList();

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
                [.. tubes.Select(id => new RecordCollectionRequest { SampleId = id })])))
            .IsSuccess.Should().BeTrue();

        var received = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider,
            s => s.ReceiveAsync([.. tubes.Select(id => new ReceiveSampleRequest { SampleId = id })]));

        received.IsSuccess.Should().BeTrue();
        received.Value.Received.Should().Be(tubes.Count);
    }

    private async Task<LabOrderItem> ItemAsync(Guid orderId, AnalysisTest analysis)
    {
        await using var context = database.Context();
        return await context.LabOrderItems.AsNoTracking()
            .SingleAsync(i => i.LabOrderId == orderId && i.AnalysisTestId == analysis.Id && i.Status != LabOrderItemStatus.Cancelled);
    }

    private static async Task<IReadOnlyList<SubcontractRow>> WorklistAsync(ServiceProvider provider)
    {
        var list = await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(provider,
            s => s.GetWorklistAsync());
        list.IsSuccess.Should().BeTrue();
        return list.Value;
    }

    private static Task<Result<IReadOnlyList<SubcontractDispatch>>> DispatchAsync(
        ServiceProvider provider, Guid? partnerOverride, params Guid[] itemIds) =>
        On<SubcontractingService, Result<IReadOnlyList<SubcontractDispatch>>>(provider,
            s => s.DispatchAsync(itemIds, partnerOverride));

    /// <summary>Enters a value on every result of the given item, as from the partner's report.</summary>
    private async Task<ResultEntryOutcome> EnterAsync(ServiceProvider provider, Guid orderId, Guid itemId,
                                                      string value)
    {
        List<Guid> rows;
        await using (var context = database.Context())
            rows = await context.TestResults.AsNoTracking()
                .Where(r => r.LabOrderItemId == itemId).Select(r => r.Id).ToListAsync();

        rows.Should().NotBeEmpty();

        var entered = await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(orderId, [.. rows.Select(id => new ResultEntry(id, value))]));

        entered.IsSuccess.Should().BeTrue();
        return entered.Value;
    }

    private async Task ValidateAllAsync(ServiceProvider provider, Guid orderId)
    {
        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(orderId, [])))
            .IsSuccess.Should().BeTrue();

        List<Guid> rows;
        await using (var context = database.Context())
            rows = await context.TestResults.AsNoTracking()
                .Where(r => r.LabOrderId == orderId && r.Status == TestResultStatus.TechnicallyValidated)
                .Select(r => r.Id).ToListAsync();

        var validated = await On<ValidationService, Result<ValidationOutcome>>(provider,
            s => s.ValidateAsync(orderId, [.. rows.Select(id => new ValidationDecision(id, true))]));

        validated.IsSuccess.Should().BeTrue(validated.IsFailure ? validated.FirstError.ResourceKey : null);
        validated.Value.Blocked.Should().BeEmpty();
        validated.Value.Validated.Should().Be(rows.Count);
    }

    // ---- 1. The whole circuit -------------------------------------------------------------------

    [SqlServerFact]
    public async Task A_SubcontractedAnalysis_GoesFromRegistrationToAReportNamingThePartner()
    {
        var lab = await LabAsync();
        var renderer = new CapturingRenderer();
        using var provider = Services(OperatorOf(lab), renderer);

        // Registration: the subcontracted act is priced and invoiced like any other.
        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD, lab.Glucose);
        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);
        var glucose = await ItemAsync(order.OrderId, lab.Glucose);

        vitD.IsSubcontracted.Should().BeTrue();
        vitD.SubcontractorSiteId.Should().Be(lab.Partner.Id);
        vitD.ChargedPrice.Should().Be(300m);
        glucose.IsSubcontracted.Should().BeFalse();
        order.Labels.Should().ContainSingle("both analyses share the serum tube");

        var invoice = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync([order.OrderId]));
        invoice.IsSuccess.Should().BeTrue(invoice.IsFailure ? invoice.FirstError.ResourceKey : null);

        await using (var context = database.Context())
        {
            var lines = await context.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoice.Value).ToListAsync();
            lines.Should().Contain(l => l.LabOrderItemId == vitD.Id);
            (await context.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Value))
                .TotalAmount.Should().Be(320m);
        }

        // Not on the list before the tube is in the laboratory.
        (await WorklistAsync(provider)).Should().BeEmpty();

        await CollectAndReceiveAsync(provider, order);

        // To send: the subcontracted act only, with its partner and the promised date.
        var toSend = (await WorklistAsync(provider)).Should().ContainSingle().Subject;
        toSend.ItemId.Should().Be(vitD.Id);
        toSend.Stage.Should().Be(SubcontractStage.ToSend);
        toSend.PartnerSiteId.Should().Be(lab.Partner.Id);
        toSend.PartnerCode.Should().Be(lab.Partner.Code);
        toSend.AccessionNumber.Should().Be(order.AccessionNumber);
        toSend.Barcode.Should().Be(order.Labels[0].Barcode);
        toSend.ReceivedAt.Should().NotBeNull();
        toSend.DueAt.Should().BeAfter(toSend.ReceivedAt!.Value.AddHours(95));

        // Dispatch: one slip for the one partner.
        var dispatched = await DispatchAsync(provider, null, vitD.Id);
        dispatched.IsSuccess.Should().BeTrue();
        var slip = dispatched.Value.Should().ContainSingle().Subject;
        slip.Number.Should().StartWith($"ST/{DateTime.Today.Year}/");
        slip.PartnerSiteId.Should().Be(lab.Partner.Id);
        slip.ItemCount.Should().Be(1);

        var awaiting = (await WorklistAsync(provider)).Should().ContainSingle().Subject;
        awaiting.Stage.Should().Be(SubcontractStage.AwaitingReturn);
        awaiting.DispatchNumber.Should().Be(slip.Number);
        awaiting.SentAt.Should().NotBeNull();

        await using (var context = database.Context())
        {
            var sent = await context.LabOrderItems.AsNoTracking().SingleAsync(i => i.Id == vitD.Id);
            sent.SubcontractSentByUserId.Should().Be(lab.Biologist.Id);

            var events = await context.SampleEvents.AsNoTracking()
                .Where(e => e.SampleId == order.Labels[0].SampleId && e.Kind == SampleEventKind.SentToSubcontractor)
                .ToListAsync();
            events.Should().ContainSingle().Which.Detail.Should().Be($"{slip.Number} → {lab.Partner.Code}");
        }

        // The slip prints, and reprints, the same.
        var printed = await On<SubcontractingService, Result<SubcontractSlip>>(provider, s => s.GetSlipAsync(slip.Number));
        printed.IsSuccess.Should().BeTrue();
        printed.Value.Partner.Id.Should().Be(lab.Partner.Id);
        printed.Value.SentByName.Should().Be("Dr Sous-traitance");
        printed.Value.Items.Should().ContainSingle().Which.AnalysisCode.Should().Be(lab.VitaminD.Code);

        // The partner's own number, trimmed.
        (await On<SubcontractingService, Result>(provider, s => s.SetPartnerReferenceAsync(vitD.Id, "  CERBA-778812 ")))
            .IsSuccess.Should().BeTrue();
        (await WorklistAsync(provider)).Single().PartnerReference.Should().Be("CERBA-778812");

        // Cleared by an empty entry, then put back.
        (await On<SubcontractingService, Result>(provider, s => s.SetPartnerReferenceAsync(vitD.Id, "  ")))
            .IsSuccess.Should().BeTrue();
        (await WorklistAsync(provider)).Single().PartnerReference.Should().BeNull();
        (await On<SubcontractingService, Result>(provider, s => s.SetPartnerReferenceAsync(vitD.Id, "CERBA-778812")))
            .IsSuccess.Should().BeTrue();

        // Return: the partner's value is entered, the act leaves the list, the value is flagged.
        await EnterAsync(provider, order.OrderId, vitD.Id, "18.5");
        await EnterAsync(provider, order.OrderId, glucose.Id, "0.95");

        (await WorklistAsync(provider)).Should().BeEmpty();

        await using (var context = database.Context())
        {
            var returned = await context.LabOrderItems.AsNoTracking().SingleAsync(i => i.Id == vitD.Id);
            returned.SubcontractReturnedAt.Should().NotBeNull();
            returned.SubcontractorReference.Should().Be("CERBA-778812");

            var results = await context.TestResults.AsNoTracking().Where(r => r.LabOrderId == order.OrderId).ToListAsync();
            results.Where(r => r.LabOrderItemId == vitD.Id).Should().OnlyContain(r => r.Flags.HasFlag(ResultFlag.Subcontracted));
            results.Where(r => r.LabOrderItemId == glucose.Id).Should().OnlyContain(r => !r.Flags.HasFlag(ResultFlag.Subcontracted));
        }

        // Validation and release: the report says the analysis was subcontracted, and to whom.
        await ValidateAllAsync(provider, order.OrderId);

        var released = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId));
        released.IsSuccess.Should().BeTrue(released.IsFailure ? released.FirstError.ResourceKey : null);

        await using (var context = database.Context())
            (await context.LabReports.AsNoTracking().SingleAsync(r => r.Id == released.Value.ReportId))
                .ContainsSubcontractedResults.Should().BeTrue();

        var model = renderer.Models.Should().ContainSingle().Subject;
        model.ContainsSubcontractedResults.Should().BeTrue();

        var blocks = model.Sections.SelectMany(s => s.Analyses).ToList();
        var vitDBlock = blocks.Single(b => b.AnalysisCode == lab.VitaminD.Code);
        vitDBlock.IsSubcontracted.Should().BeTrue();
        vitDBlock.SubcontractorName.Should().Be("Laboratoire partenaire A");
        vitDBlock.Lines.Should().OnlyContain(l => l.Flags.HasFlag(ResultFlag.Subcontracted));

        var glucoseBlock = blocks.Single(b => b.AnalysisCode == lab.Glucose.Code);
        glucoseBlock.IsSubcontracted.Should().BeFalse();
        glucoseBlock.SubcontractorName.Should().BeNull();
    }

    [SqlServerFact]
    public async Task A_TheCostActivity_LeavesTheSubcontractedAnalysisOut()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD, lab.Glucose);

        await using var context = database.Context();
        var activity = await new CostQueries(context).GetActivityAsync(
            [lab.Site.Id], DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

        activity.Select(a => a.AnalysisTestId).Should().Equal(lab.Glucose.Id);
        _ = order;
    }

    // ---- 2. Partners: the catalogue's, another chosen at the counter, two at once -----------------

    [SqlServerFact]
    public async Task B_TwoPartnersInOneSelection_GiveTwoSlips_EachWithItsOwnPartner()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD, lab.Allergy);
        await CollectAndReceiveAsync(provider, order);

        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);
        var allergy = await ItemAsync(order.OrderId, lab.Allergy);

        (await WorklistAsync(provider)).Should().HaveCount(2).And.OnlyContain(r => r.Stage == SubcontractStage.ToSend);

        var dispatched = await DispatchAsync(provider, null, vitD.Id, allergy.Id);
        dispatched.IsSuccess.Should().BeTrue();
        dispatched.Value.Should().HaveCount(2);
        dispatched.Value.Select(d => d.Number).Should().OnlyHaveUniqueItems()
            .And.OnlyContain(n => n.StartsWith($"ST/{DateTime.Today.Year}/"));
        dispatched.Value.Select(d => d.PartnerSiteId).Should().BeEquivalentTo([lab.Partner.Id, lab.OtherPartner.Id]);

        foreach (var dispatch in dispatched.Value)
        {
            var slip = await On<SubcontractingService, Result<SubcontractSlip>>(provider, s => s.GetSlipAsync(dispatch.Number));
            slip.IsSuccess.Should().BeTrue();
            slip.Value.Partner.Id.Should().Be(dispatch.PartnerSiteId);
            slip.Value.Items.Should().ContainSingle();
        }

        await using var context = database.Context();
        (await context.SampleEvents.AsNoTracking()
                .CountAsync(e => e.SampleId == order.Labels[0].SampleId && e.Kind == SampleEventKind.SentToSubcontractor))
            .Should().Be(2, "the one tube travelled on two slips, one per partner");
    }

    [SqlServerFact]
    public async Task B_APartnerChosenAtDispatch_ReplacesTheCataloguesForTheSlipAndTheReport()
    {
        var lab = await LabAsync();
        var renderer = new CapturingRenderer();
        using var provider = Services(OperatorOf(lab), renderer);

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD);
        await CollectAndReceiveAsync(provider, order);
        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);

        var dispatched = await DispatchAsync(provider, lab.OtherPartner.Id, vitD.Id);
        dispatched.IsSuccess.Should().BeTrue();
        dispatched.Value.Single().PartnerSiteId.Should().Be(lab.OtherPartner.Id);

        (await WorklistAsync(provider)).Single().PartnerSiteId.Should().Be(lab.OtherPartner.Id);

        var slip = await On<SubcontractingService, Result<SubcontractSlip>>(provider,
            s => s.GetSlipAsync(dispatched.Value.Single().Number));
        slip.Value.Partner.Id.Should().Be(lab.OtherPartner.Id);

        await EnterAsync(provider, order.OrderId, vitD.Id, "42");
        await ValidateAllAsync(provider, order.OrderId);

        (await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId)))
            .IsSuccess.Should().BeTrue();

        renderer.Models.Single().Sections.SelectMany(s => s.Analyses).Single()
            .SubcontractorName.Should().Be("Laboratoire partenaire B");
    }

    // ---- 3. Refusals ------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task C_DispatchRefusals_SayWhatIsWrong_AndSendNothing()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD, lab.Glucose);
        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);
        var glucose = await ItemAsync(order.OrderId, lab.Glucose);

        // Nothing ticked.
        var nothing = await DispatchAsync(provider, null);
        nothing.FirstError.ResourceKey.Should().Be("Subcontract_NothingSelected");

        // An item that does not exist.
        (await DispatchAsync(provider, null, Guid.NewGuid())).FirstError.ResourceKey
            .Should().Be("Subcontract_NothingSelected");

        // The tube is not in the laboratory yet.
        var early = await DispatchAsync(provider, null, vitD.Id);
        early.FirstError.ResourceKey.Should().Be("Subcontract_NotReceived");
        early.FirstError.Args.Should().Contain(order.AccessionNumber);

        await CollectAndReceiveAsync(provider, order);

        // A partner override that is not a partner laboratory.
        (await DispatchAsync(provider, lab.Site.Id, vitD.Id)).FirstError.ResourceKey
            .Should().Be("Subcontract_NoPartner");

        // An analysis the laboratory performs itself.
        var local = await DispatchAsync(provider, null, glucose.Id);
        local.FirstError.ResourceKey.Should().Be("Subcontract_NotSubcontracted");
        (await DispatchAsync(provider, lab.Partner.Id, glucose.Id)).FirstError.ResourceKey
            .Should().Be("Subcontract_NotSubcontracted");

        // One bad act in the batch sends none of them, and gives its number back.
        var next = await NextSlipNumberAsync();
        (await DispatchAsync(provider, lab.Partner.Id, vitD.Id, glucose.Id)).IsFailure.Should().BeTrue();
        (await WorklistAsync(provider)).Single().Stage.Should().Be(SubcontractStage.ToSend);

        var sent = await DispatchAsync(provider, null, vitD.Id);
        sent.IsSuccess.Should().BeTrue();
        sent.Value.Single().Number.Should().Be(next, "the refused batch consumed no number");

        // Already sent.
        var again = await DispatchAsync(provider, null, vitD.Id);
        again.FirstError.ResourceKey.Should().Be("Subcontract_AlreadySent");

        // Slip unknown.
        var slip = await On<SubcontractingService, Result<SubcontractSlip>>(provider, s => s.GetSlipAsync("ST/1999/99999"));
        slip.FirstError.ResourceKey.Should().Be("Subcontract_SlipNotFound");

        // Reference over 60 characters, after trimming.
        var tooLong = await On<SubcontractingService, Result>(provider,
            s => s.SetPartnerReferenceAsync(vitD.Id, new string('R', 61)));
        tooLong.FirstError.ResourceKey.Should().Be("Subcontract_ReferenceTooLong");
        (await On<SubcontractingService, Result>(provider,
                s => s.SetPartnerReferenceAsync(vitD.Id, "  " + new string('R', 60) + "  ")))
            .IsSuccess.Should().BeTrue();

        // A reference on an act that is not subcontracted, or does not exist.
        (await On<SubcontractingService, Result>(provider, s => s.SetPartnerReferenceAsync(glucose.Id, "X1")))
            .FirstError.ResourceKey.Should().Be("Subcontract_NotSubcontracted");
        (await On<SubcontractingService, Result>(provider, s => s.SetPartnerReferenceAsync(Guid.NewGuid(), "X1")))
            .FirstError.ResourceKey.Should().Be("Subcontract_NothingSelected");
    }

    [SqlServerFact]
    public async Task C_AnAnalysisWhosePartnerWasWithdrawn_AsksForAnotherPartner()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD);
        await CollectAndReceiveAsync(provider, order);
        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);

        await using (var context = database.Context())
        {
            var partner = await context.Sites.SingleAsync(s => s.Id == lab.Partner.Id);
            partner.IsActive = false;
            await context.SaveChangesAsync();
        }

        (await DispatchAsync(provider, null, vitD.Id)).FirstError.ResourceKey.Should().Be("Subcontract_NoPartner");

        var redirected = await DispatchAsync(provider, lab.OtherPartner.Id, vitD.Id);
        redirected.IsSuccess.Should().BeTrue();
        redirected.Value.Single().PartnerSiteId.Should().Be(lab.OtherPartner.Id);
    }

    // ---- 4. Cancelling ----------------------------------------------------------------------------

    [SqlServerFact]
    public async Task D_AnActCancelledBeforeOrAfterDispatch_LeavesTheListAndTakesNoResult()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD, lab.Allergy, lab.Glucose);
        await CollectAndReceiveAsync(provider, order);
        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);
        var allergy = await ItemAsync(order.OrderId, lab.Allergy);

        // Before dispatch: it cannot be sent any more.
        (await On<OrderAmendmentService, Result>(provider,
                s => s.CancelItemAsync(order.OrderId, allergy.Id, "Prescription retirée")))
            .IsSuccess.Should().BeTrue();
        (await WorklistAsync(provider)).Select(r => r.ItemId).Should().Equal(vitD.Id);
        (await DispatchAsync(provider, null, allergy.Id)).FirstError.ResourceKey.Should().Be("Subcontract_ItemCancelled");

        // After dispatch: off the list awaiting return, and the partner's late answer is refused.
        var sent = await DispatchAsync(provider, null, vitD.Id);
        sent.IsSuccess.Should().BeTrue();

        (await On<OrderAmendmentService, Result>(provider,
                s => s.CancelItemAsync(order.OrderId, vitD.Id, "Tube perdu au transport")))
            .IsSuccess.Should().BeTrue();
        (await WorklistAsync(provider)).Should().BeEmpty();

        List<Guid> rows;
        await using (var context = database.Context())
            rows = await context.TestResults.AsNoTracking().Where(r => r.LabOrderItemId == vitD.Id)
                .Select(r => r.Id).ToListAsync();

        var late = await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [.. rows.Select(id => new ResultEntry(id, "20"))]));
        late.Value.Accepted.Should().Be(0);
        late.Value.Rejected.Should().OnlyContain(e => e.ResourceKey == "Result_ItemCancelled");

        // The slip is a record of what left: it still lists the act.
        var slip = await On<SubcontractingService, Result<SubcontractSlip>>(provider,
            s => s.GetSlipAsync(sent.Value.Single().Number));
        slip.Value.Items.Should().ContainSingle(i => i.ItemId == vitD.Id);
    }

    [SqlServerFact]
    public async Task D_AnOrderCancelledAfterDispatch_LeavesTheList()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD);
        await CollectAndReceiveAsync(provider, order);
        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);

        (await DispatchAsync(provider, null, vitD.Id)).IsSuccess.Should().BeTrue();
        (await WorklistAsync(provider)).Should().ContainSingle();

        (await On<OrderAmendmentService, Result>(provider, s => s.CancelOrderAsync(order.OrderId, "Patient décédé")))
            .IsSuccess.Should().BeTrue();

        (await WorklistAsync(provider)).Should().BeEmpty();
    }

    // ---- 5. Sites ---------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task E_AnotherSitesActs_AreNeitherListedNorSentNorPrinted_FromThisSite()
    {
        var lab = await LabAsync();
        var other = await LabAsync();

        using var atOther = Services(OperatorOf(other));
        var theirs = await RegisterAsync(atOther, other.Patient, other.VitaminD);
        await CollectAndReceiveAsync(atOther, theirs);
        var theirItem = await ItemAsync(theirs.OrderId, other.VitaminD);

        // Signed in to this site, with access to both.
        var user = OperatorOf(lab);
        user.UserId = lab.Biologist.Id;
        user.AccessibleSiteIds = [lab.Site.Id, other.Site.Id];
        using var here = Services(user);

        (await WorklistAsync(here)).Should().BeEmpty();

        // Sent from here, it would take this site's slip number and print on no slip of either.
        (await DispatchAsync(here, null, theirItem.Id)).FirstError.ResourceKey.Should().Be("Subcontract_NothingSelected");
        (await On<SubcontractingService, Result>(here, s => s.SetPartnerReferenceAsync(theirItem.Id, "X")))
            .FirstError.ResourceKey.Should().Be("Subcontract_NothingSelected");

        // Their own slip.
        var theirSlip = await DispatchAsync(atOther, null, theirItem.Id);
        theirSlip.Value.Single().Number.Should().StartWith($"ST/{DateTime.Today.Year}/");

        // This site's slip takes the next number of the series all sites share.
        var ours = await RegisterAsync(here, lab.Patient, lab.VitaminD);
        await CollectAndReceiveAsync(here, ours);
        var ourItem = await ItemAsync(ours.OrderId, lab.VitaminD);
        var ourSlip = await DispatchAsync(here, null, ourItem.Id);
        ourSlip.Value.Single().Number.Should().NotBe(theirSlip.Value.Single().Number);

        // Reprinted here, it is this site's slip — its acts and its sender.
        var printed = await On<SubcontractingService, Result<SubcontractSlip>>(here,
            s => s.GetSlipAsync(ourSlip.Value.Single().Number));
        printed.Value.Items.Select(i => i.ItemId).Should().Equal(ourItem.Id);
        printed.Value.SentByName.Should().Be(lab.Biologist.FullName);

        (await WorklistAsync(here)).Select(r => r.ItemId).Should().Equal(ourItem.Id);
    }

    [SqlServerFact]
    public async Task E_TheSlipNamesWhoeverSentIt_NotTheSenderOfTheSameNumberAtAnotherSite()
    {
        var lab = await LabAsync();
        var other = await LabAsync();

        // The other site sends first, under its own biologist.
        await using (var context = database.Context())
        {
            var sender = await context.Users.SingleAsync(u => u.Id == other.Biologist.Id);
            sender.FullName = "Dr Autre Site";
            await context.SaveChangesAsync();
        }

        // Several acts on their slip, so the wrong sender is not found only by luck of the row order.
        using var atOther = Services(OperatorOf(other));
        var theirItems = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var theirs = await RegisterAsync(atOther, other.Patient, other.VitaminD, other.Allergy);
            await CollectAndReceiveAsync(atOther, theirs);
            theirItems.Add((await ItemAsync(theirs.OrderId, other.VitaminD)).Id);
            theirItems.Add((await ItemAsync(theirs.OrderId, other.Allergy)).Id);
        }

        var theirNumber = (await DispatchAsync(atOther, other.Partner.Id, [.. theirItems])).Value
            .Should().ContainSingle().Which.Number;

        using var here = Services(OperatorOf(lab));
        var ours = await RegisterAsync(here, lab.Patient, lab.VitaminD);
        await CollectAndReceiveAsync(here, ours);
        var sent = await DispatchAsync(here, null, (await ItemAsync(ours.OrderId, lab.VitaminD)).Id);

        var slip = await On<SubcontractingService, Result<SubcontractSlip>>(here,
            s => s.GetSlipAsync(sent.Value.Single().Number));

        slip.Value.SentByName.Should().Be("Dr Sous-traitance");
        // All sites now number their slips from one counter, so a number names one slip only.
        sent.Value.Single().Number.Should().NotBe(theirNumber);
    }

    private async Task<string> NextSlipNumberAsync()
    {
        await using var context = database.Context();
        var next = await context.NumberSequences.AsNoTracking()
            .Where(s => s.Purpose == SequencePurpose.SubcontractDispatch && s.SiteId == null
                        && s.Year == DateTime.Today.Year)
            .Select(s => (long?)s.NextValue)
            .FirstOrDefaultAsync() ?? 1;
        return $"ST/{DateTime.Today.Year}/{next:00000}";
    }

    // ---- 6. Rights --------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task F_SendingAndTheReference_NeedTheDispatchRight_TheListAndTheSlipOnlyTheView()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD);
        await CollectAndReceiveAsync(provider, order);
        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);

        var viewer = OperatorOf(lab);
        viewer.Granted = [Permissions.Samples.View];
        using var asViewer = Services(viewer);

        (await WorklistAsync(asViewer)).Should().ContainSingle();
        (await DispatchAsync(asViewer, null, vitD.Id)).FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await On<SubcontractingService, Result>(asViewer, s => s.SetPartnerReferenceAsync(vitD.Id, "X")))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        var dispatcher = OperatorOf(lab);
        dispatcher.Granted = [Permissions.Samples.View, Permissions.Samples.Subcontract];
        using var asDispatcher = Services(dispatcher);

        var sent = await DispatchAsync(asDispatcher, null, vitD.Id);
        sent.IsSuccess.Should().BeTrue();

        (await On<SubcontractingService, Result<SubcontractSlip>>(asViewer, s => s.GetSlipAsync(sent.Value.Single().Number)))
            .IsSuccess.Should().BeTrue();

        var nobody = OperatorOf(lab);
        nobody.Granted = [];
        using var asNobody = Services(nobody);

        (await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(asNobody, s => s.GetWorklistAsync()))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await On<SubcontractingService, Result<SubcontractSlip>>(asNobody, s => s.GetSlipAsync(sent.Value.Single().Number)))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
    }

    // ---- 7. An analysis added to a tube already in the laboratory ---------------------------------

    [SqlServerFact]
    public async Task G_AnAnalysisAddedToAReceivedTube_IsListedToSend_AndCompletesOnReturn()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        await CollectAndReceiveAsync(provider, order);

        // The biologist adds a vitamin D on the serum already received: no second needle.
        var added = await On<OrderAmendmentService, Result<OrderAmendment>>(provider,
            s => s.AddAnalysesAsync(order.OrderId, [lab.VitaminD.Id], [], "Ajout à la demande du prescripteur"));
        added.IsSuccess.Should().BeTrue(added.IsFailure ? added.FirstError.ResourceKey : null);
        added.Value.Labels.Should().BeEmpty("the tube already drawn is reused");

        var vitD = await ItemAsync(order.OrderId, lab.VitaminD);
        vitD.SampleId.Should().Be(order.Labels[0].SampleId);
        vitD.Status.Should().Be(LabOrderItemStatus.Received, "its tube is in the laboratory");
        vitD.DueAt.Should().NotBeNull();

        var row = (await WorklistAsync(provider)).Should().ContainSingle().Subject;
        row.ItemId.Should().Be(vitD.Id);
        row.Stage.Should().Be(SubcontractStage.ToSend);

        (await DispatchAsync(provider, null, vitD.Id)).IsSuccess.Should().BeTrue();

        await EnterAsync(provider, order.OrderId, vitD.Id, "25");
        (await WorklistAsync(provider)).Should().BeEmpty();
        (await ItemAsync(order.OrderId, lab.VitaminD)).Status.Should().Be(LabOrderItemStatus.ResultEntered);
    }

    [SqlServerFact]
    public async Task G_AfterACancelledSendOut_TheReportNamesThePartnerOfTheActReported()
    {
        var lab = await LabAsync();
        var renderer = new CapturingRenderer();
        using var provider = Services(OperatorOf(lab), renderer);

        var order = await RegisterAsync(provider, lab.Patient, lab.VitaminD, lab.Glucose);
        await CollectAndReceiveAsync(provider, order);
        var first = await ItemAsync(order.OrderId, lab.VitaminD);
        var glucose = await ItemAsync(order.OrderId, lab.Glucose);

        // Sent to the other partner the day the usual one could not take it; the tube was lost.
        (await DispatchAsync(provider, lab.OtherPartner.Id, first.Id)).IsSuccess.Should().BeTrue();
        (await On<OrderAmendmentService, Result>(provider,
                s => s.CancelItemAsync(order.OrderId, first.Id, "Tube perdu au transport")))
            .IsSuccess.Should().BeTrue();

        // Ordered again, sent to the usual partner, answered by it.
        (await On<OrderAmendmentService, Result<OrderAmendment>>(provider,
                s => s.AddAnalysesAsync(order.OrderId, [lab.VitaminD.Id], [], "Nouvel envoi")))
            .IsSuccess.Should().BeTrue();
        var second = await ItemAsync(order.OrderId, lab.VitaminD);
        second.Id.Should().NotBe(first.Id);

        (await DispatchAsync(provider, null, second.Id)).Value.Single().PartnerSiteId.Should().Be(lab.Partner.Id);

        await EnterAsync(provider, order.OrderId, second.Id, "31");
        await EnterAsync(provider, order.OrderId, glucose.Id, "0.90");
        await ValidateAllAsync(provider, order.OrderId);

        (await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId)))
            .IsSuccess.Should().BeTrue();

        renderer.Models.Single().Sections.SelectMany(s => s.Analyses).Single(a => a.AnalysisCode == lab.VitaminD.Code)
            .SubcontractorName.Should().Be("Laboratoire partenaire A");
    }
}

/// <summary>
/// A migrated, seeded database of its own for the demonstration seeder, dropped at the end.
/// </summary>
public sealed class DemoSubcontractingScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_DemoSoustraitance_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; } = "";

    private readonly bool _created;

    public DemoSubcontractingScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;
        _created = true;
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
/// The demonstration seeder's partner laboratory and subcontracted analysis, and the demonstration
/// file that carries it to the send-out list — created once, however many times the seeder runs.
/// </summary>
public sealed class DemoSubcontractingSeedDatabaseTests(DemoSubcontractingScratchDatabase database)
    : IClassFixture<DemoSubcontractingScratchDatabase>
{
    private sealed class NoReport : IReportRenderer
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

    [SqlServerFact]
    public async Task TheDemonstration_HasAPartner_AnAnalysisSentToIt_AndAFileWaitingToLeave_Once()
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
        services.AddSingleton<IReportRenderer, NoReport>();
        services.AddSingleton<IFontAvailability, AllFonts>();
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IValidationQueries, ValidationQueries>();
        services.AddIsolatedQueries<IReportQueries, ReportQueries>();
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<MarocLIS.Application.Catalog.ICatalogMaintenanceQueries, CatalogMaintenanceQueries>();
        services.AddIsolatedQueries<ISampleQueries, SampleQueries>();
        services.AddIsolatedQueries<ISubcontractQueries, SubcontractQueries>();
        services.AddIsolatedQueries<IRecollectionQueries, RecollectionQueries>();
        services.AddIsolatedQueries<IBillingQueries, BillingQueries>();
        services.AddIsolatedQueries<MarocLIS.Application.Administration.IAdministrationQueries, AdministrationQueries>();
        services.AddIsolatedQueries<MarocLIS.Application.Administration.IMasterDataQueries, MasterDataQueries>();
        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync())
                .IsSuccess.Should().BeTrue();

        for (var run = 0; run < 2; run++)
        {
            using var scope = provider.CreateScope();
            var problems = await scope.ServiceProvider.GetRequiredService<MarocLIS.Infrastructure.Persistence.Demo.DemoDataSeeder>()
                .SeedAsync();

            problems.Should().NotContain(p => p.StartsWith("Sous-traitance") || p.StartsWith("Dossier DEMO-000013"),
                                         string.Join(" | ", problems));
        }

        await using var context = database.Context();

        var partner = await context.Sites.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.Code == "DEMO-PART");
        partner.Kind.Should().Be(LabSiteKind.ReferralPartner);
        partner.Name.Fr.Should().Be("Laboratoire partenaire (démo)");

        var analysis = await context.AnalysisTests.IgnoreQueryFilters().AsNoTracking()
            .Include(a => a.Parameters).ThenInclude(p => p.ReferenceRanges)
            .Include(a => a.Tariffs)
            .SingleAsync(a => a.Code == "DEMO-VITD");
        analysis.IsSubcontracted.Should().BeTrue();
        analysis.SubcontractorSiteId.Should().Be(partner.Id);
        analysis.Parameters.Should().ContainSingle().Which.ReferenceRanges.Should().ContainSingle();
        analysis.Tariffs.Should().ContainSingle();

        // Only the demonstration's analysis is sent out: the starter catalogue is left alone.
        (await context.AnalysisTests.IgnoreQueryFilters().CountAsync(a => a.IsSubcontracted)).Should().Be(1);

        var item = await context.LabOrderItems.AsNoTracking()
            .SingleAsync(i => i.AnalysisTestId == analysis.Id);
        item.IsSubcontracted.Should().BeTrue();
        item.Status.Should().Be(LabOrderItemStatus.Received);

        var siteId = await context.LabOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == item.LabOrderId).Select(o => o.SiteId).SingleAsync();

        var worklist = await new SubcontractQueries(context).GetWorklistAsync(siteId);
        worklist.Should().ContainSingle().Which.Stage.Should().Be(SubcontractStage.ToSend);
    }
}
