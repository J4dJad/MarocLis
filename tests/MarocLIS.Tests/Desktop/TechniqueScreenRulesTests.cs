using FluentAssertions;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Sérothèque » and « Sous-traitance », moved out of the workstation's views and
/// dialogs, now shared with the web.
/// </summary>
public sealed class TechniqueScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static Func<string, bool> Rights(params string[] rights) => rights.Contains;

    private static StoredSampleRow Tube(string barcode = "T-0042", string? location = "Frigo S1", string? position = "R1-B03",
                                        int? days = 20, SampleStatus status = SampleStatus.Stored, bool defects = false,
                                        bool reservation = false, decimal? volume = 4.5m, int aliquots = 0) =>
        new(Guid.NewGuid(), barcode, "2610-000051", "BERRADA Nawal", "Sérum", status,
            Now.AddDays(-2), new DateTimeOffset(2026, 10, 16, 8, 15, 0, TimeSpan.Zero), location, position,
            days is { } d ? Today.AddDays(d) : null, days, volume, aliquots, defects, reservation);

    // ---- Sérothèque --------------------------------------------------------------------------------

    [Fact]
    public void A_tube_reads_past_retention_first_then_unshelved_then_due_within_a_week()
    {
        SampleStorageScreenRules.Ink(Tube(days: -1)).Should().Be(StorageInk.PastRetention);
        SampleStorageScreenRules.Ink(Tube(location: null, status: SampleStatus.Received, days: -3))
            .Should().Be(StorageInk.PastRetention, "purging is what the screen exists for");
        SampleStorageScreenRules.Ink(Tube(location: null, status: SampleStatus.Received)).Should().Be(StorageInk.Unshelved);
        SampleStorageScreenRules.Ink(Tube(days: 7)).Should().Be(StorageInk.Soon);
        SampleStorageScreenRules.Ink(Tube(days: 8)).Should().Be(StorageInk.Plain);
        SampleStorageScreenRules.Ink(Tube(days: null)).Should().Be(StorageInk.Plain);
    }

    [Fact]
    public void The_chips_count_the_bank_and_the_unshelved_figure_is_not_given_among_the_past_retention_tubes()
    {
        IReadOnlyList<StoredSampleRow> rows =
        [
            Tube(days: -2), Tube(days: -5), Tube(location: null, status: SampleStatus.Received), Tube()
        ];

        SampleStorageScreenRules.Shown(rows, StorageFilter.Unshelved).Should().ContainSingle();
        SampleStorageScreenRules.Shown(rows, StorageFilter.PastRetention).Should().HaveCount(4, "the server already filtered them");
        SampleStorageScreenRules.AsksServer(StorageFilter.PastRetention).Should().BeTrue();
        SampleStorageScreenRules.AsksServer(StorageFilter.Unshelved).Should().BeFalse();

        SampleStorageScreenRules.Chips(rows, StorageFilter.All, Text).Select(c => c.Caption).Should().Equal(
            "Sample.FilterAll", "Sample.PastRetention · 2", "Sample.NotShelved · 1");
        SampleStorageScreenRules.Chips(rows, StorageFilter.PastRetention, Text)[2].Caption.Should().Be("Sample.NotShelved");
        SampleStorageScreenRules.Count(rows, Text).Should().Be("4 · 2 sample.pastretention · 1 sample.notshelved");
        SampleStorageScreenRules.Search("  T-0042 ").Should().Be("T-0042");
        SampleStorageScreenRules.Search("   ").Should().BeNull();
    }

    [Fact]
    public void A_tube_is_written_with_its_place_and_position_its_signs_and_its_card()
    {
        var tube = Tube(defects: true, reservation: true, aliquots: 2);
        SampleStorageScreenRules.Place(tube, Text).Should().Be("Frigo S1 / R1-B03");
        SampleStorageScreenRules.Place(Tube(position: null), Text).Should().Be("Frigo S1");
        SampleStorageScreenRules.Place(Tube(location: null), Text).Should().Be("Sample.NotShelved");
        SampleStorageScreenRules.Flags(tube).Should().Be("! R");
        SampleStorageScreenRules.Flags(Tube(location: null, status: SampleStatus.Received)).Should().Be("?");
        SampleStorageScreenRules.FlagsMeaning(tube, Text).Should().Be("! Sample.HasDefects · R Sample.WithReservation");
        SampleStorageScreenRules.KeepUntil(tube).Should().Be("07/11/26");
        SampleStorageScreenRules.KeepUntil(Tube(days: null)).Should().Be("—");
        SampleStorageScreenRules.KeepUntil(Tube(days: null), full: true).Should().BeEmpty();
        SampleStorageScreenRules.Volume(tube).Should().Be("4.5");
        SampleStorageScreenRules.Aliquots(tube).Should().Be("2");
        SampleStorageScreenRules.Aliquots(Tube()).Should().BeEmpty();

        SampleStorageScreenRules.Pills(Tube(days: -1, defects: true), Text).Select(p => (p.Text, p.Tone)).Should().Equal(
            ("! Sample.PastRetention", "Critical"), ("! Sample.HasDefects", "Warning"));
        SampleStorageScreenRules.Facts(tube, Text).Select(f => f.Label).Should().Equal(
            "Order.AccessionNumber", "Sample.ReceivedAt", "Sample.Storage", "Sample.KeepUntil", "Sample.DaysLeft",
            "Sample.Volume", "Sample.Aliquots");
        SampleStorageScreenRules.AliquotSubtitle(tube, Text).Should().EndWith("Sample.VolumeValue(4.5)", "the unit is the language's");
    }

    [Fact]
    public void Shelving_and_aliquoting_take_the_right_to_receive_discarding_and_the_purge_the_right_to_reject()
    {
        IReadOnlyList<StoredSampleRow> rows = [Tube(days: -1), Tube()];

        var receiver = SampleStorageScreenRules.Actions(rows[1], rows, Rights(Permissions.Samples.Receive));
        receiver.Should().Be(new StorageActions(Shelve: true, Aliquot: true, Discard: false, Purge: false));

        var rejecter = SampleStorageScreenRules.Actions(rows[1], rows, Rights(Permissions.Samples.Reject));
        rejecter.Should().Be(new StorageActions(Shelve: false, Aliquot: false, Discard: true, Purge: true));

        SampleStorageScreenRules.Actions(null, [Tube()], Rights(Permissions.Samples.Receive, Permissions.Samples.Reject))
            .Should().Be(StorageActions.None, "nothing past retention, no tube highlighted");
        SampleStorageScreenRules.Actions(Tube(status: SampleStatus.Rejected), rows, Rights(Permissions.Samples.Receive))
            .Aliquot.Should().BeFalse("a refused tube is not split");
        SampleStorageScreenRules.PurgeCandidates(rows).Should().ContainSingle();
    }

    [Fact]
    public void The_purge_says_how_many_tubes_went_and_names_the_first_five_that_did_not()
    {
        SampleStorageScreenRules.PurgeOutcome(3, [], Text).Should().Be(("Sample.PurgeDone(3)", false));
        SampleStorageScreenRules.PurgeOutcome(1, ["A", "B", "C", "D", "E", "F"], Text)
            .Should().Be(("Sample.PurgePartial(1,A, B, C, D, E)", true));
        SampleStorageScreenRules.Discarded(Tube(), Text).Should().Be("T-0042 — SampleStatus.Discarded");
        SampleStorageScreenRules.Shelved(Tube(), "Frigo S2").Should().Be("T-0042 — Frigo S2");
    }

    [Fact]
    public void The_places_list_keeps_a_place_no_longer_listed_marked_and_selected()
    {
        var choices = SampleStorageScreenRules.PlaceChoices(["Frigo S1", "Congélateur C1"], "Ancien frigo", "—", p => $"{p} (hors liste)");
        choices.Select(c => c.Display).Should().Equal("—", "Frigo S1", "Congélateur C1", "Ancien frigo (hors liste)");
        SampleStorageScreenRules.SelectedIndex(choices, "ancien FRIGO").Should().Be(3);

        var listed = SampleStorageScreenRules.PlaceChoices(["Frigo S1"], " frigo s1 ", "—", p => p);
        listed.Should().HaveCount(2, "a place listed under another case is the same place");
        SampleStorageScreenRules.SelectedIndex(listed, null).Should().Be(0);
        SampleStorageScreenRules.Chosen("").Should().BeNull();
        SampleStorageScreenRules.Chosen("Frigo S1").Should().Be("Frigo S1");
    }

    [Fact]
    public void Shelving_needs_a_place_and_refuses_a_date_already_over_unless_the_tube_carries_it()
    {
        var tube = Tube(days: -3);
        var form = SampleStorageScreenRules.ShelveFormFor(Tube(location: null, position: null, days: null), Today);
        form.HasKeepUntil.Should().BeFalse();
        form.KeepUntil.Should().Be(Today.AddDays(7), "a week is offered when the box is ticked");

        form.Location = string.Empty;
        SampleStorageScreenRules.CheckShelve(form, Tube(location: null), placeCount: 0, Today).Refusal
            .Should().Be(new Application.Input.FieldRefusal(SampleStorageScreenRules.LocationField, "Sample.StorageListEmpty"),
                         "with no place in the list, the operator is told where places are created");
        SampleStorageScreenRules.CheckShelve(form, tube, placeCount: 2, Today).Refusal!.Key.Should().Be("Sample_StorageLocationRequired");

        form.Location = "Frigo S1";
        form.HasKeepUntil = true;
        form.KeepUntil = Today.AddDays(-1);
        SampleStorageScreenRules.CheckShelve(form, Tube(), 2, Today).Refusal
            .Should().Be(new Application.Input.FieldRefusal(SampleStorageScreenRules.KeepUntilField, "Sample_KeepUntilInPast"));

        form.KeepUntil = Today.AddDays(-3);
        form.Position = "  R2-A07 ";
        var (refusal, request) = SampleStorageScreenRules.CheckShelve(form, tube, 2, Today);
        refusal.Should().BeNull("the date the tube already carries may stay");
        request.Should().Be(new ShelveRequest("Frigo S1", "R2-A07", Today.AddDays(-3)));

        form.HasKeepUntil = false;
        SampleStorageScreenRules.CheckShelve(form, tube, 2, Today).Request!.KeepUntil.Should().BeNull();
    }

    [Fact]
    public void An_aliquot_volume_typed_and_unreadable_or_not_positive_is_refused_an_empty_one_is_not()
    {
        SampleStorageScreenRules.CheckAliquot(new AliquotForm { Volume = "abc" }).Refusal!.Field.Should().Be(SampleStorageScreenRules.VolumeField);
        SampleStorageScreenRules.CheckAliquot(new AliquotForm { Volume = "0" }).Refusal.Should().NotBeNull();
        SampleStorageScreenRules.CheckAliquot(new AliquotForm { Volume = "1000000" }).Refusal.Should().NotBeNull();

        var (refusal, request) = SampleStorageScreenRules.CheckAliquot(new AliquotForm { Volume = "1,5", Purpose = " Envoi ", Location = "" });
        refusal.Should().BeNull();
        request.Should().Be(new AliquotRequest(1.5m, "Envoi", null), "no place keeps the parent tube's");
        SampleStorageScreenRules.CheckAliquot(new AliquotForm()).Request!.VolumeMl.Should().BeNull();

        SampleStorageScreenRules.FieldOf("Sample_AliquotVolumeExceedsTube").Should().Be(SampleStorageScreenRules.VolumeField);
        SampleStorageScreenRules.FieldOf("Sample_StorageLocationNotInList").Should().Be(SampleStorageScreenRules.LocationField);
        SampleStorageScreenRules.FieldOf("Sample_NotFound").Should().BeNull();
    }

    // ---- Sous-traitance ----------------------------------------------------------------------------

    private static SubcontractRow Act(string accession = "2610-000051", DateTimeOffset? sent = null, DateTimeOffset? due = null,
                                      OrderPriority priority = OrderPriority.Routine, string? barcode = "T-0042",
                                      string? slip = null, string? reference = null, Guid? order = null) =>
        new(Guid.NewGuid(), order ?? Guid.NewGuid(), accession, "BERRADA Nawal", Gender.Female, new DateOnly(1980, 3, 2), priority,
            "VITD", new MultilingualText("25-OH vitamine D", "فيتامين د", "Vitamin D"), barcode, new MultilingualText("Tube sec"),
            Now.AddHours(-3), Now.AddHours(-2), Guid.NewGuid(), "PART", new MultilingualText("Laboratoire partenaire"),
            sent, sent is null ? null : slip ?? "ST/2026/00001", reference, due);

    [Fact]
    public void An_act_sent_is_late_once_its_return_is_past_and_reads_in_the_alarms_colours()
    {
        var late = Act(sent: Now.AddDays(-5), due: Now.AddHours(-1));
        var awaiting = Act(sent: Now.AddDays(-1), due: Now.AddDays(2));
        var urgent = Act(priority: OrderPriority.Urgent, due: Now.AddDays(-1));

        SubcontractingScreenRules.IsLate(late, Now).Should().BeTrue();
        SubcontractingScreenRules.IsLate(urgent, Now).Should().BeFalse("an act not sent cannot be late coming back");
        SubcontractingScreenRules.Ink(late, Now).Should().Be(SubcontractInk.Late);
        SubcontractingScreenRules.Ink(awaiting, Now).Should().Be(SubcontractInk.Awaiting);
        SubcontractingScreenRules.Ink(urgent, Now).Should().Be(SubcontractInk.UrgentToSend);
        SubcontractingScreenRules.Ink(Act(), Now).Should().Be(SubcontractInk.ToSend);

        (SubcontractingScreenRules.StageGlyph(late, Now), SubcontractingScreenRules.StageText(late, Now, Text), SubcontractingScreenRules.StageTone(late, Now))
            .Should().Be(("!", "Subcontract.Late", "Critical"));
        (SubcontractingScreenRules.StageGlyph(Act(), Now), SubcontractingScreenRules.StageText(Act(), Now, Text))
            .Should().Be(("→", "Subcontract.StageToSend"));
        (SubcontractingScreenRules.StageGlyph(awaiting, Now), SubcontractingScreenRules.StageTone(awaiting, Now))
            .Should().Be(("◐", "Waiting"));
        SubcontractingScreenRules.Due(Act(due: Now)).Should().BeEmpty("an act to send has no return expected yet");
    }

    [Fact]
    public void The_chips_and_the_search_find_an_act_by_file_patient_tube_slip_or_reference()
    {
        IReadOnlyList<SubcontractRow> rows =
        [
            Act("2610-000001"), Act("2610-000002", sent: Now.AddDays(-5), due: Now.AddHours(-1), reference: "PART-4512"),
            Act("2610-000003", sent: Now.AddDays(-1), due: Now.AddDays(2), slip: "ST/2026/00002", barcode: "T-0099")
        ];

        SubcontractingScreenRules.Shown(rows, SubcontractFilter.ToSend, null, Now).Should().ContainSingle();
        SubcontractingScreenRules.Shown(rows, SubcontractFilter.AwaitingReturn, null, Now).Should().HaveCount(2);
        SubcontractingScreenRules.Shown(rows, SubcontractFilter.Late, null, Now).Single().AccessionNumber.Should().Be("2610-000002");
        SubcontractingScreenRules.Shown(rows, SubcontractFilter.All, "part-4512", Now).Should().ContainSingle();
        SubcontractingScreenRules.Shown(rows, SubcontractFilter.All, " st/2026/00002", Now).Should().ContainSingle();
        SubcontractingScreenRules.Shown(rows, SubcontractFilter.All, "t-0099", Now).Should().ContainSingle();
        SubcontractingScreenRules.Shown(rows, SubcontractFilter.All, "berrada", Now).Should().HaveCount(3);

        SubcontractingScreenRules.Count(rows, Now, Text).Should().Be("Subcontract.Count(1,2,1)");
        SubcontractingScreenRules.Chips(rows, Now, Text).Select(c => c.Caption).Should().Equal(
            "Subcontract.StageAll · 3", "Subcontract.StageToSend · 1", "Subcontract.StageAwaitingReturn · 2", "Subcontract.Late · 1");
    }

    [Fact]
    public void An_action_applies_to_the_acts_ticked_or_the_one_highlighted_and_by_right()
    {
        var toSend = Act();
        var sent = Act(sent: Now.AddDays(-1), due: Now.AddDays(1));

        SubcontractingScreenRules.Targets([], toSend).Should().Equal(toSend);
        SubcontractingScreenRules.Targets([sent], toSend).Should().Equal(sent);
        SubcontractingScreenRules.Targets([], null).Should().BeEmpty();

        SubcontractingScreenRules.Actions([toSend], Rights(Permissions.Samples.Subcontract))
            .Should().Be(new SubcontractActions(Send: true, Reprint: false, Reference: false, EnterResults: false));
        SubcontractingScreenRules.Actions([toSend, sent], Rights(Permissions.Samples.Subcontract, Permissions.Results.Enter))
            .Should().Be(new SubcontractActions(true, true, true, true));
        SubcontractingScreenRules.Actions([toSend, sent], Rights(Permissions.Samples.View))
            .Should().Be(new SubcontractActions(Send: false, Reprint: true, Reference: false, EnterResults: false),
                         "a slip is reprinted by whoever sees the list");
        SubcontractingScreenRules.Actions([], Rights(Permissions.Samples.Subcontract)).Should().Be(SubcontractActions.None);

        SubcontractingScreenRules.ToSend([toSend, sent]).Should().Equal(toSend);
        SubcontractingScreenRules.Slips([sent, sent, toSend]).Should().Equal("ST/2026/00001");
        SubcontractingScreenRules.AwaitingReturn([toSend, sent]).Should().Be(sent);
    }

    [Fact]
    public void The_dispatch_dialog_and_the_slip_say_what_leaves_and_where()
    {
        var order = Guid.NewGuid();
        IReadOnlyList<SubcontractRow> rows = [Act(order: order), Act(order: order), Act(priority: OrderPriority.Urgent)];
        SubcontractingScreenRules.SendSummary(rows, Text).Should().Be("Subcontract.SendSummary(3,2)");
        SubcontractingScreenRules.DispatchLine(rows[0], Text).Should().Be("2610-000051  ·  VITD  ·  T-0042  →  PART");

        var partners = SubcontractingScreenRules.PartnerChoices([new SubcontractPartner(order, "BIOLAB", new MultilingualText("Biolab Rabat"))],
                                                                AppLanguage.French, Text);
        partners.Select(p => (p.Id, p.Display)).Should().Equal((null, "Subcontract.PartnerFromCatalogue"), (order, "BIOLAB  Biolab Rabat"));
        SubcontractingScreenRules.SentMessage([new SubcontractDispatch("ST/1", order, 2), new SubcontractDispatch("ST/2", order, 1)], Text)
            .Should().Be("Subcontract.Sent(ST/1, ST/2,2)");

        var slip = new SubcontractSlip("ST/2026/00001", new SubcontractPartner(order, "BIOLAB", new MultilingualText("Biolab Rabat", "بيولاب الرباط", "Biolab")),
                                       new DateTimeOffset(2026, 10, 17, 15, 40, 0, TimeSpan.Zero), "Karim Tahiri", rows);
        SubcontractingScreenRules.SlipTitle(slip, Text).Should().Be("Subcontract.SlipTitle ST/2026/00001");
        SubcontractingScreenRules.SlipCaption(slip, "PRINCIPAL", AppLanguage.Arabic, Text).Should().Contain("BIOLAB  بيولاب الرباط").And.Contain("Karim Tahiri");
        SubcontractingScreenRules.SlipHeaders(Text).Should().HaveCount(8);
        var lines = SubcontractingScreenRules.SlipRows(slip, AppLanguage.French, Text);
        lines.Should().HaveCount(5, "three analyses, a blank line and the signatures");
        lines[0][2].Should().Be("Gender.Female 02/03/1980");
        lines[2][7].Should().Be("Priority.Urgent");
        lines[0][7].Should().BeEmpty("routine is not written");
        lines[4][0].Should().Be("Subcontract.SignatureCourier");
        SubcontractingScreenRules.SlipFileName("ST/2026/00001").Should().Be("bordereau-ST-2026-00001.pdf");
    }

    [Fact]
    public void The_partners_reference_is_trimmed_emptied_to_none_and_refused_beyond_its_column()
    {
        SubcontractingScreenRules.CheckReference("  PART-4512 ").Should().Be(((Application.Input.FieldRefusal?)null, (string?)"PART-4512"));
        SubcontractingScreenRules.CheckReference("   ").Value.Should().BeNull();
        SubcontractingScreenRules.CheckReference(new string('x', 61)).Refusal!.Key.Should().Be("Subcontract_ReferenceTooLong");
        SubcontractingScreenRules.ReferencePrompt(Act(), AppLanguage.French, Text)
            .Should().Be("Subcontract.PartnerReferencePrompt(2610-000051,Laboratoire partenaire)");
    }
}
