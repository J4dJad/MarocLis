using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Relations;

namespace MarocLIS.Application.Relations;

/// <summary>The three lists of « Visites et conventions ».</summary>
public enum RelationsTab
{
    Visits,
    DueVisits,
    Partnerships
}

/// <summary>Where a convention stands today, as its pill says it.</summary>
public enum PartnershipState
{
    InForce,
    EndingSoon,
    NotInForce,
    Inactive
}

/// <summary>
/// What « Visites et conventions » offers. The visit buttons are offered on the visit lists and the
/// convention buttons on the conventions, both only to whom may record them (the right to manage the
/// relations); a change or a deletion needs a row to act on, opening the signed convention a
/// convention that has one — anyone who may see the relations opens it.
/// </summary>
public sealed record RelationsActions(
    bool VisitActions,
    bool PartnershipActions,
    bool EditVisit,
    bool DeleteVisit,
    bool EditPartnership,
    bool Attach,
    bool OpenFile,
    bool ShowInactive);

/// <summary>One fact of a visit's or a convention's file: its label, its value, and whether the value is a code or a date (in the fixed face).</summary>
public sealed record RelationFact(string Label, string Value, bool Mono = false);

/// <summary>A visit as its editor holds it.</summary>
public sealed record VisitForm(DateOnly VisitDate, Guid PrescriberId, Guid DelegateUserId, VisitPurpose Purpose, string? Notes,
                               DateOnly? NextVisitOn);

/// <summary>A convention as its editor holds it: the discount and the payment delay as typed.</summary>
public sealed record PartnershipForm
{
    public string PartnerName { get; init; } = string.Empty;
    public PartnerKind PartnerKind { get; init; } = PartnerKind.Clinic;
    public Guid? PrescriberId { get; init; }
    public PartnershipKind Kind { get; init; } = PartnershipKind.CareConvention;
    public string? Reference { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? Terms { get; init; }
    public string? Discount { get; init; }
    public string? PaymentDays { get; init; }
    public string? Ice { get; init; }
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    public string? ContactEmail { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// The rules of « Visites et conventions » that need no window: what is offered by which right, the
/// lists' columns and cells (a next visit come due in orange, a convention ending soon on the
/// warning's colour with its pill), the counts beside the title, the visit and convention editors'
/// checks and requests with each refusal beside its box, the signed convention accepted or refused,
/// the deletion's question, and the list's CSV and its file's name. The workstation's RelationsView
/// and the web's screen apply the same ones; what is done is RelationsService's.
/// </summary>
public static class RelationsScreenRules
{
    public const string PeriodField = "relations-period";
    public const string VisitDateField = "visit-date";
    public const string VisitPrescriberField = "visit-prescriber";
    public const string VisitDelegateField = "visit-delegate";
    public const string VisitNotesField = "visit-notes";
    public const string VisitNextField = "visit-next";
    public const string PartnerNameField = "partner-name";
    public const string PartnerStartField = "partner-start";
    public const string PartnerEndField = "partner-end";
    public const string PartnerTermsField = "partner-terms";
    public const string PartnerDiscountField = "partner-discount";
    public const string PartnerPaymentField = "partner-payment";
    public const string PartnerIceField = "partner-ice";
    public const string DocumentField = "partner-document";

    public const int PartnerNameMaxLength = 200;
    public const int ReferenceMaxLength = 60;
    public const int IceMaxLength = 20;
    public const int ContactNameMaxLength = 150;
    public const int PhoneMaxLength = 30;
    public const int EmailMaxLength = 200;
    public const int PartnershipNotesMaxLength = 1000;
    public const int NumberMaxLength = 6;

    /// <summary>The extensions the signed convention may have, for the file picker.</summary>
    public static IReadOnlyList<string> DocumentExtensions { get; } =
        [.. SafeDocumentFile.PickerPattern.Split(';').Select(p => p.TrimStart('*'))];

    public static string DocumentAccept => string.Join(",", DocumentExtensions);

    public static RelationsActions Actions(Func<string, bool> has, RelationsTab tab, DelegateVisitRow? visit,
                                           PartnershipRow? partnership)
    {
        ArgumentNullException.ThrowIfNull(has);
        var view = has(Permissions.Relations.View);
        var manage = has(Permissions.Relations.Manage);
        var onPartnerships = tab == RelationsTab.Partnerships;

        return new RelationsActions(
            VisitActions: manage && !onPartnerships,
            PartnershipActions: manage && onPartnerships,
            EditVisit: manage && !onPartnerships && visit is not null,
            DeleteVisit: manage && !onPartnerships && visit is not null,
            EditPartnership: manage && onPartnerships && partnership is not null,
            Attach: manage && onPartnerships && partnership is not null,
            OpenFile: view && onPartnerships && partnership is { FileName: not null },
            ShowInactive: onPartnerships);
    }

    public static string TabKey(RelationsTab tab) => tab switch
    {
        RelationsTab.DueVisits => "Relations.DueVisits",
        RelationsTab.Partnerships => "Relations.Partnerships",
        _ => "Relations.Visits"
    };

    public static string ColumnKey(string column) => $"Relations.Column.{column}";

    private static ReportColumn Column(string name, bool numeric = false) => new(name, ColumnKey(name), numeric);

    /// <summary>The columns of a list, in order: their captions are « Relations.Column.{name} ».</summary>
    public static IReadOnlyList<ReportColumn> Columns(RelationsTab tab) => tab == RelationsTab.Partnerships
        ?
        [
            Column("partner"), Column("partnerKind"), Column("kind"), Column("reference"), Column("start"), Column("end"),
            Column("state"), Column("discount", true), Column("contact"), Column("file")
        ]
        : [Column("date"), Column("prescriber"), Column("institution"), Column("delegate"), Column("purpose"), Column("notes"), Column("next")];

    /// <summary>The columns read as identifiers, in the fixed-pitch face: dates and references.</summary>
    public static bool IsIdentifier(string column) => column is "date" or "next" or "start" or "end" or "reference";

    public static string Date(DateOnly? date) => date is { } d ? PeriodReport.Date(d) : string.Empty;

    /// <summary>« 3 visites · 1 à prévoir · 2 conventions », beside the title.</summary>
    public static string Counts(int visits, int due, int partnerships, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Relations.Counts", visits, due, partnerships);
    }

    /// <summary>A visit's cells in the columns' order: its purpose in the reader's language.</summary>
    public static IReadOnlyList<string> Cells(DelegateVisitRow visit, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(visit);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            Date(visit.VisitDate), visit.PrescriberName, visit.Institution ?? string.Empty, visit.DelegateName,
            text($"VisitPurpose.{visit.Purpose}"), visit.Notes ?? string.Empty, Date(visit.NextVisitOn)
        ];
    }

    /// <summary>The next visit come due and not yet made: in orange.</summary>
    public static bool NextVisitDue(DelegateVisitRow visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        return visit.NextVisitDue;
    }

    public static PartnershipState State(PartnershipRow partnership)
    {
        ArgumentNullException.ThrowIfNull(partnership);
        return !partnership.IsActive ? PartnershipState.Inactive
            : partnership.EndingSoon ? PartnershipState.EndingSoon
            : partnership.InForce ? PartnershipState.InForce
            : PartnershipState.NotInForce;
    }

    public static string StateKey(PartnershipState state) => $"Relations.State.{state}";

    /// <summary>The pill of a state: ✓ in force, ! ending soon, ○ out of its dates or switched off.</summary>
    public static StockTone Tone(PartnershipState state) => state switch
    {
        PartnershipState.InForce => StockTone.Done,
        PartnershipState.EndingSoon => StockTone.Warning,
        _ => StockTone.Waiting
    };

    /// <summary>A convention ending within <see cref="Partnership.EndingSoonDays"/> days: its whole row on the warning's colour.</summary>
    public static bool EndingSoon(PartnershipRow partnership)
    {
        ArgumentNullException.ThrowIfNull(partnership);
        return partnership.EndingSoon;
    }

    /// <summary>« 12,5 % », the agreed discount; nothing when none was agreed.</summary>
    public static string Discount(decimal? percent, IFormatProvider culture) =>
        percent is { } d ? string.Format(culture, "{0:0.##} %", d) : string.Empty;

    /// <summary>« Mme Bennani · 0522 45 67 89 », whichever of the two is known.</summary>
    public static string Contact(PartnershipRow partnership)
    {
        ArgumentNullException.ThrowIfNull(partnership);
        return string.Join(" · ", new[] { partnership.ContactName, partnership.ContactPhone }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>A convention's cells in the columns' order, its kinds and state in the reader's language.</summary>
    public static IReadOnlyList<string> Cells(PartnershipRow partnership, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(partnership);
        ArgumentNullException.ThrowIfNull(text);
        var p = partnership;
        return
        [
            p.PartnerName, text($"PartnerKind.{p.PartnerKind}"), text($"PartnershipKind.{p.Kind}"), p.Reference ?? string.Empty,
            Date(p.StartDate), Date(p.EndDate), text(StateKey(State(p))), Discount(p.DiscountPercent, culture), Contact(p),
            p.FileName ?? string.Empty
        ];
    }

    /// <summary>A record already named by the visit or convention, since switched off, stays offered and says so.</summary>
    public static string Display(RelationLookup lookup, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(text);
        return lookup.IsActive ? lookup.Display : $"{lookup.Display} {text("Contracts.InactiveSuffix")}";
    }

    // ---- The file of the row selected ----------------------------------------------------------------

    /// <summary>
    /// The visit selected, under the list: when, whom, by whom, why and the next visit (its notes are
    /// read in full beside it).
    /// </summary>
    public static IReadOnlyList<RelationFact> Facts(DelegateVisitRow visit, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(visit);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            new(text(ColumnKey("date")), Date(visit.VisitDate), true),
            new(text(ColumnKey("prescriber")), visit.PrescriberName),
            new(text(ColumnKey("institution")), visit.Institution ?? "—"),
            new(text(ColumnKey("delegate")), visit.DelegateName),
            new(text(ColumnKey("purpose")), text($"VisitPurpose.{visit.Purpose}")),
            new(text(ColumnKey("next")), visit.NextVisitOn is { } next ? Date(next) : "—", visit.NextVisitOn is not null)
        ];
    }

    /// <summary>
    /// The convention selected, under the list: the partner and the agreement, its period and state,
    /// the terms agreed in figures, the partner's identifier and whom to call (its terms and notes are
    /// read in full beside it).
    /// </summary>
    public static IReadOnlyList<RelationFact> Facts(PartnershipDetail detail, PartnershipRow? row, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(text);
        string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
        return
        [
            new(text(ColumnKey("partnerKind")), text($"PartnerKind.{detail.PartnerKind}")),
            new(text(ColumnKey("kind")), text($"PartnershipKind.{detail.Kind}")),
            new(text(ColumnKey("reference")), Or(detail.Reference), detail.Reference is not null),
            new(text(ColumnKey("start")), Date(detail.StartDate), true),
            new(text(ColumnKey("end")), detail.EndDate is { } end ? Date(end) : "—", detail.EndDate is not null),
            new(text(ColumnKey("discount")), Or(Discount(detail.DiscountPercent, culture))),
            new(text("Relations.PaymentTermsDays"), detail.PaymentTermsDays is { } days ? days.ToString(culture) : "—"),
            new(text("Billing.Ice"), Or(detail.PartnerIce), detail.PartnerIce is not null),
            new(text("Relations.ContactName"), Or(detail.ContactName)),
            new(text("Field.Phone"), Or(detail.ContactPhone), detail.ContactPhone is not null),
            new(text("Field.Email"), Or(detail.ContactEmail), detail.ContactEmail is not null),
            new(text(ColumnKey("file")), Or(row?.FileName))
        ];
    }

    // ---- Visits --------------------------------------------------------------------------------------

    /// <summary>A new visit is today's, by the operator, a follow-up; an existing one as recorded.</summary>
    public static VisitForm FormFor(DelegateVisitRow? visit, Guid currentUserId, DateOnly today) =>
        visit is null
            ? new VisitForm(today, Guid.Empty, currentUserId, VisitPurpose.FollowUp, null, null)
            : new VisitForm(visit.VisitDate, visit.PrescriberId, visit.DelegateUserId, visit.Purpose, visit.Notes, visit.NextVisitOn);

    /// <summary>
    /// The visit typed, checked: a prescriber and a delegate chosen, no date ahead of today, the next
    /// visit after this one, the notes within their column. A refusal names its box.
    /// </summary>
    public static (FieldRefusal? Refusal, SaveDelegateVisitRequest? Request) Check(VisitForm form, Guid? visitId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.PrescriberId == Guid.Empty) return (new FieldRefusal(VisitPrescriberField, "Visit_PrescriberRequired"), null);
        if (form.DelegateUserId == Guid.Empty) return (new FieldRefusal(VisitDelegateField, "Visit_DelegateRequired"), null);
        if (form.VisitDate > today) return (new FieldRefusal(VisitDateField, "Visit_DateInFuture"), null);
        if (form.NextVisitOn is { } next && next <= form.VisitDate) return (new FieldRefusal(VisitNextField, "Visit_NextBeforeVisit"), null);
        if (form.Notes is { } notes && notes.Trim().Length > DelegateVisit.NotesMaxLength)
            return (TooLong(VisitNotesField, DelegateVisit.NotesMaxLength), null);

        return (null, new SaveDelegateVisitRequest
        {
            Id = visitId,
            VisitDate = form.VisitDate,
            PrescriberId = form.PrescriberId,
            DelegateUserId = form.DelegateUserId,
            Purpose = form.Purpose,
            Notes = Blank(form.Notes),
            NextVisitOn = form.NextVisitOn
        });
    }

    /// <summary>« Confirmez-vous la suppression de « 12/10/2026 — Dr Alami » ? », the reason asked with it.</summary>
    public static string DeletePrompt(DelegateVisitRow visit, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(visit);
        ArgumentNullException.ThrowIfNull(text);
        return text("Message.ConfirmDelete", $"{Date(visit.VisitDate)} — {visit.PrescriberName}");
    }

    // ---- Conventions ---------------------------------------------------------------------------------

    /// <summary>A new convention is a care convention with a clinic from today; an existing one as recorded.</summary>
    public static PartnershipForm FormFor(PartnershipDetail? detail, DateOnly today, IFormatProvider culture)
    {
        if (detail is null) return new PartnershipForm { StartDate = today };
        return new PartnershipForm
        {
            PartnerName = detail.PartnerName,
            PartnerKind = detail.PartnerKind,
            PrescriberId = detail.PrescriberId,
            Kind = detail.Kind,
            Reference = detail.Reference,
            StartDate = detail.StartDate,
            EndDate = detail.EndDate,
            Terms = detail.Terms,
            Discount = detail.DiscountPercent is { } d ? TypedNumber.ForEditing(d, 0, culture) : null,
            PaymentDays = detail.PaymentTermsDays?.ToString(CultureInfo.InvariantCulture),
            Ice = detail.PartnerIce,
            ContactName = detail.ContactName,
            ContactPhone = detail.ContactPhone,
            ContactEmail = detail.ContactEmail,
            Notes = detail.Notes,
            IsActive = detail.IsActive
        };
    }

    /// <summary>
    /// The convention typed, checked: a partner named, the end not before the start, a discount
    /// between 0 and 100 % (none when 0 or empty), a payment delay of whole days up to a year (none
    /// when 0 or empty), a valid ICE, the texts within their columns. A refusal names its box.
    /// </summary>
    public static (FieldRefusal? Refusal, SavePartnershipRequest? Request) Check(PartnershipForm form, Guid? partnershipId)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(form.PartnerName)) return (new FieldRefusal(PartnerNameField, "Partnership_PartnerRequired"), null);
        if (form.EndDate is { } end && end < form.StartDate) return (new FieldRefusal(PartnerEndField, "Partnership_EndBeforeStart"), null);

        decimal? discount = null;
        if (!string.IsNullOrWhiteSpace(form.Discount))
        {
            discount = TypedNumber.Parse(form.Discount);
            if (discount is null or < 0m or > 100m) return (new FieldRefusal(PartnerDiscountField, "Partnership_DiscountOutOfRange"), null);
            if (discount == 0m) discount = null;
        }

        int? days = null;
        if (!string.IsNullOrWhiteSpace(form.PaymentDays))
        {
            var typed = TypedNumber.Parse(form.PaymentDays);
            if (typed is null or < 0m or > 365m || typed != decimal.Truncate(typed.Value))
                return (new FieldRefusal(PartnerPaymentField, "Partnership_PaymentTermsOutOfRange"), null);
            days = typed == 0m ? null : (int)typed.Value;
        }

        var ice = Blank(form.Ice);
        if (ice is not null && !Domain.Morocco.MoroccanIdentifiers.IsValidIce(ice))
            return (new FieldRefusal(PartnerIceField, "Prescriber_IceInvalid"), null);
        if (form.Terms is { } terms && terms.Trim().Length > Partnership.TermsMaxLength)
            return (TooLong(PartnerTermsField, Partnership.TermsMaxLength), null);

        return (null, new SavePartnershipRequest
        {
            Id = partnershipId,
            PartnerName = form.PartnerName.Trim(),
            PartnerKind = form.PartnerKind,
            PrescriberId = form.PrescriberId is { } p && p != Guid.Empty ? p : null,
            PartnerIce = ice,
            ContactName = Blank(form.ContactName),
            ContactPhone = Blank(form.ContactPhone),
            ContactEmail = Blank(form.ContactEmail),
            Kind = form.Kind,
            Reference = Blank(form.Reference),
            StartDate = form.StartDate,
            EndDate = form.EndDate,
            Terms = Blank(form.Terms),
            DiscountPercent = discount,
            PaymentTermsDays = days,
            Notes = Blank(form.Notes),
            IsActive = form.IsActive
        });
    }

    /// <summary>
    /// The signed convention chosen: a document that cannot run anything (PDF, image, Word or Excel
    /// without macros), not empty, within <see cref="PartnershipDocument.MaximumBytes"/>.
    /// </summary>
    public static FieldRefusal? CheckDocument(string? fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName) || length <= 0) return new FieldRefusal(DocumentField, "Purchasing_FileEmpty");
        if (SafeDocumentFile.ContentTypeFor(fileName) is null) return new FieldRefusal(DocumentField, "Purchasing_FileType");
        if (length > PartnershipDocument.MaximumBytes)
            return new FieldRefusal(DocumentField, "Purchasing_FileTooLarge",
                                    (PartnershipDocument.MaximumBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>A service's refusal beside the box it names: in the visit editor, or in the convention's.</summary>
    public static string? FieldOf(string resourceKey, bool partnership) => resourceKey switch
    {
        "Visit_DateInFuture" => VisitDateField,
        "Visit_NextBeforeVisit" => VisitNextField,
        "Visit_PrescriberRequired" => VisitPrescriberField,
        "Visit_DelegateRequired" => VisitDelegateField,
        "Relations_TextTooLong" => partnership ? PartnerTermsField : VisitNotesField,
        "Partnership_PartnerRequired" => PartnerNameField,
        "Partnership_EndBeforeStart" => PartnerEndField,
        "Partnership_DiscountOutOfRange" => PartnerDiscountField,
        "Partnership_PaymentTermsOutOfRange" => PartnerPaymentField,
        "Prescriber_IceInvalid" => PartnerIceField,
        "Purchasing_FileEmpty" or "Purchasing_FileType" or "Purchasing_FileTooLarge" => DocumentField,
        _ => null
    };

    /// <summary>The box a refusal of the visits' period names: reversed.</summary>
    public static string? FieldOf(Error? error) => PeriodReport.IsPeriodRefusal(error) ? PeriodField : null;

    // ---- Export --------------------------------------------------------------------------------------

    /// <summary>The list as CSV, its captions first, or nothing when it is empty (« Aucune donnée »).</summary>
    public static byte[]? Csv(RelationsTab tab, IReadOnlyList<DelegateVisitRow> visits, IReadOnlyList<PartnershipRow> partnerships,
                              ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(visits);
        ArgumentNullException.ThrowIfNull(partnerships);
        ArgumentNullException.ThrowIfNull(text);
        var rows = tab == RelationsTab.Partnerships
            ? partnerships.Select(p => Cells(p, text, culture))
            : visits.Select(v => Cells(v, text));
        return PeriodReport.Csv([.. Columns(tab).Select(c => text(c.CaptionKey))], rows);
    }

    /// <summary>« visites-20261001-20261026.csv », « visites-a-prevoir-20261026.csv », « conventions-20261026.csv ».</summary>
    public static string FileName(RelationsTab tab, DateOnly from, DateOnly to, DateOnly today) => tab switch
    {
        RelationsTab.DueVisits => $"visites-a-prevoir-{Stamp(today)}.csv",
        RelationsTab.Partnerships => $"conventions-{Stamp(today)}.csv",
        _ => $"visites-{PeriodReport.FileStamp(from, to)}.csv"
    };

    private static string Stamp(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static FieldRefusal TooLong(string field, int max) =>
        new(field, "Relations_TextTooLong", max.ToString(CultureInfo.InvariantCulture));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
