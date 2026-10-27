using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Instruments;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.Instruments;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;
using Rules = MarocLIS.Tests.Desktop.InstrumentInterfaceScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Interfaces automates » in the browser: the links with their state, read « non piloté ici »
/// since the server never drives an analyser; each action kept by the right the workstation asks;
/// a held transmission replayed after its question; the log purged with its reason written to the
/// security journal, as on the workstation — all through the workstation's InstrumentInterfaceService.
/// </summary>
public sealed class WebInstrumentInterfaceTests : WebReferenceContext
{
    private readonly Fake<IInstrumentInterfaceQueries> _queries = Fake<IInstrumentInterfaceQueries>.Create();
    private readonly Fake<IInstrumentMessageLogStore> _log = Fake<IInstrumentMessageLogStore>.Create();
    private readonly Fake<IInstrumentResultReplayer> _replayer = Fake<IInstrumentResultReplayer>.Create();
    private readonly Auditor _auditor = new();

    public WebInstrumentInterfaceTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_log.Object);
        Services.AddSingleton(_replayer.Object);
        Services.AddSingleton(Fake<IInstrumentMessagePreviewer>.Create().Object);
        Services.AddSingleton(Fake<IRepository<InstrumentInterface>>.Create().Object);
        Services.AddSingleton<ISecurityAuditor>(_auditor);
        Services.AddSingleton(new InstrumentHostOptions { MessageLogRetentionDays = 180 });
        Services.AddScoped<InstrumentInterfaceService>();
    }

    private static readonly string[] Viewer = [Permissions.Instruments.View];

    private static readonly string[] Administrator =
    [
        Permissions.Instruments.View, Permissions.Instruments.Configure, Permissions.Instruments.StartStop,
        Permissions.Instruments.ReplayMessages, Permissions.Results.View, Permissions.Administration.RunMaintenance
    ];

    private void Links(params InterfaceRow[] rows) => _queries.On(nameof(IInstrumentInterfaceQueries.GetInterfacesAsync), _ => rows);

    private void Messages(params MessageLogRow[] rows) => _queries.On(nameof(IInstrumentInterfaceQueries.GetMessagesAsync), _ => rows);

    [Fact]
    public void TheLinks_AreListed_NotDrivenHere_TheOneInErrorToWatch_AndTheBarSaysWhoDrivesThem()
    {
        SignIn(AppLanguage.French, Administrator);
        Links(Rules.Link(), Rules.Link(error: "COM3 introuvable") with { InstrumentCode = "XN550" });
        _queries.On(nameof(IInstrumentInterfaceQueries.GetMappingsAsync), _ => new[] { Rules.Mapping(), Rules.Mapping("K", catalogueUnit: "mmol/L") });

        var page = Render<InstrumentInterfaces>();

        var rows = page.FindAll("[data-grid=links] tbody tr");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("AU480").And.Contain("non piloté ici");
        page.FindAll("[data-grid=mappings] tbody tr").Should().HaveCount(2);
        page.Find("[data-state=host]").TextContent.Should().Contain("service MarocLIS");
        page.Find("[data-state=status]").TextContent.Should().Contain("service MarocLIS");

        page.Find("[data-chip=to-watch]").Click();
        page.FindAll("[data-grid=links] tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("XN550");

        page.Find("[data-chip=all]").Click();
        page.Find("[data-field=link-search]").Input("xn5");
        page.FindAll("[data-grid=links] tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("XN550");
    }

    [Fact]
    public void WhoMayOnlyLook_SeesTheLinksAndTheirCodes_ButEveryChangeTurnedOff()
    {
        SignIn(AppLanguage.French, Viewer);
        Links(Rules.Link());

        var page = Render<InstrumentInterfaces>();

        page.FindAll("[data-grid=links] tbody tr").Should().ContainSingle();
        foreach (var action in new[] { "Interface.New", "Interface.Edit", "Interface.EditMappings", "Interface.TestFrame", "Interface.PurgeLog", "Interface.Disable" })
            page.Find($"[data-action='{action}']").HasAttribute("disabled").Should().BeTrue(action);
    }

    [Fact]
    public void AHeldTransmission_IsFoundUnderToDealWith_AndReplayedAfterItsQuestion()
    {
        SignIn(AppLanguage.French, Administrator);
        Links(Rules.Link());
        var held = Rules.Message(InstrumentMessageProcessingStatus.Failed, imported: 0);
        Messages(Rules.Message(), held);
        _replayer.On(nameof(IInstrumentResultReplayer.ReplayAsync), _ => Result.Success(2));

        var page = Render<InstrumentInterfaces>();
        page.Find("[data-tab=messages]").Click();
        page.FindAll("[data-grid=messages] tbody tr").Should().HaveCount(2);

        page.Find("[data-chip=to-deal-with]").Click();
        page.FindAll("[data-grid=messages] tbody tr").Should().ContainSingle().Which.Click();

        page.Find("[data-action='Interface.Replay']").Click();
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("Réimporter");
        _replayer.CallsTo(nameof(IInstrumentResultReplayer.ReplayAsync)).Should().Be(0, "nothing is written before the answer");

        page.Find("[data-action='Action.Yes']").Click();

        _replayer.Calls.Should().ContainSingle().Which.Arguments[0].Should().Be(held.Id);
        page.Find("[data-state=status]").TextContent.Should().Contain("2");
    }

    [Fact]
    public void ThePurge_SaysItsScope_AsksItsReason_AndWritesItToTheSecurityJournal()
    {
        SignIn(AppLanguage.French, Administrator);
        Links(Rules.Link());
        _log.On(nameof(IInstrumentMessageLogStore.CensusAsync), _ => new MessageLogCensus(40, 3, new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero), 900));
        _log.On(nameof(IInstrumentMessageLogStore.PurgeAsync), _ => 40);

        var page = Render<InstrumentInterfaces>();
        page.Find("[data-action='Interface.PurgeLog']").Click();

        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("40");
        page.Find("[data-action='Action.Yes']").Click();
        page.Find("[data-modal=reason] [data-field=reason]").Input("Base proche de la limite Express");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        _log.CallsTo(nameof(IInstrumentMessageLogStore.PurgeAsync)).Should().Be(1);
        _auditor.Events.Should().ContainSingle(e => e.Kind == SecurityEventKind.DataPurged)
                .Which.Details.Should().Contain("40 frames").And.Contain("Base proche de la limite Express");
        page.Find("[data-state=status]").TextContent.Should().Contain("40 trames supprimées");
    }

    [Fact]
    public void InArabic_TheTabsAreArabic_AndThePageRunsRightToLeft()
    {
        SignIn(AppLanguage.Arabic, Administrator);
        Links(Rules.Link());

        var page = Render<InstrumentInterfaces>();

        page.Find("[data-tab=mappings]").TextContent.Should().NotContain("Interface.").And.NotContain("[");
        page.Find("[data-state=host]").TextContent.Should().Contain("MarocLIS").And.NotContain("Web.");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
