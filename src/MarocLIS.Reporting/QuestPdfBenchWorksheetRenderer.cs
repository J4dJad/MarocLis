using System.Globalization;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using MarocLIS.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>
/// Renders the fiche de paillasse to PDF.
/// <para>
/// The layout decisions follow from where the sheet is used — on a bench, next to an analyser,
/// written on with a pen:
/// </para>
/// <list type="bullet">
/// <item><b>Landscape</b>, because the line has to carry the file, the tube, the patient and the
/// analysis <em>and</em> leave room to write. Portrait forces the result box down to a width no
/// handwritten value fits in.</item>
/// <item><b>One empty box per parameter</b>, beside its name and unit. A value written in the margin
/// next to the right analysis but the wrong parameter is a transcription error waiting to happen,
/// and an aligned box is what prevents it.</item>
/// <item><b>Urgent work is marked by a bordered, bold label and a group of its own</b>, not by a
/// tint alone: these sheets come out of monochrome laser printers, and a colour-only urgency
/// disappears on all of them.</item>
/// <item><b>The letterhead, the department, the print time and the operator repeat on every
/// page</b>, with the page number: a loose page on a bench still has to say which sheet it belongs
/// to and how old it is.</item>
/// </list>
/// </summary>
public sealed class QuestPdfBenchWorksheetRenderer : IBenchWorksheetRenderer
{
    // Widths of the columns that hold the handwriting. Shared between the outer table's header and
    // the inner per-parameter table, so the boxes sit exactly under their heading.
    private const float UnitWidth = 50;
    private const float ResultWidth = 92;

    private static readonly string[] SignOffKeys =
        ["BenchSheet.PerformedBy", "BenchSheet.PerformedAt", "BenchSheet.CheckedBy"];

    private readonly ReportingOptions _options;
    private readonly ILocalizer _localizer;

    public QuestPdfBenchWorksheetRenderer(ReportingOptions options, ILocalizer localizer)
    {
        _options = options;
        _localizer = localizer;
        QuestPdfReportRenderer.Initialise(options);
    }

    public byte[] Render(BenchWorksheet worksheet, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(worksheet);

        var rtl = language.IsRightToLeft();
        var font = rtl ? _options.ArabicFontFamily : _options.LatinFontFamily;
        var context = new Context(worksheet, language, language.ToCulture());

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(10, Unit.Millimetre);
                page.DefaultTextStyle(x => x.FontSize(8.5f)
                                            .FontFamily(font, _options.ArabicFontFamily)
                                            .FontColor(Colors.Black));

                if (rtl) page.ContentFromRightToLeft();

                page.Header().Element(c => ComposeHeader(c, context));
                page.Content().PaddingTop(5).Element(c => ComposeContent(c, context));
                page.Footer().Element(c => ComposeFooter(c, context));
            });
        }).GeneratePdf();
    }

    // ---- Header ---------------------------------------------------------------------------------

    private void ComposeHeader(IContainer container, Context context)
    {
        var sheet = context.Sheet;
        var laboratory = sheet.Laboratory;

        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                if (laboratory.LogoPng is { Length: > 0 })
                    row.ConstantItem(24, Unit.Millimetre)
                       .Height(16, Unit.Millimetre)
                       .Image(laboratory.LogoPng).FitArea();

                row.RelativeItem().PaddingHorizontal(4).Column(identity =>
                {
                    var name = laboratory.Name.For(context.Language);
                    identity.Item().Text(name).FontSize(13).Bold();

                    if (!string.IsNullOrWhiteSpace(laboratory.LegalName) && laboratory.LegalName != name)
                        identity.Item().Text(laboratory.LegalName).FontSize(8);

                    var address = string.Join(" — ", new[]
                    {
                        laboratory.Address?.For(context.Language), laboratory.City
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));

                    if (address.Length > 0) identity.Item().Text(address).FontSize(8);

                    if (!string.IsNullOrWhiteSpace(laboratory.Phone))
                        identity.Item().Text(Isolate($"Tél. {laboratory.Phone}", context)).FontSize(8);
                });

                row.ConstantItem(105, Unit.Millimetre).Column(reference =>
                {
                    AlignEnd(reference.Item(), context)
                             .Text(Label("BenchSheet.Title", context).ToUpper(context.Culture))
                             .FontSize(14).Bold().LetterSpacing(0.05f);

                    var department = sheet.DepartmentName is { } departmentName
                        ? $"{departmentName.For(context.Language)} ({Isolate(sheet.DepartmentCode, context)})"
                        : Label("BenchSheet.AllDepartments", context);

                    AlignEnd(reference.Item(), context).Text(text =>
                    {
                        text.Span($"{Label("BenchSheet.Department", context)} : ").FontSize(9);
                        text.Span(department).FontSize(11).Bold();
                    });

                    if (sheet.InstrumentName is { } instrumentName)
                        AlignEnd(reference.Item(), context).Text(text =>
                        {
                            text.Span($"{Label("BenchSheet.Instrument", context)} : ").FontSize(9);
                            text.Span($"{instrumentName.For(context.Language)} ({Isolate(sheet.InstrumentCode, context)})")
                                .FontSize(9.5f).Bold();
                        });

                    if (sheet.OnlyUrgent)
                        AlignEnd(reference.Item(), context).PaddingTop(1)
                                 .Text(Label("BenchSheet.OnlyUrgent", context).ToUpper(context.Culture))
                                 .FontSize(8.5f).Bold();
                });
            });

            column.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Black);

            // What the sheet is a snapshot of, and of when: repeated on every page, because a loose
            // page is still read as the current state of the bench.
            column.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8));
                    text.Span($"{Label("BenchSheet.PrintedAt", context)} ");
                    text.Span(Isolate(context.DateTime(sheet.PrintedAt), context)).SemiBold();
                    text.Span($"   ·   {Label("BenchSheet.PrintedBy", context)} ");
                    text.Span(sheet.PrintedBy).SemiBold();
                });

                row.AutoItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8.5f));
                    text.Span(_localizer.Format(context.Language, "BenchSheet.Total",
                                                sheet.Lines.Count)).Bold();

                    if (sheet.UrgentCount > 0)
                        text.Span("   ·   " + _localizer.Format(context.Language, "BenchSheet.UrgentTotal",
                                                                 sheet.UrgentCount)).Bold();
                });
            });
        });
    }

    // ---- Content --------------------------------------------------------------------------------

    private void ComposeContent(IContainer container, Context context)
    {
        var sheet = context.Sheet;

        container.Column(column =>
        {
            if (sheet.IsTruncated)
                column.Item().PaddingBottom(4).Border(1).BorderColor(Colors.Black).Padding(4)
                      .Text(Label("BenchSheet.Truncated", context)).FontSize(8.5f).Bold();

            column.Item().Element(c => ComposeTable(c, context));

            column.Item().PaddingTop(10).ShowEntire().Element(c => ComposeSignOff(c, context));
        });
    }

    private void ComposeTable(IContainer container, Context context)
    {
        var sheet = context.Sheet;
        var showDepartment = sheet.DepartmentName is null;

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(18);              // line number
                columns.ConstantColumn(60);              // priority
                columns.ConstantColumn(96);              // accession and tube
                columns.RelativeColumn(2.2f);            // patient
                columns.ConstantColumn(42);              // sex and age
                columns.RelativeColumn(2.2f);            // analysis
                columns.RelativeColumn(2.4f);            // parameter
                columns.ConstantColumn(UnitWidth);       // unit
                columns.ConstantColumn(ResultWidth);     // result box
                columns.ConstantColumn(62);              // received and due
                columns.RelativeColumn(1.8f);            // observations
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "#");
                HeaderCell(header.Cell(), Label("BenchSheet.Priority", context));
                HeaderCell(header.Cell(), $"{Label("BenchSheet.Accession", context)} / {Label("BenchSheet.Tube", context)}");
                HeaderCell(header.Cell(), Label("BenchSheet.Patient", context));
                HeaderCell(header.Cell(), Label("BenchSheet.SexAge", context));
                HeaderCell(header.Cell(), Label("BenchSheet.Analysis", context));
                HeaderCell(header.Cell(), Label("BenchSheet.Parameter", context));
                HeaderCell(header.Cell(), Label("BenchSheet.Unit", context));
                HeaderCell(header.Cell(), Label("BenchSheet.Result", context));
                HeaderCell(header.Cell(), $"{Label("BenchSheet.Received", context)} / {Label("BenchSheet.Due", context)}");
                HeaderCell(header.Cell(), Label("BenchSheet.Observations", context));
            });

            var number = 0;

            // Urgent work as a group of its own, headed and counted, then the routine. The query
            // already orders them so; the headings make the boundary visible from across a bench.
            foreach (var group in sheet.Lines.GroupBy(l => l.IsUrgent).OrderByDescending(g => g.Key))
            {
                var heading = group.Key
                    ? Label("BenchSheet.UrgentGroup", context)
                    : Label("BenchSheet.RoutineGroup", context);

                table.Cell().ColumnSpan(11)
                     .Background(group.Key ? Colors.Red.Lighten4 : Colors.Grey.Lighten3)
                     .BorderBottom(0.75f).BorderColor(Colors.Grey.Darken2)
                     .PaddingVertical(2).PaddingHorizontal(4)
                     .Text($"{heading.ToUpper(context.Culture)}  ({group.Count()})")
                     .FontSize(8.5f).Bold().LetterSpacing(0.04f);

                foreach (var line in group)
                    ComposeLine(table, line, ++number, showDepartment, context);
            }
        });
    }

    private void ComposeLine(TableDescriptor table, BenchWorksheetLine line, int number,
                             bool showDepartment, Context context)
    {
        var urgent = line.IsUrgent;
        var overdue = line.IsOverdue(context.Sheet.PrintedAt);

        // The tint is inset half a point from the rules. A rule is stroked across the row boundary,
        // and a tint filled right up to it lets the next row paint out the separator — urgent lines
        // then run together on the page, which is exactly where they must not.
        IContainer Cell(IContainer cell)
        {
            cell = cell.BorderBottom(0.5f).BorderColor(Colors.Grey.Darken1).PaddingVertical(0.5f);
            if (urgent) cell = cell.Background(Colors.Red.Lighten5);

            return cell.PaddingVertical(2.5f).PaddingHorizontal(2.5f);
        }

        table.Cell().Element(Cell).Text(number.ToString(context.Culture)).FontSize(7.5f)
             .FontColor(Colors.Grey.Darken2);

        table.Cell().Element(Cell).Element(c =>
        {
            if (!urgent)
            {
                c.Text(Label("Priority.Routine", context)).FontSize(7.5f).FontColor(Colors.Grey.Darken2);
                return;
            }

            // Aligned top-left so the frame hugs the word instead of stretching down a tall panel.
            AlignStart(c, context).AlignTop().Border(1.2f).BorderColor(Colors.Black).Background(Colors.White)
             .PaddingHorizontal(2).PaddingVertical(1)
             .Text(PriorityLabel(line.Priority, context).ToUpper(context.Culture))
             .FontSize(7.5f).Bold().FontColor(Colors.Red.Darken4);
        });

        table.Cell().Element(Cell).Column(identity =>
        {
            identity.Item().Text(Isolate(line.AccessionNumber, context)).FontSize(9).Bold();
            identity.Item().Text(Isolate(line.SampleBarcode, context)).FontSize(8)
                    .LetterSpacing(0.04f);
        });

        table.Cell().Element(Cell).Column(patient =>
        {
            var name = context.Language == AppLanguage.Arabic && line.PatientNameArabic is { Length: > 0 }
                ? line.PatientNameArabic
                : line.PatientName;

            patient.Item().Text(name).FontSize(9).SemiBold();

            // The Arabic form beside the Latin one: a tube can carry either, and the technician
            // checks the name on the tube against the sheet.
            if (context.Language != AppLanguage.Arabic
                && line.PatientNameArabic is { Length: > 0 }
                && line.PatientNameArabic != line.PatientName)
                patient.Item().Text(line.PatientNameArabic).FontSize(8)
                       .FontFamily(_options.ArabicFontFamily);

            if (line.SampleDefects != SampleDefect.None)
                patient.Item().PaddingTop(1).Text($"{Label("BenchSheet.Reservation", context)} : {DescribeDefects(line.SampleDefects, context)}")
                       .FontSize(7).Bold();
        });

        table.Cell().Element(Cell).Text(text =>
        {
            text.Span(SexLabel(line.Gender, context)).FontSize(8.5f).SemiBold();

            if (line.AgeYears is { } age)
                text.Span($"\n{age} {Label("Report.Years", context)}").FontSize(8);
        });

        table.Cell().Element(Cell).Column(analysis =>
        {
            analysis.Item().Text(text =>
            {
                text.Span(Isolate(line.AnalysisCode, context)).FontSize(8.5f).Bold();

                if (showDepartment)
                    text.Span($"  {Isolate(line.DepartmentCode, context)}").FontSize(7)
                        .FontColor(Colors.Grey.Darken2);
            });

            analysis.Item().Text(line.AnalysisName.For(context.Language)).FontSize(8.5f);
        });

        // Parameter, unit and the box to write in, as one inner table across the three columns so
        // each box stays on the line of its own parameter however long a name wraps.
        table.Cell().ColumnSpan(3).Element(Cell).Table(inner =>
        {
            inner.ColumnsDefinition(columns =>
            {
                // The outer cell's horizontal padding comes off the box, so it starts under the
                // heading's text rather than five points to its left.
                columns.RelativeColumn();
                columns.ConstantColumn(UnitWidth);
                columns.ConstantColumn(ResultWidth - 5);
            });

            foreach (var parameter in line.Parameters)
            {
                inner.Cell().PaddingVertical(1.5f).PaddingRight(2).AlignMiddle()
                     .Text(parameter.Name.For(context.Language)).FontSize(8.5f);

                inner.Cell().PaddingVertical(1.5f).AlignMiddle()
                     .Text(Isolate(parameter.UnitSymbol ?? string.Empty, context)).FontSize(7.5f)
                     .FontColor(Colors.Grey.Darken3);

                inner.Cell().PaddingVertical(1.5f).Height(14)
                     .Border(0.75f).BorderColor(Colors.Grey.Darken3).Background(Colors.White);
            }
        });

        table.Cell().Element(Cell).Column(timing =>
        {
            timing.Item().Text(line.ReceivedAt is { } received
                ? Isolate(context.Time(received), context)
                : "—").FontSize(8);

            if (line.DueAt is { } due)
            {
                var dueText = timing.Item().Text(Isolate(context.Time(due), context)).FontSize(8).Bold();

                // Already late when the sheet was printed: said in words as well as in weight.
                if (overdue)
                {
                    dueText.FontColor(Colors.Red.Darken3);
                    timing.Item().Text(Label("BenchSheet.Overdue", context).ToUpper(context.Culture))
                          .FontSize(7).Bold().FontColor(Colors.Red.Darken3);
                }
            }
        });

        // Observations: left blank for the pen, ruled so it reads as a field rather than a margin.
        table.Cell().Element(Cell).PaddingTop(10).Column(observations =>
        {
            for (var i = 0; i < Math.Max(1, Math.Min(3, line.Parameters.Count / 3)); i++)
                observations.Item().PaddingTop(i == 0 ? 0 : 11)
                            .LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
        });
    }

    /// <summary>
    /// Who performed the run, and who checked it. ISO 15189 §7.3.7 expects the examination to be
    /// traceable to the person who carried it out, and a sheet that comes back from the bench
    /// signed is the record of that until the values are keyed in.
    /// </summary>
    private void ComposeSignOff(IContainer container, Context context)
    {
        container.Row(row =>
        {
            foreach (var key in SignOffKeys)
            {
                row.RelativeItem().PaddingHorizontal(4).Column(block =>
                {
                    block.Item().Text(Label(key, context)).FontSize(8).Bold();
                    block.Item().Height(16).BorderBottom(0.75f).BorderColor(Colors.Grey.Darken2);
                });
            }
        });
    }

    // ---- Footer ---------------------------------------------------------------------------------

    private void ComposeFooter(IContainer container, Context context)
    {
        var sheet = context.Sheet;

        container.PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Darken1)
                 .PaddingTop(2).Row(row =>
        {
            var department = sheet.DepartmentName?.For(context.Language)
                             ?? Label("BenchSheet.AllDepartments", context);

            row.RelativeItem().Text(
                    $"{Label("BenchSheet.Title", context)} — {department} — " +
                    $"{Isolate(context.DateTime(sheet.PrintedAt), context)}   ·   " +
                    Label("BenchSheet.InternalUse", context))
               .FontSize(6.5f).FontColor(Colors.Grey.Darken2);

            row.AutoItem().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(7).FontColor(Colors.Grey.Darken2));
                text.Span($"{Label("BenchSheet.Page", context)} ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });
    }

    // ---- Shared pieces --------------------------------------------------------------------------

    private static void HeaderCell(IContainer cell, string text) =>
        cell.Background(Colors.Grey.Lighten3)
            .BorderBottom(1).BorderColor(Colors.Black)
            .PaddingVertical(3).PaddingHorizontal(2.5f)
            .Text(text).FontSize(7.5f).Bold();

    private string PriorityLabel(OrderPriority priority, Context context) => priority switch
    {
        OrderPriority.VitalEmergency => Label("Priority.VitalEmergency", context),
        OrderPriority.Urgent => Label("Priority.Urgent", context),
        _ => Label("Priority.Routine", context)
    };

    /// <summary>A single letter, as the column is read at a glance; the full word is on the report.</summary>
    private string SexLabel(Gender gender, Context context) => gender switch
    {
        Gender.Male => Label("BenchSheet.SexMale", context),
        Gender.Female => Label("BenchSheet.SexFemale", context),
        _ => "—"
    };

    private string DescribeDefects(SampleDefect defects, Context context) =>
        string.Join(", ", Enum.GetValues<SampleDefect>()
            .Where(d => d != SampleDefect.None && defects.HasFlag(d))
            .Select(d => _localizer.Get($"SampleDefect.{d}", context.Language)));

    /// <summary>
    /// The trailing edge of the line: right in French and English, left on an Arabic page.
    /// QuestPDF mirrors the order of rows and columns for a right-to-left page but not an explicit
    /// alignment, so a header block pinned "right" ends up pointing at the middle of the page.
    /// </summary>
    private static IContainer AlignEnd(IContainer container, Context context) =>
        context.Language.IsRightToLeft() ? container.AlignLeft() : container.AlignRight();

    private static IContainer AlignStart(IContainer container, Context context) =>
        context.Language.IsRightToLeft() ? container.AlignRight() : container.AlignLeft();

    private string Label(string key, Context context) => _localizer.Get(key, context.Language);

    /// <summary>
    /// Wraps Latin tokens — accession numbers, barcodes, units, times — in bidirectional isolates
    /// on an Arabic sheet, so "CASA-261012-0042" and "mmol/L" keep their own order.
    /// </summary>
    private static string Isolate(string? value, Context context) =>
        CultureManager.IsolateIfRtl(value, context.Language);

    /// <summary>The worksheet and the language and culture it is printed in, carried together.</summary>
    private sealed record Context(BenchWorksheet Sheet, AppLanguage Language, CultureInfo Culture)
    {
        public string DateTime(DateTimeOffset value) =>
            value.ToString("dd'/'MM'/'yyyy HH':'mm", Culture);

        /// <summary>Day and time: a backlog spans more than one day, and a time alone hides that.</summary>
        public string Time(DateTimeOffset value) =>
            value.ToString("dd'/'MM HH':'mm", Culture);
    }
}
