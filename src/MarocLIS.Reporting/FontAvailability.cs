using MarocLIS.Application.Abstractions;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MarocLIS.Reporting;

/// <summary>
/// Resolves report font families against the asset folder and the installed system fonts.
/// <para>
/// Both sources, because either is a legitimate way for a laboratory to supply a font: dropping the
/// TTF beside the executable, or installing it on the workstation. Checking only the asset folder
/// would report Amiri missing on a machine where it is installed system-wide, and a false alarm
/// about a font is how a real alarm about a font stops being read.
/// </para>
/// <para>
/// Read once and cached. The set of installed fonts does not change while the application runs, and
/// enumerating it is not free — the report screen would otherwise pay for it on every release.
/// </para>
/// </summary>
public sealed class FontAvailability : IFontAvailability
{
    private readonly HashSet<string> _families;

    public string ArabicFamily { get; }

    public string LatinFamily { get; }

    public FontAvailability(ReportingOptions options, ILogger<FontAvailability>? logger = null)
    {
        ArabicFamily = options.ArabicFontFamily;
        LatinFamily = options.LatinFontFamily;

        _families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var family in SKFontManager.Default.FontFamilies)
                if (!string.IsNullOrWhiteSpace(family)) _families.Add(family);
        }
        catch (Exception ex)
        {
            // A workstation with no font service reachable. Treated as "nothing installed" so the
            // asset folder below still counts, rather than as a fault that stops the application.
            logger?.LogWarning(ex, "Enumerating installed fonts failed.");
        }

        if (!Directory.Exists(options.FontDirectory))
        {
            logger?.LogWarning("Report font directory {Directory} does not exist.",
                                options.FontDirectory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(options.FontDirectory, "*.ttf"))
        {
            try
            {
                // The family name is read from the file rather than inferred from the file name:
                // "amiri-regular.ttf" declares the family "Amiri", and a renamed file would
                // otherwise look like a different font.
                using var typeface = SKTypeface.FromFile(file);

                if (typeface is not null && !string.IsNullOrWhiteSpace(typeface.FamilyName))
                    _families.Add(typeface.FamilyName);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Reading the font file {File} failed.", file);
            }
        }
    }

    public bool IsAvailable(string family) =>
        !string.IsNullOrWhiteSpace(family) && _families.Contains(family);

    public IReadOnlyList<string> MissingFrom(IEnumerable<string> families) =>
        [.. families.Where(f => !string.IsNullOrWhiteSpace(f) && !IsAvailable(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
}
