using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Relations;

namespace MarocLIS.Application.Billing;

/// <summary>A discount profile as the lists show it.</summary>
public sealed record DiscountProfileRow(Guid Id, string Name, decimal Percent, bool IsActive, DateOnly? ValidFrom,
                                        DateOnly? ValidUntil, bool AvailableToday);

public sealed record SaveDiscountProfileRequest
{
    public Guid? Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal Percent { get; init; }
    public bool IsActive { get; init; } = true;
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; init; }
}

/// <summary>A price negotiated in a convention, with the analysis and its list price today.</summary>
public sealed record PartnershipPriceRow(
    Guid Id,
    Guid PartnershipId,
    Guid AnalysisTestId,
    string AnalysisCode,
    MultilingualText AnalysisName,
    decimal? ListPrice,
    decimal? FixedPrice,
    decimal? DiscountPercent,
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    bool InForce)
{
    /// <summary>The price the convention's patients are charged today, when the list price is known.</summary>
    public decimal? NegotiatedPrice => FixedPrice ?? (ListPrice is { } list
        ? Money.Round(list * (1m - (DiscountPercent ?? 0m) / 100m))
        : null);
}

public sealed record SavePartnershipPriceRequest
{
    public Guid? Id { get; init; }
    public Guid PartnershipId { get; init; }
    public Guid AnalysisTestId { get; init; }
    public decimal? FixedPrice { get; init; }
    public decimal? DiscountPercent { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; init; }
}

/// <summary>A convention in force, as the counter offers it.</summary>
public sealed record ConventionOption(Guid Id, string PartnerName, decimal? DiscountPercent, Guid? PrescriberId);

/// <summary>What the counter's « Remise » offers today: the profiles, the conventions in force and the ceiling.</summary>
public sealed record CounterDiscountOptions(
    IReadOnlyList<DiscountProfileRow> Profiles,
    IReadOnlyList<ConventionOption> Conventions,
    DiscountCapSettings Cap);

/// <summary>One discount granted: on a request at registration, or on an invoice when it was issued.</summary>
/// <param name="Reference">The order's number, or the invoice's.</param>
/// <param name="Label">The convention, the prescriber or the profile; empty for a rate or an amount typed.</param>
/// <param name="Percent">The rate, when the discount is one.</param>
/// <param name="Gross">The amount before the discount.</param>
public sealed record DiscountReportRow(
    Guid SiteId,
    DateTimeOffset GrantedAt,
    bool OnInvoice,
    string Reference,
    string PatientName,
    string? MedicalRecordNumber,
    DiscountSource Source,
    string? Label,
    decimal? Percent,
    decimal Amount,
    decimal Gross,
    string? Reason,
    string? GrantedBy,
    string? ApprovedBy);

/// <summary>The reads of the discounts screen.</summary>
public interface IDiscountQueries
{
    /// <summary>
    /// The discounts granted on <paramref name="siteIds"/>: the orders registered from
    /// <paramref name="start"/> (included) to <paramref name="end"/> (excluded), and the invoices
    /// issued from <paramref name="from"/> to <paramref name="to"/> (both included).
    /// </summary>
    Task<IReadOnlyList<DiscountReportRow>> GetDiscountsAsync(IReadOnlyCollection<Guid> siteIds, DateTimeOffset start,
                                                             DateTimeOffset end, DateOnly from, DateOnly to,
                                                             CancellationToken ct = default);

    /// <summary>A convention's negotiated prices, with each analysis's code, name and list price on <paramref name="on"/>.</summary>
    Task<IReadOnlyList<PartnershipPriceRow>> GetPartnershipPricesAsync(Guid partnershipId, DateOnly on,
                                                                       CancellationToken ct = default);
}

/// <summary>
/// Discounts: the profiles the counter chooses from, the prices negotiated in the conventions, what
/// the counter's « Remise » offers, and the report of the discounts granted. Every change is traced in
/// the audit trail with its reason.
/// <para>
/// Rights: the profiles and the negotiated prices are tariffs (<c>billing.tariffs.manage</c>);
/// granting a discount at the counter is <c>billing.discount</c>; the report is read by whoever
/// approves discounts above the ceiling or steers the laboratory.
/// </para>
/// </summary>
public sealed class DiscountService(
    IRepository<DiscountProfile> profiles,
    IRepository<PartnershipPrice> prices,
    IRepository<Partnership> partnerships,
    IRepository<Domain.Catalog.AnalysisTest> analyses,
    IDiscountQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock,
    IInvoicePrintSettingsQueries? settings = null,
    ILicenceState? licence = null)
{
    /// <summary>The longest period the report computes at once.</summary>
    public const int MaximumPeriodDays = 366;

    // ---- Profiles ----------------------------------------------------------------------------------

    /// <summary>Every profile, by name: to those who manage them, and to those who grant discounts.</summary>
    public async Task<Result<IReadOnlyList<DiscountProfileRow>>> GetProfilesAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Billing.ManageTariffs, Permissions.Billing.ApplyDiscount);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<DiscountProfileRow>>(permitted.FirstError);

        var all = await profiles.ListAsync(_ => true, ct);
        return Result.Success<IReadOnlyList<DiscountProfileRow>>([.. all.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(Row)]);
    }

    public async Task<Result<Guid>> SaveProfileAsync(SaveDiscountProfileRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var permitted = guard.Require(Permissions.Billing.ManageTariffs);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var licensed = licence.RequireNewWork();
        if (licensed.IsFailure) return Result.Failure<Guid>(licensed.FirstError);

        var name = request.Name?.Trim() ?? string.Empty;

        DiscountProfile profile;
        string? before = null;
        if (request.Id is { } id)
        {
            var existing = await profiles.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("DiscountProfile_NotFound"));
            profile = existing;
            before = Describe(profile);
        }
        else
        {
            profile = new DiscountProfile(name, request.Percent);
        }

        profile.Name = name;
        profile.Percent = request.Percent;
        profile.IsActive = request.IsActive;
        profile.ValidFrom = request.ValidFrom;
        profile.ValidUntil = request.ValidUntil;

        var valid = profile.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        var sameName = await profiles.ListAsync(p => p.Name == name && p.Id != profile.Id, ct);
        if (sameName.Count > 0) return Result.Failure<Guid>(Error.Conflict("DiscountProfile_NameTaken", name));

        if (request.Id is null) await profiles.AddAsync(profile, ct);
        else profiles.Update(profile);

        unitOfWork.AuditReason = before is null
            ? $"Profil de remise créé : {Describe(profile)}"
            : $"Profil de remise modifié : {before} → {Describe(profile)}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(profile.Id);
    }

    // ---- The counter ------------------------------------------------------------------------------

    /// <summary>What « Remise » offers at the counter today; only to whoever may grant a discount.</summary>
    public async Task<Result<CounterDiscountOptions>> GetCounterOptionsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ApplyDiscount);
        if (permitted.IsFailure) return Result.Failure<CounterDiscountOptions>(permitted.FirstError);

        var today = clock.Today;
        var siteId = currentUser.SiteId;

        var offered = (await profiles.ListAsync(p => p.IsActive, ct))
            .Where(p => p.IsAvailableOn(today))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(Row)
            .ToList();

        var conventions = (await partnerships.ListAsync(p => p.SiteId == siteId && p.IsActive, ct))
            .Where(p => p.IsInForce(today))
            .OrderBy(p => p.PartnerName, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ConventionOption(p.Id, p.PartnerName, p.DiscountPercent, p.PrescriberId))
            .ToList();

        var cap = settings is null
            ? DiscountCapSettings.Default
            : DiscountCapSettings.FromValues(await settings.GetSettingValuesAsync(DiscountCapSettings.Prefix, ct));

        return Result.Success(new CounterDiscountOptions(offered, conventions, cap));
    }

    // ---- Negotiated prices ------------------------------------------------------------------------

    /// <summary>A convention's negotiated prices: read with the conventions, changed with the tariffs.</summary>
    public async Task<Result<IReadOnlyList<PartnershipPriceRow>>> GetPricesAsync(Guid partnershipId, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Relations.View, Permissions.Billing.ManageTariffs);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PartnershipPriceRow>>(permitted.FirstError);

        return Result.Success(await queries.GetPartnershipPricesAsync(partnershipId, clock.Today, ct));
    }

    /// <summary>
    /// Records or changes a negotiated price. One price per analysis and day within a convention: a
    /// new period starts after the previous one ends.
    /// </summary>
    public async Task<Result<Guid>> SavePriceAsync(SavePartnershipPriceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var permitted = guard.Require(Permissions.Billing.ManageTariffs);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var licensed = licence.RequireNewWork();
        if (licensed.IsFailure) return Result.Failure<Guid>(licensed.FirstError);

        var partnership = await partnerships.GetByIdAsync(request.PartnershipId, ct);
        if (partnership is null) return Result.Failure<Guid>(Error.NotFound("Partnership_NotFound"));

        PartnershipPrice price;
        string? before = null;
        if (request.Id is { } id)
        {
            var existing = await prices.GetByIdAsync(id, ct);
            if (existing is null || existing.PartnershipId != partnership.Id)
                return Result.Failure<Guid>(Error.NotFound("PartnershipPrice_NotFound"));
            price = existing;
            before = Describe(price);
        }
        else
        {
            price = new PartnershipPrice(partnership.Id, request.AnalysisTestId, request.ValidFrom);
        }

        price.AnalysisTestId = request.AnalysisTestId;
        price.FixedPrice = request.FixedPrice;
        price.DiscountPercent = request.DiscountPercent;
        price.ValidFrom = request.ValidFrom;
        price.ValidUntil = request.ValidUntil;

        var valid = price.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        var analysis = await analyses.GetByIdAsync(price.AnalysisTestId, ct);
        if (analysis is null) return Result.Failure<Guid>(Error.NotFound("PartnershipPrice_AnalysisRequired"));

        var analysisId = price.AnalysisTestId;
        var siblings = await prices.ListAsync(p => p.PartnershipId == partnership.Id && p.AnalysisTestId == analysisId, ct);
        if (siblings.Any(p => !p.IsDeleted && p.Overlaps(price)))
            return Result.Failure<Guid>(Error.Conflict("PartnershipPrice_Overlap", analysis.Code));

        if (request.Id is null) await prices.AddAsync(price, ct);
        else prices.Update(price);

        unitOfWork.AuditReason = before is null
            ? $"Tarif négocié ajouté à la convention {partnership.PartnerName} : {analysis.Code} {Describe(price)}"
            : $"Tarif négocié de la convention {partnership.PartnerName} modifié : {analysis.Code} {before} → {Describe(price)}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(price.Id);
    }

    /// <summary>Withdraws a negotiated price, with a reason; the orders already priced keep theirs.</summary>
    public async Task<Result> RemovePriceAsync(Guid priceId, string? reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ManageTariffs);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason)) return Result.Failure(Error.Validation("PartnershipPrice_RemovalReasonRequired"));

        var price = await prices.GetByIdAsync(priceId, ct);
        if (price is null || price.IsDeleted) return Result.Failure(Error.NotFound("PartnershipPrice_NotFound"));

        var partnership = await partnerships.GetByIdAsync(price.PartnershipId, ct);
        var analysis = await analyses.GetByIdAsync(price.AnalysisTestId, ct);

        prices.SoftDelete(price, currentUser.UserId, reason.Trim());
        unitOfWork.AuditReason =
            $"Tarif négocié retiré de la convention {partnership?.PartnerName} : {analysis?.Code} {Describe(price)} — {reason.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Report -----------------------------------------------------------------------------------

    /// <summary>
    /// The discounts granted over a period, on every site the operator may open: at registration
    /// (convention, prescriber, profile, rate typed) and when an invoice was issued.
    /// </summary>
    public async Task<Result<IReadOnlyList<DiscountReportRow>>> GetReportAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Billing.ApproveDiscount, Permissions.Steering.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<DiscountReportRow>>(permitted.FirstError);

        if (to < from) return Result.Failure<IReadOnlyList<DiscountReportRow>>(Error.Validation("Stats_PeriodReversed"));
        if (to.DayNumber - from.DayNumber + 1 > MaximumPeriodDays)
            return Result.Failure<IReadOnlyList<DiscountReportRow>>(Error.Validation("Stats_PeriodTooLong", MaximumPeriodDays));

        var offset = clock.Now.Offset;
        DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), offset);
        IReadOnlyCollection<Guid> sites = currentUser.AccessibleSiteIds.Count > 0 ? currentUser.AccessibleSiteIds : [currentUser.SiteId];

        var rows = await queries.GetDiscountsAsync(sites, Start(from), Start(to.AddDays(1)), from, to, ct);
        return Result.Success<IReadOnlyList<DiscountReportRow>>([.. rows.OrderByDescending(r => r.GrantedAt)]);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private DiscountProfileRow Row(DiscountProfile p) =>
        new(p.Id, p.Name, p.Percent, p.IsActive, p.ValidFrom, p.ValidUntil, p.IsAvailableOn(clock.Today));

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static string Describe(DiscountProfile p) =>
        $"{p.Name} {p.Percent.ToString("0.##", French)} %{(p.IsActive ? string.Empty : " (inactif)")}{Period(p.ValidFrom, p.ValidUntil)}";

    private static string Describe(PartnershipPrice p) =>
        (p.FixedPrice is { } fixedPrice ? $"{fixedPrice.ToString("N2", French)} DH" : $"−{(p.DiscountPercent ?? 0m).ToString("0.##", French)} %")
        + Period(p.ValidFrom, p.ValidUntil);

    private static string Period(DateOnly? from, DateOnly? until) =>
        from is null && until is null
            ? string.Empty
            : $" du {from?.ToString("dd/MM/yyyy", French) ?? "…"} au {until?.ToString("dd/MM/yyyy", French) ?? "…"}";
}
