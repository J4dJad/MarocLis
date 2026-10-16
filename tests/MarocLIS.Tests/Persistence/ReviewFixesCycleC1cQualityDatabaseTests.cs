using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Sequencing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the quality scenarios, created and dropped by the class.</summary>
public sealed class CycleC1cQualityDatabase : IDisposable
{
    private readonly bool _created;

    public CycleC1cQualityDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c1cqual_" + Guid.NewGuid().ToString("N")[..8]
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
/// The non-conformity and document workflows end to end on SQL Server, through the services the
/// screens call: a file worked to closure, reopened, and refused once closed; the analysis read
/// back for the dialog; a retired document whose approved version cannot be put in force. Each
/// step is checked in a fresh context, as the next screen would see it, with its audit entry.
/// </summary>
public sealed class ReviewFixesCycleC1cQualityDatabaseTests(CycleC1cQualityDatabase database)
    : IClassFixture<CycleC1cQualityDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 16);
    }

    private static readonly Guid Site = Guid.NewGuid();
    private static readonly FixedClock Clock = new();

    private static ICurrentUser User(Guid userId) => Stub<ICurrentUser>.Create(new()
    {
        ["get_IsAuthenticated"] = _ => true,
        ["get_UserId"] = _ => userId,
        ["get_UserName"] = _ => "qualite",
        ["get_FullName"] = _ => "Responsable qualité",
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

    private static NonConformityService NonConformities(LisDbContext context, ICurrentUser user) =>
        new(new NonConformityRepository(context, Clock), new QualityQueries(context),
            new SequenceAllocator(context, Clock, user), new UnitOfWork(context), Allowed, user, Clock);

    private static DocumentControlService Documents(LisDbContext context, ICurrentUser user) =>
        new(new ControlledDocumentRepository(context, Clock), new DocumentControlQueries(context),
            new UnitOfWork(context), Allowed, user, Clock);

    private async Task<T> InFreshContextAsync<T>(ICurrentUser user, Func<LisDbContext, Task<T>> step)
    {
        await using var context = Context(user);
        return await step(context);
    }

    [SqlServerFact]
    public async Task ANonConformity_IsWorkedToClosure_ThenReadOnlyUntilReopened()
    {
        var manager = User(Guid.NewGuid());

        // Opened, a major item: cause, action and effectiveness check are all required to close.
        var reference = await InFreshContextAsync(manager, c => NonConformities(c, manager).OpenAsync(
            new CreateNonConformityRequest
            {
                Category = NonConformityCategory.Equipment,
                Severity = NonConformitySeverity.Major,
                Description = MultilingualText.FromFrench("Lampe de l'automate d'hémostase hors service")
            }));
        reference.IsSuccess.Should().BeTrue(string.Join(", ", reference.Errors.Select(e => e.ResourceKey)));

        Guid fileId;
        await using (var context = Context(manager))
            fileId = (await context.NonConformities.SingleAsync(n => n.Reference == reference.Value)).Id;

        async Task<NonConformity> Reload()
        {
            await using var context = Context(manager);
            return await context.NonConformities.Include(n => n.Actions).AsNoTracking().SingleAsync(n => n.Id == fileId);
        }

        Task<Result> AddAction(string text) => InFreshContextAsync(manager, c => NonConformities(c, manager).AddActionAsync(
            new AddActionRequest(fileId, ActionKind.CorrectiveAction, MultilingualText.FromFrench(text),
                                 Guid.Empty, Clock.Today.AddDays(5))));

        Task<Result> Complete(Guid actionId) => InFreshContextAsync(manager, c => NonConformities(c, manager)
            .CompleteActionAsync(fileId, actionId, MultilingualText.FromFrench("Bon d'intervention")));

        (await AddAction("Remplacer la lampe")).IsSuccess.Should().BeTrue();
        var first = (await Reload()).Actions.Single();
        var done = await Complete(first.Id);
        done.IsSuccess.Should().BeTrue(string.Join(", ", done.Errors.Select(e => e.ResourceKey)));
        (await Reload()).Status.Should().Be(NonConformityStatus.AwaitingVerification);

        // A second action while awaiting verification: the file is in progress again.
        (await AddAction("Contrôler les autres lampes")).IsSuccess.Should().BeTrue();
        var file = await Reload();
        file.Status.Should().Be(NonConformityStatus.InProgress);

        (await InFreshContextAsync(manager, c => NonConformities(c, manager).CloseAsync(fileId)))
            .Errors.Select(e => e.ResourceKey).Should().Contain("Nc_AllActionsMustBeFinished");

        (await Complete(file.Actions.Single(a => a.Id != first.Id).Id)).IsSuccess.Should().BeTrue();
        (await InFreshContextAsync(manager, c => NonConformities(c, manager).RecordAnalysisAsync(
            fileId, RootCauseCategory.EquipmentFailure, MultilingualText.FromFrench("Lampe en fin de vie"),
            effectivenessVerification: MultilingualText.FromFrench("TP/INR conformes 5 jours")))).IsSuccess.Should().BeTrue();

        // The analysis dialog opens on what was recorded.
        var analysis = await InFreshContextAsync(manager, c => NonConformities(c, manager).GetAnalysisAsync(fileId));
        analysis.Value.RootCause.Should().Be(RootCauseCategory.EquipmentFailure);
        analysis.Value.RootCauseAnalysis!.Fr.Should().Be("Lampe en fin de vie");
        analysis.Value.EffectivenessVerifiedOn.Should().Be(Clock.Today);

        (await InFreshContextAsync(manager, c => NonConformities(c, manager).CloseAsync(fileId))).IsSuccess.Should().BeTrue();
        (await Reload()).Status.Should().Be(NonConformityStatus.Closed);

        // Closed: no new action, no new analysis, no second closure — until reopened.
        (await AddAction("Ajout tardif")).FirstError.ResourceKey.Should().Be("Nc_AlreadyClosed");
        (await InFreshContextAsync(manager, c => NonConformities(c, manager).RecordAnalysisAsync(
            fileId, RootCauseCategory.HumanError))).FirstError.ResourceKey.Should().Be("Nc_AlreadyClosed");
        (await InFreshContextAsync(manager, c => NonConformities(c, manager).CloseAsync(fileId)))
            .FirstError.ResourceKey.Should().Be("Nc_AlreadyClosed");

        var closed = await Reload();
        closed.Actions.Should().HaveCount(2);
        closed.RootCause.Should().Be(RootCauseCategory.EquipmentFailure);

        (await InFreshContextAsync(manager, c => NonConformities(c, manager).ReopenAsync(fileId, "Récidive constatée")))
            .IsSuccess.Should().BeTrue();
        (await AddAction("Changer de fournisseur de lampes")).IsSuccess.Should().BeTrue();
        (await Reload()).Status.Should().Be(NonConformityStatus.InProgress);

        // Every step left its reason in the audit trail.
        await using (var context = Context(manager))
        {
            var reasons = await context.AuditEntries.AsNoTracking()
                                       .Where(a => a.EntityId == fileId && a.Reason != null)
                                       .Select(a => a.Reason!).ToListAsync();
            reasons.Should().Contain(r => r.StartsWith("Clôture de la non-conformité"));
            reasons.Should().Contain(r => r.StartsWith("Réouverture de la non-conformité"));
        }
    }

    [SqlServerFact]
    public async Task ARetiredDocument_KeepsItsApprovedVersionOutOfForce_UntilReactivated()
    {
        var author = User(Guid.NewGuid());
        var approver = User(Guid.NewGuid());
        var reference = "PR-C1C-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var created = await InFreshContextAsync(author, c => Documents(c, author).CreateAsync(
            new CreateControlledDocumentRequest
            {
                Reference = reference, Title = MultilingualText.FromFrench("Prélèvement veineux"),
                Kind = DocumentKind.Procedure
            }));
        created.IsSuccess.Should().BeTrue();
        var id = created.Value;

        (await InFreshContextAsync(author, c => Documents(c, author).AddDraftAsync(id, null, [37, 80, 68, 70], "pr.pdf")))
            .IsSuccess.Should().BeTrue();
        var submitted = await InFreshContextAsync(author, c => Documents(c, author).SubmitForReviewAsync(id, 1));
        submitted.IsSuccess.Should().BeTrue(string.Join(", ", submitted.Errors.Select(e => e.ResourceKey)));
        (await InFreshContextAsync(approver, c => Documents(c, approver).ApproveAsync(id, 1))).IsSuccess.Should().BeTrue();

        (await InFreshContextAsync(approver, c => Documents(c, approver).SetActiveAsync(id, false, "Procédure abandonnée")))
            .IsSuccess.Should().BeTrue();

        (await InFreshContextAsync(approver, c => Documents(c, approver).MakeEffectiveAsync(id, 1, Clock.Today)))
            .FirstError.ResourceKey.Should().Be("Document_Retired");
        (await InFreshContextAsync(author, c => Documents(c, author).AddDraftAsync(id, null, [1], "pr2.pdf")))
            .FirstError.ResourceKey.Should().Be("Document_Retired");

        // Reactivated, the approved version goes in force and can be acknowledged.
        (await InFreshContextAsync(approver, c => Documents(c, approver).SetActiveAsync(id, true, "Reprise de la procédure")))
            .IsSuccess.Should().BeTrue();
        (await InFreshContextAsync(approver, c => Documents(c, approver).MakeEffectiveAsync(id, 1, Clock.Today)))
            .IsSuccess.Should().BeTrue();
        (await InFreshContextAsync(author, c => Documents(c, author).AcknowledgeAsync(id))).IsSuccess.Should().BeTrue();

        var register = await InFreshContextAsync(author, c => Documents(c, author).GetRegisterAsync());
        register.Value.Should().ContainSingle(d => d.Reference == reference)
                .Which.Should().Match<ControlledDocumentRow>(d => d.EffectiveVersion == 1 && d.AcknowledgedByCurrentUser);
    }
}
