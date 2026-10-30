using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Relations;

namespace MarocLIS.Application.Orders;

/// <summary>What the counter chose for the discount of a request, before the save.</summary>
public sealed record OrderDiscountChoice
{
    public Guid? PrescriberId { get; init; }

    /// <summary>A convention chosen at the counter; null lets the prescriber's decide.</summary>
    public Guid? PartnershipId { get; init; }

    /// <summary>« Sans convention »: the prescriber's convention is not applied.</summary>
    public bool WithoutConvention { get; init; }

    /// <summary>A discount profile; its rate is the one granted.</summary>
    public Guid? DiscountProfileId { get; init; }

    /// <summary>A rate typed, without a profile.</summary>
    public decimal? DiscountPercent { get; init; }

    public string? DiscountReason { get; init; }
}

/// <summary>Where the discount of a request stands: the convention, the rates offered and the one applied.</summary>
/// <param name="Prices">The convention's negotiated prices.</param>
public sealed record OrderDiscountContext(
    Partnership? Convention,
    DiscountOffer? Automatic,
    DiscountOffer? Counter,
    DiscountOffer? Applied,
    IReadOnlyList<PartnershipPrice> Prices)
{
    public static readonly OrderDiscountContext None = new(null, null, null, null, []);

    /// <summary>The order's rate in percent; 0 without one.</summary>
    public decimal Percent => Applied?.Percent ?? 0m;
}

/// <summary>The discount a request was priced with, as the counter shows it.</summary>
/// <param name="Source">The source of the rate applied; a convention when its negotiated prices alone give the discount.</param>
/// <param name="Percent">The order's rate; null when only negotiated prices apply.</param>
/// <param name="Label">The convention, the prescriber or the profile; empty for a rate typed.</param>
/// <param name="ConventionId">The convention the request is priced under, if any.</param>
/// <param name="ConventionName">Its partner's name.</param>
/// <param name="ApprovalRequired">The counter's rate goes beyond the ceiling: another user must approve it.</param>
public sealed record AppliedDiscount(
    DiscountSource Source,
    decimal? Percent,
    string Label,
    Money Amount,
    Guid? ConventionId,
    string? ConventionName,
    bool ApprovalRequired,
    Money Ceiling);

/// <summary>
/// Resolves the discount of a request for the counter's quote, its registration and the analyses
/// added to it later, by <see cref="DiscountRules"/>: the convention in force, the prescriber's
/// rate, the counter's profile or rate, and the convention's negotiated prices. Checks the right to
/// grant a discount and, above the ceiling, another user's approval.
/// </summary>
public sealed class OrderDiscounts(
    IRepository<Partnership> partnerships,
    IRepository<Prescriber> prescribers,
    IRepository<PartnershipPrice> prices,
    IRepository<DiscountProfile> profiles,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IInvoicePrintSettingsQueries? settings = null,
    IAuthenticationService? authentication = null)
{
    /// <summary>
    /// The discount of a request on <paramref name="day"/>. A convention chosen, a profile or a rate
    /// typed needs <c>billing.discount</c>; a profile must be offered that day; the counter's rate
    /// needs a reason when <paramref name="requireReason"/> (the save, not the running quote).
    /// </summary>
    public async Task<Result<OrderDiscountContext>> ResolveAsync(OrderDiscountChoice choice, DateOnly day,
                                                                 bool requireReason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(choice);

        var counterAsked = choice.DiscountProfileId is not null || choice.DiscountPercent is > 0m;
        if (choice.PartnershipId is not null || counterAsked)
        {
            var allowed = guard.Require(Permissions.Billing.ApplyDiscount);
            if (allowed.IsFailure) return Result.Failure<OrderDiscountContext>(allowed.FirstError);
        }

        var siteId = currentUser.SiteId;
        IReadOnlyList<Partnership> candidates = choice.WithoutConvention
            ? []
            : await partnerships.ListAsync(p => p.SiteId == siteId && p.IsActive, ct);

        var convention = DiscountRules.ConventionFor(candidates, siteId, choice.PrescriberId, choice.PartnershipId,
                                                     choice.WithoutConvention, day);

        if (choice.PartnershipId is not null && convention is null)
            return Result.Failure<OrderDiscountContext>(Error.Validation("Order_ConventionNotInForce"));

        var prescriber = choice.PrescriberId is { } prescriberId ? await prescribers.GetByIdAsync(prescriberId, ct) : null;
        var automatic = DiscountRules.Automatic(convention, prescriber, day);

        DiscountOffer? counter = null;
        if (choice.DiscountProfileId is { } profileId)
        {
            var profile = await profiles.GetByIdAsync(profileId, ct);
            if (profile is null || !profile.IsAvailableOn(day))
                return Result.Failure<OrderDiscountContext>(Error.Validation("Order_DiscountProfileNotAvailable"));
            counter = new DiscountOffer(DiscountSource.Profile, profile.Percent, profile.Name, ProfileId: profile.Id);
        }
        else if (choice.DiscountPercent is { } percent && percent != 0m)
        {
            if (percent is < 0m or > 100m)
                return Result.Failure<OrderDiscountContext>(Error.Validation("Order_DiscountOutOfRange"));
            counter = new DiscountOffer(DiscountSource.Manual, percent, string.Empty);
        }

        if (counter is not null && requireReason && string.IsNullOrWhiteSpace(choice.DiscountReason))
            return Result.Failure<OrderDiscountContext>(Error.Validation("Order_DiscountReasonRequired"));

        IReadOnlyList<PartnershipPrice> negotiated = convention is null
            ? []
            : await prices.ListAsync(p => p.PartnershipId == convention.Id && !p.IsDeleted, ct);

        return Result.Success(new OrderDiscountContext(convention, automatic, counter,
                                                       DiscountRules.Applied(automatic, counter), negotiated));
    }

    /// <summary>The ceiling of Paramètres › Factures; the default when it cannot be read.</summary>
    public async Task<DiscountCapSettings> CapAsync(CancellationToken ct = default) =>
        settings is null
            ? DiscountCapSettings.Default
            : DiscountCapSettings.FromValues(await settings.GetSettingValuesAsync(DiscountCapSettings.Prefix, ct));

    /// <summary>
    /// The approval the order's discount needs: none (null) within the ceiling or for an automatic
    /// rate; above it, another user holding <c>billing.discount.approve</c>, verified here.
    /// </summary>
    public async Task<Result<ApproverIdentity?>> ApproveAsync(OrderDiscountContext context, OrderPricing pricing,
                                                              DiscountApproval? approval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pricing);

        if (context.Applied is null || !DiscountRules.IsDiscretionary(context.Applied.Source))
            return Result.Success<ApproverIdentity?>(null);

        var cap = await CapAsync(ct);
        if (!DiscountRules.NeedsApproval(context.Applied, pricing, cap)) return Result.Success<ApproverIdentity?>(null);

        var verified = await DiscountApprovals.VerifyAsync(authentication, currentUser, approval,
                                                           DiscountApprovals.Required(cap, pricing.Gross.Amount, currentUser.Language), ct);
        return verified.IsFailure
            ? Result.Failure<ApproverIdentity?>(verified.Errors)
            : Result.Success<ApproverIdentity?>(verified.Value);
    }

    /// <summary>The discount as the counter shows it, once the request is priced.</summary>
    public static AppliedDiscount? Describe(OrderDiscountContext context, OrderPricing pricing, DiscountCapSettings cap)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(cap);

        if (pricing.Discount.IsZero && context.Convention is null) return null;

        var applied = context.Applied;
        var source = pricing.RateDiscount.IsZero && !pricing.NegotiatedDiscount.IsZero
            ? DiscountSource.Convention
            : applied?.Source ?? (context.Convention is null ? DiscountSource.None : DiscountSource.Convention);

        var label = source == DiscountSource.Convention && (applied is null || applied.Source == DiscountSource.Convention || pricing.RateDiscount.IsZero)
            ? context.Convention?.PartnerName ?? applied?.Label ?? string.Empty
            : applied?.Label ?? string.Empty;

        return new AppliedDiscount(source,
                                   pricing.RateDiscount.IsZero ? null : applied?.Percent,
                                   label, pricing.Discount, context.Convention?.Id, context.Convention?.PartnerName,
                                   DiscountRules.NeedsApproval(applied, pricing, cap),
                                   Money.Of(cap.CapFor(pricing.Gross.Amount)));
    }

    /// <summary>
    /// Writes the discount onto the order: its rate, source, amount, reason, convention, profile and
    /// approver. The reason is the counter's for a profile or a rate typed, else the source's own.
    /// </summary>
    public static void Stamp(LabOrder order, OrderDiscountContext context, OrderPricing pricing, string? counterReason,
                             ApproverIdentity? approver)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pricing);

        var applied = pricing.RateDiscount.IsZero ? null : context.Applied;

        order.PartnershipId = context.Convention?.Id;
        order.DiscountAmount = pricing.Discount.Amount;
        order.DiscountApprovedByUserId = approver?.UserId;
        order.DiscountProfileId = applied?.Source == DiscountSource.Profile ? applied.ProfileId : null;

        if (applied is not null)
        {
            order.DiscountPercent = applied.Percent;
            order.DiscountSource = applied.Source;
            order.DiscountReason = DiscountRules.IsDiscretionary(applied.Source)
                ? counterReason?.Trim()
                : DiscountRules.ReasonFor(applied);
        }
        else if (!pricing.NegotiatedDiscount.IsZero && context.Convention is { } convention)
        {
            order.DiscountPercent = null;
            order.DiscountSource = DiscountSource.Convention;
            order.DiscountReason = "Tarifs négociés : " + convention.PartnerName;
        }
        else
        {
            order.DiscountPercent = null;
            order.DiscountSource = DiscountSource.None;
            order.DiscountReason = null;
        }
    }

    /// <summary>What the audit trail says of an order's discount, in French figures.</summary>
    public static string AuditText(LabOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.DiscountAmount <= 0m) return string.Empty;

        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var rate = order.DiscountPercent is { } percent ? $"{percent.ToString("0.##", fr)} %, " : string.Empty;
        return $" ; remise ({SourceText(order.DiscountSource)}) : {rate}{order.DiscountAmount.ToString("N2", fr)} DH — {order.DiscountReason}";
    }

    private static string SourceText(DiscountSource source) => source switch
    {
        DiscountSource.Convention => "convention",
        DiscountSource.Prescriber => "prescripteur",
        DiscountSource.Profile => "profil",
        DiscountSource.Manual => "saisie",
        _ => "aucune"
    };
}
