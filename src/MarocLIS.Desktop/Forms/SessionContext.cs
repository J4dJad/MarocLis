using MarocLIS.Desktop.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The message loop's context for the whole working day: the sign-in, then the main window, then
/// the sign-in again after a sign-out, in turns, in this one process (<see cref="SessionFlow"/>).
/// <para>
/// One window at a time is the application's main window. Each closes before the next opens, and
/// the next is brought to the front and given the focus, so a dialog opened from it is in front of
/// it and answers — there is no sign-in window left behind to take the dialogs' focus.
/// </para>
/// </summary>
internal sealed class SessionContext : ApplicationContext
{
    private bool _firstSession = true;

    /// <summary>Opens the first sign-in. False when there is nothing to show and the application ends.</summary>
    public bool Start() => OpenLogin(openDatabase: true);

    // ---- Sign-in ----------------------------------------------------------------------------------

    private bool OpenLogin(bool openDatabase)
    {
        var warnAboutFonts = _firstSession;
        _firstSession = false;

        if (!Program.StartHost(openDatabase, warnAboutFonts)) return false;

        var login = Program.Services.GetRequiredService<LoginForm>();
        login.FormClosed += OnLoginClosed;
        MainForm = login;
        Present(login);
        return true;
    }

    private void OnLoginClosed(object? sender, FormClosedEventArgs e)
    {
        var login = (LoginForm)sender!;
        login.FormClosed -= OnLoginClosed;

        // Detached first: the sign-in's window is destroyed after this event, and as the main
        // window its destruction would end the message loop before the shell opens.
        MainForm = null;

        if (SessionFlow.AfterLogin(login.DialogResult == DialogResult.OK) == SessionStep.Quit)
        {
            ExitThread();
            return;
        }

        // Once the sign-in's own handler has finished with it.
        Post(() => OpenShell(startScreen: null));
    }

    // ---- Main window ------------------------------------------------------------------------------

    private void OpenShell(string? startScreen)
    {
        try
        {
            var shell = ActivatorUtilities.CreateInstance<MainShellForm>(Program.Services);
            shell.StartScreen = startScreen;
            shell.FormClosed += OnShellClosed;
            shell.DirectionChanged += OnDirectionChanged;
            MainForm = shell;
            Present(shell);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The main window could not be opened.");
            MessageBox.Show(ex.Message, "MarocLIS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitThread();
        }
    }

    /// <summary>
    /// The interface changed direction: a new main window for the same session, on the screen the
    /// operator was on, shown before the old one closes so the desktop never shows a gap.
    /// </summary>
    private void OnDirectionChanged(object? sender, EventArgs e)
    {
        var old = (MainShellForm)sender!;
        old.DirectionChanged -= OnDirectionChanged;
        old.FormClosed -= OnShellClosed;

        var screen = old.ActiveScreen;
        MainForm = null;

        Post(() =>
        {
            OpenShell(screen);
            old.CloseReplaced();
        });
    }

    private void OnShellClosed(object? sender, FormClosedEventArgs e)
    {
        var shell = (MainShellForm)sender!;
        shell.FormClosed -= OnShellClosed;
        shell.DirectionChanged -= OnDirectionChanged;

        var exit = shell.Exit;
        MainForm = null;

        if (SessionFlow.AfterShell(exit) != SessionStep.ShowLogin)
        {
            ExitThread();
            return;
        }

        // The session's services go with it, and new ones are built for the next operator: nothing
        // of this session — a scope, a cached query, a tracked entity — reaches theirs.
        Post(() =>
        {
            Program.StopHost();

            bool opened;
            try
            {
                opened = OpenLogin(SessionFlow.ReopensDatabase(exit));
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "The sign-in could not be opened again.");
                MessageBox.Show(ex.Message, "MarocLIS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                opened = false;
            }

            if (!opened) ExitThread();
        });
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>Shows a window in front of the others, with the focus.</summary>
    private static void Present(Form form)
    {
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.BringToFront();
        form.Activate();
    }

    private static void Post(Action action)
    {
        var context = SynchronizationContext.Current;
        if (context is null) action();
        else context.Post(_ => action(), null);
    }
}
