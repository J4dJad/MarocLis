using Bunit;
using FluentAssertions;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Samples;
using MarocLIS.Web.Components.Screens.Samples;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>« Sérothèque » in the browser: the tubes and their places, the scanner, the shelving, the discard and the purge, by right.</summary>
public sealed class WebSerumBankTests : WebBenchContext
{
    private readonly Sample _tube = new("T-0042", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private readonly List<StoredSampleRow> _rows;

    public WebSerumBankTests()
    {
        _tube.Collect(TestClock.Now.AddDays(-2), Guid.NewGuid(), 4.5m, null);
        _tube.Receive(TestClock.Now.AddDays(-2), Guid.NewGuid(), SampleDefect.None, false, 21m, null);

        _rows =
        [
            Row(_tube.Id, "T-0042", "BERRADA Nawal", null, null, null, SampleStatus.Received),
            Row(Guid.NewGuid(), "T-0007", "SQALLI Hamza", "Frigo S1", "R1-B03", -3, SampleStatus.Stored),
            Row(Guid.NewGuid(), "T-0008", "ALAOUI Fatima Zahra", "Frigo S1", "R1-B04", 30, SampleStatus.Stored)
        ];

        SampleQueries.On(nameof(ISampleQueries.GetStoredSamplesAsync),
                         args => (IReadOnlyList<StoredSampleRow>)[.. _rows.Where(r => args[2] is not true || r.IsPastRetention)
                                                                         .Where(r => args[1] is not string term || r.Barcode.Contains(term, StringComparison.OrdinalIgnoreCase))])
                     .On(nameof(ISampleQueries.GetStorageLocationsAsync), _ => (IReadOnlyList<string>)["Frigo S1", "Congélateur C1"]);
        SampleStore.On(nameof(Application.Abstractions.IRepository<Sample>.GetByIdWithDetailsAsync), args => (Guid)args[0]! == _tube.Id ? _tube : null);
    }

    private StoredSampleRow Row(Guid id, string barcode, string patient, string? location, string? position, int? days, SampleStatus status) =>
        new(id, barcode, "2610-000051", patient, "Sérum", status, TestClock.Now.AddDays(-2), TestClock.Now.AddDays(-2),
            location, position, days is { } d ? TestClock.Today.AddDays(d) : null, days, 4.5m, 0, false, false);

    [Fact]
    public void TheTubes_AreListedWithTheirPlace_ThePastRetentionInRed_AndTheUnshelvedInOrange()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive, Permissions.Samples.Reject);
        var page = Render<SampleStorage>();

        page.FindAll("[data-grid=serumbank] tbody tr").Should().HaveCount(3);
        page.Find("[data-row=T-0007]").ClassList.Should().Contain("is-tone-critical-fill");
        page.Find("[data-row=T-0042]").ClassList.Should().Contain("is-tone-abnormal-fill");
        page.Find("[data-row=T-0007]").TextContent.Should().Contain("Frigo S1 / R1-B03");
        page.Find("[data-state=count]").TextContent.Should().Be("3 · 1 conservation dépassée · 1 non rangé");
        page.Find("[data-filter=pastretention]").TextContent.Should().Be("Conservation dépassée · 1");

        page.Find("[data-row=T-0008]").Click();
        page.Find("[data-card=tube]").TextContent.Should().Contain("T-0008").And.Contain("ALAOUI Fatima Zahra");
    }

    [Fact]
    public void ATubeScanned_IsLookedUpAndHighlighted_AndOneFoundNowhereIsSaid()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive);
        var page = Render<SampleStorage>();

        page.Find("[data-field=storage-search]").Input("t-0008");
        page.Find("[data-field=storage-search]").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.Find("tr.is-selected").GetAttribute("data-row").Should().Be("T-0008");
        SampleQueries.Calls.Last(c => c.Method == nameof(ISampleQueries.GetStoredSamplesAsync)).Arguments[1].Should().Be("t-0008");

        page.Find("[data-field=storage-search]").Input("T-9999");
        page.Find("[data-field=storage-search]").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        page.Find("[data-state=status]").TextContent.Should().Contain("Aucun tube ne correspond à « T-9999 »");
    }

    [Fact]
    public void ATube_IsShelvedInAPlaceOfTheList_AtItsPosition_WithItsRetention()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive);
        var page = Render<SampleStorage>();

        page.Find("[data-row=T-0042]").Click();
        page.Find(".action-bar [data-action='Sample.Shelve']").Click();

        var dialog = page.Find("[data-modal=shelve]");
        dialog.TextContent.Should().Contain("Ranger dans la sérothèque — T-0042").And.Contain("BERRADA Nawal");
        page.FindAll("[data-field=storage-location] option").Select(o => o.TextContent).Should().Equal("— Choisir un lieu —", "Frigo S1", "Congélateur C1");

        page.Find("[data-field=storage-location]").Change("Congélateur C1");
        page.Find("[data-field=storage-position]").Input("R2-A07");
        page.Find("[data-field=storage-has-keep-until]").Change(true);
        page.Find("[data-modal=shelve] [data-action='Action.Save']").Click();

        page.FindAll("[data-modal=shelve]").Should().BeEmpty();
        _tube.StorageLocation.Should().Be("Congélateur C1");
        _tube.StoragePosition.Should().Be("R2-A07");
        _tube.KeepUntil.Should().Be(TestClock.Today.AddDays(7));
        page.Find("[data-state=status]").TextContent.Should().Be("T-0042 — Congélateur C1");
    }

    [Fact]
    public void AShelvingWithNoPlace_OrADatePast_IsRefusedNextToItsBox()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive);
        var page = Render<SampleStorage>();

        page.Find("[data-row=T-0042]").Click();
        page.Find(".action-bar [data-action='Sample.Shelve']").Click();
        page.Find("[data-modal=shelve] [data-action='Action.Save']").Click();
        page.Find("[data-refusal=storage-location]").TextContent.Should().Contain("L'emplacement de rangement est obligatoire");

        page.Find("[data-field=storage-location]").Change("Frigo S1");
        page.Find("[data-field=storage-has-keep-until]").Change(true);
        page.Find("[data-field=storage-keep-until]").Change(TestClock.Today.AddDays(-1).ToString("yyyy-MM-dd"));
        page.Find("[data-modal=shelve] [data-action='Action.Save']").Click();
        page.Find("[data-refusal=storage-keep-until]").TextContent.Should().Contain("Conserver jusqu'au");
        _tube.StorageLocation.Should().BeNull();
    }

    [Fact]
    public void AnAliquotWithAnUnreadableVolume_IsRefusedNextToItsBox()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive);
        var page = Render<SampleStorage>();

        page.Find("[data-row=T-0042]").Click();
        page.Find(".action-bar [data-action='Sample.CreateAliquot']").Click();
        page.Find("[data-modal=aliquot]").TextContent.Should().Contain("4.5 mL");
        page.Find("[data-field=aliquot-volume]").Input("abc");
        page.Find("[data-modal=aliquot] [data-action='Action.Save']").Click();

        page.Find("[data-refusal=aliquot-volume]").TextContent.Should().Contain("Volume de l'aliquote invalide");
    }

    [Fact]
    public void Discarding_IsConfirmedThenItsReasonAsked()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Reject);
        var page = Render<SampleStorage>();

        page.Find("[data-row=T-0042]").Click();
        page.Find(".action-bar [data-action='Sample.Discard']").Click();
        page.Find("[data-modal=confirm]").TextContent.Should().Contain("Éliminer le tube T-0042 (BERRADA Nawal)");
        page.Find("[data-action='Action.Yes']").Click();
        page.Find("[data-modal=reason]").TextContent.Should().Contain("Motif de l'élimination du tube T-0042");
        page.Find("[data-field=reason]").Input("Tube hémolysé, analyses terminées");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        _tube.Status.Should().Be(SampleStatus.Discarded);
        page.Find("[data-state=status]").TextContent.Should().Contain("T-0042 — ");
    }

    [Fact]
    public void ThePurge_IsBehindMoreActions_AndAsksHowManyTubes()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Reject);
        var page = Render<SampleStorage>();

        page.Find(".action-bar .more-menu [data-action='Sample.PurgeExpired']").Click();
        page.Find("[data-modal=confirm]").TextContent.Should().Contain("Éliminer les 1 tubes");
    }

    [Fact]
    public void WithoutTheRightToReject_DiscardingAndThePurgeAreNotOffered_WithoutTheRightToReceive_ShelvingIsNot()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Receive);
        var receiver = Render<SampleStorage>();
        receiver.FindAll("[data-action='Sample.Discard']").Should().BeEmpty();
        receiver.FindAll("[data-action='Sample.PurgeExpired']").Should().BeEmpty();
        receiver.FindAll("[data-action='Sample.Shelve']").Should().ContainSingle();
    }

    [Fact]
    public void InArabic_TheScreenSpeaksArabic()
    {
        SignIn(AppLanguage.Arabic, Permissions.Samples.View, Permissions.Samples.Receive, Permissions.Samples.Reject);
        var page = Render<SampleStorage>();

        page.Find("[data-action='Sample.Shelve']").TextContent.Should().Contain("الترتيب في بنك الأمصال");
        page.Find("[data-row=T-0042]").TextContent.Should().Contain("غير مرتب");
        page.Markup.Should().NotContain("[[", "every text has its Arabic translation");
    }
}
