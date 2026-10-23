using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Web.Security;

namespace MarocLIS.Web.Session;

/// <summary>
/// The session a scope works for: one HTTP request, or one Blazor circuit — one browser tab.
/// <para>
/// Scoped, like the operator it restores. The workstation holds one operator for the life of the
/// process, in a singleton; a server answering several browsers cannot, so every scope here gets
/// its own <see cref="CurrentUserAccessor"/> (<see cref="PerRequestIdentityServiceCollectionExtensions.UsePerRequestCurrentUser"/>)
/// and this replays into it the session the authentication service opened at sign-in. Everything
/// the scope resolves — the use cases, the permission guard, the auditors, the database context's
/// site filter — then reads this browser's operator, and nobody else's.
/// </para>
/// </summary>
public sealed class SessionContext(RequestSession request, WebSessionStore store, WebDefaults defaults)
{
    private DateTimeOffset _lastTouch;

    /// <summary>The browser's session, once attached; null before sign-in.</summary>
    public WebSession? Session { get; private set; }

    public bool IsSignedIn => Session is not null;

    /// <summary>The scope's operator, as every service of the scope sees it.</summary>
    public ICurrentUser User => request.User;

    /// <summary>The language of what the scope shows: the session's, else the browser's choice before sign-in.</summary>
    public AppLanguage Language { get; private set; } = defaults.Language;

    /// <summary>When the operator last did something in this tab; the idle timeout counts from it.</summary>
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.Now;

    /// <summary>Raised when the session's language or state changed, for the layout to redraw.</summary>
    public event Action? Changed;

    /// <summary>
    /// Takes <paramref name="session"/> as the scope's: its operator restored into the scope's
    /// accessor and named on the scope's audit records by the session's workstation. Once only:
    /// a scope that served one session never serves another.
    /// </summary>
    public void Attach(WebSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (Session is not null && Session.Id != session.Id)
            throw new InvalidOperationException("A scope serves one session.");

        Session = session;
        Language = session.Language;
        request.UseWorkstation(session.AuditWorkstation);
        request.Restore(session.Session);
    }

    /// <summary>The language of an anonymous page: the browser's choice, coerced by the caller.</summary>
    public void UseLanguage(AppLanguage language)
    {
        if (Language == language) return;
        Language = language;
        Changed?.Invoke();
    }

    /// <summary>Reads the session again from the store, after it was changed elsewhere; false when it is gone.</summary>
    public bool Refresh()
    {
        if (Session is null) return false;

        // Not Find: whether the session has been idle too long is the monitor's to decide and to
        // record, from the activity of every tab, not the store's to drop silently here.
        if (store.Get(Session.Id) is not { } current) return false;

        var languageChanged = current.Language != Language;
        Session = current;
        Language = current.Language;
        if (languageChanged) Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Notes the operator's activity in this tab. The store is told at most every half minute: the
    /// idle timeout counts in minutes, and a mouse moving across a list is many events a second.
    /// </summary>
    public void RecordActivity()
    {
        var now = DateTimeOffset.Now;
        LastActivity = now;

        if (Session is null || now - _lastTouch < TimeSpan.FromSeconds(30)) return;
        _lastTouch = now;
        store.Touch(Session.Id);
    }
}

/// <summary>The defaults of a scope before anything is known of the browser.</summary>
public sealed record WebDefaults(AppLanguage Language);
