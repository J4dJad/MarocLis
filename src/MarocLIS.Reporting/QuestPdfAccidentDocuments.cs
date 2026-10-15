using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>
/// The work-accident papers: the employer's declaration sent to the insurer, and the yearly
/// register of accidents for the hygiene and safety committee and the labour inspector.
/// </summary>
public sealed partial class QuestPdfHrDocumentRenderer
{
    private static string DT(DateTime at) => at.ToString("dd/MM/yyyy 'à' HH'h'mm", Fr);

    internal static string KindLabel(AccidentKind kind) => kind switch
    {
        AccidentKind.Commuting => "Accident de trajet",
        AccidentKind.BloodExposure => "Accident d'exposition au sang (AES)",
        _ => "Accident du travail"
    };

    internal static string ExposureLabel(ExposureType? type) => type switch
    {
        ExposureType.Needlestick => "Piqûre par aiguille",
        ExposureType.Cut => "Coupure par objet tranchant",
        ExposureType.MucousSplash => "Projection sur muqueuse (œil, bouche)",
        ExposureType.SkinSplash => "Projection sur peau lésée",
        _ => "—"
    };

    public byte[] RenderDeclaration(AccidentDeclarationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var a = document.Accident;
        var r = a.Request;
        var e = document.Employee;

        return Document.Create(container => container.Page(page =>
        {
            Page(page, document.Employer);
            page.DefaultTextStyle(x => x.FontSize(9.5f).FontFamily(_options.LatinFontFamily).LineHeight(1.3f));
            page.Content().PaddingTop(10).Column(column =>
            {
                column.Spacing(5);
                column.Item().AlignCenter().Text("DÉCLARATION D'ACCIDENT DU TRAVAIL").FontSize(15).Bold().LetterSpacing(0.04f);
                column.Item().AlignCenter().Text($"{KindLabel(r.Kind)} — référence {a.Number}").FontSize(10);
                column.Item().AlignCenter().Text("Loi n° 18-12 relative à la réparation des accidents du travail — à adresser à l'assureur dans les 48 heures")
                      .FontSize(8).Italic().FontColor(Colors.Grey.Darken2);

                Section(column, "Employeur");
                Pairs(column,
                [
                    ("Raison sociale", LegalName(document.Employer)),
                    ("Adresse", Address(document.Employer)),
                    ("N° d'affiliation CNSS", document.Employer.CnssAffiliationNumber),
                    ("Assureur AT", r.InsurerName),
                    ("N° de dossier assureur", r.InsurerClaimNumber)
                ]);

                Section(column, "Victime");
                Pairs(column,
                [
                    ("Nom et prénom", e.FullName),
                    ("Matricule", e.Number),
                    ("CIN", e.Cin),
                    ("N° d'immatriculation CNSS", e.CnssNumber),
                    ("Date de naissance", e.BirthDate is { } b ? D(b) : null),
                    ("Emploi", JobOf(e)),
                    ("Service", document.DepartmentName),
                    ("Date d'embauche", D(e.HireDate)),
                    ("Contrat", ContractLabel(e.ContractType))
                ]);

                Section(column, "Accident");
                Pairs(column,
                [
                    ("Date et heure", DT(r.OccurredAt)),
                    ("Lieu", r.Place),
                    ("Nature", KindLabel(r.Kind)),
                    ("Type d'exposition", r.Kind == AccidentKind.BloodExposure ? ExposureLabel(r.Exposure) : null),
                    ("Source identifiée", r.Kind == AccidentKind.BloodExposure ? (r.SourceIdentified ? "Oui" : "Non") : null),
                    ("Témoins", r.Witnesses),
                    ("Premiers soins sur place", r.FirstAidGiven ? "Oui" : "Non"),
                    ("Consultation médicale", r.ConsultedAt is { } c ? DT(c) : null)
                ]);
                column.Item().Text("Circonstances détaillées").SemiBold();
                column.Item().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(5).MinHeight(40).Text(r.Circumstances);
                column.Item().Text("Nature et siège des lésions (selon le certificat médical initial)").SemiBold();
                column.Item().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(5).MinHeight(24).Text(r.Injuries ?? string.Empty);

                if (a.Stoppages.Count > 0)
                {
                    var first = a.Stoppages.Where(s => s.Status == LeaveStatus.Approved).OrderBy(s => s.StartDate).FirstOrDefault();
                    if (first is not null)
                        column.Item().Text($"Arrêt de travail prescrit du {D(first.StartDate)} au {D(first.EndDate)}.");
                }
                column.Item().Text(r.Subrogation
                    ? "L'employeur maintient le salaire et demande à être subrogé dans les droits de la victime aux indemnités journalières."
                    : "Les indemnités journalières sont à verser directement à la victime.").FontSize(9);

                column.Item().Text("Pièce jointe : certificat médical initial. Aucun résultat d'analyse ni diagnostic ne figure sur cette déclaration.")
                      .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken2);
                column.Item().ShowEntire().Column(sign =>
                {
                    sign.Item().PaddingTop(4).AlignRight().Text(Place(document.Employer, document.IssuedOn));
                    sign.Item().AlignRight().Text("L'employeur ou son représentant").SemiBold();
                    sign.Item().AlignRight().Text(document.IssuedBy);
                    sign.Item().PaddingTop(30).AlignRight().Text("Signature et cachet").FontSize(8).FontColor(Colors.Grey.Darken2);
                });
            });
        })).GeneratePdf();
    }

    public byte[] RenderReport(AccidentReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rows = document.Accidents.Where(a => a.OccurredAt.Year == document.Year).OrderBy(a => a.OccurredAt).ToList();

        return Document.Create(container => container.Page(page =>
        {
            Page(page, document.Employer);
            page.Size(PageSizes.A4.Landscape());
            page.Margin(14, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(8.5f).FontFamily(_options.LatinFontFamily).LineHeight(1.25f));
            page.Content().PaddingTop(8).Column(column =>
            {
                column.Spacing(8);
                column.Item().AlignCenter().Text($"REGISTRE DES ACCIDENTS DU TRAVAIL — {document.Year}").FontSize(14).Bold();
                column.Item().AlignCenter().Text($"{rows.Count} accident(s), {rows.Sum(r => r.StoppageDays):0.#} jour(s) d'arrêt — édité le {D(document.IssuedOn)}")
                      .FontSize(9).FontColor(Colors.Grey.Darken2);

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(1.6f); c.RelativeColumn(1.6f); c.RelativeColumn(3f); c.RelativeColumn(2f);
                        c.RelativeColumn(2.4f); c.RelativeColumn(2.4f); c.RelativeColumn(1.6f); c.RelativeColumn(1.2f);
                        c.RelativeColumn(1.6f); c.RelativeColumn(1.2f);
                    });
                    table.Header(h =>
                    {
                        foreach (var (text, right) in new[] { ("N°", false), ("Date", false), ("Salarié", false), ("Service", false),
                                                              ("Nature", false), ("Lieu", false), ("Déclaré le", false),
                                                              ("Arrêt (j)", true), ("Reprise", false), ("Clos", false) })
                        {
                            var cell = h.Cell().Background(Colors.Grey.Lighten3).BorderBottom(0.8f).Padding(3);
                            (right ? cell.AlignRight() : cell).Text(text).Bold();
                        }
                    });

                    foreach (var r in rows)
                    {
                        IContainer Cell() => table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2.5f).PaddingHorizontal(3);
                        Cell().Text(r.Number);
                        Cell().Text(r.OccurredAt.ToString("dd/MM/yyyy HH:mm", Fr));
                        Cell().Text($"{r.EmployeeNumber} {r.EmployeeName}");
                        Cell().Text(r.DepartmentName ?? "—");
                        Cell().Text(KindLabel(r.Kind));
                        Cell().Text(r.Place ?? string.Empty);
                        Cell().Text(r.DeclaredToInsurerOn is { } d ? D(d) : "non déclaré");
                        Cell().AlignRight().Text(r.StoppageDays.ToString("0.#", Fr));
                        Cell().Text(r.ReturnedToWorkOn is { } back ? D(back) : string.Empty);
                        Cell().Text(r.IsClosed ? "oui" : "non");
                    }
                });

                if (document.Summary.Count > 0)
                {
                    column.Item().PaddingTop(6).Text("Synthèse par service et par nature").Bold();
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(3); c.RelativeColumn(1.4f); c.RelativeColumn(1.8f); c.RelativeColumn(1.8f); });
                        table.Header(h =>
                        {
                            foreach (var (text, right) in new[] { ("Service", false), ("Nature", false), ("Nombre", true),
                                                                  ("Avec arrêt", true), ("Jours d'arrêt", true) })
                            {
                                var cell = h.Cell().Background(Colors.Grey.Lighten3).BorderBottom(0.8f).Padding(3);
                                (right ? cell.AlignRight() : cell).Text(text).Bold();
                            }
                        });
                        foreach (var s in document.Summary)
                        {
                            IContainer Cell() => table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2.5f).PaddingHorizontal(3);
                            Cell().Text(s.Department);
                            Cell().Text(KindLabel(s.Kind));
                            Cell().AlignRight().Text(s.Count.ToString(Fr));
                            Cell().AlignRight().Text(s.WithStoppage.ToString(Fr));
                            Cell().AlignRight().Text(s.StoppageDays.ToString("0.#", Fr));
                        }
                    });
                }
            });
        })).GeneratePdf();
    }

    private static string ContractLabel(ContractType type) => type switch
    {
        ContractType.Permanent => "CDI",
        ContractType.FixedTerm => "CDD",
        ContractType.Anapec => "Contrat d'insertion (ANAPEC)",
        ContractType.Tahfiz => "Programme Tahfiz",
        ContractType.Internship => "Stage",
        ContractType.PartTime => "Temps partiel",
        _ => "Contrat de travail"
    };

    private static void Section(ColumnDescriptor column, string title) =>
        column.Item().PaddingTop(4).BorderBottom(0.8f).PaddingBottom(2).Text(title.ToUpper(Fr)).Bold().FontSize(10);

    private static void Pairs(ColumnDescriptor column, (string Label, string? Value)[] pairs)
    {
        var shown = pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToList();
        column.Item().Table(table =>
        {
            table.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(3); });
            foreach (var (label, value) in shown)
            {
                table.Cell().PaddingVertical(1.5f).Text(label).FontColor(Colors.Grey.Darken2);
                table.Cell().PaddingVertical(1.5f).Text(value!).SemiBold();
            }
            if (shown.Count % 2 == 1) { table.Cell(); table.Cell(); }
        });
    }
}
