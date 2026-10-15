using System.Text;
using FluentAssertions;
using MarocLIS.Instruments.Protocol;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// Reassembly of an analyser's message from its frames: records continued over an ETB frame, and
/// frames sent again after a lost ACK or arriving out of sequence.
/// </summary>
public sealed class ReviewFixesAstmTests
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static AstmFrame Read(byte[] wire)
    {
        var reader = new AstmFrameReader(Latin1);
        reader.Feed(wire);
        return reader.TryReadFrame()!;
    }

    private static AstmFrame Frame(int number, string text, bool isLast) =>
        Read(AstmControl.BuildFrame(number, text, isLast, Latin1));

    [Fact]
    public void RecordContinuedOverAnIntermediateFrame_IsJoinedBeforeSplitting()
    {
        var assembler = new AstmMessageAssembler();

        assembler.Accept(Frame(1, "H|\\^&\rR|1|^^^GLU|5", isLast: false), out var first)
                 .Should().Be(AstmFrameDisposition.Accepted);
        first.Should().BeEmpty("nothing is split before the last frame");
        assembler.InProgress.Should().BeTrue();

        assembler.Accept(Frame(2, "6|mg/dL\rL|1|N\r", isLast: true), out var records)
                 .Should().Be(AstmFrameDisposition.Accepted);

        records.Should().Equal("H|\\^&", "R|1|^^^GLU|56|mg/dL", "L|1|N");
        assembler.InProgress.Should().BeFalse();
    }

    [Fact]
    public void LongRecord_BuiltIntoSeveralFrames_ComesBackWhole()
    {
        var comment = "C|1|L|" + new string('x', 600);
        var frames = AstmControl.BuildFrames(["H|\\^&", comment, "L|1|N"], Latin1);
        frames.Count.Should().BeGreaterThan(2);

        var assembler = new AstmMessageAssembler();
        var records = new List<string>();
        foreach (var wire in frames)
        {
            assembler.Accept(Read(wire), out var done).Should().Be(AstmFrameDisposition.Accepted);
            records.AddRange(done);
        }

        records.Should().Equal("H|\\^&", comment, "L|1|N");
    }

    [Fact]
    public void FrameSentAgainAfterALostAck_IsAcknowledgedButNotTakenTwice()
    {
        var assembler = new AstmMessageAssembler();
        var first = Frame(1, "H|\\^&\rR|1|^^^NA|139\r", isLast: false);

        assembler.Accept(first, out _).Should().Be(AstmFrameDisposition.Accepted);
        assembler.Accept(first, out var repeated).Should().Be(AstmFrameDisposition.Duplicate);
        repeated.Should().BeEmpty();

        assembler.Accept(Frame(2, "L|1|N\r", isLast: true), out var records);
        records.Should().Equal("H|\\^&", "R|1|^^^NA|139", "L|1|N");
    }

    [Fact]
    public void LastFrameSentAgain_DoesNotStartAnotherMessage()
    {
        var assembler = new AstmMessageAssembler();
        var only = Frame(1, "H|\\^&\rL|1|N\r", isLast: true);

        assembler.Accept(only, out var records).Should().Be(AstmFrameDisposition.Accepted);
        records.Should().HaveCount(2);

        assembler.Accept(only, out var again).Should().Be(AstmFrameDisposition.Duplicate);
        again.Should().BeEmpty();
    }

    [Fact]
    public void MissingFrame_IsRefusedAndNothingIsTaken()
    {
        var assembler = new AstmMessageAssembler();

        assembler.Accept(Frame(1, "H|\\^&\rR|1|^^^GLU|5", isLast: false), out _);
        assembler.Accept(Frame(3, "L|1|N\r", isLast: true), out var records)
                 .Should().Be(AstmFrameDisposition.OutOfSequence);
        records.Should().BeEmpty();

        // The analyser resends what it should have: the expected frame is still accepted.
        assembler.Accept(Frame(2, "6\rL|1|N\r", isLast: true), out records)
                 .Should().Be(AstmFrameDisposition.Accepted);
        records.Should().Equal("H|\\^&", "R|1|^^^GLU|56", "L|1|N");
    }

    [Fact]
    public void FrameNumbers_WrapFromSevenToZero()
    {
        var assembler = new AstmMessageAssembler();
        var expected = new StringBuilder();

        for (var i = 0; i < 9; i++)
        {
            var number = (i + 1) % 8;
            assembler.Accept(Frame(number, $"R|{i}|", isLast: false), out _)
                     .Should().Be(AstmFrameDisposition.Accepted, $"frame {number} is the next one");
            expected.Append($"R|{i}|");
        }

        assembler.Accept(Frame(2, "\r", isLast: true), out var records)
                 .Should().Be(AstmFrameDisposition.Accepted);
        records.Should().Equal(expected.ToString());
    }

    [Fact]
    public void BadChecksum_IsRefused()
    {
        var wire = AstmControl.BuildFrame(1, "R|1|^^^GLU|56\r", isLast: true, Latin1);
        wire[5] ^= 0x01; // a digit flipped on the line

        new AstmMessageAssembler().Accept(Read(wire), out var records)
            .Should().Be(AstmFrameDisposition.ChecksumFailed);
        records.Should().BeEmpty();
    }

    [Fact]
    public void NextMessage_NumberedFromOneAgain_IsAccepted_ButNotInsideAMessage()
    {
        var assembler = new AstmMessageAssembler();
        assembler.Accept(Frame(1, "H|\\^&\r", isLast: false), out _);
        assembler.Accept(Frame(2, "L|1|N\r", isLast: true), out _);

        // Some analysers restart the numbering for each message of a session.
        assembler.Accept(Frame(1, "H|\\^&\r", isLast: false), out _).Should().Be(AstmFrameDisposition.Accepted);

        // Inside a message a 1 where 3 is expected is not a restart but lost frames.
        assembler.Reset();
        assembler.Accept(Frame(1, "H|\\^&\r", isLast: false), out _);
        assembler.Accept(Frame(2, "P|1\r", isLast: false), out _);
        assembler.Accept(Frame(1, "x", isLast: false), out _).Should().Be(AstmFrameDisposition.OutOfSequence);
    }
}
