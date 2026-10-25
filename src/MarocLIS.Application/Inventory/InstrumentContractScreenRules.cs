using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Contrats automates » offers, by right, by the contract and by the intervention selected —
/// the workstation's PermissionGate calls and RefreshButtons. InstrumentContractService stays the judge.
/// </summary>
public sealed record InstrumentContractScreenActions(bool ManageRight, bool TimesRight,
                                                    bool New, bool Edit, bool Attach, bool OpenFile, bool Times);

/// <summary>A captive reagent as typed in the contract: the item, its contract price and its cost per test.</summary>
public sealed class ContractReagentForm
{
    public Guid? StockItemId { get; set; }
    public string? Price { get; set; }
    public string? CostPerTest { get; set; }
}

/// <summary>A contract as typed: its terms, its analysers and its captive reagents.</summary>
public sealed class InstrumentContractForm
{
    public string? Reference { get; set; }
    public Guid? SupplierId { get; set; }
    public InstrumentContractKind Kind { get; set; } = InstrumentContractKind.Placement;
    public bool IsActive { get; set; } = true;
    public DateOnly Start { get; set; }
    public int DurationMonths { get; set; } = 60;
    public CommitmentBasis Basis { get; set; } = CommitmentBasis.TestsPerYear;
    public string? Committed { get; set; }
    public string? PenaltyAmount { get; set; }
    public string? CountedAnalyses { get; set; }
    public string? PreventiveVisits { get; set; }
    public string? MaxDelayHours { get; set; }
    public HashSet<Guid> InstrumentIds { get; set; } = [];
    public List<ContractReagentForm> Reagents { get; set; } = [];
    public string? MaintenanceTerms { get; set; }
    public string? TrainingTerms { get; set; }
    public string? ExitConditions { get; set; }
    public string? PenaltyTerms { get; set; }
    public string? Notes { get; set; }
}

/// <summary>When the supplier was called and when its technician arrived, as typed (the clock of the laboratory).</summary>
public sealed class InterventionTimesForm
{
    public bool HasRequested { get; set; }
    public DateTime Requested { get; set; }
    public bool HasArrived { get; set; }
    public DateTime Arrived { get; set; }
}

/// <summary>
/// The rules of « Contrats automates » that need no window: what is offered by which right, how a
/// contract reads on the dashboard (its end and the days left, the commitment against what was
/// consumed and projected, the alerts — commitment at risk, end within 90 days, intervention delays
/// exceeded — in their colours), how the interventions on its analysers read against the delay it
/// allows, and how a contract, its file and the call and arrival times of an intervention are typed
/// and refused box by box. The workstation's InstrumentContractView and its dialogs and the web's
/// screen apply the same ones; every decision stays InstrumentContractService's.
/// </summary>
public static class InstrumentContractScreenRules
{
    public const int ReferenceMaxLength = 60;
    public const int CodesMaxLength = 1000;
    public const int LongTermsMaxLength = 2000;
    public const int ShortTermsMaxLength = 1000;
    public const int IntegerMaxLength = 6;
    public const int DurationMin = 1;
    public const int DurationMax = 240;

    public const string ReferenceField = "contract-reference";
    public const string SupplierField = "contract-supplier";
    public const string DurationField = "contract-duration";
    public const string CommittedField = "contract-committed";
    public const string PenaltyAmountField = "contract-penalty-amount";
    public const string VisitsField = "contract-visits";
    public const string MaxDelayField = "contract-max-delay";
    public const string ReagentsField = "contract-reagents";
    public const string FileField = "contract-file";
    public const string RequestedField = "times-requested";
    public const string ArrivedField = "times-arrived";

    /// <summary>The kinds of file a contract is kept as: a PDF, a scan, a Word file.</summary>
    public static readonly IReadOnlyList<string> FileExtensions = [".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".docx"];

    public static string FileAccept => string.Join(",", FileExtensions);

    /// <summary>A figure of a reagent line: « contract-price-2 », « contract-cost-2 ».</summary>
    public static string ReagentField(string column, int index) => $"contract-{column}-{index.ToString(CultureInfo.InvariantCulture)}";

    public static InstrumentContractScreenActions Actions(ICurrentUser user, InstrumentContractRow? contract,
                                                         ContractInterventionRow? intervention)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, contract, intervention);
    }

    public static InstrumentContractScreenActions Actions(Func<string, bool> has, InstrumentContractRow? contract,
                                                         ContractInterventionRow? intervention)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Equipment.ManageContracts);
        var times = has(Permissions.Equipment.RecordMaintenance);
        return new InstrumentContractScreenActions(
            manage, times,
            New: manage,
            Edit: manage && contract is not null,
            Attach: manage && contract is not null,
            OpenFile: contract is { FileName: not null },
            Times: times && contract is not null && intervention is not null);
    }

    /// <summary>The contract kept selected after a reload: the one worked on, else the first.</summary>
    public static InstrumentContractRow? Keep(IReadOnlyList<InstrumentContractRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.Id == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    // ---- The dashboard -------------------------------------------------------------------------------

    public static string Date(DateOnly date) => date.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    /// <summary>« 31/12/26 (45 j) » while the contract runs; the date alone once it has ended.</summary>
    public static string End(InstrumentContractRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.DaysToEnd >= 0
            ? $"{Date(row.EndDate)} ({row.DaysToEnd.ToString(CultureInfo.InvariantCulture)} {text("Quotes.DaysShort")})"
            : Date(row.EndDate);
    }

    public static bool HasCommitment(CommitmentStatus commitment)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        return commitment.Basis != CommitmentBasis.None;
    }

    /// <summary>« 01/01/26–31/12/26 »: the period the commitment is measured on; nothing without one.</summary>
    public static string Period(CommitmentStatus commitment) =>
        HasCommitment(commitment) ? $"{Date(commitment.PeriodStart)}–{Date(commitment.PeriodEnd)}" : string.Empty;

    /// <summary>
    /// A figure of the commitment: tests counted whole, an amount bought written by
    /// <paramref name="money"/> (in DH on the screens, with two decimals on the workstation's grid);
    /// nothing without a commitment.
    /// </summary>
    public static string Figure(decimal value, CommitmentStatus commitment, Func<decimal, string> money, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentNullException.ThrowIfNull(money);
        if (!HasCommitment(commitment)) return string.Empty;
        return ContractCommitment.CountsPurchases(commitment.Basis)
            ? money(value)
            : value.ToString("N0", provider ?? CultureInfo.CurrentCulture);
    }

    /// <summary>« 87 % »: projected over committed.</summary>
    public static string Ratio(CommitmentStatus commitment, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        return commitment.ProjectedRatio is { } ratio && HasCommitment(commitment)
            ? (ratio * 100m).ToString("0", provider ?? CultureInfo.CurrentCulture) + " %"
            : string.Empty;
    }

    /// <summary>The alerts of a contract, as the dashboard writes them one after the other.</summary>
    public static string Alerts(InstrumentContractRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var alerts = new List<string>();
        if (!row.IsActive) alerts.Add(text("Contracts.Inactive"));
        else if (row.DaysToEnd < 0) alerts.Add(text("Contracts.Expired"));
        if (row.Commitment.AtRisk) alerts.Add(text("Contracts.AtRisk"));
        if (row.IsEndingSoon) alerts.Add(text("Contracts.EndingSoon"));
        if (row.DelaysExceeded > 0) alerts.Add(text("Contracts.DelaysExceeded", row.DelaysExceeded));
        return string.Join(" · ", alerts);
    }

    /// <summary>The pill of the alerts: at risk critical, ending soon or late interventions a warning, ended waiting; none when all is well.</summary>
    public static StockTone? AlertTone(InstrumentContractRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Commitment.AtRisk ? StockTone.Critical
            : row.IsEndingSoon || row.DelaysExceeded > 0 ? StockTone.Warning
            : !row.IsActive || row.DaysToEnd < 0 ? StockTone.Waiting
            : null;
    }

    /// <summary>A commitment at risk on the alarm's fill; a contract ended or terminated muted.</summary>
    public static StockInk RowInk(InstrumentContractRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Commitment.AtRisk ? StockInk.Critical
            : row.IsEndingSoon || row.DelaysExceeded > 0 ? StockInk.Plain
            : !row.IsActive || row.DaysToEnd < 0 ? StockInk.Muted
            : StockInk.Plain;
    }

    /// <summary>The end near: a reminder, not an emergency — said on the end date, not on the row.</summary>
    public static StockInk EndInk(InstrumentContractRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.Commitment.AtRisk && row.IsEndingSoon ? StockInk.AbnormalBold : StockInk.Plain;
    }

    /// <summary>At risk, the ratio and the alerts in the critical ink.</summary>
    public static StockInk RatioInk(InstrumentContractRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Commitment.AtRisk ? StockInk.CriticalBold : StockInk.Plain;
    }

    /// <summary>« 3 intervention(s), 12,5 h d'arrêt »; nothing without intervention.</summary>
    public static string Maintenance(InstrumentContractRow row, ScreenText text, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.Interventions == 0
            ? string.Empty
            : text("Contracts.MaintenanceSummary", row.Interventions, row.DowntimeHours.ToString("0.#", provider ?? CultureInfo.CurrentCulture));
    }

    /// <summary>« 4 contrats · Risque de non-atteinte 1 · Fin proche 1 ».</summary>
    public static string Count(IReadOnlyCollection<InstrumentContractRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var atRisk = rows.Count(r => r.Commitment.AtRisk);
        var ending = rows.Count(r => r.IsEndingSoon);
        var count = text("StocksUi.Contracts", rows.Count);
        if (atRisk > 0) count += $" · {text("Contracts.AtRisk")} {atRisk.ToString(CultureInfo.InvariantCulture)}";
        if (ending > 0) count += $" · {text("Contracts.EndingSoon")} {ending.ToString(CultureInfo.InvariantCulture)}";
        return count;
    }

    /// <summary>The rule of the alerts under the dashboard: 90 % of the commitment, 90 days before the end.</summary>
    public static string AlertRule(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Contracts.AlertRule", (int)(ContractCommitment.RiskThreshold * 100m), InstrumentContract.EndingSoonDays);
    }

    /// <summary>The cells a search looks in: what the dashboard shows of the contract.</summary>
    public static IReadOnlyList<string?> SearchCells(InstrumentContractRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            row.Reference, row.SupplierName, text($"InstrumentContractKind.{row.Kind}"), row.Instruments, End(row, text),
            text($"CommitmentBasis.{row.Commitment.Basis}"), Period(row.Commitment), Alerts(row, text), row.FileName
        ];
    }

    // ---- The interventions on the contract's analysers ----------------------------------------------

    public static string Moment(DateTimeOffset? moment) =>
        moment?.ToLocalTime().ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Hours(decimal? hours, IFormatProvider? provider = null) =>
        hours?.ToString("0.#", provider ?? CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>The cells of an intervention: date, analyser, nature, description, downtime, call, arrival, delay, technician.</summary>
    public static IReadOnlyList<string> InterventionCells(ContractInterventionRow row, ScreenText text, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            row.PerformedOn.ToString("dd/MM/yy", CultureInfo.InvariantCulture), row.InstrumentCode, text($"MaintenanceKind.{row.Kind}"),
            row.Description, Hours(row.DowntimeHours, provider), Moment(row.RequestedAt), Moment(row.ArrivedAt),
            Hours(row.DelayHours, provider), row.Technician ?? string.Empty
        ];
    }

    /// <summary>The delay beyond what the contract allows, marked on its own cell, not on the row.</summary>
    public static StockInk DelayInk(ContractInterventionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DelayExceeded ? StockInk.CriticalBold : StockInk.Plain;
    }

    /// <summary>« CTR-2024-07 · délai maximal 48 h »: whose interventions, against which delay.</summary>
    public static string InterventionsNote(InstrumentContractRow? contract, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return contract is null ? string.Empty
            : contract.MaxInterventionDelayHours is { } max ? $"{contract.Reference} · {text("Contracts.MaxDelayShort", max)}"
            : contract.Reference;
    }

    /// <summary>The intervention kept selected after a reload: the one whose times were just recorded.</summary>
    public static ContractInterventionRow? KeepIntervention(IReadOnlyList<ContractInterventionRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.MaintenanceRecordId == kept);
    }

    // ---- A contract ---------------------------------------------------------------------------------

    /// <summary>« CODE — Nom », « (inactif) » after one offered only because the contract uses it.</summary>
    public static string Display(ContractLookup entry, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(text);
        var shown = $"{entry.Code} — {entry.Name.For(language)}";
        return entry.IsActive ? shown : $"{shown} {text("Contracts.InactiveSuffix")}";
    }

    private static string Figure(decimal value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>A new contract starts today for five years, counted in tests a year; one edited shows what was saved.</summary>
    public static InstrumentContractForm Form(InstrumentContractDetail? detail, DateOnly today, ContractOptions? options = null)
    {
        if (detail is null) return new InstrumentContractForm { Start = today };
        return new InstrumentContractForm
        {
            Reference = detail.Reference,
            SupplierId = detail.SupplierId,
            Kind = detail.Kind,
            IsActive = detail.IsActive,
            Start = detail.StartDate,
            DurationMonths = detail.DurationMonths,
            Basis = detail.CommitmentBasis,
            Committed = detail.CommittedVolume > 0m ? Figure(detail.CommittedVolume, "0.##") : null,
            PenaltyAmount = detail.PenaltyAmountMad is { } penalty ? Figure(penalty, "0.00") : null,
            CountedAnalyses = detail.CountedAnalysisCodesCsv,
            PreventiveVisits = detail.PreventiveVisitsPerYear?.ToString(CultureInfo.InvariantCulture),
            MaxDelayHours = detail.MaxInterventionDelayHours?.ToString(CultureInfo.InvariantCulture),
            InstrumentIds = [.. detail.InstrumentIds],
            // A reagent no longer offered (deleted item) is not shown, as at the workstation.
            Reagents = [.. detail.Reagents
                .Where(r => options is null || options.StockItems.Any(i => i.Id == r.StockItemId))
                .Select(r => new ContractReagentForm
                {
                    StockItemId = r.StockItemId,
                    Price = r.ContractPriceMad is { } p ? Figure(p, "0.00") : null,
                    CostPerTest = r.CostPerTestMad is { } c ? Figure(c, "0.00") : null
                })],
            MaintenanceTerms = detail.MaintenanceTerms,
            TrainingTerms = detail.TrainingTerms,
            ExitConditions = detail.ExitConditions,
            PenaltyTerms = detail.PenaltyTerms,
            Notes = detail.Notes
        };
    }

    /// <summary>
    /// A contract refused on its box: a reference and a supplier, a term of 1 to 240 months, the
    /// volume or amount committed unless « Sans engagement », visits and delay whole and positive,
    /// a penalty or a reagent price typed « 5000 DH » is no number (it was saved as none).
    /// </summary>
    public static (FieldRefusal? Refusal, SaveInstrumentContractRequest? Request) Check(InstrumentContractForm form, Guid? contractId)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Reference)) return (new FieldRefusal(ReferenceField, "Contract_ReferenceRequired"), null);
        if (form.SupplierId is not { } supplierId || supplierId == Guid.Empty) return (new FieldRefusal(SupplierField, "Contract_SupplierRequired"), null);
        if (form.DurationMonths is < DurationMin or > DurationMax) return (new FieldRefusal(DurationField, "Contract_DurationOutOfRange"), null);

        if (TypedNumber.IsUnreadable(form.Committed)) return (new FieldRefusal(CommittedField, "Result_NotANumber", form.Committed!.Trim()), null);
        var committed = TypedNumber.Parse(form.Committed) ?? 0m;
        if (committed < 0m) return (new FieldRefusal(CommittedField, "Contract_CommitmentNegative"), null);
        if (form.Basis != CommitmentBasis.None && committed <= 0m) return (new FieldRefusal(CommittedField, "Contract_CommitmentRequired"), null);

        if (TypedNumber.IsUnreadable(form.PenaltyAmount)) return (new FieldRefusal(PenaltyAmountField, "Result_NotANumber", form.PenaltyAmount!.Trim()), null);
        var penalty = TypedNumber.Parse(form.PenaltyAmount);
        if (penalty < 0m) return (new FieldRefusal(PenaltyAmountField, "Contract_NegativeTerm"), null);

        if (!TryWhole(form.PreventiveVisits, out var visits)) return (new FieldRefusal(VisitsField, "Contract_NegativeTerm"), null);
        if (!TryWhole(form.MaxDelayHours, out var maxDelay)) return (new FieldRefusal(MaxDelayField, "Contract_NegativeTerm"), null);

        var reagents = new List<InstrumentContractReagentInput>();
        for (var i = 0; i < form.Reagents.Count; i++)
        {
            var line = form.Reagents[i];
            if (line.StockItemId is not { } itemId) continue;

            // An unreadable contract price was saved as no price, and the reagent then ordered at
            // whatever the last order said.
            if (TypedNumber.IsUnreadable(line.Price)) return (new FieldRefusal(ReagentField("price", i), "Result_NotANumber", line.Price!.Trim()), null);
            if (TypedNumber.IsUnreadable(line.CostPerTest)) return (new FieldRefusal(ReagentField("cost", i), "Result_NotANumber", line.CostPerTest!.Trim()), null);

            var price = TypedNumber.Parse(line.Price);
            var cost = TypedNumber.Parse(line.CostPerTest);
            if (price < 0m) return (new FieldRefusal(ReagentField("price", i), "Po_PriceMustNotBeNegative"), null);
            if (cost < 0m) return (new FieldRefusal(ReagentField("cost", i), "Po_PriceMustNotBeNegative"), null);

            reagents.Add(new InstrumentContractReagentInput(itemId, price, cost));
        }

        return (null, new SaveInstrumentContractRequest
        {
            Id = contractId,
            Reference = form.Reference.Trim(),
            SupplierId = supplierId,
            Kind = form.Kind,
            StartDate = form.Start,
            DurationMonths = form.DurationMonths,
            CommitmentBasis = form.Basis,
            CommittedVolume = committed,
            CountedAnalysisCodesCsv = Blank(form.CountedAnalyses),
            PreventiveVisitsPerYear = visits,
            MaxInterventionDelayHours = maxDelay,
            MaintenanceTerms = Blank(form.MaintenanceTerms),
            TrainingTerms = Blank(form.TrainingTerms),
            ExitConditions = Blank(form.ExitConditions),
            PenaltyTerms = Blank(form.PenaltyTerms),
            PenaltyAmountMad = penalty,
            Notes = Blank(form.Notes),
            IsActive = form.IsActive,
            InstrumentIds = [.. form.InstrumentIds],
            Reagents = reagents
        });
    }

    /// <summary>A whole number of 0 or more, or nothing; « -1 » or « 2,5 » is refused.</summary>
    public static bool TryWhole(string? text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(TypedNumber.WithoutDirectionMarks(text).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    /// <summary>The box a refusal of InstrumentContractService names; any other is said in the dialog.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "Contract_ReferenceRequired" or "Contract_ReferenceExists" => ReferenceField,
        "Contract_SupplierRequired" => SupplierField,
        "Contract_DurationOutOfRange" => DurationField,
        "Contract_CommitmentRequired" or "Contract_CommitmentNegative" => CommittedField,
        "Contract_NegativeTerm" => VisitsField,
        "Po_PriceMustNotBeNegative" => ReagentsField,
        "Purchasing_FileTooLarge" or "Purchasing_FileType" or "Purchasing_FileEmpty" => FileField,
        "Contract_ArrivalBeforeRequest" => ArrivedField,
        "Contract_InterventionTimeInFuture" => RequestedField,
        _ => null
    };

    // ---- The contract's file ------------------------------------------------------------------------

    /// <summary>A file chosen: one of the kinds kept, not empty and not past the size kept.</summary>
    public static FieldRefusal? CheckFile(string? fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName) || length <= 0) return new FieldRefusal(FileField, "Purchasing_FileEmpty");
        if (!FileExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant())) return new FieldRefusal(FileField, "Purchasing_FileType");
        if (length > PurchasingDocument.MaximumBytes)
            return new FieldRefusal(FileField, "Purchasing_FileTooLarge",
                                    (PurchasingDocument.MaximumBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>A file already joined: the new one becomes current, the old one stays archived — asked first.</summary>
    public static string? ReplaceQuestion(InstrumentContractRow contract, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(text);
        return contract.FileName is { } name ? text("Contracts.ReplaceFileQuestion", name) : null;
    }

    // ---- The call and arrival times of an intervention ----------------------------------------------

    /// <summary>
    /// The times recorded, or 08:00 and 10:00 on the day of the intervention, never later than now:
    /// read twice, the clock opened an intervention entered the same morning on a time to come.
    /// </summary>
    public static InterventionTimesForm TimesForm(ContractInterventionRow row, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(row);
        DateTime Shown(DateTimeOffset? value, int hour)
        {
            var shown = value?.LocalDateTime ?? row.PerformedOn.ToDateTime(new TimeOnly(hour, 0));
            shown = shown > now ? now : shown;
            return new DateTime(shown.Year, shown.Month, shown.Day, shown.Hour, shown.Minute, 0, DateTimeKind.Unspecified);
        }

        return new InterventionTimesForm
        {
            HasRequested = row.RequestedAt is not null,
            Requested = Shown(row.RequestedAt, 8),
            HasArrived = row.ArrivedAt is not null,
            Arrived = Shown(row.ArrivedAt, 10)
        };
    }

    /// <summary>« AUT-01 · 12/10/2026 »: the intervention whose times are recorded.</summary>
    public static string TimesTitle(ContractInterventionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.InstrumentCode} · {row.PerformedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The arrival cannot precede the call, and neither can be in the future.</summary>
    public static (FieldRefusal? Refusal, DateTimeOffset? Requested, DateTimeOffset? Arrived) Check(InterventionTimesForm form, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(form);
        DateTimeOffset? requested = form.HasRequested ? new DateTimeOffset(DateTime.SpecifyKind(form.Requested, DateTimeKind.Local)) : null;
        DateTimeOffset? arrived = form.HasArrived ? new DateTimeOffset(DateTime.SpecifyKind(form.Arrived, DateTimeKind.Local)) : null;

        if (requested > now) return (new FieldRefusal(RequestedField, "Contract_InterventionTimeInFuture"), null, null);
        if (arrived > now) return (new FieldRefusal(ArrivedField, "Contract_InterventionTimeInFuture"), null, null);
        if (requested is { } r && arrived is { } a && a < r) return (new FieldRefusal(ArrivedField, "Contract_ArrivalBeforeRequest"), null, null);
        return (null, requested, arrived);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
