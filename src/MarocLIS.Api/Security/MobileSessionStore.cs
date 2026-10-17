using System.Collections.Concurrent;
using System.Security.Cryptography;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;

namespace MarocLIS.Api.Security;

/// <summary>A phone's session, as the server holds it.</summary>
/// <param name="Session">What the authentication service produced at sign-in, replayed on each request.</param>
/// <param name="Workstation">The device that signed in; a request from another device is refused.</param>
public sealed record MobileSession(
    string Id,
    AuthenticatedSession Session,
    string Workstation,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastSeenAt);

/// <summary>Why a session is no longer usable.</summary>
public enum SessionEnd
{
    None = 0,

    /// <summary>Not known here: signed out, ended, or opened before the service restarted.</summary>
    Unknown = 1,

    /// <summary>Unused for longer than the password policy's idle timeout.</summary>
    Idle = 2,

    /// <summary>Older than the absolute lifetime, however busy it was.</summary>
    Expired = 3
}

/// <summary>
/// The server-side record of the phones signed in.
/// <para>
/// The cookie carries only a random identifier; the session itself — who, which site, which
/// permissions, which device — stays here. Signing out therefore really ends the session: a copy of
/// the cookie taken before is worth nothing after, which a self-contained cookie cannot promise.
/// The idle timeout is enforced here too, to the minute, rather than left to the cookie's sliding
/// renewal.
/// </para>
/// <para>
/// In memory, deliberately. A restart of the service signs every phone out, which costs a biologist
/// one sign-in; in exchange nothing about a session is ever written to disk on the server. The
/// cookie's encryption keys are ephemeral for the same reason (see <c>ApiServiceCollectionExtensions</c>).
/// </para>
/// </summary>
public sealed class MobileSessionStore(PasswordPolicy policy, MobileApiOptions options, IClock clock)
{
    private readonly ConcurrentDictionary<string, MobileSession> _sessions = new(StringComparer.Ordinal);

    public TimeSpan IdleTimeout => policy.IdleTimeout;

    public MobileSession Open(AuthenticatedSession session, string workstation)
    {
        // 256 bits from the operating system's generator: guessing one is not a plan.
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var now = clock.Now;
        var opened = new MobileSession(id, session, workstation, now, now);
        _sessions[id] = opened;

        Sweep(now);
        return opened;
    }

    /// <summary>The live session for <paramref name="id"/>, or why there is none. A live one is touched.</summary>
    public (MobileSession? Session, SessionEnd End) Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var session))
            return (null, SessionEnd.Unknown);

        var now = clock.Now;
        var end = EndOf(session, now);

        if (end != SessionEnd.None)
        {
            _sessions.TryRemove(id, out _);
            return (session, end);
        }

        var touched = session with { LastSeenAt = now };
        _sessions.TryUpdate(id, touched, session);
        return (touched, SessionEnd.None);
    }

    /// <summary>
    /// Switches the language of a live session, for « Mon compte » on the phone. Only the session:
    /// the account's preferred language is the workstation's, and a biologist who reads the phone
    /// in Arabic may well want the bench in French.
    /// </summary>
    public MobileSession? ChangeLanguage(string? id, AppLanguage language)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var session)) return null;

        var changed = session with { Session = session.Session with { Language = language } };
        return _sessions.TryUpdate(id, changed, session) ? changed : null;
    }

    public void Remove(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _sessions.TryRemove(id, out _);
    }

    /// <summary>Sessions open now, for the tests and for a status line.</summary>
    public int Count => _sessions.Count;

    private SessionEnd EndOf(MobileSession session, DateTimeOffset now)
    {
        if (now - session.LastSeenAt > policy.IdleTimeout) return SessionEnd.Idle;
        if (now - session.OpenedAt > options.MaxSessionLifetime) return SessionEnd.Expired;
        return SessionEnd.None;
    }

    // Abandoned sessions (a phone that never signed out) are dropped as others open, so the store
    // cannot grow without bound between restarts.
    private void Sweep(DateTimeOffset now)
    {
        foreach (var (id, session) in _sessions)
            if (EndOf(session, now) != SessionEnd.None) _sessions.TryRemove(id, out _);
    }
}
