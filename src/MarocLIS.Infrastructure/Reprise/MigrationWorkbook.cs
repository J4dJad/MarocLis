using System.Globalization;
using ClosedXML.Excel;
using MarocLIS.Application.Migration;
using MarocLIS.Domain.Migration;

namespace MarocLIS.Infrastructure.Reprise;

/// <summary>
/// Reads .xlsx workbooks for the take-over and writes the templates, with ClosedXML (already the
/// solution's spreadsheet library). A date cell is read as a date and a number cell as a number,
/// whatever the format the old application gave them, so the validation sees the same text as from
/// a CSV or a database.
/// </summary>
public sealed class MigrationWorkbook : IMigrationWorkbook
{
    public IReadOnlyList<string> SheetNames(string path)
    {
        using var workbook = Open(path);
        return [.. workbook.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).Select(w => w.Name)];
    }

    public SourceTable Read(string path, string? sheet, int maxRows = int.MaxValue)
    {
        using var workbook = Open(path);

        var worksheet = sheet is null
            ? workbook.Worksheets.First(w => w.Visibility == XLWorksheetVisibility.Visible)
            : workbook.Worksheets.TryGetWorksheet(sheet, out var named) ? named : throw new ArgumentException("Migration_SheetNotFound");

        var used = worksheet.RangeUsed();
        if (used is null) return SourceTable.Empty;

        var firstRow = used.FirstRow().RowNumber();
        var lastRow = used.LastRow().RowNumber();
        var firstColumn = used.FirstColumn().ColumnNumber();
        var lastColumn = used.LastColumn().ColumnNumber();

        var columns = new List<string>();
        for (var c = firstColumn; c <= lastColumn; c++)
        {
            var header = worksheet.Cell(firstRow, c).GetFormattedString().Trim();
            if (header.Length == 0) header = $"Colonne{c - firstColumn + 1}";
            var unique = header;
            for (var n = 2; columns.Contains(unique, StringComparer.OrdinalIgnoreCase); n++) unique = $"{header}_{n}";
            columns.Add(unique);
        }

        var rows = new List<string?[]>();
        var truncated = false;

        for (var r = firstRow + 1; r <= lastRow; r++)
        {
            var values = new string?[columns.Count];
            var any = false;

            for (var c = firstColumn; c <= lastColumn; c++)
            {
                var text = Text(worksheet.Cell(r, c));
                values[c - firstColumn] = text;
                any |= text is not null;
            }

            if (!any) continue;
            if (rows.Count >= maxRows) { truncated = true; break; }
            rows.Add(values);
        }

        return new SourceTable(columns, rows, truncated);
    }

    private static string? Text(IXLCell cell)
    {
        var value = cell.Value;
        string? text = value.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.DateTime => SourceTable.ToText(value.GetDateTime()),
            XLDataType.Number => value.GetNumber().ToString("R", CultureInfo.InvariantCulture),
            XLDataType.Boolean => value.GetBoolean() ? "1" : "0",
            XLDataType.TimeSpan => value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
            XLDataType.Error => null,
            _ => value.GetText()
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static XLWorkbook Open(string path)
    {
        // Shared read: the file is often still open in Excel on the same workstation.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    public void WriteTemplate(DataMigrationKind kind, string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName(kind));
        var fields = MigrationSchema.Fields(kind);
        var example = MigrationTemplates.Example(kind);

        for (var i = 0; i < fields.Count; i++)
        {
            var header = sheet.Cell(1, i + 1);
            header.Value = fields[i].Header;
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = fields[i].Required ? XLColor.FromHtml("#DDEFEA") : XLColor.FromHtml("#F2F4F7");

            // Text cells, so a CIN, a phone or a file number keeps its leading zeros.
            sheet.Column(i + 1).Style.NumberFormat.Format = "@";
            if (i < example.Count) sheet.Cell(2, i + 1).SetValue(example[i]);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, fields.Count).AdjustToContents(1, 2, 12, 40);
        workbook.SaveAs(path);
    }

    private static string SheetName(DataMigrationKind kind) => kind switch
    {
        DataMigrationKind.Patients => "Patients",
        DataMigrationKind.Prescribers => "Prescripteurs",
        DataMigrationKind.Catalogue => "Catalogue",
        DataMigrationKind.History => "Antériorités",
        DataMigrationKind.OpeningBalances => "Créances",
        _ => "Données"
    };
}
