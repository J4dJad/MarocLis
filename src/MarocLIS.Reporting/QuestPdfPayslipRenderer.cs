using System.Globalization;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>
/// The payslip, in French, with the mentions the Code du travail (art. 370) requires: employer and
/// its CNSS affiliation, the employee's identity, post and CNSS number, the period, every element
/// of pay and every deduction, and the net paid. The employer's contributions follow for
/// information. Printed from the amounts stored with the payslip, never recomputed.
/// </summary>
public sealed class QuestPdfPayslipRenderer : IPayslipRenderer
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private readonly ReportingOptions _options;

    public QuestPdfPayslipRenderer(ReportingOptions options)
    {
        _options = options;
        QuestPdfReportRenderer.Initialise(options);
    }

    private static string A(decimal value) => value.ToString("N2", Fr);
    private static string P(decimal rate) => $"{rate.ToString("0.##", Fr)} %";

    public byte[] Render(IReadOnlyList<PayslipDocument> payslips)
    {
        ArgumentNullException.ThrowIfNull(payslips);

        return Document.Create(container =>
        {
            foreach (var doc in payslips)
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(14, Unit.Millimetre);
                    page.DefaultTextStyle(x => x.FontSize(9).FontFamily(_options.LatinFontFamily).FontColor(Colors.Black));

                    page.Header().Element(c => Header(c, doc));
                    page.Content().PaddingTop(6).Element(c => Body(c, doc));
                    page.Footer().AlignCenter().Text(
                        "Bulletin à conserver par le salarié sans limitation de durée.").FontSize(7).FontColor(Colors.Grey.Darken2);
                });
        }).GeneratePdf();
    }

    private static void Header(IContainer container, PayslipDocument doc)
    {
        var employer = doc.Employer;
        var month = new DateTime(doc.Year, doc.Month, 1).ToString("MMMM yyyy", Fr);

        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                if (employer.LogoPng is { Length: > 0 })
                    row.ConstantItem(22, Unit.Millimetre).Height(16, Unit.Millimetre).Image(employer.LogoPng).FitArea();

                row.RelativeItem().PaddingHorizontal(4).Column(identity =>
                {
                    var name = string.IsNullOrWhiteSpace(employer.LegalName) ? employer.Name : employer.LegalName;
                    identity.Item().Text(name).FontSize(12).Bold();

                    var place = string.Join(" — ", new[] { employer.Address, string.Join(" ", new[] { employer.PostalCode, employer.City }
                        .Where(s => !string.IsNullOrWhiteSpace(s))) }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (place.Length > 0) identity.Item().Text(place).FontSize(8);

                    var ids = string.Join("   ·   ", new (string Label, string? Value)[]
                        {
                            ("CNSS", employer.CnssAffiliationNumber), ("ICE", employer.Ice), ("IF", employer.FiscalId),
                            ("RC", employer.CommercialRegisterNumber)
                        }
                        .Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Label} : {p.Value}"));
                    if (ids.Length > 0) identity.Item().Text(ids).FontSize(7.5f);
                });

                row.ConstantItem(60, Unit.Millimetre).AlignRight().Column(title =>
                {
                    title.Item().AlignRight().Text("BULLETIN DE PAIE").FontSize(13).Bold();
                    title.Item().AlignRight().Text($"Période : {Fr.TextInfo.ToTitleCase(month)}").FontSize(10).Bold();
                    title.Item().AlignRight()
                         .Text($"Du 01/{doc.Month:00}/{doc.Year} au {DateTime.DaysInMonth(doc.Year, doc.Month)}/{doc.Month:00}/{doc.Year}")
                         .FontSize(8);
                    if (doc.Status == PayrollStatus.Draft)
                        title.Item().AlignRight().Text("PROVISOIRE — paie non clôturée").FontSize(8).Bold()
                             .FontColor(Colors.Red.Darken2);
                });
            });

            column.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Black);
        });
    }

    private static void Body(IContainer container, PayslipDocument doc)
    {
        var p = doc.Payslip;
        var a = p.Amounts;
        var v = p.Variables;
        var rates = doc.Parameters;
        var payDate = new DateOnly(doc.Year, doc.Month, 1).AddMonths(1).AddDays(-1);
        var years = LeaveRules.FullYears(p.HireDate, payDate);

        container.Column(column =>
        {
            column.Item().Border(0.5f).BorderColor(Colors.Grey.Darken1).Padding(5).Row(row =>
            {
                void Pair(ColumnDescriptor c, string label, string? value) => c.Item().Text(t =>
                {
                    t.Span($"{label} : ").FontSize(8).FontColor(Colors.Grey.Darken3);
                    t.Span(string.IsNullOrWhiteSpace(value) ? "—" : value).FontSize(8.5f).SemiBold();
                });

                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(p.FullName).FontSize(11).Bold();
                    Pair(c, "Matricule", p.Number);
                    Pair(c, "Emploi", p.JobTitle);
                    Pair(c, "Contrat", ContractLabel(p.ContractType));
                });
                row.RelativeItem().Column(c =>
                {
                    Pair(c, "CIN", p.Cin);
                    Pair(c, "N° CNSS", p.CnssNumber);
                    Pair(c, "Embauche", $"{p.HireDate:dd/MM/yyyy} ({years} an{(years > 1 ? "s" : string.Empty)})");
                    Pair(c, "Personnes à charge", p.DependentPersons.ToString(Fr));
                    Pair(c, "Jours payés", a.PaidDays.ToString("0.#", Fr) + $" / {rates.MonthlyWorkingDays.ToString("0", Fr)}");
                });
            });

            column.Item().PaddingTop(8).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(5);
                    c.RelativeColumn(2);
                    c.RelativeColumn(1.4f);
                    c.RelativeColumn(2);
                    c.RelativeColumn(2);
                });

                table.Header(h =>
                {
                    foreach (var (text, right) in new[] { ("Rubrique", false), ("Base", true), ("Taux", true),
                                                          ("Gains", true), ("Retenues", true) })
                    {
                        var cell = h.Cell().Background(Colors.Grey.Lighten3).BorderBottom(0.8f).Padding(3);
                        (right ? cell.AlignRight() : cell).Text(text).Bold().FontSize(8.5f);
                    }
                });

                void Line(string label, decimal? @base, string? rate, decimal? gain, decimal? deduction, bool bold = false)
                {
                    IContainer Cell() => table.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2.5f).PaddingHorizontal(3);
                    var l = Cell().Text(label); if (bold) l.Bold();
                    Cell().AlignRight().Text(@base is { } b ? A(b) : string.Empty);
                    Cell().AlignRight().Text(rate ?? string.Empty);
                    var g = Cell().AlignRight().Text(gain is { } x ? A(x) : string.Empty); if (bold) g.Bold();
                    var d = Cell().AlignRight().Text(deduction is { } y ? A(y) : string.Empty); if (bold) d.Bold();
                }

                Line("Salaire de base", rates.MonthlyWorkingDays, null, p.BaseSalary, null);
                if (a.AbsenceDeduction > 0)
                    Line($"Absences non rémunérées ({v.AbsenceDays.ToString("0.#", Fr)} j)", p.BaseSalary / rates.MonthlyWorkingDays,
                         null, -a.AbsenceDeduction, null);
                if (a.SeniorityBonus > 0)
                    Line("Prime d'ancienneté", p.BaseSalary - a.AbsenceDeduction, P(a.SeniorityRate), a.SeniorityBonus, null);

                var hourly = p.BaseSalary / rates.MonthlyHours;
                if (v.OvertimeHours25 > 0) Line($"Heures supplémentaires à 25 % ({v.OvertimeHours25:0.##} h)", hourly, "125 %", Math.Round(hourly * 1.25m * v.OvertimeHours25, 2), null);
                if (v.OvertimeHours50 > 0) Line($"Heures supplémentaires à 50 % ({v.OvertimeHours50:0.##} h)", hourly, "150 %", Math.Round(hourly * 1.5m * v.OvertimeHours50, 2), null);
                if (v.OvertimeHours100 > 0) Line($"Heures supplémentaires à 100 % ({v.OvertimeHours100:0.##} h)", hourly, "200 %", Math.Round(hourly * 2m * v.OvertimeHours100, 2), null);
                if (v.TaxableBonus > 0) Line("Primes et gratifications", null, null, v.TaxableBonus, null);
                if (v.TransportAllowance > 0)
                    Line(v.TransportAllowance > rates.TransportExemptionCap
                             ? $"Indemnité de transport (dont {A(rates.TransportExemptionCap)} exonérés)"
                             : "Indemnité de transport (exonérée)", null, null, v.TransportAllowance, null);
                if (v.OtherExemptAllowance > 0) Line("Autres indemnités non imposables", null, null, v.OtherExemptAllowance, null);

                Line("SALAIRE BRUT", null, null, a.Gross, null, bold: true);

                if (a.CnssEmployee > 0)
                    Line("CNSS — prestations sociales", Math.Min(a.GrossTaxable, rates.CnssCeiling), P(rates.CnssEmployeeRate), null, a.CnssEmployee);
                if (a.AmoEmployee > 0)
                    Line("AMO — assurance maladie obligatoire", a.GrossTaxable, P(rates.AmoEmployeeRate), null, a.AmoEmployee);
                if (a.IncomeTax > 0 || a.IncomeTaxBeforeFamily > 0)
                    Line("Impôt sur le revenu", a.NetTaxable, null, null, a.IncomeTax);
                if (v.AdvanceDeduction > 0) Line("Remboursement d'avance", null, null, null, v.AdvanceDeduction);
                if (v.OtherDeduction > 0) Line("Autres retenues", null, null, null, v.OtherDeduction);

                Line("TOTAL", null, null, a.Gross, a.Gross - a.NetPay, bold: true);
            });

            column.Item().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("Détail de l'impôt").FontSize(8).Bold();
                    c.Item().Text($"Brut imposable : {A(a.GrossTaxable)}   ·   Frais professionnels : {A(a.ProfessionalExpenses)}").FontSize(7.5f);
                    c.Item().Text($"Net imposable : {A(a.NetTaxable)}   ·   IR brut : {A(a.IncomeTaxBeforeFamily)}   ·   Charges de famille : −{A(a.FamilyDeduction)}").FontSize(7.5f);
                    if (a.Exemption != PayrollExemption.None)
                        c.Item().Text(ExemptionLabel(a.Exemption)).FontSize(7.5f).Italic();
                });

                row.ConstantItem(62, Unit.Millimetre).Border(1).Padding(5).Column(c =>
                {
                    c.Item().AlignRight().Text("NET À PAYER").FontSize(9).Bold();
                    c.Item().AlignRight().Text($"{A(a.NetPay)} DH").FontSize(14).Bold();
                    var bank = string.IsNullOrWhiteSpace(p.Rib) ? "Paiement en espèces ou par chèque"
                        : $"Virement {p.BankName} — RIB {p.Rib}";
                    c.Item().AlignRight().Text(bank).FontSize(7);
                });
            });

            if (!string.IsNullOrWhiteSpace(v.Note))
                column.Item().PaddingTop(4).Text($"Note : {v.Note}").FontSize(7.5f);

            column.Item().PaddingTop(10).Text("Charges patronales (pour information)").FontSize(8).Bold();
            column.Item().Text(string.Join("   ·   ", new[]
            {
                $"CNSS prestations sociales {A(a.CnssEmployer)}",
                $"Allocations familiales {A(a.FamilyAllowanceEmployer)}",
                $"Formation professionnelle {A(a.TrainingTaxEmployer)}",
                $"AMO {A(a.AmoEmployer)}",
                $"Coût total employeur {A(a.EmployerCost)} DH"
            })).FontSize(7.5f);
        });
    }

    private static string ContractLabel(ContractType type) => type switch
    {
        ContractType.Permanent => "CDI",
        ContractType.FixedTerm => "CDD",
        ContractType.Anapec => "Contrat d'insertion (ANAPEC)",
        ContractType.Tahfiz => "Programme Tahfiz",
        ContractType.Internship => "Stage",
        ContractType.PartTime => "Temps partiel",
        _ => type.ToString()
    };

    private static string ExemptionLabel(PayrollExemption exemption) => exemption switch
    {
        PayrollExemption.InsertionContract => "Contrat d'insertion : indemnité de stage exonérée de CNSS, d'AMO et d'IR.",
        PayrollExemption.Tahfiz => "Programme Tahfiz : charges patronales CNSS prises en charge par l'État ; IR exonéré jusqu'à 10 000 DH brut.",
        PayrollExemption.Internship => "Stage : gratification exonérée de cotisations et d'IR.",
        _ => string.Empty
    };
}
