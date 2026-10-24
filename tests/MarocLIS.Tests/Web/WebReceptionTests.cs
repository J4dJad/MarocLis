using Bunit;
using FluentAssertions;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Samples;
using MarocLIS.Web.Components.Screens.Samples;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Réception » in the browser: the scanner in the scan box, the tube's band with its defects
/// ticked by SampleReceptionRules, and the decision offered by right.
/// </summary>
public sealed class WebReceptionTests : WebBenchContext
{
    private readonly SampleReceptionCandidate _tube = new()
    {
        SampleId = Guid.NewGuid(),
        LabOrderId = Guid.NewGuid(),
        Barcode = "T-2610-0042",
        AccessionNumber = "2610-000042",
        PatientName = "FASSI FIHRI Leila",
        SpecimenTypeName = new MultilingualText("Sang total EDTA"),
        Status = SampleStatus.Collected,
        ExistingDefects = SampleDefect.Haemolysed,
        AnalysisCodes = ["NFS"]
    };

    public WebReceptionTests()
    {
        SampleQueries.On(nameof(ISampleQueries.GetReceptionCandidateAsync), args => (string)args[0]! == _tube.Barcode ? _tube : null)
                     .On(nameof(ISampleQueries.GetAwaitingReceptionAsync), _ => (IReadOnlyList<SampleReceptionCandidate>)[_tube]);
    }

    [Fact]
    public void AScannedTube_EndingWithEnter_IsShown_ItsUpstreamDefectTicked_AndTheConsultationRecorded()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive, Permissions.Samples.Reject);
        var page = Render<SampleReception>();

        var scan = page.Find("[data-field=scan]");
        scan.Input("T-2610-0042");
        scan.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.Find("[data-state=candidate]").GetAttribute("data-tube").Should().Be("T-2610-0042");
        page.Find("[data-defect=Haemolysed]").GetAttribute("aria-pressed").Should().Be("true");
        DataAudit.Entries.Should().Contain(e => e.Kind == DataAccessKind.ViewOrder && e.OrderId == _tube.LabOrderId
                                                && e.Context == "SampleReception");
    }

    [Fact]
    public void AnUnknownBarcode_IsSaid_AndNothingIsShown()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive);
        var page = Render<SampleReception>();

        var scan = page.Find("[data-field=scan]");
        scan.Input("T-INCONNU");
        scan.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.FindAll("[data-state=candidate]").Should().BeEmpty();
        page.Find("[data-state=status]").TextContent.Should().Contain("T-INCONNU");
    }

    [Fact]
    public void WithoutTheRightToReject_RejectIsOff()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive);
        var page = Render<SampleReception>();

        var scan = page.Find("[data-field=scan]");
        scan.Input("T-2610-0042");
        scan.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.Find("[data-action='Action.Reject']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void TheDecision_IsBesideTheTube_TheRefusalWithItsReason()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive, Permissions.Samples.Reject);
        var page = Render<SampleReception>();

        var scan = page.Find("[data-field=scan]");
        scan.Input("T-2610-0042");
        scan.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        var decision = page.Find("[data-state=decision]");
        var groups = decision.QuerySelectorAll(".decision-group");
        groups.Should().HaveCount(2);
        groups[0].QuerySelector("[data-field=storage]").Should().NotBeNull();
        groups[0].QuerySelector("[data-action='Action.Accept']").Should().NotBeNull();
        groups[0].QuerySelector("[data-action='Action.AcceptWithReservation']").Should().NotBeNull();
        groups[1].QuerySelector("[data-field=reject-reason]").Should().NotBeNull();
        groups[1].QuerySelector("[data-action='Action.Reject']")!.ClassList.Should().Contain("btn-danger");
    }

    [Fact]
    public void TheTubesAwaitingReception_AreListed()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive);

        Render<SampleReception>().FindAll("[data-grid=awaiting] tbody tr").Should().ContainSingle();
    }
}
