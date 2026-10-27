using MarocLIS.Domain.Common;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>Why the shell window closed.</summary>
public enum ShellExit
{
    /// <summary>Closed by the operator (title-bar cross, Alt+F4, Windows log-off): the application ends.</summary>
    Closed,

    /// <summary>Signed out, by the operator, the idle lock or a revoked account: back to the sign-in.</summary>
    SignedOut,

    /// <summary>
    /// The database was replaced (a restore): back to the sign-in, on a database opened and checked
    /// again from the start.
    /// </summary>
    Restart,

    /// <summary>
    /// The interface changed reading direction (French or English to Arabic, or back): the same
    /// session goes on in a new window built in the new direction.
    /// </summary>
    Rebuilt
}

/// <summary>What the application does next.</summary>
public enum SessionStep
{
    /// <summary>Show the sign-in, on a fresh set of services.</summary>
    ShowLogin,

    /// <summary>Open the main window for the operator just signed in.</summary>
    OpenShell,

    /// <summary>Open a new main window for the same session, in the new direction.</summary>
    RebuildShell,

    /// <summary>End the application.</summary>
    Quit
}

/// <summary>
/// The order of the windows of a working day, in one process.
/// <para>
/// The sign-out used to restart the whole process. The new process opened its own « Connexion »
/// window while the old one was still closing — on another screen, behind the others, with the
/// previous operator's name — and a workstation could end up with two instances, one of them
/// showing a sign-in nobody asked for. The sign-in, the main window and the sign-out now take turns
/// in the same process: each window closes before the next one opens, and the next one takes the
/// focus. What made the restart attractive — nothing of one operator's session surviving into the
/// next — is kept by disposing every service of the session and building them again.
/// </para>
/// <para>
/// WinForms-free so the order can be tested.
/// </para>
/// </summary>
public static class SessionFlow
{
    /// <summary>After the sign-in window closes: the shell when somebody signed in, the end otherwise.</summary>
    public static SessionStep AfterLogin(bool signedIn) => signedIn ? SessionStep.OpenShell : SessionStep.Quit;

    /// <summary>After the shell window closes, by the reason it gave.</summary>
    public static SessionStep AfterShell(ShellExit exit) => exit switch
    {
        ShellExit.SignedOut or ShellExit.Restart => SessionStep.ShowLogin,
        ShellExit.Rebuilt => SessionStep.RebuildShell,
        _ => SessionStep.Quit
    };

    /// <summary>
    /// True when a sign-out leaves the database to be opened and checked again before the next
    /// sign-in: only after a restore, which replaced it. An ordinary sign-out does not run the
    /// migrations and the demonstration data again.
    /// </summary>
    public static bool ReopensDatabase(ShellExit exit) => exit == ShellExit.Restart;

    /// <summary>
    /// True when going from <paramref name="from"/> to <paramref name="to"/> changes the reading
    /// direction, so the main window is rebuilt rather than mirrored in place.
    /// <para>
    /// Mirrored in place, every control of the window and of the open screen recreated its
    /// window handle one by one: the screen stayed white for a long moment, and the window could
    /// be left unresponsive. A new window built in the right direction from the start has none of
    /// that, and the session — the operator, the screen they were on — carries over.
    /// </para>
    /// </summary>
    public static bool ChangesDirection(AppLanguage from, AppLanguage to) =>
        from.IsRightToLeft() != to.IsRightToLeft();

    /// <summary>
    /// The screen the rebuilt window opens on: the one the operator was on, when they may still
    /// open it; otherwise the first screen of the menu.
    /// </summary>
    public static string? StartScreen(string? previous, Func<string, bool> permitted)
    {
        ArgumentNullException.ThrowIfNull(permitted);
        return !string.IsNullOrEmpty(previous) && permitted(previous) ? previous : null;
    }
}
