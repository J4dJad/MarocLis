using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the cycle 5 quality and stock scenarios.</summary>
public sealed class Cycle5bDatabase : IDisposable
{
    private readonly bool _created;

    public Cycle5bDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c5b_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = new LisDbContext(new DbContextOptionsBuilder<LisDbContext>()
                                                 .UseSqlServer(ConnectionString).Options);
        context.Database.EnsureCreated();
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
/// Cycle 5, lot b: the workflows of the quality and stock screens no earlier cycle replayed on SQL
/// Server, through the services the screens call, each step read back in a fresh context.
/// </summary>
public sealed class Cycle5bQualityStockDatabaseTests(Cycle5bDatabase database) : IClassFixture<Cycle5bDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 17);
    }

    private static readonly Guid Site = Guid.NewGuid();
    private static readonly FixedClock Clock = new();

    private static ICurrentUser User(Guid userId) => Stub<ICurrentUser>.Create(new()
    {
        ["get_IsAuthenticated"] = _ => true,
        ["get_UserId"] = _ => userId,
        ["get_UserName"] = _ => "c5b",
        ["get_FullName"] = _ => "Cycle 5b",
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

    private static InstrumentContractService Contracts(LisDbContext c, ICurrentUser user) =>
        new(new Repository<InstrumentContract>(c, Clock), new InstrumentRepository(c, Clock),
            new Repository<PurchasingDocument>(c, Clock), new InstrumentContractQueries(c),
            new UnitOfWork(c), Allowed, user, Clock);

    private async Task<T> InFreshContextAsync<T>(ICurrentUser user, Func<LisDbContext, Task<T>> step)
    {
        await using var context = Context(user);
        return await step(context);
    }

    private static string Keys(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [SqlServerFact]
    public async Task APlacementContract_CountsItsApprovedReagentOrders_FlagsALateTechnician_AndLeavesTheListWhenTerminated()
    {
        var manager = User(Guid.NewGuid());

        var supplier = new Supplier(Unique("F"), "Diagnostica Maroc") { IsApproved = true };
        var other = new Supplier(Unique("F"), "Autre fournisseur") { IsApproved = true };
        var analyser = new Instrument(Unique("AUT"), MultilingualText.FromFrench("Automate d'immunologie"),
                                      EquipmentKind.Analyser, Site);
        var reagent = new StockItem(Unique("REA"), MultilingualText.FromFrench("Réactif TSH"), StockItemKind.Reagent,
                                    "coffret", Site);

        // One order approved at 1 000 MAD less 10 % on ten boxes, one left as a draft: only the first commits.
        var approved = new PurchaseOrder(Unique("BC/"), supplier.Id, Site, Clock.Today.AddDays(-20), Guid.NewGuid());
        approved.AddLine(new PurchaseOrderLine(reagent.Id, 10m, 1_000m) { DiscountRate = 0.10m });
        approved.Approve(Clock.Now.AddDays(-20), Guid.NewGuid()).IsSuccess.Should().BeTrue();
        var draft = new PurchaseOrder(Unique("BC/"), supplier.Id, Site, Clock.Today.AddDays(-5), Guid.NewGuid());
        draft.AddLine(new PurchaseOrderLine(reagent.Id, 50m, 1_000m));

        await using (var seed = Context(manager))
        {
            seed.Suppliers.AddRange(supplier, other);
            seed.Instruments.Add(analyser);
            seed.StockItems.Add(reagent);
            seed.PurchaseOrders.AddRange(approved, draft);
            await seed.SaveChangesAsync();
        }

        SaveInstrumentContractRequest Request(Guid supplierId, string reference) => new()
        {
            Reference = reference,
            SupplierId = supplierId,
            Kind = InstrumentContractKind.Placement,
            StartDate = Clock.Today.AddMonths(-6),
            DurationMonths = 60,
            CommitmentBasis = CommitmentBasis.PurchaseAmountPerYear,
            CommittedVolume = 120_000m,
            MaxInterventionDelayHours = 24,
            PreventiveVisitsPerYear = 2,
            InstrumentIds = [analyser.Id],
            Reagents = [new InstrumentContractReagentInput(reagent.Id, 1_000m, 12.5m)]
        };

        var reference = Unique("MAD-");
        var created = await InFreshContextAsync(manager, c => Contracts(c, manager).SaveAsync(Request(supplier.Id, reference)));
        created.IsSuccess.Should().BeTrue(Keys(created));

        // The same reference is refused for the same supplier, but another supplier may use it.
        (await InFreshContextAsync(manager, c => Contracts(c, manager).SaveAsync(Request(supplier.Id, " " + reference + " "))))
            .FirstError.ResourceKey.Should().Be("Contract_ReferenceExists");
        var elsewhere = await InFreshContextAsync(manager, c => Contracts(c, manager).SaveAsync(Request(other.Id, reference)));
        elsewhere.IsSuccess.Should().BeTrue(Keys(elsewhere));

        // A breakdown: the supplier called at 08:00 two days ago, its technician there 30 h later.
        var record = new MaintenanceRecord(MaintenanceKind.Corrective, Clock.Today.AddDays(-1),
                                           MultilingualText.FromFrench("Remplacement de la lampe"))
        { DowntimeHours = 30m };
        await using (var c = Context(manager))
        {
            var tracked = await c.Instruments.SingleAsync(i => i.Id == analyser.Id);
            tracked.AddMaintenance(record);
            await c.SaveChangesAsync();
        }

        var called = Clock.Now.AddDays(-2);
        (await InFreshContextAsync(manager, c => Contracts(c, manager).RecordInterventionTimesAsync(
            analyser.Id, record.Id, called, called.AddHours(-1))))
            .FirstError.ResourceKey.Should().Be("Contract_ArrivalBeforeRequest");
        (await InFreshContextAsync(manager, c => Contracts(c, manager).RecordInterventionTimesAsync(
            analyser.Id, record.Id, called, Clock.Now.AddHours(1))))
            .FirstError.ResourceKey.Should().Be("Contract_InterventionTimeInFuture");
        var timed = await InFreshContextAsync(manager, c => Contracts(c, manager).RecordInterventionTimesAsync(
            analyser.Id, record.Id, called, called.AddHours(30)));
        timed.IsSuccess.Should().BeTrue(Keys(timed));

        await using (var c = Context(manager))
        {
            var row = (await Contracts(c, manager).GetContractsAsync()).Value.Single(r => r.Id == created.Value);
            row.Commitment.Consumed.Should().Be(9_000m);
            row.Commitment.AtRisk.Should().BeTrue("9 000 MAD over half a year projects far below 120 000");
            row.Interventions.Should().Be(1);
            row.DelaysExceeded.Should().Be(1);
            row.DowntimeHours.Should().Be(30m);

            var intervention = (await Contracts(c, manager).GetInterventionsAsync(created.Value)).Value.Single();
            intervention.DelayHours.Should().Be(30m);
            intervention.DelayExceeded.Should().BeTrue();
        }

        // Terminated early: stamped on the day, gone from the active list, still there with the inactive.
        var detail = (await InFreshContextAsync(manager, c => Contracts(c, manager).GetContractAsync(created.Value))).Value;
        var terminated = await InFreshContextAsync(manager, c => Contracts(c, manager).SaveAsync(Request(supplier.Id, reference) with
        {
            Id = detail.Id,
            IsActive = false
        }));
        terminated.IsSuccess.Should().BeTrue(Keys(terminated));

        await using (var c = Context(manager))
        {
            (await Contracts(c, manager).GetContractsAsync()).Value.Should().NotContain(r => r.Id == created.Value);
            var inactive = (await Contracts(c, manager).GetContractsAsync(includeInactive: true)).Value.Single(r => r.Id == created.Value);
            inactive.IsActive.Should().BeFalse();
            inactive.Commitment.AtRisk.Should().BeFalse("a terminated contract raises no more alerts");

            var saved = await c.InstrumentContracts.AsNoTracking().SingleAsync(x => x.Id == created.Value);
            saved.TerminatedOn.Should().Be(Clock.Today);
            saved.Reagents.Single().CostPerTestMad.Should().Be(12.5m);

            var reasons = await c.AuditEntries.AsNoTracking().Select(a => a.Reason).ToListAsync();
            reasons.Should().Contain($"Création du contrat automate {reference}");
            reasons.Should().Contain($"Modification du contrat automate {reference}");
            reasons.Should().Contain(r => r != null && r.StartsWith($"Heures d'appel et d'arrivée du technicien sur {analyser.Code}"));
        }
    }
}
