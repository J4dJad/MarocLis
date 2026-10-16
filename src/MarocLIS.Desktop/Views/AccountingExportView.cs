using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The accounting export: a period, the sites, the journals and the layout; the entries checked
/// (each balanced, each line on a CGNC account) before the file is written, and the entries that
/// stop it listed. The simple VAT statement of the same period is printed from here too.
/// </summary>
public sealed class AccountingExportView : LocalizedView
{
    private static readonly AccountingJournals[] JournalChoices =
    [
        AccountingJournals.Sales, AccountingJournals.Receipts, AccountingJournals.Adjustments,
        AccountingJournals.Payroll, AccountingJournals.Depreciation, AccountingJournals.Purchases
    ];

    private readonly AccountingExportService _accounting;
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<AccountingExportView>? _logger;

    private readonly Label _heading = new();
    private readonly Label _fromLabel = new(), _toLabel = new(), _layoutLabel = new(), _sitesLabel = new(),
                           _journalsLabel = new();
    private readonly DateTimePicker _from = new(), _to = new();
    private readonly ComboBox _layout = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckedListBox _sites = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly Dictionary<AccountingJournals, CheckBox> _journals = [];
    private readonly Button _check = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _export = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _vatCsv = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _vatPdf = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();
    private readonly DataGridView _summary = UiTheme.Grid();
    private readonly DataGridView _problems = UiTheme.Grid();
    private readonly Label _scopeTitle = new();
    private readonly Label _summaryTitle = new();
    private readonly Label _problemsTitle = new();

    private IReadOnlyList<AccountingSiteOption> _siteOptions = [];
    private AccountingExportPreview? _preview;

    public AccountingExportView(AccountingExportService accounting, IListPdfRenderer lists, IDocumentOutput output,
                                IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                                ILogger<AccountingExportView>? logger = null)
        : base(localizer, cultureManager)
    {
        _accounting = accounting;
        _lists = lists;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();

        _check.Click += async (_, _) => await RunBusyAsync(CheckAsync);
        _export.Click += async (_, _) => await RunBusyAsync(ExportAsync);
        _vatCsv.Click += async (_, _) => await RunBusyAsync(() => VatAsync(pdf: false));
        _vatPdf.Click += async (_, _) => await RunBusyAsync(() => VatAsync(pdf: true));

        PermissionGate.Require(Permissions.Accounting.Export, _check, _export, _vatCsv, _vatPdf);
    }

    public override string Title => T("Nav.AccountingExport");

    private void BuildLayout()
    {
        Padding = new Padding(24, 20, 24, 16);

        // The title, then the checks and exports; the export itself, the primary, at the far end.
        var header = FinanceUi.PageHeader(_heading, null, _check, _vatCsv, _vatPdf, _export);

        // The last calendar month by default: the usual rhythm of the accountant's entries.
        var firstOfMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _from.Format = _to.Format = DateTimePickerFormat.Short;
        _from.Value = firstOfMonth.AddMonths(-1);
        _to.Value = firstOfMonth.AddDays(-1);
        _from.Width = _to.Width = 150;
        _layout.FlatStyle = FlatStyle.Flat;
        _layout.Width = 330;
        _sites.Size = new Size(320, 70);
        _sites.BorderStyle = BorderStyle.FixedSingle;

        var journals = new FlowLayoutPanel
        {
            Size = new Size(690, 70),
            WrapContents = true,
            BackColor = UiTheme.SurfaceRaised
        };

        foreach (var journal in JournalChoices)
        {
            var box = new CheckBox
            {
                AutoSize = true,
                MinimumSize = new Size(225, 24),
                Checked = true,
                Font = UiTheme.Body,
                Margin = new Padding(0, 0, 4, 2)
            };
            _journals[journal] = box;
            journals.Controls.Add(box);
        }

        var scope = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            Padding = new Padding(16, 4, 16, 4),
            BackColor = UiTheme.SurfaceRaised
        };
        scope.Controls.Add(FinanceUi.Stacked(_fromLabel, _from));
        scope.Controls.Add(FinanceUi.Stacked(_toLabel, _to));
        scope.Controls.Add(FinanceUi.Stacked(_layoutLabel, _layout));
        scope.SetFlowBreak(scope.Controls[^1], true);
        scope.Controls.Add(FinanceUi.Stacked(_sitesLabel, _sites));
        scope.Controls.Add(FinanceUi.Stacked(_journalsLabel, journals));

        var scopeCard = new FinanceCard(scope, FinanceUi.CardHeader(_scopeTitle, null))
        {
            Dock = DockStyle.Top,
            Height = 232
        };

        _export.Enabled = false;

        foreach (var (name, weight) in new[] { ("journal", 10), ("kind", 22), ("entries", 10), ("debit", 14), ("credit", 14) })
            _summary.Columns.Add(new DataGridViewTextBoxColumn { Name = name, FillWeight = weight, ReadOnly = true,
                                                                 SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var (name, weight) in new[]
                 {
                     ("problem", 14), ("date", 9), ("journal", 7), ("piece", 14), ("label", 34), ("detail", 16)
                 })
            _problems.Columns.Add(new DataGridViewTextBoxColumn { Name = name, FillWeight = weight, ReadOnly = true,
                                                                  SortMode = DataGridViewColumnSortMode.NotSortable });

        // Figures and pieces in the fixed-pitch face, so the columns line up.
        foreach (var name in new[] { "entries", "debit", "credit" })
        {
            _summary.Columns[name]!.DefaultCellStyle.Font = UiTheme.Monospace;
            _summary.Columns[name]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        _problems.Columns["date"]!.DefaultCellStyle.Font = UiTheme.Monospace;
        _problems.Columns["piece"]!.DefaultCellStyle.Font = UiTheme.Monospace;

        _status.Dock = DockStyle.Top;
        _status.Height = 40;
        _status.Font = UiTheme.BodyBold;
        _status.Padding = new Padding(0, 12, 0, 6);
        _status.AutoEllipsis = true;

        _problems.DefaultCellStyle.ForeColor = UiTheme.Critical;

        var results = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        results.Controls.Add(new FinanceCard(_summary, FinanceUi.CardHeader(_summaryTitle, null)) { Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        results.Controls.Add(new FinanceCard(_problems, FinanceUi.CardHeader(_problemsTitle, null)) { Margin = new Padding(8, 0, 0, 0) }, 1, 0);

        Controls.Add(results);
        Controls.Add(_status);
        Controls.Add(scopeCard);
        Controls.Add(header);
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.AccountingExport");
        _fromLabel.Text = T("Field.From");
        _toLabel.Text = T("Field.To");
        _layoutLabel.Text = T("SupplierInvoices.ExportLayout");
        _sitesLabel.Text = T("Accounting.Sites");
        _journalsLabel.Text = T("Accounting.Journals");
        _check.Text = T("Accounting.Check");
        _export.Text = T("Action.Export");
        _vatCsv.Text = T("Accounting.VatStatementCsv");
        _vatPdf.Text = T("Accounting.VatStatementPdf");
        _scopeTitle.Text = T("Finance.ExportScope");
        _summaryTitle.Text = T("Accounting.Journals");
        _problemsTitle.Text = T("Finance.ExportProblems");

        foreach (var (journal, box) in _journals) box.Text = T($"AccountingJournals.{journal}");

        var selected = _layout.SelectedIndex;
        _layout.Items.Clear();
        foreach (var layout in Enum.GetValues<AccountingExportLayout>())
            _layout.Items.Add(T($"AccountingExportLayout.{layout}"));
        _layout.SelectedIndex = selected >= 0 ? selected : 0;

        string[] summary = [T("Accounting.Journal"), T("Accounting.JournalKind"), T("Accounting.Entries"),
                            T("Accounting.Debit"), T("Accounting.Credit")];
        for (var c = 0; c < summary.Length; c++) _summary.Columns[c].HeaderText = summary[c];

        string[] problems = [T("Accounting.Problem"), T("Field.Date"), T("Accounting.Journal"), T("Accounting.Piece"),
                             T("Accounting.Label"), T("Accounting.Detail")];
        for (var c = 0; c < problems.Length; c++) _problems.Columns[c].HeaderText = problems[c];

        Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            if (_siteOptions.Count > 0) return;

            var sites = await _accounting.GetSitesAsync(ct);
            if (sites.IsFailure) { _dialogs.ShowFailure(sites); return; }

            _siteOptions = sites.Value;
            _sites.Items.Clear();
            foreach (var site in _siteOptions) _sites.Items.Add($"{site.Code} — {site.Name}", true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the sites of the accounting export failed.");
        }
    }

    private AccountingExportRequest Request()
    {
        var journals = _journals.Where(j => j.Value.Checked)
                                .Aggregate(AccountingJournals.None, (all, j) => all | j.Key);
        var layouts = Enum.GetValues<AccountingExportLayout>();
        var layout = _layout.SelectedIndex >= 0 && _layout.SelectedIndex < layouts.Length
            ? layouts[_layout.SelectedIndex]
            : AccountingExportLayout.Sage;

        return new AccountingExportRequest(
            DateOnly.FromDateTime(_from.Value.Date), DateOnly.FromDateTime(_to.Value.Date),
            [.. _sites.CheckedIndices.Cast<int>().Select(i => _siteOptions[i].Id)],
            journals, layout);
    }

    private async Task CheckAsync()
    {
        try
        {
            var preview = await _accounting.PrepareAsync(Request());
            if (preview.IsFailure) { _dialogs.ShowFailure(preview, _check.Text); return; }

            _preview = preview.Value;
            Render();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Checking the accounting entries failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _check.Text);
        }
    }

    private void Render()
    {
        _summary.Rows.Clear();
        _problems.Rows.Clear();

        if (_preview is not { } preview)
        {
            _status.Text = T("Accounting.CheckFirst");
            _status.ForeColor = UiTheme.TextSecondary;
            _export.Enabled = false;
            return;
        }

        var fr = CultureInfo.CurrentCulture;
        foreach (var group in preview.Entries.GroupBy(e => (e.Journal, e.Kind)).OrderBy(g => g.Key.Journal, StringComparer.Ordinal))
            _summary.Rows.Add(group.Key.Journal, T($"AccountingJournals.{group.Key.Kind}"),
                              group.Count().ToString(fr), group.Sum(e => e.Debit).ToString("N2", fr),
                              group.Sum(e => e.Credit).ToString("N2", fr));

        foreach (var problem in preview.Problems)
            _problems.Rows.Add(T($"AccountingProblemKind.{problem.Kind}"), problem.Date.ToString("d", fr), problem.Journal,
                               problem.Piece, problem.Label, problem.Detail);

        if (preview.Problems.Count > 0)
        {
            _status.Text = T("Accounting_ExportRefused", preview.Problems.Count);
            _status.ForeColor = UiTheme.Critical;
        }
        else if (preview.Entries.Count == 0)
        {
            _status.Text = T("Purchasing_NothingToExport");
            _status.ForeColor = UiTheme.TextSecondary;
        }
        else
        {
            _status.Text = T("Accounting.Balanced", preview.Entries.Count);
            _status.ForeColor = UiTheme.Normal;
        }

        if (!preview.DepreciationAvailable && _journals[AccountingJournals.Depreciation].Checked)
            _status.Text += "  ·  " + T("Accounting.NoDepreciationSource");

        _export.Enabled = preview.CanExport;
    }

    private async Task ExportAsync()
    {
        try
        {
            var file = await _accounting.ExportAsync(Request());
            if (file.IsFailure)
            {
                _dialogs.ShowFailure(file, _export.Text);
                await CheckAsync();
                return;
            }

            var opened = await _output.OpenAsync(file.Value, "csv");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _export.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exporting the accounting entries failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _export.Text);
        }
    }

    private async Task VatAsync(bool pdf)
    {
        var title = pdf ? _vatPdf.Text : _vatCsv.Text;
        try
        {
            var request = Request();
            var statement = await _accounting.GetVatStatementAsync(request.From, request.To, request.SiteIds);
            if (statement.IsFailure) { _dialogs.ShowFailure(statement, title); return; }

            byte[] file;
            if (pdf)
            {
                var fr = CultureInfo.GetCultureInfo("fr-FR");
                file = _lists.Render(new ListDocument(
                    $"État de TVA du {request.From.ToString("dd/MM/yyyy", fr)} au {request.To.ToString("dd/MM/yyyy", fr)}",
                    VatStatement.Warning,
                    ["Libellé", "Base HT", "TVA"],
                    statement.Value.Rows()));
            }
            else
            {
                file = statement.Value.ToCsv();
            }

            var opened = await _output.OpenAsync(file, pdf ? "pdf" : "csv");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, title);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Producing the VAT statement failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), title);
        }
    }
}
