using MarocLIS.Api.Contracts;

namespace MarocLIS.Mobile.Services;

/// <summary>Why the phone is on the sign-in screen.</summary>
public enum SessionEndReason
{
    /// <summary>Never signed in on this opening of the application.</summary>
    NotSignedIn = 0,

    /// <summary>The idle timeout ran out on the phone's own clock.</summary>
    Idle = 1,

    /// <summary>« Se déconnecter ».</summary>
    SignedOut = 2,

    /// <summary>The server answered 401: expired, revoked, another device, restarted.</summary>
    Server = 3
}

/// <summary>
/// The signed-in biologist on this phone, and the idle clock.
/// <para>
/// The clock mirrors the server's: the server ends a session after the password policy's idle
/// timeout without a request (MobileSessionStore), so the phone counts the same minutes from its
/// last answered request and locks the screen at the same moment — instead of leaving a patient's
/// file on screen to fail at the next tap. A request answered resets it; a tap that sends nothing
/// does not, because it does not keep the server's session alive either.
/// </para>
/// </summary>
public sealed class SessionState : IDisposable
{
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private DateTimeOffset _lastActivity;

    public SessionState() : this(TimeProvider.System) { }

    public SessionState(TimeProvider time) => _time = time;

    public SessionInfo? Info { get; private set; }

    public bool IsSignedIn => Info is not null;

    /// <summary>When the session opened, as the server says; the phone's clock otherwise.</summary>
    public DateTimeOffset SignedInAt { get; private set; }

    public SessionEndReason EndReason { get; private set; }

    /// <summary>The server's sentence when it ended the session, in the biologist's language.</summary>
    public string? EndMessage { get; private set; }

    /// <summary>
    /// The last session's identifier and idle timeout, kept after it ends: the sign-in screen offers
    /// the identifier again (never the password) and states the timeout before anyone signs in.
    /// </summary>
    public string? LastUserName { get; private set; }

    public int LastIdleMinutes { get; private set; } = 15;

    /// <summary>
    /// The languages the laboratory works in, French first: the only ones the language buttons
    /// offer. The three until the server has said otherwise; kept after the session ends, for the
    /// sign-in screen.
    /// </summary>
    public IReadOnlyList<UiLanguage> Languages { get; private set; } = Enum.GetValues<UiLanguage>();

    /// <summary>The languages as the server names them; French is always among them.</summary>
    public void UseLanguages(IEnumerable<string>? names)
    {
        if (names is null) return;
        Languages = [.. names.Select(Texts.Parse).Append(UiLanguage.French).Distinct().Order()];
    }

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(Math.Max(1, Info?.IdleTimeoutMinutes ?? 15));

    /// <summary>Raised when the session ends, whatever the reason; the layout returns to the sign-in screen.</summary>
    public event Action<SessionEndReason>? Ended;

    /// <summary>Raised when <see cref="Info"/> changes (sign-in, language).</summary>
    public event Action? Changed;

    public void SignedIn(SessionInfo info)
    {
        Info = info;
        LastUserName = info.UserName;
        LastIdleMinutes = info.IdleTimeoutMinutes;
        SignedInAt = info.SignedInAt ?? _time.GetUtcNow();
        EndReason = SessionEndReason.NotSignedIn;
        EndMessage = null;
        UseLanguages(info.ActiveLanguages);
        Touch();
        Changed?.Invoke();
    }

    /// <summary>The session's description changed on the server (its language).</summary>
    public void Update(SessionInfo info)
    {
        if (!IsSignedIn) return;
        Info = info;
        UseLanguages(info.ActiveLanguages);
        Changed?.Invoke();
    }

    /// <summary>A request was answered: the server's idle clock restarted, so the phone's does.</summary>
    public void Touch()
    {
        _lastActivity = _time.GetUtcNow();
        if (!IsSignedIn) return;

        _timer ??= _time.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Ends the session now if the idle timeout has passed — on the phone waking up, when a timer
    /// suspended with the page may not have fired yet.
    /// </summary>
    public void CheckIdle()
    {
        if (IsSignedIn && _time.GetUtcNow() - _lastActivity >= IdleTimeout) Expire();
    }

    private void Expire() => End(SessionEndReason.Idle);

    public void End(SessionEndReason reason, string? message = null)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var wasSignedIn = IsSignedIn;
        Info = null;
        EndReason = reason;
        EndMessage = message;

        if (wasSignedIn || reason != SessionEndReason.NotSignedIn) Ended?.Invoke(reason);
    }

    public void Dispose() => _timer?.Dispose();
}
