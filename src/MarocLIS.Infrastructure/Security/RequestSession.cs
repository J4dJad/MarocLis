using MarocLIS.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarocLIS.Infrastructure.Security;

/// <summary>
/// The identity of one request to the MarocLIS API: who signed in on the phone, and from which
/// device.
/// <para>
/// The desktop holds one operator for the life of the process, in a singleton
/// <see cref="CurrentUserAccessor"/>. A server answering several biologists at once cannot: each
/// request has to carry its own operator, or one biologist's validation would be signed with the
/// name of whoever happened to sign in last. With <see cref="PerRequestIdentityServiceCollectionExtensions.UsePerRequestCurrentUser"/>
/// the accessor is scoped instead, so everything a request resolves — the use cases, the permission
/// guard, the auditors, the audit interceptor, the database context's site filter — reads the
/// request's operator and nobody else's.
/// </para>
/// <para>
/// The accessor's setters stay internal to this assembly, as on the desktop: this is the one public
/// way in, and it only replays a session the authentication service itself produced at sign-in.
/// </para>
/// </summary>
public sealed class RequestSession(CurrentUserAccessor accessor)
{
    /// <summary>The audit columns hold 100 characters; a longer device name is shortened, not refused.</summary>
    public const int MaxWorkstationLength = 100;

    /// <summary>The request's operator, as every service of the request sees it.</summary>
    public ICurrentUser User => accessor;

    /// <summary>
    /// Names the device the request comes from (« Mobile · … »), recorded on every audit entry,
    /// data-access log and security event of the request. Set first, before signing in, so the
    /// sign-in itself is recorded against the phone.
    /// </summary>
    public void UseWorkstation(string workstation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workstation);

        accessor.SetWorkstation(workstation.Length <= MaxWorkstationLength
            ? workstation
            : string.Concat(workstation.AsSpan(0, MaxWorkstationLength - 1), "…"));
    }

    /// <summary>
    /// Restores the session opened at sign-in for this request. The session is then re-checked
    /// against the account by <see cref="ISessionValidator"/>, as the desktop shell does on its timer.
    /// </summary>
    public void Restore(AuthenticatedSession session) => accessor.Apply(session);
}

public static class PerRequestIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the desktop's process-wide operator by one per scope — per request, in the API.
    /// Called after <c>AddMarocLisInfrastructure</c>; the desktop never calls it and keeps its
    /// singleton.
    /// <para>
    /// The session validator follows, since it reads the accessor: it was a singleton safe to hold
    /// for the life of the shell, and here it must check the request's session, not a stale one.
    /// </para>
    /// </summary>
    public static IServiceCollection UsePerRequestCurrentUser(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<CurrentUserAccessor, CurrentUserAccessor>());
        services.Replace(ServiceDescriptor.Scoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserAccessor>()));
        services.Replace(ServiceDescriptor.Scoped<ISessionValidator, SessionValidator>());
        services.TryAddScoped<RequestSession>();

        return services;
    }
}
