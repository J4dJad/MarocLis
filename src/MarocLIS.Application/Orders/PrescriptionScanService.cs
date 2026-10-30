using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Orders;

/// <summary>One page or file handed in for the prescription: a scanner page, a photo, a PDF.</summary>
public sealed record PrescriptionPage(string FileName, byte[] Content);

/// <summary>A page whose kind has been read from its bytes, ready to be assembled.</summary>
public sealed record PrescriptionSource(string FileName, byte[] Content, string ContentType);

/// <summary>The prescription as it is kept and handed out: always one PDF.</summary>
public sealed record PrescriptionScanFile(string FileName, byte[] Content, string ContentType, int? PageCount);

/// <summary>What a screen shows of an attached prescription without holding it twice.</summary>
public sealed record PrescriptionScanSummary(int? PageCount, int Bytes);

/// <summary>
/// Assembles the pages of a prescription into the one PDF kept on the order: photos and scanner
/// pages downscaled and recompressed, PDFs kept as they are, all in the order given.
/// </summary>
public interface IPrescriptionDocumentComposer
{
    /// <summary>
    /// The PDF, or <c>Prescription_UnreadableFile</c> naming the page that could not be read (a
    /// damaged image, a PDF protected by a password).
    /// </summary>
    Result<byte[]> Compose(IReadOnlyList<PrescriptionSource> pages);
}

/// <summary>
/// The limits of a scanned prescription, and the check every page goes through before it is
/// assembled — whichever screen it came from.
/// <para>
/// One document per order, always a PDF: a photo from a phone, the pages of the scanner and a PDF
/// sent by the clinic end up as one file that opens the same way everywhere, prints in one go and
/// goes with an AMO claim as it is. A photo is downscaled to <see cref="ImageLongSide"/> pixels and
/// recompressed at JPEG quality <see cref="JpegQuality"/>: still readable when printed on A4, and a
/// few hundred kilobytes instead of the 4 to 8 MB a phone takes.
/// </para>
/// </summary>
public static class PrescriptionScanPolicy
{
    public const string PdfContentType = "application/pdf";

    /// <summary>The largest single file accepted, before recompression (a phone photo is 3 to 8 MB).</summary>
    public const int MaxFileMegabytes = 20;
    public const long MaxFileBytes = MaxFileMegabytes * 1024L * 1024L;

    /// <summary>The largest assembled document kept on an order.</summary>
    public const int MaxDocumentMegabytes = 10;
    public const long MaxDocumentBytes = MaxDocumentMegabytes * 1024L * 1024L;

    /// <summary>The most pages one prescription may have.</summary>
    public const int MaxPages = 20;

    /// <summary>Long side of a photo or scanner page once downscaled, in pixels.</summary>
    public const int ImageLongSide = 2000;

    /// <summary>JPEG quality of a recompressed photo.</summary>
    public const int JpegQuality = 80;

    /// <summary>For the browser's file input: what it offers to choose, or to take with the camera.</summary>
    public const string AcceptAttribute = "application/pdf,image/jpeg,image/png";

    /// <summary>For the workstation's file picker.</summary>
    public const string PickerPattern = "*.pdf;*.jpg;*.jpeg;*.png";

    /// <summary>
    /// Reads what a page really is from its bytes and refuses what is too large or not a PDF, a
    /// JPEG or a PNG, whatever its name says.
    /// </summary>
    public static Result<PrescriptionSource> Check(PrescriptionPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var name = string.IsNullOrWhiteSpace(page.FileName) ? "ordonnance" : Path.GetFileName(page.FileName.Trim());

        if (page.Content is null || page.Content.Length == 0)
            return Result.Failure<PrescriptionSource>(Error.Validation("Prescription_UnsupportedFile", name));

        if (page.Content.LongLength > MaxFileBytes)
            return Result.Failure<PrescriptionSource>(
                Error.Validation("Prescription_FileTooLarge", name, MaxFileMegabytes));

        var type = SafeDocumentFile.ContentTypeFromSignature(page.Content);
        if (type is null)
            return Result.Failure<PrescriptionSource>(Error.Validation("Prescription_UnsupportedFile", name));

        return Result.Success(new PrescriptionSource(name, page.Content, type));
    }

    /// <summary>
    /// Checks every page and assembles them. The page count is the assembled PDF's own, so that a
    /// three-page PDF counts three.
    /// </summary>
    public static Result<(byte[] Pdf, int? Pages)> Assemble(IPrescriptionDocumentComposer composer,
                                                            IReadOnlyList<PrescriptionPage> pages,
                                                            byte[]? existing = null)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(pages);

        if (pages.Count == 0) return Result.Failure<(byte[], int?)>(Error.Validation("Prescription_NoPage"));

        var sources = new List<PrescriptionSource>(pages.Count + 1);
        if (existing is { Length: > 0 })
            sources.Add(new PrescriptionSource("ordonnance.pdf", existing, PdfContentType));

        foreach (var page in pages)
        {
            var checkedPage = Check(page);
            if (checkedPage.IsFailure) return Result.Failure<(byte[], int?)>(checkedPage.FirstError);
            sources.Add(checkedPage.Value);
        }

        var composed = composer.Compose(sources);
        if (composed.IsFailure) return Result.Failure<(byte[], int?)>(composed.Errors);

        var pdf = composed.Value;
        if (pdf.LongLength > MaxDocumentBytes)
            return Result.Failure<(byte[], int?)>(Error.Validation("Prescription_DocumentTooLarge", MaxDocumentMegabytes));

        var count = PdfPageCounter.Count(pdf);
        if (count > MaxPages)
            return Result.Failure<(byte[], int?)>(Error.Validation("Prescription_TooManyPages", MaxPages));

        return Result.Success((pdf, count));
    }

    /// <summary>
    /// Checks a document already assembled, as a registration receives it: a PDF, within the size.
    /// </summary>
    public static Result CheckAssembled(byte[]? document, string? contentType)
    {
        if (document is null) return Result.Success();

        if (document.LongLength > MaxDocumentBytes)
            return Result.Failure(Error.Validation("Prescription_DocumentTooLarge", MaxDocumentMegabytes));

        if (SafeDocumentFile.ContentTypeFromSignature(document) != PdfContentType
            || (contentType is not null && !string.Equals(contentType, PdfContentType, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure(Error.Validation("Prescription_UnsupportedFile", "ordonnance"));

        return Result.Success();
    }

    /// <summary>The name the document is downloaded under: « Ordonnance-2610-000042.pdf ».</summary>
    public static string FileName(string accessionNumber) =>
        $"Ordonnance-{string.Concat(accessionNumber.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'))}.pdf";
}

/// <summary>
/// The scanned prescription (« ordonnance numérisée ») of an existing order: attached, replaced,
/// completed with more pages, viewed and removed.
/// <para>
/// The rights are those of the order itself: reading it is <see cref="Permissions.Orders.View"/>,
/// changing it is <see cref="Permissions.Orders.Edit"/>, as for any correction of the file. A
/// prescription is part of the request, not a separate document with its own keeper.
/// </para>
/// <para>
/// A cancelled file takes no change: it is closed, and its prescription stays as it was when it was
/// cancelled. A reported one still does: the AMO claim is sent after the report, and the scan
/// forgotten at the counter is often added then.
/// </para>
/// <para>
/// Every change is in the audit trail with its own reason (attached, replaced, pages added,
/// removed); the document itself is recorded there by its size, not copied. Opening it is a
/// consultation of the medical record and is logged as such (loi 08-09).
/// </para>
/// </summary>
public sealed class PrescriptionScanService(
    IRepository<LabOrder> orders,
    IPrescriptionDocumentComposer composer,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    IDataAccessAuditor accessAuditor)
{
    /// <summary>
    /// Assembles pages for an order not registered yet (« Nouvelle demande »): the screen keeps the
    /// result and hands it to the registration. <paramref name="existing"/> is what was already
    /// assembled, to which the new pages are added.
    /// </summary>
    public Task<Result<PrescriptionScanFile>> PrepareAsync(IReadOnlyList<PrescriptionPage> pages,
                                                           byte[]? existing = null)
    {
        var permitted = guard.RequireAny(Permissions.Orders.Create, Permissions.Orders.Edit);
        if (permitted.IsFailure) return Task.FromResult(Result.Failure<PrescriptionScanFile>(permitted.FirstError));

        var assembled = PrescriptionScanPolicy.Assemble(composer, pages, existing);
        if (assembled.IsFailure) return Task.FromResult(Result.Failure<PrescriptionScanFile>(assembled.Errors));

        return Task.FromResult(Result.Success(new PrescriptionScanFile(
            "Ordonnance.pdf", assembled.Value.Pdf, PrescriptionScanPolicy.PdfContentType, assembled.Value.Pages)));
    }

    /// <summary>The prescription of an order, to show or download.</summary>
    public async Task<Result<PrescriptionScanFile>> GetAsync(Guid orderId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.View);
        if (permitted.IsFailure) return Result.Failure<PrescriptionScanFile>(permitted.FirstError);

        var order = await orders.GetByIdAsync(orderId, ct);
        if (order is null) return Result.Failure<PrescriptionScanFile>(Error.NotFound("Order_NotFound", orderId));

        if (order.PrescriptionScan is not { Length: > 0 } content)
            return Result.Failure<PrescriptionScanFile>(Error.NotFound("Prescription_NotAttached"));

        await accessAuditor.LogAsync(DataAccessKind.ViewOrder, order.PatientId, order.Id, "PrescriptionScan", ct: ct);

        var type = order.PrescriptionScanContentType
                   ?? SafeDocumentFile.ContentTypeFromSignature(content)
                   ?? PrescriptionScanPolicy.PdfContentType;

        return Result.Success(new PrescriptionScanFile(
            PrescriptionScanPolicy.FileName(order.AccessionNumber), content, type,
            type == PrescriptionScanPolicy.PdfContentType ? PdfPageCounter.Count(content) : 1));
    }

    /// <summary>
    /// Attaches the pages as the order's prescription, replacing the one it had; with
    /// <paramref name="append"/>, adds them after the pages already there.
    /// </summary>
    public async Task<Result<PrescriptionScanSummary>> AttachAsync(Guid orderId, IReadOnlyList<PrescriptionPage> pages,
                                                                   bool append = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Edit);
        if (permitted.IsFailure) return Result.Failure<PrescriptionScanSummary>(permitted.FirstError);

        var order = await orders.GetByIdAsync(orderId, ct);
        if (order is null) return Result.Failure<PrescriptionScanSummary>(Error.NotFound("Order_NotFound", orderId));

        if (order.Status == LabOrderStatus.Cancelled)
            return Result.Failure<PrescriptionScanSummary>(Error.Conflict("Order_CancelledCannotAmend"));

        var had = order.PrescriptionScan is { Length: > 0 };
        var existing = append && had && order.PrescriptionScanContentType == PrescriptionScanPolicy.PdfContentType
            ? order.PrescriptionScan
            : null;

        var assembled = PrescriptionScanPolicy.Assemble(composer, pages, existing);
        if (assembled.IsFailure) return Result.Failure<PrescriptionScanSummary>(assembled.Errors);

        var (pdf, count) = assembled.Value;
        order.PrescriptionScan = pdf;
        order.PrescriptionScanContentType = PrescriptionScanPolicy.PdfContentType;

        var what = !had ? "jointe au" : existing is not null ? "complétée sur le" : "remplacée sur le";
        unitOfWork.AuditReason =
            $"Ordonnance numérisée {what} dossier {order.AccessionNumber} ({PagesText(count)}, {pdf.Length / 1024} Ko)";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new PrescriptionScanSummary(count, pdf.Length));
    }

    /// <summary>Removes the order's prescription (a wrong patient's, an unreadable photo).</summary>
    public async Task<Result> RemoveAsync(Guid orderId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Edit);
        if (permitted.IsFailure) return permitted;

        var order = await orders.GetByIdAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound", orderId));

        if (order.Status == LabOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Order_CancelledCannotAmend"));

        if (order.PrescriptionScan is not { Length: > 0 })
            return Result.Failure(Error.NotFound("Prescription_NotAttached"));

        order.PrescriptionScan = null;
        order.PrescriptionScanContentType = null;

        unitOfWork.AuditReason = $"Ordonnance numérisée retirée du dossier {order.AccessionNumber}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    private static string PagesText(int? count) => count switch
    {
        null => "pages non comptées",
        1 => "1 page",
        _ => $"{count} pages"
    };
}

/// <summary>
/// Counts the pages of a PDF without a PDF library: the largest <c>/Count</c> of a <c>/Type /Pages</c>
/// node, in the file's text or in its compressed object streams. Null when none is found; the
/// screens then say « ordonnance jointe » without a number.
/// </summary>
public static class PdfPageCounter
{
    private static readonly System.Text.RegularExpressions.Regex PagesNode = new(
        @"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)|/Count\s+(\d+)[^>]*?/Type\s*/Pages\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    public static int? Count(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        int? best = null;

        try
        {
            Scan(System.Text.Encoding.Latin1.GetString(pdf), ref best);

            if (best is null)
            {
                foreach (var stream in FlateStreams(pdf))
                    Scan(System.Text.Encoding.Latin1.GetString(stream), ref best);
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return best;
        }

        return best;
    }

    private static void Scan(string text, ref int? best)
    {
        foreach (System.Text.RegularExpressions.Match match in PagesNode.Matches(text))
        {
            var group = match.Groups[1].Success ? match.Groups[1] : match.Groups[2];
            if (int.TryParse(group.ValueSpan, System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
                best = Math.Max(best ?? 0, n);
        }
    }

    /// <summary>The inflated content of each stream that inflates; the others are skipped.</summary>
    private static IEnumerable<byte[]> FlateStreams(byte[] pdf)
    {
        var span = 0;
        while (true)
        {
            var start = IndexOf(pdf, "stream"u8, span);
            if (start < 0) yield break;

            var body = start + 6;
            if (body < pdf.Length && pdf[body] == '\r') body++;
            if (body < pdf.Length && pdf[body] == '\n') body++;

            var end = IndexOf(pdf, "endstream"u8, body);
            if (end < 0) yield break;
            span = end + 9;

            // "endstream" also contains "stream": skip a match that was the end of the previous one.
            if (start >= 3 && pdf.AsSpan(start - 3, 3).SequenceEqual("end"u8)) continue;

            byte[]? inflated = null;
            try
            {
                using var input = new MemoryStream(pdf, body, end - body);
                using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                inflated = output.ToArray();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // An image or a stream compressed otherwise: not a page tree.
            }

            if (inflated is not null) yield return inflated;
        }
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> value, int from)
    {
        if (from >= data.Length) return -1;
        var index = data.AsSpan(from).IndexOf(value);
        return index < 0 ? -1 : from + index;
    }
}
