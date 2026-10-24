using FluentAssertions;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Prélèvements », « Réception » and « Re-prélèvements » moved out of the
/// workstation's views, now shared with the web.
/// </summary>
public sealed class SampleScreenRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(1));

    // ---- Prélèvements ----------------------------------------------------------------------------

    private static CollectionWorklistEntry Tube(Guid order, int drawOrder, bool collected = false,
                                                OrderPriority priority = OrderPriority.Routine, int fasting = 0,
                                                string barcode = "T-1", DateTimeOffset? registered = null,
                                                params string[] analyses) => new()
    {
        SampleId = Guid.NewGuid(),
        LabOrderId = order,
        Barcode = barcode,
        AccessionNumber = "2610-000001",
        PatientName = "BENNANI Salma",
        SpecimenTypeName = new MultilingualText("Sang"),
        Priority = priority,
        RegisteredAt = registered ?? Now,
        DrawOrder = drawOrder,
        CollectedAt = collected ? Now : null,
        RequiredFastingHours = fasting,
        AnalysisCodes = analyses
    };

    [Fact]
    public void Collection_chips_keep_the_urgent_or_fasting_patients_most_urgent_first_then_by_arrival()
    {
        Guid early = Guid.NewGuid(), late = Guid.NewGuid(), urgent = Guid.NewGuid();
        CollectionWorklistEntry[] entries =
        [
            Tube(late, 1, registered: Now.AddMinutes(-5)),
            Tube(early, 1, fasting: 12, registered: Now.AddMinutes(-30)),
            Tube(urgent, 1, priority: OrderPriority.Urgent, registered: Now)
        ];

        CollectionRules.Orders(entries, CollectionFilter.All).Select(g => g.Key).Should().Equal(urgent, early, late);
        CollectionRules.Orders(entries, CollectionFilter.Urgent).Select(g => g.Key).Should().Equal(urgent);
        CollectionRules.Orders(entries, CollectionFilter.Fasting).Select(g => g.Key).Should().Equal(early);
    }

    [Fact]
    public void A_patients_tubes_come_in_draw_order_the_ones_left_ticked_then_all_once_drawn()
    {
        var order = Guid.NewGuid();
        var citrate = Tube(order, 2, collected: true);
        var dry = Tube(order, 1);
        var other = Tube(Guid.NewGuid(), 0);

        var set = CollectionRules.DrawSet([citrate, dry, other], order);

        set.Should().Equal(dry, citrate);
        CollectionRules.ToDraw(set).Should().Be(1);
        CollectionRules.IsTickedByDefault(dry, set).Should().BeTrue();
        CollectionRules.IsTickedByDefault(citrate, set).Should().BeFalse();
        CollectionRules.RemainingToTick(set, new HashSet<Guid>()).Should().Be(1);
        CollectionRules.RemainingToTick(set, new HashSet<Guid> { dry.SampleId }).Should().Be(0);

        var drawn = CollectionRules.DrawSet([citrate], order);
        CollectionRules.IsTickedByDefault(citrate, drawn).Should().BeTrue();
    }

    [Fact]
    public void The_fasting_band_names_its_analyses_and_asks_only_when_nothing_answered_or_declared()
    {
        var order = Guid.NewGuid();
        CollectionWorklistEntry[] set = [Tube(order, 1, fasting: 12, analyses: ["GLY", "CHOL"]), Tube(order, 2, analyses: ["NFS"])];

        CollectionRules.RequiredFasting(set).Should().Be(12);
        CollectionRules.RequiredFasting([]).Should().Be(0);
        CollectionRules.FastingAnalyses(set).Should().Equal("GLY", "CHOL");

        CollectionRules.ShownFasting(FastingAnswer.NotAsked, declared: true).Should().BeTrue();
        CollectionRules.ShownFasting(FastingAnswer.No, declared: true).Should().BeFalse();
        CollectionRules.ShownFasting(FastingAnswer.Unknown, declared: true).Should().BeNull();

        CollectionRules.MustAskFasting(FastingAnswer.NotAsked, null, 12, toDraw: 1).Should().BeTrue();
        CollectionRules.MustAskFasting(FastingAnswer.NotAsked, false, 12, toDraw: 1).Should().BeFalse();
        CollectionRules.MustAskFasting(FastingAnswer.NotAsked, null, 0, toDraw: 1).Should().BeFalse();
        CollectionRules.MustAskFasting(FastingAnswer.NotAsked, null, 12, toDraw: 0).Should().BeFalse();

        CollectionRules.FastingToRecord(FastingAnswer.Yes, null, 12, 1).Should().BeTrue();
        CollectionRules.FastingToRecord(FastingAnswer.NotAsked, false, 12, 1).Should().BeFalse();
        CollectionRules.FastingToRecord(FastingAnswer.Unknown, true, 12, 1).Should().BeNull();
        CollectionRules.FastingToRecord(FastingAnswer.Yes, null, 12, toDraw: 0).Should().BeNull();
    }

    [Fact]
    public void A_typed_volume_that_is_no_number_or_cannot_fit_a_tube_is_refused_an_empty_box_records_none()
    {
        CollectionRules.IsVolumeRefused(null).Should().BeFalse();
        CollectionRules.IsVolumeRefused(string.Empty).Should().BeFalse();
        CollectionRules.IsVolumeRefused("4,5").Should().BeFalse();
        CollectionRules.Volume("4,5").Should().Be(4.5m);
        CollectionRules.IsVolumeRefused("abc").Should().BeTrue();
        CollectionRules.IsVolumeRefused("   ").Should().BeTrue();
        CollectionRules.IsVolumeRefused("0").Should().BeTrue();
        CollectionRules.IsVolumeRefused("5000000").Should().BeTrue();
    }

    [Fact]
    public void Recording_draws_the_ticked_tubes_left_and_refuses_nothing_ticked_or_all_drawn_already()
    {
        var order = Guid.NewGuid();
        var drawn = Tube(order, 1, collected: true);
        var left = Tube(order, 2);
        CollectionWorklistEntry[] set = [drawn, left];

        CollectionRules.Plan(set, [], dispatch: false).Refusal.Should().Be(CollectionRefusal.NothingSelected);
        CollectionRules.Plan(set, [drawn.SampleId], dispatch: false).Refusal.Should().Be(CollectionRefusal.AlreadyCollected);

        var dispatch = CollectionRules.Plan(set, [drawn.SampleId], dispatch: true);
        dispatch.Refusal.Should().Be(CollectionRefusal.None);
        dispatch.ToDraw.Should().BeEmpty();

        CollectionRules.Plan(set, [drawn.SampleId, left.SampleId], dispatch: false).ToDraw.Should().Equal(left.SampleId);
    }

    [Fact]
    public void The_draws_carry_the_fasting_hours_only_when_fasted_and_no_empty_comment()
    {
        var id = Guid.NewGuid();

        var fasted = CollectionRules.Requests([(id, 4m)], true, 12, CollectionLocation.Laboratory, "  au bras gauche ").Single();
        fasted.SampleId.Should().Be(id);
        fasted.VolumeMl.Should().Be(4m);
        fasted.PatientFasted.Should().BeTrue();
        fasted.DeclaredFastingHours.Should().Be(12);
        fasted.Location.Should().Be(CollectionLocation.Laboratory);
        fasted.Note.Should().Be("au bras gauche");

        var plain = CollectionRules.Requests([(id, null)], false, 12, null, " ").Single();
        plain.DeclaredFastingHours.Should().BeNull();
        plain.Location.Should().BeNull();
        plain.Note.Should().BeNull();
    }

    [Fact]
    public void The_collection_bar_follows_the_tubes_and_each_right()
    {
        var order = Guid.NewGuid();
        CollectionWorklistEntry[] waiting = [Tube(order, 1)];
        CollectionWorklistEntry[] drawn = [Tube(order, 1, collected: true)];

        var phlebotomist = new LicenceTestKit.User(true, Permissions.Samples.Collect, Permissions.Samples.Dispatch);
        var everything = new LicenceTestKit.User(true, Permissions.Samples.Collect, Permissions.Samples.Dispatch,
                                                 Permissions.Samples.Reprint, Permissions.Orders.View);

        CollectionRules.Actions(waiting, phlebotomist).Should().Be(new CollectionActions(true, true, false, false));
        CollectionRules.Actions(drawn, everything).Should().Be(new CollectionActions(false, true, true, true));
        CollectionRules.Actions([], everything).Should().Be(CollectionActions.None);
        CollectionRules.Actions(waiting, new LicenceTestKit.User(true)).Should().Be(CollectionActions.None);
    }

    [Fact]
    public void A_scan_names_a_tube_by_its_exact_barcode()
    {
        var tube = Tube(Guid.NewGuid(), 1, barcode: "T-0042");

        CollectionRules.Scanned([tube], " T-0042 ").Should().Be(tube);
        CollectionRules.Scanned([tube], "T-004").Should().BeNull();
        CollectionRules.Scanned([tube], "  ").Should().BeNull();
    }

    // ---- Réception -------------------------------------------------------------------------------

    private static SampleReceptionCandidate Candidate(SampleStatus status = SampleStatus.Collected, bool stale = false,
                                                      SampleDefect existing = SampleDefect.None,
                                                      decimal? volume = null, decimal? minimum = null,
                                                      bool cold = false) => new()
    {
        SampleId = Guid.NewGuid(),
        LabOrderId = Guid.NewGuid(),
        Barcode = "T-0001",
        AccessionNumber = "2610-000001",
        PatientName = "EL AMRANI Karim",
        SpecimenTypeName = new MultilingualText("Sang"),
        Status = status,
        StabilityExceeded = stale,
        ExistingDefects = existing,
        VolumeMl = volume,
        MinimumVolumeMl = minimum,
        RequiresColdChain = cold
    };

    [Fact]
    public void The_reception_pill_puts_the_tube_on_the_bench_first_then_delay_defects_and_status()
    {
        var tube = Candidate(stale: true);

        SampleReceptionRules.Verdict(tube, tube.SampleId).Should().Be(ReceptionVerdict.Scanned);
        SampleReceptionRules.Verdict(tube, null).Should().Be(ReceptionVerdict.ExcessiveDelay);
        SampleReceptionRules.Verdict(Candidate(existing: SampleDefect.Haemolysed), null).Should().Be(ReceptionVerdict.Defects);
        SampleReceptionRules.Verdict(Candidate(SampleStatus.InTransit), null).Should().Be(ReceptionVerdict.InTransit);
        SampleReceptionRules.Verdict(Candidate(), null).Should().Be(ReceptionVerdict.Waiting);
    }

    [Fact]
    public void A_scanned_tube_comes_with_its_delay_its_short_volume_and_its_upstream_defects_ticked()
    {
        SampleReceptionRules.PresetDefects(Candidate(stale: true, existing: SampleDefect.Haemolysed, volume: 1m, minimum: 2m))
            .Should().Be(SampleDefect.ExcessiveDelay | SampleDefect.Haemolysed | SampleDefect.InsufficientVolume);
        SampleReceptionRules.PresetDefects(Candidate(volume: 3m, minimum: 2m)).Should().Be(SampleDefect.None);
    }

    [Fact]
    public void Accept_or_accept_with_reservation_follows_the_defects_and_a_decided_tube_is_only_refusable()
    {
        SampleReceptionRules.Actions(Candidate(), SampleDefect.None).Should().Be(new ReceptionActions(true, false, true));
        SampleReceptionRules.Actions(Candidate(), SampleDefect.Clotted).Should().Be(new ReceptionActions(false, true, true));
        SampleReceptionRules.Actions(Candidate(SampleStatus.Received), SampleDefect.None).Should().Be(new ReceptionActions(false, false, true));
        SampleReceptionRules.Actions(Candidate(SampleStatus.Rejected), SampleDefect.None).Should().Be(ReceptionActions.None);
        SampleReceptionRules.Actions(null, SampleDefect.None).Should().Be(ReceptionActions.None);

        SampleReceptionRules.IsAlreadyDecided(Candidate(SampleStatus.InTransit)).Should().BeFalse();
        SampleReceptionRules.IsAlreadyDecided(Candidate(SampleStatus.Received)).Should().BeTrue();
    }

    [Fact]
    public void The_reception_buttons_also_need_their_rights()
    {
        var receiver = new LicenceTestKit.User(true, Permissions.Samples.Receive);
        var rejecter = new LicenceTestKit.User(true, Permissions.Samples.Reject);

        SampleReceptionRules.Actions(Candidate(), SampleDefect.None, receiver).Should().Be(new ReceptionActions(true, false, false));
        SampleReceptionRules.Actions(Candidate(), SampleDefect.Clotted, rejecter).Should().Be(new ReceptionActions(false, false, true));
    }

    [Fact]
    public void A_reception_temperature_must_be_a_plausible_number_and_the_cold_chain_only_counts_when_required()
    {
        SampleReceptionRules.IsTemperatureRefused(null).Should().BeFalse();
        SampleReceptionRules.IsTemperatureRefused("4,2").Should().BeFalse();
        SampleReceptionRules.IsTemperatureRefused("froid").Should().BeTrue();
        SampleReceptionRules.IsTemperatureRefused("150").Should().BeTrue();

        var plain = Candidate();
        var request = SampleReceptionRules.Request(plain, SampleDefect.Lipaemic, true, "4,2", true, "R1-B2");
        request.SampleId.Should().Be(plain.SampleId);
        request.ObservedDefects.Should().Be(SampleDefect.Lipaemic);
        request.AcceptWithReservation.Should().BeTrue();
        request.TemperatureCelsius.Should().Be(4.2m);
        request.ColdChainRespected.Should().BeNull();
        request.StorageLocation.Should().Be("R1-B2");

        SampleReceptionRules.Request(Candidate(cold: true), SampleDefect.None, false, null, false, null)
            .ColdChainRespected.Should().BeFalse();
    }

    // ---- Re-prélèvements -------------------------------------------------------------------------

    private static RecollectionRow Recall(OrderPriority priority = OrderPriority.Routine, int contacts = 0,
                                          string? phone = "+212661234567") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2610-000007", "CHRAIBI Fatima Zahra", phone, priority,
            "T-OLD", "T-NEW", SampleRejectionReason.Haemolysed, null, Now.AddHours(-2), "NFS", contacts, null, null);

    [Fact]
    public void Recollection_chips_and_search_keep_the_rows_named()
    {
        var called = Recall(contacts: 1);
        var urgent = Recall(OrderPriority.Urgent);

        RecollectionRules.Matches(called, RecollectionFilter.NotContacted, null, Now).Should().BeFalse();
        RecollectionRules.Matches(urgent, RecollectionFilter.NotContacted, null, Now).Should().BeTrue();
        RecollectionRules.Matches(urgent, RecollectionFilter.Priority, null, Now).Should().BeTrue();
        RecollectionRules.Matches(called, RecollectionFilter.Priority, null, Now).Should().BeFalse();

        RecollectionRules.Matches(called, RecollectionFilter.All, "chraibi", Now).Should().BeTrue();
        RecollectionRules.Matches(called, RecollectionFilter.All, "T-NEW", Now).Should().BeTrue();
        RecollectionRules.Matches(called, RecollectionFilter.All, "66123", Now).Should().BeTrue();
        RecollectionRules.Matches(Recall(phone: null), RecollectionFilter.All, "66123", Now).Should().BeFalse();
    }

    [Fact]
    public void Calling_back_needs_the_right_to_edit_the_file_the_sms_a_channel_and_closing_the_right_to_cancel()
    {
        var editor = new LicenceTestKit.User(true, Permissions.Orders.Edit);
        var manager = new LicenceTestKit.User(true, Permissions.Orders.Edit, Permissions.Orders.Cancel);

        RecollectionRules.Actions(Recall(), canSendSms: false, editor).Should().Be(new RecollectionActions(true, false, false));
        RecollectionRules.Actions(Recall(), canSendSms: true, manager).Should().Be(new RecollectionActions(true, true, true));
        RecollectionRules.Actions(null, canSendSms: true, manager).Should().Be(RecollectionActions.None);
    }
}
