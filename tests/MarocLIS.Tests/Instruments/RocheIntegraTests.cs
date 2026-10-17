using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Simulator;
using MarocLIS.Instruments.Transport;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// The COBAS INTEGRA host interface, against the examples of the Roche Host Interface Manual
/// (version 1.24): the block check, the block layout, the line layouts, the flags.
/// </summary>
public sealed class RocheIntegraProtocolTests
{
    private const char Soh = '\x01', Stx = '\x02', Etx = '\x03', Eot = '\x04';

    /// <summary>The manual's "message structure" example, line for line, with LF line ends.</summary>
    private static readonly string ManualExample =
        $"{Soh}\n09 COBAS INTEGRA    04\n{Stx}\n53 Order# 211044711 20/10/93 SER\n55 178\n" +
        $"00 +3.234000E+01 mg/dl 004 023 014 000\n{Etx}\n1\n562\n{Eot}\n";

    [Fact]
    public void Checksum_OfTheManualsExample_Is562()
    {
        var upToCounter = ManualExample[..(ManualExample.IndexOf("562", StringComparison.Ordinal))];

        RocheIntegra.Checksum(upToCounter).Should().Be(562,
            "the sum runs from SOH through the line end after the sequence counter, modulo 1000");
    }

    [Fact]
    public void ManualsExample_ReadsAsAValidPatientBlock()
    {
        var block = RocheIntegra.ParseFramed(ManualExample)!;

        block.InstrumentCode.Should().Be("09");
        block.Identifier.Should().Be("COBAS INTEGRA");
        block.BlockCode.Should().Be("04");
        block.SequenceCounter.Should().Be(1);
        block.SentChecksum.Should().Be(562);
        block.ChecksumValid.Should().BeTrue();
        block.Lines.Should().HaveCount(3);
    }

    [Fact]
    public void ADamagedDigit_FailsTheBlockCheck()
    {
        var damaged = ManualExample.Replace("+3.234000E+01", "+3.284000E+01");

        RocheIntegra.ParseFramed(damaged)!.ChecksumValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Build_ProducesABlockThatChecksOut_WithEitherLineEnd(string lineEnd)
    {
        var text = RocheIntegra.Build("14", "MAROCLIS", "09", ["10 01"], 1, lineEnd);

        text.Should().StartWith($"{Soh}{lineEnd}14 MAROCLIS         09{lineEnd}{Stx}{lineEnd}10 01{lineEnd}{Etx}{lineEnd}1{lineEnd}");
        text.Should().EndWith($"{Eot}{lineEnd}");

        var block = RocheIntegra.ParseFramed(text)!;
        block.ChecksumValid.Should().BeTrue();
        block.HasBlockCheck.Should().BeTrue();
        block.Lines.Should().Equal("10 01");
    }

    [Fact]
    public void Build_PadsASumUnderAHundredWithSpaces()
    {
        // Search a counter and line that sum to a two-digit value: the field stays three wide.
        var text = Enumerable.Range(0, 100).SelectMany(n => Enumerable.Range(0, 10).Select(d => (n, d)))
            .Select(x => RocheIntegra.Build("14", "X", "09", [$"10 {new string('A', x.n)}{x.d}"], 0))
            .First(t => RocheIntegra.ParseFramed(t)!.SentChecksum < 100);

        var lines = text.Split('\n');
        lines[^3].Should().HaveLength(3).And.StartWith(" ");
    }

    [Fact]
    public void SynchronisationBlock_HasNoBlockCheck()
    {
        var text = RocheIntegra.Build("14", "MAROCLIS", "00", [], sequenceCounter: null);

        text.Should().Be($"{Soh}\n14 MAROCLIS         00\n{Stx}\n{Etx}\n{Eot}\n");
        RocheIntegra.ParseFramed(text)!.HasBlockCheck.Should().BeFalse();
    }

    [Fact]
    public void Reader_SplitsAStreamIntoBlocks_AndDropsXonXoff()
    {
        var reader = new RocheIntegra.Reader(Encoding.Latin1);
        var first = RocheIntegra.Build("14", "A", "00", [], 0);
        var second = RocheIntegra.Build("14", "A", "04", ["55   1"], 1);
        var stream = "\x11noise" + first + second;

        reader.Feed(Encoding.Latin1.GetBytes(stream[..10]));
        reader.TryRead().Should().BeNull();

        reader.Feed(Encoding.Latin1.GetBytes(stream[10..] + "\x13"));
        reader.TryRead().Should().Be(first);
        reader.TryRead().Should().Be(second);
        reader.TryRead().Should().BeNull();
    }

    // ---- Results ------------------------------------------------------------------------------

    /// <summary>A block 04 in the v1.24 line layouts: lines 53, 55 and 00 as their pages print them.</summary>
    private static string PatientBlock(string resultLine, string test = "55   1",
                                       string order = "53 abcdef987654321 00/00/0000 URI") =>
        RocheIntegra.Build("14", "COBAS INTEGRA400", "04", [order, test, resultLine], 1);

    [Fact]
    public void PatientResult_IsReadWithItsBarcodeTestNumberValueAndUnit()
    {
        var message = IntegraMessageParser.Parse(PatientBlock(
            "00  8.694475E+01 U/l      0   0   0   0  0.000000E+00  0.000000E+00"));

        message.Kind.Should().Be(InstrumentMessageKind.Results);
        message.HasBlockingIssues.Should().BeFalse();

        var specimen = message.Specimens.Single();
        specimen.SpecimenId.Should().Be("abcdef987654321");
        specimen.IsControl.Should().BeFalse();

        var result = specimen.Results.Single();
        result.InstrumentTestCode.Should().Be("001", "test numbers are kept in their three-digit form");
        result.RawTestId.Should().Be("1");
        result.Value.Should().Be("86.94475");
        result.Unit.Should().Be("U/l");
        result.AbnormalFlag.Should().BeNull();
        result.Comments.Should().BeEmpty();
        result.ResultType.Should().Be("04");
        result.IsNotDone.Should().BeFalse();
    }

    [Fact]
    public void TheManualsOlderExample_IsReadToo()
    {
        var message = IntegraMessageParser.Parse(ManualExample);

        var result = message.Specimens.Single().Results.Single();
        result.InstrumentTestCode.Should().Be("178");
        result.Value.Should().Be("32.34");
        result.Unit.Should().Be("mg/dl");
        // Flags 004 / 023 / 014 / 000: X = 4 (replicate), CALC 14 (< REAG RNG).
        result.Comments.Should().Contain("X ->").And.Contain("< REAG RNG");
    }

    [Fact]
    public void AboveTheTestRange_IsCensoredAndNamed_WithTheLimit()
    {
        // The manual's "> TEST RNG" example: CALC flag 31 and the range limit.
        var message = IntegraMessageParser.Parse(PatientBlock(
            "00 10.694475E+01 U/l      0   0  31   0  1.500000E+02  10.00000E+00"));

        var result = message.Specimens.Single().Results.Single();
        result.Value.Should().Be("106.94475");
        result.AbnormalFlag.Should().Be(">");
        result.Comments.Should().Contain("> TEST RNG (150)");
    }

    [Fact]
    public void AResultTheFlagsPrevented_IsMarkedNotDone_NotStoredAsANumber()
    {
        var message = IntegraMessageParser.Parse(PatientBlock(
            "00 +9.999999E+99 mmol/l   0   0  17   0  0.000000E+00  0.000000E+00"));

        var result = message.Specimens.Single().Results.Single();
        result.Value.Should().BeNull();
        result.IsNotDone.Should().BeTrue();
        result.Comments.Should().Contain("HIGH ACT");
    }

    [Theory]
    [InlineData(13, "L")]
    [InlineData(14, "H")]
    [InlineData(21, "LL")]
    [InlineData(22, "HH")]
    public void TheReferenceRangeFlag_BecomesTheInterpretation(int qc, string expected)
    {
        var message = IntegraMessageParser.Parse(PatientBlock(
            $"00 +5.400000E+00 mmol/l   0   0   0  {qc}  0.000000E+00  0.000000E+00"));

        message.Specimens.Single().Results.Single().AbnormalFlag.Should().Be(expected);
    }

    [Fact]
    public void AResultWithNoUnit_KeepsItsFlagsInPlace()
    {
        var message = IntegraMessageParser.Parse(PatientBlock("00 +1.230000E+00   0   0   0  14"));

        var result = message.Specimens.Single().Results.Single();
        result.Unit.Should().BeNull();
        result.AbnormalFlag.Should().Be("H");
    }

    [Fact]
    public void AResultTheAnalyserDeleted_IsShownButNotImported()
    {
        var message = IntegraMessageParser.Parse(PatientBlock("00 +1.230000E+00 g/L      3   0   0   0"));

        var result = message.Specimens.Single().Results.Single();
        result.IsExcluded.Should().BeTrue();
        result.Comments.Should().Contain("X d");
    }

    [Fact]
    public void CollatedResults_GiveOneSpecimenWithEachTest()
    {
        var text = RocheIntegra.Build("14", "COBAS INTEGRA400", "04",
        [
            "53 TUBE-0001       17/10/2026 SER",
            "55  31",
            "00 +9.200000E-01 g/L      0   0   0   0",
            "55 445",
            "00 +1.050000E+01 mg/L     0   0   0   0",
            "53 TUBE-0002       17/10/2026 SER",
            "55   3",
            "00 +3.100000E-01 g/L      0   0   0   0"
        ], 0);

        var message = IntegraMessageParser.Parse(text);

        message.Specimens.Should().HaveCount(2);
        message.Specimens[0].SpecimenId.Should().Be("TUBE-0001");
        message.Specimens[0].Results.Select(r => (r.InstrumentTestCode, r.Value))
               .Should().Equal(("031", "0.92"), ("445", "10.5"));
        message.Specimens[1].Results.Single().InstrumentTestCode.Should().Be("003");
    }

    [Fact]
    public void ControlResult_GoesToQc_UnderTheControlsNameAndLot()
    {
        var text = RocheIntegra.Build("14", "COBAS INTEGRA400", "06",
        [
            "55  31",
            "01 22:07:59",
            "02 PNU",
            "12 362745 31/05/2021",
            "00 +5.300000E+00 mmol/l   0   0   0   0"
        ], 1);

        var specimen = IntegraMessageParser.Parse(text).Specimens.Single();

        specimen.IsControl.Should().BeTrue();
        specimen.SpecimenId.Should().Be("PNU");
        specimen.ControlIdentifier.Should().Be("PNU 362745");
        specimen.Results.Single().InstrumentTestCode.Should().Be("031");
    }

    [Fact]
    public void CalibrationResult_IsReadButNotImported()
    {
        var message = IntegraMessageParser.Parse(RocheIntegra.Build("14", "X", "02",
            ["55  31", "01 22:07:59", "03 C -1.929185E-02 -1.513617E-02"], 1));

        message.Kind.Should().Be(InstrumentMessageKind.Status);
        message.Specimens.Should().BeEmpty();
        message.Issues.Should().ContainSingle(i => i.ResourceKey == "Interface_IntegraCalibrationIgnored");
    }

    [Fact]
    public void TubesWithoutOrder_AreReadAsAQuery()
    {
        // The manual's line 42 example.
        var text = RocheIntegra.Build("14", "X", "62", ["42  21 25 1 AB(012:3456)    URI"], 1);

        var block = RocheIntegra.ParseFramed(text)!;
        var tube = RocheIntegra.Tubes(block).Single();
        tube.Barcode.Should().Be("AB(012:3456)");
        tube.Barcoded.Should().BeTrue();
        tube.SampleType.Should().Be("URI");

        var message = IntegraMessageParser.Parse(text);
        message.Kind.Should().Be(InstrumentMessageKind.Query);
        message.QueriedSpecimenIds.Should().Equal("AB(012:3456)");
    }

    [Fact]
    public void StoredText_ParsesBackToTheSameResults()
    {
        var original = IntegraMessageParser.Parse(PatientBlock(
            "00  8.694475E+01 U/l      0   0   0   0  0.000000E+00  0.000000E+00"));

        original.RawText.Should().Be(
            "14 COBAS INTEGRA400 04\r53 abcdef987654321 00/00/0000 URI\r55   1\r" +
            "00  8.694475E+01 U/l      0   0   0   0  0.000000E+00  0.000000E+00");

        // As InstrumentMessageSink.Reparse undoes it for this protocol.
        var replayed = IntegraMessageParser.Parse(original.RawText!, InstrumentMessageFormat.Auto);

        replayed.Specimens.Single().SpecimenId.Should().Be("abcdef987654321");
        replayed.Specimens.Single().Results.Single().Value.Should().Be("86.94475");
    }

    [Fact]
    public void APastedTrace_WithTheControlCharactersWrittenOut_IsReadAndChecked()
    {
        var trace = ManualExample.Replace("\x01", "<SOH>").Replace("\x02", "<STX>")
                                 .Replace("\x03", "<ETX>").Replace("\x04", "<EOT>")
                                 .Replace("\n", "<LF>\r\n");

        var normalised = InstrumentMessagePreviewer.Normalise(InstrumentProtocol.RocheIntegra, trace);
        var message = IntegraMessageParser.Parse(normalised);

        message.Issues.Should().BeEmpty("the sum still checks out once the trace is turned back into bytes");
        message.Specimens.Single().Results.Single().InstrumentTestCode.Should().Be("178");
    }

    [Fact]
    public void ASampleTranscript_FromTheSimulator_ReadsInTheTester()
    {
        var transcript = IntegraInstrumentEmulator.SampleTranscript("TUBE-0001", [31, 445]);

        var message = IntegraMessageParser.Parse(
            InstrumentMessagePreviewer.Normalise(InstrumentProtocol.RocheIntegra, transcript));

        message.Issues.Should().BeEmpty();
        message.Specimens.Should().HaveCount(2).And.OnlyContain(s => s.SpecimenId == "TUBE-0001");
        message.Specimens.SelectMany(s => s.Results).Select(r => r.InstrumentTestCode)
               .Should().Equal("031", "445");
    }

    [Fact]
    public void ATranscriptWhoseLineEndsATextBoxTurnedIntoCrLf_StillChecks()
    {
        // The sample as the tester's text box holds it: every line break a CR LF.
        var boxed = IntegraInstrumentEmulator.SampleTranscript("TUBE-0001", [31])
                                             .Replace("<LF>", "").Replace("\r\n", "\n").Replace("\n", "\r\n");

        IntegraMessageParser.Parse(InstrumentMessagePreviewer.Normalise(InstrumentProtocol.RocheIntegra, boxed))
                            .Issues.Should().BeEmpty();

        // A damaged value is still caught either way.
        var damaged = boxed.Replace("E-01", "E-02").Replace("E+00 g", "E+01 g");
        IntegraMessageParser.Parse(InstrumentMessagePreviewer.Normalise(InstrumentProtocol.RocheIntegra, damaged))
                            .Issues.Should().Contain(i => i.ResourceKey == "Interface_IntegraChecksum");
    }

    [Fact]
    public void TextThatIsNotABlock_IsRefused()
    {
        IntegraMessageParser.Parse("H|\\^&|||ASTM").HasBlockingIssues.Should().BeTrue();
    }

    [Fact]
    public void AcceptedResultTypes_FilterByBlock()
    {
        var format = new InstrumentMessageFormat { AcceptedResultTypes = "07" };

        var message = IntegraMessageParser.Parse(PatientBlock("00 +1.230000E+00 g/L      0   0   0   0"), format);

        message.Specimens.Single().Results.Single().IsExcluded.Should().BeTrue();
    }

    // ---- Orders --------------------------------------------------------------------------------

    [Theory]
    [InlineData("031", 31)]
    [InlineData("31", 31)]
    [InlineData("0-031", 31)]
    [InlineData(" 445 ", 445)]
    [InlineData("GLUC3", null)]
    [InlineData("0", null)]
    [InlineData("1000", null)]
    public void TestNumber_IsReadFromTheMappingCode(string code, int? expected) =>
        RocheIntegra.TestNumber(code).Should().Be(expected);

    [Fact]
    public void OrderEntry_LaysOutLines53_54_55()
    {
        var order = new InstrumentOrderDownload
        {
            SpecimenId = "TUBE-0001",
            InstrumentTestCodes = ["031", "0-445", "GLUC3", "031"],
            Priority = "R"
        };

        var blocks = RocheIntegra.OrderEntry(order, "SER", "A", zeroPadIntegers: false, out var skipped);

        skipped.Should().Equal("GLUC3");
        blocks.Single().Should().Equal(
            "53 TUBE-0001       00/00/0000 SER",
            "54   0  0 A",
            "55  31",
            "55 445");
    }

    [Fact]
    public void OrderEntry_IsStat_WhenTheOrderIs_AndSplitsPastFiftyTests()
    {
        var order = new InstrumentOrderDownload
        {
            SpecimenId = "T",
            InstrumentTestCodes = [.. Enumerable.Range(1, 60).Select(n => n.ToString())],
            Priority = "S"
        };

        var blocks = RocheIntegra.OrderEntry(order, null, "A", zeroPadIntegers: true, out _);

        blocks.Should().HaveCount(2);
        blocks[0][0].Should().Be("53 T               00/00/0000");
        blocks[0][1].Should().Be("54 000 00 S");
        blocks[0].Count(l => l.StartsWith("55", StringComparison.Ordinal)).Should().Be(50);
        blocks[1][0].Should().StartWith("53 T");
        blocks[1].Should().NotContain(l => l.StartsWith("54", StringComparison.Ordinal),
            "tests added to an existing order carry no line 54");
        blocks[1].Should().HaveCount(11);
    }
}

/// <summary>
/// The driver against the simulated analyser: the host polls, the analyser answers, and the driver
/// keeps the counter and the retransmissions the way the manual describes.
/// </summary>
public sealed class RocheIntegraDriverTests
{
    private static InstrumentInterface Link(InstrumentDirection direction = InstrumentDirection.Unidirectional,
                                            InstrumentTransportKind transport = InstrumentTransportKind.Serial) =>
        new(Guid.NewGuid(), Guid.NewGuid(), InstrumentProtocol.RocheIntegra, transport)
        {
            Direction = direction,
            AcknowledgementTimeoutSeconds = 2,
            MaxRetries = 2
        };

    [Fact]
    public async Task ARound_FetchesEveryWaitingResult_ThenStops()
    {
        var emulator = new IntegraInstrumentEmulator(seed: 1);
        emulator.QueuePatientResult("TUBE-0001", 31, 0.92m, "g/L");
        emulator.QueuePatientResult("TUBE-0001", 445, 10.5m, "mg/L");
        emulator.QueueControlResult("PNU", 31, 5.3m, "mmol/l");

        var sink = new RecordingSink();
        await using var driver = new RocheIntegraInstrumentDriver(Link(), new EmulatedLine(emulator), sink);

        await driver.RoundAsync();

        emulator.PendingResults.Should().Be(0);
        sink.Handled.Should().HaveCount(3);
        sink.Handled[0].Specimens.Single().Results.Single().Should().Match<InstrumentResult>(
            r => r.InstrumentTestCode == "031" && r.Value == "0.92" && r.Unit == "g/L");
        sink.Handled[2].Specimens.Single().IsControl.Should().BeTrue();

        // Synchronisation, four result requests (the last answered 00), no service request.
        emulator.ReceivedBlockCodes.Should().Equal("00", "09", "09", "09", "09");
    }

    [Fact]
    public async Task HostQuery_SendsTheOrderOfEachWaitingTube_AndCollectsItsResultsNextRound()
    {
        var emulator = new IntegraInstrumentEmulator(seed: 2);
        emulator.TubesWithoutOrder.Add(("TUBE-0007", "SER"));
        emulator.TubesWithoutOrder.Add(("UNKNOWN-1", "SER"));

        var sink = new RecordingSink
        {
            Orders =
            {
                ["TUBE-0007"] = new InstrumentOrderDownload
                {
                    SpecimenId = "TUBE-0007", InstrumentTestCodes = ["031", "445"], Priority = "R"
                }
            }
        };

        await using var driver = new RocheIntegraInstrumentDriver(
            Link(InstrumentDirection.HostQueryDownload), new EmulatedLine(emulator), sink);

        await driver.RoundAsync();

        sink.Queried.Should().Equal("TUBE-0007", "UNKNOWN-1");
        emulator.ReceivedOrders.Single().Should().Equal(
            "53 TUBE-0007       00/00/0000 SER", "54   0  0 A", "55  31", "55 445");
        emulator.TubesWithoutOrder.Select(t => t.Barcode).Should().Equal("UNKNOWN-1");

        await driver.RoundAsync();

        sink.Handled.SelectMany(m => m.Specimens).Should().OnlyContain(s => s.SpecimenId == "TUBE-0007");
        sink.Handled.SelectMany(m => m.Specimens).SelectMany(s => s.Results).Select(r => r.InstrumentTestCode)
            .Should().Equal("031", "445");

        // The unknown tube is not asked about again in the next round.
        sink.Queried.Should().HaveCount(2);
    }

    [Fact]
    public async Task ADamagedAnswer_IsAskedForAgain_WithTheSameCounter()
    {
        var emulator = new IntegraInstrumentEmulator(seed: 3);
        emulator.QueuePatientResult("TUBE-0001", 31, 0.92m, "g/L");

        var line = new EmulatedLine(emulator) { DamageAnswer = 2 };   // the first result block
        var sink = new RecordingSink();
        await using var driver = new RocheIntegraInstrumentDriver(Link(), line, sink);

        await driver.RoundAsync();

        sink.Handled.Should().ContainSingle("the repeated answer is the same result, taken once");
        sink.Raw.Should().Contain(r => r.StartsWith("[CHECKSUM FAILED]", StringComparison.Ordinal));

        // The second and third requests carry the same counter: a retransmission, not a new request.
        var counters = line.Requests.Select(r => RocheIntegra.ParseFramed(r)!.SequenceCounter).ToList();
        counters[1].Should().Be(counters[2]);
        counters[3].Should().NotBe(counters[2]);
    }

    [Fact]
    public async Task ARequestTheAnalyserReadBadly_IsSentAgain()
    {
        var emulator = new IntegraInstrumentEmulator(seed: 4);
        emulator.QueuePatientResult("TUBE-0001", 31, 0.92m, "g/L");

        var line = new EmulatedLine(emulator) { DamageRequest = 2 };   // the first result request
        var sink = new RecordingSink();
        await using var driver = new RocheIntegraInstrumentDriver(Link(), line, sink);

        await driver.RoundAsync();

        sink.Raw.Should().Contain(r => r.Contains("99 01"), "the analyser answered block 99, error 01");
        sink.Handled.Should().ContainSingle();
    }

    [Fact]
    public async Task ASilentAnalyser_EndsTheRound_AndIsSynchronisedNextTime()
    {
        var emulator = new IntegraInstrumentEmulator(seed: 5);
        var line = new EmulatedLine(emulator) { Silent = true };
        var sink = new RecordingSink();
        var link = Link();
        link.AcknowledgementTimeoutSeconds = 1;
        link.MaxRetries = 1;

        await using var driver = new RocheIntegraInstrumentDriver(link, line, sink);

        await driver.RoundAsync();
        line.Silent = false;
        emulator.QueuePatientResult("TUBE-0001", 31, 0.92m, "g/L");
        await driver.RoundAsync();

        sink.States.Should().Contain(s => s.Contains("No answer to block 09"));
        sink.Handled.Should().ContainSingle();

        // Synchronisation, the request and its retry unanswered; synchronised again next round.
        line.Requests.Select(r => RocheIntegra.ParseFramed(r)!.BlockCode)
            .Should().StartWith(["00", "09", "09", "00", "09"]);
    }

    [Fact]
    public async Task RoundTrip_OverTcp_ThroughTheSimulator()
    {
        var emulator = new IntegraInstrumentEmulator(seed: 6) { LineEnd = "\r\n" };
        emulator.TubesWithoutOrder.Add(("TUBE-0042", null));

        await using var simulator = new IntegraAnalyserSimulator(emulator);
        simulator.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = simulator.RunAsync(stop.Token);

        var link = Link(InstrumentDirection.HostQueryDownload, InstrumentTransportKind.Tcp);
        link.Host = "127.0.0.1";
        link.TcpPort = simulator.Port;

        var sink = new RecordingSink
        {
            Orders = { ["TUBE-0042"] = new InstrumentOrderDownload { SpecimenId = "TUBE-0042", InstrumentTestCodes = ["031"] } }
        };

        await using (var transport = new TcpInstrumentTransport(link))
        await using (var driver = new RocheIntegraInstrumentDriver(link, transport, sink,
                         options: new IntegraHostOptions { LineTerminator = "CRLF" }))
        {
            (await transport.ConnectAsync()).Should().BeTrue();

            await driver.RoundAsync(stop.Token);   // the order goes down
            await driver.RoundAsync(stop.Token);   // its result comes up
        }

        stop.Cancel();
        await serving;

        var result = sink.Handled.SelectMany(m => m.Specimens).Single();
        result.SpecimenId.Should().Be("TUBE-0042");
        result.Results.Single().InstrumentTestCode.Should().Be("031");
        decimal.Parse(result.Results.Single().Value!, System.Globalization.CultureInfo.InvariantCulture)
               .Should().BeInRange(0.70m, 1.10m);
    }

    [Fact]
    public async Task APushedWorklist_IsSentInTheNextRound()
    {
        var emulator = new IntegraInstrumentEmulator(seed: 8);
        var sink = new RecordingSink();
        await using var driver = new RocheIntegraInstrumentDriver(
            Link(InstrumentDirection.Bidirectional), new EmulatedLine(emulator), sink);

        await driver.SendOrdersAsync([new InstrumentOrderDownload { SpecimenId = "TUBE-9", InstrumentTestCodes = ["3"] }]);
        await driver.RoundAsync();

        emulator.ReceivedOrders.Single().Should().Contain("55   3");
        sink.Raw.Should().Contain(r => r.Contains("96 00"), "the order's acceptance is on record");
    }

    /// <summary>A serial line with the emulated analyser at its end, answering every block at once.</summary>
    private sealed class EmulatedLine(IntegraInstrumentEmulator emulator) : IInstrumentTransport
    {
        private readonly ConcurrentQueue<byte[]> _incoming = new();
        private readonly RocheIntegra.Reader _reader = new(Encoding.Latin1);
        private int _answers;

        public List<string> Requests { get; } = [];

        /// <summary>Which answer (1-based) arrives with a damaged digit.</summary>
        public int? DamageAnswer { get; init; }

        /// <summary>Which request (1-based) reaches the analyser with a damaged digit.</summary>
        public int? DamageRequest { get; init; }

        public bool Silent { get; set; }

        public bool IsConnected => true;
        public string Description => "emulated INTEGRA";
        public event EventHandler<string>? Disconnected { add { } remove { } }

        public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;

        public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct = default)
        {
            byte[]? bytes;
            while (!_incoming.TryDequeue(out bytes))
            {
                try { await Task.Delay(5, ct); }
                catch (OperationCanceledException) { return 0; }
            }

            bytes.CopyTo(buffer, 0);
            return bytes.Length;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            _reader.Feed(data.Span);

            while (_reader.TryRead() is { } block)
            {
                Requests.Add(block);
                if (Silent) continue;

                var received = Requests.Count == DamageRequest ? Damage(block) : block;
                var answer = emulator.Respond(received);
                if (answer is null) continue;

                _answers++;
                if (_answers == DamageAnswer) answer = Damage(answer);

                _incoming.Enqueue(Encoding.Latin1.GetBytes(answer));
            }

            return Task.CompletedTask;
        }

        /// <summary>One character changed inside the data, as line noise would.</summary>
        private static string Damage(string block)
        {
            var at = block.IndexOf('\x02') + 3;
            var chars = block.ToCharArray();
            chars[at] = chars[at] == '1' ? '2' : '1';
            return new string(chars);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : IInstrumentMessageSink
    {
        public List<InstrumentMessage> Handled { get; } = [];
        public List<string> Raw { get; } = [];
        public List<string> Queried { get; } = [];
        public List<string> States { get; } = [];
        public Dictionary<string, InstrumentOrderDownload> Orders { get; } = [];

        public Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message, CancellationToken ct = default)
        {
            Handled.Add(message);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
            Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default)
        {
            Queried.AddRange(specimenIds);
            return Task.FromResult<IReadOnlyList<InstrumentOrderDownload>>(
                [.. specimenIds.Where(Orders.ContainsKey).Select(id => Orders[id])]);
        }

        public Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content,
                                CancellationToken ct = default)
        {
            Raw.Add(content);
            return Task.CompletedTask;
        }

        public Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error,
                                               CancellationToken ct = default)
        {
            if (error is not null) States.Add(error);
            return Task.CompletedTask;
        }
    }
}
