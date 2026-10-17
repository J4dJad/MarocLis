using FluentAssertions;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Simulator;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// Cycle 5, lot a: a COBAS INTEGRA test number written in the table as Roche's documents print it
/// (« 0-031 ») or without its zeros (« 31 ») — the order download already read both as test 31 —
/// receives the analyser's « 031 » result instead of « code non correspondant ».
/// </summary>
public sealed class Cycle5aIntegraMappingTests
{
    [Theory]
    [InlineData("031")]
    [InlineData("0-031")]
    [InlineData("31")]
    public void AnIntegraResult_FindsItsTestNumber_HoweverTheTableWritesIt(string written)
    {
        var glucose = Guid.NewGuid();
        var mappings = new[]
        {
            new InstrumentTestMapping("0-003", Guid.NewGuid()),
            new InstrumentTestMapping(written, glucose)
        };

        var message = IntegraMessageParser.Parse(
            RocheIntegra.FromTranscript(IntegraInstrumentEmulator.SampleTranscript("TUBE-0001", [31, 3])));

        var results = message.Specimens.SelectMany(s => s.Results).ToList();
        results.Select(r => r.InstrumentTestCode).Should().Contain(["031", "003"]);

        InstrumentMessageSink.FindMapping(mappings, "031")!.TestParameterId.Should().Be(glucose);
        InstrumentMessageSink.FindMapping(mappings, "003")!.InstrumentTestCode.Should().Be("0-003");
        InstrumentMessageSink.FindMapping(mappings, "032").Should().BeNull();
    }

    [Fact]
    public void TheExactCode_StillComesFirst_AndASwitchedOffNumberIsNotUsed()
    {
        var exact = new InstrumentTestMapping("031", Guid.NewGuid());
        var other = new InstrumentTestMapping("0-031", Guid.NewGuid());

        InstrumentMessageSink.FindMapping([other, exact], "031").Should().BeSameAs(exact);

        var off = new InstrumentTestMapping("0-031", Guid.NewGuid()) { IsEnabled = false };
        InstrumentMessageSink.FindMapping([off], "031").Should().BeNull();

        // Codes of other analysers are untouched: no number, no match on one.
        InstrumentMessageSink.FindMapping([new InstrumentTestMapping("GLY", Guid.NewGuid())], "031").Should().BeNull();
    }
}
