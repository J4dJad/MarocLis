using System.Globalization;
using System.Text;
using MarocLIS.Domain.Migration;

namespace MarocLIS.Application.Migration;

/// <summary>How a MarocLIS field reads its source text.</summary>
public enum MigrationFieldType
{
    Text = 0,
    Date = 1,
    Number = 2,
    WholeNumber = 3,

    /// <summary>A value looked up in a correspondence table (« Correspondances »): sex, payer, analysis…</summary>
    Coded = 4
}

/// <summary>The lists a coded field's source values are matched against.</summary>
public enum MigrationCodeList
{
    None = 0,
    Sex = 1,
    Payer = 2,
    Analysis = 3,
    Unit = 4,
    Specialty = 5,
    Department = 6,
    Specimen = 7
}

/// <summary>
/// One MarocLIS field a source column can be mapped to.
/// <para>
/// <see cref="Header"/> is the French column header of the downloadable template, and the first
/// name the automatic matching tries; <see cref="Synonyms"/> are the names other applications are
/// known to use (« NOM_PAT », « lastname »…). The screen shows the field by its resource key,
/// <c>Migration.Field.{Key}</c>, in the operator's language.
/// </para>
/// </summary>
public sealed record MigrationField(
    string Key,
    string Header,
    MigrationFieldType Type,
    bool Required = false,
    int MaxLength = 0,
    MigrationCodeList CodeList = MigrationCodeList.None,
    params string[] Synonyms)
{
    public string LabelKey => $"Migration.Field.{Key}";
}

/// <summary>The fields of each kind of data, in the column order of its template.</summary>
public static class MigrationSchema
{
    // Field keys, shared between kinds where the meaning is the same.
    public const string SourceId = "SourceId";
    public const string OldNumber = "OldNumber";
    public const string LastName = "LastName";
    public const string FirstName = "FirstName";
    public const string LastNameArabic = "LastNameArabic";
    public const string FirstNameArabic = "FirstNameArabic";
    public const string BirthDate = "BirthDate";
    public const string Age = "Age";
    public const string Sex = "Sex";
    public const string Cin = "Cin";
    public const string Mobile = "Mobile";
    public const string Phone = "Phone";
    public const string Email = "Email";
    public const string Address = "Address";
    public const string City = "City";
    public const string Guardian = "Guardian";
    public const string PayerCode = "PayerCode";
    public const string MembershipNumber = "MembershipNumber";
    public const string Specialty = "Specialty";
    public const string Inpe = "Inpe";
    public const string OrderNumber = "OrderNumber";
    public const string Institution = "Institution";
    public const string AnalysisCode = "AnalysisCode";
    public const string AnalysisName = "AnalysisName";
    public const string Price = "Price";
    public const string LetterKey = "LetterKey";
    public const string Coefficient = "Coefficient";
    public const string ActCode = "ActCode";
    public const string Department = "Department";
    public const string Specimen = "Specimen";
    public const string Unit = "Unit";
    public const string PatientSourceId = "PatientSourceId";
    public const string PatientOldNumber = "PatientOldNumber";
    public const string PatientCin = "PatientCin";
    public const string ResultDate = "ResultDate";
    public const string Value = "Value";
    public const string ReferenceRange = "ReferenceRange";
    public const string OldAccession = "OldAccession";
    public const string Amount = "Amount";
    public const string AsOf = "AsOf";
    public const string OldInvoiceNumber = "OldInvoiceNumber";
    public const string OldInvoiceDate = "OldInvoiceDate";
    public const string Label = "Label";

    public static IReadOnlyList<DataMigrationKind> Kinds { get; } =
    [
        DataMigrationKind.Patients, DataMigrationKind.Prescribers, DataMigrationKind.Catalogue,
        DataMigrationKind.History, DataMigrationKind.OpeningBalances
    ];

    private static readonly MigrationField[] PatientFields =
    [
        new(SourceId, "Ancien identifiant", MigrationFieldType.Text, MaxLength: 100,
            Synonyms: ["id", "id_patient", "idpatient", "code_patient", "codepatient", "num_patient", "patient_id", "ref", "reference"]),
        new(OldNumber, "Ancien n° de dossier", MigrationFieldType.Text, MaxLength: 60,
            Synonyms: ["n dossier", "no dossier", "num_dossier", "numdossier", "dossier", "ndossier", "mrn", "ipp", "matricule"]),
        new(LastName, "Nom", MigrationFieldType.Text, Required: true, MaxLength: 120,
            Synonyms: ["nom", "nom_pat", "nompat", "nom_patient", "lastname", "last_name", "surname", "name", "family_name"]),
        new(FirstName, "Prénom", MigrationFieldType.Text, Required: true, MaxLength: 120,
            Synonyms: ["prenom", "prenom_pat", "prenompat", "firstname", "first_name", "given_name", "forename"]),
        new(LastNameArabic, "Nom (arabe)", MigrationFieldType.Text, MaxLength: 120,
            Synonyms: ["nom_ar", "nomar", "nom_arabe", "lastname_ar"]),
        new(FirstNameArabic, "Prénom (arabe)", MigrationFieldType.Text, MaxLength: 120,
            Synonyms: ["prenom_ar", "prenomar", "prenom_arabe", "firstname_ar"]),
        new(BirthDate, "Date de naissance", MigrationFieldType.Date,
            Synonyms: ["date_naissance", "datenaissance", "date_naiss", "datenaiss", "naissance", "ddn", "dob", "birthdate", "birth_date", "date_of_birth"]),
        new(Age, "Âge (ans)", MigrationFieldType.WholeNumber,
            Synonyms: ["age", "age_ans"]),
        new(Sex, "Sexe", MigrationFieldType.Coded, CodeList: MigrationCodeList.Sex,
            Synonyms: ["sexe", "sex", "genre", "gender", "civilite"]),
        new(Cin, "CIN", MigrationFieldType.Text, MaxLength: 20,
            Synonyms: ["cin", "cni", "carte_identite", "num_cin", "national_id"]),
        new(Mobile, "Téléphone mobile", MigrationFieldType.Text, MaxLength: 20,
            Synonyms: ["gsm", "portable", "mobile", "tel_mobile", "telephone_mobile", "cellulaire", "tel", "telephone"]),
        new(Phone, "Autre téléphone", MigrationFieldType.Text, MaxLength: 20,
            Synonyms: ["tel2", "telephone2", "tel_fixe", "fixe", "telephone_fixe", "phone"]),
        new(Email, "E-mail", MigrationFieldType.Text, MaxLength: 200,
            Synonyms: ["email", "mail", "courriel", "e_mail"]),
        new(Address, "Adresse", MigrationFieldType.Text, MaxLength: 400,
            Synonyms: ["adresse", "address", "adr"]),
        new(City, "Ville", MigrationFieldType.Text, MaxLength: 100,
            Synonyms: ["ville", "city", "localite"]),
        new(Guardian, "Tuteur (nom)", MigrationFieldType.Text, MaxLength: 200,
            Synonyms: ["tuteur", "responsable", "parent", "guardian"]),
        new(PayerCode, "Organisme (code)", MigrationFieldType.Coded, CodeList: MigrationCodeList.Payer,
            Synonyms: ["organisme", "code_organisme", "mutuelle", "assurance", "payeur", "caisse", "couverture", "amo", "payer"]),
        new(MembershipNumber, "N° d'immatriculation", MigrationFieldType.Text, MaxLength: 40,
            Synonyms: ["immatriculation", "num_immatriculation", "matricule_amo", "n_affiliation", "affiliation", "num_adherent", "adherent", "membership", "num_assure"])
    ];

    private static readonly MigrationField[] PrescriberFields =
    [
        new(SourceId, "Ancien identifiant", MigrationFieldType.Text, MaxLength: 100,
            Synonyms: ["id", "id_medecin", "idmedecin", "code_medecin", "codemedecin", "id_prescripteur", "code"]),
        new(LastName, "Nom", MigrationFieldType.Text, MaxLength: 120,
            Synonyms: ["nom", "nom_med", "nommed", "nom_medecin", "lastname", "last_name", "name"]),
        new(FirstName, "Prénom", MigrationFieldType.Text, MaxLength: 120,
            Synonyms: ["prenom", "prenom_med", "firstname", "first_name"]),
        new(Specialty, "Spécialité", MigrationFieldType.Coded, MaxLength: 150, CodeList: MigrationCodeList.Specialty,
            Synonyms: ["specialite", "specialty", "speciality", "type", "qualite"]),
        new(Inpe, "INPE", MigrationFieldType.Text, MaxLength: 20,
            Synonyms: ["inpe", "num_inpe"]),
        new(OrderNumber, "N° d'ordre", MigrationFieldType.Text, MaxLength: 40,
            Synonyms: ["num_ordre", "n_ordre", "ordre", "cnom"]),
        new(Phone, "Téléphone", MigrationFieldType.Text, MaxLength: 30,
            Synonyms: ["tel", "telephone", "phone", "tel_cabinet"]),
        new(Mobile, "Téléphone mobile", MigrationFieldType.Text, MaxLength: 20,
            Synonyms: ["gsm", "portable", "mobile"]),
        new(Email, "E-mail", MigrationFieldType.Text, MaxLength: 200,
            Synonyms: ["email", "mail", "courriel"]),
        new(Address, "Adresse", MigrationFieldType.Text, MaxLength: 400,
            Synonyms: ["adresse", "address", "adresse_cabinet"]),
        new(City, "Ville", MigrationFieldType.Text, MaxLength: 100,
            Synonyms: ["ville", "city"]),
        new(Institution, "Établissement", MigrationFieldType.Text, MaxLength: 200,
            Synonyms: ["etablissement", "clinique", "hopital", "institution", "structure"])
    ];

    private static readonly MigrationField[] CatalogueFields =
    [
        new(AnalysisCode, "Code analyse", MigrationFieldType.Text, Required: true, MaxLength: 30,
            Synonyms: ["code", "code_analyse", "codeanalyse", "code_examen", "mnemonique", "abrege", "test_code"]),
        new(AnalysisName, "Libellé", MigrationFieldType.Text, MaxLength: 200,
            Synonyms: ["libelle", "nom", "designation", "nom_analyse", "intitule", "analyse", "examen", "name"]),
        new(Price, "Prix (MAD)", MigrationFieldType.Number,
            Synonyms: ["prix", "tarif", "montant", "prix_dh", "prix_mad", "price"]),
        new(LetterKey, "Lettre clé", MigrationFieldType.Text, MaxLength: 5,
            Synonyms: ["lettre", "lettre_cle", "cle"]),
        new(Coefficient, "Coefficient B", MigrationFieldType.Number,
            Synonyms: ["coefficient", "coef", "cotation", "nombre_b", "nb_b", "b"]),
        new(ActCode, "Code acte NABM", MigrationFieldType.Text, MaxLength: 10,
            Synonyms: ["nabm", "code_nabm", "code_acte", "acte", "nomenclature", "code_ngap"]),
        new(Department, "Section", MigrationFieldType.Coded, CodeList: MigrationCodeList.Department,
            Synonyms: ["section", "service", "departement", "famille", "chapitre", "discipline"]),
        new(Specimen, "Type d'échantillon", MigrationFieldType.Coded, CodeList: MigrationCodeList.Specimen,
            Synonyms: ["echantillon", "prelevement", "nature", "type_echantillon", "specimen", "milieu"]),
        new(Unit, "Unité", MigrationFieldType.Coded, CodeList: MigrationCodeList.Unit,
            Synonyms: ["unite", "unit", "unites"])
    ];

    private static readonly MigrationField[] HistoryFields =
    [
        new(SourceId, "Ancien identifiant", MigrationFieldType.Text, MaxLength: 100,
            Synonyms: ["id", "id_resultat", "idresultat", "id_result"]),
        new(PatientSourceId, "Ancien identifiant patient", MigrationFieldType.Text, MaxLength: 100,
            Synonyms: ["id_patient", "idpatient", "code_patient", "codepatient", "patient_id", "num_patient"]),
        new(PatientOldNumber, "Ancien n° de dossier patient", MigrationFieldType.Text, MaxLength: 60,
            Synonyms: ["dossier_patient", "num_dossier_patient", "ipp", "mrn"]),
        new(PatientCin, "CIN patient", MigrationFieldType.Text, MaxLength: 20,
            Synonyms: ["cin", "cin_patient"]),
        new(ResultDate, "Date du résultat", MigrationFieldType.Date, Required: true,
            Synonyms: ["date", "date_resultat", "date_validation", "date_dossier", "date_prelevement", "date_analyse"]),
        new(AnalysisCode, "Code analyse", MigrationFieldType.Coded, Required: true, CodeList: MigrationCodeList.Analysis,
            Synonyms: ["code", "code_analyse", "codeanalyse", "code_examen", "code_parametre", "mnemonique", "test_code", "analyse"]),
        new(Value, "Résultat", MigrationFieldType.Text, Required: true, MaxLength: HistoricalResult.ValueMaxLength,
            Synonyms: ["resultat", "valeur", "value", "result", "res"]),
        new(Unit, "Unité", MigrationFieldType.Coded, CodeList: MigrationCodeList.Unit,
            Synonyms: ["unite", "unit", "unites"]),
        new(ReferenceRange, "Valeurs de référence", MigrationFieldType.Text, MaxLength: HistoricalResult.RangeMaxLength,
            Synonyms: ["normes", "valeurs_normales", "vn", "reference", "valeurs_reference", "intervalle", "normales", "range"]),
        new(OldAccession, "N° dossier ancien", MigrationFieldType.Text, MaxLength: HistoricalResult.AccessionMaxLength,
            Synonyms: ["num_dossier", "n_dossier", "dossier", "numdossier", "accession", "num_demande", "demande"])
    ];

    private static readonly MigrationField[] BalanceFields =
    [
        new(SourceId, "Ancien identifiant", MigrationFieldType.Text, MaxLength: 100,
            Synonyms: ["id", "id_facture", "idfacture", "id_creance"]),
        new(PatientSourceId, "Ancien identifiant patient", MigrationFieldType.Text, MaxLength: 100,
            Synonyms: ["id_patient", "idpatient", "code_patient", "patient_id"]),
        new(PatientOldNumber, "Ancien n° de dossier patient", MigrationFieldType.Text, MaxLength: 60,
            Synonyms: ["dossier_patient", "ipp", "mrn"]),
        new(PatientCin, "CIN patient", MigrationFieldType.Text, MaxLength: 20,
            Synonyms: ["cin", "cin_patient"]),
        new(PayerCode, "Organisme (code)", MigrationFieldType.Coded, CodeList: MigrationCodeList.Payer,
            Synonyms: ["organisme", "code_organisme", "mutuelle", "payeur", "caisse", "tiers", "payer"]),
        new(Amount, "Montant dû (MAD)", MigrationFieldType.Number, Required: true,
            Synonyms: ["montant", "reste", "reste_du", "solde", "du", "montant_du", "balance", "amount", "restant"]),
        new(AsOf, "Date du solde", MigrationFieldType.Date,
            Synonyms: ["date_solde", "date_arrete", "arrete", "date"]),
        new(OldInvoiceNumber, "N° facture ancienne", MigrationFieldType.Text, MaxLength: OpeningBalance.ReferenceMaxLength,
            Synonyms: ["num_facture", "n_facture", "facture", "numfacture", "invoice", "invoice_number"]),
        new(OldInvoiceDate, "Date facture", MigrationFieldType.Date,
            Synonyms: ["date_facture", "datefacture", "invoice_date"]),
        new(Label, "Libellé", MigrationFieldType.Text, MaxLength: OpeningBalance.LabelMaxLength,
            Synonyms: ["libelle", "observation", "commentaire", "note", "designation"])
    ];

    public static IReadOnlyList<MigrationField> Fields(DataMigrationKind kind) => kind switch
    {
        DataMigrationKind.Patients => PatientFields,
        DataMigrationKind.Prescribers => PrescriberFields,
        DataMigrationKind.Catalogue => CatalogueFields,
        DataMigrationKind.History => HistoryFields,
        DataMigrationKind.OpeningBalances => BalanceFields,
        _ => []
    };

    public static MigrationField? Field(DataMigrationKind kind, string key) =>
        Fields(kind).FirstOrDefault(f => f.Key == key);

    /// <summary>
    /// The form names are compared in: upper case, accents and every separator gone
    /// (« Date de naissance », « DATE_NAISS » and « date-naissance » differ only by what this drops).
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }
}
