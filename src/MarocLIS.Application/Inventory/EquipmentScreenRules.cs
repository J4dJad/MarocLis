using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Équipements » offers, by right and by the instrument selected — the workstation's
/// PermissionGate calls and ShowDetail. A decommissioned instrument takes nothing more: no
/// intervention, no calibration, no status (EquipmentService refuses them too).
/// </summary>
public sealed record EquipmentScreenActions(
    bool MaintenanceRight, bool CalibrationRight, bool StatusRight,
    bool Maintenance, bool Calibration, bool Status);

/// <summary>An intervention as typed: its nature, its description in the three languages, the downtime and the post-check.</summary>
public sealed class MaintenanceForm
{
    public MaintenanceKind Kind { get; set; } = MaintenanceKind.Corrective;
    public string? DescriptionFr { get; set; }
    public string? DescriptionAr { get; set; }
    public string? DescriptionEn { get; set; }
    public string? Downtime { get; set; }
    public string? PartsReplaced { get; set; }
    public string? Technician { get; set; }
    public bool PostCheckPassed { get; set; } = true;
}

/// <summary>An intervention read from its form, as EquipmentService.RecordMaintenanceAsync takes it.</summary>
public sealed record MaintenanceEntry(MaintenanceKind Kind, MultilingualText Description, decimal? DowntimeHours,
                                      string? PartsReplaced, string? ExternalTechnician, bool PostCheckPassed);

/// <summary>A calibration as typed: its outcome, the calibrator lot, how long it holds, the curve and a comment.</summary>
public sealed class CalibrationForm
{
    public CalibrationOutcome Outcome { get; set; } = CalibrationOutcome.Passed;
    public string? CalibratorLot { get; set; }
    public bool HasValidity { get; set; } = true;
    public DateOnly ValidUntil { get; set; }
    public string? Slope { get; set; }
    public string? Intercept { get; set; }
    public string? Comment { get; set; }
}

/// <summary>A calibration read from its form, as EquipmentService.RecordCalibrationAsync takes it.</summary>
public sealed record CalibrationEntry(CalibrationOutcome Outcome, string? CalibratorLotNumber, DateOnly? ValidUntil,
                                      decimal? Slope, decimal? Intercept, string? Comment);

/// <summary>A change of status as typed: the new status and its reason, which goes in the audit trail.</summary>
public sealed class EquipmentStatusForm
{
    public EquipmentStatus Status { get; set; } = EquipmentStatus.Operational;
    public string? Reason { get; set; }
}

/// <summary>One fact of the instrument's card: its label, its value, and whether the value is a code or a date.</summary>
public sealed record EquipmentFact(string Label, string Value, bool Mono);

/// <summary>
/// The rules of « Équipements » that need no window: what is offered by which right and for which
/// instrument, how an instrument reads in the register (its due dates with their marker, the
/// status pill, the alarm on a lapsed calibration), what its card says is wrong with it, and how an
/// intervention, a calibration and a change of status are typed and refused box by box. A failed
/// check after an intervention takes an operational instrument out of service and offers the
/// non-conformity, opened on the instrument. The workstation's EquipmentView and its dialogs and the
/// web's screen apply the same ones; every decision stays EquipmentService's.
/// </summary>
public static class EquipmentScreenRules
{
    public const int DescriptionMaxLength = 2000;
    public const int PartsMaxLength = 1000;
    public const int TechnicianMaxLength = 200;
    public const int CalibratorLotMaxLength = 60;
    public const int CommentMaxLength = 1000;
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 500;

    public const string DescriptionField = "maintenance-description";
    public const string DowntimeField = "maintenance-downtime";
    public const string LotField = "calibration-lot";
    public const string ValidUntilField = "calibration-valid";
    public const string SlopeField = "calibration-slope";
    public const string InterceptField = "calibration-intercept";
    public const string ReasonField = "status-reason";

    /// <summary>
    /// The statuses the service applies from this screen. Commissioning and Backup are set when the
    /// instrument record is created, and offering them here would produce a refusal.
    /// </summary>
    public static readonly IReadOnlyList<EquipmentStatus> StatusChoices =
    [
        EquipmentStatus.Operational, EquipmentStatus.UnderMaintenance, EquipmentStatus.OutOfService, EquipmentStatus.Decommissioned
    ];

    public static EquipmentScreenActions Actions(ICurrentUser user, EquipmentRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, row);
    }

    public static EquipmentScreenActions Actions(Func<string, bool> has, EquipmentRow? row)
    {
        ArgumentNullException.ThrowIfNull(has);
        var maintenance = has(Permissions.Equipment.RecordMaintenance);
        var calibration = has(Permissions.Equipment.Calibrate);
        var status = has(Permissions.Equipment.Manage);
        // Decommissioning is final: nothing more is recorded on the instrument, and its status no
        // longer changes.
        var active = row is { Status: not EquipmentStatus.Decommissioned };
        return new EquipmentScreenActions(maintenance, calibration, status,
                                          maintenance && active, calibration && active, status && active);
    }

    /// <summary>The instrument kept selected after a reload: the one just serviced, else the first.</summary>
    public static EquipmentRow? Keep(IReadOnlyList<EquipmentRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.InstrumentId == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    // ---- The register --------------------------------------------------------------------------------

    public static StockTone StatusTone(EquipmentStatus status) => status switch
    {
        EquipmentStatus.Operational => StockTone.Done,
        EquipmentStatus.OutOfService => StockTone.Critical,
        EquipmentStatus.UnderMaintenance => StockTone.Warning,
        EquipmentStatus.Commissioning => StockTone.Running,
        _ => StockTone.Waiting
    };

    public static string StatusKey(EquipmentStatus status) => $"EquipmentStatus.{status}";

    public static string KindKey(EquipmentKind kind) => $"EquipmentKind.{kind}";

    /// <summary>
    /// A lapsed calibration is the serious one: results produced on that instrument are not
    /// defensible, so the whole row is in the alarm's colours, as an instrument out of service; a
    /// decommissioned one is muted.
    /// </summary>
    public static StockInk RowInk(EquipmentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.CalibrationOverdue || row.Status == EquipmentStatus.OutOfService ? StockInk.CriticalBold
            : row.Status == EquipmentStatus.Decommissioned ? StockInk.Muted
            : StockInk.Plain;
    }

    /// <summary>A late maintenance: a reminder, not an emergency — said on its date, not on the row.</summary>
    public static StockInk MaintenanceInk(EquipmentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.MaintenanceOverdue && RowInk(row) == StockInk.Plain ? StockInk.AbnormalBold : StockInk.Plain;
    }

    /// <summary>An expired maintenance contract: said on its date, not on the row.</summary>
    public static StockInk ContractInk(EquipmentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ContractExpired && RowInk(row) == StockInk.Plain ? StockInk.AbnormalBold : StockInk.Plain;
    }

    /// <summary>
    /// A due date with its marker. The marker matters more than the colour: this list gets printed
    /// for the equipment file, and a red cell prints as a grey one.
    /// </summary>
    public static string Due(DateOnly? date, bool overdue) =>
        date is not { } value ? "—"
        : overdue ? "! " + value.ToString("dd/MM/yy", CultureInfo.InvariantCulture)
        : value.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    public static string MaintenanceDue(EquipmentRow row) => Due(row.NextMaintenanceDue, row.MaintenanceOverdue);

    public static string CalibrationDue(EquipmentRow row) => Due(row.NextCalibrationDue, row.CalibrationOverdue);

    public static string ContractDue(EquipmentRow row) => Due(row.ContractValidUntil, row.ContractExpired);

    /// <summary>« 7 équipements · Échéance dépassée 2 ».</summary>
    public static string Count(IReadOnlyCollection<EquipmentRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var overdue = rows.Count(r => r.CalibrationOverdue || r.MaintenanceOverdue);
        var count = text("StocksUi.Instruments", rows.Count);
        return overdue > 0 ? $"{count} · {text("Equipment.Overdue")} {overdue.ToString(CultureInfo.InvariantCulture)}" : count;
    }

    /// <summary>The cells a search looks in: what the register shows of the instrument.</summary>
    public static IReadOnlyList<string?> SearchCells(EquipmentRow row, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            row.Code, row.Name.For(language), text(KindKey(row.Kind)), text(StatusKey(row.Status)), row.SerialNumber,
            row.Location, row.Manufacturer, row.Model, MaintenanceDue(row), CalibrationDue(row), ContractDue(row)
        ];
    }

    // ---- The instrument's card -----------------------------------------------------------------------

    /// <summary>« Sysmex · XN-550 · N° de série 12345 · Paillasse 2 · Motif : … ».</summary>
    public static string Detail(EquipmentRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(row.Manufacturer)) parts.Add(row.Manufacturer);
        if (!string.IsNullOrWhiteSpace(row.Model)) parts.Add(row.Model);
        if (!string.IsNullOrWhiteSpace(row.SerialNumber)) parts.Add($"{text("Equipment.SerialNumber")} {row.SerialNumber}");
        if (!string.IsNullOrWhiteSpace(row.Location)) parts.Add(row.Location);
        if (!string.IsNullOrWhiteSpace(row.LastStatusReason)) parts.Add($"{text("Field.Reason")} : {row.LastStatusReason}");
        return string.Join("  ·  ", parts);
    }

    /// <summary>The facts of the card: kind, serial number, place and the three due dates.</summary>
    public static IReadOnlyList<EquipmentFact> Facts(EquipmentRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            new(text("Equipment.Kind"), text(KindKey(row.Kind)), false),
            new(text("Equipment.SerialNumber"), row.SerialNumber ?? string.Empty, true),
            new(text("Equipment.Location"), row.Location ?? string.Empty, false),
            new(text("Equipment.NextMaintenance"), MaintenanceDue(row), true),
            new(text("Equipment.NextCalibration"), CalibrationDue(row), true),
            new(text("Equipment.Contract"), ContractDue(row), true)
        ];
    }

    /// <summary>
    /// What is wrong with the instrument, said rather than left to compare two dates in a row; none
    /// when nothing is.
    /// </summary>
    public static (string Key, StockInk Ink)? Warning(EquipmentRow? row) => row switch
    {
        null => null,
        { Status: EquipmentStatus.Decommissioned } => ("Equipment.DecommissionedFinal", StockInk.Muted),
        { CalibrationOverdue: true } => ("Equipment.CalibrationOverdueWarning", StockInk.Critical),
        { MaintenanceOverdue: true } => ("Equipment.MaintenanceOverdueWarning", StockInk.Abnormal),
        { ContractExpired: true } => ("Equipment.ContractExpiredWarning", StockInk.Abnormal),
        _ => null
    };

    // ---- An intervention ----------------------------------------------------------------------------

    /// <summary>
    /// A breakdown repair is the commonest thing recorded off the cuff; preventive work is usually
    /// recorded from a plan, so corrective is the safer default to land on. The check passed is the
    /// default: unticking it is the record of a failed one.
    /// </summary>
    public static MaintenanceForm NewMaintenance() => new();

    public static (FieldRefusal? Refusal, MaintenanceEntry? Entry) Check(MaintenanceForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(form.DescriptionFr)) return (new FieldRefusal(DescriptionField, "Equipment_DescriptionRequired"), null);
        // « 2 h » was saved as no downtime, and « 1e20 » failed the save with « Erreur inattendue ».
        if (TypedNumber.IsUnreadable(form.Downtime)) return (new FieldRefusal(DowntimeField, "Result_NotANumber", form.Downtime!.Trim()), null);

        var hours = TypedNumber.Parse(form.Downtime) is { } h && h > 0m ? h : (decimal?)null;
        return (null, new MaintenanceEntry(
            form.Kind,
            new MultilingualText(form.DescriptionFr.Trim(), (form.DescriptionAr ?? string.Empty).Trim(), (form.DescriptionEn ?? string.Empty).Trim()),
            hours, Blank(form.PartsReplaced), Blank(form.Technician), form.PostCheckPassed));
    }

    /// <summary>The intervention's dialog title: « Enregistrer une intervention — AUT-01 ».</summary>
    public static string Title(string key, EquipmentRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text(key)} — {row.Code}";
    }

    // ---- A failed check after an intervention --------------------------------------------------------

    /// <summary>
    /// The service has taken an operational instrument out of service on a failed check: whether it
    /// did, read on the row as it was before the intervention.
    /// </summary>
    public static bool TakenOutOfService(EquipmentRow before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return before.Status == EquipmentStatus.Operational;
    }

    /// <summary>Whoever may report a non-conformity is offered one, opened on the instrument.</summary>
    public static bool MayReportNonConformity(Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        return has(Permissions.Quality.ReportNonConformity) || has(Permissions.Quality.ManageNonConformity);
    }

    /// <summary>The question after a failed check: taken out of service, and the non-conformity offered.</summary>
    public static string PostCheckQuestion(EquipmentRow before, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TakenOutOfService(before)
            ? $"{text("Equipment.PostCheckFailedOutOfService")}\n\n{text("Equipment.PostCheckFailedOfferNc")}"
            : text("Equipment.PostCheckFailedOfferNc");
    }

    /// <summary>« Contrôle après intervention non conforme — AUT-01 Sysmex XN-550 ».</summary>
    public static string NonConformityDescription(EquipmentRow row, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Equipment.PostCheckFailedNcDescription", row.Code, row.Name.For(language));
    }

    /// <summary>The non-conformity opened on the instrument: equipment, major, seen by the staff.</summary>
    public static NonConformityForm NonConformity(EquipmentRow row, DateOnly today, AppLanguage language, ScreenText text) =>
        NonConformityScreenRules.Prefill(today, NonConformityCategory.Equipment, NonConformitySeverity.Major,
                                         DetectionSource.StaffObservation, NonConformityDescription(row, language, text),
                                         row.InstrumentId);

    // ---- A calibration ------------------------------------------------------------------------------

    /// <summary>A calibration valid for a month by default, as the workstation's dialog opens.</summary>
    public static CalibrationForm NewCalibration(DateOnly today) => new() { ValidUntil = today.AddMonths(1) };

    /// <summary>
    /// « Conforme avec réserve » is still a calibration the instrument runs on: it needs the same
    /// traceability as a plain pass. Said before « Enregistrer », not after.
    /// </summary>
    public static bool LotRequired(CalibrationOutcome outcome) =>
        outcome is CalibrationOutcome.Passed or CalibrationOutcome.PassedWithReservation;

    public static (FieldRefusal? Refusal, CalibrationEntry? Entry) Check(CalibrationForm form, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (LotRequired(form.Outcome) && string.IsNullOrWhiteSpace(form.CalibratorLot))
            return (new FieldRefusal(LotField, "Calibration_CalibratorLotRequired"), null);
        if (form.HasValidity && form.ValidUntil < today)
            return (new FieldRefusal(ValidUntilField, "Calibration_ValidUntilPast"), null);
        // A slip in the slope or the intercept was saved as no figure, and « 1e20 » failed the save.
        if (TypedNumber.IsUnreadable(form.Slope)) return (new FieldRefusal(SlopeField, "Result_NotANumber", form.Slope!.Trim()), null);
        if (TypedNumber.IsUnreadable(form.Intercept)) return (new FieldRefusal(InterceptField, "Result_NotANumber", form.Intercept!.Trim()), null);

        return (null, new CalibrationEntry(form.Outcome, Blank(form.CalibratorLot), form.HasValidity ? form.ValidUntil : null,
                                           TypedNumber.Parse(form.Slope), TypedNumber.Parse(form.Intercept), Blank(form.Comment)));
    }

    // ---- A change of status -------------------------------------------------------------------------

    /// <summary>Both of these are consequential enough to warn about before the operator commits.</summary>
    public static string? StatusNoticeKey(EquipmentStatus status) => status switch
    {
        EquipmentStatus.Operational => "Equipment.ReturnToServiceNotice",
        EquipmentStatus.Decommissioned => "Equipment.DecommissionNotice",
        _ => null
    };

    /// <summary>« Statut : En service »: where the instrument stands, above the new status.</summary>
    public static string CurrentStatus(EquipmentRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Field.Status")} : {text(StatusKey(row.Status))}";
    }

    /// <summary>A reason of five characters at least: it ends up in the audit trail.</summary>
    public static (FieldRefusal? Refusal, string? Reason) Check(EquipmentStatusForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var reason = form.Reason?.Trim() ?? string.Empty;
        return reason.Length < ReasonMinLength
            ? (new FieldRefusal(ReasonField, "Equipment_StatusReasonRequired"), null)
            : (null, reason);
    }

    /// <summary>The box a refusal of EquipmentService names; any other is said in the dialog.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "Equipment_DescriptionRequired" => DescriptionField,
        "Calibration_CalibratorLotRequired" => LotField,
        "Equipment_StatusReasonRequired" => ReasonField,
        _ => null
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
