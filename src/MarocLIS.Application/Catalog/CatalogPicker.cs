using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Catalog;

/// <summary>
/// A pickable item in order entry — an analysis or a panel, presented uniformly.
/// <para>
/// One list rather than two tabs: a reception clerk told "bilan lipidique" does not know or care
/// whether the laboratory configured that as a panel or a single analysis, and making them choose
/// the right tab first is friction that produces wrong orders.
/// </para>
/// </summary>
public sealed record CatalogPickerItem(
    Guid Id,
    bool IsPanel,
    string Code,
    MultilingualText Name,
    string? DepartmentCode,
    MultilingualText? DepartmentName,
    decimal Price,
    int? FastingHours,
    int TurnaroundHours,
    bool IsAvailableUrgently,
    ConsentRequirement ConsentRequirement,
    bool IsSubcontracted,
    MultilingualText? ContainerName,
    string? ContainerColourHex,
    int ComponentCount,
    IReadOnlyList<PickerComponent>? Components = null,
    decimal? PackagePrice = null);

/// <summary>A mandatory member of a panel and the price it is billed at.</summary>
public sealed record PickerComponent(Guid AnalysisTestId, decimal Price);
