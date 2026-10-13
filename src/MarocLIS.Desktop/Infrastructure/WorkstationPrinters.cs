using System.Drawing.Printing;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The printers this workstation uses: one for A4 documents (reports, invoices, bench sheets) and
/// one for tube labels.
/// <para>
/// A laboratory has an A4 printer at the front desk and a thermal label printer at the collection
/// room, and the two are never the same machine. The choice belongs to the workstation, not to the
/// database: the reception PC and the collection PC print to different devices with the same
/// configuration. It is kept in <c>%LOCALAPPDATA%\MarocLIS\printers.json</c>, and a label printer
/// chosen here, "none" included, takes precedence over <c>Labels:LabelPrinterName</c> in <c>appsettings.json</c>.
/// </para>
/// </summary>
public sealed class WorkstationPrinters
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly LabelPrintOptions _labels;
    private readonly ILogger<WorkstationPrinters>? _logger;
    private readonly string _path;

    public WorkstationPrinters(LabelPrintOptions labels, ILogger<WorkstationPrinters>? logger = null,
                               string? path = null)
    {
        _labels = labels;
        _logger = logger;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarocLIS", "printers.json");

        Load();
    }

    /// <summary>Printer for A4 documents; empty means the Windows default printer.</summary>
    public string DocumentPrinter { get; private set; } = string.Empty;

    /// <summary>
    /// Send PDFs straight to <see cref="DocumentPrinter"/> instead of opening them on screen first.
    /// </summary>
    public bool PrintDocumentsDirectly { get; private set; }

    /// <summary>Printer for tube labels; empty means labels come out as a PDF.</summary>
    public string LabelPrinter => _labels.LabelPrinterName;

    public static IReadOnlyList<string> Installed()
    {
        try
        {
            return [.. PrinterSettings.InstalledPrinters.Cast<string>().Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception)
        {
            // The spooler service can be stopped; an empty list is then the truth.
            return [];
        }
    }

    public static string? DefaultPrinter()
    {
        try
        {
            // Checked against the installed list: with no default set, some spoolers answer with a
            // message ("Default printer is not set.") in place of a name.
            var name = new PrinterSettings().PrinterName;
            return Installed().Contains(name, StringComparer.OrdinalIgnoreCase) ? name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool Save(string documentPrinter, bool printDirectly, string labelPrinter)
    {
        var stored = new Stored(documentPrinter.Trim(), printDirectly, labelPrinter.Trim());

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(stored, Json));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving the workstation printers failed.");
            return false;
        }

        Apply(stored);
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            if (JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path)) is { } stored)
                Apply(stored);
        }
        catch (Exception ex)
        {
            // A damaged file must not stop the application from starting; the defaults apply.
            _logger?.LogWarning(ex, "Reading the workstation printers failed.");
        }
    }

    private void Apply(Stored stored)
    {
        DocumentPrinter = stored.DocumentPrinter ?? string.Empty;
        PrintDocumentsDirectly = stored.PrintDocumentsDirectly && DocumentPrinter.Length > 0;
        // Once this workstation has made a choice it wins, "none" included: a wrong printer name
        // left in appsettings.json must not come back the moment the operator clears it here.
        _labels.LabelPrinterName = stored.LabelPrinter ?? string.Empty;
    }

    private sealed record Stored(string? DocumentPrinter, bool PrintDocumentsDirectly, string? LabelPrinter);
}
