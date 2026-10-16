using MarocLIS.Application.Reports;

namespace MarocLIS.Desktop.Views;

/// <summary>The tabs of the « Comptes rendus » screen.</summary>
public enum ReportTab
{
    /// <summary>Files with validated results that no report carries yet.</summary>
    ToIssue = 0,

    /// <summary>Every version released in the period: the register.</summary>
    Issued = 1,

    /// <summary>The current final versions never handed over: the former « non remis » tick box.</summary>
    ToDeliver = 2
}

/// <summary>
/// The rules of the « Comptes rendus » screen that need no window, kept free of WinForms so the test
/// project can compile them in.
/// </summary>
public static class ReportRegisterRules
{
    /// <summary>
    /// The tab the screen opens on: the register, as before the redesign. The register was the
    /// screen's main list, and the one Ctrl+P printed; « À émettre » stays one click away, its
    /// count shown on the tab.
    /// </summary>
    public const ReportTab DefaultTab = ReportTab.Issued;

    /// <summary>
    /// The register rows a tab lists. « À remettre » is the former « Uniquement les comptes rendus
    /// non remis » tick box, which the query answered with exactly this test.
    /// </summary>
    public static IReadOnlyList<ReportRegisterRow> RegisterRows(IReadOnlyList<ReportRegisterRow> all, ReportTab tab) =>
        tab == ReportTab.ToDeliver ? [.. all.Where(r => r.AwaitingDelivery)] : all;

    public static int Count(IReadOnlyList<PendingReportRow> pending, IReadOnlyList<ReportRegisterRow> register,
                            ReportTab tab) => tab switch
    {
        ReportTab.ToIssue => pending.Count,
        ReportTab.ToDeliver => register.Count(r => r.AwaitingDelivery),
        _ => register.Count
    };

    /// <summary>Whether a tab carries a report somebody has to act on today: a critical value not handed over.</summary>
    public static bool IsUrgent(IReadOnlyList<ReportRegisterRow> register, ReportTab tab) =>
        tab == ReportTab.ToDeliver && register.Any(r => r.IsUrgentToDeliver);

    /// <summary>
    /// What a release of a waiting file will be: final once no analysis is awaited, partial
    /// otherwise, and its version number after the ones already released.
    /// </summary>
    public static (bool Final, int NextVersion) NextRelease(PendingReportRow row) =>
        (row.AwaitingAnalyses == 0, row.ReleasedVersions + 1);
}
