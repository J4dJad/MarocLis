using System.Text;
using FluentAssertions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Xunit;

namespace MarocLIS.Tests.Printing;

/// <summary>
/// Tests for the fiche de paillasse printed from the result-entry screen.
/// <para>
/// As for the invoice, the layout is not asserted — a unit test does not read a PDF's appearance
/// usefully. What is asserted is that a realistic sheet renders in every language, Arabic included
/// with its right-to-left page, and that every label it prints resolves: an unresolved key on a
/// bench sheet prints as "[[BenchSheet.Result]]" above the column the technician writes in.
/// </para>
/// </summary>
public sealed class BenchWorksheetRendererTests
{
    private static readonly DateTimeOffset PrintedAt = new(2026, 10, 13, 9, 30, 0, TimeSpan.FromHours(1));

    private static BenchWorksheetLine Line(string accession, OrderPriority priority, string analysisCode,
                                           string analysisName, int minutesAgo,
                                           params BenchWorksheetParameter[] parameters) => new()
    {
        LabOrderItemId = Guid.NewGuid(),
        AccessionNumber = accession,
        SampleBarcode = $"{accession}-01",
        PatientName = "EL ALAOUI Fatima",
        PatientNameArabic = "العلوي فاطمة",
        Gender = Gender.Female,
        AgeYears = 47,
        Priority = priority,
        DepartmentCode = "BIO",
        AnalysisCode = analysisCode,
        AnalysisName = new MultilingualText(analysisName, null, analysisName),
        ReceivedAt = PrintedAt.AddMinutes(-minutesAgo),
        DueAt = PrintedAt.AddMinutes(priority == OrderPriority.Routine ? 600 : -15),
        Parameters = parameters
    };

    private static BenchWorksheetParameter Parameter(string code, string name, string? unit) =>
        new(code, new MultilingualText(name, null, name), unit, ResultValueType.Numeric);

    private static BenchWorksheet Sample(bool allDepartments = false) => new()
    {
        Laboratory = new BenchLaboratory(
            new MultilingualText("Laboratoire Al Amal", "مختبر الأمل", "Al Amal Laboratory"),
            "LABO AL AMAL SARL",
            new MultilingualText("12, boulevard Zerktouni"),
            "Casablanca",
            "05 22 00 00 00",
            LogoPng: null),
        DepartmentCode = allDepartments ? null : "BIO",
        DepartmentName = allDepartments ? null : new MultilingualText("Biochimie", "الكيمياء الحيوية", "Biochemistry"),
        InstrumentCode = allDepartments ? null : "AU480",
        InstrumentName = allDepartments ? null : new MultilingualText("Beckman AU480"),
        PrintedAt = PrintedAt,
        PrintedBy = "Benani Youssef",
        Lines =
        [
            Line("CASA-261013-0007", OrderPriority.VitalEmergency, "IONO", "Ionogramme sanguin", 20,
                 Parameter("NA", "Sodium", "mmol/L"),
                 Parameter("K", "Potassium", "mmol/L"),
                 Parameter("CL", "Chlorures", "mmol/L")) with
            {
                SampleDefects = SampleDefect.Haemolysed
            },
            Line("CASA-261013-0011", OrderPriority.Urgent, "TROP", "Troponine I ultrasensible", 12,
                 Parameter("TNI", "Troponine I", "ng/L")),
            Line("CASA-261012-0042", OrderPriority.Routine, "GLY", "Glycémie à jeun", 180,
                 Parameter("GLU", "Glucose", "g/L")),
            Line("CASA-261012-0043", OrderPriority.Routine, "BL", "Bilan lipidique", 175,
                 Parameter("CT", "Cholestérol total", "g/L"),
                 Parameter("TG", "Triglycérides", "g/L"),
                 Parameter("HDL", "HDL-cholestérol", "g/L")) with
            {
                PatientNameArabic = null,
                Gender = Gender.Male,
                AgeYears = null
            }
        ]
    };

    private static (QuestPdfBenchWorksheetRenderer Renderer, JsonLocalizer Localizer) Create()
    {
        var localizer = new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true });

        var renderer = new QuestPdfBenchWorksheetRenderer(new ReportingOptions
        {
            // No fonts beside the test binaries: QuestPDF falls back to its own.
            FontDirectory = Path.Combine(AppContext.BaseDirectory, "no-fonts-here")
        }, localizer);

        return (renderer, localizer);
    }

    private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 4);

    [Theory]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void Worksheet_with_urgent_and_routine_lines_renders_as_a_pdf(AppLanguage language)
    {
        var (renderer, localizer) = Create();

        var pdf = renderer.Render(Sample(), language);

        pdf.Should().NotBeEmpty();
        Header(pdf).Should().Be("%PDF");
        localizer.UnresolvedKeysSeen.Should().BeEmpty();
    }

    [Fact]
    public void Worksheet_for_every_department_and_cut_short_still_renders()
    {
        var (renderer, localizer) = Create();

        var pdf = renderer.Render(Sample(allDepartments: true) with { IsTruncated = true, OnlyUrgent = true },
                                  AppLanguage.French);

        Header(pdf).Should().Be("%PDF");
        localizer.UnresolvedKeysSeen.Should().BeEmpty();
    }

    [Fact]
    public void Urgent_lines_are_counted_whatever_their_level_of_urgency() =>
        Sample().UrgentCount.Should().Be(2);

    [Fact]
    public void A_line_is_overdue_only_once_its_due_time_has_passed()
    {
        var lines = Sample().Lines;

        lines[0].IsOverdue(PrintedAt).Should().BeTrue();
        lines[2].IsOverdue(PrintedAt).Should().BeFalse();
    }
}
