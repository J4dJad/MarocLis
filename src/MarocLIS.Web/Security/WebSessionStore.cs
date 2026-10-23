using System.Collections.Concurrent;
using System.Security.Cryptography;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;

namespace MarocLIS.Web.Security;

/// <summary>A browser's session, as the server holds it.</summary>
/// <param name="Session">What the authentication service produced at sign-in, replayed in every scope.</param>
/// <param name="Workstation">The browser's workstation (<see cref="BrowserIdentity"/>); another browser is refused.</param>
/// <param name="AuditWorkstation">The workstation and its address, as the audit records name it.</param>
/// <param name="Language">The session's language: the account's at sign-in, then the operator's choice.</param>
/// <param name="PasswordChangeRequired">The core asked for a new password; nothing else opens until it is given.</param>
/// <param name="SignInNoticePending">The licence's notice at sign-in has yet to be shown.</param>
public sealed record WebSession(
    string Id,
    AuthenticatedSession Session,
    string Workstation,
    string AuditWorkstation,
    AppLanguage Language,
    bool PasswordChangeRequired,
    bool SignInNoticePending,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset LastCheckedAt);

/// <summary>Why a session is no longer usable.</summary>
public enum WebSessionEnd
{
    None = 0,

    /// <summary>Not known here: signed out, ended, or opened before the server restarted.</summary>
    Unknown = 1,

    /// <summary>Unused for longer than the password policy's idle timeout.</summary>
    Idle = 2
}

/// <summary>
/// The server-side record of the browsers signed in, as <c>MobileSessionStore</c> is the phones'.
/// <para>
/// The cookie carries only a random identifier; who, which site, which rights and which browser
/// stay here, so signing out really ends the session and a copy of the cookie is worth nothing
/// after. In memory, deliberately: a restart signs every browser out, and nothing about a session
/// is ever written to the server's disk.
/// </para>
/// <para>
/// The idle timeout is the password policy's — the delay after which a Windows workstation locks —
/// counted from the last thing the operator did in the browser (<see cref="Touch"/>), not from the
/// last HTTP request: a Blazor page talks to the server over its circuit, not in requests.
/// </para>
/// </summary>
public sealed class WebSessionStore(PasswordPolicy policy, IClock clock)
{
    private readonly ConcurrentDictionary<string, WebSession> _sessions = new(StringComparer.Ordinal);

    public TimeSpan IdleTimeout => policy.IdleTimeout;

    public WebSession Open(AuthenticatedSession session, string workstation, string auditWorkstation,
                           AppLanguage language, bool signInNotice)
    {
        ArgumentNullException.ThrowIfNull(session);

        // 256 bits from the operating system's generator: guessing one is not a plan.
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var now = clock.Now;
        var opened = new WebSession(id, session, workstation, auditWorkstation, language,
                                    session.MustChangePassword, signInNotice, now, now, now);
        _sessions[id] = opened;

        Sweep(now);
        return opened;
    }

    /// <summary>The live session for <paramref name="id"/>, or why there is none.</summary>
    public (WebSession? Session, WebSessionEnd End) Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var session))
            return (null, WebSessionEnd.Unknown);

        if (IsIdle(session, clock.Now))
        {
            _sessions.TryRemove(id, out _);
            return (session, WebSessionEnd.Idle);
        }

        return (session, WebSessionEnd.None);
    }

    /// <summary>The session as it is held, idle or not; null when there is none.</summary>
    public WebSession? Get(string? id) =>
        !string.IsNullOrEmpty(id) && _sessions.TryGetValue(id, out var session) ? session : null;

    /// <summary>Notes the operator's activity: the idle timeout starts again from now.</summary>
    public void Touch(string? id) => Update(id, s => s with { LastSeenAt = clock.Now });

    /// <summary>Changes a live session; null when it is gone.</summary>
    public WebSession? Update(string? id, Func<WebSession, WebSession> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (string.IsNullOrEmpty(id)) return null;

        while (_sessions.TryGetValue(id, out var current))
        {
            var changed = change(current);
            if (_sessions.TryUpdate(id, changed, current)) return changed;
        }

        return null;
    }

    public void Remove(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _sessions.TryRemove(id, out _);
    }

    /// <summary>Sessions open now, for the tests and a status line.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Idle for longer than the policy allows. A policy without an idle timeout (zero) never ends a
    /// session for inactivity, as it never locks a workstation.
    /// </summary>
    public bool IsIdle(WebSession session, DateTimeOffset now) =>
        policy.IdleTimeout > TimeSpan.Zero && now - session.LastSeenAt >= policy.IdleTimeout;

    // Abandoned sessions (a browser closed without signing out) are dropped as others open.
    private void Sweep(DateTimeOffset now)
    {
        foreach (var (id, session) in _sessions)
            if (IsIdle(session, now)) _sessions.TryRemove(id, out _);
    }
}
