using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The reception checks, shared by the direct receipt and the purchase-order receipt: the GS1 scan
/// box, the temperature at arrival against the article's storage range, and the conformity
/// checklist. Each checklist box starts ticked; unticking one records the failure and opens a
/// non-conformity against the supplier when the delivery is saved.
/// </summary>
internal sealed class ReceptionChecksPanel : Panel
{
    public const int PanelHeight = 196;

    private readonly ILocalizer _localizer;
    private readonly TextBox _scan = UiTheme.Input(470);
    private readonly TextBox _temperature = UiTheme.Input(90);
    private readonly Label _range = new();
    private readonly CheckBox _matchesOrder = new();
    private readonly CheckBox _packaging = new();
    private readonly CheckBox _french = new();
    private readonly CheckBox _certificate = new();
    private StorageCondition _condition;

    /// <summary>Raised when a scan was read; the dialog fills the lot and expiry from it.</summary>
    public event Action<Gs1Data>? Scanned;

    public ReceptionChecksPanel(ILocalizer localizer, StorageCondition condition)
    {
        _localizer = localizer;
        Size = new Size(480, PanelHeight);
        BackColor = UiTheme.SurfaceRaised;

        var scanLabel = UiTheme.FieldLabel(localizer["Reception.Scan"]);
        scanLabel.Location = new Point(0, 0);
        _scan.Location = new Point(0, 18);
        _scan.Tag = UiTags.LatinValue;
        _scan.Font = UiTheme.Monospace;
        // A wedge scanner types the symbol and presses Enter: read it then, not on each character.
        _scan.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            ReadScan();
        };

        var temperatureLabel = UiTheme.FieldLabel(localizer["Reception.ArrivalTemperature"]);
        temperatureLabel.Location = new Point(0, 52);
        _temperature.Location = new Point(0, 70);
        _temperature.Tag = UiTags.LatinValue;
        _temperature.TextAlign = HorizontalAlignment.Right;
        _temperature.Font = UiTheme.MonospaceBold;

        _range.Font = UiTheme.Small;
        _range.ForeColor = UiTheme.TextSecondary;
        _range.AutoSize = false;
        _range.Bounds = new Rectangle(100, 74, 380, 20);

        foreach (var (box, key, at) in new[]
                 {
                     (_matchesOrder, "Reception.MatchesOrder", new Point(0, 104)),
                     (_packaging, "Reception.PackagingIntact", new Point(240, 104)),
                     (_french, "Reception.FrenchLabelling", new Point(0, 126)),
                     (_certificate, "Reception.CertificateReceived", new Point(240, 126))
                 })
        {
            box.Text = localizer[key];
            box.Checked = true;
            box.AutoSize = true;
            box.Font = UiTheme.Body;
            box.Location = at;
        }

        var notice = new Label
        {
            Text = localizer["Reception.ChecklistNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(0, 152, 480, 40)
        };

        Controls.AddRange([scanLabel, _scan, temperatureLabel, _temperature, _range,
                           _matchesOrder, _packaging, _french, _certificate, notice]);

        SetCondition(condition);
    }

    /// <summary>The storage condition decides the range shown and whether a temperature is required.</summary>
    public void SetCondition(StorageCondition condition)
    {
        _condition = condition;
        _range.Text = ColdChain.RequiresTemperature(condition)
            ? _localizer.Format("Reception.RangeRequired", ColdChain.RangeText(condition))
            : _localizer.Format("Reception.RangeAdvisory", ColdChain.RangeText(condition));
        _range.ForeColor = ColdChain.RequiresTemperature(condition) ? UiTheme.Abnormal : UiTheme.TextSecondary;
    }

    public decimal? Temperature =>
        decimal.TryParse(_temperature.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// The checks as entered, or null with a message when the temperature is unreadable or missing
    /// for a cold-chain article. An out-of-range temperature is asked about, not refused: the
    /// delivery may still be taken in, quarantined, with its non-conformity.
    /// </summary>
    public ReceptionCheck? Build(IUserDialogs dialogs, string title)
    {
        if (!string.IsNullOrWhiteSpace(_temperature.Text) && Temperature is null)
        {
            dialogs.Warn(_localizer.Format("Result_NotANumber", _temperature.Text), title);
            _temperature.Focus();
            return null;
        }

        var check = new ReceptionCheck
        {
            ArrivalTemperatureCelsius = Temperature,
            MatchesOrder = _matchesOrder.Checked,
            PackagingIntact = _packaging.Checked,
            FrenchLabelling = _french.Checked,
            CertificateOfAnalysisReceived = _certificate.Checked
        };

        var assessment = ReceptionAssessment.Assess(_condition, check);

        if (assessment.IsFailure)
        {
            dialogs.Warn(_localizer.Describe(assessment.Errors), title);
            _temperature.Focus();
            return null;
        }

        if (assessment.Value.Temperature == TemperatureVerdict.Warning)
            dialogs.Info(_localizer.Format("Reception.AmbientWarning", ColdChain.RangeText(_condition)), title);

        if (assessment.Value.NeedsNonConformity
            && !dialogs.Confirm(_localizer["Reception.NonConformityQuestion"], title))
            return null;

        return check;
    }

    private void ReadScan()
    {
        var parsed = Gs1Parser.Parse(_scan.Text);

        if (parsed.IsFailure)
        {
            _range.ForeColor = UiTheme.Critical;
            _range.Text = _localizer.Describe(parsed.Errors);
            return;
        }

        Scanned?.Invoke(parsed.Value);
        _scan.SelectAll();
        SetCondition(_condition);
    }
}

/// <summary>The patients' results produced with a lot — what a recall has to review — with a CSV export.</summary>
internal sealed class RecallListDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly IDocumentOutput _output;
    private readonly LotQualityService _lotQuality;
    private readonly Guid _lotId;
    private readonly IReadOnlyList<RecallResultRow> _rows;

    internal RecallListDialog(ILocalizer localizer, IUserDialogs dialogs, IDocumentOutput output,
                              LotQualityService lotQuality, Guid lotId, string lotNumber,
                              IReadOnlyList<RecallResultRow> rows)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _output = output;
        _lotQuality = lotQuality;
        _lotId = lotId;
        _rows = rows;

        Text = localizer.Format("Recall.Title", lotNumber, rows.Count);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 520);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var grid = UiTheme.Grid();
        grid.Dock = DockStyle.Fill;

        foreach (var (name, header, weight) in Columns(localizer))
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Automatic
            });
        }

        foreach (var r in rows)
        {
            grid.Rows.Add(r.PatientName, r.MedicalRecordNumber, r.AccessionNumber, r.AnalysisCode,
                          r.ParameterCode, r.ResultDate?.ToString("dd/MM/yyyy HH:mm") ?? string.Empty,
                          r.IsValidated ? localizer["Recall.Yes"] : localizer["Recall.No"]);
        }

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.SurfaceRaised };

        var export = UiTheme.PrimaryButton(localizer["Recall.ExportCsv"]);
        export.Bounds = new Rectangle(12, 8, 220, UiTheme.ButtonHeight);
        export.Enabled = rows.Count > 0;
        export.Click += async (_, _) => await ExportAsync();

        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Bounds = new Rectangle(244, 8, 140, UiTheme.ButtonHeight);
        close.DialogResult = DialogResult.Cancel;

        bar.Controls.AddRange([export, close]);
        Controls.Add(grid);
        Controls.Add(bar);
        CancelButton = close;
    }

    private static IEnumerable<(string Name, string Header, int Weight)> Columns(ILocalizer l) =>
    [
        ("patient", l["Recall.Patient"], 22), ("mrn", l["Recall.MedicalRecord"], 12),
        ("order", l["Recall.Order"], 12), ("analysis", l["Recall.Analysis"], 10),
        ("parameter", l["Recall.Parameter"], 10), ("date", l["Recall.ResultDate"], 14),
        ("validated", l["Recall.Validated"], 8)
    ];

    private async Task ExportAsync()
    {
        var bytes = PurchasingCsv.Recall(_rows, [.. Columns(_localizer).Select(c => c.Header)]);
        await _lotQuality.LogRecallExportAsync(_lotId, _rows.Count);

        var opened = await _output.OpenAsync(bytes, "csv");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, Text);
    }
}

/// <summary>The new lot against the previous one, per control level.</summary>
internal sealed class LotComparisonDialog : Form
{
    internal LotComparisonDialog(ILocalizer localizer, LotComparisonView comparison)
    {
        Text = localizer.Format("LotComparison.Title", comparison.ItemCode, comparison.NewLotNumber,
                                comparison.PreviousLotNumber ?? "—");
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(960, 440);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var grid = UiTheme.Grid();
        grid.Dock = DockStyle.Fill;

        void Add(string key, int weight) => grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = localizer[key], FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        Add("LotComparison.Level", 30);
        Add("LotComparison.NewN", 6);
        Add("LotComparison.NewMean", 10);
        Add("LotComparison.NewSd", 9);
        Add("LotComparison.OldN", 6);
        Add("LotComparison.OldMean", 10);
        Add("LotComparison.OldSd", 9);
        Add("LotComparison.Difference", 10);

        static string N(decimal? v) => v?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—";

        foreach (var r in comparison.Rows)
        {
            grid.Rows.Add(r.Label, r.NewCount, N(r.NewMean), N(r.NewSd), r.OldCount, N(r.OldMean),
                          N(r.OldSd), r.DifferencePercent is { } d ? $"{d:+0.##;-0.##;0} %" : "—");
        }

        var notice = new Label
        {
            Text = comparison.Rows.Count == 0
                ? localizer["LotComparison.NoData"]
                : localizer["LotComparison.Notice"],
            Dock = DockStyle.Bottom,
            Height = 40,
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            Padding = new Padding(8, 4, 8, 4)
        };

        Controls.Add(grid);
        Controls.Add(notice);
    }
}

/// <summary>The certificates kept with a lot: open one, or attach a PDF or a scan.</summary>
internal sealed class LotCertificatesDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly IDocumentOutput _output;
    private readonly ReceptionService _reception;
    private readonly Guid _lotId;
    private readonly ListBox _list = new();
    private readonly Button _attach;
    private readonly Button _open;

    internal LotCertificatesDialog(ILocalizer localizer, IUserDialogs dialogs, IDocumentOutput output,
                                   ReceptionService reception, Guid lotId, string lotNumber)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _output = output;
        _reception = reception;
        _lotId = lotId;

        Text = localizer.Format("Certificates.Title", lotNumber);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 320);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        _list.Bounds = new Rectangle(20, 16, 480, 220);
        _list.DoubleClick += async (_, _) => await OpenAsync();

        _attach = UiTheme.PrimaryButton(localizer["Certificates.Attach"]);
        _attach.Bounds = new Rectangle(20, 260, 150, UiTheme.ButtonHeight);
        _attach.Click += async (_, _) => await AttachAsync();
        PermissionGate.Require(Permissions.Inventory.Receive, _attach);

        _open = UiTheme.SecondaryButton(localizer["Certificates.Open"]);
        _open.Bounds = new Rectangle(180, 260, 150, UiTheme.ButtonHeight);
        _open.Click += async (_, _) => await OpenAsync();

        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Bounds = new Rectangle(350, 260, 150, UiTheme.ButtonHeight);
        close.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_list, _attach, _open, close]);
        CancelButton = close;

        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var rows = await _reception.GetCertificatesAsync(_lotId);
        if (rows.IsFailure) { _dialogs.ShowFailure(rows, Text); return; }

        _list.Items.Clear();
        foreach (var row in rows.Value) _list.Items.Add(new Entry(row));
    }

    private async Task AttachAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = _attach.Text,
            Filter = $"{_localizer["Credential.ScanFiles"]} (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] content;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > LotCertificateFile.MaximumBytes)
            {
                _dialogs.Warn(_localizer.Format("Credential_FileTooLarge", LotCertificateFile.MaximumBytes / (1024 * 1024)),
                              _attach.Text);
                return;
            }
            content = await File.ReadAllBytesAsync(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{_localizer["Documents.FileUnreadable"]}\n{ex.Message}", _attach.Text);
            return;
        }

        var attached = await _reception.AttachCertificateAsync(_lotId, Path.GetFileName(picker.FileName), content);
        if (attached.IsFailure) { _dialogs.ShowFailure(attached, _attach.Text); return; }

        await LoadAsync();
    }

    private async Task OpenAsync()
    {
        if (_list.SelectedItem is not Entry entry) return;

        var file = await _reception.GetCertificateAsync(entry.Row.Id);
        if (file.IsFailure) { _dialogs.ShowFailure(file, _open.Text); return; }

        var opened = await _output.ViewAsync(file.Value.Content, Path.GetExtension(file.Value.FileName));
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _open.Text);
    }

    private sealed record Entry(LotCertificateRow Row)
    {
        public override string ToString() => $"{Row.FileName}  ·  {Row.AddedAt:dd/MM/yyyy}";
    }
}
