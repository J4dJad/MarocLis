using MarocLIS.Domain.Catalog;

namespace MarocLIS.Application.Catalog;

/// <summary>
/// The lists every catalogue editor picks from, loaded once per activation.
/// <para>
/// Loaded together rather than per dialog: opening the analysis editor would otherwise make four
/// round trips before the operator sees a field, and the four lists are small enough that keeping
/// them is cheaper than fetching them again. The workstation's CatalogView and the web's screen
/// load them the same way.
/// </para>
/// </summary>
public sealed record CatalogLookups(
    IReadOnlyList<NomenclatureRow> Departments,
    IReadOnlyList<NomenclatureRow> SpecimenTypes,
    IReadOnlyList<NomenclatureRow> ContainerTypes,
    IReadOnlyList<NomenclatureRow> Units,
    IReadOnlyList<PartnerSiteOption> PartnerSites,
    IReadOnlyList<InstrumentOption> Instruments)
{
    public static CatalogLookups Empty { get; } = new([], [], [], [], [], []);

    /// <summary>A list that could not be read is left empty: the editor then offers nothing to pick, not an error.</summary>
    public static async Task<CatalogLookups> LoadAsync(CatalogMaintenanceService catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var departments = await catalog.GetNomenclatureAsync(NomenclatureKind.Department, ct);
        var specimens = await catalog.GetNomenclatureAsync(NomenclatureKind.SpecimenType, ct);
        var containers = await catalog.GetNomenclatureAsync(NomenclatureKind.ContainerType, ct);
        var units = await catalog.GetNomenclatureAsync(NomenclatureKind.MeasurementUnit, ct);
        var partners = await catalog.GetPartnerSitesAsync(ct);
        var instruments = await catalog.GetInstrumentOptionsAsync(ct);

        return new CatalogLookups(
            departments.IsSuccess ? departments.Value : [],
            specimens.IsSuccess ? specimens.Value : [],
            containers.IsSuccess ? containers.Value : [],
            units.IsSuccess ? units.Value : [],
            partners.IsSuccess ? partners.Value : [],
            instruments.IsSuccess ? instruments.Value : []);
    }
}
