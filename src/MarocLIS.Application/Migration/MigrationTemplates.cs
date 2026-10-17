using System.Text;
using MarocLIS.Domain.Migration;

namespace MarocLIS.Application.Migration;

/// <summary>
/// The « Télécharger le modèle » files: one per kind, the French headers the automatic matching
/// recognises, and one example row the laboratory replaces with its own data.
/// </summary>
public static class MigrationTemplates
{
    public static IReadOnlyList<string> Headers(DataMigrationKind kind) =>
        [.. MigrationSchema.Fields(kind).Select(f => f.Header)];

    /// <summary>One example row, in the template's column order.</summary>
    public static IReadOnlyList<string> Example(DataMigrationKind kind) => kind switch
    {
        DataMigrationKind.Patients =>
        [
            "P000123", "2019/00456", "ALAOUI", "Salma", "العلوي", "سلمى", "05/03/1971", "", "F", "AB123456",
            "0661234567", "0522123456", "salma.alaoui@exemple.ma", "12, rue des Orangers", "Casablanca", "",
            "CNSS", "123456789"
        ],
        DataMigrationKind.Prescribers =>
        [
            "M042", "BENNANI", "Karim", "Médecin généraliste", "12345678", "CNOM-1234", "0522000000",
            "0661000000", "dr.bennani@exemple.ma", "45, boulevard Zerktouni", "Casablanca", ""
        ],
        DataMigrationKind.Catalogue =>
        [
            "GLY", "Glycémie à jeun", "30,00", "B", "30", "B118", "BIOCHIMIE", "SERUM", "g/L"
        ],
        DataMigrationKind.History =>
        [
            "R998877", "P000123", "2019/00456", "AB123456", "14/02/2024", "GLY", "1,05", "g/L", "0,70 - 1,10", "24/01234"
        ],
        DataMigrationKind.OpeningBalances =>
        [
            "F2024-0815", "P000123", "2019/00456", "AB123456", "", "150,00", "31/12/2025", "FA-2024-0815",
            "15/11/2024", "Reste à payer sur bilan"
        ],
        _ => []
    };

    public static string FileName(DataMigrationKind kind, string extension) => kind switch
    {
        DataMigrationKind.Patients => "modele-patients",
        DataMigrationKind.Prescribers => "modele-prescripteurs",
        DataMigrationKind.Catalogue => "modele-catalogue-tarifs",
        DataMigrationKind.History => "modele-anteriorites",
        DataMigrationKind.OpeningBalances => "modele-creances-ouvertes",
        _ => "modele"
    } + extension;

    /// <summary>The CSV template, « ; »-separated and UTF-8 with its mark, as a French Excel opens it.</summary>
    public static byte[] Csv(DataMigrationKind kind)
    {
        var text = CsvSource.Line(Headers(kind)) + "\r\n" + CsvSource.Line(Example(kind)) + "\r\n";
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }
}
