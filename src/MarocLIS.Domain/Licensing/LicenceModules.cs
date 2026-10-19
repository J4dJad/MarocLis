namespace MarocLIS.Domain.Licensing;

/// <summary>
/// The optional modules as an enumeration, named exactly as in the licence file: a refusal names
/// the missing module through it, and the localizer translates an enumeration value it is given
/// as an argument (« LicenceModule.Qualite »).
/// </summary>
public enum LicenceModule
{
    Instruments,
    ValidationMobile,
    Arabe,
    TiersPayant,
    Qualite,
    StocksAchats,
    Equipements,
    RessourcesHumaines,
    Paie,
    Relations,
    Finance,
    Pilotage,
    MultiSites
}

/// <summary>
/// The optional modules a licence opens, by the identifier written in the licence file.
/// <para>
/// Everything not listed here is the base (« socle ») and is always on: patients, orders,
/// collection and reception, recollections, the serum bank, subcontracting, result entry and
/// validation at the bench, reports, billing and the till, simple statistics, the catalogue,
/// users, the audit trail, the reference data and the settings. A laboratory that loses every
/// module keeps a laboratory that runs from registration to report.
/// </para>
/// <para>
/// The identifiers are French words, as the rest of the file is: the file is meant to be readable
/// by whoever receives it. They are part of the signed text, so renaming one invalidates every
/// licence already issued with it — add, never rename.
/// </para>
/// </summary>
public static class LicenceModules
{
    /// <summary>Instrument interfaces, frame testing, automatic reception of results.</summary>
    public const string Instruments = nameof(LicenceModule.Instruments);

    /// <summary>The biologist's phone application: validation, critical values, report release.</summary>
    public const string MobileValidation = nameof(LicenceModule.ValidationMobile);

    /// <summary>The Arabic interface and Arabic reports.</summary>
    public const string Arabic = nameof(LicenceModule.Arabe);

    /// <summary>Third-party payer claims (« Bordereaux »).</summary>
    public const string ThirdPartyPayer = nameof(LicenceModule.TiersPayant);

    /// <summary>Internal QC and Westgard rules, EQA, non-conformities, controlled documents.</summary>
    public const string Quality = nameof(LicenceModule.Qualite);

    /// <summary>Stock, purchase orders, quotes, supplier invoices and evaluation, counts, temperatures.</summary>
    public const string StockAndPurchasing = nameof(LicenceModule.StocksAchats);

    /// <summary>The equipment register and the instrument contracts.</summary>
    public const string Equipment = nameof(LicenceModule.Equipements);

    /// <summary>Personnel, habilitations, deadlines, leave, planning, work accidents.</summary>
    public const string HumanResources = nameof(LicenceModule.RessourcesHumaines);

    /// <summary>Payroll: apart from HR because it is sensitive and often done by an accountant.</summary>
    public const string Payroll = nameof(LicenceModule.Paie);

    /// <summary>Prescriber statistics, delegate visits and conventions, patient messaging.</summary>
    public const string Relations = nameof(LicenceModule.Relations);

    /// <summary>Accounting export, fixed assets, bank reconciliation, cash forecast, till review.</summary>
    public const string Finance = nameof(LicenceModule.Finance);

    /// <summary>The management dashboard and the cost and margin analysis.</summary>
    public const string Steering = nameof(LicenceModule.Pilotage);

    /// <summary>More than one site (main laboratory and satellites) in the same database.</summary>
    public const string MultiSite = nameof(LicenceModule.MultiSites);

    /// <summary>Every module, in the order the licence tab and the generator list them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Instruments, MobileValidation, Arabic, ThirdPartyPayer, Quality,
        StockAndPurchasing, Equipment, HumanResources, Payroll, Relations, Finance, Steering,
        MultiSite
    ];

    public static bool IsKnown(string module) => All.Contains(module, StringComparer.Ordinal);

    /// <summary>The resource key of a module's name, « LicenceModule.Qualite » for instance.</summary>
    public static string NameKey(string module) => "LicenceModule." + module;

    /// <summary>The module as an enumeration value, for a message that names it; null when unknown.</summary>
    public static LicenceModule? AsEnum(string module) =>
        Enum.TryParse<LicenceModule>(module, ignoreCase: false, out var value) ? value : null;
}

/// <summary>
/// The packs a licence is usually sold as. Only a starting point: the licence file lists modules
/// one by one, so a module can be sold on its own, and the generator lets these presets be edited.
/// The proposals of the licensing design (2, 5 and 10 workstations) are the defaults.
/// </summary>
public static class LicencePacks
{
    public sealed record Pack(string Name, int Workstations, IReadOnlyList<string> Modules);

    public static Pack Essential { get; } = new("Essentiel", 2, []);

    public static Pack Professional { get; } = new("Professionnel", 5,
    [
        LicenceModules.Instruments, LicenceModules.MobileValidation, LicenceModules.Arabic,
        LicenceModules.ThirdPartyPayer, LicenceModules.Quality
    ]);

    // Multi-site is an option of every pack, not part of the complete one.
    public static Pack Complete { get; } = new("Complet", 10,
        [.. LicenceModules.All.Where(m => m != LicenceModules.MultiSite)]);

    public static IReadOnlyList<Pack> Defaults { get; } = [Essential, Professional, Complete];
}
