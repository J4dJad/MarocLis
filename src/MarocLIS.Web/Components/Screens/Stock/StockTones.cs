using MarocLIS.Application.Inventory;

namespace MarocLIS.Web.Components.Screens.Stock;

/// <summary>
/// The pill, the ink and the row tone of the stock screens (StockTone, StockInk, StockRowTone), as
/// the workstation's StockPill and its cell formatting colour them.
/// </summary>
public static class StockTones
{
    /// <summary>The Pill component's tone.</summary>
    public static string Pill(StockTone tone) => tone switch
    {
        StockTone.Running => "Info",
        StockTone.Done => "Ok",
        StockTone.Warning => "Warning",
        StockTone.Critical => "Critical",
        _ => "Waiting"
    };

    /// <summary>« ✓ Utilisable »: the pill's text with the workstation's symbol before it.</summary>
    public static string Label(StockTone tone, string text) => Application.Inventory.StockTones.Label(tone, text);

    /// <summary>The class of a text in its ink.</summary>
    public static string? Ink(StockInk ink) => ink switch
    {
        StockInk.Muted => "muted",
        StockInk.Abnormal => "abnormal",
        StockInk.AbnormalBold => "abnormal strong",
        StockInk.Critical => "critical",
        StockInk.CriticalBold => "critical strong",
        _ => null
    };

    /// <summary>The class of a whole row: the alarm on its fill, in bold for a critical article.</summary>
    public static string? Row(StockInk ink) => ink switch
    {
        StockInk.Muted => "is-tone-muted",
        StockInk.Critical => "stock-alarm",
        StockInk.CriticalBold => "stock-alarm stock-alarm-strong",
        _ => null
    };

    public static string? Row(StockRowTone tone) => tone switch
    {
        StockRowTone.Critical => "stock-alarm",
        StockRowTone.CriticalBold => "stock-alarm stock-alarm-strong",
        StockRowTone.Warning => "stock-warn",
        _ => null
    };
}
