namespace MarocLIS.Application.Abstractions;

/// <summary>
/// Whether the fonts a report needs are actually present on this workstation.
/// <para>
/// This exists because of how the failure looks when it happens. Report fonts are registered from
/// <c>Assets/Fonts/*.ttf</c> beside the executable, and if that folder is missing — an incomplete
/// copy, a deployment that forgot the assets, a licensed Arabic font the laboratory never
/// supplied — nothing is registered and nothing complains. The PDF engine then substitutes a
/// default face, and the Latin report comes out merely ugly while the <b>Arabic report comes out
/// unreadable</b>: Arabic needs contextual forms, and a font without them renders the text as
/// isolated letters that a patient cannot read and a clinician cannot check.
/// </para>
/// <para>
/// A substituted font is the worst class of defect this application can produce, because the output
/// looks like a document. It has a header, a number, a signature and a QR code, and it is wrong in
/// a way nobody notices until a patient complains. So the condition is detected rather than hoped
/// about, and the Arabic release is refused rather than degraded.
/// </para>
/// </summary>
public interface IFontAvailability
{
    /// <summary>
    /// The configured Arabic family, so a refusal can name the font an installer must supply.
    /// <para>
    /// Exposed here rather than read from the reporting options by the caller, because the
    /// application layer decides <em>whether</em> an Arabic report may be released and should not
    /// have to know what the laboratory chose to render it with.
    /// </para>
    /// </summary>
    string ArabicFamily { get; }

    /// <summary>The configured Latin family, for the same reason.</summary>
    string LatinFamily { get; }

    /// <summary>True when a family can be resolved, from the asset folder or from the system.</summary>
    bool IsAvailable(string family);

    /// <summary>
    /// Which of the given families cannot be resolved. Returns a list rather than a boolean because
    /// the message has to name the font the installer must supply.
    /// </summary>
    IReadOnlyList<string> MissingFrom(IEnumerable<string> families);
}
