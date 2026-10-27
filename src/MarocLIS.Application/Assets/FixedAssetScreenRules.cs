using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Assets;

/// <summary>
/// What « Immobilisations » offers, by right and by the line selected — the workstation's
/// PermissionGate on its buttons and RefreshButtons: a new asset, one from an instrument, the
/// accounts and an asset's change or disposal need the right to manage the register; the plan, the
/// printout and the CSV only to see it. A line of equipment placed by a supplier takes none.
/// </summary>
public sealed record FixedAssetActions(
    bool ManageRight, bool New, bool FromInstrument, bool Edit, bool Schedule, bool Disposal, bool CancelDisposal,
    bool Pdf, bool Csv, bool Accounts);

/// <summary>An asset as typed in its editor, the amounts as the operator writes them.</summary>
public sealed class FixedAssetForm
{
    public Guid? Id { get; set; }
    public string? Code { get; set; }
    public string? Label { get; set; }
    public FixedAssetCategory Category { get; set; } = FixedAssetCategory.LaboratoryEquipment;
    public DateOnly AcquisitionDate { get; set; }
    public DateOnly InServiceDate { get; set; }
    public DepreciationMethod Method { get; set; } = DepreciationMethod.StraightLine;
    public int UsefulLifeYears { get; set; }
    public string? Residual { get; set; }
    public string? Cost { get; set; }
    public string? Vat { get; set; }
    public bool VatRecoverable { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? SupplierInvoiceId { get; set; }
    public string? InvoiceReference { get; set; }
    public Guid? InstrumentId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>A disposal as typed: when, for how much, why.</summary>
public sealed class AssetDisposalForm
{
    public DateOnly Date { get; set; }
    public string? Price { get; set; }
    public string? Reason { get; set; }
}

/// <summary>A disposal read from its form, as FixedAssetService.RecordDisposalAsync takes it.</summary>
public sealed record AssetDisposalEntry(DateOnly Date, decimal Price, string Reason);

/// <summary>The three accounts of a category as typed.</summary>
public sealed class CategoryAccountsForm
{
    public string? Asset { get; set; }
    public string? Depreciation { get; set; }
    public string? Charge { get; set; }
}

/// <summary>What the instrument linked to an asset says in its editor, and whether it stops the save.</summary>
public sealed record AssetInstrumentState(string Text, bool Critical);

/// <summary>A month of an asset's plan with the depreciation accumulated at its end.</summary>
public sealed record AssetScheduleMonth(string Month, DateOnly Date, decimal Amount, decimal Accumulated);

/// <summary>
/// The rules of « Immobilisations » that need no window: what is offered by which right and for
/// which line, how a line reads (an asset sold or scrapped and a supplier's equipment muted), the
/// register's heading, an asset typed and refused box by box with the plan it gives said as it is
/// typed, the instrument it is linked to (one placed by its supplier refused, one already
/// capitalised said so), a supplier invoice filling the number, the amounts and the date, a
/// disposal, the accounts of each category refused box by box, an asset's plan by month, the
/// instrument's interventions and the register's printout. The workstation's FixedAssetView and its
/// dialogs and the web's screen apply the same ones; every decision stays FixedAssetService's.
/// </summary>
public static class FixedAssetScreenRules
{
    public const int YearMinimum = 1990;
    public const int YearMaximum = 2100;

    public const string CodeField = "asset-code";
    public const string LabelField = "asset-label";
    public const string AcquiredField = "asset-acquired";
    public const string InServiceField = "asset-inservice";
    public const string LifeField = "asset-life";
    public const string ResidualField = "asset-residual";
    public const string CostField = "asset-cost";
    public const string VatField = "asset-vat";
    public const string InvoiceReferenceField = "asset-invoice-reference";
    public const string InstrumentField = "asset-instrument";
    public const string NotesField = "asset-notes";
    public const string DisposalDateField = "disposal-date";
    public const string DisposalPriceField = "disposal-price";
    public const string DisposalReasonField = "disposal-reason";

    /// <summary>The columns of the register's PDF: the accounting ones; supplier, invoice and instrument are in the CSV.</summary>
    public const int PdfColumns = 20;

    public static FixedAssetActions Actions(Func<string, bool> has, FixedAssetRegisterRow? asset, bool registerShown)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Assets.Manage);
        var view = has(Permissions.Assets.View) || manage;
        var selected = asset is not null;
        var disposed = asset?.Asset.DisposalDate is not null;
        return new FixedAssetActions(manage, manage, manage, manage && selected, view && selected,
                                     manage && selected && !disposed, manage && selected && disposed,
                                     view && registerShown, view && registerShown, manage);
    }

    /// <summary>« Sortie », or « Annuler la sortie » on an asset already out of the register.</summary>
    public static string DisposalKey(FixedAssetRegisterRow? asset) =>
        asset?.Asset.DisposalDate is not null ? "Assets.CancelDisposal" : "Assets.Disposal";

    public static string CategoryKey(FixedAssetCategory category) => $"FixedAssetCategory.{category}";

    public static string MethodKey(DepreciationMethod method) => $"DepreciationMethod.{method}";

    /// <summary>An asset sold or scrapped reads muted: it is still in the year's register, no longer in use.</summary>
    public static StockInk RowInk(FixedAssetRegisterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Asset.DisposalDate is not null ? StockInk.Muted : StockInk.Plain;
    }

    /// <summary>The asset kept selected after a reload: the one just saved, else the first.</summary>
    public static FixedAssetRegisterRow? Keep(FixedAssetRegister? register, Guid? kept)
    {
        if (register is null) return null;
        return register.Assets.FirstOrDefault(r => r.Asset.Id == kept) ?? register.Assets.FirstOrDefault();
    }

    /// <summary>« Registre des immobilisations 2026 — 4 immobilisations · valeur brute … · 1 équipement mis à disposition ».</summary>
    public static string Heading(FixedAssetRegister register, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        var heading = text("Assets.Heading", register.Year, register.Assets.Count, amount(register.GrossValue),
                           amount(register.Charge), amount(register.NetBookValue));
        return register.SupplierProvided.Count > 0
            ? $"{heading} · {text("Assets.SupplierProvidedCount", register.SupplierProvided.Count)}"
            : heading;
    }

    public static string Percent(decimal rate) => FixedAssetRegisterReport.Percent(rate);

    // ---- An asset ------------------------------------------------------------------------------------

    /// <summary>An amount for its box, written in the session's culture (no thousands separator) so it reads back as typed.</summary>
    private static string Editable(decimal value, IFormatProvider? culture) =>
        value == 0m ? string.Empty : TypedNumber.ForEditing(value, 2, culture ?? CultureInfo.InvariantCulture);

    /// <summary>
    /// The editor's boxes: an asset's values, a draft from an instrument, or a new laboratory
    /// equipment bought and put into service today, depreciated straight-line over its category's
    /// usual life.
    /// </summary>
    public static FixedAssetForm Form(SaveFixedAssetRequest? values, DateOnly today, IFormatProvider? culture = null)
    {
        var category = values?.Category ?? FixedAssetCategory.LaboratoryEquipment;
        static DateOnly Or(DateOnly? date, DateOnly today) => date is { } d && d != default ? d : today;
        return new FixedAssetForm
        {
            Id = values?.Id,
            Code = values?.Code,
            Label = values?.Label,
            Category = category,
            AcquisitionDate = Or(values?.AcquisitionDate, today),
            InServiceDate = Or(values?.InServiceDate, today),
            Method = values?.Method ?? DepreciationMethod.StraightLine,
            UsefulLifeYears = values?.UsefulLifeYears is > 0 and var life ? life : FixedAssetCategories.DefaultUsefulLifeYears(category),
            Residual = Editable(values?.ResidualValueMad ?? 0m, culture),
            Cost = Editable(values?.CostExcludingVatMad ?? 0m, culture),
            Vat = Editable(values?.VatAmountMad ?? 0m, culture),
            VatRecoverable = values?.VatRecoverable ?? false,
            SupplierId = values?.SupplierId,
            SupplierInvoiceId = values?.SupplierInvoiceId,
            InvoiceReference = values?.InvoiceReference,
            InstrumentId = values?.InstrumentId,
            Notes = values?.Notes
        };
    }

    /// <summary>
    /// The plan the boxes give, said as they are typed: the depreciable base and the straight-line
    /// rate, or the declining coefficient and rate — refused below three years.
    /// </summary>
    public static string Plan(FixedAssetForm form, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        var life = form.UsefulLifeYears;
        var gross = FixedAsset.GrossValue(TypedNumber.Parse(form.Cost) ?? 0m, TypedNumber.Parse(form.Vat) ?? 0m, form.VatRecoverable);

        if (form.Method != DepreciationMethod.Declining)
            return text("Assets.PlanStraightLine", amount(gross), Percent(Depreciation.StraightLineRate(life)));

        return Depreciation.DecliningCoefficient(life) is { } coefficient
            ? text("Assets.PlanDeclining", amount(gross), coefficient.ToString("0.#", CultureInfo.InvariantCulture),
                   Percent(Depreciation.DecliningRate(life) ?? 0m))
            : text("Asset_DecliningNeedsThreeYears");
    }

    /// <summary>« ANA-01 — Automate (Mis à disposition) », « … (déjà immobilisé : IMM-2024-003) ».</summary>
    public static string InstrumentDisplay(AssetInstrumentOption instrument, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(text);
        var display = $"{instrument.Code} — {instrument.Name.For(language)}";
        if (instrument.ProvidedBy is not null) return $"{display} ({text("Assets.SupplierProvided")})";
        if (instrument.AssetCode is not null) return $"{display} ({text("Assets.AlreadyCapitalised", instrument.AssetCode)})";
        return display;
    }

    /// <summary>« FRN-01 — Bio-Distribution », marked inactive when it is.</summary>
    public static string SupplierDisplay(AssetLookup supplier, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(text);
        return supplier.IsActive
            ? $"{supplier.Code} — {supplier.Name}"
            : $"{supplier.Code} — {supplier.Name} {text("Contracts.InactiveSuffix")}";
    }

    /// <summary>« FA-2026-118 · 12/03/2026 · 185 000,00 DH ».</summary>
    public static string InvoiceDisplay(AssetInvoiceOption invoice, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(amount);
        return $"{invoice.InvoiceNumber} · {invoice.InvoiceDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} · {amount(invoice.TotalExcludingVat)}";
    }

    /// <summary>
    /// What the instrument chosen says: placed by its supplier (refused), already capitalised
    /// (refused by the service), bought on its date, or bought without a date on its file.
    /// </summary>
    public static AssetInstrumentState? InstrumentState(AssetInstrumentOption? instrument, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return instrument switch
        {
            null => null,
            { ProvidedBy: { } contract } => new AssetInstrumentState(text("Asset_SupplierProvided", contract), true),
            { AssetCode: { } code } => new AssetInstrumentState(text("Asset_InstrumentAlreadyCapitalised", code), true),
            { PurchaseDate: { } bought } => new AssetInstrumentState(
                text("Assets.InstrumentBought", bought.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)), false),
            _ => new AssetInstrumentState(text("Assets.InstrumentNoPurchaseDate"), false)
        };
    }

    /// <summary>
    /// Picking an invoice fills its number and, when they are still empty, the amounts, and the
    /// acquisition date of a new asset: one invoice may bill more than this asset, so nothing typed
    /// is overwritten.
    /// </summary>
    public static void ApplyInvoice(FixedAssetForm form, AssetInvoiceOption invoice, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(invoice);
        form.SupplierInvoiceId = invoice.Id;
        form.InvoiceReference = invoice.InvoiceNumber;
        if (string.IsNullOrWhiteSpace(form.Cost))
        {
            form.Cost = Editable(invoice.TotalExcludingVat, culture);
            form.Vat = Editable(invoice.TotalVat, culture);
        }
        if (form.Id is null) form.AcquisitionDate = invoice.InvoiceDate;
    }

    /// <summary>
    /// The asset as FixedAssetService.SaveAsync takes it, or the box at fault: a code and a
    /// designation, a cost above zero, a VAT and a residual value that are amounts and not negative,
    /// and no instrument its supplier placed — it stays the supplier's property.
    /// </summary>
    public static (FieldRefusal? Refusal, SaveFixedAssetRequest? Request) Check(FixedAssetForm form, FixedAssetOptions? options)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(form.Code)) return (new FieldRefusal(CodeField, "Asset_CodeRequired"), null);
        if (string.IsNullOrWhiteSpace(form.Label)) return (new FieldRefusal(LabelField, "Asset_LabelRequired"), null);

        foreach (var (field, typed) in new[] { (CostField, form.Cost), (VatField, form.Vat), (ResidualField, form.Residual) })
            if (TypedNumber.IsUnreadable(typed)) return (new FieldRefusal(field, "Result_NotANumber", typed!.Trim()), null);

        var cost = TypedNumber.Parse(form.Cost);
        if (cost is not > 0m) return (new FieldRefusal(CostField, "Asset_CostRequired"), null);
        var vat = TypedNumber.Parse(form.Vat) ?? 0m;
        if (vat < 0m) return (new FieldRefusal(VatField, "Asset_AmountNegative"), null);
        var residual = TypedNumber.Parse(form.Residual) ?? 0m;
        if (residual < 0m) return (new FieldRefusal(ResidualField, "Asset_AmountNegative"), null);

        if (form.InstrumentId is { } instrumentId
            && options?.Instruments.FirstOrDefault(i => i.Id == instrumentId) is { ProvidedBy: { } contract })
            return (new FieldRefusal(InstrumentField, "Asset_SupplierProvided", contract), null);

        return (null, new SaveFixedAssetRequest
        {
            Id = form.Id,
            Code = form.Code.Trim(),
            Label = form.Label.Trim(),
            Category = form.Category,
            AcquisitionDate = form.AcquisitionDate,
            InServiceDate = form.InServiceDate,
            CostExcludingVatMad = cost.Value,
            VatAmountMad = vat,
            VatRecoverable = form.VatRecoverable,
            SupplierId = form.SupplierId,
            InvoiceReference = Blank(form.InvoiceReference),
            SupplierInvoiceId = form.SupplierId is null ? null : form.SupplierInvoiceId,
            InstrumentId = form.InstrumentId,
            Method = form.Method,
            UsefulLifeYears = form.UsefulLifeYears,
            ResidualValueMad = residual,
            Notes = Blank(form.Notes)
        });
    }

    /// <summary>The box a refusal of FixedAssetService names; any other is said in the dialog.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "Asset_CodeRequired" or "Asset_CodeExists" or "Asset_CodeTooLong" => CodeField,
        "Asset_LabelRequired" or "Asset_LabelTooLong" => LabelField,
        "Asset_AcquisitionDateRequired" => AcquiredField,
        "Asset_InServiceBeforeAcquisition" => InServiceField,
        "Asset_CostRequired" => CostField,
        "Asset_UsefulLifeOutOfRange" or "Asset_DecliningNeedsThreeYears" => LifeField,
        "Asset_ResidualOutOfRange" => ResidualField,
        "Asset_InvoiceReferenceTooLong" => InvoiceReferenceField,
        "Asset_NotesTooLong" => NotesField,
        "Asset_SupplierProvided" or "Asset_InstrumentAlreadyCapitalised" => InstrumentField,
        "Asset_DisposalInFuture" or "Asset_DisposalBeforeAcquisition" => DisposalDateField,
        "Asset_DisposalReasonRequired" or "Asset_DisposalReasonTooLong" => DisposalReasonField,
        _ => null
    };

    /// <summary>The instruments a new asset may be created from: bought, not capitalised, not placed by a supplier.</summary>
    public static IReadOnlyList<AssetInstrumentOption> Candidates(FixedAssetOptions options) => FixedAssetService.Candidates(options);

    // ---- A disposal ----------------------------------------------------------------------------------

    public static AssetDisposalForm NewDisposal(DateOnly today) => new() { Date = today };

    /// <summary>
    /// A disposal dated today at the latest and not before the acquisition, a price that is an
    /// amount and not negative (nothing for a scrapping), and its reason.
    /// </summary>
    public static (FieldRefusal? Refusal, AssetDisposalEntry? Entry) Check(AssetDisposalForm form, FixedAssetRegisterRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(row);
        if (form.Date > today) return (new FieldRefusal(DisposalDateField, "Asset_DisposalInFuture"), null);
        if (form.Date < row.Asset.AcquisitionDate) return (new FieldRefusal(DisposalDateField, "Asset_DisposalBeforeAcquisition"), null);
        if (TypedNumber.IsUnreadable(form.Price))
            return (new FieldRefusal(DisposalPriceField, "Result_NotANumber", form.Price!.Trim()), null);
        var price = TypedNumber.Parse(form.Price) ?? 0m;
        if (price < 0m) return (new FieldRefusal(DisposalPriceField, "Asset_AmountNegative"), null);
        if (string.IsNullOrWhiteSpace(form.Reason)) return (new FieldRefusal(DisposalReasonField, "Asset_DisposalReasonRequired"), null);

        return (null, new AssetDisposalEntry(form.Date, price, form.Reason.Trim()));
    }

    /// <summary>« Annuler la sortie de l'immobilisation IMM-2021-002 ? … ».</summary>
    public static string CancelDisposalQuestion(FixedAssetRegisterRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Assets.CancelDisposalQuestion", row.Asset.Code);
    }

    // ---- The accounts --------------------------------------------------------------------------------

    /// <summary>The box of one account of a category: « account-LaboratoryEquipment-Asset ».</summary>
    public static string AccountField(FixedAssetCategory category, string kind) => $"account-{category}-{kind}";

    public static Dictionary<FixedAssetCategory, CategoryAccountsForm> AccountsForm(FixedAssetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Enum.GetValues<FixedAssetCategory>().ToDictionary(c => c, c =>
        {
            var accounts = settings.For(c);
            return new CategoryAccountsForm { Asset = accounts.Asset, Depreciation = accounts.Depreciation, Charge = accounts.Charge };
        });
    }

    /// <summary>
    /// The accounts as FixedAssetService.SaveSettingsAsync takes them, or the first box at fault:
    /// the asset in class 2 (not 28), its depreciation in 28, the charge in class 6, digits only.
    /// </summary>
    public static (FieldRefusal? Refusal, FixedAssetSettings? Settings) Check(
        IReadOnlyDictionary<FixedAssetCategory, CategoryAccountsForm> form, FixedAssetSettings current)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(current);
        var accounts = new Dictionary<FixedAssetCategory, CategoryAccounts>();
        foreach (var category in Enum.GetValues<FixedAssetCategory>())
        {
            var typed = form.TryGetValue(category, out var boxes) ? boxes : new CategoryAccountsForm();
            var (asset, depreciation, charge) = (Trim(typed.Asset), Trim(typed.Depreciation), Trim(typed.Charge));
            if (!FixedAssetSettings.IsAssetAccount(asset))
                return (new FieldRefusal(AccountField(category, "Asset"), "Asset_AccountInvalid", asset), null);
            if (!FixedAssetSettings.IsDepreciationAccount(depreciation))
                return (new FieldRefusal(AccountField(category, "Depreciation"), "Asset_AccountInvalid", depreciation), null);
            if (!FixedAssetSettings.IsChargeAccount(charge))
                return (new FieldRefusal(AccountField(category, "Charge"), "Asset_AccountInvalid", charge), null);
            accounts[category] = new CategoryAccounts(asset, depreciation, charge);
        }

        return (null, current with { Accounts = accounts });
    }

    // ---- The plan of an asset ------------------------------------------------------------------------

    /// <summary>« Plan d'amortissement — IMM-2026-001 Automate de biochimie ».</summary>
    public static string ScheduleTitle(FixedAssetRegisterRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Assets.ScheduleTitle", row.Asset.Code, row.Asset.Label);
    }

    /// <summary>« Valeur brute … · Linéaire sur 5 ans · acquisition … · mise en service … · comptes 6193 / 28332 ».</summary>
    public static string ScheduleHeader(FixedAssetRegisterRow row, DepreciationSchedule schedule, ScreenText text,
                                        Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return text("Assets.ScheduleHeader", amount(schedule.GrossValue), text(MethodKey(row.Asset.Method)), row.Asset.UsefulLifeYears,
                    row.Asset.AcquisitionDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    row.Asset.InServiceDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    row.Accounts.Charge, row.Accounts.Depreciation);
    }

    /// <summary>The plan's months, each with the depreciation accumulated at its end.</summary>
    public static IReadOnlyList<AssetScheduleMonth> ScheduleMonths(DepreciationSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var accumulated = 0m;
        var months = new List<AssetScheduleMonth>(schedule.Months.Count);
        foreach (var m in schedule.Months)
        {
            accumulated = Money.Round(accumulated + m.Amount);
            months.Add(new AssetScheduleMonth($"{m.Month:00}/{m.Year}", m.Date, m.Amount, accumulated));
        }
        return months;
    }

    /// <summary>« Linéaire » or « Dégressif »: the mode of a year of the plan (a declining plan ends straight-line).</summary>
    public static string YearModeKey(DepreciationYear year)
    {
        ArgumentNullException.ThrowIfNull(year);
        return year.StraightLine ? MethodKey(DepreciationMethod.StraightLine) : MethodKey(DepreciationMethod.Declining);
    }

    public static string ScheduleFileName(FixedAssetRegisterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"plan-amortissement-{FileSafe(row.Asset.Code)}.csv";
    }

    // ---- The instrument linked ----------------------------------------------------------------------

    /// <summary>The instrument behind a line: an asset's, or the supplier's equipment itself.</summary>
    public static Guid? InstrumentOf(object? line) => line switch
    {
        FixedAssetRegisterRow row => row.Asset.InstrumentId,
        SupplierProvidedRow provided => provided.InstrumentId,
        _ => null
    };

    /// <summary>« Équipement lié — maintenance et pannes — AUT-01 · Automate · En service ».</summary>
    public static string InstrumentHeading(AssetInstrumentSummary? summary, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return summary is null
            ? text("Assets.InstrumentSection")
            : $"{text("Assets.InstrumentSection")} — {summary.Code} · {summary.Name.For(language)} · {text($"EquipmentStatus.{summary.Status}")}";
    }

    /// <summary>The instrument's make, serial number, interventions, breakdowns, downtime, cost and next due dates.</summary>
    public static string InstrumentSummaryText(AssetInstrumentSummary summary, ScreenText text, Func<decimal, string> amount,
                                               IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        static string Date(DateOnly? date) => date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—";
        return text("Assets.InstrumentSummary",
                    string.Join(" ", new[] { summary.Manufacturer, summary.Model }.Where(v => !string.IsNullOrWhiteSpace(v))),
                    summary.SerialNumber ?? "—", summary.Interventions.Count, summary.Breakdowns,
                    summary.DowntimeHours.ToString("0.#", culture), amount(summary.MaintenanceCost),
                    Date(summary.NextPreventiveMaintenanceDue), Date(summary.NextCalibrationDue),
                    Date(summary.MaintenanceContractValidUntil));
    }

    /// <summary>A breakdown is the line to look at: the row in the warning's colours, its nature in its ink.</summary>
    public static bool IsBreakdown(AssetMaintenanceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Kind == Domain.Equipment.MaintenanceKind.Corrective;
    }

    // ---- The register's printout ---------------------------------------------------------------------

    /// <summary>The register's column headings, in the screen's language.</summary>
    public static IReadOnlyList<string> Headers(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            text("Field.Code"), text("Assets.Label"), text("Assets.Category"), text("Assets.Account"), text("Assets.AcquisitionDate"),
            text("Assets.InServiceDate"), text("Assets.GrossValue"), text("Assets.Method"), text("Assets.UsefulLife"),
            text("Assets.Rate"), text("Assets.Opening"), text("Assets.Charge"), text("Assets.GrossOut"), text("Assets.DepreciationOut"),
            text("Assets.ClosingGross"), text("Assets.Closing"), text("Assets.NetBookValue"),
            text("Assets.DisposalDate"), text("Assets.DisposalPrice"), text("Assets.DisposalResult"), text("Inventory.Supplier"),
            text("Assets.InvoiceReference"), text("Assets.Instrument")
        ];
    }

    /// <summary>The register's lines in the screen's language: per category its assets and subtotal, the total, the supplier's equipment.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReportLines(FixedAssetRegister register, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(text);
        return FixedAssetRegisterReport.Lines(register, c => text(CategoryKey(c)), m => text(MethodKey(m)),
                                              p => p.InstrumentName.For(language), text("Assets.Subtotal"),
                                              text("Assets.Total"), text("Assets.SupplierProvided"));
    }

    /// <summary>The PDF's columns and lines: the accounting columns only.</summary>
    public static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) PdfTable(
        FixedAssetRegister register, AppLanguage language, ScreenText text) =>
        ([.. Headers(text).Take(PdfColumns)],
         [.. ReportLines(register, language, text).Select(l => (IReadOnlyList<string>)[.. l.Take(PdfColumns)])]);

    public static string ReportTitle(FixedAssetRegister register, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(text);
        return text("Assets.ReportTitle", register.Year);
    }

    public static string ReportCaption(DateTimeOffset now, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Assets.ReportCaption")} — {now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>« immobilisations-2026.pdf » or « .csv ».</summary>
    public static string ReportFileName(FixedAssetRegister register, bool pdf)
    {
        ArgumentNullException.ThrowIfNull(register);
        return $"immobilisations-{register.Year.ToString(CultureInfo.InvariantCulture)}.{(pdf ? "pdf" : "csv")}";
    }

    private static string FileSafe(string text) =>
        new([.. text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

    private static string Trim(string? text) => text?.Trim() ?? string.Empty;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
