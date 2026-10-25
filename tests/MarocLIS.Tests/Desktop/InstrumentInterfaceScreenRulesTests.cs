using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Instruments;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Interfaces automates » and of its « Tester une trame », moved out of the
/// workstation's InstrumentInterfaceView and InstrumentFrameTestDialog, now shared with the web.
/// </summary>
public sealed class InstrumentInterfaceScreenRulesTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    internal static InterfaceRow Link(bool enabled = true, int mappings = 3, int enabledMappings = 3, int failed = 0,
                                      string? error = null, bool autoAccept = false, string? blocked = null,
                                      InstrumentDirection direction = InstrumentDirection.Unidirectional) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "AU480", new MultilingualText("Beckman AU480"),
            InstrumentProtocol.AstmE1394, InstrumentTransportKind.Serial, direction, enabled, "COM3 · 9600 8N1",
            "ISO-8859-1", 15, 3, autoAccept, true, true, null, null, error, mappings, enabledMappings, 12, failed,
            "COM3", 9600, 8, "None", "One", "None", null, null, false, null, null, null, null)
        { EquipmentBlockKey = blocked };

    internal static MappingRow Mapping(string code = "GLU", bool enabled = true, string? catalogueUnit = "g/L",
                                       string? instrumentUnit = null, decimal? factor = null, decimal? offset = null,
                                       ResultValueType type = ResultValueType.Numeric, string? map = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), code, Guid.NewGuid(), "GLY", new MultilingualText("Glycémie"), "GLY",
            type, catalogueUnit, instrumentUnit, factor, offset, map, enabled);

    internal static MessageLogRow Message(InstrumentMessageProcessingStatus status = InstrumentMessageProcessingStatus.Imported,
                                          MessageDirection direction = MessageDirection.Inbound, int? results = 2,
                                          int? imported = 2, string? specimens = "T-0042", bool replayed = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), direction, new DateTimeOffset(2026, 10, 20, 9, 30, 15, TimeSpan.Zero), status,
            specimens, results, imported, null, null, replayed ? DateTimeOffset.UtcNow : null, replayed ? "Admin" : null, 120);

    private static PreviewLine Line(PreviewOutcome outcome, string? code = "GLU", string? detail = null, bool control = false) =>
        new("T-0042", control, "2026-000123", "EL AMRANI Karim", code, code, Guid.NewGuid(), "GLY", "1.02", "1.02",
            "g/L", null, null, outcome, detail);

    // ---- Rights ----------------------------------------------------------------------------------

    [Fact]
    public void Each_action_is_kept_by_the_right_the_workstation_asks()
    {
        var link = Link();
        var failed = Message(InstrumentMessageProcessingStatus.Failed);

        var configure = InstrumentInterfaceScreenRules.Actions(
            new LicenceTestKit.User(true, Permissions.Instruments.Configure), link, failed);
        configure.NewLink.Should().BeTrue();
        configure.EditMappings.Should().BeTrue();
        configure.TestFrame.Should().BeFalse("a frame names patients: it also takes the results right");
        configure.Toggle.Should().BeFalse();
        configure.ViewRaw.Should().BeFalse();
        configure.Replay.Should().BeFalse();
        configure.PurgeLog.Should().BeFalse();

        var all = InstrumentInterfaceScreenRules.Actions(
            new LicenceTestKit.User(true, Permissions.Instruments.Configure, Permissions.Results.View,
                                    Permissions.Instruments.StartStop, Permissions.Instruments.ReplayMessages,
                                    Permissions.Administration.RunMaintenance), link, failed);
        all.Should().Be(new InterfaceActions(true, true, true, true, true, "Interface.Disable", true, true, true));

        var nothingSelected = InstrumentInterfaceScreenRules.Actions(
            new LicenceTestKit.User(true, Permissions.Instruments.Configure, Permissions.Instruments.StartStop), null, null);
        nothingSelected.EditLink.Should().BeFalse();
        nothingSelected.Toggle.Should().BeFalse();
        nothingSelected.ToggleKey.Should().Be("Interface.Enable");
    }

    [Fact]
    public void Only_a_frame_somebody_can_still_act_on_is_replayed_or_listed_to_deal_with()
    {
        var user = new LicenceTestKit.User(true, Permissions.Instruments.ReplayMessages);
        var imported = Message();
        var partial = Message(InstrumentMessageProcessingStatus.PartiallyImported, imported: 1);
        var held = Message(InstrumentMessageProcessingStatus.AwaitingAcceptance);
        var outbound = Message(InstrumentMessageProcessingStatus.Failed, MessageDirection.Outbound);

        InstrumentInterfaceScreenRules.Actions(user, Link(), imported).Replay.Should().BeFalse();
        InstrumentInterfaceScreenRules.Actions(user, Link(), partial).Replay.Should().BeTrue();
        InstrumentInterfaceScreenRules.ToDealWith([imported, partial, held, outbound]).Should().Equal(partial, held);
    }

    [Fact]
    public void A_new_link_is_offered_for_an_analyser_without_one_by_code()
    {
        static InstrumentMasterRow Row(string code, EquipmentKind kind, bool linked) =>
            new(Guid.NewGuid(), code, new MultilingualText(code), kind, EquipmentStatus.Operational, null, null, null,
                null, null, null, null, null, null, linked, 0, false);

        var free = InstrumentInterfaceScreenRules.FreeInstruments(
            [Row("XN", EquipmentKind.Analyser, false), Row("AU", EquipmentKind.Analyser, false),
             Row("CEN", EquipmentKind.Centrifuge, false), Row("BC", EquipmentKind.Analyser, true)]);

        free.Select(i => i.Code).Should().Equal("AU", "XN");
    }

    // ---- Links ---------------------------------------------------------------------------------

    [Fact]
    public void Where_the_analysers_are_not_driven_the_live_state_says_so_rather_than_down()
    {
        var link = Link();

        InstrumentInterfaceScreenRules.LiveKey(false, link, null).Should().Be("Interface.NotDrivenHere");
        InstrumentInterfaceScreenRules.LiveTone(false, link, "Failed").Should().Be(InterfaceTone.Muted);
        InstrumentInterfaceScreenRules.LiveKey(true, Link(enabled: false), "Running").Should().Be("Interface.Disabled");
        InstrumentInterfaceScreenRules.LiveKey(true, link, null).Should().Be("Interface.Starting");
        InstrumentInterfaceScreenRules.LiveKey(true, link, "Running").Should().Be("InstrumentLinkStatus.Running");
        InstrumentInterfaceScreenRules.LiveTone(true, link, "Running").Should().Be(InterfaceTone.Ok);
        InstrumentInterfaceScreenRules.LiveTone(true, link, "Misconfigured").Should().Be(InterfaceTone.Critical);
        InstrumentInterfaceScreenRules.LiveTone(true, link, null).Should().Be(InterfaceTone.Info);
    }

    [Fact]
    public void A_link_is_coloured_by_what_is_worst_about_it_and_grey_when_off()
    {
        InstrumentInterfaceScreenRules.Tone(Link(enabled: false, error: "x"), "Failed").Should().Be(InterfaceTone.Muted);
        InstrumentInterfaceScreenRules.Tone(Link(enabledMappings: 0), null).Should().Be(InterfaceTone.Critical);
        InstrumentInterfaceScreenRules.Tone(Link(blocked: "Equipment.Blocked"), "Running").Should().Be(InterfaceTone.Critical);
        InstrumentInterfaceScreenRules.Tone(Link(failed: 2), "Running").Should().Be(InterfaceTone.Warning);
        InstrumentInterfaceScreenRules.Tone(Link(), "Running").Should().Be(InterfaceTone.Ok);
        InstrumentInterfaceScreenRules.Tone(Link(), null).Should().Be(InterfaceTone.Plain);
    }

    [Fact]
    public void A_link_s_flags_and_counts_read_as_the_grid_shows_them()
    {
        var link = Link(mappings: 7, enabledMappings: 5, failed: 3, error: "timeout", autoAccept: true, blocked: "Equipment.Blocked");

        InstrumentInterfaceScreenRules.Flags(link, Text)
            .Should().Equal("Equipment.Blocked(AU480)", "Interface.AutoAccept", "Interface.HasError");
        InstrumentInterfaceScreenRules.Flags(Link(enabled: false, enabledMappings: 0), Text)
            .Should().Equal("Interface.Disabled");
        InstrumentInterfaceScreenRules.MappingCount(link).Should().Be("5/7");
        InstrumentInterfaceScreenRules.MappingCount(Link()).Should().Be("3");
        InstrumentInterfaceScreenRules.MessagesToday(link).Should().Be("12 (3)");
        InstrumentInterfaceScreenRules.LastMessage(Link()).Should().Be("—");
    }

    // ---- Mappings ------------------------------------------------------------------------------

    [Fact]
    public void A_unit_mismatch_without_a_factor_is_the_alarm_of_the_code_table()
    {
        var mismatch = Mapping(instrumentUnit: "mmol/L");
        var converted = Mapping(instrumentUnit: "mmol/L", factor: 0.18m, offset: -0.5m);
        var coded = Mapping(type: ResultValueType.Coded);

        InstrumentInterfaceScreenRules.Tone(mismatch).Should().Be(InterfaceTone.Critical);
        InstrumentInterfaceScreenRules.Tone(converted).Should().Be(InterfaceTone.Plain);
        InstrumentInterfaceScreenRules.Tone(coded).Should().Be(InterfaceTone.Warning);
        InstrumentInterfaceScreenRules.Tone(Mapping(enabled: false, instrumentUnit: "mmol/L")).Should().Be(InterfaceTone.Muted);
        InstrumentInterfaceScreenRules.Flags(mismatch, Text).Should().Equal("Interface.UnitMismatch");
        InstrumentInterfaceScreenRules.Units(converted).Should().Be("mmol/L → g/L");
        InstrumentInterfaceScreenRules.Units(Mapping()).Should().Be("g/L");
        InstrumentInterfaceScreenRules.Conversion(converted, French).Should().Be("×0,18 -0,5");
        InstrumentInterfaceScreenRules.Parameter(converted).Should().Be("GLY/GLY");
    }

    // ---- Message log ---------------------------------------------------------------------------

    [Fact]
    public void A_frame_reads_with_its_status_its_counts_and_its_issues_in_the_operator_s_language()
    {
        var failed = Message(InstrumentMessageProcessingStatus.Failed, results: 3, imported: 0, replayed: true);

        InstrumentInterfaceScreenRules.Tone(failed).Should().Be(InterfaceTone.Critical);
        InstrumentInterfaceScreenRules.StatusTone(Message(InstrumentMessageProcessingStatus.Received)).Should().Be(InterfaceTone.Info);
        InstrumentInterfaceScreenRules.Status(failed, Text)
            .Should().Be("InstrumentMessageProcessingStatus.Failed · Interface.Replayed");
        InstrumentInterfaceScreenRules.Counts(failed).Should().Be("0/3");
        InstrumentInterfaceScreenRules.Counts(Message(results: null)).Should().BeEmpty();

        static string Said(string key, params object?[] args) => key == "Instrument.Unmapped" ? "Code {0} inconnu" : key;
        InstrumentInterfaceScreenRules.Issues("Instrument.Unmapped\tNA\nInstrument.NoTube\tT-9", Said)
            .Should().Be("Code NA inconnu · Instrument.NoTube (T-9)");
        InstrumentInterfaceScreenRules.Issues(null, Said).Should().BeEmpty();
    }

    [Fact]
    public void The_verbatim_frame_names_its_control_characters_rather_than_strip_them()
    {
        InstrumentInterfaceScreenRules.MakeControlCharactersVisible("\u0005\u00021H|\\^&\r\n\u0003\u0004", "\n")
            .Should().Be("<ENQ><STX>1H|\\^&<CR>\n<ETX><EOT>\n");
        InstrumentInterfaceScreenRules.RawHeader(Message(), Text)
            .Should().Be("MessageDirection.Inbound   ·   InstrumentMessageProcessingStatus.Imported   ·   2/2 result.plural   ·   T-0042");
    }

    [Fact]
    public void A_replay_that_stored_nothing_says_so_and_a_purge_states_its_scope_before()
    {
        InstrumentInterfaceScreenRules.ReplayOutcome(0, Text).Should().Be("Interface.ReplayNothing");
        InstrumentInterfaceScreenRules.ReplayOutcome(2, Text).Should().Be("Interface.ReplayDone(2)");

        var census = new MessageLogCensus(40, 3, new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero), 900);
        InstrumentInterfaceScreenRules.NothingToPurge(census).Should().BeFalse();
        InstrumentInterfaceScreenRules.NothingToPurge(census with { Purgeable = 0 }).Should().BeTrue();
        InstrumentInterfaceScreenRules.PurgeScope(census, Text, French).Should().StartWith("Interface.PurgeLogScope(40,").And.EndWith(",3)");
        InstrumentInterfaceScreenRules.RetainedWarning(census, Text).Should().Be("Interface.PurgeLogRetained(3)");
        InstrumentInterfaceScreenRules.RetainedWarning(census with { RetainedReplayable = 0 }, Text).Should().BeNull();
    }

    // ---- « Tester une trame » -----------------------------------------------------------------

    [Fact]
    public void The_frame_test_opens_on_the_last_inbound_frame_and_its_sample_uses_the_link_s_tubes_and_tests()
    {
        var outbound = Message(direction: MessageDirection.Outbound, specimens: "T-9");
        var empty = Message(results: 0, specimens: null);
        var last = Message(specimens: "T-0042, T-0043");

        InstrumentInterfaceScreenRules.OpensOn(last).Should().BeTrue();
        InstrumentInterfaceScreenRules.OpensOn(outbound).Should().BeFalse();
        InstrumentInterfaceScreenRules.LastInboundWithResults([outbound, empty, last]).Should().Be(last);
        InstrumentInterfaceScreenRules.SampleTube([outbound, empty, last]).Should().Be("T-0042");
        InstrumentInterfaceScreenRules.SampleTube([]).Should().Be("TUBE-0001");

        InstrumentInterfaceScreenRules.SampleTests([Mapping("0-12"), Mapping("7"), Mapping("GLU"), Mapping("9", enabled: false)])
            .Should().Equal(12, 7);
        InstrumentInterfaceScreenRules.SampleTests([Mapping("GLU")]).Should().BeNull();
        InstrumentInterfaceScreenRules.HasSample(InstrumentProtocol.RocheIntegra).Should().BeTrue();
        InstrumentInterfaceScreenRules.HasSample(InstrumentProtocol.AstmE1394).Should().BeFalse();
        InstrumentInterfaceScreenRules.Displayable("H|1\rR|2\r\nL|3", "\n").Should().Be("H|1\nR|2\nL|3");
    }

    [Fact]
    public void A_preview_says_what_the_import_would_do_line_by_line()
    {
        var stored = Line(PreviewOutcome.WouldStore);
        var unmapped = Line(PreviewOutcome.UnmappedCode, detail: "NA") with { ParameterCode = null, TestParameterId = null };
        var excluded = Line(PreviewOutcome.ExcludedType, control: true);

        InstrumentInterfaceScreenRules.OutcomeTone(stored).Should().Be(InterfaceTone.Ok);
        InstrumentInterfaceScreenRules.OutcomeTone(excluded).Should().Be(InterfaceTone.Muted);
        InstrumentInterfaceScreenRules.OutcomeTone(unmapped).Should().Be(InterfaceTone.Critical);
        InstrumentInterfaceScreenRules.Outcome(unmapped, Text).Should().Be("PreviewOutcome.UnmappedCode (NA)");
        InstrumentInterfaceScreenRules.Specimen(excluded, Text).Should().Be("Interface.TestFrameControl T-0042");
        InstrumentInterfaceScreenRules.Order(stored).Should().Be("2026-000123 · EL AMRANI Karim");
        InstrumentInterfaceScreenRules.Value(stored).Should().Be("1.02 g/L");
        InstrumentInterfaceScreenRules.Parameter(unmapped, new Dictionary<Guid, string>()).Should().BeEmpty();

        var preview = new MessagePreview(["Frame.Truncated"], [stored, unmapped, excluded]);
        InstrumentInterfaceScreenRules.Summary(preview, Text).Should().Be("Interface.TestFrameSummary(1,3)  ·  Frame.Truncated");
        InstrumentInterfaceScreenRules.SummaryTone(preview).Should().Be(InterfaceTone.Ok);
        InstrumentInterfaceScreenRules.SummaryTone(new MessagePreview([], [unmapped])).Should().Be(InterfaceTone.Warning);
        InstrumentInterfaceScreenRules.PreviewIssue("Frame.Bad\tline 3", Text).Should().Be("Frame.Bad : line 3");
    }
}
