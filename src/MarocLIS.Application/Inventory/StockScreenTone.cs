namespace MarocLIS.Application.Inventory;

/// <summary>
/// The pill of a state on the stock screens — « Stock », « Inventaires », « Températures »: the
/// workstation's StockPill, by meaning rather than by colour, so the web draws the same pills with
/// the same symbol before the text.
/// </summary>
public enum StockTone
{
    /// <summary>Waiting for someone (○): a draft count, a unit out of service.</summary>
    Waiting,

    /// <summary>Under way (◐): a count being counted.</summary>
    Running,

    /// <summary>Done and sound (✓): a usable lot, a reading in range, a closed count.</summary>
    Done,

    /// <summary>Needs attention (!): quarantined, beyond open stability, no reading yet today.</summary>
    Warning,

    /// <summary>The alarm (✕): rejected, recalled, expired, out of range.</summary>
    Critical
}

/// <summary>How a value of a stock list reads beside the others: the workstation's cell inks.</summary>
public enum StockInk
{
    Plain,

    /// <summary>Kept for reference, or not computable: the muted ink.</summary>
    Muted,

    /// <summary>A warning on the cell itself: the abnormal ink.</summary>
    Abnormal,

    /// <summary>A warning said louder: the abnormal ink, in bold.</summary>
    AbnormalBold,

    /// <summary>The alarm, on its fill: the critical ink over the critical background.</summary>
    Critical,

    /// <summary>The alarm that stops an analysis being offered: critical, in bold.</summary>
    CriticalBold
}

public static class StockTones
{
    /// <summary>The symbol the workstation draws before a pill's text.</summary>
    public static string Symbol(StockTone tone) => tone switch
    {
        StockTone.Running => "◐",
        StockTone.Done => "✓",
        StockTone.Warning => "!",
        StockTone.Critical => "✕",
        _ => "○"
    };

    /// <summary>« ✓ Utilisable »: the pill's text as the workstation paints it.</summary>
    public static string Label(StockTone tone, string text) => $"{Symbol(tone)} {text}";
}
