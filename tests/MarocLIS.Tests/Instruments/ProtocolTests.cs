using System.Text;
using FluentAssertions;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Protocol;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// ASTM E1381 link-layer tests: framing and the checksum.
/// <para>
/// The checksum is the single most commonly mis-implemented part of an analyser integration — it
/// covers the bytes <em>after</em> STX up to and <em>including</em> the terminator, modulo 256, in
/// two upper-case hexadecimal characters. Getting it wrong means either rejecting every good frame
/// or, worse, accepting a corrupted one and storing a wrong result.
/// </para>
/// </summary>
public sealed class AstmFramingTests
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    [Fact]
    public void Checksum_CoversTheFrameNumberPayloadAndTerminator()
    {
        // Worked by hand: '1' (0x31) + 'H' (0x48) + ETX (0x03) = 0x7C.
        var body = new byte[] { (byte)'1', (byte)'H' };

        AstmControl.Checksum(body, AstmControl.Etx).Should().Be("7C");
    }

    [Fact]
    public void Checksum_WrapsAtTwoHundredAndFiftySix()
    {
        // Sums above 0xFF must be taken modulo 256 and still render as two hex digits.
        var body = Enumerable.Repeat((byte)0xFF, 3).ToArray();     // 765
        var checksum = AstmControl.Checksum(body, AstmControl.Etx); // 765 + 3 = 768 -> 0x00

        checksum.Should().Be("00");
        checksum.Length.Should().Be(2);
    }

    [Fact]
    public void BuildFrame_ProducesTheExpectedByteSequence()
    {
        var frame = AstmControl.BuildFrame(1, "H|\\^&", isLast: true, Latin1);

        // E1381 frame tail: <terminator> C1 C2 <CR> <LF> — five bytes, so the terminator is at [^5]
        // and the two checksum characters at [^4] and [^3].
        frame[0].Should().Be(AstmControl.Stx);
        frame[1].Should().Be((byte)'1');
        frame[^5].Should().Be(AstmControl.Etx);
        frame[^2].Should().Be(AstmControl.Cr);
        frame[^1].Should().Be(AstmControl.Lf);
    }

    [Fact]
    public void FrameNumbers_CycleThroughSevenAndBackToZero()
    {
        var numbers = new List<int> { 1 };
        for (var i = 0; i < 8; i++) numbers.Add(AstmControl.NextFrameNumber(numbers[^1]));

        numbers.Should().Equal(1, 2, 3, 4, 5, 6, 7, 0, 1);
    }

    [Fact]
    public void Reader_AcceptsAValidFrameAndSplitsItsRecords()
    {
        var frame = AstmControl.BuildFrame(1, "P|1\rO|1|SPEC001\r", isLast: true, Latin1);

        var reader = new AstmFrameReader(Latin1);
        reader.Feed(frame);

        var read = reader.TryReadFrame();

        read.Should().NotBeNull();
        read!.ChecksumValid.Should().BeTrue();
        read.FrameNumber.Should().Be(1);
        read.IsLast.Should().BeTrue();
        read.Records.Should().Equal("P|1", "O|1|SPEC001");
    }

    [Fact]
    public void Reader_DetectsACorruptedChecksum()
    {
        // This is what a noisy RS-232 run actually produces, and the driver has to NAK it.
        var frame = AstmControl.BuildFrame(1, "P|1\r", isLast: true, Latin1).ToArray();
        var checksumIndex = frame.Length - 3;
        frame[checksumIndex] = frame[checksumIndex] == (byte)'0' ? (byte)'1' : (byte)'0';

        var reader = new AstmFrameReader(Latin1);
        reader.Feed(frame);

        reader.TryReadFrame()!.ChecksumValid.Should().BeFalse();
    }

    [Fact]
    public void Reader_ReassemblesAFrameSplitAcrossTwoReads()
    {
        // Serial and TCP both deliver arbitrary chunks; a reader that assumed one read equals one
        // frame would drop most real traffic.
        var frame = AstmControl.BuildFrame(1, "P|1\r", isLast: true, Latin1);
        var reader = new AstmFrameReader(Latin1);

        reader.Feed(frame.AsSpan(0, 4));
        reader.TryReadFrame().Should().BeNull("the frame is still incomplete");

        reader.Feed(frame.AsSpan(4));
        reader.TryReadFrame()!.ChecksumValid.Should().BeTrue();
    }

    [Fact]
    public void Reader_HandlesTwoFramesArrivingInOneRead()
    {
        var first = AstmControl.BuildFrame(1, "P|1\r", isLast: false, Latin1);
        var second = AstmControl.BuildFrame(2, "O|1|SPEC\r", isLast: true, Latin1);

        var reader = new AstmFrameReader(Latin1);
        reader.Feed([.. first, .. second]);

        reader.TryReadFrame()!.FrameNumber.Should().Be(1);
        reader.TryReadFrame()!.FrameNumber.Should().Be(2);
        reader.TryReadFrame().Should().BeNull();
    }

    [Fact]
    public void Reader_QueuesControlCharactersSeenOutsideAFrame()
    {
        var reader = new AstmFrameReader(Latin1);
        reader.Feed([AstmControl.Enq]);

        reader.TryReadFrame().Should().BeNull();
        reader.TryDequeueControl().Should().Be(AstmControl.Enq);
        reader.TryDequeueControl().Should().BeNull();
    }

    [Fact]
    public void Reader_HandsOverALoneControlCharacterWithoutAFrameScan()
    {
        // The driver asks for controls first. A lone ENQ (the analyser bidding for the line) or
        // ACK (the analyser accepting ours) must be seen straight away, not one read later.
        var reader = new AstmFrameReader(Latin1);
        reader.Feed([AstmControl.Enq]);

        reader.TryDequeueControl().Should().Be(AstmControl.Enq);
        reader.TryDequeueControl().Should().BeNull();
    }

    [Fact]
    public void Reader_LeavesAFrameInProgressAloneWhenAskedForControls()
    {
        var frame = AstmControl.BuildFrame(1, "P|1\r", isLast: true, Latin1);

        var reader = new AstmFrameReader(Latin1);
        reader.Feed([AstmControl.Enq, .. frame, AstmControl.Eot]);

        reader.TryDequeueControl().Should().Be(AstmControl.Enq);
        reader.TryDequeueControl().Should().BeNull();
        reader.TryReadFrame()!.ChecksumValid.Should().BeTrue();
        reader.TryDequeueControl().Should().Be(AstmControl.Eot);
    }

    [Fact]
    public void Reader_PreservesControlCharactersThatPrecedeAFrame()
    {
        // ENQ then a frame in one read: the control must not be swallowed by the frame scan, or
        // the driver never grants the line and the analyser times out.
        var frame = AstmControl.BuildFrame(1, "P|1\r", isLast: true, Latin1);

        var reader = new AstmFrameReader(Latin1);
        reader.Feed([AstmControl.Enq, .. frame]);

        reader.TryReadFrame().Should().NotBeNull();
        reader.TryDequeueControl().Should().Be(AstmControl.Enq);
    }

    [Fact]
    public void BuildFrames_SplitsALongMessageAcrossFrames()
    {
        var records = Enumerable.Range(1, 40)
            .Select(i => $"R|{i}|^^^TEST{i}|123.45|mg/L|1-2|N||F")
            .ToList();

        var frames = AstmControl.BuildFrames(records, Latin1);

        frames.Count.Should().BeGreaterThan(1);

        // Only the last frame may be terminated with ETX; the rest use ETB.
        for (var i = 0; i < frames.Count - 1; i++)
            frames[i][^5].Should().Be(AstmControl.Etb);

        frames[^1][^5].Should().Be(AstmControl.Etx);
    }
}

/// <summary>ASTM E1394 record parsing.</summary>
public sealed class AstmMessageParserTests
{
    private static IReadOnlyList<string> ResultMessage() =>
    [
        "H|\\^&|||ARCHITECT^1.0|||||||P|1|20260915103000",
        "P|1||P-00000042||BENANI^YOUSSEF||19850315|M",
        "O|1|SPEC001||^^^GLY\\^^^CREAT|R|20260915100000|20260915093000||||N|||20260915095000|",
        "R|1|^^^GLY|1.05|g/L|0.70 to 1.10|N||F||OP1|20260915102000|20260915102500|ARCHITECT",
        "R|2|^^^CREAT|18.5|mg/L|7.0 to 13.0|H||F||OP1|20260915102000|20260915102500|ARCHITECT",
        "C|1|I|Résultat vérifié sur dilution|I",
        "L|1|N"
    ];

    [Fact]
    public void Parse_ExtractsTheSpecimenAndItsResults()
    {
        var message = AstmMessageParser.Parse(ResultMessage());

        message.Kind.Should().Be(InstrumentMessageKind.Results);
        message.SenderId.Should().Be("ARCHITECT");
        message.SenderVersion.Should().Be("1.0");

        var specimen = message.Specimens.Should().ContainSingle().Subject;
        specimen.SpecimenId.Should().Be("SPEC001");
        specimen.PatientId.Should().Be("P-00000042");
        specimen.IsControl.Should().BeFalse();
        specimen.Results.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_KeepsTheCobasTestCodeAsTransmitted()
    {
        // Captured from a cobas c 311: the code is followed by "/" and the dilution. The parser
        // keeps it whole; the mapping lookup is what reads past the slash.
        var message = AstmMessageParser.Parse(new[] {
            @"H|\^&|||cobas c 311^1|||||host|RSUPL^REAL|P|1",
            "P|1",
            @"O|1|0000000040|1^50001^001^^S1^SC|^^^GLY^|R||||||N||||1||||||20261014022702|||F",
            "R|1|^^^GLY/|62.17|||N||F||admin|||P1",
            "R|2|^^^767/5|4.1||||||F",
            "L|1|N" });

        message.Specimens.Single().Results.Select(r => r.InstrumentTestCode)
               .Should().Equal("GLY/", "767/5");
    }

    [Theory]
    [InlineData("GLY/", "GLY")]
    [InlineData("767/5", "767")]
    [InlineData("A/G", "A/G")]
    [InlineData("GLY", "GLY")]
    public void FindMapping_ReadsPastACobasDilutionButKeepsACodeWithASlash(string sent, string expected)
    {
        var parameter = Guid.NewGuid();
        var mappings = new[] { "GLY", "767", "A/G", "A" }
            .Select(code => new MarocLIS.Domain.Equipment.InstrumentTestMapping(code, parameter))
            .ToList();

        MarocLIS.Instruments.Hosting.InstrumentMessageSink.FindMapping(mappings, sent)!
            .InstrumentTestCode.Should().Be(expected);
    }

    [Fact]
    public void FindMapping_IgnoresASwitchedOffCode()
    {
        var mapping = new MarocLIS.Domain.Equipment.InstrumentTestMapping("GLY", Guid.NewGuid()) { IsEnabled = false };

        MarocLIS.Instruments.Hosting.InstrumentMessageSink.FindMapping([mapping], "GLY/").Should().BeNull();
    }

    [Fact]
    public void Parse_ReadsTheTestCodeFromTheFourthComponent()
    {
        // The standard puts the local code in component four of the universal test identifier.
        var message = AstmMessageParser.Parse(ResultMessage());

        message.Specimens.Single().Results.Select(r => r.InstrumentTestCode)
               .Should().Equal("GLY", "CREAT");
    }

    [Fact]
    public void Parse_FallsBackToTheFirstComponentWhenTheFourthIsEmpty()
    {
        // A good share of analysers send the code in component one instead. Rejecting those would
        // mean results sitting unreported on the machine.
        IReadOnlyList<string> records =
        [
            "H|\\^&|||VENDOR|||||||P|1|20260915103000",
            "O|1|SPEC002||GLU|R|20260915100000",
            "R|1|GLU|5.4|mmol/L|||N||F",
            "L|1|N"
        ];

        var message = AstmMessageParser.Parse(records);

        message.Specimens.Single().Results.Single().InstrumentTestCode.Should().Be("GLU");
    }

    [Fact]
    public void Parse_AttachesACommentToThePrecedingResult()
    {
        var message = AstmMessageParser.Parse(ResultMessage());

        var results = message.Specimens.Single().Results;
        var last = results[^1];
        last.Comments.Should().ContainSingle()
            .Which.Should().Contain("dilution");
    }

    [Fact]
    public void Parse_PreservesTheAbnormalFlagAsTransmitted()
    {
        var message = AstmMessageParser.Parse(ResultMessage());

        message.Specimens.Single().Results
               .Single(r => r.InstrumentTestCode == "CREAT").AbnormalFlag.Should().Be("H");
    }

    [Fact]
    public void Parse_RecognisesAQualityControlSpecimenFromTheActionCode()
    {
        // Action code "Q" in field 12. Telling control from patient material at the wire boundary
        // is what stops a control value landing in a patient's file.
        IReadOnlyList<string> records =
        [
            "H|\\^&|||ARCHITECT|||||||P|1|20260915103000",
            "P|1",
            "O|1|QC-LOT-882||^^^GLY|R|20260915100000|||||Q|||20260915095000|CONTROL-NORMAL",
            "R|1|^^^GLY|1.02|g/L|||N||F",
            "L|1|N"
        ];

        var message = AstmMessageParser.Parse(records);
        var specimen = message.Specimens.Single();

        specimen.IsControl.Should().BeTrue();
        specimen.ControlIdentifier.Should().Be("CONTROL-NORMAL");
    }

    [Fact]
    public void Parse_RecognisesAControlSpecimenFromTheDescriptorAlone()
    {
        // Some analysers signal QC only in the specimen descriptor, not the action code.
        IReadOnlyList<string> records =
        [
            "H|\\^&|||VENDOR|||||||P|1|20260915103000",
            "O|1|SPEC003||^^^GLY|R|20260915100000|||||N|||20260915095000|QC LEVEL 2",
            "R|1|^^^GLY|1.02|g/L|||N||F",
            "L|1|N"
        ];

        AstmMessageParser.Parse(records).Specimens.Single().IsControl.Should().BeTrue();
    }

    [Fact]
    public void Parse_TreatsAMissingSpecimenIdentifierAsBlocking()
    {
        // Without it there is no way to bind the results to a patient, so the message is unusable.
        IReadOnlyList<string> records =
        [
            "H|\\^&|||VENDOR|||||||P|1|20260915103000",
            "O|1|||^^^GLY|R",
            "R|1|^^^GLY|1.02|g/L|||N||F",
            "L|1|N"
        ];

        var message = AstmMessageParser.Parse(records);

        message.HasBlockingIssues.Should().BeTrue();
        message.Issues.Should().Contain(i => i.ResourceKey == "Instruments_MissingSpecimenId");
    }

    [Fact]
    public void Parse_TreatsAnUnknownRecordTypeAsANonBlockingWarning()
    {
        // Firmware sends records the standard does not define. Rejecting the whole transmission
        // over one of them would be the wrong trade.
        IReadOnlyList<string> records =
        [
            "H|\\^&|||VENDOR|||||||P|1|20260915103000",
            "O|1|SPEC004||^^^GLY|R",
            "R|1|^^^GLY|1.02|g/L|||N||F",
            "Z|1|vendor specific",
            "L|1|N"
        ];

        var message = AstmMessageParser.Parse(records);

        message.HasBlockingIssues.Should().BeFalse();
        message.Specimens.Single().Results.Should().HaveCount(1);
        message.Issues.Should().Contain(i => i.ResourceKey == "Instruments_UnknownRecordType");
    }

    [Fact]
    public void Parse_RecognisesAHostQuery()
    {
        IReadOnlyList<string> records =
        [
            "H|\\^&|||ARCHITECT|||||||P|1|20260915103000",
            "Q|1|^SPEC005||ALL||||||||O",
            "L|1|N"
        ];

        var message = AstmMessageParser.Parse(records);

        message.Kind.Should().Be(InstrumentMessageKind.Query);
        message.QueriedSpecimenIds.Should().Equal("SPEC005");
    }

    [Fact]
    public void Parse_ReadsNonDefaultDelimitersFromTheHeader()
    {
        // The standard allows them to be redefined, and a few analysers do.
        IReadOnlyList<string> records =
        [
            "H#!~\\&#||#VENDOR#||||||#P#1#20260915103000",
            "O#1#SPEC006##~~~GLY#R",
            "R#1#~~~GLY#1.02#g/L###N##F",
            "L#1#N"
        ];

        var message = AstmMessageParser.Parse(records);

        message.Specimens.Should().ContainSingle();
        message.Specimens.Single().SpecimenId.Should().Be("SPEC006");
        message.Specimens.Single().Results.Single().InstrumentTestCode.Should().Be("GLY");
    }

    [Theory]
    [InlineData("20260915103045", 2026, 9, 15, 10, 30, 45)]
    [InlineData("202609151030", 2026, 9, 15, 10, 30, 0)]
    [InlineData("2026091510", 2026, 9, 15, 10, 0, 0)]
    [InlineData("20260915", 2026, 9, 15, 0, 0, 0)]
    public void ParseTimestamp_AcceptsTheTruncatedFormsAnalysersSend(
        string value, int year, int month, int day, int hour, int minute, int second)
    {
        var parsed = AstmMessageParser.ParseTimestamp(value);

        parsed.Should().NotBeNull();
        parsed!.Value.Year.Should().Be(year);
        parsed.Value.Month.Should().Be(month);
        parsed.Value.Day.Should().Be(day);
        parsed.Value.Hour.Should().Be(hour);
        parsed.Value.Minute.Should().Be(minute);
        parsed.Value.Second.Should().Be(second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void ParseTimestamp_ReturnsNullForUnusableInput(string value) =>
        AstmMessageParser.ParseTimestamp(value).Should().BeNull();

    [Fact]
    public void BuildOrderDownload_EmitsAParsableRoundTrip()
    {
        var order = new InstrumentOrderDownload
        {
            SpecimenId = "SPEC007",
            InstrumentTestCodes = ["GLY", "CREAT", "UREE"],
            PatientId = "P-00000099",
            PatientName = "BENANI^YOUSSEF",
            Sex = "M",
            Priority = "R"
        };

        var records = AstmMessageParser.BuildOrderDownload(
            [order], "MAROCLIS", new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero));

        records[0].Should().StartWith("H|");
        records[^1].Should().Be("L|1|N");

        // The three test codes repeat within the universal test identifier field.
        var orderRecord = records.Single(r => r.StartsWith("O|", StringComparison.Ordinal));
        orderRecord.Should().Contain("^^^GLY");
        orderRecord.Should().Contain("^^^CREAT");
        orderRecord.Should().Contain("^^^UREE");
    }

    [Fact]
    public void BuildNoInformation_TellsTheAnalyserToMoveOn()
    {
        var records = AstmMessageParser.BuildNoInformation(
            "MAROCLIS", new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));

        records.Should().HaveCount(2);
        records[0].Should().StartWith("H|");
        records[^1].Should().Be("L|1|I");
    }

    [Fact]
    public void Parse_OfAnEmptyMessage_IsBlocking()
    {
        var message = AstmMessageParser.Parse([]);

        message.Kind.Should().Be(InstrumentMessageKind.Unsupported);
        message.Issues.Should().Contain(i => i.ResourceKey == "Instruments_EmptyMessage");
    }
}

/// <summary>
/// HL7 v2 parsing tests.
/// <para>
/// The MSH field-offset quirk gets its own tests: MSH-1 <em>is</em> the field separator, so MSH-9
/// is not the ninth token after splitting. Getting that wrong shifts every MSH field by one, and
/// the symptom is a message type that never matches.
/// </para>
/// </summary>
public sealed class Hl7MessageTests
{
    private const string Oru =
        "MSH|^~\\&|COBAS|LAB|MAROCLIS|LAB|20260915103000||ORU^R01|MSG00001|P|2.5\r" +
        "PID|1||P-00000042^^^LAB||BENANI^YOUSSEF||19850315|M\r" +
        "OBR|1|ORD001|SPEC010|GLY^Glycémie^L|||20260915093000|||||||20260915095000\r" +
        "OBX|1|NM|GLY^Glycémie^L||1.05|g/L|0.70-1.10|N|||F|||20260915102000\r" +
        "OBX|2|NM|CREAT^Créatinine^L||18.5|mg/L|7.0-13.0|H|||F|||20260915102000\r" +
        "NTE|1|L|Contrôle effectué sur dilution\r";

    [Fact]
    public void MshFieldNumbering_AccountsForTheSeparatorBeingFieldOne()
    {
        var message = Hl7Message.Parse(Oru);

        message.MessageType.Should().Be("ORU^R01");
        message.ControlId.Should().Be("MSG00001");
        message.SendingApplication.Should().Be("COBAS");
        message.Version.Should().Be("2.5");
    }

    [Fact]
    public void Parse_ExtractsThePatientAndSpecimen()
    {
        var message = Hl7ResultParser.Parse(Oru);

        message.Kind.Should().Be(InstrumentMessageKind.Results);

        var specimen = message.Specimens.Should().ContainSingle().Subject;
        specimen.SpecimenId.Should().Be("SPEC010");
        specimen.PatientId.Should().Be("P-00000042");
        specimen.PatientName.Should().Contain("BENANI");
        specimen.Results.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ReadsTheObservationValuesUnitsAndFlags()
    {
        var results = Hl7ResultParser.Parse(Oru).Specimens.Single().Results;

        var glucose = results.Single(r => r.InstrumentTestCode == "GLY");
        glucose.Value.Should().Be("1.05");
        glucose.Unit.Should().Be("g/L");
        glucose.ReferenceRange.Should().Be("0.70-1.10");
        glucose.AbnormalFlag.Should().Be("N");
        glucose.ResultStatus.Should().Be("F");

        results.Single(r => r.InstrumentTestCode == "CREAT").AbnormalFlag.Should().Be("H");
    }

    [Fact]
    public void Parse_AttachesAnNteToThePrecedingObservation()
    {
        var results = Hl7ResultParser.Parse(Oru).Specimens.Single().Results;

        results[^1].Comments.Should().ContainSingle()
               .Which.Should().Contain("dilution");
    }

    [Fact]
    public void SpmSegment_TakesPrecedenceForTheSpecimenIdentifier()
    {
        // Analysers differ on whether they populate SPM-2 or OBR-3; both have to work.
        const string withSpm =
            "MSH|^~\\&|COBAS|LAB|MAROCLIS|LAB|20260915103000||ORU^R01|MSG2|P|2.5\r" +
            "PID|1||P-1||X^Y||19900101|F\r" +
            "SPM|1|TUBE-777||SER\r" +
            "OBR|1|ORD2|OTHER-ID|GLY^Glycémie^L\r" +
            "OBX|1|NM|GLY^Glycémie^L||1.00|g/L|||N|||F\r";

        Hl7ResultParser.Parse(withSpm).Specimens.Single().SpecimenId.Should().Be("TUBE-777");
    }

    [Fact]
    public void Unescape_RestoresTheDelimiterEscapeSequences()
    {
        var message = Hl7Message.Parse(Oru);

        message.Unescape(@"a\F\b").Should().Be("a|b");
        message.Unescape(@"a\S\b").Should().Be("a^b");
        message.Unescape(@"a\R\b").Should().Be("a~b");
        message.Unescape(@"a\E\b").Should().Be(@"a\b");
    }

    [Fact]
    public void Escape_AndUnescape_RoundTrip()
    {
        var message = Hl7Message.Parse(Oru);
        const string awkward = "Valeur | avec ^ des ~ délimiteurs";

        message.Unescape(message.Escape(awkward)).Should().Be(awkward);
    }

    [Fact]
    public void BuildAck_ReturnsApplicationAcceptForASuccessfulHandover()
    {
        var message = Hl7Message.Parse(Oru);

        var ack = message.BuildAck("MAROCLIS", DateTimeOffset.Now);

        ack.Should().Contain("MSA|AA|MSG00001");
        ack.Should().Contain("ACK");
    }

    [Fact]
    public void BuildAck_ReturnsApplicationErrorWhenTheResultsCouldNotBeStored()
    {
        // An analyser that receives AA considers its results delivered and is free to discard
        // them, so acknowledging a message the LIS could not store is how results vanish.
        var message = Hl7Message.Parse(Oru);

        var ack = message.BuildAck("MAROCLIS", DateTimeOffset.Now, "Storage failed");

        ack.Should().Contain("MSA|AE|MSG00001");
        ack.Should().Contain("Storage failed");
    }

    [Fact]
    public void Parse_OfAnAckIsRecognisedAndNotTreatedAsResults()
    {
        const string ack =
            "MSH|^~\\&|COBAS|LAB|MAROCLIS|LAB|20260915103000||ACK|MSG3|P|2.5\r" +
            "MSA|AA|MSG00001\r";

        Hl7ResultParser.Parse(ack).Kind.Should().Be(InstrumentMessageKind.Acknowledgement);
    }

    [Fact]
    public void Parse_OfAQueryExtractsTheRequestedSpecimens()
    {
        const string query =
            "MSH|^~\\&|COBAS|LAB|MAROCLIS|LAB|20260915103000||QRY^R02|MSG4|P|2.3\r" +
            "QRD|20260915103000|R|I|Q001|||1^RD|SPEC020|OTH|||\r";

        var message = Hl7ResultParser.Parse(query);

        message.Kind.Should().Be(InstrumentMessageKind.Query);
        message.QueriedSpecimenIds.Should().Contain("SPEC020");
    }

    [Fact]
    public void Parse_WithoutAnMshSegment_IsBlocking()
    {
        var message = Hl7ResultParser.Parse("PID|1||P-1||X^Y\r");

        message.Kind.Should().Be(InstrumentMessageKind.Unsupported);
        message.Issues.Should().Contain(i => i.ResourceKey == "Instruments_MissingMsh");
    }

    [Theory]
    [InlineData("20260915103045", 2026, 9, 15)]
    [InlineData("20260915", 2026, 9, 15)]
    [InlineData("202609151030+0100", 2026, 9, 15)]
    public void ParseTimestamp_AcceptsHl7Forms(string value, int year, int month, int day)
    {
        var parsed = Hl7Message.ParseTimestamp(value);

        parsed.Should().NotBeNull();
        parsed!.Value.Year.Should().Be(year);
        parsed.Value.Month.Should().Be(month);
        parsed.Value.Day.Should().Be(day);
    }

    [Fact]
    public void ParseTimestamp_HonoursAnExplicitOffset()
    {
        var parsed = Hl7Message.ParseTimestamp("20260915103000+0100");

        parsed.Should().NotBeNull();
        parsed!.Value.Offset.Should().Be(TimeSpan.FromHours(1));
    }
}

/// <summary>MLLP framing, which is what delimits HL7 messages on a TCP stream.</summary>
public sealed class MllpTests
{
    [Fact]
    public void Wrap_AddsTheStartAndEndBlockBytes()
    {
        var framed = Mllp.Wrap("MSH|^~\\&|A\r");

        framed[0].Should().Be(Mllp.StartBlock);
        framed[^2].Should().Be(Mllp.EndBlock);
        framed[^1].Should().Be(Mllp.CarriageReturn);
    }

    [Fact]
    public void Reader_ExtractsACompleteMessage()
    {
        const string payload = "MSH|^~\\&|A\rPID|1\r";
        var reader = new Mllp.Reader();

        reader.Feed(Mllp.Wrap(payload));

        reader.TryReadMessage().Should().Be(payload);
        reader.TryReadMessage().Should().BeNull();
    }

    [Fact]
    public void Reader_WaitsForAnIncompleteMessage()
    {
        // HL7 over TCP has no length prefix, so the boundary has to be found across reads.
        var framed = Mllp.Wrap("MSH|^~\\&|A\r");
        var reader = new Mllp.Reader();

        reader.Feed(framed.AsSpan(0, 5));
        reader.TryReadMessage().Should().BeNull();

        reader.Feed(framed.AsSpan(5));
        reader.TryReadMessage().Should().NotBeNull();
    }

    [Fact]
    public void Reader_HandlesTwoMessagesInOneRead()
    {
        var first = Mllp.Wrap("MSH|A\r");
        var second = Mllp.Wrap("MSH|B\r");

        var reader = new Mllp.Reader();
        reader.Feed([.. first, .. second]);

        reader.TryReadMessage().Should().Be("MSH|A\r");
        reader.TryReadMessage().Should().Be("MSH|B\r");
        reader.TryReadMessage().Should().BeNull();
    }
}
