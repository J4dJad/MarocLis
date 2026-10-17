using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Quality;
using MarocLIS.Application.Results;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Simulator;
using MarocLIS.Instruments.Transport;
using MarocLIS.Localization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the COBAS INTEGRA tests, dropped at the end.</summary>
public sealed class IntegraScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Integra_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public IntegraScratchDatabase()
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
/// A COBAS INTEGRA 400 plus result, end to end against SQL Server: the simulated analyser lists a
/// tube without an order, the host downloads the tube's glucose under the mapped test number, the
/// analyser measures it, and the next round's result block lands on that tube's glucose — through
/// the same sink, scopes and service account the instrument host uses.
/// </summary>
public sealed class RocheIntegraDatabaseTests(IntegraScratchDatabase database)
    : IClassFixture<IntegraScratchDatabase>
{
    private static readonly Guid Site = Guid.NewGuid();

    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "technicien";
        public string FullName => "Technicien de paillasse";
        public Guid SiteId => Site;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [Site];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "PAILLASSE-1";
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [SqlServerFact]
    public async Task AnIntegraResult_IsReceivedMappedAndStoredOnItsTube()
    {
        // ---- The laboratory: a glucose ordered on one tube, an INTEGRA link mapping 031 to it.
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var analysis = new AnalysisTest(Unique("GLY"), new MultilingualText("Glycémie"), department.Id, specimen.Id);
        var glucose = new TestParameter(Unique("GLY"), new MultilingualText("Glycémie"), ResultValueType.Numeric);
        glucose.AddReferenceRange(ReferenceRange.Numeric(0.70m, 1.10m));
        analysis.AddParameter(glucose);

        // A second tube of the same order, without the glucose: its value must not land there.
        var patient = new Patient(Unique("P"), "BENNANI", "Karim", new DateOnly(1975, 4, 2), Gender.Male, Site);
        var order = new LabOrder(Unique("A"), patient.Id, Site, DateTimeOffset.Now.AddHours(-1), Guid.NewGuid());
        var item = new LabOrderItem(analysis.Id, OrderPriority.Routine);
        order.AddItem(item);

        var barcode = Unique("T");
        var tube = new Sample(barcode, order.Id, specimen.Id, Site);
        item.SampleId = tube.Id;
        var other = new Sample(Unique("T"), order.Id, specimen.Id, Site);

        // Drawn and received at the bench: the analyser only takes results for a tube in the laboratory.
        foreach (var sample in new[] { tube, other })
        {
            sample.Collect(DateTimeOffset.Now.AddMinutes(-50), Guid.NewGuid(), null, null).IsSuccess.Should().BeTrue();
            sample.Receive(DateTimeOffset.Now.AddMinutes(-40), Guid.NewGuid(), SampleDefect.None, false, null, null).IsSuccess.Should().BeTrue();
        }

        var result = new TestResult(order.Id, item.Id, glucose.Id, Site);

        var instrument = new Instrument(Unique("I400"), new MultilingualText("cobas INTEGRA 400 plus"),
                                        EquipmentKind.Analyser, Site);

        var link = new InstrumentInterface(instrument.Id, Site, InstrumentProtocol.RocheIntegra,
                                           InstrumentTransportKind.Tcp)
        {
            Direction = InstrumentDirection.HostQueryDownload,
            AutoAcceptResults = true,
            Host = "127.0.0.1",
            AcknowledgementTimeoutSeconds = 5,
            MaxRetries = 2
        };
        link.AddMapping(new InstrumentTestMapping("031", glucose.Id));

        // The account the instrument host writes under.
        var account = new AppUser("interface", "Interface automates", "not-a-password-hash");

        await using (var context = database.Context())
        {
            context.Departments.Add(department);
            context.SpecimenTypes.Add(specimen);
            context.AnalysisTests.Add(analysis);
            context.Patients.Add(patient);
            context.LabOrders.Add(order);
            context.Samples.AddRange(tube, other);
            context.TestResults.Add(result);
            context.Instruments.Add(instrument);
            context.InstrumentInterfaces.Add(link);
            context.Users.Add(account);
            await context.SaveChangesAsync();
        }

        // ---- The host's composition, as the desktop builds it.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddInstrumentInterfacing(configuration);
        services.AddMarocLisLocalization(AppLanguage.French);
        // The queries the desktop registers behind its isolating proxy, as the host reads them.
        services.AddIsolatedQueries<IQualityQueries, QualityQueries>();
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddSingleton<ICurrentUser, Operator>();
        await using var provider = services.BuildServiceProvider();

        var serviceAccount = provider.GetRequiredService<InstrumentServiceAccount>();
        (await serviceAccount.EnsureResolvedAsync()).Should().BeTrue();

        var sink = new ScopedInstrumentMessageSink(provider.GetRequiredService<IServiceScopeFactory>(),
                                                   identity: serviceAccount.AsCurrentUser());

        // ---- The analyser: the tube is on board, barcoded, with no order yet.
        var emulator = new IntegraInstrumentEmulator(seed: 11);
        emulator.TubesWithoutOrder.Add((barcode, "SER"));

        await using var simulator = new IntegraAnalyserSimulator(emulator);
        simulator.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serving = simulator.RunAsync(stop.Token);

        link.TcpPort = simulator.Port;

        await using (var transport = new TcpInstrumentTransport(link))
        await using (var driver = new RocheIntegraInstrumentDriver(link, transport, sink))
        {
            (await transport.ConnectAsync(stop.Token)).Should().BeTrue();

            await driver.RoundAsync(stop.Token);   // host query: the glucose goes down as test 031
            await driver.RoundAsync(stop.Token);   // the result comes up
        }

        stop.Cancel();
        await serving;

        emulator.ReceivedOrders.Should().ContainSingle()
            .Which.Should().Contain("55  31").And.Contain(l => l.StartsWith($"53 {barcode}", StringComparison.Ordinal));

        // ---- Stored on the right tube's glucose, under the service account, from the instrument.
        await using var read = database.Context();

        var stored = await read.TestResults.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == result.Id);
        stored.NumericValue.Should().NotBeNull().And.BeInRange(0.70m, 1.10m);
        stored.IsAutoTransmitted.Should().BeTrue();
        stored.EnteredByUserId.Should().Be(account.Id);
        stored.InstrumentId.Should().Be(instrument.Id);

        read.TestResults.IgnoreQueryFilters().Count(r => r.LabOrderId == order.Id).Should().Be(1,
            "nothing was created for the other tube");

        var log = await read.InstrumentMessageLog.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.InstrumentInterfaceId == link.Id && m.Direction == MessageDirection.Inbound
                        && m.Status == InstrumentMessageProcessingStatus.Imported)
            .ToListAsync();

        log.Should().ContainSingle();
        log[0].SpecimenIds.Should().Be(barcode);
        log[0].RawContent.Should().StartWith("14 COBAS INTEGRA400 04\r53 " + barcode);

        // The stored frame replays to the same value: what an operator's replay would import.
        var replayed = IntegraMessageParser.Parse(log[0].RawContent);
        replayed.Specimens.Single().Results.Single().InstrumentTestCode.Should().Be("031");
    }
}
