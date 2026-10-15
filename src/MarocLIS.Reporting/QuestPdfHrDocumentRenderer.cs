using System.Globalization;
using MarocLIS.Application.Billing;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Patients;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>
/// The HR letters, in French, on the laboratory's letterhead: attestation de travail, attestation
/// de salaire, certificat de travail, reçu pour solde de tout compte, and the salary transfer
/// order for the bank.
/// </summary>
public sealed class QuestPdfHrDocumentRenderer : IHrDocumentRenderer
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private readonly ReportingOptions _options;

    public QuestPdfHrDocumentRenderer(ReportingOptions options)
    {
        _options = options;
        QuestPdfReportRenderer.Initialise(options);
    }

    private static string A(decimal value) => value.ToString("N2", Fr);
    private static string D(DateOnly date) => date.ToString("dd/MM/yyyy", Fr);
    private static string MonthName(int year, int month) =>
        Fr.TextInfo.ToTitleCase(new DateTime(year, month, 1).ToString("MMMM yyyy", Fr));

    /// <summary>"d'octobre 2026", "de mars 2026": the month inside a sentence.</summary>
    private static string OfMonth(int year, int month)
    {
        var name = new DateTime(year, month, 1).ToString("MMMM yyyy", Fr);
        return "aeiouyh".Contains(name[0], StringComparison.Ordinal) ? $"d'{name}" : $"de {name}";
    }

    // ---- Letters -------------------------------------------------------------------------------

    public byte[] Render(HrDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return Document.Create(container => container.Page(page =>
        {
            Page(page, document.Employer);
            page.Content().PaddingTop(18).Column(column =>
            {
                column.Spacing(10);
                column.Item().AlignCenter().Text(Title(document.Kind)).FontSize(16).Bold().LetterSpacing(0.05f);
                column.Item().PaddingTop(10);

                switch (document.Kind)
                {
                    case HrDocumentKind.EmploymentAttestation:
                    case HrDocumentKind.SalaryAttestation:
                        Attestation(column, document);
                        break;
                    case HrDocumentKind.WorkCertificate:
                        Certificate(column, document);
                        break;
                    case HrDocumentKind.SettlementReceipt:
                        Receipt(column, document);
                        return;
                }

                column.Item().PaddingTop(16).AlignRight().Text(Place(document.Employer, document.IssuedOn));
                column.Item().PaddingTop(4).AlignRight().Column(sign =>
                {
                    sign.Item().AlignRight().Text("Pour la direction").SemiBold();
                    sign.Item().AlignRight().Text(document.IssuedBy);
                    sign.Item().PaddingTop(46).AlignRight().Text("Signature et cachet").FontSize(8).FontColor(Colors.Grey.Darken2);
                });
            });
        })).GeneratePdf();
    }

    private void Page(PageDescriptor page, InvoiceIssuer employer)
    {
        page.Size(PageSizes.A4);
        page.Margin(20, Unit.Millimetre);
        page.DefaultTextStyle(x => x.FontSize(10.5f).FontFamily(_options.LatinFontFamily).FontColor(Colors.Black).LineHeight(1.35f));
        page.Header().Element(c => Letterhead(c, employer));
        page.Footer().AlignCenter().Text(FooterLine(employer)).FontSize(7).FontColor(Colors.Grey.Darken2);
    }

    private static void Letterhead(IContainer container, InvoiceIssuer employer)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                if (employer.LogoPng is { Length: > 0 })
                    row.ConstantItem(24, Unit.Millimetre).Height(18, Unit.Millimetre).Image(employer.LogoPng).FitArea();

                row.RelativeItem().PaddingHorizontal(4).Column(identity =>
                {
                    identity.Item().Text(LegalName(employer)).FontSize(13).Bold();
                    if (Address(employer) is { Length: > 0 } place) identity.Item().Text(place).FontSize(8.5f);
                    var contact = string.Join("   ·   ", new[] { employer.Phone, employer.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (contact.Length > 0) identity.Item().Text(contact).FontSize(8.5f);
                });
            });
            column.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Black);
        });
    }

    private static string FooterLine(InvoiceIssuer employer) => string.Join("   ·   ", new (string Label, string? Value)[]
        {
            ("CNSS", employer.CnssAffiliationNumber), ("ICE", employer.Ice), ("IF", employer.FiscalId),
            ("RC", employer.CommercialRegisterNumber), ("Patente", employer.ProfessionalTaxNumber)
        }
        .Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Label} : {p.Value}"));

    private static string LegalName(InvoiceIssuer e) => string.IsNullOrWhiteSpace(e.LegalName) ? e.Name : e.LegalName;

    private static string Address(InvoiceIssuer e) =>
        string.Join(", ", new[] { e.Address, string.Join(" ", new[] { e.PostalCode, e.City }.Where(s => !string.IsNullOrWhiteSpace(s))) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string Place(InvoiceIssuer e, DateOnly on) =>
        $"Fait à {(string.IsNullOrWhiteSpace(e.City) ? "…………………" : e.City)}, le {D(on)}";

    private static string Title(HrDocumentKind kind) => kind switch
    {
        HrDocumentKind.EmploymentAttestation => "ATTESTATION DE TRAVAIL",
        HrDocumentKind.SalaryAttestation => "ATTESTATION DE SALAIRE",
        HrDocumentKind.WorkCertificate => "CERTIFICAT DE TRAVAIL",
        _ => "REÇU POUR SOLDE DE TOUT COMPTE"
    };

    private static bool Female(HrDocumentEmployee e) => e.Gender == Gender.Female;
    private static string Civility(HrDocumentEmployee e) => e.Gender switch
    {
        Gender.Female => "Madame",
        Gender.Male => "Monsieur",
        _ => "M./Mme"
    };
    private static string Agree(HrDocumentEmployee e, string masculine, string feminine, string either) => e.Gender switch
    {
        Gender.Female => feminine,
        Gender.Male => masculine,
        _ => either
    };

    private static string JobOf(HrDocumentEmployee e) => !string.IsNullOrWhiteSpace(e.JobTitle) ? e.JobTitle : e.Job switch
    {
        EmployeeJob.Biologist => "biologiste",
        EmployeeJob.Technician => Agree(e, "technicien de laboratoire", "technicienne de laboratoire", "technicien(ne) de laboratoire"),
        EmployeeJob.Nurse => Agree(e, "infirmier préleveur", "infirmière préleveuse", "infirmier(ère) préleveur(se)"),
        EmployeeJob.Secretary => "secrétaire médicale",
        EmployeeJob.Courier => "coursier",
        EmployeeJob.Cleaner => "agent d'entretien",
        _ => "agent administratif"
    };

    private static string ContractOf(ContractType type) => type switch
    {
        ContractType.Permanent => "d'un contrat de travail à durée indéterminée",
        ContractType.FixedTerm => "d'un contrat de travail à durée déterminée",
        ContractType.Anapec => "d'un contrat d'insertion (ANAPEC)",
        ContractType.Tahfiz => "d'un contrat de travail (programme Tahfiz)",
        ContractType.Internship => "d'une convention de stage",
        ContractType.PartTime => "d'un contrat de travail à temps partiel",
        _ => "d'un contrat de travail"
    };

    /// <summary>"Madame X, née le …, titulaire de la CIN n° …, immatriculée à la CNSS sous le n° …"</summary>
    private static void Identity(TextDescriptor t, HrDocumentEmployee e)
    {
        t.Span($"{Civility(e)} ");
        t.Span(e.FullName).Bold();
        if (e.BirthDate is { } born) t.Span($", {Agree(e, "né", "née", "né(e)")} le {D(born)}");
        if (!string.IsNullOrWhiteSpace(e.Cin)) t.Span($", titulaire de la CIN n° {e.Cin}");
        if (!string.IsNullOrWhiteSpace(e.CnssNumber))
            t.Span($", {Agree(e, "immatriculé", "immatriculée", "immatriculé(e)")} à la CNSS sous le n° {e.CnssNumber}");
    }

    private static void Employer(TextDescriptor t, InvoiceIssuer employer, string verb)
    {
        t.Span("Nous soussignés, ");
        t.Span(LegalName(employer)).SemiBold();
        if (Address(employer) is { Length: > 0 } place) t.Span($", {place}");
        if (!string.IsNullOrWhiteSpace(employer.CnssAffiliationNumber))
            t.Span($", affilié à la CNSS sous le n° {employer.CnssAffiliationNumber}");
        t.Span($", {verb} que ");
    }

    private static void Attestation(ColumnDescriptor column, HrDocument doc)
    {
        var e = doc.Employee;
        column.Item().Text(t =>
        {
            t.Justify();
            Employer(t, doc.Employer, "attestons");
            Identity(t, e);
            t.Span($", est {Agree(e, "employé", "employée", "employé(e)")} au sein de notre établissement depuis le ");
            t.Span(D(e.HireDate)).SemiBold();
            t.Span(" en qualité de ");
            t.Span(JobOf(e)).SemiBold();
            t.Span($", dans le cadre {ContractOf(e.ContractType)}.");
        });

        if (doc.Kind == HrDocumentKind.SalaryAttestation && doc.BaseSalary is { } salary)
        {
            column.Item().Text(t =>
            {
                t.Justify();
                t.Span($"{Agree(e, "L'intéressé", "L'intéressée", "L'intéressé(e)")} perçoit un salaire mensuel brut de base de ");
                t.Span($"{A(salary)} DH").Bold();
                t.Span($" ({AmountInWords.Render(salary, AppLanguage.French)}).");
            });

            if (doc.SalaryMonths.Count > 0)
            {
                column.Item().Text("Rémunérations versées au cours des derniers mois :");
                column.Item().PaddingHorizontal(30).Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); });
                    table.Header(h =>
                    {
                        h.Cell().BorderBottom(0.8f).Padding(3).Text("Mois").Bold();
                        h.Cell().BorderBottom(0.8f).Padding(3).AlignRight().Text("Salaire brut").Bold();
                        h.Cell().BorderBottom(0.8f).Padding(3).AlignRight().Text("Net payé").Bold();
                    });
                    foreach (var m in doc.SalaryMonths)
                    {
                        table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(MonthName(m.Year, m.Month));
                        table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text(A(m.Gross));
                        table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text(A(m.NetPay));
                    }
                });
            }
        }

        column.Item().Text($"Cette attestation est délivrée à {Agree(e, "l'intéressé", "l'intéressée", "l'intéressé(e)")}, " +
                           "à sa demande, pour servir et valoir ce que de droit.");
    }

    private static void Certificate(ColumnDescriptor column, HrDocument doc)
    {
        var e = doc.Employee;
        column.Item().Text(t =>
        {
            t.Justify();
            Employer(t, doc.Employer, "certifions");
            Identity(t, e);
            t.Span($", a été {Agree(e, "employé", "employée", "employé(e)")} au sein de notre établissement du ");
            t.Span(D(e.HireDate)).SemiBold();
            t.Span(" au ");
            t.Span(e.ExitDate is { } exit ? D(exit) : "…").SemiBold();
            t.Span(" en qualité de ");
            t.Span(JobOf(e)).SemiBold();
            t.Span(".");
        });

        column.Item().Text($"{Agree(e, "Il", "Elle", "Il (elle)")} nous quitte libre de tout engagement.");
        column.Item().Text($"Ce certificat est délivré à {Agree(e, "l'intéressé", "l'intéressée", "l'intéressé(e)")} " +
                           "conformément à l'article 72 du Code du travail, pour servir et valoir ce que de droit.");
    }

    private static void Receipt(ColumnDescriptor column, HrDocument doc)
    {
        var e = doc.Employee;
        var s = doc.Settlement!;
        var paid = s.FinalPayslip?.NetPay ?? s.LeaveIndemnity + s.NoticeIndemnity + s.SeverancePay;

        column.Item().Text(t =>
        {
            t.Justify();
            t.Span($"Je {Agree(e, "soussigné", "soussignée", "soussigné(e)")}, ");
            Identity(t, e);
            t.Span($", ayant occupé l'emploi de {JobOf(e)} du {D(e.HireDate)} au {D(s.ExitDate)}, reconnais avoir reçu de ");
            t.Span(LegalName(doc.Employer)).SemiBold();
            t.Span(" la somme de ");
            t.Span($"{A(paid)} DH").Bold();
            t.Span($" ({AmountInWords.Render(paid, AppLanguage.French)}) pour solde de tout compte, en paiement des sommes suivantes :");
        });

        column.Item().PaddingHorizontal(20).Table(table =>
        {
            table.ColumnsDefinition(c => { c.RelativeColumn(5); c.RelativeColumn(2); });

            void Line(string label, decimal amount, bool bold = false)
            {
                var l = table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(label);
                var a = table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text($"{A(amount)} DH");
                if (bold) { l.Bold(); a.Bold(); }
            }

            var indemnities = s.LeaveIndemnity + s.NoticeIndemnity + s.SeverancePay;
            if (s.FinalPayslip is { } final)
                Line($"Salaire et accessoires {OfMonth(final.Year, final.Month)} (brut)", final.Gross - indemnities);
            if (s.LeaveIndemnity > 0)
                Line($"Indemnité compensatrice de congés payés ({s.LeaveDays.ToString("0.##", Fr)} jours)", s.LeaveIndemnity);
            if (s.NoticeIndemnity > 0) Line("Indemnité compensatrice de préavis", s.NoticeIndemnity);
            if (s.SeverancePay > 0) Line("Indemnité légale de licenciement (exonérée)", s.SeverancePay);

            if (s.FinalPayslip is { } slip)
            {
                Line("Total brut", slip.Gross, bold: true);
                Line("Retenues (CNSS, AMO, impôt sur le revenu, avances)", -s.FinalPayslipDeductions);
                Line("Net perçu", slip.NetPay, bold: true);
            }
            else
                Line("Total (montants bruts, avant retenues)", indemnities, bold: true);
        });

        column.Item().Text(t =>
        {
            t.Justify();
            t.Span("Le présent reçu est établi en deux exemplaires, dont l'un m'a été remis. Je reconnais avoir été " +
                   $"{Agree(e, "informé", "informée", "informé(e)")} qu'il peut être dénoncé dans les ");
            t.Span("soixante jours").SemiBold();
            t.Span(" suivant sa signature, par lettre recommandée avec accusé de réception ou par une réclamation " +
                   "devant l'agent de l'inspection du travail (articles 73 à 76 du Code du travail). Passé ce délai, " +
                   "il est libératoire pour l'employeur des sommes qui y sont portées.");
        });

        column.Item().PaddingTop(10).Text(s.SignedOn is { } signed
            ? Place(doc.Employer, signed)
            : $"Fait à {(string.IsNullOrWhiteSpace(doc.Employer.City) ? "…………………" : doc.Employer.City)}, le ……………………");

        column.Item().PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().Text("Pour l'employeur").SemiBold();
                c.Item().Text(doc.IssuedBy);
                c.Item().PaddingTop(40).Text("Signature et cachet").FontSize(8).FontColor(Colors.Grey.Darken2);
            });
            row.RelativeItem().Column(c =>
            {
                c.Item().AlignRight().Text($"{Agree(e, "Le salarié", "La salariée", "Le salarié")}").SemiBold();
                c.Item().AlignRight().Text(e.FullName);
                c.Item().PaddingTop(40).AlignRight()
                 .Text("Signature légalisée, précédée de la mention manuscrite\n« Lu et approuvé, pour solde de tout compte »")
                 .FontSize(8).FontColor(Colors.Grey.Darken2);
            });
        });
    }

    // ---- Transfer order ------------------------------------------------------------------------

    public byte[] RenderTransferOrder(TransferOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var transfers = order.ByTransfer.ToList();
        var others = order.OtherwisePaid.ToList();
        var total = transfers.Sum(l => l.Amount);
        var reference = $"SALAIRE {order.Month:00}/{order.Year}";

        return Document.Create(container => container.Page(page =>
        {
            Page(page, order.Employer);
            page.DefaultTextStyle(x => x.FontSize(9.5f).FontFamily(_options.LatinFontFamily).LineHeight(1.3f));
            page.Content().PaddingTop(12).Column(column =>
            {
                column.Spacing(8);
                column.Item().AlignRight().Text(Place(order.Employer, order.IssuedOn));
                column.Item().Text("À l'attention de Monsieur le Directeur de l'agence bancaire").SemiBold();
                column.Item().Text(t =>
                {
                    t.Span("Objet : ").Bold();
                    t.Span($"ordre de virement des salaires {OfMonth(order.Year, order.Month)}");
                });
                column.Item().Text(t =>
                {
                    t.Justify();
                    t.Span("Nous vous prions de bien vouloir débiter notre compte n° ……………………………………………… de la somme de ");
                    t.Span($"{A(total)} DH").Bold();
                    t.Span($" ({AmountInWords.Render(total, AppLanguage.French)}) et de virer aux bénéficiaires ci-dessous " +
                           $"les montants indiqués, avec le motif « {reference} ».");
                });

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(22); c.RelativeColumn(1.4f); c.RelativeColumn(4); c.RelativeColumn(2.6f);
                        c.RelativeColumn(4.6f); c.RelativeColumn(2.2f);
                    });
                    table.Header(h =>
                    {
                        foreach (var (text, right) in new[] { ("N°", false), ("Matricule", false), ("Bénéficiaire", false),
                                                              ("Banque", false), ("RIB", false), ("Montant", true) })
                        {
                            var cell = h.Cell().Background(Colors.Grey.Lighten3).BorderBottom(0.8f).Padding(3);
                            (right ? cell.AlignRight() : cell).Text(text).Bold().FontSize(8.5f);
                        }
                    });

                    var i = 0;
                    foreach (var l in transfers)
                    {
                        IContainer Cell() => table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2.5f).PaddingHorizontal(3);
                        Cell().Text((++i).ToString(Fr)).FontSize(8.5f);
                        Cell().Text(l.Number).FontSize(8.5f);
                        Cell().Text(l.FullName).FontSize(8.5f);
                        Cell().Text(l.BankName ?? string.Empty).FontSize(8.5f);
                        Cell().Text(FormatRib(l.Rib)).FontSize(8);
                        Cell().AlignRight().Text(A(l.Amount)).FontSize(8.5f);
                    }

                    table.Cell().ColumnSpan(5).PaddingVertical(4).PaddingHorizontal(3).Text($"TOTAL — {transfers.Count} virements").Bold();
                    table.Cell().PaddingVertical(4).PaddingHorizontal(3).AlignRight().Text(A(total)).Bold();
                });

                if (others.Count > 0)
                    column.Item().Text(
                        $"Hors virement (sans RIB, payés en espèces ou par chèque) : " +
                        string.Join(", ", others.Select(o => $"{o.FullName} {A(o.Amount)} DH"))).FontSize(8).Italic();

                column.Item().Text("Veuillez agréer, Monsieur le Directeur, l'expression de nos salutations distinguées.");
                column.Item().PaddingTop(6).AlignRight().Column(sign =>
                {
                    sign.Item().AlignRight().Text(LegalName(order.Employer)).SemiBold();
                    sign.Item().PaddingTop(40).AlignRight().Text("Signature autorisée et cachet").FontSize(8).FontColor(Colors.Grey.Darken2);
                });
            });
        })).GeneratePdf();
    }

    /// <summary>24 digits grouped as the banks print them: bank, town, account, key.</summary>
    private static string FormatRib(string? rib) =>
        rib is { Length: 24 } r ? $"{r[..3]} {r[3..6]} {r[6..22]} {r[22..]}" : rib ?? string.Empty;
}
