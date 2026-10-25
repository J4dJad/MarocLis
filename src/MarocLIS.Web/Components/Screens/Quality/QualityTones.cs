using MarocLIS.Application.Quality;

namespace MarocLIS.Web.Components.Screens.Quality;

/// <summary>
/// The pill and the ink of the quality registers' tones (QualityTone, QualityInk), as the
/// workstation's QhTone colours its pills and cells.
/// </summary>
public static class QualityTones
{
    /// <summary>The Pill component's tone.</summary>
    public static string Pill(QualityTone tone) => tone switch
    {
        QualityTone.Ok => "Ok",
        QualityTone.Info => "Info",
        QualityTone.Warning => "Warning",
        QualityTone.Critical => "Critical",
        QualityTone.Muted => "Muted",
        _ => "Waiting"
    };

    /// <summary>The class of a text in its ink: muted, normal green, warning or alarm in bold.</summary>
    public static string? Ink(QualityInk ink) => ink switch
    {
        QualityInk.Muted => "muted",
        QualityInk.Normal => "ink-normal",
        QualityInk.Abnormal => "abnormal strong",
        QualityInk.Critical => "critical strong",
        _ => null
    };
}
