using System.Security.Cryptography;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Tests.Licensing;

/// <summary>
/// The licences of the tests: signed with the development key's private half (a fixture of this
/// project), checked against its public half as MarocLIS embeds it.
/// </summary>
internal static class LicenceTestKit
{
    public static readonly DateOnly Today = new(2026, 10, 18);
    public const string ServerId = "A1B2-C3D4-E5F6-0718-293A";

    private static readonly Lazy<string> PrivatePem = new(() =>
    {
        using var stream = typeof(LicenceTestKit).Assembly
                               .GetManifestResourceStream("MarocLIS.Tests.dev-licence-private-key.pem")
                           ?? throw new InvalidOperationException("The development private key is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The development key's private half: a new instance, for the caller to dispose.</summary>
    public static ECDsa PrivateKey()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(PrivatePem.Value);
        return key;
    }

    /// <summary>The public half, as MarocLIS embeds it.</summary>
    public static ECDsa PublicKey() => new LicenceTrust().PublicKey;

    // A licence already ended began a year before its end, so its dates stay consistent.
    public static LicenceTerms Annual(int days = 365, params string[] modules) => new()
    {
        Number = "LIC-2026-0042",
        Version = 1,
        Laboratory = "Laboratoire Al Amal",
        Authorisation = "AUT-123/2019",
        Site = "Casablanca",
        Kind = LicenceKind.Annual,
        IssuedOn = Today.AddDays(Math.Min(-10, days - 365)),
        StartsOn = Today.AddDays(Math.Min(-10, days - 365)),
        EndsOn = Today.AddDays(days),
        Workstations = 5,
        Modules = modules,
        ServerId = ServerId
    };

    public static string Sign(LicenceTerms terms)
    {
        using var key = PrivateKey();
        return LicenceFile.Sign(terms, key);
    }

    /// <summary>A licence signed with a key that is not the vendor's.</summary>
    public static string SignWithStranger(LicenceTerms terms)
    {
        using var key = LicenceKeys.CreatePair();
        return LicenceFile.Sign(terms, key);
    }

    public static LicenceFacts Facts(string? text, DateOnly? today = null) => new()
    {
        LicenceText = text,
        ServerId = ServerId,
        Today = today ?? Today,
        ReleaseDate = Today.AddDays(-30)
    };

    public static LicenceStatus Evaluate(LicenceFacts facts)
    {
        using var key = PublicKey();
        return LicenceEvaluator.Evaluate(facts, key);
    }

    /// <summary>A licence in force with every module: what a host that tests something else runs under.</summary>
    public static LicenceStatus FullStatus() => new()
    {
        State = LicenceState.Valid,
        Mode = LicenceMode.Full,
        Today = Today,
        Modules = new HashSet<string>(LicenceModules.All, StringComparer.Ordinal),
        WorkstationLimit = 99
    };

    public static LicenceStatus ReadOnlyStatus(DateOnly? benchUntil = null) => new()
    {
        State = LicenceState.Expired,
        Mode = LicenceMode.ReadOnly,
        Today = Today,
        BenchClearingUntil = benchUntil,
        Modules = new HashSet<string>(LicenceModules.All, StringComparer.Ordinal)
    };

    // ---- Fakes -------------------------------------------------------------------------------------

    public sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(1));
        public DateOnly Today => DateOnly.FromDateTime(Now.Date);
    }

    public sealed class User(bool authenticated = true, params string[] permissions) : ICurrentUser
    {
        public bool IsAuthenticated => authenticated;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "admin";
        public string FullName => "Administrateur";
        public Guid SiteId => Guid.Empty;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(permissions);
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "POSTE-01";
    }

    public sealed class Guard(ICurrentUser user) : IPermissionGuard
    {
        private Result Answer(params string[] permissions) =>
            permissions.Any(user.Has) ? Result.Success() : Result.Failure(Error.Forbidden("Message.PermissionDenied"));

        public Result Require(string permission) => Answer(permission);
        public Result RequireAny(params string[] permissions) => Answer(permissions);
        public Result RequireAll(params string[] permissions) => permissions.All(user.Has) ? Result.Success() : Answer();

        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Answer(Permissions.Results.BiologicalValidate));
    }

    public sealed class Auditor : ISecurityAuditor
    {
        public List<(SecurityEventKind Kind, bool Succeeded, string? Reason, string? Details)> Events { get; } = [];

        public Task LogAsync(SecurityEventKind kind, bool succeeded, string? attemptedUserName = null,
                             string? failureReasonKey = null, Guid? targetUserId = null, string? details = null,
                             CancellationToken ct = default)
        {
            Events.Add((kind, succeeded, failureReasonKey, details));
            return Task.CompletedTask;
        }
    }

    /// <summary>The database's side of the licence, in memory.</summary>
    public sealed class Store : ILicenceStore
    {
        public List<LicenceRecord> Records { get; } = [];
        public string MachineName { get; set; } = "LABSRV";
        public Guid InstallationId { get; set; } = Guid.Parse("6f1c2a52-3a4b-4c4d-9e8f-0a1b2c3d4e5f");
        public int OrderCount { get; set; }
        public DateOnly? LatestOrderOn { get; set; }
        public bool HasPatients { get; set; }
        public DateOnly? LatestDateSeen { get; set; }
        public DateOnly? RestrictedSince { get; set; }

        public string ServerId => ServerIdentity.Compute(MachineName, null, InstallationId);

        public Task<StoredLicence> ReadAsync(CancellationToken ct = default)
        {
            var current = Records.Where(r => r.ReplacedAt is null).OrderByDescending(r => r.ImportedAt).FirstOrDefault();
            return Task.FromResult(new StoredLicence
            {
                Content = current?.Content,
                Number = current?.Number,
                Version = current?.Version,
                MachineName = MachineName,
                InstallationId = InstallationId,
                OrderCount = OrderCount,
                LatestOrderOn = LatestOrderOn,
                HasPatients = HasPatients,
                LatestDateSeen = LatestDateSeen,
                RestrictedSince = RestrictedSince,
                SiteName = "Laboratoire Al Amal",
                LegalName = "LABO AL AMAL SARL",
                Authorisation = "AUT-123/2019"
            });
        }

        public Task<IReadOnlyList<LicenceHistoryRow>> HistoryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LicenceHistoryRow>>(
                [.. Records.Select(r => new LicenceHistoryRow(r.Number, r.Version, r.Kind, r.ImportedAt,
                                                              r.ImportedByUserName, r.Workstation, r.ReplacedAt))]);

        public Task<(int Version, string Content)?> HighestVersionAsync(string number, CancellationToken ct = default) =>
            Task.FromResult(Records.Where(r => r.Number == number).OrderByDescending(r => r.Version)
                                   .Select(r => ((int, string)?)(r.Version, r.Content)).FirstOrDefault());

        public Task SaveAsync(LicenceRecord record, CancellationToken ct = default)
        {
            foreach (var previous in Records) previous.Replace(record.ImportedAt);
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task SaveMarkersAsync(DateOnly? latestDateSeen, DateOnly? restrictedSince, bool clearRestricted,
                                     CancellationToken ct = default)
        {
            if (latestDateSeen is { } seen && (LatestDateSeen is null || seen > LatestDateSeen)) LatestDateSeen = seen;
            if (restrictedSince is not null) RestrictedSince = restrictedSince;
            else if (clearRestricted) RestrictedSince = null;
            return Task.CompletedTask;
        }
    }
}
