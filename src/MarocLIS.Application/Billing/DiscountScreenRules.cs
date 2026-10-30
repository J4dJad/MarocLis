using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Relations;

namespace MarocLIS.Application.Billing;

/// <summary>What « Remises accordées » offers: computing to whoever may read the report, exporting once computed.</summary>
public sealed record DiscountReportActions(bool Compute, bool Export);

/// <summary>A discount profile as its editor holds it: the rate and the dates as typed.</summary>
public sealed record DiscountProfileForm
{
    public string Name { get; init; } = string.Empty;
    public string? Percent { get; init; }
    public bool IsActive { get; init; } = true;
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; init; }
}

/// <summary>A negotiated price as its editor holds it: the analysis chosen, and a price or a rate as typed.</summary>
public sealed record PartnershipPriceForm
{
    public Guid? AnalysisTestId { get; init; }

    /// <summary>True for a price in dirhams, false for a rate off the list price.</summary>
    public bool IsFixedPrice { get; init; } = true;

    public string? Value { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; init; }
}

/// <summary>What the counter's « Remise » dialog gives back: the convention and the counter's own discount.</summary>
/// <param name="PartnershipId">A convention chosen; null keeps the prescriber's.</param>
/// <param name="WithoutConvention">« Sans convention ».</param>
/// <param name="ProfileId">A profile chosen.</param>
/// <param name="Percent">A rate typed, without a profile.</param>
/// <param name="Approval">Another user's name and password, when the rate goes beyond the ceiling.</param>
public sealed record CounterDiscount(
    Guid? PartnershipId,
    bool WithoutConvention,
    Guid? ProfileId,
    decimal? Percent,
    string? Reason,
    DiscountApproval? Approval)
{
    public static readonly CounterDiscount None = new(null, false, null, null, null, null);

    /// <summary>Whether the counter grants a discount of its own (a profile or a rate).</summary>
    public bool GrantsDiscount => ProfileId is not null || Percent is > 0m;

    /// <summary>Whether anything differs from what applies by itself.</summary>
    public bool IsEmpty => this == None;
}

/// <summary>
/// The rules of the discount screens that need no window: who sees what (granting at the counter,
/// managing the profiles and the negotiated prices, reading the report), the counter's « Remise »
/// dialog read and checked, the line the counter's total shows, the editors of a profile and of a
/// negotiated price with each refusal beside its box, and the report — its key figures, its columns,
/// its filters by source and by text, its CSV and its file's name. The workstation and the web apply
/// the same ones; what is done is <see cref="DiscountService"/>'s and <see cref="OrderDiscounts"/>'.
/// </summary>
public static class DiscountScreenRules
{
    public const string PeriodField = "discounts-period";
    public const string ProfileNameField = "profile-name";
    public const string ProfilePercentField = "profile-percent";
    public const string ProfileUntilField = "profile-until";
    public const string PriceAnalysisField = "price-analysis";
    public const string PriceValueField = "price-value";
    public const string PriceUntilField = "price-until";
    public const string CounterPercentField = "discount-percent";
    public const string CounterReasonField = "discount-reason";
    public const string ApproverField = "approver";
    public const string ApproverPasswordField = "approver-password";

    /// <summary>The value of the convention list that keeps the prescriber's own (« Automatique »).</summary>
    public const string AutomaticConvention = "auto";

    /// <summary>The value of the convention list that takes none (« Sans convention »).</summary>
    public const string NoConvention = "none";

    /// <summary>The value of the profile list for a rate typed (« Autre taux »).</summary>
    public const string ManualProfile = "manual";

    // ---- Rights ------------------------------------------------------------------------------------

    /// <summary>« Remise… » at the counter: hidden on the web, greyed at the workstation, without <c>billing.discount</c>.</summary>
    public static bool CanGrant(Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        return has(Permissions.Billing.ApplyDiscount);
    }

    /// <summary>Creating and changing the profiles and the negotiated prices: <c>billing.tariffs.manage</c>.</summary>
    public static bool CanManage(Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        return has(Permissions.Billing.ManageTariffs);
    }

    /// <summary>Reading a convention's negotiated prices: with the conventions or with the tariffs.</summary>
    public static bool CanSeePrices(Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        return has(Permissions.Relations.View) || has(Permissions.Billing.ManageTariffs);
    }

    public static DiscountReportActions Actions(Func<string, bool> has, IReadOnlyList<DiscountReportRow>? rows)
    {
        ArgumentNullException.ThrowIfNull(has);
        var read = has(Permissions.Billing.ApproveDiscount) || has(Permissions.Steering.View);
        return new DiscountReportActions(read, read && rows is { Count: > 0 });
    }

    // ---- Wording -------------------------------------------------------------------------------------

    public static string SourceKey(DiscountSource source) => $"Discounts.Source.{source}";

    public static string Percent(decimal value, IFormatProvider culture) => value.ToString("0.##", culture) + " %";

    /// <summary>
    /// The line of the counter's total: « Remise — convention Clinique Al Amal (10 %) », « Remise —
    /// profil Personnel (50 %) · approbation requise »; null without a discount.
    /// </summary>
    public static string? CounterLine(AppliedDiscount? discount, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (discount is null || discount.Amount.IsZero) return null;

        var what = text(SourceKey(discount.Source));
        if (discount.Label is { Length: > 0 } label) what += " " + label;
        if (discount.Percent is { } percent) what += $" ({Percent(percent, culture)})";
        else if (discount.Source == DiscountSource.Convention) what += " (" + text("Discounts.NegotiatedPrices") + ")";

        var line = text("Discounts.CounterLine", what);
        return discount.ApprovalRequired ? line + " · " + text("Discounts.ApprovalRequired") : line;
    }

    // ---- The counter's dialog ----------------------------------------------------------------------------

    /// <summary>
    /// The rate the counter grants with the dialog as it stands: the profile's, or the rate typed;
    /// null when none or when the rate typed cannot be read.
    /// </summary>
    public static decimal? CounterPercent(string? profileChoice, IReadOnlyList<DiscountProfileRow> profiles, string? typed)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profileChoice == ManualProfile) return TypedNumber.Parse(typed?.Replace("%", string.Empty, StringComparison.Ordinal));
        return Guid.TryParse(profileChoice, out var id) ? profiles.FirstOrDefault(p => p.Id == id)?.Percent : null;
    }

    /// <summary>
    /// Whether the counter's rate, on <paramref name="gross"/>, goes beyond the ceiling: the
    /// dialog then asks for another user's name and password. Reckoned on the whole gross, so it asks
    /// whenever the save may need it (a negotiated price can only lower what the rate gives).
    /// </summary>
    public static bool NeedsApproval(decimal? percent, decimal gross, DiscountCapSettings cap)
    {
        ArgumentNullException.ThrowIfNull(cap);
        return percent is > 0m and <= 100m && cap.Exceeds(Money.Round(gross * percent.Value / 100m), gross);
    }

    /// <summary>
    /// The dialog read and checked: a rate typed between 0 and 100 %, a reason with a profile or a
    /// rate, and beyond the ceiling an approver and their password. A refusal names its box.
    /// </summary>
    public static (CounterDiscount? Discount, FieldRefusal? Refusal) ReadCounter(
        string? conventionChoice, string? profileChoice, IReadOnlyList<DiscountProfileRow> profiles, string? typedPercent,
        string? reason, string? approver, string? approverPassword, decimal gross, DiscountCapSettings cap)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(cap);

        Guid? partnershipId = Guid.TryParse(conventionChoice, out var convention) ? convention : null;
        var without = conventionChoice == NoConvention;

        Guid? profileId = null;
        decimal? percent = null;
        if (profileChoice == ManualProfile)
        {
            percent = TypedNumber.Parse(typedPercent?.Replace("%", string.Empty, StringComparison.Ordinal));
            if (percent is null or <= 0m or > 100m) return (null, new FieldRefusal(CounterPercentField, "Order_DiscountOutOfRange"));
        }
        else if (Guid.TryParse(profileChoice, out var chosen))
        {
            if (profiles.All(p => p.Id != chosen)) return (null, new FieldRefusal(CounterPercentField, "Order_DiscountProfileNotAvailable"));
            profileId = chosen;
        }

        var rate = profileId is { } id ? profiles.First(p => p.Id == id).Percent : percent;
        var grants = profileId is not null || percent is > 0m;
        if (grants && string.IsNullOrWhiteSpace(reason)) return (null, new FieldRefusal(CounterReasonField, "Order_DiscountReasonRequired"));
        if (reason is { } r && r.Trim().Length > BillingScreenRules.DiscountReasonMaxLength)
            return (null, new FieldRefusal(CounterReasonField, "Relations_TextTooLong", BillingScreenRules.DiscountReasonMaxLength.ToString(CultureInfo.InvariantCulture)));

        DiscountApproval? approval = null;
        if (grants && NeedsApproval(rate, gross, cap))
        {
            if (string.IsNullOrWhiteSpace(approver)) return (null, new FieldRefusal(ApproverField, "Approval_CredentialsRequired"));
            if (string.IsNullOrEmpty(approverPassword)) return (null, new FieldRefusal(ApproverPasswordField, "Approval_CredentialsRequired"));
            approval = new DiscountApproval(approver.Trim(), approverPassword);
        }

        return (new CounterDiscount(partnershipId, without, profileId, percent, grants ? reason!.Trim() : null, approval), null);
    }

    /// <summary>The convention list's value for a discount chosen: « auto », « none » or the convention's id.</summary>
    public static string ConventionChoice(CounterDiscount discount)
    {
        ArgumentNullException.ThrowIfNull(discount);
        return discount.WithoutConvention ? NoConvention
            : discount.PartnershipId is { } id ? id.ToString() : AutomaticConvention;
    }

    /// <summary>The profile list's value for a discount chosen: empty, « manual » or the profile's id.</summary>
    public static string ProfileChoice(CounterDiscount discount)
    {
        ArgumentNullException.ThrowIfNull(discount);
        return discount.ProfileId is { } id ? id.ToString() : discount.Percent is > 0m ? ManualProfile : string.Empty;
    }

    /// <summary>A profile as the counter's list writes it: « Personnel — 50 % ».</summary>
    public static string ProfileOption(DiscountProfileRow profile, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return $"{profile.Name} — {Percent(profile.Percent, culture)}";
    }

    /// <summary>A convention as the counter's list writes it: « Clinique Al Amal — 10 % ».</summary>
    public static string ConventionOption(ConventionOption convention, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(convention);
        return convention.DiscountPercent is { } percent ? $"{convention.PartnerName} — {Percent(percent, culture)}" : convention.PartnerName;
    }

    // ---- Profiles ------------------------------------------------------------------------------------

    public static IReadOnlyList<ReportColumn> ProfileColumns { get; } =
    [
        new("name", "Discounts.Column.name"), new("percent", "Discounts.Column.percent", true),
        new("period", "Discounts.Column.period"), new("state", "Discounts.Column.state")
    ];

    public static IReadOnlyList<ReportRow> ProfileRows(IReadOnlyList<DiscountProfileRow> profiles, ScreenText text,
                                                       IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(text);
        return [.. profiles.Select(p => new ReportRow(
            [p.Name, Percent(p.Percent, culture), PeriodText(p.ValidFrom, p.ValidUntil, text),
             text(!p.IsActive ? "Discounts.State.Inactive" : p.AvailableToday ? "Discounts.State.Offered" : "Discounts.State.OutOfDates")],
            p.AvailableToday ? StockInk.Plain : StockInk.Muted))];
    }

    public static DiscountProfileForm FormFor(DiscountProfileRow? row, IFormatProvider culture) =>
        row is null
            ? new DiscountProfileForm()
            : new DiscountProfileForm
            {
                Name = row.Name,
                Percent = TypedNumber.ForEditing(row.Percent, 0, culture),
                IsActive = row.IsActive,
                ValidFrom = row.ValidFrom,
                ValidUntil = row.ValidUntil
            };

    public static (FieldRefusal? Refusal, SaveDiscountProfileRequest? Request) Check(DiscountProfileForm form, Guid? profileId)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(form.Name)) return (new FieldRefusal(ProfileNameField, "DiscountProfile_NameRequired"), null);
        if (form.Name.Trim().Length > Domain.Billing.DiscountProfile.NameMaxLength)
            return (new FieldRefusal(ProfileNameField, "Relations_TextTooLong", Domain.Billing.DiscountProfile.NameMaxLength.ToString(CultureInfo.InvariantCulture)), null);

        var percent = TypedNumber.Parse(form.Percent?.Replace("%", string.Empty, StringComparison.Ordinal));
        if (percent is null or <= 0m or > 100m) return (new FieldRefusal(ProfilePercentField, "DiscountProfile_PercentOutOfRange"), null);
        if (form.ValidFrom is { } from && form.ValidUntil is { } until && until < from)
            return (new FieldRefusal(ProfileUntilField, "DiscountProfile_UntilBeforeFrom"), null);

        return (null, new SaveDiscountProfileRequest
        {
            Id = profileId, Name = form.Name.Trim(), Percent = percent.Value, IsActive = form.IsActive,
            ValidFrom = form.ValidFrom, ValidUntil = form.ValidUntil
        });
    }

    // ---- Negotiated prices -------------------------------------------------------------------------------

    public static IReadOnlyList<ReportColumn> PriceColumns { get; } =
    [
        new("code", "Discounts.Column.code"), new("analysis", "Discounts.Column.analysis"),
        new("list", "Discounts.Column.listPrice", true), new("deal", "Discounts.Column.deal", true),
        new("price", "Discounts.Column.negotiatedPrice", true), new("period", "Discounts.Column.period")
    ];

    public static IReadOnlyList<ReportRow> PriceRows(IReadOnlyList<PartnershipPriceRow> prices, ScreenText text,
                                                     Func<decimal, string> amount, AppLanguage language, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return [.. prices.Select(p => new ReportRow(
            [p.AnalysisCode, p.AnalysisName.For(language),
             p.ListPrice is { } list ? amount(list) : "—",
             p.FixedPrice is { } fixedPrice ? amount(fixedPrice) : "−" + Percent(p.DiscountPercent ?? 0m, culture),
             p.NegotiatedPrice is { } price ? amount(price) : "—",
             PeriodText(p.ValidFrom, p.ValidUntil, text)],
            p.InForce ? StockInk.Plain : StockInk.Muted))];
    }

    public static PartnershipPriceForm PriceFormFor(PartnershipPriceRow? row, DateOnly today, IFormatProvider culture) =>
        row is null
            ? new PartnershipPriceForm { ValidFrom = today }
            : new PartnershipPriceForm
            {
                AnalysisTestId = row.AnalysisTestId,
                IsFixedPrice = row.FixedPrice is not null,
                Value = TypedNumber.ForEditing(row.FixedPrice ?? row.DiscountPercent ?? 0m, row.FixedPrice is null ? 0 : 2, culture),
                ValidFrom = row.ValidFrom,
                ValidUntil = row.ValidUntil
            };

    public static (FieldRefusal? Refusal, SavePartnershipPriceRequest? Request) Check(PartnershipPriceForm form, Guid partnershipId,
                                                                                    Guid? priceId)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.AnalysisTestId is not { } analysisId || analysisId == Guid.Empty)
            return (new FieldRefusal(PriceAnalysisField, "PartnershipPrice_AnalysisRequired"), null);

        var value = TypedNumber.Parse(form.Value?.Replace("%", string.Empty, StringComparison.Ordinal));
        if (form.IsFixedPrice && value is null or < 0m or > PartnershipPrice.PriceLimit)
            return (new FieldRefusal(PriceValueField, "PartnershipPrice_PriceOutOfRange"), null);
        if (!form.IsFixedPrice && value is null or <= 0m or > 100m)
            return (new FieldRefusal(PriceValueField, "PartnershipPrice_PercentOutOfRange"), null);
        if (form.ValidUntil is { } until && until < form.ValidFrom)
            return (new FieldRefusal(PriceUntilField, "PartnershipPrice_UntilBeforeFrom"), null);

        return (null, new SavePartnershipPriceRequest
        {
            Id = priceId,
            PartnershipId = partnershipId,
            AnalysisTestId = analysisId,
            FixedPrice = form.IsFixedPrice ? value : null,
            DiscountPercent = form.IsFixedPrice ? null : value,
            ValidFrom = form.ValidFrom,
            ValidUntil = form.ValidUntil
        });
    }

    /// <summary>A service's refusal beside the box it names, in the profile or price editor.</summary>
    public static string? FieldOf(string resourceKey) => resourceKey switch
    {
        "DiscountProfile_NameRequired" or "DiscountProfile_NameTaken" => ProfileNameField,
        "DiscountProfile_PercentOutOfRange" => ProfilePercentField,
        "DiscountProfile_UntilBeforeFrom" => ProfileUntilField,
        "PartnershipPrice_AnalysisRequired" or "PartnershipPrice_Overlap" => PriceAnalysisField,
        "PartnershipPrice_PriceOutOfRange" or "PartnershipPrice_PercentOutOfRange" or "PartnershipPrice_PriceOrPercent" => PriceValueField,
        "PartnershipPrice_UntilBeforeFrom" => PriceUntilField,
        "Stats_PeriodReversed" or "Stats_PeriodTooLong" => PeriodField,
        _ => null
    };

    // ---- The report ------------------------------------------------------------------------------------

    /// <summary>The report's source filters: every source, then each one.</summary>
    public static IReadOnlyList<DiscountSource?> SourceFilters { get; } =
        [null, DiscountSource.Convention, DiscountSource.Prescriber, DiscountSource.Profile, DiscountSource.Manual];

    public static string FilterKey(DiscountSource? source) => source is { } s ? SourceKey(s) : "Discounts.AllSources";

    public static IReadOnlyList<ReportColumn> Columns { get; } =
    [
        new("date", "Discounts.Column.date"), new("reference", "Discounts.Column.reference"),
        new("patient", "Discounts.Column.patient"), new("source", "Discounts.Column.source"),
        new("detail", "Discounts.Column.detail"), new("percent", "Discounts.Column.percent", true),
        new("amount", "Discounts.Column.amount", true), new("gross", "Discounts.Column.gross", true),
        new("reason", "Discounts.Column.reason"), new("grantedBy", "Discounts.Column.grantedBy"),
        new("approvedBy", "Discounts.Column.approvedBy")
    ];

    public static bool IsIdentifier(string column) => column is "date" or "reference";

    /// <summary>
    /// The rows a source and a text keep: the text is looked for in the reference, the patient, the
    /// detail, the reason and both users, ignoring case and accents.
    /// </summary>
    public static IReadOnlyList<DiscountReportRow> Filter(IReadOnlyList<DiscountReportRow>? rows, DiscountSource? source, string? term)
    {
        if (rows is null) return [];
        var wanted = Fold(term);
        return [.. rows.Where(r => (source is null || r.Source == source)
                                   && (wanted.Length == 0 || new[] { r.Reference, r.PatientName, r.MedicalRecordNumber, r.Label, r.Reason, r.GrantedBy, r.ApprovedBy }
                                       .Any(v => Fold(v).Contains(wanted, StringComparison.Ordinal))))];
    }

    public static IReadOnlyList<ReportRow> Rows(IReadOnlyList<DiscountReportRow> rows, ScreenText text, Func<decimal, string> amount,
                                                IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return [.. rows.Select(r => new ReportRow(
            [r.GrantedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
             r.Reference,
             r.MedicalRecordNumber is { Length: > 0 } record ? $"{r.PatientName} ({record})" : r.PatientName,
             text(SourceKey(r.Source)) + (r.OnInvoice ? " · " + text("Discounts.OnInvoice") : string.Empty),
             r.Label ?? string.Empty,
             r.Percent is { } percent ? Percent(percent, culture) : "—",
             amount(r.Amount), amount(r.Gross),
             r.Reason ?? string.Empty, r.GrantedBy ?? string.Empty, r.ApprovedBy ?? string.Empty],
            r.ApprovedBy is not null ? StockInk.Abnormal : StockInk.Plain))];
    }

    /// <summary>The key figures: the total granted, its share of the gross, how many, approved beyond the ceiling, and by source.</summary>
    public static IReadOnlyList<ReportTile> Tiles(IReadOnlyList<DiscountReportRow> rows, ScreenText text, Func<decimal, string> amount,
                                                  IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);

        var total = rows.Sum(r => r.Amount);
        var gross = rows.Sum(r => r.Gross);
        decimal Of(params DiscountSource[] sources) => rows.Where(r => sources.Contains(r.Source)).Sum(r => r.Amount);

        return
        [
            new("total", text("Discounts.Tile.Total"), amount(total),
                gross > 0m ? text("Discounts.Tile.OfGross", Percent(Math.Round(total * 100m / gross, 1), culture)) : null),
            new("count", text("Discounts.Tile.Count"), PeriodReport.Count(rows.Count, culture)),
            new("approved", text("Discounts.Tile.Approved"), PeriodReport.Count(rows.Count(r => r.ApprovedBy is not null), culture),
                null, rows.Any(r => r.ApprovedBy is not null) ? StockInk.Abnormal : StockInk.Plain),
            new("agreed", text("Discounts.Tile.Agreed"), amount(Of(DiscountSource.Convention, DiscountSource.Prescriber))),
            new("granted", text("Discounts.Tile.Granted"), amount(Of(DiscountSource.Profile, DiscountSource.Manual)))
        ];
    }

    public static byte[]? Csv(IReadOnlyList<DiscountReportRow> rows, ScreenText text, Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PeriodReport.Csv([.. Columns.Select(c => text(c.CaptionKey))], Rows(rows, text, amount, culture).Select(r => r.Cells));
    }

    /// <summary>« remises-20261001-20261031.csv ».</summary>
    public static string FileName(DateOnly from, DateOnly to) => $"remises-{PeriodReport.FileStamp(from, to)}.csv";

    // ---- Helpers ---------------------------------------------------------------------------------------

    private static string PeriodText(DateOnly? from, DateOnly? until, ScreenText text) =>
        (from, until) switch
        {
            (null, null) => text("Discounts.Always"),
            ({ } f, null) => text("Discounts.Since", PeriodReport.Date(f)),
            (null, { } u) => text("Discounts.Until", PeriodReport.Date(u)),
            ({ } f, { } u) => PeriodReport.Period(f, u)
        };

    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Trim().Normalize(System.Text.NormalizationForm.FormD);
        return new string([.. decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)])
            .ToUpperInvariant();
    }
}
