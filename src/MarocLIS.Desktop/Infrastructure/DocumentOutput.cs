using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Infrastructure;
/// <inheritdoc cref="IDocumentOutput"/>
/// <summary>
/// Keeps viewed documents in one per-run folder and removes it on the way out.
/// <para>
/// Three rules, each fixing something the previous direct-to-<c>%TEMP%</c> calls got wrong.
/// </para>
/// <list type="number">
/// <item><b>Names carry nothing.</b> The old names were <c>etiquettes-{accession}.pdf</c> and
/// <c>{accession}-v{n}.pdf</c>, so a directory listing disclosed which patients had been seen
/// without opening a single file. Names are random now.</item>
/// <item><b>One folder, deleted on exit.</b> Everything goes in one per-run folder which is removed
/// when the application closes, instead of accumulating in <c>%TEMP%</c> for ever beside every
/// other program's leftovers. Nothing in the solution deleted these files before.</item>
/// <item><b>Folders from earlier runs are swept at start-up.</b> A crash, or a file a viewer still
/// held open at exit, would otherwise leave one behind permanently — and a cleanup that only works
/// on a clean shutdown is not a cleanup.</item>
/// </list>
/// <para>
/// What this does <em>not</em> do is make the location privileged.
/// <c>%LOCALAPPDATA%</c> carries the same permissions as <c>%TEMP%</c>, because <c>%TEMP%</c> is
/// normally inside it. The protection here is the bounded lifetime and the opaque name, not the
/// directory: anyone signed in as that Windows user can read these files while the application is
/// running. On a shared reception workstation that is a real exposure window, and the mitigation for
/// it is the operating system's — a per-person Windows account — not this application's. Said plainly
/// because an installation that believes otherwise will not create those accounts.
/// </para>
/// </summary>
public sealed class SessionDocumentOutput : IDocumentOutput, IDisposable
{
    private const string RootFolderName = "MarocLIS";
    private const string SessionPrefix = "view-";

    private readonly ILogger<SessionDocumentOutput>? _logger;
    private readonly string _sessionFolder;
    private readonly Func<string, bool> _open;
    private readonly Lock _gate = new();

    private bool _disposed;

    /// <param name="shellOpen">
    /// How a written file is handed to the operating system. Injectable for one reason: a test that
    /// exercised the real one would launch a PDF reader on whoever ran it.
    /// </param>
    public SessionDocumentOutput(ILogger<SessionDocumentOutput>? logger = null,
                                  Func<string, bool>? shellOpen = null)
    {
        _logger = logger;
        _open = shellOpen ?? ShellOpen;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RootFolderName);

        _sessionFolder = Path.Combine(root, $"{SessionPrefix}{Guid.NewGuid():N}");

        SweepPreviousSessions(root);
    }

    public async Task<Result<string>> OpenAsync(byte[] content, string extension,
                                                  CancellationToken ct = default)
    {
        if (content.Length == 0)
            return Result.Failure<string>(Error.Validation("Document_Empty"));

        string path;

        try
        {
            Directory.CreateDirectory(_sessionFolder);

            // Random, not derived from the accession number or the patient: the file name is itself
            // a disclosure if it is readable in a directory listing.
            path = Path.Combine(_sessionFolder,
                                 $"{Guid.NewGuid():N}.{extension.TrimStart('.')}");

            await File.WriteAllBytesAsync(path, content, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Writing a document for viewing failed.");
            return Result.Failure<string>(Error.Conflict("Document_WriteFailed"));
        }

        if (!_open(path))
        {
            // The document exists and is readable; only the viewer is missing. The failure carries
            // the path so the caller can tell the operator where it went, instead of the bare
            // "unexpected error" with no document and no way to find it that this used to produce.
            _logger?.LogWarning("No registered viewer for {Extension}.", extension);
            return Result.Failure<string>(Error.Conflict("Document_NoViewer", path));
        }

        return Result.Success(path);
    }

    private static bool ShellOpen(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes folders left by earlier runs.
    /// <para>
    /// Only folders matching this class's own prefix are touched, and only directories — a cleanup
    /// that deletes by pattern in a directory it does not own is how a housekeeping routine removes
    /// something it was never meant to.
    /// </para>
    /// </summary>
    private void SweepPreviousSessions(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (var folder in Directory.EnumerateDirectories(root, $"{SessionPrefix}*"))
                TryDeleteFolder(folder);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Sweeping previous viewing folders failed.");
        }
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            // A file still open in a viewer cannot be deleted. Logged and left: the next start-up
            // sweeps it, which is why the sweep exists at all.
            _logger?.LogInformation(ex, "Viewing folder {Folder} could not be removed yet.", folder);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        TryDeleteFolder(_sessionFolder);
    }
}
