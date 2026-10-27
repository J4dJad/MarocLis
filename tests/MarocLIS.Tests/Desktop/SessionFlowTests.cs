using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The working day on a workstation: the sign-in, the main window, the sign-out back to the sign-in
/// in the same process, a change of reading direction that rebuilds the window for the same
/// session, and the language a session opens in.
/// </summary>
public sealed class SessionFlowTests
{
    [Fact]
    public void TheSignIn_OpensTheMainWindow_OrEndsTheApplicationWhenClosed()
    {
        SessionFlow.AfterLogin(signedIn: true).Should().Be(SessionStep.OpenShell);
        SessionFlow.AfterLogin(signedIn: false).Should().Be(SessionStep.Quit);
    }

    [Fact]
    public void SigningOut_ReturnsToTheSignIn_AndClosingTheWindow_EndsTheApplication()
    {
        // « Se déconnecter » used to end the whole application once the stray sign-in was closed.
        SessionFlow.AfterShell(ShellExit.SignedOut).Should().Be(SessionStep.ShowLogin);
        SessionFlow.AfterShell(ShellExit.Restart).Should().Be(SessionStep.ShowLogin);
        SessionFlow.AfterShell(ShellExit.Closed).Should().Be(SessionStep.Quit);
        SessionFlow.AfterShell(ShellExit.Rebuilt).Should().Be(SessionStep.RebuildShell);
    }

    [Fact]
    public void OnlyARestore_OpensTheDatabaseAgainBeforeTheNextSignIn()
    {
        SessionFlow.ReopensDatabase(ShellExit.Restart).Should().BeTrue();
        SessionFlow.ReopensDatabase(ShellExit.SignedOut).Should().BeFalse();
    }

    [Theory]
    [InlineData(AppLanguage.French, AppLanguage.Arabic, true)]
    [InlineData(AppLanguage.English, AppLanguage.Arabic, true)]
    [InlineData(AppLanguage.Arabic, AppLanguage.French, true)]
    [InlineData(AppLanguage.French, AppLanguage.English, false)]
    [InlineData(AppLanguage.English, AppLanguage.French, false)]
    public void OnlyAChangeOfDirection_RebuildsTheMainWindow(AppLanguage from, AppLanguage to, bool rebuilt) =>
        SessionFlow.ChangesDirection(from, to).Should().Be(rebuilt);

    [Fact]
    public void TheRebuiltWindow_ReopensTheScreenTheOperatorWasOn_WhenStillPermitted()
    {
        SessionFlow.StartScreen("patients", _ => true).Should().Be("patients");
        SessionFlow.StartScreen("billing", key => key != "billing").Should().BeNull();
        SessionFlow.StartScreen(null, _ => true).Should().BeNull();
    }

    [Fact]
    public void TheLanguageChosenAtTheSignIn_WinsOverTheAccounts()
    {
        var all = LanguageSettings.Default;

        // « العربية » chosen on the sign-in, an account in French: the interface stays in Arabic.
        all.ForSession(AppLanguage.Arabic, AppLanguage.French).Should().Be(AppLanguage.Arabic);

        // A workstation that never chose: the account's language.
        all.ForSession(null, AppLanguage.English).Should().Be(AppLanguage.English);

        // Arabic switched off since: the account's, then French.
        var noArabic = LanguageSettings.Of([AppLanguage.English]);
        noArabic.ForSession(AppLanguage.Arabic, AppLanguage.English).Should().Be(AppLanguage.English);
        noArabic.ForSession(AppLanguage.Arabic, AppLanguage.Arabic).Should().Be(AppLanguage.French);
    }

    [Theory]
    [InlineData("Arabic", AppLanguage.Arabic)]
    [InlineData("English", AppLanguage.English)]
    [InlineData("French", AppLanguage.French)]
    [InlineData("arabic", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheStoredChoice_IsReadByName_AndAnythingElseIsNoChoice(string? stored, AppLanguage? expected) =>
        LanguageSettings.Parse(stored).Should().Be(expected);
}
