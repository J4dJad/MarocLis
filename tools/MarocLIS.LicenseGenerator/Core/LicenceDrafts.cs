using System.Text.Encodings.Web;
using System.Text.Json;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.LicenseGenerator.Core;

/// <summary>The French words of the generator for what the domain names in code.</summary>
public static class GeneratorTexts
{
    public static string Kind(LicenceKind kind) => kind switch
    {
        LicenceKind.Trial => "Essai",
        LicenceKind.Annual => "Annuelle",
        LicenceKind.Perpetual => "Perpétuelle",
        LicenceKind.Training => "Formation",
        _ => kind.ToString()
    };

    // As MarocLIS's French catalogue names them on the licence tab, so both programs say the same.
    public static string Module(string module) => module switch
    {
        LicenceModules.Instruments => "Connexion automates",
        LicenceModules.MobileValidation => "Validation mobile",
        LicenceModules.Arabic => "Langue arabe",
        LicenceModules.ThirdPartyPayer => "Tiers payant",
        LicenceModules.Quality => "Qualité",
        LicenceModules.StockAndPurchasing => "Stocks et achats",
        LicenceModules.Equipment => "Équipements",
        LicenceModules.HumanResources => "Ressources humaines",
        LicenceModules.Payroll => "Paie",
        LicenceModules.Relations => "Relations et messagerie",
        LicenceModules.Finance => "Finance et comptabilité",
        LicenceModules.Steering => "Pilotage",
        LicenceModules.MultiSite => "Multi-sites",
        _ => module
    };

    /// <summary>The domain's reasons for refusing terms, in French.</summary>
    public static string Problem(string key) => key switch
    {
        "Licence_NumberRequired" => "Le numéro de licence est obligatoire.",
        "Licence_VersionInvalid" => "La version doit être 1 ou plus.",
        "Licence_LaboratoryRequired" => "Le nom du laboratoire est obligatoire.",
        "Licence_WorkstationsInvalid" => "Le nombre de postes doit être d'au moins 1.",
        "Licence_EndRequired" => "Une licence d'essai ou annuelle a une date de fin.",
        "Licence_EndBeforeStart" => "La date de fin précède la date de début.",
        "Licence_UpdatesUntilRequired" => "Une licence perpétuelle indique jusqu'à quand les mises à jour sont couvertes.",
        "Licence_TrainingOneWorkstation" => "Une licence de formation vaut pour un seul poste.",
        "Licence_PeriodsInvalid" => "Préavis, grâce et paillasse vont de 0 à 365 jours.",
        _ => key
    };
}

/// <summary>
/// The terms a new licence starts from, by kind: the values the licensing design left undecided,
/// filled with its proposals, each still editable on the form.
/// </summary>
public static class LicenceDrafts
{
    public static LicenceTerms For(LicenceKind kind, string number, DateOnly today, LicencePacks.Pack pack) =>
        Apply(new LicenceTerms
        {
            Number = number,
            Laboratory = string.Empty,
            Kind = kind,
            IssuedOn = today,
            StartsOn = today,
            Workstations = pack.Workstations,
            Modules = pack.Modules
        }, kind, today);

    /// <summary>
    /// The dates, the binding and the workstations a kind implies, kept where the kind leaves a
    /// choice: switching from annual to trial shortens the end to thirty days and unbinds the server.
    /// </summary>
    public static LicenceTerms Apply(LicenceTerms terms, LicenceKind kind, DateOnly today)
    {
        var start = terms.StartsOn == default ? today : terms.StartsOn;
        return kind switch
        {
            LicenceKind.Trial => terms with
            {
                Kind = kind,
                EndsOn = start.AddDays(LicenceTerms.DefaultTrialDays),
                UpdatesUntil = null
            },
            LicenceKind.Annual => terms with
            {
                Kind = kind,
                EndsOn = start.AddYears(1).AddDays(-1),
                UpdatesUntil = null
            },
            LicenceKind.Perpetual => terms with
            {
                Kind = kind,
                EndsOn = null,
                UpdatesUntil = start.AddYears(1)
            },
            LicenceKind.Training => terms with
            {
                Kind = kind,
                EndsOn = null,
                UpdatesUntil = null,
                Workstations = 1,
                ServerId = null
            },
            _ => terms
        };
    }

    /// <summary>
    /// Server binding is on by default and off for a trial or training licence: those are installed
    /// before the laboratory's server exists, often on a demonstration laptop.
    /// </summary>
    public static bool BindsByDefault(LicenceKind kind) => kind is LicenceKind.Annual or LicenceKind.Perpetual;

    /// <summary>The licence file's name: number and version, so successive versions do not overwrite one another.</summary>
    public static string FileName(LicenceTerms terms) => $"{terms.Number}-v{terms.Version}.lic";
}

/// <summary>
/// The packs the form offers, editable and kept beside the register. They only fill the form: a
/// licence lists its modules one by one and does not name a pack.
/// </summary>
public static class PackPresets
{
    public const string FileName = "packs.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<LicencePacks.Pack> Load(string path)
    {
        if (!File.Exists(path)) return LicencePacks.Defaults;

        var packs = JsonSerializer.Deserialize<List<LicencePacks.Pack>>(File.ReadAllText(path), Json);
        return packs is { Count: > 0 } ? packs : LicencePacks.Defaults;
    }

    public static void Save(string path, IEnumerable<LicencePacks.Pack> packs) =>
        KeyVault.WriteAtomically(path, JsonSerializer.Serialize(packs.ToList(), Json));
}
