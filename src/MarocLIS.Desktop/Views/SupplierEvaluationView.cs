using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The yearly supplier evaluation (ISO 15189 §6.8), computed from what the laboratory recorded:
/// deliveries against the date the order asked for, refused deliveries, non-conformities raised
/// against the supplier. The proposed score can be applied to the supplier's record, and the year
/// prints as a PDF or exports as CSV for the management review file.
/// </summary>
public sealed class SupplierEvaluationView : LocalizedView
{
    private readonly SupplierEvaluationService _evaluation;
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<SupplierEvaluationView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _heading = new();
    private readonly Label _yearLabel = new();
    private readonly NumericUpDown _year = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _apply = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _pdf = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _csv = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _formula = new();

    private IReadOnlyList<SupplierEvaluationRow> _rows = [];

    public SupplierEvaluationView(SupplierEvaluationService evaluation, IListPdfRenderer lists,
                                  IDocumentOutput output, IUserDialogs dialogs, ILocalizer localizer,
                                  ICultureManager cultureManager,
                                  ILogger<SupplierEvaluationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _evaluation = evaluation;
        _lists = lists;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();

        _refresh.Click += async (_, _) => await ActivateAsync();
        _year.ValueChanged += async (_, _) => await ActivateAsync();
        _apply.Click += async (_, _) => await RunBusyAsync(ApplyAsync);
        _pdf.Click += async (_, _) => await RunBusyAsync(PrintAsync);
        _csv.Click += async (_, _) => await RunBusyAsync(ExportAsync);
        _grid.SelectionChanged += (_, _) => _apply.Enabled = Current?.Kpis.ProposedScore is not null;

        // Whoever manages suppliers evaluates them.
        PermissionGate.Require(Permissions.Inventory.ManagePurchaseOrders, _apply, _pdf, _csv, _refresh);
    }

    public override string Title => T("Nav.SupplierEvaluation");

    private SupplierEvaluationRow? Current => _grid.CurrentRow?.Tag as SupplierEvaluationRow;

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        _yearLabel.Font = UiTheme.Small;
        _yearLabel.ForeColor = UiTheme.TextSecondary;
        _yearLabel.AutoSize = true;
        _yearLabel.Location = new Point(0, 34);

        _year.Minimum = 2000;
        _year.Maximum = 2100;
        _year.Value = DateTime.Today.Year;
        _year.Bounds = new Rectangle(70, 30, 80, UiTheme.InputHeight);

        _refresh.Bounds = new Rectangle(170, 26, 110, UiTheme.ButtonHeight);
        _apply.Bounds = new Rectangle(290, 26, 230, UiTheme.ButtonHeight);
        _pdf.Bounds = new Rectangle(530, 26, 150, UiTheme.ButtonHeight);
        _csv.Bounds = new Rectangle(690, 26, 150, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_heading, _yearLabel, _year, _refresh, _apply, _pdf, _csv]);

        foreach (var (name, weight) in new[]
                 {
                     ("code", 8), ("name", 18), ("deliveries", 7), ("ontime", 9), ("delay", 8),
                     ("refused", 7), ("nc", 6), ("proposed", 8), ("current", 8), ("registration", 12),
                     ("validity", 9)
                 })
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        _grid.Dock = DockStyle.Fill;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not SupplierEvaluationRow row) return;
            if (row.RegistrationExpired || row.Kpis.ProposedScore is < 50)
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
        };

        _formula.Dock = DockStyle.Bottom;
        _formula.Height = 46;
        _formula.Font = UiTheme.Small;
        _formula.ForeColor = UiTheme.TextSecondary;

        Controls.Add(_grid);
        Controls.Add(_formula);
        Controls.Add(bar);
    }

    private IReadOnlyList<string> Headers() =>
    [
        T("Field.Code"), T("Inventory.Supplier"), T("SupplierEvaluation.Deliveries"),
        T("SupplierEvaluation.OnTime"), T("SupplierEvaluation.AverageDelay"),
        T("SupplierEvaluation.Refused"), T("SupplierEvaluation.NonConformities"),
        T("SupplierEvaluation.ProposedScore"), T("SupplierEvaluation.CurrentScore"),
        T("Inventory.RegulatoryRegistration"), T("Inventory.RegulatoryRegistrationValidUntil")
    ];

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.SupplierEvaluation");
        _yearLabel.Text = T("SupplierEvaluation.Year");
        _refresh.Text = T("Action.Refresh");
        _apply.Text = T("SupplierEvaluation.Apply");
        _pdf.Text = T("SupplierEvaluation.Pdf");
        _csv.Text = T("SupplierEvaluation.Csv");
        _formula.Text = T("SupplierEvaluation.Formula");

        var headers = Headers();
        for (var i = 0; i < _grid.Columns.Count; i++) _grid.Columns[i].HeaderText = headers[i];

        if (_rows.Count > 0) Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _evaluation.EvaluateAsync((int)_year.Value, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing the supplier evaluation failed.");
        }
    }

    private void Render()
    {
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var index = _grid.Rows.Add([.. PurchasingCsv.SupplierReportCells(row)]);
            _grid.Rows[index].Tag = row;
        }

        _heading.Text = $"{T("Nav.SupplierEvaluation")} — {(int)_year.Value} · {_rows.Count}";
        _apply.Enabled = Current?.Kpis.ProposedScore is not null;
    }

    /// <summary>Applies the proposed score, or one the evaluator types instead, as today's evaluation.</summary>
    private async Task ApplyAsync()
    {
        if (Current is not { Kpis.ProposedScore: { } proposed } row) return;

        var answer = _dialogs.PromptForText(T("SupplierEvaluation.ApplyPrompt", row.Name, proposed),
                                            _apply.Text, proposed.ToString(CultureInfo.InvariantCulture));
        if (answer is null) return;

        if (!int.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var score))
        {
            _dialogs.Warn(Localizer.Format("Result_NotANumber", answer), _apply.Text);
            return;
        }

        var applied = await _evaluation.ApplyScoreAsync(row.SupplierId, score, proposed, (int)_year.Value);
        if (applied.IsFailure) { _dialogs.ShowFailure(applied, _apply.Text); return; }

        await ActivateAsync();
    }

    private async Task PrintAsync()
    {
        var document = new ListDocument(
            T("SupplierEvaluation.ReportTitle", (int)_year.Value),
            $"{T("SupplierEvaluation.Formula")} — {DateTime.Now:dd/MM/yyyy HH:mm}",
            Headers(),
            [.. _rows.Select(PurchasingCsv.SupplierReportCells)],
            Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic);

        var opened = await _output.OpenAsync(_lists.Render(document), "pdf");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _pdf.Text);
    }

    private async Task ExportAsync()
    {
        var opened = await _output.OpenAsync(PurchasingCsv.SupplierReport(_rows, Headers()), "csv");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _csv.Text);
    }
}
