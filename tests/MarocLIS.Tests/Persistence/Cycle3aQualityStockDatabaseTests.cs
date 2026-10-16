using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Infrastructure.Sequencing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the cycle 3 quality and stock scenarios (foreign keys off).</summary>
public sealed class Cycle3aDatabase : IDisposable
{
    private readonly bool _created;

    public Cycle3aDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c3a_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = new LisDbContext(new DbContextOptionsBuilder<LisDbContext>()
                                                 .UseSqlServer(ConnectionString).Options);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
        _created = true;
    }

    public string ConnectionString { get; } = string.Empty;

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = new LisDbContext(new DbContextOptionsBuilder<LisDbContext>()
                                                 .UseSqlServer(ConnectionString).Options);
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// Cycle 3, lot a: inputs the quality and stock screens accepted and the database then refused
/// with « Erreur inattendue », or that left a record in a state the catalogue forbids — each
/// replayed through the service on SQL Server and read back in a fresh context.
/// </summary>
public sealed class Cycle3aQualityStockDatabaseTests(Cycle3aDatabase database) : IClassFixture<Cycle3aDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 16);
    }

    private static readonly Guid Site = Guid.NewGuid();
    private static readonly FixedClock Clock = new();

    private static ICurrentUser User(Guid userId) =>
        Stub<ICurrentUser>.Create(new()
        {
            ["get_IsAuthenticated"] = _ => true,
            ["get_UserId"] = _ => userId,
            ["get_UserName"] = _ => "c3a",
            ["get_FullName"] = _ => "Cycle 3a",
            ["get_SiteId"] = _ => Site,
            ["get_SiteCode"] = _ => "PRINCIPAL",
            ["get_AccessibleSiteIds"] = _ => (IReadOnlyList<Guid>)[Site],
            ["Has"] = _ => true
        });

    private static IPermissionGuard Allowed => Stub<IPermissionGuard>.Create(new()
    {
        [nameof(IPermissionGuard.Require)] = _ => Result.Success(),
        [nameof(IPermissionGuard.RequireAny)] = _ => Result.Success(),
        [nameof(IPermissionGuard.RequireAll)] = _ => Result.Success()
    });

    private LisDbContext Context(ICurrentUser user) =>
        new(new DbContextOptionsBuilder<LisDbContext>()
                .UseSqlServer(database.ConnectionString)
                .AddInterceptors(new AuditSaveChangesInterceptor(user, Clock))
                .Options, user);

    private static QualityControlService Qc(LisDbContext c, ICurrentUser user) =>
        new(new Repository<QcTarget>(c, Clock), new QcResultRepository(c, Clock), new Repository<QcMaterial>(c, Clock),
            new NonConformityRepository(c, Clock), new QualityQueries(c), new SequenceAllocator(c, Clock, user),
            new UnitOfWork(c), Allowed, user, Clock);

    private static EqaService Eqa(LisDbContext c, ICurrentUser user) =>
        new(new Repository<EqaParticipation>(c, Clock), new NonConformityRepository(c, Clock), new EqaQueries(c),
            new SequenceAllocator(c, Clock, user), new UnitOfWork(c), Allowed, user, Clock);

    private static SupplierInvoiceService Invoices(LisDbContext c, ICurrentUser user) =>
        new(new SupplierInvoiceRepository(c, Clock), new Repository<SupplierInvoiceFile>(c, Clock),
            new Repository<Supplier>(c, Clock), new Repository<LatePaymentDeclaration>(c, Clock),
            new SupplierInvoiceQueries(c), new ConfigurationWriter(c), new UnitOfWork(c), Allowed, user, Clock);

    private static string Keys(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    [SqlServerFact]
    public async Task AControlLeftWithoutAnyWestgardRule_IsRefused_AndKeepsTheRulesItHad()
    {
        var manager = User(Guid.NewGuid());
        var target = new QcTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            AssignedMean = 5m, AssignedSd = 0.2m, ActiveRules = WestgardRule.OneThreeS | WestgardRule.TwoTwoS
        };
        await using (var seed = Context(manager))
        {
            seed.QcTargets.Add(target);
            await seed.SaveChangesAsync();
        }

        await using (var c = Context(manager))
        {
            (await Qc(c, manager).SetActiveRulesAsync(target.Id, WestgardRule.None)).FirstError.ResourceKey
                .Should().Be("QcTarget_AtLeastOneRuleRequired");
            var changed = await Qc(c, manager).SetActiveRulesAsync(target.Id, WestgardRule.OneThreeS);
            changed.IsSuccess.Should().BeTrue(Keys(changed));
        }

        await using (var c = Context(manager))
            (await c.QcTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).ActiveRules
                .Should().Be(WestgardRule.OneThreeS);
    }

    [SqlServerFact]
    public async Task AnEqaValueKeyedInTheWrongUnit_IsRefusedCleanly_AndTheCorrectedOneOpensItsNonConformity()
    {
        var manager = User(Guid.NewGuid());
        var participation = new EqaParticipation("ProBioQual", "2026-C3A", Guid.NewGuid(), Site, Clock.Today.AddDays(-20))
        {
            ReportedValue = 1000m, SubmittedOn = Clock.Today.AddDays(-10)
        };
        await using (var seed = Context(manager))
        {
            seed.EqaParticipations.Add(participation);
            await seed.SaveChangesAsync();
        }

        // 0.0001 keyed for 1000: a score and a bias in the hundreds of millions, beyond the columns.
        await using (var c = Context(manager))
        {
            var eqa = Eqa(c, manager);
            (await eqa.RecordEvaluationAsync(participation.Id, new EqaEvaluation
            {
                AssignedValue = 0.0001m, PeerGroupMean = 0.0001m, PeerGroupSd = 0.00001m
            })).FirstError.ResourceKey.Should().Be("Eqa_ScoreOutOfRange");

            // Same screen, same context: the corrected entry saves, unsatisfactory, with its NC.
            var corrected = await eqa.RecordEvaluationAsync(participation.Id, new EqaEvaluation
            {
                AssignedValue = 900m, PeerGroupMean = 900m, PeerGroupSd = 25m
            });
            corrected.IsSuccess.Should().BeTrue(Keys(corrected));
            corrected.Value.Should().NotBeNull();
        }

        await using (var c = Context(manager))
        {
            var saved = await c.EqaParticipations.AsNoTracking().SingleAsync(p => p.Id == participation.Id);
            saved.ZScore.Should().Be(4m);
            saved.BiasPercent.Should().Be(11.11m);
            saved.Outcome.Should().Be(EqaOutcome.Unsatisfactory);
            saved.NonConformityId.Should().NotBeNull();
        }
    }

    [SqlServerFact]
    public async Task AScanWithAWindowsLengthName_IsAttached_UnderAShortenedNameKeepingItsExtension()
    {
        var clerk = User(Guid.NewGuid());
        var invoice = new SupplierInvoice(Guid.NewGuid(), Site, "FA-C3A-1", Clock.Today, Clock.Today, clerk.UserId);
        await using (var seed = Context(clerk))
        {
            seed.SupplierInvoices.Add(invoice);
            await seed.SaveChangesAsync();
        }

        var name = new string('f', 240) + ".pdf";
        await using (var c = Context(clerk))
        {
            var attached = await Invoices(c, clerk).AttachScanAsync(invoice.Id, name, [0x25, 0x50, 0x44, 0x46]);
            attached.IsSuccess.Should().BeTrue(Keys(attached));
        }

        await using (var c = Context(clerk))
        {
            var scan = (await Invoices(c, clerk).GetScanAsync(invoice.Id)).Value;
            scan.FileName.Should().HaveLength(200).And.EndWith(".pdf");
            (await c.SupplierInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id)).ScanFileName
                .Should().Be(scan.FileName);
        }
    }
}
