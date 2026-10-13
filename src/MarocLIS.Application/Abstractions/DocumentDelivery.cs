using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Abstractions;

/// <summary>
/// Hands a generated document to the operator, and owns its lifetime on disk.
/// <para>
/// Declared here rather than in the desktop client because the <em>policy</em> is not a user
/// interface concern. Every print in this application is "write a file, let the shell open it", and
/// that means patient data lands on the local filesystem where nothing the rest of the system
/// enforces still applies: no query filter reaches it, no read of it is logged, no retention governs
/// it. How long it lives and what it is called are decisions worth stating and testing. Where the
/// bytes actually go is the implementation's business.
/// </para>
/// </summary>
public interface IDocumentOutput
{
    /// <summary>
    /// Writes a document and opens it in the operator's registered viewer, returning where it went.
    /// <para>
    /// The path comes back on success so a caller can tell the operator where to find it when no
    /// viewer is registered — the one failure that used to become a bare "unexpected error" with no
    /// document and no way to locate it.
    /// </para>
    /// </summary>
    Task<Result<string>> OpenAsync(byte[] content, string extension, CancellationToken ct = default);
}

/// <summary>
/// Sends tube labels straight to a label printer.
/// <para>
/// A label has to come out at exactly the configured size or its barcode will not scan. Handing a
/// 50 x 25 mm PDF to whichever reader the operator happens to have means the reader decides the
/// scale — "fit to page" is the default in most of them — and a barcode scaled by a few percent
/// reads as a different code or as nothing at all.
/// </para>
/// </summary>
public interface ILabelPrinter
{
    /// <summary>True when a printer is configured and the operating system recognises it.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The configured printer name, or empty when none is set. The distinction matters: empty means
    /// "this laboratory has not configured one", while a name that is not available means "it is
    /// configured and broken", and those two deserve different answers.
    /// </summary>
    string ConfiguredPrinter { get; }

    /// <summary>Prints one label per tube.</summary>
    Result Print(IReadOnlyList<SampleLabel> labels);
}

/// <summary>Where a batch of labels ended up.</summary>
public enum LabelDestination
{
    /// <summary>Straight to the configured label printer, at the right size.</summary>
    Printer,

    /// <summary>As a PDF in the operator's viewer, because no label printer is configured.</summary>
    Viewer
}

/// <summary>What happened, for the line the operator reads afterwards.</summary>
public sealed record LabelOutcome(LabelDestination Destination, int Count);

/// <summary>
/// The one place that decides how labels leave the application.
/// <para>
/// Four screens print labels — registration, collection, reception and the order file — and the
/// choice is the same decision in all four. Made once here so a laboratory that configures a printer
/// gets it everywhere, rather than on whichever screens were remembered.
/// </para>
/// <para>
/// Three outcomes, and the middle one is the point:
/// </para>
/// <list type="number">
/// <item>A printer is configured and available: print.</item>
/// <item>A printer is configured and <em>not</em> available: <b>refuse</b>. Falling back to a PDF
/// here would be the worst behaviour of the three — a laboratory that named a printer and had its
/// labels silently come out somewhere else would find out from a roll of wasted stock, or from a
/// tube that never got labelled at all.</item>
/// <item>No printer is configured: produce a PDF, and say so, because the operator now has to check
/// the scale themselves.</item>
/// </list>
/// </summary>
public static class LabelOutput
{
    /// <param name="render">
    /// Produces the PDF for the fallback path. A delegate rather than the renderer interface, which
    /// lives in the reporting assembly: the application layer decides where labels go, and it does
    /// that without taking a dependency on a PDF library to do it.
    /// </param>
    public static async Task<Result<LabelOutcome>> EmitAsync(
        ILabelPrinter printer,
        IDocumentOutput output,
        Func<IReadOnlyList<SampleLabel>, byte[]> render,
        IReadOnlyList<SampleLabel> labels,
        CancellationToken ct = default)
    {
        if (labels.Count == 0)
            return Result.Failure<LabelOutcome>(Error.Validation("Label_NothingToPrint"));

        if (printer.IsAvailable)
        {
            var printed = printer.Print(labels);

            return printed.IsFailure
                ? Result.Failure<LabelOutcome>(printed.FirstError)
                : Result.Success(new LabelOutcome(LabelDestination.Printer, labels.Count));
        }

        if (!string.IsNullOrWhiteSpace(printer.ConfiguredPrinter))
            return Result.Failure<LabelOutcome>(
                Error.Conflict("Label_PrinterNotFound", printer.ConfiguredPrinter));

        var opened = await output.OpenAsync(render(labels), "pdf", ct);

        return opened.IsFailure
            ? Result.Failure<LabelOutcome>(opened.FirstError)
            : Result.Success(new LabelOutcome(LabelDestination.Viewer, labels.Count));
    }
}
