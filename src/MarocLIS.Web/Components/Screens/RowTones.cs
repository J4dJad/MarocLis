using MarocLIS.Application.Administration;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Instruments;

namespace MarocLIS.Web.Components.Screens;

/// <summary>
/// The class a row of a list takes for the tone its screen's rules gave it — the workstation's
/// PaintRow: grey when switched off, filled in the warning's or the alarm's colours when somebody
/// has to come back to it, said in the warning's colour, or set in bold.
/// </summary>
public static class RowTones
{
    public static string? Class(CatalogTone tone) => tone switch
    {
        CatalogTone.Muted => "is-tone-muted",
        CatalogTone.Abnormal => "is-tone-abnormal",
        CatalogTone.Critical => "is-tone-critical",
        _ => null
    };

    public static string? Class(MasterDataTone tone) => tone switch
    {
        MasterDataTone.Muted => "is-tone-muted",
        MasterDataTone.Warning => "is-tone-warning",
        MasterDataTone.Abnormal => "is-tone-abnormal",
        MasterDataTone.Critical => "is-tone-critical",
        MasterDataTone.Strong => "is-tone-strong",
        _ => null
    };

    /// <summary>« Interfaces automates »: grey when off, the warning's colour, the alarm's, or the normal green.</summary>
    public static string? Class(InterfaceTone tone) => tone switch
    {
        InterfaceTone.Muted => "is-tone-muted",
        InterfaceTone.Warning => "is-tone-warning",
        InterfaceTone.Critical => "is-tone-critical",
        InterfaceTone.Ok => "is-tone-ok",
        _ => null
    };

    /// <summary>The pill of a state in the tone of its row.</summary>
    public static string Pill(CatalogTone tone) => tone switch
    {
        CatalogTone.Muted => "Waiting",
        CatalogTone.Abnormal => "Warning",
        CatalogTone.Critical => "Critical",
        _ => "Ok"
    };

    public static string Pill(MasterDataTone tone) => tone switch
    {
        MasterDataTone.Muted => "Waiting",
        MasterDataTone.Warning or MasterDataTone.Abnormal => "Warning",
        MasterDataTone.Critical => "Critical",
        _ => "Ok"
    };

    /// <summary>The workstation's pill colours of « Interfaces automates » (AdminLayout.Ok, Warn, Crit, Run, Wait).</summary>
    public static string Pill(InterfaceTone tone) => tone switch
    {
        InterfaceTone.Ok => "Ok",
        InterfaceTone.Warning => "Warning",
        InterfaceTone.Critical => "Critical",
        InterfaceTone.Info => "Info",
        _ => "Waiting"
    };
}
