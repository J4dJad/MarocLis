using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Input;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Administration;

/// <summary>The tabs of « Paramètres », in their order on the screen.</summary>
public enum SettingsTab
{
    Site,
    Numbering,
    Settings,
    InvoicePrint,
    Languages,
    Storage,
    Printers,
    Backups,
    Licence
}

/// <summary>What the numbering tab offers on the selected series.</summary>
/// <param name="Closes">The button reads « Clôturer » on an open series, « Rouvrir » on a closed one.</param>
public sealed record SequenceActions(bool EditFormat, bool ToggleClosed, bool SetStart, bool Closes);

/// <summary>The site's identity as typed in the form, before it becomes a request: its boxes write to it.</summary>
public sealed record SiteForm
{
    public string Code { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public LabSiteKind Kind { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string? Ice { get; set; }
    public string? FiscalId { get; set; }
    public string? CommercialRegisterNumber { get; set; }
    public string? CommercialRegisterCourt { get; set; }
    public string? ProfessionalTaxNumber { get; set; }
    public string? CnssAffiliationNumber { get; set; }
    public string? Inpe { get; set; }
    public string? MinistryAuthorization { get; set; }

    /// <summary>The authorisation's date; null when its box is not ticked.</summary>
    public DateOnly? MinistryAuthorizationDate { get; set; }

    public bool IsAccredited { get; set; }
    public string? AccreditationNumber { get; set; }
    public DateOnly? AccreditationValidUntil { get; set; }

    /// <summary>The responsible biologist chosen; null for « — ».</summary>
    public Guid? ResponsibleBiologistUserId { get; set; }

    public string? AddressFr { get; set; }
    public string? AddressAr { get; set; }
    public string? City { get; set; }
    public MoroccanRegion Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    /// <summary>A logo chosen in this form, as a PNG; null when none was chosen.</summary>
    public byte[]? NewLogo { get; set; }

    /// <summary>Whether the stored logo was removed in this form.</summary>
    public bool LogoRemoved { get; set; }
}

/// <summary>
/// The rules of « Paramètres » that need no window: which tabs are offered to whom (the licence's
/// tab with its own right alone), the site's form and the banners of what still blocks invoicing
/// and reporting, what the numbering offers on a series, how a setting is shown, the storage
/// places, the discount ceiling typed, and the languages ticked. The workstation's view and the
/// web's screen apply the same ones; the decisions they lead to are the services'.
/// </summary>
public static class SettingsScreenRules
{
    // The lengths of the site's columns: a longer text failed the save with « Erreur inattendue ».
    // The identifiers the service checks (ICE, IF, CNSS, INPE) are left to its message.
    public const int SiteNameMaxLength = 300;
    public const int LegalNameMaxLength = 250;
    public const int RegisterMaxLength = 40;
    public const int CourtMaxLength = 100;
    public const int AuthorizationMaxLength = 60;
    public const int AddressMaxLength = 400;
    public const int PostalCodeMaxLength = 10;
    public const int ContactMaxLength = 400;

    /// <summary>The columns of a setting: a longer key or value failed the save with an unexpected error.</summary>
    public const int SettingKeyMaxLength = 120;
    public const int SettingValueMaxLength = 2000;

    public const int SequenceFormatMaxLength = 100;
    public const int SequenceStartMaxLength = 12;
    public const int SequenceReasonMaxLength = 500;

    /// <summary>The widest logo kept, scaled down as a PNG: printed at the head of every report and invoice.</summary>
    public const int LogoMaxWidth = 800;

    /// <summary>What a sensitive setting shows in place of its value: some hold credentials.</summary>
    public const string MaskedValue = "••••••••";

    /// <summary>The settings proper: the sites or the settings right; the licence and the backups have rights of their own.</summary>
    public static bool HasSettingsRight(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Has(Permissions.Administration.ManageSites) || user.Has(Permissions.Administration.ManageSettings);
    }

    /// <summary>
    /// The tabs offered to <paramref name="user"/>, in their order. The storage places are
    /// settings work, not even listed without the right; the backups likewise with a right of
    /// their own. The licence tab with its own right, and that right alone opens the screen on it
    /// only — with the backups beside it when that right is held too: both are governed by their own.
    /// </summary>
    /// <param name="licenceAvailable">The installation evaluates a licence.</param>
    /// <param name="backupsAvailable">The installation can reach SQL Server's backups.</param>
    public static IReadOnlyList<SettingsTab> Tabs(ICurrentUser user, bool licenceAvailable = true, bool backupsAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(user);

        var tabs = Enum.GetValues<SettingsTab>().ToList();

        if (!user.Has(Permissions.Administration.ManageSettings)) tabs.Remove(SettingsTab.Storage);
        if (!BackupsOffered(user, backupsAvailable)) tabs.Remove(SettingsTab.Backups);

        if (!licenceAvailable || !user.Has(Permissions.Administration.License))
            tabs.Remove(SettingsTab.Licence);
        else if (!HasSettingsRight(user))
            tabs.RemoveAll(t => t is not (SettingsTab.Licence or SettingsTab.Backups));

        return tabs;
    }

    public static bool BackupsOffered(ICurrentUser user, bool backupsAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(user);
        return backupsAvailable && user.Has(Permissions.Administration.Backup);
    }

    // ---- Site ----------------------------------------------------------------------------------

    /// <summary>
    /// The code, the legal name (the site's name while it has none, as a site just created) and the
    /// kind, so a partner laboratory is not mistaken for one of the laboratory's own sites.
    /// </summary>
    public static string SiteDisplay(SiteDetail site, string kindText)
    {
        ArgumentNullException.ThrowIfNull(site);
        return $"{site.Code} — {(string.IsNullOrWhiteSpace(site.LegalName) ? site.Name.Fr : site.LegalName)} ({kindText})";
    }

    /// <summary>
    /// A partner laboratory neither invoices nor releases reports here: only its identity and
    /// contact details are used, on the dispatch slips. Its blockers would only be noise.
    /// </summary>
    public static bool ShowsBlockers(LabSiteKind kind) => kind != LabSiteKind.ReferralPartner;

    /// <summary>
    /// Only a qualified biologist may be named responsible for the site: loi n° 12-01 reserves the
    /// direction of a medical biology laboratory to one, and the service refuses anyone else.
    /// </summary>
    public static IReadOnlyList<UserRow> ResponsibleCandidates(IEnumerable<UserRow> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        return [.. users.Where(u => u.QualifiedToValidate)];
    }

    public static string ResponsibleDisplay(UserRow biologist)
    {
        ArgumentNullException.ThrowIfNull(biologist);
        return $"{biologist.FullName} ({biologist.ProfessionalOrderNumber})";
    }

    /// <summary>The form opened on a site: what it shows, as it was recorded.</summary>
    public static SiteForm FormOf(SiteDetail site)
    {
        ArgumentNullException.ThrowIfNull(site);

        return new SiteForm
        {
            Code = site.Code,
            NameFr = site.Name.Fr,
            NameAr = site.Name.Ar,
            Kind = site.Kind,
            LegalName = site.LegalName,
            Ice = site.Ice,
            FiscalId = site.FiscalId,
            CommercialRegisterNumber = site.CommercialRegisterNumber,
            CommercialRegisterCourt = site.CommercialRegisterCourt,
            ProfessionalTaxNumber = site.ProfessionalTaxNumber,
            CnssAffiliationNumber = site.CnssAffiliationNumber,
            Inpe = site.Inpe,
            MinistryAuthorization = site.HealthMinistryAuthorizationNumber,
            MinistryAuthorizationDate = site.HealthMinistryAuthorizationDate,
            IsAccredited = site.IsAccredited,
            AccreditationNumber = site.AccreditationNumber,
            AccreditationValidUntil = site.AccreditationValidUntil,
            ResponsibleBiologistUserId = site.ResponsibleBiologistUserId,
            AddressFr = site.AddressLine?.Fr,
            AddressAr = site.AddressLine?.Ar,
            City = site.City,
            Region = site.Region,
            PostalCode = site.PostalCode,
            Phone = site.Phone,
            Fax = site.Fax,
            Email = site.Email,
            Website = site.Website
        };
    }

    /// <summary>
    /// The request a save sends: the English names kept as they were (no box types them), the
    /// accreditation's end only while accredited, a logo removed only when none was chosen after.
    /// </summary>
    public static SiteRequest ToRequest(SiteDetail site, SiteForm form)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(form);

        return new SiteRequest
        {
            SiteId = site.Id,
            Code = form.Code,
            Name = new MultilingualText(form.NameFr.Trim(), (form.NameAr ?? string.Empty).Trim(), site.Name.En),
            Kind = form.Kind,
            LegalName = form.LegalName,
            Ice = form.Ice,
            FiscalId = form.FiscalId,
            CommercialRegisterNumber = form.CommercialRegisterNumber,
            CommercialRegisterCourt = form.CommercialRegisterCourt,
            ProfessionalTaxNumber = form.ProfessionalTaxNumber,
            CnssAffiliationNumber = form.CnssAffiliationNumber,
            Inpe = form.Inpe,
            HealthMinistryAuthorizationNumber = form.MinistryAuthorization,
            HealthMinistryAuthorizationDate = form.MinistryAuthorizationDate,
            IsAccredited = form.IsAccredited,
            AccreditationNumber = form.AccreditationNumber,
            AccreditationValidUntil = form.IsAccredited ? form.AccreditationValidUntil : null,
            ResponsibleBiologistUserId = form.ResponsibleBiologistUserId == Guid.Empty ? null : form.ResponsibleBiologistUserId,
            AddressLine = new MultilingualText((form.AddressFr ?? string.Empty).Trim(), (form.AddressAr ?? string.Empty).Trim(),
                                               site.AddressLine?.En),
            City = form.City,
            Region = form.Region,
            PostalCode = form.PostalCode,
            Phone = form.Phone,
            Fax = form.Fax,
            Email = form.Email,
            Website = form.Website,
            LogoPng = form.NewLogo,
            RemoveLogo = form.LogoRemoved && form.NewLogo is null,
            IsActive = site.IsActive
        };
    }

    /// <summary>What a save says: complete when the site may invoice and release reports, saved otherwise.</summary>
    public static (string Key, bool Complete) SavedStatus(SiteSaveOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var complete = outcome.CanInvoice && outcome.CanReleaseReports;
        return (complete ? "Site.ConfigurationComplete" : "Message.Saved", complete);
    }

    /// <summary>The site a creation asks for: the code in capitals, the Arabic name only when typed.</summary>
    public static NewSiteRequest NewSite(string code, string nameFr, string? nameAr, LabSiteKind kind) => new()
    {
        Code = code.Trim(),
        Name = new MultilingualText(nameFr.Trim(), string.IsNullOrWhiteSpace(nameAr) ? null : nameAr.Trim()),
        Kind = kind
    };

    // ---- Numbering -----------------------------------------------------------------------------

    /// <summary>
    /// A format is editable only before the first number is handed out — after that the DGI's
    /// requirement of an unbroken chronological series makes a change unreconcilable; the start
    /// moves before the first number, or forward on a series that is not fiscal.
    /// </summary>
    public static SequenceActions Actions(ICurrentUser user, SequenceRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);

        var manage = user.Has(Permissions.Administration.ManageSettings);
        return new SequenceActions(
            manage && row is { HasBeenUsed: false, IsClosed: false },
            manage && row is not null,
            manage && row is { IsClosed: false } && (!row.HasBeenUsed || !NumberSequence.IsFiscal(row.Purpose)),
            row is not { IsClosed: true });
    }

    /// <summary>« Clôturée », « En service » or « Modifiable ».</summary>
    public static string StateKey(SequenceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsClosed ? "Admin.SequenceClosed" : row.HasBeenUsed ? "Admin.SequenceInUse" : "Admin.SequenceEditable";
    }

    /// <summary>The next number typed: digits only, or null (« Sequence_StartOutOfRange »).</summary>
    public static long? ParseStart(string? typed) =>
        long.TryParse(typed?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var next) ? next : null;

    // ---- Settings ------------------------------------------------------------------------------

    /// <summary>A sensitive value is not shown: some hold credentials, and the screen is used at a shared workstation.</summary>
    public static string DisplayValue(SettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsSecuritySensitive ? MaskedValue : row.Value ?? string.Empty;
    }

    /// <summary>What the editor of a setting starts from: nothing for a sensitive one.</summary>
    public static string EditInitial(SettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsSecuritySensitive ? string.Empty : row.Value ?? string.Empty;
    }

    public static bool CanEditSettings(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Has(Permissions.Administration.ManageSettings);
    }

    public static bool CanEditSites(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Has(Permissions.Administration.ManageSites);
    }

    // ---- Storage places ------------------------------------------------------------------------

    /// <summary>Renaming and switching off take a place; switching one off is confirmed with the tubes it holds.</summary>
    public static (bool Rename, bool Toggle, bool Reactivates) PlaceActions(ICurrentUser user, SampleStorageLocationRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);
        var manage = user.Has(Permissions.Administration.ManageSettings);
        return (manage && row is not null, manage && row is not null, row is { IsActive: false });
    }

    // ---- Invoices and the discount ceiling -----------------------------------------------------

    /// <summary>
    /// The ceiling as typed, or the box that is wrong: a percentage from 0 to 100, and an amount
    /// (none for no ceiling in dirhams) from 0 to <see cref="DiscountCapSettings.AmountLimit"/>.
    /// </summary>
    public static (DiscountCapSettings? Cap, FieldRefusal? Refusal) ReadDiscountCap(string? percentText, string? amountText)
    {
        if (TypedNumber.Parse(percentText) is not { } rate || rate < 0m || rate > 100m)
            return (null, new FieldRefusal("cap-percent", "DiscountCap_PercentOutOfRange"));

        decimal? amount = null;
        if (!string.IsNullOrWhiteSpace(amountText))
        {
            amount = TypedNumber.Parse(amountText);
            if (amount is not { } value || value < 0m || value > DiscountCapSettings.AmountLimit)
                return (null, new FieldRefusal("cap-amount", "DiscountCap_AmountOutOfRange"));
        }

        return (new DiscountCapSettings { MaxPercent = rate, MaxAmount = amount }, null);
    }

    /// <summary>The ceiling as its boxes show it, in the operator's culture.</summary>
    public static (string Percent, string Amount) DiscountCapTexts(DiscountCapSettings cap, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(cap);
        ArgumentNullException.ThrowIfNull(culture);
        // The amount without the direction mark the Arabic culture puts before a minus sign: the
        // box reads back as the value it shows.
        return (cap.MaxPercent.ToString("0.##", culture),
                cap.MaxAmount is { } amount
                    ? TypedNumber.WithoutDirectionMarks(amount.ToString("0.00##########", culture))
                    : string.Empty);
    }

    // ---- Languages -----------------------------------------------------------------------------

    /// <summary>French always — the working language, the one a record in a language switched off is shown in — and the ones ticked.</summary>
    public static IReadOnlyList<AppLanguage> ActiveLanguages(bool arabic, bool english)
    {
        List<AppLanguage> active = [AppLanguage.French];
        if (arabic) active.Add(AppLanguage.Arabic);
        if (english) active.Add(AppLanguage.English);
        return active;
    }

    /// <summary>Arabic is a module of the licence: without it its box stays off, and says why.</summary>
    public static bool ArabicLicensed(LicenceStatus? licence) => licence?.HasModule(LicenceModules.Arabic) ?? true;
}
