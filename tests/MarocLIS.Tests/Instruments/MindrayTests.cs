using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Quality;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Simulator;
using MarocLIS.Instruments.Transport;
using MarocLIS.Tests.Domain;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// Mindray analysers in HL7 v2.3.1: BS chemistry, BC haematology and CL immunoassay. The messages
/// below are laid out as the LIS manuals print them; the dialogues run through the HL7 driver over a
/// loopback socket with the simulated analyser at the other end, as the analyser connects to the LIS.
/// </summary>
public sealed class MindrayTests
{
    private static readonly InstrumentMessageFormat Bs = new() { Hl7Dialect = Hl7Dialect.MindrayBs, SpecimenIdField = "OBR-2" };
    private static readonly InstrumentMessageFormat Bc = new() { Hl7Dialect = Hl7Dialect.MindrayBc, SpecimenIdField = "OBR-3", TestCodeComponent = 2 };
    private static readonly InstrumentMessageFormat Cl = new() { Hl7Dialect = Hl7Dialect.MindrayCl, SpecimenIdField = "OBR-2" };

    // A BS-480 sample: barcode in OBR-2, the analyser's sample number in OBR-3, one OBX per test.
    private const string BsResults =
        "MSH|^~\\&|Mindray|BS-480|||20261026101500||ORU^R01|17|P|2.3.1||||0||ASCII|||\r" +
        "PID|1||||ALAMI Fatima||||||||||||||||||||||||||\r" +
        "OBR|1|2610260012|12|Mindray^BS-480|N||20261026101500||||||||serum|||||||||||||||||||||||||||||||||\r" +
        "OBX|1|NM|GLU|GLU|5.230000|mmol/L|3.90-6.10|N|||F||5.230000|20261026101500||||\r" +
        "OBX|2|NM|ALT|ALT|56.000000|U/L|0-40|H|||F||56.000000|20261026101500||||\r";

    // A BS control: MSH-16 « 2 »; control number, name, lot, expiry and level in OBR-2 to OBR-6.
    private const string BsControl =
        "MSH|^~\\&|Mindray|BS-480|||20261026102000||ORU^R01|18|P|2.3.1||||2||ASCII|||\r" +
        "OBR|1|3|Contrôle Normal|882|20270430|H|20261026102000||||||||serum|||||||||||||||||||||||||||||||||\r" +
        "OBX|1|NM|GLY|GLY|1.020000|g/L|||||F||1.020000|20261026102000||||\r";

    // A BS calibration: MSH-16 « 1 ».
    private const string BsCalibration =
        "MSH|^~\\&|Mindray|BS-480|||20261026102500||ORU^R01|19|P|2.3.1||||1||ASCII|||\r" +
        "OBR|1|||GLU|GLU|Linear|20261026102500||||||||||||||||||||||||||||||||||||||||\r" +
        "OBX|1|NM|GLU|GLU|0.012300|||||||F||0.012300|20261026102500||||\r";

    // A BC-5380 count: sample number in OBR-3, LOINC-coded parameters, mode and flag lines, a histogram.
    private const string BcResults =
        "MSH|^~\\&|Mindray|BC-5380|||20261026103000||ORU^R01|5|P|2.3.1||||0||UNICODE|||\r" +
        "PID|1||^^^^MR||^BENNANI Omar|||M\r" +
        "PV1|1\r" +
        "OBR|1||2610260031|00001^Automated Count^99MRC||20261026102800|20261026103000|||||||||||||||||HM||||||||admin\r" +
        "OBX|1|IS|08001^Take Mode^99MRC||O||||||F\r" +
        "OBX|2|IS|08003^Test Mode^99MRC||CBC+DIFF||||||F\r" +
        "OBX|3|NM|6690-2^WBC^LN||11.42|10*9/L|4.00-10.00|H|||F\r" +
        "OBX|4|NM|718-7^HGB^LN||135|g/L|110-160|N|||F\r" +
        "OBX|5|NM|777-3^PLT^LN||254|10*9/L|100-300|N|||F\r" +
        "OBX|6|IS|12014^Platelet Clump?^99MRC||T||||||F\r" +
        "OBX|7|ED|15000^WBC Histogram. BMP^99MRC||^Image^BMP^Base64^Qk02AAAAAAAAADYAAAAoAAAAEAAAABAAAAABABgAAAAAAAAD||||||F\r";

    // A CL-1000i in HL7 mode: laid out as the BS.
    private const string ClResults =
        "MSH|^~\\&|Mindray|CL-1000i|||20261026104000||ORU^R01|31|P|2.3.1||||0||ASCII|||\r" +
        "PID|1||||||||||||||||||||||||||||\r" +
        "OBR|1|2610260045|7|Mindray^CL-1000i|N||20261026104000||||||||serum|||||||||||||||||||||||||||||||||\r" +
        "OBX|1|NM|TSH|TSH|2.340000|uIU/mL|0.35-4.94|N|||F||2.340000|20261026104000||||\r";

    // ---- Results ----------------------------------------------------------------------------------

    [Fact]
    public void BsResults_AreReadOnTheBarcode_WithTheCodesAsSent()
    {
        var message = Hl7ResultParser.Parse(BsResults, Bs);

        message.Kind.Should().Be(InstrumentMessageKind.Results);
        var specimen = message.Specimens.Single();
        specimen.SpecimenId.Should().Be("2610260012");
        specimen.IsControl.Should().BeFalse();
        specimen.PatientName.Should().Be("ALAMI Fatima");
        specimen.Results.Select(r => (r.InstrumentTestCode, r.Value, r.Unit, r.AbnormalFlag))
            .Should().Equal(("GLU", "5.230000", "mmol/L", "N"), ("ALT", "56.000000", "U/L", "H"));
    }

    [Fact]
    public void BsResults_OfATubeReadWithoutItsBarcode_AreNotTakenForTheTubeOfItsRunNumber()
    {
        // The barcode was not read: OBR-2 is empty, OBR-3 still holds the run's sample number « 12 ».
        var noRead = BsResults.Replace("OBR|1|2610260012|12|", "OBR|1||12|", StringComparison.Ordinal);

        var message = Hl7ResultParser.Parse(noRead, Bs);

        message.Specimens.Should().NotContain(s => s.SpecimenId == "12",
                                              "the run number is no barcode: the results would land on the tube labelled 12");
        message.HasBlockingIssues.Should().BeTrue("a result without its tube is held, replayable, not imported");
        message.Issues.Should().Contain(i => i.ResourceKey == "Instruments_MissingSpecimenId");
    }

    [Fact]
    public void BsControl_GoesToQc_WithItsLotAndLevelReadable()
    {
        var message = Hl7ResultParser.Parse(BsControl, Bs);

        var specimen = message.Specimens.Single();
        specimen.IsControl.Should().BeTrue("MSH-16 « 2 » marks a quality control");
        ControlLotMatcher.Names(specimen.ControlIdentifier!, "882").Should().BeTrue();
        ControlLotMatcher.LevelNamed(specimen.ControlIdentifier!).Should().Be(QcLevel.High);

        // The same frame read as standard HL7 would have gone to a patient file.
        Hl7ResultParser.Parse(BsControl).Specimens.Single().IsControl.Should().BeFalse();
    }

    [Fact]
    public async Task BsControl_IsStoredOnTheTargetOfItsLotAndLevel()
    {
        var bench = new ReviewFixesCycle3ClinicalTests.ControlBench(("881", QcLevel.Normal), ("882", QcLevel.High));

        await bench.SendAsync(Hl7ResultParser.Parse(BsControl, Bs));

        bench.Stored.Should().ContainSingle().Which.QcTargetId.Should().Be(bench.Targets[1].Id);
    }

    [Fact]
    public void BsCalibration_IsIgnored_WithTheReasonOnTheLogLine()
    {
        var message = Hl7ResultParser.Parse(BsCalibration, Bs);

        message.Kind.Should().Be(InstrumentMessageKind.Status, "the sink records it as ignored and the ACK is AA");
        message.Specimens.Should().BeEmpty();
        message.HasBlockingIssues.Should().BeFalse();
        message.Issues.Should().ContainSingle().Which.ResourceKey.Should().Be("Instruments_MindrayCalibrationIgnored");
    }

    [Fact]
    public void BcResults_KeepTheNumbers_AndTheGraphicsAndFlagsAreDroppedWhenTheLinkSaysSo()
    {
        var message = Hl7ResultParser.Parse(BcResults, Bc);

        var specimen = message.Specimens.Single();
        specimen.SpecimenId.Should().Be("2610260031");
        specimen.Results.Select(r => r.InstrumentTestCode)
            .Should().Equal("Take Mode", "Test Mode", "WBC", "HGB", "PLT", "Platelet Clump?", "WBC Histogram. BMP");

        var wbc = specimen.Results.Single(r => r.InstrumentTestCode == "WBC");
        (wbc.Value, wbc.AbnormalFlag, wbc.Unit).Should().Be(("11.42", "H", "10*9/L"));

        var histogram = specimen.Results.Single(r => r.InstrumentTestCode == "WBC Histogram. BMP");
        histogram.Value.Should().Be("(ED)", "the Base64 bitmap is no value to carry into a result");

        var kept = specimen.Results
            .Where(r => !InstrumentMessageSink.IgnoredAsUnmappedNonNumeric(UnmappedNonNumericHandling.Ignore, r))
            .Select(r => r.InstrumentTestCode);
        kept.Should().Equal("WBC", "HGB", "PLT");

        specimen.Results.Count(r => InstrumentMessageSink.IgnoredAsUnmappedNonNumeric(UnmappedNonNumericHandling.Report, r))
            .Should().Be(0, "« Report » lists every unknown code");
    }

    [Fact]
    public void BcResults_OnTheLoincCode_WhenTheLinkReadsTheFirstComponent()
    {
        var message = Hl7ResultParser.Parse(BcResults, Bc with { TestCodeComponent = 1 });

        message.Specimens.Single().Results.Select(r => r.InstrumentTestCode).Should().Contain(["6690-2", "718-7", "777-3"]);
    }

    [Fact]
    public void ClResults_AreReadAsTheBs()
    {
        var specimen = Hl7ResultParser.Parse(ClResults, Cl).Specimens.Single();

        specimen.SpecimenId.Should().Be("2610260045");
        specimen.Results.Single().Should().Match<InstrumentResult>(r => r.InstrumentTestCode == "TSH" && r.Value == "2.340000");
    }

    [Fact]
    public void TheQuery_GivesTheBarcodeOfQrd8()
    {
        var query = new MindrayInstrumentEmulator(Hl7Dialect.MindrayBs, seed: 1).Query("2610260012");

        var message = Hl7ResultParser.Parse(query, Bs);

        message.Kind.Should().Be(InstrumentMessageKind.Query);
        message.QueriedSpecimenIds.Should().Equal("2610260012");
    }

    // ---- Answers ----------------------------------------------------------------------------------

    [Fact]
    public void TheWorklist_CarriesPatientSampleAndTestsInTheNumberedDspLines()
    {
        var query = Hl7Message.Parse(new MindrayInstrumentEmulator(seed: 1).Query("2610260012"));
        var order = new InstrumentOrderDownload
        {
            SpecimenId = "2610260012",
            InstrumentTestCodes = ["GLU", "ALT"],
            PatientId = "P-000123",
            PatientName = "ALAMI Fatima",
            Sex = "F",
            DateOfBirth = new DateOnly(1980, 5, 17),
            Priority = "S",
            SpecimenType = "Sérum",
            CollectedAt = new DateTimeOffset(2026, 10, 26, 8, 30, 0, TimeSpan.FromHours(1))
        };

        var dsr = Hl7Message.Parse(MindrayHl7.BuildWorklist(query, order, "MAROCLIS", DateTimeOffset.Now));

        dsr.MessageType.Should().Be("DSR^Q03");
        dsr.Field("QAK", 2).Should().Be("OK");
        dsr.FirstSegment("QRD").Should().NotBeNull("the query's QRD is echoed");
        var lines = dsr.SegmentsNamed("DSP").ToDictionary(s => int.Parse(dsr.Field(s, 1)!), s => dsr.Field(s, 3));
        lines[1].Should().Be("P-000123");
        lines[3].Should().Be("ALAMI Fatima");
        lines[4].Should().Be("19800517000000");
        lines[5].Should().Be("F");
        lines[21].Should().Be("2610260012");
        lines[23].Should().Be("20261026083000");
        lines[24].Should().Be("Y");
        lines[26].Should().Be("serum");
        lines[29].Should().Be("GLU^^^");
        lines[30].Should().Be("ALT^^^");
        dsr.Segments[^1][0].Should().Be("DSC");
    }

    [Fact]
    public void TheResultAck_IsAckR01_WithTheErrorConditionInMsa6()
    {
        var inbound = Hl7Message.Parse(BsResults);

        var ack = Hl7Message.Parse(MindrayHl7.BuildAck(inbound, "MAROCLIS", DateTimeOffset.Now));
        ack.MessageType.Should().Be("ACK^R01");
        (ack.Field("MSA", 1), ack.Field("MSA", 2), ack.Field("MSA", 6)).Should().Be(("AA", "17", "0"));
        ack.Version.Should().Be("2.3.1");

        var refused = Hl7Message.Parse(MindrayHl7.BuildAck(inbound, "MAROCLIS", DateTimeOffset.Now, "retain"));
        (refused.Field("MSA", 1), refused.Field("MSA", 6)).Should().Be(("AE", "207"));
    }

    // ---- Over a loopback socket, through the driver -------------------------------------------------

    [Fact]
    public async Task Bs_ResultsQueryAndWorklist_OverTcp_ThroughTheSimulator()
    {
        var sink = new RecordingSink
        {
            Orders = { ["2610260012"] = new InstrumentOrderDownload { SpecimenId = "2610260012", InstrumentTestCodes = ["GLU", "ALT"], PatientName = "ALAMI Fatima" } }
        };

        await using var lis = await LisAsync(Hl7Dialect.MindrayBs, InstrumentDirection.Bidirectional, sink);
        await using var analyser = await lis.AnalyserAsync(new MindrayInstrumentEmulator(Hl7Dialect.MindrayBs, seed: 3));

        // The tube is scanned: QRY^Q02, answered QCK^Q02 « OK » then DSR^Q03, acknowledged ACK^Q03.
        var (qck, dsr) = await analyser.QueryAsync("2610260012");
        Hl7Message.Parse(qck!).MessageType.Should().Be("QCK^Q02");
        Hl7Message.Parse(qck!).Field("QAK", 2).Should().Be("OK");
        var worklist = MindrayInstrumentEmulator.ReadWorklist(dsr!);
        worklist.Barcode.Should().Be("2610260012");
        worklist.Tests.Should().Equal("GLU", "ALT");
        worklist.PatientName.Should().Be("ALAMI Fatima");

        // The tests run and come back: ORU^R01, acknowledged ACK^R01.
        var tests = worklist.Tests.Select(c => { var (v, u) = analyser.Emulator.Measure(c); return (c, v, u); }).ToList();
        var ack = await analyser.ReportAsync("2610260012", tests);
        Hl7Message.Parse(ack!).MessageType.Should().Be("ACK^R01");
        Hl7Message.Parse(ack!).Field("MSA", 1).Should().Be("AA");

        // A control and a calibration are acknowledged too.
        var control = await analyser.ExchangeAsync(analyser.Emulator.ControlResults("Contrôle", "882", "M", [("GLU", 5.1m, "mmol/L")]));
        Hl7Message.Parse(control!).Field("MSA", 1).Should().Be("AA");
        var calibration = await analyser.ExchangeAsync(analyser.Emulator.CalibrationResults("GLU"));
        Hl7Message.Parse(calibration!).Field("MSA", 1).Should().Be("AA");

        await lis.StopAsync();

        sink.Queried.Should().Equal("2610260012");
        var handled = sink.Handled.Where(m => m.Kind == InstrumentMessageKind.Results).ToList();
        handled.Should().HaveCount(2);
        handled[0].Specimens.Single().Results.Select(r => r.InstrumentTestCode).Should().Equal("GLU", "ALT");
        handled[1].Specimens.Single().IsControl.Should().BeTrue();
        sink.Handled.Should().Contain(m => m.Kind == InstrumentMessageKind.Status, "the calibration is on record as ignored");
        sink.Handled.Should().NotContain(m => m.Kind == InstrumentMessageKind.Acknowledgement, "ACK^Q03 closes the dialogue");
        sink.Raw.Should().Contain(r => r.Contains("ACK^Q03"), "the analyser's acknowledgement is in the log");
    }

    [Fact]
    public async Task Bs_AnUnknownBarcode_IsAnsweredNotFound_WithNoWorklist()
    {
        var sink = new RecordingSink();
        await using var lis = await LisAsync(Hl7Dialect.MindrayBs, InstrumentDirection.Bidirectional, sink);
        await using var analyser = await lis.AnalyserAsync(new MindrayInstrumentEmulator(Hl7Dialect.MindrayBs, seed: 4));

        var (qck, dsr) = await analyser.QueryAsync("9999999999");

        Hl7Message.Parse(qck!).Field("QAK", 2).Should().Be("NF");
        dsr.Should().BeNull();
        analyser.AnswerTimeout = TimeSpan.FromMilliseconds(300);
        (await analyser.ReadAsync()).Should().BeNull("nothing follows a « not found »");

        await lis.StopAsync();
        sink.Queried.Should().Equal("9999999999");
    }

    [Fact]
    public async Task Bc_ResultsAreAcknowledged_AndAQueryIsNotFound()
    {
        var sink = new RecordingSink
        {
            Orders = { ["2610260031"] = new InstrumentOrderDownload { SpecimenId = "2610260031", InstrumentTestCodes = ["WBC"] } }
        };
        await using var lis = await LisAsync(Hl7Dialect.MindrayBc, InstrumentDirection.Bidirectional, sink);
        await using var analyser = await lis.AnalyserAsync(new MindrayInstrumentEmulator(Hl7Dialect.MindrayBc, seed: 5));

        var ack = await analyser.ReportAsync("2610260031", [("WBC", 11.4m, "10*9/L"), ("HGB", 135m, "g/L")]);
        Hl7Message.Parse(ack!).MessageType.Should().Be("ACK^R01");

        var (qck, _) = await analyser.QueryAsync("2610260031");
        Hl7Message.Parse(qck!).Field("QAK", 2).Should().Be("NF", "the BC worklist dialogue is not offered");

        await lis.StopAsync();

        sink.Queried.Should().BeEmpty();
        var results = sink.Handled.Single().Specimens.Single().Results;
        results.Where(r => !InstrumentMessageSink.IgnoredAsUnmappedNonNumeric(UnmappedNonNumericHandling.Ignore, r))
               .Select(r => r.InstrumentTestCode)
               .Should().Equal("Age", "WBC", "HGB", "WBC Histogram Left Line");
    }

    [Fact]
    public async Task Cl_QueryIsAnsweredAsTheBs()
    {
        var sink = new RecordingSink
        {
            Orders = { ["2610260045"] = new InstrumentOrderDownload { SpecimenId = "2610260045", InstrumentTestCodes = ["TSH", "FT4"] } }
        };
        await using var lis = await LisAsync(Hl7Dialect.MindrayCl, InstrumentDirection.Bidirectional, sink);
        await using var analyser = await lis.AnalyserAsync(new MindrayInstrumentEmulator(Hl7Dialect.MindrayCl, seed: 6));

        var (_, dsr) = await analyser.QueryAsync("2610260045");
        MindrayInstrumentEmulator.ReadWorklist(dsr!).Tests.Should().Equal("TSH", "FT4");

        var ack = await analyser.ReportAsync("2610260045", [("TSH", 2.34m, "uIU/mL")]);
        Hl7Message.Parse(ack!).Field("MSA", 1).Should().Be("AA");

        await lis.StopAsync();
        sink.Handled.Single().Specimens.Single().SpecimenId.Should().Be("2610260045");
    }

    [Fact]
    public async Task AUnidirectionalLink_AnswersNotFound_WithoutLookingUp()
    {
        var sink = new RecordingSink
        {
            Orders = { ["2610260012"] = new InstrumentOrderDownload { SpecimenId = "2610260012", InstrumentTestCodes = ["GLU"] } }
        };
        await using var lis = await LisAsync(Hl7Dialect.MindrayBs, InstrumentDirection.Unidirectional, sink);
        await using var analyser = await lis.AnalyserAsync(new MindrayInstrumentEmulator(Hl7Dialect.MindrayBs, seed: 7));

        var (qck, dsr) = await analyser.QueryAsync("2610260012");

        Hl7Message.Parse(qck!).Field("QAK", 2).Should().Be("NF");
        dsr.Should().BeNull();
        await lis.StopAsync();
        sink.Queried.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedResult_IsAcknowledgedAe_SoTheAnalyserKeepsIt()
    {
        var sink = new RecordingSink { Accept = false };
        await using var lis = await LisAsync(Hl7Dialect.MindrayBs, InstrumentDirection.Unidirectional, sink);
        await using var analyser = await lis.AnalyserAsync(new MindrayInstrumentEmulator(Hl7Dialect.MindrayBs, seed: 8));

        var ack = Hl7Message.Parse((await analyser.ReportAsync("2610260012", [("GLU", 5m, "mmol/L")]))!);

        ack.MessageType.Should().Be("ACK^R01");
        (ack.Field("MSA", 1), ack.Field("MSA", 6)).Should().Be(("AE", "207"));
        await lis.StopAsync();
    }

    [Fact]
    public async Task AResultMessage_IsStoredOnceByTheSink_NotAlsoAsARawLine()
    {
        var sink = new RecordingSink();
        await using var lis = await LisAsync(Hl7Dialect.MindrayBs, InstrumentDirection.Unidirectional, sink);
        await using var analyser = await lis.AnalyserAsync(new MindrayInstrumentEmulator(Hl7Dialect.MindrayBs, seed: 9));

        await analyser.ReportAsync("2610260012", [("GLU", 5m, "mmol/L")]);

        sink.Handled.Should().ContainSingle();
        sink.Raw.Should().NotContain(r => r.Contains("ORU^R01"));
        await lis.StopAsync();
    }

    [Fact]
    public void TheSampleTranscript_ReadsInTheTester()
    {
        var bs = MindrayInstrumentEmulator.SampleTranscript(Hl7Dialect.MindrayBs, "TUBE-7", ["GLU", "UREA"]);
        Hl7ResultParser.Parse(bs, Bs).Specimens.Single().Results.Select(r => r.InstrumentTestCode).Should().Equal("GLU", "UREA");

        var bc = MindrayInstrumentEmulator.SampleTranscript(Hl7Dialect.MindrayBc, "TUBE-8");
        var specimen = Hl7ResultParser.Parse(bc, Bc).Specimens.Single();
        specimen.SpecimenId.Should().Be("TUBE-8");
        specimen.Results.Select(r => r.InstrumentTestCode).Should().Contain(["WBC", "RBC", "HGB", "PLT"]);
    }

    [Fact]
    public void TheFrameTester_OffersASample_OnAMindrayLink()
    {
        var mindray = MarocLIS.Application.Instruments.MessageFormatSettings.Default with { Dialect = Hl7Dialect.MindrayBc };

        MarocLIS.Application.Instruments.InstrumentInterfaceScreenRules.HasSample(InstrumentProtocol.Hl7V2, mindray).Should().BeTrue();
        MarocLIS.Application.Instruments.InstrumentInterfaceScreenRules
            .HasSample(InstrumentProtocol.Hl7V2, MarocLIS.Application.Instruments.MessageFormatSettings.Default).Should().BeFalse();
        MarocLIS.Application.Instruments.InstrumentInterfaceScreenRules
            .HasSample(InstrumentProtocol.RocheIntegra, MarocLIS.Application.Instruments.MessageFormatSettings.Default).Should().BeTrue();
    }

    // ---- The bench -----------------------------------------------------------------------------------

    private static async Task<Lis> LisAsync(Hl7Dialect dialect, InstrumentDirection direction, RecordingSink sink)
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        var link = new InstrumentInterface(Guid.NewGuid(), Guid.NewGuid(), InstrumentProtocol.Hl7V2, InstrumentTransportKind.Tcp)
        {
            ListenAsServer = true,
            TcpPort = port,
            Direction = direction,
            Hl7Dialect = dialect,
            Encoding = dialect == Hl7Dialect.MindrayBc ? "UTF-8" : "ISO-8859-1",
            FormatSpecimenIdField = dialect == Hl7Dialect.MindrayBc ? "OBR-3" : "OBR-2",
            FormatTestCodeComponent = dialect == Hl7Dialect.MindrayBc ? 2 : null
        };

        var transport = new TcpInstrumentTransport(link);
        var driver = new Hl7InstrumentDriver(link, transport, sink);
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = Task.Run(() => driver.RunAsync(stop.Token));

        await Task.CompletedTask;
        return new Lis(port, driver, stop, running);
    }

    private sealed class Lis(int port, Hl7InstrumentDriver driver, CancellationTokenSource stop, Task running) : IAsyncDisposable
    {
        public async Task<MindrayAnalyserSimulator> AnalyserAsync(MindrayInstrumentEmulator emulator)
        {
            var simulator = new MindrayAnalyserSimulator(emulator, "127.0.0.1", port);

            // The LIS starts listening on its own thread: try again until it does.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await simulator.ConnectAsync();
                    return simulator;
                }
                catch (SocketException) when (attempt < 50)
                {
                    await Task.Delay(100);
                }
            }
        }

        public async Task StopAsync()
        {
            await stop.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await driver.DisposeAsync();
            stop.Dispose();
        }
    }

    private sealed class RecordingSink : IInstrumentMessageSink
    {
        private readonly object _gate = new();

        public bool Accept { get; init; } = true;
        public List<InstrumentMessage> Handled { get; } = [];
        public List<string> Raw { get; } = [];
        public List<string> Queried { get; } = [];
        public Dictionary<string, InstrumentOrderDownload> Orders { get; } = [];

        public Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message, CancellationToken ct = default)
        {
            lock (_gate) Handled.Add(message);
            return Task.FromResult(Accept);
        }

        public Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
            Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default)
        {
            lock (_gate) Queried.AddRange(specimenIds);
            return Task.FromResult<IReadOnlyList<InstrumentOrderDownload>>(
                [.. specimenIds.Where(Orders.ContainsKey).Select(id => Orders[id])]);
        }

        public Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content,
                                CancellationToken ct = default)
        {
            lock (_gate) Raw.Add(content);
            return Task.CompletedTask;
        }

        public Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error,
                                               CancellationToken ct = default) => Task.CompletedTask;
    }
}
