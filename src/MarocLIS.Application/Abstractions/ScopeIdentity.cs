using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Abstractions;

/// <summary>
/// The identity a DI scope works under when it is not the operator signed in at this workstation.
/// <para>
/// <see cref="ICurrentUser"/> is a process-wide singleton, which is right for the screens and wrong
/// for the analyser host: an import running in the background of the bench workstation was filtered
/// to the sites of whoever was signed in — a tube from another site came back
/// "Interface_SpecimenUnknown" — and its audit trail named that operator. A scope that sets an
/// identity here, before resolving anything else, gets a database context, an audit interceptor and
/// a sequence allocator that see that identity instead.
/// </para>
/// </summary>
public sealed class ScopeIdentity
{
    /// <summary>The identity the scope works under; null means the signed-in operator.</summary>
    public ICurrentUser? User { get; private set; }

    /// <summary>Whether the scope's database context reads every site.</summary>
    public bool IgnoreSiteScope { get; private set; }

    /// <summary>
    /// Sets the scope's identity. Once only, and before anything that reads it is resolved: a
    /// context created before the call would keep the operator's site filter.
    /// </summary>
    public void RunAs(ICurrentUser user, bool ignoreSiteScope)
    {
        if (User is not null)
            throw new InvalidOperationException("The identity of a scope is set once.");

        User = user;
        IgnoreSiteScope = ignoreSiteScope;
    }
}

/// <summary>
/// A non-interactive account: the analyser host and other background work run under it, never
/// under the operator who happens to be signed in.
/// <para>
/// No site and no permission. It is not a session anybody opened, so it cannot pass a permission
/// check; the work it does reads across sites because the scope that uses it says so explicitly.
/// </para>
/// </summary>
public sealed class ServiceAccountUser(Guid userId, string userName, string fullName) : ICurrentUser
{
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.Ordinal);

    public bool IsAuthenticated => true;

    public Guid UserId { get; } = userId;
    public string UserName { get; } = userName;
    public string FullName { get; } = fullName;

    /// <summary>None: <see cref="Guid.Empty"/>, which audit records as "no site".</summary>
    public Guid SiteId => Guid.Empty;
    public string SiteCode => string.Empty;

    public AppLanguage Language => AppLanguage.French;

    public ProfessionalQualification Qualification => ProfessionalQualification.None;

    public IReadOnlyList<Guid> AccessibleSiteIds => [];

    public IReadOnlySet<string> Permissions => Nothing;

    public IReadOnlySet<string> AuthorizedDepartmentCodes => Nothing;

    public bool Has(string permission) => false;

    public string Workstation { get; } = Environment.MachineName;
}
