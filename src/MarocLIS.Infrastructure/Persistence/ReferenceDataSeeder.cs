using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// Seeds the reference data the application cannot start without: disciplines, specimen and
/// container types, units, payers, roles with their permission sets, the number sequences, and a
/// first administrator.
/// <para>
/// Idempotent by design — it runs on every start and inserts only what is missing, so a version
/// that adds a permission or a discipline needs no migration step from the laboratory.
/// </para>
/// </summary>
public static class ReferenceDataSeeder
{
    /// <summary>
    /// Value of the biology letter-key in dirham used for the seeded tariffs.
    /// <para>
    /// <b>This is a placeholder and must be set by the laboratory.</b> The reimbursement base of a
    /// biology act is <c>coefficient × letter-key value</c>, and the letter-key value is fixed
    /// nationally by the Tarif National de Référence. Seeding a guess and letting it stand would
    /// mis-state every AMO claim, so the figure is surfaced in Administration → Tariffs with a
    /// warning until an administrator confirms it against the current TNR.
    /// </para>
    /// </summary>
    public const decimal PlaceholderLetterKeyValueMad = 1.00m;

    public static async Task SeedAsync(LisDbContext context, IPasswordHasher passwordHasher,
                                       IClock clock, CancellationToken ct = default)
    {
        await SeedDepartmentsAsync(context, ct);
        await SeedUnitsAsync(context, ct);
        await SeedSpecimenAndContainerTypesAsync(context, ct);
        await SeedPayersAsync(context, ct);
        await SeedRolesAsync(context, ct);
        var site = await SeedDefaultSiteAsync(context, ct);
        await SeedSequencesAsync(context, site.Id, clock, ct);
        await SeedAdministratorAsync(context, passwordHasher, clock, site.Id, ct);
        await SeedInterfaceAccountAsync(context, passwordHasher, site.Id, ct);
        await SeedStarterCatalogueAsync(context, clock, ct);
    }

    // ---- Disciplines ---------------------------------------------------------------------------

    private static async Task SeedDepartmentsAsync(LisDbContext context, CancellationToken ct)
    {
        (string Code, string Fr, string Ar, string En, int Order)[] departments =
        [
            (Department.Codes.Biochemistry,  "Biochimie",              "الكيمياء الحيوية",     "Biochemistry",    10),
            (Department.Codes.Haematology,   "Hématologie",            "علم الدم",             "Haematology",     20),
            (Department.Codes.Haemostasis,   "Hémostase",              "تخثر الدم",            "Haemostasis",     30),
            (Department.Codes.Immunology,    "Immunologie",            "علم المناعة",          "Immunology",      40),
            (Department.Codes.Serology,      "Sérologie",              "علم الأمصال",          "Serology",        50),
            (Department.Codes.Hormonology,   "Hormonologie",           "علم الهرمونات",        "Hormonology",     60),
            (Department.Codes.Bacteriology,  "Bactériologie",          "علم الجراثيم",         "Bacteriology",    70),
            (Department.Codes.Parasitology,  "Parasitologie",          "علم الطفيليات",        "Parasitology",    80),
            (Department.Codes.Mycology,      "Mycologie",              "علم الفطريات",         "Mycology",        90),
            (Department.Codes.Virology,      "Virologie",              "علم الفيروسات",        "Virology",       100),
            (Department.Codes.Genetics,      "Génétique",              "علم الوراثة",          "Genetics",       110),
            (Department.Codes.Toxicology,    "Toxicologie",            "علم السموم",           "Toxicology",     120),
            (Department.Codes.Cytology,      "Cytologie",              "علم الخلايا",          "Cytology",       130),
            (Department.Codes.UrineAnalysis, "Examen des urines",      "تحليل البول",          "Urine analysis", 140)
        ];

        var existing = await context.Departments.IgnoreQueryFilters()
            .Select(d => d.Code).ToListAsync(ct);

        foreach (var (code, fr, ar, en, order) in departments.Where(d => !existing.Contains(d.Code)))
        {
            context.Departments.Add(new Department(code, new MultilingualText(fr, ar, en))
            {
                DisplayOrder = order
            });
        }

        await context.SaveChangesAsync(ct);
    }

    // ---- Units ---------------------------------------------------------------------------------

    private static async Task SeedUnitsAsync(LisDbContext context, CancellationToken ct)
    {
        // Moroccan laboratories report in conventional units; the UCUM code is kept so exports and
        // instrument mappings stay unambiguous.
        (string Code, string Symbol, string? Ucum)[] units =
        [
            ("G_L",       "g/L",        "g/L"),
            ("G_DL",      "g/dL",       "g/dL"),
            ("MG_L",      "mg/L",       "mg/L"),
            ("MG_DL",     "mg/dL",      "mg/dL"),
            ("UG_L",      "µg/L",       "ug/L"),
            ("NG_ML",     "ng/mL",      "ng/mL"),
            ("PG_ML",     "pg/mL",      "pg/mL"),
            ("MMOL_L",    "mmol/L",     "mmol/L"),
            ("UMOL_L",    "µmol/L",     "umol/L"),
            ("NMOL_L",    "nmol/L",     "nmol/L"),
            ("PMOL_L",    "pmol/L",     "pmol/L"),
            ("MEQ_L",     "mEq/L",      "meq/L"),
            ("UI_L",      "UI/L",       "U/L"),
            ("MUI_L",     "mUI/L",      "m[IU]/L"),
            ("UI_ML",     "UI/mL",      "[IU]/mL"),
            ("PERCENT",   "%",          "%"),
            ("G_24H",     "g/24 h",     "g/(24.h)"),
            ("MG_24H",    "mg/24 h",    "mg/(24.h)"),
            ("MM_H",      "mm/h",       "mm/h"),
            ("FL",        "fL",         "fL"),
            ("PG",        "pg",         "pg"),
            ("GIGA_L",    "10⁹/L",      "10*9/L"),
            ("TERA_L",    "10¹²/L",     "10*12/L"),
            ("PER_MM3",   "/mm³",       "/mm3"),
            ("SECONDS",   "s",          "s"),
            ("RATIO",     "ratio",      "1"),
            ("INR",       "INR",        "1"),
            ("ML_MIN",    "mL/min",     "mL/min"),
            ("ML_MIN_173","mL/min/1,73 m²", "mL/min/{1.73_m2}"),
            ("CFU_ML",    "UFC/mL",     "{CFU}/mL"),
            ("TITRE",     "titre",      "1"),
            ("NONE",      "",           "1")
        ];

        var existing = await context.Units.Select(u => u.Code).ToListAsync(ct);

        foreach (var (code, symbol, ucum) in units.Where(u => !existing.Contains(u.Code)))
            context.Units.Add(new MeasurementUnit(code, symbol) { UcumCode = ucum });

        await context.SaveChangesAsync(ct);
    }

    // ---- Specimen and container types ----------------------------------------------------------

    private static async Task SeedSpecimenAndContainerTypesAsync(LisDbContext context, CancellationToken ct)
    {
        (string Code, string Fr, string Ar, string En, int Fasting)[] specimens =
        [
            (SpecimenType.Codes.SerumDryTube,    "Sérum",                  "مصل الدم",          "Serum",              0),
            (SpecimenType.Codes.WholeBloodEdta,  "Sang total (EDTA)",      "دم كامل (EDTA)",    "Whole blood (EDTA)", 0),
            (SpecimenType.Codes.PlasmaCitrate,   "Plasma citraté",         "بلازما بالسيترات",  "Citrated plasma",    0),
            (SpecimenType.Codes.PlasmaHeparin,   "Plasma hépariné",        "بلازما بالهيبارين", "Heparin plasma",     0),
            (SpecimenType.Codes.PlasmaFluoride,  "Plasma fluoré",          "بلازما بالفلورايد", "Fluoride plasma",    8),
            (SpecimenType.Codes.Urine,           "Urines",                 "بول",               "Urine",              0),
            (SpecimenType.Codes.Urine24H,        "Urines de 24 heures",    "بول 24 ساعة",       "24-hour urine",      0),
            (SpecimenType.Codes.Stool,           "Selles",                 "براز",              "Stool",              0),
            (SpecimenType.Codes.Csf,             "Liquide céphalorachidien","السائل النخاعي",   "Cerebrospinal fluid",0),
            (SpecimenType.Codes.Sputum,          "Expectoration",          "بلغم",              "Sputum",             0),
            (SpecimenType.Codes.ThroatSwab,      "Frottis de gorge",       "مسحة الحلق",        "Throat swab",        0),
            (SpecimenType.Codes.VaginalSwab,     "Prélèvement vaginal",    "مسحة مهبلية",       "Vaginal swab",       0),
            (SpecimenType.Codes.UrethralSwab,    "Prélèvement urétral",    "مسحة إحليلية",      "Urethral swab",      0),
            (SpecimenType.Codes.WoundSwab,       "Prélèvement de plaie",   "مسحة جرح",          "Wound swab",         0),
            (SpecimenType.Codes.BloodCulture,    "Hémoculture",            "مزرعة دموية",       "Blood culture",      0),
            (SpecimenType.Codes.Tissue,          "Tissu",                  "نسيج",              "Tissue",             0)
        ];

        var existingSpecimens = await context.SpecimenTypes.Select(s => s.Code).ToListAsync(ct);

        foreach (var (code, fr, ar, en, fasting) in specimens.Where(s => !existingSpecimens.Contains(s.Code)))
        {
            context.SpecimenTypes.Add(new SpecimenType(code, new MultilingualText(fr, ar, en))
            {
                FastingHours = fasting
            });
        }

        // Cap colours follow the usual international convention, which is what the phlebotomist
        // actually recognises at the drawing chair. Draw order matters: additive carry-over between
        // tubes is a real pre-analytical error source.
        (string Code, string Fr, string Ar, string En, string Colour, string? Additive, decimal Volume, int Order)[] containers =
        [
            ("TUBE_SEC",     "Tube sec (activateur)", "أنبوب جاف",         "Plain tube",        "#E8B33A", "Silice",             5.0m, 4),
            ("TUBE_GEL",     "Tube gel séparateur",   "أنبوب بهلام فاصل",  "Gel separator",     "#D4A017", "Gel + silice",       5.0m, 5),
            ("TUBE_EDTA",    "Tube EDTA",             "أنبوب EDTA",        "EDTA tube",         "#8B1A8B", "EDTA K2",            3.0m, 6),
            ("TUBE_CITRATE", "Tube citraté",          "أنبوب السيترات",    "Citrate tube",      "#3A6EA5", "Citrate 3,2 %",      2.7m, 2),
            ("TUBE_HEPARINE","Tube héparine",         "أنبوب الهيبارين",   "Heparin tube",      "#3F8F3F", "Héparine de lithium",4.0m, 3),
            ("TUBE_FLUORURE","Tube fluoré",           "أنبوب الفلورايد",   "Fluoride tube",     "#7A7A7A", "Fluorure + oxalate", 2.0m, 7),
            ("TUBE_VS",      "Tube VS (citrate)",     "أنبوب سرعة الترسيب","ESR tube",          "#1A1A1A", "Citrate 1/4",        1.6m, 8),
            ("HEMOC_AERO",   "Flacon hémoculture aérobie","قنينة هوائية",  "Aerobic bottle",    "#3A6EA5", null,                10.0m, 1),
            ("HEMOC_ANAERO", "Flacon hémoculture anaérobie","قنينة لاهوائية","Anaerobic bottle","#D4531E", null,                10.0m, 1),
            ("POT_URINE",    "Pot à urines stérile",  "وعاء بول معقم",     "Sterile urine cup", "#FFFFFF", null,                60.0m, 9),
            ("POT_SELLES",   "Pot à selles",          "وعاء براز",         "Stool container",   "#FFFFFF", null,                30.0m, 9),
            ("ECOUVILLON",   "Écouvillon avec milieu de transport","ماسحة بوسط النقل","Swab with transport medium","#FFFFFF", null, 0m, 9)
        ];

        var existingContainers = await context.ContainerTypes.Select(c => c.Code).ToListAsync(ct);

        foreach (var c in containers.Where(c => !existingContainers.Contains(c.Code)))
        {
            context.ContainerTypes.Add(
                new ContainerType(c.Code, new MultilingualText(c.Fr, c.Ar, c.En), c.Colour)
                {
                    Additive = c.Additive is null ? null : new MultilingualText(c.Additive),
                    NominalVolumeMl = c.Volume == 0m ? null : c.Volume,
                    MinimumVolumeMl = c.Volume == 0m ? null : Math.Round(c.Volume * 0.5m, 2),
                    DrawOrder = c.Order
                });
        }

        await context.SaveChangesAsync(ct);
    }

    // ---- Payers --------------------------------------------------------------------------------

    /// <summary>
    /// The payers a new installation starts with.
    /// <para>
    /// Public, and separated from the code that writes them, so a test can assert that the shipped
    /// rows satisfy <c>Payer.Validate</c> — the same reason <see cref="RolePermissionSets.Build"/>
    /// is reachable. Reference data that violates its own invariants is a defect that only shows
    /// up the first time an administrator opens one of these records and tries to save it, which
    /// is a long way from here.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Payer> BuildSeedPayers()
    {
        // Default reimbursement rates reflect the common AMO position for biology acts. They are
        // starting points an administrator confirms against the current convention, not law —
        // which is what the payer maintenance screen is for, and what its notice says.
        (string Code, string Fr, string Ar, string En, PayerKind Kind, decimal Rate, bool Tnr, bool ThirdParty)[] payers =
        [
            // AMO is kept untranslated in all three languages: it is the statutory scheme's name,
            // the word printed on the member's card, and the word the payer uses on a rejection.
            ("DIRECT",   "Paiement direct",  "أداء مباشر",                                      "Direct payment",               PayerKind.SelfPay,      0.00m, false, false),
            ("CNSS",     "AMO — CNSS",       "التأمين الإجباري — الصندوق الوطني للضمان الاجتماعي", "AMO — CNSS",                   PayerKind.AmoCnss,      0.70m, true,  true),
            ("CNOPS",    "AMO — CNOPS",      "التأمين الإجباري — CNOPS",                        "AMO — CNOPS",                  PayerKind.AmoCnops,     0.70m, true,  true),
            ("TADAMON",  "AMO Tadamon",      "أمو تضامن",                                        "AMO Tadamon",                  PayerKind.AmoTadamon,   1.00m, true,  true),
            ("ACHAMIL",  "AMO Achamil",      "أمو الشامل",                                       "AMO Achamil",                  PayerKind.AmoAchamil,   0.70m, true,  true),
            ("FAR",      "Forces armées royales", "القوات المسلحة الملكية",                      "Royal Armed Forces",           PayerKind.ArmedForces,  0.80m, true,  true),
            ("MUTUELLE", "Mutuelle (à préciser)", "تعاضدية (تحدد)",                              "Mutual fund (to be specified)",PayerKind.Mutuelle,     0.20m, false, true),
            ("ASSURANCE","Assurance privée (à préciser)", "تأمين خاص (يحدد)",                    "Private insurer (to be specified)", PayerKind.PrivateInsurer, 0.80m, false, true),
            ("EMPLOYEUR","Employeur conventionné", "مشغل متعاقد",                                "Contracted employer",          PayerKind.Employer,     1.00m, false, true),
            ("LABO_TIERS","Laboratoire adresseur", "مختبر مُحيل",                                "Referring laboratory",         PayerKind.ReferringInstitution, 1.00m, false, true)
        ];

        return
        [
            .. payers.Select(p => new Payer(p.Code, new MultilingualText(p.Fr, p.Ar, p.En), p.Kind)
            {
                DefaultReimbursementRate = p.Rate,
                UsesNationalReferenceTariff = p.Tnr,
                ThirdPartyPaymentAgreement = p.ThirdParty,
                RequiresPrescription = p.Kind != PayerKind.SelfPay,
                RequiresInpeOnClaim = p.Kind is PayerKind.AmoCnss or PayerKind.AmoCnops
                                              or PayerKind.AmoTadamon or PayerKind.AmoAchamil,
                PaymentTermsDays = p.Kind == PayerKind.SelfPay ? 0 : 60
            })
        ];
    }

    private static async Task SeedPayersAsync(LisDbContext context, CancellationToken ct)
    {
        var existing = await context.Payers.IgnoreQueryFilters().Select(p => p.Code).ToListAsync(ct);

        foreach (var payer in BuildSeedPayers().Where(p => !existing.Contains(p.Code)))
            context.Payers.Add(payer);

        await context.SaveChangesAsync(ct);
    }

    // ---- Roles ---------------------------------------------------------------------------------

    private static async Task SeedRolesAsync(LisDbContext context, CancellationToken ct)
    {
        var definitions = RolePermissionSets.Build();

        var existing = await context.Roles
            .Include(r => r.Permissions)
            .ToDictionaryAsync(r => r.Code, ct);

        foreach (var (code, name, permissions) in definitions)
        {
            if (existing.TryGetValue(code, out var role))
            {
                // The prescriber role used to carry reports.print, which is the permission that
                // releases a new report version — an outside doctor could issue the laboratory's
                // reports. Withdrawn once, on the built-in role only, in exchange for reprinting
                // an already released PDF; must run before ApplyShippedPermissions records the
                // replacement as offered.
                if (code == SystemRoles.Prescriber && role.IsBuiltIn)
                    role.ReplaceShippedPermissionOnce(Permissions.Reports.Print,
                                                      Permissions.Reports.ReprintValidated);

                // Grant only the permissions a new version introduced (never offered before), so
                // nothing an administrator deliberately added or removed is undone on restart.
                role.ApplyShippedPermissions(permissions);
                continue;
            }

            var created = new Role(code, name, isBuiltIn: true);
            created.SeedShippedPermissions(permissions);
            context.Roles.Add(created);
        }

        await context.SaveChangesAsync(ct);
    }

    // ---- Default site --------------------------------------------------------------------------

    private static async Task<LabSite> SeedDefaultSiteAsync(LisDbContext context, CancellationToken ct)
    {
        var site = await context.Sites.IgnoreQueryFilters().FirstOrDefaultAsync(ct);
        if (site is not null) return site;

        // A minimal placeholder: the application refuses to invoice or release reports until an
        // administrator has completed the legal identifiers, which ValidateForInvoicing() checks.
        site = new LabSite("PRINCIPAL",
                           new MultilingualText("Laboratoire principal", "المختبر الرئيسي",
                                                "Main laboratory"),
                           LabSiteKind.MainLaboratory)
        {
            LegalName = "À COMPLÉTER",
            City = "À COMPLÉTER",
            AddressLine = new MultilingualText("À compléter", "يُستكمل", "To be completed")
        };

        context.Sites.Add(site);
        await context.SaveChangesAsync(ct);
        return site;
    }

    // ---- Number sequences ----------------------------------------------------------------------

    private static async Task SeedSequencesAsync(LisDbContext context, Guid siteId, IClock clock,
                                                 CancellationToken ct)
    {
        // The allocator creates a missing sequence on demand; these are pre-created so an
        // administrator can adjust the number format before the first document is issued rather
        // than discovering the default on an invoice that can no longer be changed.
        (SequencePurpose Purpose, Guid? Site, int? Year, string Format)[] sequences =
        [
            (SequencePurpose.MedicalRecord,     null,   null,            "P-{SEQ:8}"),
            (SequencePurpose.SampleBarcode,     null,   null,            "{SEQ:10}"),
            (SequencePurpose.Accession,         siteId, clock.Today.Year, "{YY}{MM}-{SEQ:6}"),
            (SequencePurpose.Invoice,           siteId, clock.Today.Year, "FAC/{SITE}/{YEAR}/{SEQ:6}"),
            (SequencePurpose.CreditNote,        siteId, clock.Today.Year, "AV/{SITE}/{YEAR}/{SEQ:6}"),
            (SequencePurpose.Receipt,           siteId, clock.Today.Year, "REC/{SITE}/{YEAR}/{SEQ:6}"),
            (SequencePurpose.NonConformity,     null,   clock.Today.Year, "NC-{YEAR}-{SEQ:4}"),
            (SequencePurpose.PurchaseOrder,     null,   clock.Today.Year, "BC/{YEAR}/{SEQ:5}"),
            (SequencePurpose.ControlledDocument,null,   null,            "DOC-{SEQ:5}")
        ];

        foreach (var s in sequences)
        {
            var exists = await context.NumberSequences.AnyAsync(
                n => n.Purpose == s.Purpose && n.SiteId == s.Site && n.Year == s.Year, ct);

            if (!exists)
                context.NumberSequences.Add(new NumberSequence(s.Purpose, s.Site, s.Year, s.Format));
        }

        await context.SaveChangesAsync(ct);
    }

    // ---- First administrator -------------------------------------------------------------------

    private static async Task SeedAdministratorAsync(LisDbContext context, IPasswordHasher hasher,
                                                     IClock clock, Guid siteId, CancellationToken ct)
    {
        if (await context.Users.IgnoreQueryFilters().AnyAsync(ct)) return;

        var adminRole = await context.Roles.FirstAsync(r => r.Code == SystemRoles.Administrator, ct);

        // A known first credential is unavoidable, so it is made harmless: the account must change
        // its password before it can do anything, and the fact that it did is in the security log.
        const string initialPassword = "Maroc-LIS-2026!";

        var admin = new AppUser("admin", "Administrateur système", hasher.Hash(initialPassword))
        {
            MustChangePassword = true,
            Qualification = ProfessionalQualification.Administrative,
            PreferredLanguage = AppLanguage.French
        };

        admin.AssignRole(adminRole.Id);
        admin.GrantSiteAccess(siteId, isDefault: true);

        context.Users.Add(admin);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates the non-interactive account that analyser results are attributed to.
    /// <para>
    /// A machine write needs an accountable identity for the same reason a manual one does, and the
    /// honest answer is not a person: attributing an analyser's value to whichever technician
    /// happened to be signed in puts their name against a measurement they never saw. So the
    /// interfaces write as this account, with the from-instrument flag set on every value.
    /// </para>
    /// <para>
    /// It holds no role and therefore no permission, and its password hash is random bytes nobody
    /// has — it cannot sign in and cannot do anything a screen offers. It is deliberately visible in
    /// the user list rather than hidden: an account that writes patient results and does not appear
    /// in the administration screen is exactly what an inspector objects to.
    /// </para>
    /// </summary>
    private static async Task SeedInterfaceAccountAsync(LisDbContext context, IPasswordHasher hasher,
                                                         Guid siteId, CancellationToken ct)
    {
        const string userName = "interface";

        if (await context.Users.IgnoreQueryFilters().AnyAsync(u => u.UserName == userName, ct))
            return;

        var unusable = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator
                                                             .GetBytes(32));

        var account = new AppUser(userName, "Interface automates (compte de service)",
                                   hasher.Hash(unusable))
        {
            Qualification = ProfessionalQualification.None,
            PreferredLanguage = AppLanguage.French
        };

        account.GrantSiteAccess(siteId, isDefault: true);

        context.Users.Add(account);
        await context.SaveChangesAsync(ct);
    }

    // ---- Starter analysis catalogue -------------------------------------------------------------

    private static async Task SeedStarterCatalogueAsync(LisDbContext context, IClock clock,
                                                        CancellationToken ct)
    {
        if (await context.AnalysisTests.IgnoreQueryFilters().AnyAsync(ct)) return;

        await StarterCatalogue.SeedAsync(context, clock, ct);
    }
}

/// <summary>
/// The permission set each shipped role carries. Held as code rather than data so that a new
/// permission introduced by a release reaches the roles that should have it, and so the intent
/// behind each role is reviewable in one place.
/// </summary>
internal static class RolePermissionSets
{
    internal static IReadOnlyList<(string Code, MultilingualText Name, string[] Permissions)> Build() =>
    [
        (SystemRoles.Administrator,
         new MultilingualText("Administrateur", "مدير النظام", "Administrator"),
         // Everything, including the audit trail. Deliberately not given a clinical qualification:
         // an administrator can configure the system but cannot validate a result, because
         // CanBiologicallyValidate() checks the qualification and not the permission.
         [.. Permissions.All]),

        (SystemRoles.ResponsibleBiologist,
         new MultilingualText("Biologiste responsable", "البيولوجي المسؤول", "Responsible biologist"),
         [
             Permissions.Patients.View, Permissions.Patients.Create, Permissions.Patients.Edit,
             Permissions.Patients.Merge, Permissions.Patients.Export,
             Permissions.Orders.View, Permissions.Orders.Create, Permissions.Orders.Edit,
             Permissions.Orders.Cancel, Permissions.Orders.AddUrgent,
             Permissions.Samples.View, Permissions.Samples.Receive, Permissions.Samples.Reject,
             Permissions.Samples.Collect, Permissions.Samples.Reprint, Permissions.Samples.Dispatch,
             Permissions.Results.View, Permissions.Results.Enter, Permissions.Results.Modify,
             Permissions.Results.TechnicalValidate, Permissions.Results.BiologicalValidate,
             Permissions.Results.Unvalidate, Permissions.Results.EnterCritical,
             Permissions.Reports.View, Permissions.Reports.Print, Permissions.Reports.Send,
             Permissions.Reports.Amend, Permissions.Reports.ReprintValidated,
             Permissions.Quality.View, Permissions.Quality.EnterQc, Permissions.Quality.ReviewQc,
             Permissions.Quality.ManageQcRules, Permissions.Quality.ManageNonConformity,
             Permissions.Quality.ApproveDocument, Permissions.Quality.ManageDocuments,
             Permissions.Catalog.View, Permissions.Catalog.Manage, Permissions.Catalog.ManageReferenceRanges,
             Permissions.Equipment.View, Permissions.Equipment.Manage,
             Permissions.Equipment.RecordMaintenance, Permissions.Equipment.Calibrate,
             Permissions.Instruments.View, Permissions.Instruments.Configure,
             Permissions.Instruments.StartStop, Permissions.Instruments.ReplayMessages,
             Permissions.Inventory.View, Permissions.Inventory.Receive, Permissions.Inventory.Issue,
             Permissions.Inventory.ApprovePurchaseOrders,
             Permissions.Billing.View,
             Permissions.Statistics.View, Permissions.Statistics.EpidemiologicalDeclaration,
             Permissions.Administration.ViewUsers, Permissions.Administration.ViewAuditTrail,
             Permissions.HumanResources.View, Permissions.HumanResources.Manage,
             Permissions.HumanResources.ManageHabilitations, Permissions.HumanResources.Salary,
             Permissions.HumanResources.RequestLeave, Permissions.HumanResources.ApproveLeave,
             Permissions.HumanResources.ManagePlanning, Permissions.HumanResources.Accidents
         ]),

        (SystemRoles.Biologist,
         new MultilingualText("Biologiste", "بيولوجي", "Biologist"),
         [
             Permissions.Patients.View, Permissions.Patients.Edit,
             Permissions.Orders.View, Permissions.Orders.Create, Permissions.Orders.Edit,
             Permissions.Samples.View, Permissions.Samples.Receive, Permissions.Samples.Reject,
             Permissions.Results.View, Permissions.Results.Enter, Permissions.Results.Modify,
             Permissions.Results.TechnicalValidate, Permissions.Results.BiologicalValidate,
             Permissions.Results.EnterCritical,
             Permissions.Reports.View, Permissions.Reports.Print, Permissions.Reports.Send,
             Permissions.Reports.Amend,
             Permissions.Quality.View, Permissions.Quality.EnterQc, Permissions.Quality.ReviewQc,
             Permissions.Quality.ManageNonConformity,
             Permissions.Catalog.View,
             Permissions.Equipment.View, Permissions.Instruments.View,
             Permissions.Inventory.View, Permissions.Statistics.View,
             Permissions.HumanResources.RequestLeave,
             // A blood exposure is declared by the biologist on duty, whoever it is.
             Permissions.HumanResources.Accidents
         ]),

        (SystemRoles.Technician,
         new MultilingualText("Technicien de laboratoire", "تقني مختبر", "Laboratory technician"),
         [
             Permissions.Patients.View,
             Permissions.Orders.View,
             Permissions.Samples.View, Permissions.Samples.Collect, Permissions.Samples.Receive,
             Permissions.Samples.Reject, Permissions.Samples.Reprint,
             // Prepares the send-outs to partner laboratories.
             Permissions.Samples.Dispatch,
             // Enter and technically validate, but never release: biological validation is
             // reserved to a biologist by loi n° 12-01, and the role does not carry the grant.
             Permissions.Results.View, Permissions.Results.Enter,
             Permissions.Results.TechnicalValidate,
             Permissions.Reports.View,
             Permissions.Quality.View, Permissions.Quality.EnterQc,
             Permissions.Catalog.View,
             Permissions.Equipment.View, Permissions.Equipment.RecordMaintenance,
             Permissions.Instruments.View, Permissions.Instruments.StartStop,
             Permissions.Inventory.View, Permissions.Inventory.Issue,
             Permissions.HumanResources.RequestLeave
         ]),

        (SystemRoles.Phlebotomist,
         new MultilingualText("Préleveur", "آخذ العينات", "Phlebotomist"),
         [
             Permissions.Patients.View, Permissions.Patients.Create,
             Permissions.Orders.View,
             Permissions.Samples.View, Permissions.Samples.Collect, Permissions.Samples.Reprint,
             Permissions.Samples.Dispatch,
             Permissions.Catalog.View,
             Permissions.HumanResources.RequestLeave
         ]),

        (SystemRoles.Receptionist,
         new MultilingualText("Secrétariat / accueil", "الكتابة والاستقبال", "Reception"),
         [
             Permissions.Patients.View, Permissions.Patients.Create, Permissions.Patients.Edit,
             Permissions.Orders.View, Permissions.Orders.Create, Permissions.Orders.Edit,
             Permissions.Orders.Cancel, Permissions.Orders.AddUrgent,
             Permissions.Samples.View, Permissions.Samples.Reprint,
             // Reception hands over reports but never sees an unvalidated result.
             Permissions.Reports.View, Permissions.Reports.Print, Permissions.Reports.Send,
             Permissions.Billing.View, Permissions.Billing.CreateInvoice,
             Permissions.Billing.RecordPayment, Permissions.Billing.CloseCashSession,
             Permissions.Catalog.View,
             Permissions.HumanResources.RequestLeave
         ]),

        (SystemRoles.QualityManager,
         new MultilingualText("Responsable qualité", "مسؤول الجودة", "Quality manager"),
         [
             Permissions.Patients.View, Permissions.Orders.View,
             Permissions.Samples.View, Permissions.Results.View, Permissions.Reports.View,
             Permissions.Quality.View, Permissions.Quality.EnterQc, Permissions.Quality.ReviewQc,
             Permissions.Quality.ManageQcRules, Permissions.Quality.ManageNonConformity,
             Permissions.Quality.ApproveDocument, Permissions.Quality.ManageDocuments,
             Permissions.Catalog.View, Permissions.Catalog.ManageReferenceRanges,
             Permissions.Equipment.View, Permissions.Equipment.Manage,
             Permissions.Equipment.RecordMaintenance, Permissions.Equipment.Calibrate,
             Permissions.Instruments.View,
             Permissions.Inventory.View, Permissions.Inventory.Receive, Permissions.Inventory.Adjust,
             Permissions.Inventory.ApprovePurchaseOrders,
             Permissions.Statistics.View,
             Permissions.Administration.ViewUsers, Permissions.Administration.ViewAuditTrail,
             // The habilitation matrix is the quality manager's audit evidence; granting stays
             // with the biologists.
             Permissions.HumanResources.View, Permissions.HumanResources.RequestLeave
         ]),

        (SystemRoles.Accountant,
         new MultilingualText("Comptabilité", "المحاسبة", "Accounting"),
         [
             // No clinical result access: billing needs the act and its price, not the value.
             Permissions.Patients.View,
             Permissions.Orders.View,
             Permissions.Billing.View, Permissions.Billing.CreateInvoice,
             Permissions.Billing.CreditNote, Permissions.Billing.RecordPayment,
             Permissions.Billing.ApplyDiscount, Permissions.Billing.ManageTariffs,
             Permissions.Billing.CloseCashSession, Permissions.Billing.ExportAccounting,
             Permissions.Inventory.View, Permissions.Inventory.ManagePurchaseOrders,
             Permissions.Statistics.View,
             Permissions.HumanResources.RequestLeave
         ]),

        (SystemRoles.Prescriber,
         new MultilingualText("Prescripteur", "طبيب واصف", "Prescriber"),
         [
             // Read-only consultation of validated reports for their own patients. Scoping to
             // "their own" is not a permission: an account linked to a prescriber is restricted
             // to that prescriber's orders by the data layer's query filters (PrescriberScope).
             // Never reports.print, which releases a new report version; reprinting the PDF
             // already released is the consultation a prescriber needs.
             Permissions.Patients.View, Permissions.Orders.View,
             Permissions.Results.View, Permissions.Reports.View, Permissions.Reports.ReprintValidated
         ])
    ];
}
