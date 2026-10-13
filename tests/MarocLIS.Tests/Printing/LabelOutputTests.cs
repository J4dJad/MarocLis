using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;
using Xunit;

namespace MarocLIS.Tests.Printing;

/// <summary>
/// Where labels go, and the one case that must not be a fallback.
/// <para>
/// A label printed at the wrong scale is worse than one not printed at all: it looks correct, goes
/// on a tube, and fails to scan at the analyser hours later. So a laboratory that has named a
/// printer and whose printer is unreachable has to be told, not quietly handed a PDF that will come
/// out of the A4 machine one label to a sheet.
/// </para>
/// </summary>
public sealed class LabelOutputTests
{
    [Fact]
    public async Task PrinterAvailable_GoesToThePrinter()
    {
        var printer = new FakePrinter(configured: "ZEBRA-01", available: true);
        var output = new FakeOutput();

        var result = await LabelOutput.EmitAsync(printer, output, Render, Labels(3));

        result.IsSuccess.Should().BeTrue();
        result.Value.Destination.Should().Be(LabelDestination.Printer);
        result.Value.Count.Should().Be(3);

        printer.Printed.Should().Be(3);
        output.Opened.Should().Be(0, "nothing should be written to disk when a printer took it");
    }

    [Fact]
    public async Task NoPrinterConfigured_FallsBackToAPdf()
    {
        // The documented shipping state: appsettings leaves LabelPrinterName empty, so a laboratory
        // that has not configured one still gets labels.
        var printer = new FakePrinter(configured: string.Empty, available: false);
        var output = new FakeOutput();

        var result = await LabelOutput.EmitAsync(printer, output, Render, Labels(2));

        result.IsSuccess.Should().BeTrue();
        result.Value.Destination.Should().Be(LabelDestination.Viewer);
        output.Opened.Should().Be(1);
        output.LastExtension.Should().Be("pdf");
    }

    [Fact]
    public async Task PrinterConfiguredButUnreachable_IsRefusedAndNotSilentlyRedirected()
    {
        // The case that matters. Falling through to the PDF here would send a 50 x 25 mm label to
        // whatever the default printer is, and the laboratory would find out from the stock.
        var printer = new FakePrinter(configured: "ZEBRA-01", available: false);
        var output = new FakeOutput();

        var result = await LabelOutput.EmitAsync(printer, output, Render, Labels(1));

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Label_PrinterNotFound");
        result.FirstError.Args.Should().Contain("ZEBRA-01",
            "the operator has to be told which name is wrong, not just that one is");

        output.Opened.Should().Be(0);
        printer.Printed.Should().Be(0);
    }

    [Fact]
    public async Task PrintFailure_IsReportedAndNotRetriedAsAPdf()
    {
        var printer = new FakePrinter(configured: "ZEBRA-01", available: true,
                                       failWith: Error.Conflict("Label_PrintFailed"));

        var output = new FakeOutput();

        var result = await LabelOutput.EmitAsync(printer, output, Render, Labels(1));

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Label_PrintFailed");
        output.Opened.Should().Be(0, "out of paper is not a reason to print somewhere else");
    }

    [Fact]
    public async Task NoLabels_IsRefusedBeforeAnythingIsTouched()
    {
        var printer = new FakePrinter("ZEBRA-01", available: true);
        var output = new FakeOutput();

        var result = await LabelOutput.EmitAsync(printer, output, Render, []);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Label_NothingToPrint");
        printer.Printed.Should().Be(0);
        output.Opened.Should().Be(0);
    }

    // ---- Fixture ------------------------------------------------------------------------------

    private static byte[] Render(IReadOnlyList<SampleLabel> labels) => [1, 2, 3];

    private static IReadOnlyList<SampleLabel> Labels(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new SampleLabel(
            Guid.NewGuid(), $"TUBE-{i:0000}", "BENANI AHMED", null,
            new DateOnly(1980, 1, 1), "25-000123", "EDTA", "#8e44ad", i, count,
            ["NFS"], IsUrgent: false, DateTimeOffset.Now))];

    private sealed class FakePrinter(string configured, bool available, Error? failWith = null)
        : ILabelPrinter
    {
        public int Printed { get; private set; }

        public bool IsAvailable => available;

        public string ConfiguredPrinter => configured;

        public Result Print(IReadOnlyList<SampleLabel> labels)
        {
            if (failWith is { } error) return Result.Failure(error);

            Printed += labels.Count;
            return Result.Success();
        }
    }

    private sealed class FakeOutput : IDocumentOutput
    {
        public int Opened { get; private set; }
        public string? LastExtension { get; private set; }

        public Task<Result<string>> OpenAsync(byte[] content, string extension,
                                                CancellationToken ct = default)
        {
            Opened++;
            LastExtension = extension;

            return Task.FromResult(Result.Success(Path.Combine(Path.GetTempPath(), "fake.pdf")));
        }
    }
}
