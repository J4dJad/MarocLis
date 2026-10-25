using System.Text;
using Bunit;
using FluentAssertions;
using MarocLIS.Application.Migration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Migration;
using MarocLIS.Infrastructure.Reprise;
using MarocLIS.Web.Components.Screens.Migration;
using MarocLIS.Web.Session;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Reprise de données » in the browser: a CSV sent from the browser read on the server, its
/// columns matched; the « Simulation » refusing the row without a name; « Importer » only after the
/// simulation and its question, written through the workstation's DataMigrationService, whose store
/// closes the journal entry with its audit line; a profile kept in the server's folder; nothing
/// offered without the migration right; and the screen in Arabic.
/// </summary>
public sealed class WebDataMigrationTests : WebScreenContext, IDisposable
{
    private readonly Fake<IDataMigrationStore> _store = Fake<IDataMigrationStore>.Create();
    private readonly string _profiles = Path.Combine(Path.GetTempPath(), $"maroclis-web-profiles-{Guid.NewGuid():N}");

    private const string Patients =
        "id;nom;prenom;date_naissance\r\n" +
        "P1;ALAOUI;Fatima Zahra;14/05/1988\r\n" +
        "P2;;Karim;01/01/1990\r\n" +
        "P3;BENNANI;Youssef;23/11/1975\r\n";

    public WebDataMigrationTests()
    {
        _store.On(nameof(IDataMigrationStore.LoadContextAsync), _ => new MigrationContext { Today = new DateOnly(2026, 10, 18) });
        _store.On(nameof(IDataMigrationStore.WriteChunkAsync), a => (((IReadOnlyList<PlannedRow>)a[2]!).Count, 0));

        Services.AddSingleton(_store.Object);
        Services.AddSingleton(Fake<IMigrationSourceReader>.Create().Object);
        Services.AddSingleton<IMigrationWorkbook, MigrationWorkbook>();
        Services.AddSingleton(new MigrationProfileStore(_profiles));
        Services.AddScoped<DataMigrationService>();
    }

    void IDisposable.Dispose()
    {
        if (Directory.Exists(_profiles)) Directory.Delete(_profiles, recursive: true);
        Dispose();
    }

    private static readonly string[] Migrator = [Permissions.Administration.Migration];

    private static void Upload(IRenderedComponent<DataMigration> page, string name, string content) =>
        page.FindComponents<InputFile>()[0].UploadFiles(InputFileContent.CreateFromBinary(Encoding.UTF8.GetBytes(content), name));

    private IRenderedComponent<DataMigration> Read(string system = "LABOWIN")
    {
        var page = Render<DataMigration>();
        page.Find("[data-field=system]").Input(system);
        Upload(page, "patients.csv", Patients);
        page.WaitForAssertion(() => page.Find("[data-state=status]").TextContent.Should().Contain("3 ligne(s) lue(s)"));
        return page;
    }

    [Fact]
    public void ACsvSentFromTheBrowser_IsReadOnTheServer_ItsColumnsMatched_AndPreviewed()
    {
        SignIn(AppLanguage.French, Migrator);

        var page = Read();

        page.Find("[data-state=file]").TextContent.Should().Be("patients.csv");
        page.Find("[data-state=database-notice]").TextContent.Should().Contain("application Windows");
        page.FindAll("[data-grid=columns] tbody tr").Should().NotBeEmpty();
        page.Find("[data-grid=columns] [data-row=LastName] option[selected]").TextContent.Should().Contain("nom");

        page.Find("[data-tab=Preview]").Click();
        page.FindAll("[data-grid=preview] tbody tr").Should().HaveCount(3);
    }

    [Fact]
    public void AFileThatIsNeitherExcelNorCsv_IsRefused_BeforeAnythingIsRead()
    {
        SignIn(AppLanguage.French, Migrator);
        var page = Render<DataMigration>();

        Upload(page, "patients.pdf", "%PDF-1.7");

        page.Find("[data-state=status]").ClassList.Should().Contain("is-critical");
        page.Find("[data-state=file]").TextContent.Should().NotBe("patients.pdf");
        _store.CallsTo(nameof(IDataMigrationStore.LoadContextAsync)).Should().Be(0);
    }

    [Fact]
    public void TheSimulation_RefusesTheRowWithoutAName_AndImportWaitsForItAndForItsQuestion()
    {
        SignIn(AppLanguage.French, Migrator);
        var page = Read();

        page.Find("[data-action='Migration.Import']").HasAttribute("disabled").Should().BeTrue("no simulation yet");

        page.Find("[data-action='Migration.Simulate']").Click();
        page.WaitForAssertion(() => page.FindAll("[data-grid=report] tbody tr").Should().NotBeEmpty());
        page.Find("[data-grid=report] tbody tr").GetAttribute("data-row").Should().Be("3", "the header is row 1, the patient without a name row 3");
        _store.CallsTo(nameof(IDataMigrationStore.StartBatchAsync)).Should().Be(0, "a simulation writes nothing");

        page.WaitForAssertion(() => page.Find("[data-action='Migration.Import']").HasAttribute("disabled")
                                         .Should().BeFalse(page.Find("[data-state=status]").TextContent));
        page.Find("[data-action='Migration.Import']").Click();
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("À créer : 2").And.Contain("En erreur (non importés) : 1");
        page.Find("[data-action='Action.Yes']").Click();

        page.WaitForAssertion(() => _store.CallsTo(nameof(IDataMigrationStore.CompleteBatchAsync)).Should().Be(1));
        _store.Calls.Single(c => c.Method == nameof(IDataMigrationStore.StartBatchAsync)).Arguments[1].Should().Be("LABOWIN");
        page.WaitForAssertion(() => page.Find("[data-state=status]").TextContent.Should().Contain("2"));
    }

    [Fact]
    public void AProfile_IsKeptInTheServersFolder_WithoutAPassword()
    {
        SignIn(AppLanguage.French, Migrator);
        var page = Read();

        page.Find("[data-action='Migration.SaveProfile']").Click();
        page.Find("[data-modal=profile-name] [data-field=text]").Input("Labowin 2019");
        page.Find("[data-modal=profile-name] [data-action='Action.Ok']").Click();

        var store = Services.GetRequiredService<MigrationProfileStore>();
        store.Names().Should().Equal("Labowin 2019");
        store.Load("Labowin 2019")!.ProtectedPassword.Should().BeNull();
        page.Find("[data-state=status]").TextContent.Should().Contain("Labowin 2019");
        page.Find("[data-field=profile] option[selected]").TextContent.Should().Be("Labowin 2019");
    }

    [Fact]
    public void WithoutTheMigrationRight_NothingCanBeSentSimulatedOrImported()
    {
        SignIn(AppLanguage.French, Permissions.Administration.ManageUsers);

        var page = Render<DataMigration>();

        page.Find("[data-action='Migration.Browse'] input").HasAttribute("disabled").Should().BeTrue();
        foreach (var action in new[] { "Migration.SaveProfile", "Migration.Simulate", "Migration.Import", "Migration.UndoLast" })
            page.Find($"[data-action='{action}']").HasAttribute("disabled").Should().BeTrue(action);
    }

    [Fact]
    public void InArabic_TheTabsAreArabic_AndThePageRunsRightToLeft()
    {
        SignIn(AppLanguage.Arabic, Migrator);

        var page = Render<DataMigration>();

        page.Find("[data-tab=Preview]").TextContent.Trim().Should().NotBe("Aperçu").And.NotContain("Migration.");
        page.Find("[data-state=database-notice]").TextContent.Should().Contain("Windows").And.NotContain("Web.");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
