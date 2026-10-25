using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Instruments;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of the link editor and of the code table, moved out of the workstation's
/// InstrumentInterfaceDialogs, now shared with the web.
/// </summary>
public sealed class InterfaceEditorRulesTests
{
    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    // ---- The form ------------------------------------------------------------------------------

    [Fact]
    public void A_new_link_starts_from_the_editor_s_defaults_and_an_existing_one_from_what_it_saved()
    {
        var blank = InterfaceEditorRules.FormOf(null);
        blank.Protocol.Should().Be(InstrumentProtocol.AstmE1394);
        blank.Transport.Should().Be(InstrumentTransportKind.Serial);
        blank.BaudRate.Should().Be("9600");
        blank.Encoding.Should().Be("ISO-8859-1");
        blank.AcknowledgementTimeoutSeconds.Should().Be(15);
        blank.SpecimenIdField.Should().BeNull();

        var link = InstrumentInterfaceScreenRulesTests.Link() with
        {
            BaudRate = 31250, Parity = "even", AcknowledgementTimeoutSeconds = 900, TcpPort = 5000,
            Format = MessageFormatSettings.Default with { SpecimenIdField = "O-4", TestCodeComponent = 9 }
        };
        var form = InterfaceEditorRules.FormOf(link);
        form.BaudRate.Should().Be("1200", "a speed the list does not offer falls back to its first, as the drop-down does");
        form.Parity.Should().Be("Even");
        form.AcknowledgementTimeoutSeconds.Should().Be(InterfaceEditorRules.AcknowledgementTimeoutMax);
        form.TcpPort.Should().Be("5000");
        form.SpecimenIdField.Should().Be("4");
        form.TestCodeComponent.Should().BeNull();
    }

    [Fact]
    public void The_tube_field_follows_the_protocol_and_the_components_go_where_there_are_none()
    {
        InterfaceEditorRules.SpecimenFields(InstrumentProtocol.AstmE1394)
            .Should().Equal(("3", "O-3"), ("4", "O-4"));
        InterfaceEditorRules.SpecimenFields(InstrumentProtocol.Hl7V2).Select(f => f.Caption).Should().Contain("SPM-2");

        var form = new InterfaceForm { Protocol = InstrumentProtocol.Hl7V2, SpecimenIdField = "SPM-2", TestCodeComponent = 2, SpecimenIdComponent = 1 };
        InterfaceEditorRules.ProtocolChanged(form);
        form.SpecimenIdField.Should().Be("SPM-2");

        form.Protocol = InstrumentProtocol.Poct1A;
        InterfaceEditorRules.ProtocolChanged(form);
        form.SpecimenIdField.Should().BeNull();
        form.TestCodeComponent.Should().BeNull();
        form.SpecimenIdComponent.Should().BeNull();
    }

    [Fact]
    public void A_preset_fills_the_fields_and_the_form_tells_which_preset_it_stands_at()
    {
        var form = InterfaceEditorRules.FormOf(null);
        InterfaceEditorRules.ProfileOf(form).Should().Be("Interface.FormatProfile.Standard");

        InterfaceEditorRules.ApplyProfile(form, "Interface.FormatProfile.Abbott");
        form.AcceptedResultTypes.Should().Be("F");
        InterfaceEditorRules.ProfileOf(form).Should().Be("Interface.FormatProfile.Abbott");

        form.TestCodeSuffix = "#";
        InterfaceEditorRules.ProfileOf(form).Should().Be(InterfaceEditorRules.CustomProfile);

        InterfaceEditorRules.ApplyProfile(form, InterfaceEditorRules.IntegraProfile);
        form.Protocol.Should().Be(InstrumentProtocol.RocheIntegra);
        form.Direction.Should().Be(InstrumentDirection.HostQueryDownload);
        form.Handshake.Should().Be("XOnXOff");
        form.Encoding.Should().Be("ASCII");
        form.AcknowledgementTimeoutSeconds.Should().Be(180);
        InterfaceEditorRules.ProfileOf(form).Should().Be(InterfaceEditorRules.IntegraProfile);
        InterfaceEditorRules.FormatNoticeKey(form.Protocol).Should().Be("Interface.IntegraNotice");

        InterfaceEditorRules.ApplyProfile(form, "Interface.FormatProfile.RocheCobas");
        form.Protocol.Should().Be(InstrumentProtocol.AstmE1394, "a vendor format puts an INTEGRA link back on ASTM");
        form.TestCodeSuffix.Should().Be("/");

        var before = form.TestCodeSuffix;
        InterfaceEditorRules.ApplyProfile(form, InterfaceEditorRules.CustomProfile);
        form.TestCodeSuffix.Should().Be(before);
    }

    [Fact]
    public void Only_the_half_of_the_form_the_transport_gives_a_meaning_to_is_open()
    {
        InterfaceEditorRules.Fields(InstrumentTransportKind.Serial, false)
            .Should().Be(new TransportFields(true, false, false, false, false, "Interface.SerialNotice"));
        InterfaceEditorRules.Fields(InstrumentTransportKind.Tcp, false)
            .Should().Be(new TransportFields(false, true, true, true, false, "Interface.TcpNotice"));
        InterfaceEditorRules.Fields(InstrumentTransportKind.Tcp, true).Host.Should().BeFalse();
        InterfaceEditorRules.Fields(InstrumentTransportKind.FileDrop, false)
            .Should().Be(new TransportFields(false, false, false, false, true, "Interface.FileDropNotice"));
    }

    [Fact]
    public void The_save_is_refused_on_the_box_at_fault()
    {
        var instrument = Guid.NewGuid();

        InterfaceEditorRules.Check(new InterfaceForm { SerialPortName = "COM3" }, Guid.Empty)!.Field.Should().Be("instrument");
        InterfaceEditorRules.Check(new InterfaceForm(), instrument)!.Should().Be(new FieldRefusal("serialport", "Interface_SerialPortRequired"));
        InterfaceEditorRules.Check(new InterfaceForm { Transport = InstrumentTransportKind.Tcp, TcpPort = "70000", Host = "10.0.0.5" }, instrument)!
            .Field.Should().Be("tcpport");
        InterfaceEditorRules.Check(new InterfaceForm { Transport = InstrumentTransportKind.Tcp, TcpPort = "5000" }, instrument)!
            .Field.Should().Be("host");
        InterfaceEditorRules.Check(new InterfaceForm { Transport = InstrumentTransportKind.Tcp, TcpPort = "5000", ListenAsServer = true }, instrument)
            .Should().BeNull();
        InterfaceEditorRules.Check(new InterfaceForm { Transport = InstrumentTransportKind.FileDrop }, instrument)!
            .Field.Should().Be("inbound");
        InterfaceEditorRules.NeedsAutoAcceptConfirmation(new InterfaceForm { AutoAcceptResults = true }).Should().BeTrue();
    }

    [Fact]
    public void The_request_carries_the_form_with_its_blanks_dropped_and_its_figures_read()
    {
        var instrument = Guid.NewGuid();
        var form = new InterfaceForm
        {
            Transport = InstrumentTransportKind.Tcp, Host = " 10.0.0.5 ", TcpPort = " 5000 ", BaudRate = "x",
            SerialPortName = "  ", TestCodeSuffix = "/", SpecimenIdField = "4"
        };

        var request = InterfaceEditorRules.ToRequest(form, null, instrument);

        request.InstrumentId.Should().Be(instrument);
        request.Host.Should().Be("10.0.0.5");
        request.TcpPort.Should().Be(5000);
        request.BaudRate.Should().Be(9600);
        request.SerialPortName.Should().BeNull();
        request.Format.TestCodeSuffix.Should().Be("/");
        request.Format.SpecimenIdField.Should().Be("4");
    }

    // ---- The code table ------------------------------------------------------------------------

    [Fact]
    public void The_table_shows_the_codes_in_use_and_counts_the_retired_ones()
    {
        var kept = InstrumentInterfaceScreenRulesTests.Mapping("GLU", factor: 0.18m);
        var retired = InstrumentInterfaceScreenRulesTests.Mapping("OLD", enabled: false);

        var lines = InterfaceEditorRules.Lines([kept, retired], System.Globalization.CultureInfo.InvariantCulture);

        lines.Should().ContainSingle().Which.Factor.Should().Be("0.18");
        InterfaceEditorRules.Retired([kept, retired]).Should().Be(1);
        InterfaceEditorRules.Summary(1, 1, Text).Should().Be("Interface.MappingSummary(1,1)");
        InterfaceEditorRules.Mapped(lines).Should().Contain(kept.TestParameterId);
        InterfaceEditorRules.ParameterLabel(kept, AppLanguage.French).Should().Be("GLY/GLY  Glycémie");
    }

    [Fact]
    public void A_new_line_starts_with_the_laboratory_s_code_and_a_unit_mismatch_is_seen_before_the_save()
    {
        var parameter = new MappableParameter(Guid.NewGuid(), "NFS", "HB", new MultilingualText("Hémoglobine"),
                                              ResultValueType.Numeric, "g/dL");
        var line = InterfaceEditorRules.NewLine(parameter, Guid.NewGuid());

        line.IsNew.Should().BeTrue();
        line.Code.Should().Be("HB");
        line.Original.CatalogueUnit.Should().Be("g/dL");

        line.InstrumentUnit = "g/L";
        InterfaceEditorRules.IsUnitMismatch(line).Should().BeTrue();
        line.Factor = "0,1";
        InterfaceEditorRules.IsUnitMismatch(line).Should().BeFalse();
    }

    [Fact]
    public void The_table_is_refused_on_its_first_line_at_fault()
    {
        MappingLine Line(string code, string factor = "", string offset = "") =>
            new() { Original = InstrumentInterfaceScreenRulesTests.Mapping(code), Code = code, Factor = factor, Offset = offset };

        var good = InterfaceEditorRules.Check([Line(" GLU ", "0,0555", "-1.5")]);
        good.Refusal.Should().BeNull();
        good.Line.Should().Be(-1);
        good.Mappings.Should().ContainSingle().Which.Should().Match<MappingRequest>(
            m => m.InstrumentTestCode == "GLU" && m.ConversionFactor == 0.0555m && m.ConversionOffset == -1.5m && m.IsEnabled);

        var blank = InterfaceEditorRules.Check([Line("GLU"), Line(" ")]);
        blank.Refusal!.Key.Should().Be("Interface_MappingCodeRequired");
        blank.Line.Should().Be(1);
        blank.Mappings.Should().BeEmpty();

        InterfaceEditorRules.Check([Line("GLU", "abc")]).Refusal!.Key.Should().Be("Interface_ConversionNotNumeric");
        InterfaceEditorRules.Check([Line("GLU", "0")]).Refusal!.Key.Should().Be("Interface_ConversionFactorZero");
        InterfaceEditorRules.Check([Line("GLU", "1", "99999999999")]).Refusal!.Key.Should().Be("Interface_ConversionOutOfRange");
    }
}
