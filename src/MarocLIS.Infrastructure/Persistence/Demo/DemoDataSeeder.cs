using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Infrastructure.Persistence.Demo;

/// <summary>
/// Fills every screen of the application with a coherent, invented laboratory day, so that a
/// demonstration or a training installation can be explored button by button instead of opening
/// onto empty grids.
/// <para>
/// <b>Never on a production database.</b> Run only when <c>Application:SeedDemoDataOnFirstRun</c>
/// is <c>true</c>, and that flag must be <c>false</c> on any installation that holds real patients.
/// The <c>appsettings.json</c> of the demonstration build ships it switched on; a production
/// deployment sets it to <c>false</c> before the first start.
/// These are invented people: a laboratory that ran this against its production database would have
/// fictitious patients in a medical record it is legally obliged to keep for ten years, fictitious
/// invoices in a series the DGI requires unbroken, and no way to remove either afterwards. So the
/// caller has to ask for it explicitly, and everything it creates is marked so nobody has to query
/// the database to tell a demonstration record from a real one:
/// </para>
/// <list type="bullet">
/// <item>every patient's record number carries the <c>DEMO-</c> prefix;</item>
/// <item>every order carries <see cref="DemoMarker"/> in its internal note — a note is shown on the
/// worklists and never printed, whereas a marker written into a clinical field would appear on a
/// report as if it were medical information;</item>
/// <item>every code-bearing reference record (articles, suppliers, instruments, control lots,
/// documents, the mutual) has a <c>DEMO-</c> code, and named records say "(démo)";</item>
/// <item>the demonstration users are called <c>demo.*</c>.</item>
/// </list>
/// <para>
/// <b>Through the application services, not around them.</b> The orders are registered, collected,
/// received, entered, validated, released and invoiced by the same use cases a screen calls, under
/// the identity of a demonstration user holding the right role. Writing the rows directly would be
/// shorter and would produce data the application itself could never have produced — an invoice
/// without its gapless number, a report without its stored PDF, a biological validation by someone
/// who is not a biologist — and a demonstration built on such rows shows behaviour the product does
/// not have. Only what no service creates, or what has to lie in the past, is written directly: the
/// users, the patients and the history of control points behind the Levey-Jennings chart.
/// </para>
/// <para>
/// <b>Refuses a database holding real data.</b> The flag is a configuration value and can be left on
/// by mistake, so before anything else the seeder looks for a patient without the <c>DEMO-</c>
/// prefix or an issued invoice that is not a demonstration patient's; if it finds one it logs a
/// warning and creates nothing — no demonstration users, no invoice numbers taken from the series.
/// Demonstration users created earlier, while the database was still empty, are deactivated by
/// <see cref="DemoAccountGuard"/> once real data appears, as long as they keep their published
/// passwords.
/// </para>
/// <para>
/// <b>Idempotent per section, not per database.</b> It runs at every start while the flag is on, and
/// it has to work on a database that already holds the laboratory's reference data and an earlier
/// version's demonstration data. A single "skip if any patient exists" guard would then skip
/// everything, forever — so each section looks for its own demonstration records and creates only
/// what is missing, and the order scenarios resume from whatever stage a previous, interrupted run
/// reached.
/// </para>
/// <para>
/// <b>Never fatal.</b> A section that fails is logged and the next one runs; the laboratory's
/// application must start whether or not its demonstration data could be completed.
/// </para>
/// </summary>
public sealed partial class DemoDataSeeder(
    IServiceScopeFactory scopes,
    CurrentUserAccessor currentUser,
    IPasswordHasher passwordHasher,
    IClock clock,
    ILogger<DemoDataSeeder>? logger = null)
{
    /// <summary>
    /// Written into the internal note of every demonstration order, and into the notes of the
    /// demonstration prescribers and purchase orders. Visible on the worklists and on the order
    /// file, and never printed on a report.
    /// </summary>
    public const string DemoMarker = "JEU DE DÉMONSTRATION — NE PAS UTILISER EN PRODUCTION";

    /// <summary>Prefix of every demonstration code and record number.</summary>
    public const string DemoPrefix = "DEMO-";

    private readonly List<string> _problems = [];
    private readonly Dictionary<DemoActor, AuthenticatedSession> _sessions = [];
    private readonly Dictionary<DemoActor, Guid> _actors = [];

    private Guid _siteId;
    private string _siteCode = string.Empty;

    /// <summary>
    /// Brings the demonstration data up to date and returns what could not be done, one line per
    /// problem. An empty list means every screen has its content.
    /// </summary>
    public async Task<IReadOnlyList<string>> SeedAsync(CancellationToken ct = default)
    {
        _problems.Clear();
        _sessions.Clear();
        _actors.Clear();

        // Whoever was signed in before — nobody, at start-up — is signed in again afterwards. The
        // sections switch between the demonstration users, and leaving the last of them in the
        // session would hand the login screen's caller somebody else's permissions.
        var previous = Snapshot();

        try
        {
            // The flag alone is not trusted: a production database whose appsettings.json still
            // has it on would otherwise get demonstration users with published passwords and
            // invented invoices in its legal series. A database holding demonstration data only
            // keeps being completed; one holding anything else is left alone.
            if (await FindRealDataAsync(ct) is { } reason)
            {
                logger?.LogWarning(
                    "Demonstration data NOT seeded: the database already holds real data ({Reason}). " +
                    "Set Application:SeedDemoDataOnFirstRun to false in appsettings.json.", reason);
                _problems.Add($"Données réelles présentes ({reason}) : données de démonstration non créées.");
                return [.. _problems];
            }

            if (!await ResolveSiteAndAdministratorAsync(ct)) return [.. _problems];

            await RunSectionAsync("Utilisateurs", SeedUsersAsync, ct);

            // Everything after this point is done by the demonstration users; without them there
            // is nobody entitled to do it.
            if (_actors.Count < 5) return [.. _problems];

            await RunSectionAsync("Identité du site", SeedSiteIdentityAsync, ct);
            await RunSectionAsync("Prescripteurs et organismes", SeedPrescribersAndPayersAsync, ct);
            await RunSectionAsync("Fournisseurs et stocks", SeedSuppliersAndStockAsync, ct);
            await RunSectionAsync("Équipements", SeedEquipmentAsync, ct);
            await RunSectionAsync("Interface automate", SeedInstrumentInterfaceAsync, ct);
            await RunSectionAsync("Contrôle interne", SeedQualityControlAsync, ct);
            // Before the orders, so a demonstration file can carry the analysis sent out.
            await RunSectionAsync("Sous-traitance", SeedSubcontractingAsync, ct);
            await RunSectionAsync("Lieux de stockage", SeedStorageLocationsAsync, ct);
            await RunSectionAsync("Patients", SeedPatientsAsync, ct);
            await RunSectionAsync("Dossiers", SeedOrdersAsync, ct);
            // After the orders: its received transmission is for one of their tubes.
            await RunSectionAsync("Interface COBAS INTEGRA", SeedIntegraInterfaceAsync, ct);

            // After the orders, so the rejected control flags the results it covers.
            await RunSectionAsync("Contrôle interne du jour", SeedTodaysQcRunsAsync, ct);
            await RunSectionAsync("Facturation", SeedBillingAsync, ct);
            await RunSectionAsync("Non-conformités", SeedNonConformitiesAsync, ct);
            await RunSectionAsync("Documents qualité", SeedDocumentsAsync, ct);
            await RunSectionAsync("Évaluation externe", SeedEqaAsync, ct);
            await RunSectionAsync("Bons de commande", SeedPurchaseOrdersAsync, ct);
            await RunSectionAsync("Ressources humaines", SeedHumanResourcesAsync, ct);
            await RunSectionAsync("Accidents du travail", SeedAccidentsAsync, ct);
        }
        finally
        {
            Restore(previous);
        }

        if (_problems.Count > 0)
        {
            logger?.LogWarning("Demonstration data incomplete ({Count} problem(s)): {Problems}",
                               _problems.Count, string.Join(" | ", _problems));
        }

        return [.. _problems];
    }

    private async Task RunSectionAsync(string name, Func<CancellationToken, Task> section,
                                       CancellationToken ct)
    {
        try
        {
            await section(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Demonstration section {Section} failed.", name);
            _problems.Add($"{name} : {ex.GetType().Name} — {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Says what, if anything, shows that the database holds the laboratory's own data; see
    /// <see cref="DemoAccountGuard.FindRealDataAsync"/>.
    /// </summary>
    private async Task<string?> FindRealDataAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await DemoAccountGuard.FindRealDataAsync(
            scope.ServiceProvider.GetRequiredService<LisDbContext>(), ct);
    }

    // ---- Who does what -------------------------------------------------------------------------

    /// <summary>
    /// The people of the demonstration laboratory. Each step is done by the one whose role does it
    /// in a real laboratory, so the audit trail, the "entered by" and "validated by" columns and the
    /// separation-of-duty rules all read the way they would on a working day.
    /// </summary>
    private enum DemoActor
    {
        Administrator,
        Biologist,
        Technician,
        Secretary,
        QualityManager
    }

    /// <summary>
    /// Finds the site and the administrator every later step needs. The site is the administrator's
    /// default one, so the demonstration lands where the administrator signs in.
    /// </summary>
    private async Task<bool> ResolveSiteAndAdministratorAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

        // The seeded "admin" first; failing that, any active administrator — the laboratory may
        // have renamed or retired the first account, and the demonstration must not need it.
        var administrators = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Include(u => u.SiteAccess)
            .Where(u => u.IsActive && u.Roles.Any(r => r.Role!.Code == SystemRoles.Administrator))
            .ToListAsync(ct);

        var admin = administrators.FirstOrDefault(u => u.UserName == "admin")
                    ?? administrators.OrderBy(u => u.UserName).FirstOrDefault();

        if (admin is null)
        {
            _problems.Add("Aucun administrateur actif : données de démonstration non créées.");
            return false;
        }

        var siteId = admin.SiteAccess.FirstOrDefault(a => a.IsDefaultSite)?.SiteId
                     ?? admin.SiteAccess.FirstOrDefault()?.SiteId;

        var site = siteId is { } id
            ? await db.Sites.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted && s.IsActive, ct)
            : null;

        if (site is null)
        {
            _problems.Add("L'administrateur n'a pas de site actif : données de démonstration non créées.");
            return false;
        }

        _siteId = site.Id;
        _siteCode = site.Code;
        _actors[DemoActor.Administrator] = admin.Id;

        return true;
    }

    /// <summary>
    /// Puts a demonstration user in the session, exactly as a sign-in would, without a password.
    /// <para>
    /// Not through <see cref="IAuthenticationService.SignInAsync"/>: that needs the password, and
    /// on an existing installation the administrator has changed theirs — as they were forced to.
    /// It would also write a sign-in to the security log for every step, which would make the
    /// log of a demonstration database record sign-ins that never happened.
    /// </para>
    /// </summary>
    private async Task ActAsAsync(DemoActor actor, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(actor, out var session))
        {
            if (!_actors.TryGetValue(actor, out var userId))
                throw new InvalidOperationException($"Demonstration user {actor} is not available.");

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

            var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions)
                .Include(u => u.SiteAccess)
                .FirstAsync(u => u.Id == userId, ct);

            var permissions = user.Roles
                .Select(r => r.Role)
                .Where(r => r is { IsActive: true })
                .SelectMany(r => r!.Permissions.Select(p => p.Permission))
                .ToHashSet(StringComparer.Ordinal);

            session = new AuthenticatedSession(
                user.Id, user.UserName, user.FullName, _siteId, _siteCode, AppLanguage.French,
                user.Qualification, permissions, [.. user.SiteAccess.Select(a => a.SiteId)],
                user.AuthorizedDepartmentCodes, MustChangePassword: false);

            _sessions[actor] = session;
        }

        currentUser.Apply(session);
    }

    /// <summary>Runs one use case as one demonstration user, in a scope of its own.</summary>
    /// <remarks>
    /// A fresh scope per call, because the services are scoped around one change tracker: reusing
    /// one across steps would let an entity loaded by an earlier step be saved, stale, by a later
    /// one — the very thing a screen avoids by creating its own scope.
    /// </remarks>
    private async Task<T> AsAsync<TService, T>(DemoActor actor, Func<TService, Task<T>> action,
                                               CancellationToken ct) where TService : notnull
    {
        await ActAsAsync(actor, ct);

        using var scope = scopes.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>A read straight from the database, outside any service.</summary>
    private async Task<T> QueryAsync<T>(Func<LisDbContext, Task<T>> query)
    {
        using var scope = scopes.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<LisDbContext>());
    }

    /// <summary>
    /// A direct write, for the few records no service creates. Done as the administrator, so the
    /// audit interceptor stamps a real user on them.
    /// </summary>
    private async Task WriteAsync(Func<LisDbContext, Task> write, CancellationToken ct)
    {
        await ActAsAsync(DemoActor.Administrator, ct);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

        await write(db);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Records a refused step and says whether it went through.</summary>
    private bool Check(Result result, string step)
    {
        if (result.IsSuccess) return true;

        _problems.Add($"{step} : " + string.Join(", ", result.Errors.Select(
            e => e.Args.Length == 0
                ? e.ResourceKey
                : $"{e.ResourceKey}({string.Join("; ", e.Args)})")));

        return false;
    }

    private AuthenticatedSession? Snapshot() =>
        currentUser.IsAuthenticated
            ? new AuthenticatedSession(currentUser.UserId, currentUser.UserName,
                                       currentUser.FullName, currentUser.SiteId,
                                       currentUser.SiteCode, currentUser.Language,
                                       currentUser.Qualification, currentUser.Permissions,
                                       currentUser.AccessibleSiteIds,
                                       currentUser.AuthorizedDepartmentCodes, false,
                                       currentUser.PrescriberId, currentUser.IsPrescriberScoped,
                                       currentUser.SecurityStamp)
            : null;

    private void Restore(AuthenticatedSession? previous)
    {
        if (previous is null) currentUser.Clear();
        else currentUser.Apply(previous);
    }

    // ---- Users ---------------------------------------------------------------------------------

    /// <summary>
    /// One demonstration user per role a laboratory day needs, with a known password so the
    /// laboratory can sign in as each of them and see the screens from that role's side.
    /// <para>
    /// Created directly rather than through <c>UserAdministrationService</c>, because that service
    /// rightly forces a password change at first sign-in — and a demonstration whose four accounts
    /// all demand a new password before anything can be shown is a demonstration of the password
    /// screen. The passwords satisfy the configured policy all the same.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<DemoUser> Users =
    [
        new("demo.biologiste", "Demo-Biologiste-2026!", "Dr Yasmine Alaoui Belghiti (démo)",
            "د. ياسمين العلوي بلغيتي", ProfessionalQualification.MedicalBiologist,
            [SystemRoles.ResponsibleBiologist], "CNOM-DEMO-48213", "601234567"),

        new("demo.technicien", "Demo-Technicien-2026!", "Mehdi Aït Ouahmane (démo)",
            "مهدي آيت واحمان", ProfessionalQualification.LaboratoryTechnician,
            [SystemRoles.Technician], null, null),

        new("demo.secretaire", "Demo-Secretaire-2026!", "Hanane Bouzid (démo)",
            "حنان بوزيد", ProfessionalQualification.Administrative,
            [SystemRoles.Receptionist, SystemRoles.Accountant], null, null),

        new("demo.qualite", "Demo-Qualite-2026!", "Rachida Kettani (démo)",
            "رشيدة الكتاني", ProfessionalQualification.PharmacistBiologist,
            [SystemRoles.QualityManager], "CNOP-DEMO-20774", null)
    ];

    public sealed record DemoUser(string UserName, string Password, string FullName,
                                    string FullNameArabic, ProfessionalQualification Qualification,
                                    string[] Roles, string? OrderNumber, string? Inpe);

    private async Task SeedUsersAsync(CancellationToken ct)
    {
        var actorsByName = new Dictionary<string, DemoActor>
        {
            ["demo.biologiste"] = DemoActor.Biologist,
            ["demo.technicien"] = DemoActor.Technician,
            ["demo.secretaire"] = DemoActor.Secretary,
            ["demo.qualite"] = DemoActor.QualityManager
        };

        await WriteAsync(async db =>
        {
            var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Code, r => r.Id, ct);
            var names = Users.Select(u => u.UserName).ToList();

            var existing = await db.Users.IgnoreQueryFilters()
                .Where(u => names.Contains(u.UserName))
                .ToDictionaryAsync(u => u.UserName, u => u.Id, ct);

            foreach (var demo in Users)
            {
                if (existing.TryGetValue(demo.UserName, out var id))
                {
                    _actors[actorsByName[demo.UserName]] = id;
                    continue;
                }

                var user = new AppUser(demo.UserName, demo.FullName, passwordHasher.Hash(demo.Password))
                {
                    FullNameArabic = demo.FullNameArabic,
                    Email = $"{demo.UserName}@labo-demo.example",
                    Qualification = demo.Qualification,
                    ProfessionalOrderNumber = demo.OrderNumber,
                    Inpe = demo.Inpe,
                    QualificationValidUntil = demo.OrderNumber is null ? null : clock.Today.AddYears(3),
                    PreferredLanguage = AppLanguage.French
                };

                // Stamps the change date, so the expiry policy counts from today rather than
                // treating a password that was never "changed" as already expired.
                user.SetPassword(user.PasswordHash, clock.Now, mustChangeAtNextLogon: false);

                foreach (var code in demo.Roles)
                    if (roles.TryGetValue(code, out var roleId)) user.AssignRole(roleId);

                user.GrantSiteAccess(_siteId, isDefault: true);

                db.Users.Add(user);
                _actors[actorsByName[demo.UserName]] = user.Id;
            }
        }, ct);
    }
}
