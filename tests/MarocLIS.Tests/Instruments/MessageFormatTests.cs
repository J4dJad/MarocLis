using System.Text;
using FluentAssertions;
using MarocLIS.Application.Instruments;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Transport;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// The per-analyser message format: where a vendor departs from the textbook layout, the link says
/// so and the parser follows. "Auto" must keep reading exactly what it read before.
/// </summary>
public sealed class AstmMessageFormatTests
{
    private static string[] Frame(string order, params string[] results) =>
        [@"H|\^&|||ANALYSER^1|||||||P|1", "P|1", order, .. results, "L|1|N"];

    [Fact]
    public void Auto_ReadsTheFourthComponentAndKeepsTheSuffix()
    {
        var message = AstmMessageParser.Parse(Frame("O|1|T1||^^^GLY|R", "R|1|^^^GLY/|0.95|g/L||N||F"));

        var result = message.Specimens.Single().Results.Single();
        result.InstrumentTestCode.Should().Be("GLY/");
        result.RawTestId.Should().Be("^^^GLY/");
        result.IsExcluded.Should().BeFalse();
    }

    [Fact]
    public void Suffix_IsStrippedWhenConfigured()
    {
        var format = new InstrumentMessageFormat { TestCodeSuffix = "/" };

        var message = AstmMessageParser.Parse(Frame("O|1|T1||^^^GLY|R", "R|1|^^^767/5|1.2|||N||F"), format);

        message.Specimens.Single().Results.Single().InstrumentTestCode.Should().Be("767");
    }

    [Fact]
    public void Component_IsTakenWhereTheLinkSays()
    {
        // Abbott: "^^^16^TSH^UNDILUTED^F" — the assay number is component 4, its name component 5.
        var format = new InstrumentMessageFormat { TestCodeComponent = 5 };

        var message = AstmMessageParser.Parse(
            Frame("O|1|T1||^^^16|R", "R|1|^^^16^TSH^UNDILUTED^F|2.1|mUI/L||N||F"), format);

        message.Specimens.Single().Results.Single().InstrumentTestCode.Should().Be("TSH");
    }

    [Fact]
    public void AcceptedTypes_DropTheInterpretationBesideTheValue()
    {
        var format = new InstrumentMessageFormat { AcceptedResultTypes = "F" };

        var message = AstmMessageParser.Parse(Frame("O|1|T1||^^^28|R",
            "R|1|^^^28^HBsAg^UNDILUTED^F|0.31|S/CO||N||F",
            "R|2|^^^28^HBsAg^UNDILUTED^I|NonReactive|||N||F"), format);

        var results = message.Specimens.Single().Results;
        results[0].ResultType.Should().Be("F");
        results[0].IsExcluded.Should().BeFalse();
        results[1].ResultType.Should().Be("I");
        results[1].IsExcluded.Should().BeTrue();
    }

    [Fact]
    public void SpecimenComponent_ReadsTheBarcodeInsideTheField()
    {
        // Sysmex: O-4 is "rack^position^barcode".
        var format = new InstrumentMessageFormat { SpecimenIdField = "4", SpecimenIdComponent = 3 };

        var message = AstmMessageParser.Parse(
            Frame("O|1||01^05^TUBE77^B|^^^WBC|R", "R|1|^^^WBC|6.2|||N||F"), format);

        message.Specimens.Single().SpecimenId.Should().Be("TUBE77");
    }
}

public sealed class Hl7MessageFormatTests
{
    private static string Message(params string[] obx) => string.Join('\r',
        [
            @"MSH|^~\&|DEV|LAB|LIS|LAB|20261014101500||OUL^R22|42|P|2.5",
            "PID|1||P1",
            "SPM|1|TUBE123||SER",
            "OBR|1|PLACER9|FILLER9|GLU^Glucose",
            .. obx
        ]);

    [Fact]
    public void SpecimenField_CanPointAtTheSpmSegment()
    {
        var format = new InstrumentMessageFormat { SpecimenIdField = "SPM-2" };

        var message = Hl7ResultParser.Parse(
            Message("OBX|1|NM|GLU^Glucose^LN|1|5.4|mmol/L|3.9-6.1|N|||F"), format);

        message.Specimens.Single().SpecimenId.Should().Be("TUBE123");
    }

    [Fact]
    public void CodeComponent_PicksTheConfiguredPart()
    {
        var format = new InstrumentMessageFormat { TestCodeComponent = 2 };

        var message = Hl7ResultParser.Parse(
            Message("OBX|1|NM|2345-7^GLU^LN|1|5.4|mmol/L|3.9-6.1|N|||F"), format);

        message.Specimens.Single().Results.Single().InstrumentTestCode.Should().Be("GLU");
    }

    [Fact]
    public void AcceptedTypes_FilterOnValueTypeOrStatus()
    {
        var format = new InstrumentMessageFormat { AcceptedResultTypes = "NM" };

        var message = Hl7ResultParser.Parse(Message(
            "OBX|1|NM|GLU^Glucose|1|5.4|mmol/L|3.9-6.1|N|||F",
            "OBX|2|ST|ALARM^Alarm|1|Hemolysis||||||F"), format);

        var results = message.Specimens.Single().Results;
        results.Single(r => r.InstrumentTestCode == "GLU").IsExcluded.Should().BeFalse();
        results.Single(r => r.InstrumentTestCode == "ALARM").IsExcluded.Should().BeTrue();
    }
}

public sealed class InstrumentFlagAndUnmappedTests
{
    [Fact]
    public void Flags_LeaveOutNormalAndTheRocheNoAlarmCode()
    {
        var quiet = new InstrumentResult { InstrumentTestCode = "GLY", AbnormalFlag = "N", Comments = ["0"] };
        var alarmed = new InstrumentResult { InstrumentTestCode = "GLY", AbnormalFlag = "H", Comments = ["0", ">Test"] };

        InstrumentMessageSink.DescribeInstrumentFlags(quiet).Should().BeNull();
        InstrumentMessageSink.DescribeInstrumentFlags(alarmed).Should().Be("H · >Test");
    }

    [Theory]
    [InlineData(UnmappedNonNumericHandling.Ignore, "Blasts?", true)]
    [InlineData(UnmappedNonNumericHandling.Ignore, "12.5", false)]
    [InlineData(UnmappedNonNumericHandling.Report, "Blasts?", false)]
    public void UnmappedNonNumeric_IsDroppedOnlyWhenTheLinkSaysSo(
        UnmappedNonNumericHandling handling, string value, bool ignored)
    {
        var result = new InstrumentResult { InstrumentTestCode = "X", Value = value };

        InstrumentMessageSink.IgnoredAsUnmappedNonNumeric(handling, result).Should().Be(ignored);
    }

    [Fact]
    public void SpecimenFields_AreProtocolSpecific()
    {
        MessageFormatSettings.SpecimenFieldsFor(InstrumentProtocol.AstmE1394).Should().Equal("3", "4");
        MessageFormatSettings.SpecimenFieldsFor(InstrumentProtocol.Hl7V2).Should().Contain("SPM-2");
        MessageFormatSettings.SpecimenFieldsFor(InstrumentProtocol.Poct1A).Should().Equal("SPC", "PT");
    }
}

public sealed class PastedFrameTests
{
    [Fact]
    public void Normalise_UndoesTraceToolTranscription()
    {
        const string pasted = "<STX>1H|\\^&|||c311<CR><ETX>A4\n<STX>2P|1<CR><ETX>3B\r\n3O|1|T1||^^^GLY<CR>\n4R|1|^^^GLY/|0.95|g/L||N||F<CR>\nL|1|N";

        var text = InstrumentMessagePreviewer.Normalise(InstrumentProtocol.AstmE1394, pasted);
        var message = AstmMessageParser.Parse(text.Split('\r'));

        text.Split('\r').Select(r => r[0]).Should().Equal('H', 'P', 'O', 'R', 'L');
        message.Specimens.Single().SpecimenId.Should().Be("T1");
        message.Specimens.Single().Results.Single().Value.Should().Be("0.95");
    }

    [Fact]
    public void Normalise_JoinsHl7LinesWithCarriageReturns()
    {
        var text = InstrumentMessagePreviewer.Normalise(InstrumentProtocol.Hl7V2, "MSH|^~\\&|A\nPID|1\n");

        text.Should().Be("MSH|^~\\&|A\rPID|1");
    }
}

public sealed class Poct1ATests
{
    private const string Observations = """
        <?xml version="1.0" encoding="UTF-8"?>
        <OBS.R01>
          <HDR><HDR.control_id V="10005"/><HDR.version_id V="POCT1"/><HDR.creation_dttm V="2026-10-14T09:30:00+01:00"/></HDR>
          <SVC>
            <SVC.role_cd V="OBS"/>
            <SVC.observation_dttm V="2026-10-14T09:25:00+01:00"/>
            <PT>
              <PT.patient_id V="TUBE555"/>
              <OBS>
                <OBS.observation_id V="GLU"/>
                <OBS.value V="5.4" U="mmol/L"/>
                <OBS.interpretation_cd V="N"/>
              </OBS>
              <OBS>
                <OBS.observation_id V="HCT"/>
                <OBS.value V="38" U="%"/>
                <OBS.interpretation_cd V="L"/>
                <NTE><NTE.text V="Clot detected"/></NTE>
              </OBS>
            </PT>
          </SVC>
          <SVC>
            <SVC.role_cd V="LQC"/>
            <CTC><CTC.name V="Level 1"/><CTC.lot_number V="LOT42"/></CTC>
            <OBS><OBS.observation_id V="GLU"/><OBS.value V="2.8" U="mmol/L"/></OBS>
          </SVC>
          <SVC>
            <SVC.role_cd V="CAL"/>
          </SVC>
        </OBS.R01>
        """;

    [Fact]
    public void Parser_ReadsPatientAndControlEvents()
    {
        var message = Poct1AResultParser.Parse(Observations);

        message.Kind.Should().Be(InstrumentMessageKind.Results);
        message.ControlId.Should().Be("10005");
        message.Specimens.Should().HaveCount(2);

        var patient = message.Specimens[0];
        patient.SpecimenId.Should().Be("TUBE555");
        patient.IsControl.Should().BeFalse();
        patient.Results.Select(r => r.InstrumentTestCode).Should().Equal("GLU", "HCT");
        patient.Results[0].Value.Should().Be("5.4");
        patient.Results[0].Unit.Should().Be("mmol/L");
        patient.Results[1].AbnormalFlag.Should().Be("L");
        patient.Results[1].Comments.Should().Equal("Clot detected");

        var control = message.Specimens[1];
        control.IsControl.Should().BeTrue();
        control.ControlIdentifier.Should().Be("LOT42");

        // The calibration is reported, not imported.
        message.Issues.Should().ContainSingle(i => i.ResourceKey == "Interface_Poct1ARoleIgnored");
        message.HasBlockingIssues.Should().BeFalse();
    }

    [Fact]
    public void Parser_RefusesInvalidXml()
    {
        var message = Poct1AResultParser.Parse("<OBS.R01><HDR>");

        message.HasBlockingIssues.Should().BeTrue();
    }

    [Fact]
    public void Parser_RefusesExternalEntities()
    {
        const string hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]>
            <OBS.R01><SVC><PT><PT.patient_id V="&e;"/></PT></SVC></OBS.R01>
            """;

        Poct1AResultParser.Parse(hostile).HasBlockingIssues.Should().BeTrue();
    }

    [Fact]
    public void Reader_SplitsDocumentsAcrossReads()
    {
        var reader = new Poct1A.Reader(Encoding.UTF8);
        const string two = "<?xml version=\"1.0\"?><HEL.R01><HDR/></HEL.R01><DST.R01><HDR/></DST.R01>";

        reader.Feed(Encoding.UTF8.GetBytes(two[..30]));
        reader.TryReadMessage().Should().BeNull();

        reader.Feed(Encoding.UTF8.GetBytes(two[30..]));
        reader.TryReadMessage().Should().StartWith("<?xml").And.EndWith("</HEL.R01>");
        reader.TryReadMessage().Should().Be("<DST.R01><HDR/></DST.R01>");
        reader.TryReadMessage().Should().BeNull();
    }

    [Fact]
    public async Task Driver_AcknowledgesRequestsObservationsAndEnds()
    {
        var link = new InstrumentInterface(Guid.NewGuid(), Guid.NewGuid(), InstrumentProtocol.Poct1A,
                                           InstrumentTransportKind.Tcp);
        var transport = new RecordingTransport();
        var sink = new RecordingSink(accept: true);
        var driver = new Poct1AInstrumentDriver(link, transport, sink);

        await driver.FeedAsync(Bytes("<HEL.R01><HDR><HDR.control_id V=\"1\"/></HDR><DEV><DEV.device_id V=\"GM-7\"/></DEV></HEL.R01>"));
        await driver.FeedAsync(Bytes("<DST.R01><HDR><HDR.control_id V=\"2\"/></HDR><DST><DST.new_observations_qty V=\"1\"/></DST></DST.R01>"));
        await driver.FeedAsync(Bytes(Observations));
        await driver.FeedAsync(Bytes("<EOT.R01><HDR><HDR.control_id V=\"4\"/></HDR><EOT><EOT.topic_cd V=\"OBS\"/></EOT></EOT.R01>"));

        transport.Sent.Select(Poct1A.ReadHeader).Select(h => h!.Type)
            .Should().Equal("ACK.R01", "ACK.R01", "REQ.R01", "ACK.R01", "ACK.R01", "END.R01");

        transport.Sent[3].Should().Contain("V=\"AA\"").And.Contain("ACK.ack_control_id V=\"10005\"");
        sink.Handled.Should().ContainSingle().Which.Specimens.Should().HaveCount(2);
    }

    [Fact]
    public async Task Driver_SaysAeWhenTheObservationsWereNotStored()
    {
        var link = new InstrumentInterface(Guid.NewGuid(), Guid.NewGuid(), InstrumentProtocol.Poct1A,
                                           InstrumentTransportKind.Tcp);
        var transport = new RecordingTransport();
        var driver = new Poct1AInstrumentDriver(link, transport, new RecordingSink(accept: false));

        await driver.FeedAsync(Bytes(Observations));

        transport.Sent.Should().ContainSingle().Which.Should().Contain("V=\"AE\"");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class RecordingTransport : IInstrumentTransport
    {
        public List<string> Sent { get; } = [];
        public bool IsConnected => true;
        public string Description => "test";
        public event EventHandler<string>? Disconnected { add { } remove { } }

        public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<int> ReadAsync(byte[] buffer, CancellationToken ct = default) => Task.FromResult(0);

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Sent.Add(Encoding.UTF8.GetString(data.Span));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink(bool accept) : IInstrumentMessageSink
    {
        public List<InstrumentMessage> Handled { get; } = [];

        public Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message, CancellationToken ct = default)
        {
            Handled.Add(message);
            return Task.FromResult(accept);
        }

        public Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
            Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstrumentOrderDownload>>([]);

        public Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content,
                                CancellationToken ct = default) => Task.CompletedTask;

        public Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error,
                                               CancellationToken ct = default) => Task.CompletedTask;
    }
}
