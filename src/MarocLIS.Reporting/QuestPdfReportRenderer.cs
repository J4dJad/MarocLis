using MarocLIS.Application.Reports;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Results;
using MarocLIS.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

public sealed class ReportingOptions
{
    public const string SectionName = "Reporting";

    /// <summary>
    /// QuestPDF licence type as a string: <c>Community</c>, <c>Professional</c> or
    /// <c>Enterprise</c>. Read from configuration rather than hard-coded — see LICENSING.md;
    /// the Community licence is not available to an organisation above 1 M USD revenue.
    /// </summary>
    public string QuestPdfLicense { get; set; } = "Community";

    /// <summary>Folder holding the TTF fonts, default <c>Assets/Fonts</c> beside the executable.</summary>
    public string FontDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

    /// <summary>Font used for Latin text.</summary>
    public string LatinFontFamily { get; set; } = "Carlito";

    /// <summary>
    /// Font used for Arabic. Must contain full Arabic coverage with contextual forms — Amiri
    /// (SIL OFL) or Noto Naskh Arabic are both suitable.
    /// </summary>
    public string ArabicFontFamily { get; set; } = "Amiri";

    /// <summary>Prints a "SPECIMEN" watermark; used while a laboratory is still validating layouts.</summary>
    public bool DraftWatermark { get; set; }

    /// <summary>Include the antériorité column when a previous value exists.</summary>
    public bool ShowPreviousValues { get; set; } = true;
}

/// <summary>
/// Renders a <see cref="ReportModel"/> to PDF.
/// <para>
/// The layout decisions that matter clinically, rather than aesthetically:
/// </para>
/// <list type="bullet">
/// <item>Abnormal values are marked by a <b>symbol and bold weight</b>, not by colour alone.
/// Laboratory reports are routinely printed on monochrome laser printers and faxed, and a
/// colour-only flag disappears on both.</item>
/// <item>Latin tokens inside Arabic text are wrapped in Unicode bidirectional isolates, so
/// <c>mg/dL</c> and reference intervals do not render with their slash and hyphen on the wrong
/// side.</item>
/// <item>The reference interval printed is the one <em>frozen onto the result</em>, not the
/// catalogue's current one — a reprint has to show what the clinician was actually told.</item>
/// <item>A partial or amended report says so in its header, at full size. A corrected report that
/// does not announce itself is more dangerous than no report.</item>
/// </list>
/// </summary>
public sealed class QuestPdfReportRenderer : IReportRenderer
{
    private readonly ReportingOptions _options;
    private readonly ILocalizer _localizer;
    private static bool _initialised;
    private static readonly object InitLock = new();

    public QuestPdfReportRenderer(ReportingOptions options, ILocalizer localizer)
    {
        _options = options;
        _localizer = localizer;
        Initialise(options);
    }

    /// <summary>
    /// Sets the licence and registers the fonts, once per process. Shared with
    /// <see cref="QuestPdfInvoiceRenderer"/> so the two renderers cannot disagree on either.
    /// </summary>
    internal static void Initialise(ReportingOptions options)
    {
        lock (InitLock)
        {
            if (_initialised) return;

            QuestPDF.Settings.License = options.QuestPdfLicense switch
            {
                "Professional" => LicenseType.Professional,
                "Enterprise" => LicenseType.Enterprise,
                _ => LicenseType.Community
            };

            // Fonts are registered from disk rather than embedded: an Arabic Naskh font with full
            // coverage is a 400 KB-plus asset the laboratory supplies, and licences differ.
            if (Directory.Exists(options.FontDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(options.FontDirectory, "*.ttf"))
                {
                    using var stream = File.OpenRead(file);
                    QuestPDF.Drawing.FontManager.RegisterFont(stream);
                }
            }

            _initialised = true;
        }
    }

    public byte[] Render(ReportModel model) => BuildDocument(model).GeneratePdf();

    public int CountPages(byte[] pdf)
    {
        // QuestPDF does not read back a generated document. Counting "/Type /Page" occurrences in
        // the raw bytes is crude but reliable enough for the archive record, and avoids pulling in
        // a PDF parser purely to fill one integer.
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf("/Type /Page", index, StringComparison.Ordinal)) >= 0)
        {
            // "/Type /Pages" is the page-tree node, not a page.
            if (index + 11 >= text.Length || text[index + 11] != 's') count++;
            index += 11;
        }

        return Math.Max(1, count);
    }

    private Document BuildDocument(ReportModel model)
    {
        var rtl = model.Language.IsRightToLeft();
        var font = rtl ? _options.ArabicFontFamily : _options.LatinFontFamily;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(14, Unit.Millimetre);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily(font).FontColor(Colors.Black));

                if (rtl) page.ContentFromRightToLeft();

                page.Header().Element(c => ComposeHeader(c, model));
                page.Content().Element(c => ComposeContent(c, model));
                page.Footer().Element(c => ComposeFooter(c, model));
            });
        });
    }

    // ---- Header ---------------------------------------------------------------------------------

    private void ComposeHeader(IContainer container, ReportModel model)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                if (model.LogoPng is { Length: > 0 })
                    row.ConstantItem(24, Unit.Millimetre)
                       .Height(18, Unit.Millimetre)
                       .Image(model.LogoPng).FitArea();

                row.RelativeItem().PaddingHorizontal(4).Column(identity =>
                {
                    identity.Item().Text(model.LaboratoryName)
                            .FontSize(13).Bold();

                    if (!string.IsNullOrWhiteSpace(model.LaboratoryLegalName)
                        && model.LaboratoryLegalName != model.LaboratoryName)
                        identity.Item().Text(model.LaboratoryLegalName).FontSize(8);

                    var address = string.Join(" — ", new[] { model.LaboratoryAddress, model.LaboratoryCity }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));

                    if (address.Length > 0) identity.Item().Text(address).FontSize(8);

                    var contact = string.Join("   ", new[]
                    {
                        model.LaboratoryPhone is { Length: > 0 } ? Isolate($"Tél. {model.LaboratoryPhone}", model) : null,
                        model.LaboratoryEmail
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));

                    if (contact.Length > 0) identity.Item().Text(contact).FontSize(8);

                    // Loi n° 12-01: the report identifies the authorised laboratory.
                    if (!string.IsNullOrWhiteSpace(model.MinistryAuthorizationNumber))
                        identity.Item().Text(Isolate(
                            $"{Label("Report.AuthorizationNumber", model)} : {model.MinistryAuthorizationNumber}",
                            model)).FontSize(7.5f);

                    if (model.IsAccredited && !string.IsNullOrWhiteSpace(model.AccreditationNumber))
                        identity.Item().Text(Isolate(
                            $"ISO 15189 — {model.AccreditationNumber}", model)).FontSize(7.5f);
                });

                row.ConstantItem(42, Unit.Millimetre).AlignRight().Column(reference =>
                {
                    reference.Item().AlignRight()
                             .Text(Isolate(model.AccessionNumber, model)).FontSize(12).Bold();

                    reference.Item().AlignRight()
                             .Text($"{Label("Report.Version", model)} {model.VersionNumber}")
                             .FontSize(7.5f);

                    if (model.IsUrgent)
                        reference.Item().AlignRight().PaddingTop(2)
                                 .Text(Label("Priority.Urgent", model).ToUpperInvariant())
                                 .FontSize(9).Bold();
                });
            });

            column.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Black);

            // The document's own nature, at full size — final, partial, or amended.
            column.Item().PaddingTop(4).AlignCenter()
                  .Text(HeaderLabel(model)).FontSize(11).Bold().LetterSpacing(0.05f);

            if (model.AmendmentKind != Domain.Reports.ReportAmendmentKind.None
                && model.AmendmentReason is { } reason)
            {
                column.Item().PaddingTop(3).Border(1).BorderColor(Colors.Black)
                      .Padding(4).Column(amendment =>
                      {
                          amendment.Item().Text($"{Label("Report.AmendmentReason", model)} :")
                                   .FontSize(8).Bold();
                          amendment.Item().Text(reason.For(model.Language)).FontSize(8);
                      });
            }

            column.Item().PaddingTop(5).Element(c => ComposeIdentityBlock(c, model));
        });
    }

    private void ComposeIdentityBlock(IContainer container, ReportModel model)
    {
        container.Border(0.5f).BorderColor(Colors.Grey.Darken1).Padding(5).Row(row =>
        {
            row.RelativeItem().Column(patient =>
            {
                patient.Item().Text(Label("Report_Header_Patient", model)).FontSize(8).Bold();

                // The Arabic form of the name is printed alongside the Latin one whenever it exists:
                // the patient checks their own identity on the document they are handed.
                var name = model.Language == AppLanguage.Arabic && model.PatientNameArabic is { Length: > 0 }
                    ? model.PatientNameArabic
                    : model.PatientName;

                patient.Item().Text(name).FontSize(11).Bold();

                if (model.Language != AppLanguage.Arabic
                    && model.PatientNameArabic is { Length: > 0 }
                    && model.PatientNameArabic != model.PatientName)
                    patient.Item().Text(model.PatientNameArabic).FontSize(9)
                           .FontFamily(_options.ArabicFontFamily);

                patient.Item().Text(Field("Patient.RecordNumber", model.MedicalRecordNumber, model))
                       .FontSize(8);

                var demographics = new List<string> { Label($"Gender.{model.Gender}", model) };

                if (model.DateOfBirth is { } dob)
                    demographics.Add($"{Label("Patient.DateOfBirth", model)} {dob:dd/MM/yyyy}");

                if (model.AgeYears is { } age)
                    demographics.Add($"{age} {Label("Report.Years", model)}");

                patient.Item().Text(Isolate(string.Join("   ", demographics), model)).FontSize(8);

                if (!string.IsNullOrWhiteSpace(model.Cin))
                    patient.Item().Text(Field("Patient.Cin", model.Cin, model)).FontSize(8);

                // Pregnancy changes which reference intervals applied; stating it makes the
                // interpretation on the page self-explanatory.
                if (model.IsPregnant)
                    patient.Item().Text(Label("Patient.IsPregnant", model)).FontSize(8).Bold();
            });

            row.ConstantItem(6);

            row.RelativeItem().Column(order =>
            {
                order.Item().Text(Label("Report_Header_Order", model)).FontSize(8).Bold();

                if (!string.IsNullOrWhiteSpace(model.PrescriberName))
                {
                    order.Item().Text(Field("Order.Prescriber", model.PrescriberName, model)).FontSize(8);
                    if (!string.IsNullOrWhiteSpace(model.PrescriberSpeciality))
                        order.Item().PaddingLeft(6).Text(model.PrescriberSpeciality).FontSize(7.5f);
                }

                if (model.PrescriptionDate is { } prescribed)
                    order.Item().Text(Isolate(
                        $"{Label("Order.PrescriptionDate", model)} : {prescribed:dd/MM/yyyy}", model))
                        .FontSize(8);

                if (model.CollectedAt is { } collected)
                    order.Item().Text(Isolate(
                        $"{Label("Report.CollectedOn", model)} {collected:dd/MM/yyyy HH:mm}", model))
                        .FontSize(8);

                if (model.ReceivedAt is { } received)
                    order.Item().Text(Isolate(
                        $"{Label("Report.SampleReceivedOn", model)} {received:dd/MM/yyyy HH:mm}", model))
                        .FontSize(8);

                order.Item().Text(Isolate(
                    $"{Label("Report.ReleasedAt", model)} {model.ReleasedAt:dd/MM/yyyy HH:mm}", model))
                    .FontSize(8);

                if (!string.IsNullOrWhiteSpace(model.ClinicalInformation))
                    order.Item().PaddingTop(2)
                         .Text($"{Label("Order.ClinicalInformation", model)} : {model.ClinicalInformation}")
                         .FontSize(7.5f).Italic();
            });
        });
    }

    // ---- Content ---------------------------------------------------------------------------------

    private void ComposeContent(IContainer container, ReportModel model)
    {
        container.PaddingTop(6).Column(column =>
        {
            foreach (var section in model.Sections)
            {
                column.Item().PaddingTop(5).Element(c => ComposeSection(c, model, section));
            }

            if (model.Conclusion is { } conclusion
                && !string.IsNullOrWhiteSpace(conclusion.For(model.Language)))
            {
                column.Item().PaddingTop(8).Border(0.5f).BorderColor(Colors.Grey.Darken1)
                      .Padding(5).Column(block =>
                      {
                          block.Item().Text(Label("Result.Conclusion", model)).FontSize(8).Bold();
                          block.Item().PaddingTop(2).Text(conclusion.For(model.Language)).FontSize(9);
                      });
            }

            if (model.Reservations.Count > 0)
            {
                column.Item().PaddingTop(6).Column(block =>
                {
                    block.Item().Text(Label("Report.ReservationNote", model)).FontSize(8).Bold();
                    foreach (var reservation in model.Reservations)
                        block.Item().Text($"• {Isolate(reservation, model)}").FontSize(7.5f);
                });
            }

            column.Item().PaddingTop(10).Element(c => ComposeSignature(c, model));
        });
    }

    private void ComposeSection(IContainer container, ReportModel model, ReportSection section)
    {
        container.Column(column =>
        {
            column.Item().Background(Colors.Grey.Lighten3).Padding(3)
                  .Text(section.DepartmentName.For(model.Language).ToUpperInvariant())
                  .FontSize(9.5f).Bold();

            foreach (var analysis in section.Analyses)
            {
                column.Item().PaddingTop(3).Column(block =>
                {
                    block.Item().Row(row =>
                    {
                        row.RelativeItem().Text(analysis.AnalysisName.For(model.Language))
                           .FontSize(9).Bold();

                        // Accreditation and subcontracting are stated per analysis, because within
                        // one report some analyses are in scope and others are not.
                        var markers = new List<string>();
                        if (analysis.IsAccredited) markers.Add("ISO 15189");
                        if (analysis.IsSubcontracted) markers.Add(Label("Catalog.Subcontracted", model));

                        if (markers.Count > 0)
                            row.AutoItem().AlignBottom()
                               .Text(Isolate(string.Join(" · ", markers), model)).FontSize(7);
                    });

                    if (analysis.Method is { } method
                        && !string.IsNullOrWhiteSpace(method.For(model.Language)))
                        block.Item().Text($"{Label("Result.Method", model)} : {method.For(model.Language)}")
                             .FontSize(7).Italic().FontColor(Colors.Grey.Darken2);

                    block.Item().PaddingTop(2).Element(c => ComposeResultTable(c, model, analysis));
                });
            }
        });
    }

    private void ComposeResultTable(IContainer container, ReportModel model,
                                     ReportAnalysisBlock analysis)
    {
        var showPrevious = _options.ShowPreviousValues
                           && analysis.Lines.Any(l => l.PreviousValue is { Length: > 0 });

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(4);                      // parameter
                columns.ConstantColumn(60);                     // result
                columns.ConstantColumn(16);                     // flag
                columns.ConstantColumn(52);                     // unit
                columns.RelativeColumn(2.4f);                   // reference interval
                if (showPrevious) columns.ConstantColumn(58);   // previous
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), Label("Result.Parameter", model));
                HeaderCell(header.Cell(), Label("Result.Value", model), align: true);
                HeaderCell(header.Cell(), string.Empty);
                HeaderCell(header.Cell(), Label("Result.Unit", model));
                HeaderCell(header.Cell(), Label("Result.ReferenceRange", model));
                if (showPrevious) HeaderCell(header.Cell(), Label("Result.Previous", model), align: true);
            });

            foreach (var line in analysis.Lines)
            {
                if (line.IsHeading)
                {
                    table.Cell().ColumnSpan(showPrevious ? 6u : 5u)
                         .PaddingTop(2).PaddingBottom(1)
                         .Text(line.ParameterName.For(model.Language)).FontSize(8).Bold().Italic();
                    continue;
                }

                var abnormal = line.IsAbnormal;
                var critical = line.IsCritical;

                table.Cell().Element(Cell).PaddingLeft(line.IndentLevel * 8)
                     .Text(line.ParameterName.For(model.Language)).FontSize(8.5f);

                table.Cell().Element(Cell).AlignRight()
                     .Text(text =>
                     {
                         var span = text.Span(Isolate(line.Value, model)).FontSize(8.5f);
                         if (abnormal) span.Bold();
                     });

                // The flag is a glyph, not a colour: these reports are printed in monochrome and
                // faxed, and a colour-only cue is simply lost.
                table.Cell().Element(Cell).AlignCenter()
                     .Text(FlagGlyph(line)).FontSize(8.5f).Bold();

                table.Cell().Element(Cell)
                     .Text(Isolate(line.Unit ?? string.Empty, model)).FontSize(7.5f);

                table.Cell().Element(Cell)
                     .Text(Isolate(line.ReferenceRange ?? string.Empty, model)).FontSize(7.5f);

                if (showPrevious)
                {
                    table.Cell().Element(Cell).AlignRight().Text(text =>
                    {
                        if (line.PreviousValue is { Length: > 0 })
                        {
                            text.Span(Isolate(line.PreviousValue, model)).FontSize(7.5f);
                            if (line.PreviousDate is { } date)
                                text.Line(Isolate($"{date:dd/MM/yy}", model))
                                    .FontSize(6.5f).FontColor(Colors.Grey.Darken1);
                        }
                    });
                }

                // A comment belongs directly under the value it qualifies, not in a footnote pile.
                var comment = line.BiologistComment?.For(model.Language);

                if (!string.IsNullOrWhiteSpace(comment) || !string.IsNullOrWhiteSpace(line.Comment))
                {
                    table.Cell().ColumnSpan(showPrevious ? 6u : 5u)
                         .PaddingLeft(6 + line.IndentLevel * 8).PaddingBottom(2)
                         .Column(notes =>
                         {
                             if (!string.IsNullOrWhiteSpace(comment))
                                 notes.Item().Text(comment).FontSize(7.5f).Italic();

                             if (!string.IsNullOrWhiteSpace(line.Comment))
                                 notes.Item().Text(line.Comment).FontSize(7)
                                      .Italic().FontColor(Colors.Grey.Darken2);
                         });
                }

                if (critical)
                {
                    table.Cell().ColumnSpan(showPrevious ? 6u : 5u)
                         .PaddingLeft(6).PaddingBottom(2)
                         .Text(Label("Report.CriticalCommunicated", model)).FontSize(7).Bold();
                }
            }
        });
    }

    private static void HeaderCell(IContainer cell, string text, bool align = false)
    {
        var container = cell.BorderBottom(0.5f).BorderColor(Colors.Grey.Darken1)
                            .PaddingVertical(2).PaddingHorizontal(2);

        if (align) container = container.AlignRight();

        container.Text(text).FontSize(7.5f).Bold().FontColor(Colors.Grey.Darken3);
    }

    private static IContainer Cell(IContainer container) =>
        container.BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2)
                 .PaddingVertical(1.5f).PaddingHorizontal(2);

    /// <summary>
    /// The abnormality marker. Doubled arrows for a critical value, single for out-of-range —
    /// readable on a monochrome print and unambiguous when dictated over the phone.
    /// </summary>
    private static string FlagGlyph(ReportLine line) => line.Interpretation switch
    {
        ResultInterpretation.CriticalLow => "↓↓",
        ResultInterpretation.CriticalHigh => "↑↑",
        ResultInterpretation.Low => "↓",
        ResultInterpretation.High => "↑",
        ResultInterpretation.Abnormal => "*",
        _ => string.Empty
    };

    // ---- Signature and footer -------------------------------------------------------------------

    private void ComposeSignature(IContainer container, ReportModel model)
    {
        container.Row(row =>
        {
            row.RelativeItem();

            row.ConstantItem(70, Unit.Millimetre).Column(block =>
            {
                block.Item().AlignCenter()
                     .Text(Label("Report_Header_Signature", model)).FontSize(8).Bold();

                if (model.SignaturePng is { Length: > 0 })
                    block.Item().AlignCenter().Height(16, Unit.Millimetre)
                         .Image(model.SignaturePng).FitHeight();
                else
                    block.Item().Height(16, Unit.Millimetre);

                var name = model.Language == AppLanguage.Arabic
                           && model.SignatoryNameArabic is { Length: > 0 }
                    ? model.SignatoryNameArabic
                    : model.SignatoryName;

                block.Item().AlignCenter().Text(name).FontSize(9).Bold();

                block.Item().AlignCenter()
                     .Text(Label($"Qualification.{model.SignatoryQualification}", model))
                     .FontSize(7.5f);

                // Loi n° 12-01: the biologist's Ordre registration number identifies who released
                // the result, and is not optional on the document.
                block.Item().AlignCenter().Text(Isolate(
                        $"{Label("Validation.OrderNumber", model)} : {model.SignatoryOrderNumber}",
                        model)).FontSize(7.5f);
            });
        });
    }

    private void ComposeFooter(IContainer container, ReportModel model)
    {
        container.PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Darken1)
                 .PaddingTop(2).Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Text(Label("Report.ConfidentialityNote", model))
                   .FontSize(6.5f).FontColor(Colors.Grey.Darken2);

                row.AutoItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(7).FontColor(Colors.Grey.Darken2));
                    text.Span($"{Label("Report.Page", model).Split('{')[0].Trim()} ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });

            column.Item().AlignCenter().PaddingTop(1)
                  .Text(Label("Report.EndOfReport", model))
                  .FontSize(6.5f).FontColor(Colors.Grey.Darken1);
        });
    }

    // ---- Localisation helpers -------------------------------------------------------------------

    private string Label(string key, ReportModel model) => _localizer.Get(key, model.Language);

    private string Field(string labelKey, string? value, ReportModel model) =>
        Isolate($"{Label(labelKey, model)} : {value}", model);

    /// <summary>
    /// Wraps a value in bidirectional isolates when the document is right-to-left, so Latin tokens
    /// keep their own direction. Without it, "mg/dL" and "0,70 - 1,10" render with their separators
    /// on the wrong side inside Arabic text.
    /// </summary>
    private static string Isolate(string value, ReportModel model) =>
        CultureManager.IsolateIfRtl(value, model.Language);

    private string HeaderLabel(ReportModel model) =>
        (model.Completeness, model.AmendmentKind) switch
        {
            (_, not Domain.Reports.ReportAmendmentKind.None) => Label("Report.Amended", model),
            (Domain.Reports.ReportCompleteness.Partial, _) => Label("Report.Partial", model),
            _ => Label("Report.Final", model)
        };
}
