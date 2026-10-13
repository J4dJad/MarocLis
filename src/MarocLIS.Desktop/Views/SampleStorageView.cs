using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Samples;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The serum bank: where every received tube is, and which ones may be purged.
/// <para>
/// A laboratory keeps tubes after the run for add-on requests and for repeats. Two things then go
/// wrong without a screen like this. A tube that was received but never shelved is a tube nobody
/// can find when the add-on arrives — so those are listed and flagged, not hidden. And a bank that
/// is never purged fills up, while a tube physically in the bin but still recorded as stored sends
/// a technician hunting for it.
/// </para>
/// <para>
/// Aliquoting lives here because it is a storage act: one draw feeds a local analyser, a
/// subcontracted send and a retain, and each fraction needs its own barcode because they end up in
/// different places. Traceability has to follow the fraction, not the parent draw.
/// </para>
/// </summary>
public sealed class SampleStorageView : LocalizedView
{
    private readonly SampleWorkflowService _samples;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<SampleStorageView>? _logger;

    private readonly TextBox _search = UiTheme.Input(280);
    private readonly CheckBox _onlyExpired = new();
    private readonly Label _heading = new();
    private readonly DataGridView _grid = UiTheme.Grid();

    private readonly Button _shelve = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _aliquot = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _discard = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _purge = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<StoredSampleRow> _rows = [];

    public SampleStorageView(SampleWorkflowService samples, IUserDialogs dialogs,
                             ILocalizer localizer, ICultureManager cultureManager,
                             ILogger<SampleStorageView>? logger = null)
        : base(localizer, cultureManager)
    {
        _samples = samples;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.SerumBank");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.SectionTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 2);

        _search.Location = new Point(0, 28);

        _onlyExpired.Font = UiTheme.Small;
        _onlyExpired.ForeColor = UiTheme.TextSecondary;
        _onlyExpired.AutoSize = true;
        _onlyExpired.Location = new Point(416, 34);

        // Before the tick box, not after it: the tick box sizes itself to its sentence, which in
        // French ran under a button placed at a fixed position to its right.
        _refresh.Bounds = new Rectangle(296, 27, 110, UiTheme.ButtonHeight - 4);

        bar.Controls.AddRange([_heading, _search, _onlyExpired, _refresh]);

        BuildColumns();

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_grid);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48,
                                  BackColor = UiTheme.Surface };

        _shelve.Bounds = new Rectangle(0, 10, 200, UiTheme.ButtonHeight);
        _aliquot.Bounds = new Rectangle(210, 10, 190, UiTheme.ButtonHeight);
        _discard.Bounds = new Rectangle(410, 10, 170, UiTheme.ButtonHeight);
        _purge.Bounds = new Rectangle(590, 10, 240, UiTheme.ButtonHeight);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(842, 16, 640, 20);

        actions.Controls.AddRange([_shelve, _aliquot, _discard, _purge, _status]);

        Controls.Add(host);
        Controls.Add(actions);
        Controls.Add(bar);
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();

        void Add(string name, int weight, Font? font = null, bool right = false)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("barcode", 11, UiTheme.MonospaceBold);
        Add("accession", 12, UiTheme.Monospace);
        Add("patient", 18);
        Add("specimen", 12, UiTheme.Small);
        Add("received", 10, UiTheme.Monospace);
        Add("location", 12, UiTheme.Monospace);
        Add("keepuntil", 10, UiTheme.Monospace);
        Add("days", 6, UiTheme.MonospaceBold, right: true);
        Add("volume", 6, UiTheme.Monospace, right: true);
        Add("aliquots", 5, UiTheme.Monospace, right: true);
        Add("flags", 6, UiTheme.SmallBold);
    }

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ActivateAsync(); }
        };

        _onlyExpired.CheckedChanged += async (_, _) => await ActivateAsync();
        _refresh.Click += async (_, _) => await ActivateAsync();

        _shelve.Click += async (_, _) => await ShelveAsync();
        _aliquot.Click += async (_, _) => await AliquotAsync();
        _discard.Click += async (_, _) => await DiscardAsync();
        _purge.Click += async (_, _) => await PurgeAsync();

        _grid.SelectionChanged += (_, _) => RefreshActions();
        _grid.CellFormatting += OnCellFormatting;
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not StoredSampleRow row) return;

        // Past retention leads, because purging is the action this screen exists to enable.
        if (row.IsPastRetention)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.IsUnshelved)
        {
            // Received but nowhere: the tube that cannot be found when an add-on arrives.
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
        else if (row.DaysToKeepUntil is <= 7)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
    }

    protected override void ApplyTexts()
    {
        _search.PlaceholderText = T("Sample.StorageSearchPlaceholder");
        _onlyExpired.Text = T("Sample.OnlyPastRetention");

        _shelve.Text = T("Sample.Shelve");
        _aliquot.Text = T("Sample.CreateAliquot");
        _discard.Text = T("Sample.Discard");
        _purge.Text = T("Sample.PurgeExpired");
        _refresh.Text = T("Action.Refresh");

        if (_grid.Columns.Count > 0)
        {
            _grid.Column("barcode").HeaderText = T("Sample.Barcode");
            _grid.Column("accession").HeaderText = T("Order.AccessionNumber");
            _grid.Column("patient").HeaderText = T("Patient.Title");
            _grid.Column("specimen").HeaderText = T("Sample.SpecimenType");
            _grid.Column("received").HeaderText = T("Sample.ReceivedAt");
            _grid.Column("location").HeaderText = T("Sample.Storage");
            _grid.Column("keepuntil").HeaderText = T("Sample.KeepUntil");
            _grid.Column("days").HeaderText = T("Sample.DaysLeft");
            _grid.Column("volume").HeaderText = T("Sample.Volume");
            _grid.Column("aliquots").HeaderText = T("Sample.Aliquots");
            _grid.Column("flags").HeaderText = string.Empty;
        }

        if (_rows.Count > 0) Render();
        RefreshActions();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _samples.GetStoredSamplesAsync(
                string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim(),
                _onlyExpired.Checked, ct);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the serum bank failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    private void Render()
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var flags = new List<string>(3);
            if (row.IsUnshelved) flags.Add("?");
            if (row.HasDefects) flags.Add("!");
            if (row.IsAcceptedWithReservation) flags.Add("R");

            var index = _grid.Rows.Add(
                row.Barcode,
                row.AccessionNumber,
                row.PatientName,
                row.SpecimenTypeName,
                row.ReceivedAt?.ToLocalTime().ToString("dd/MM/yy HH:mm") ?? string.Empty,
                row.StorageLocation is { Length: > 0 }
                    ? row.StoragePosition is { Length: > 0 }
                        ? $"{row.StorageLocation} / {row.StoragePosition}"
                        : row.StorageLocation
                    : T("Sample.NotShelved"),
                row.KeepUntil?.ToString("dd/MM/yy") ?? "—",
                row.DaysToKeepUntil?.ToString() ?? "—",
                row.VolumeMl?.ToString("0.#") ?? string.Empty,
                row.AliquotCount > 0 ? row.AliquotCount.ToString() : string.Empty,
                string.Join(" ", flags));

            _grid.Rows[index].Tag = row;
        }

        _grid.ResumeLayout(performLayout: true);

        var expired = _rows.Count(r => r.IsPastRetention);
        var unshelved = _rows.Count(r => r.IsUnshelved);

        var parts = new List<string>(3) { $"{_rows.Count}" };
        if (expired > 0) parts.Add($"{expired} {T("Sample.PastRetention").ToLowerInvariant()}");
        if (unshelved > 0) parts.Add($"{unshelved} {T("Sample.NotShelved").ToLowerInvariant()}");

        _heading.Text = $"{T("Nav.SerumBank")} — {string.Join(" · ", parts)}";

        if (_grid.Rows.Count > 0) _grid.SelectFirstRow();

        RefreshActions();
    }

    private StoredSampleRow? Selected => _grid.CurrentRow?.Tag as StoredSampleRow;

    private void RefreshActions()
    {
        var row = Selected;

        _shelve.Enabled = row is not null;
        _discard.Enabled = row is not null;

        // Aliquoting needs a tube that is actually present and not already refused.
        _aliquot.Enabled = row is { Status: not SampleStatus.Rejected };

        var expired = _rows.Count(r => r.IsPastRetention);
        _purge.Enabled = expired > 0;
        _purge.ForeColor = expired > 0 ? UiTheme.Abnormal : UiTheme.TextMuted;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task ShelveAsync()
    {
        if (Selected is not { } row) return;

        using var dialog = new SampleStorageDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _samples.MoveToStorageAsync(row.SampleId, dialog.StorageLocation,
                                                        dialog.Position, dialog.KeepUntil);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Sample.Shelve")); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = $"{row.Barcode} — {dialog.StorageLocation}";

        await ActivateAsync();
    }

    private async Task AliquotAsync()
    {
        if (Selected is not { } row) return;

        using var dialog = new SampleAliquotDialog(Localizer, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _samples.CreateAliquotAsync(row.SampleId, dialog.VolumeMl,
                                                        dialog.Purpose, dialog.StorageLocation);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Sample.CreateAliquot")); return; }

        // The new barcode is what the operator has to write on the tube, so it is stated rather
        // than left to be found in a list.
        _dialogs.Info(T("Sample.AliquotCreated", result.Value), T("Sample.CreateAliquot"));

        await ActivateAsync();
    }

    private async Task DiscardAsync()
    {
        if (Selected is not { } row) return;

        // Confirmed before the reason is asked for: discarding a tube ends any possibility of
        // repeating an analysis on it, and that is worth one deliberate click.
        if (!_dialogs.Confirm(T("Sample.ConfirmDiscard", row.Barcode, row.PatientName),
                               T("Sample.Discard")))
            return;

        var note = _dialogs.PromptForReason(T("Sample.DiscardPrompt", row.Barcode),
                                             T("Sample.Discard"));

        if (note is null) return;

        var result = await _samples.DiscardAsync(row.SampleId, note);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Sample.Discard")); return; }

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = $"{row.Barcode} — {T("SampleStatus.Discarded")}";

        await ActivateAsync();
    }

    /// <summary>
    /// Discards every tube past its retention, in one pass, against one recorded reason.
    /// <para>
    /// A purge is a routine periodic job — a technician clearing a rack of last month's tubes —
    /// and making them confirm each of eighty tubes individually is how the job stops being done
    /// at all. The count is stated first and each tube still gets its own audit entry.
    /// </para>
    /// </summary>
    private async Task PurgeAsync()
    {
        var expired = _rows.Where(r => r.IsPastRetention).ToList();

        if (expired.Count == 0) return;

        if (!_dialogs.Confirm(T("Sample.ConfirmPurge", expired.Count), T("Sample.PurgeExpired")))
            return;

        var note = _dialogs.PromptForReason(T("Sample.PurgePrompt", expired.Count),
                                             T("Sample.PurgeExpired"));

        if (note is null) return;

        var discarded = 0;
        var failures = new List<string>();

        foreach (var row in expired)
        {
            var result = await _samples.DiscardAsync(row.SampleId, note);

            if (result.IsSuccess) discarded++;
            else failures.Add(row.Barcode);
        }

        _status.ForeColor = failures.Count == 0 ? UiTheme.Normal : UiTheme.Abnormal;

        _status.Text = failures.Count == 0
            ? T("Sample.PurgeDone", discarded)
            : T("Sample.PurgePartial", discarded, string.Join(", ", failures.Take(5)));

        await ActivateAsync();
    }
}

/// <summary>Shelving a tube: where it went, and how long it is kept.</summary>
internal sealed class SampleStorageDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly TextBox _location = UiTheme.Input(240);
    private readonly TextBox _position = UiTheme.Input(180);
    private readonly DateTimePicker _keepUntil = new();
    private readonly CheckBox _hasKeepUntil = new();

    /// <summary>
    /// Where the tube went. <c>StorageLocation</c>, not <c>Location</c>: that name belongs to
    /// <see cref="Form.Location"/> — the dialog's position on screen — and shadowing it means the
    /// next person to position this dialog, or to read its position, silently gets a shelf
    /// reference instead of a <see cref="Point"/>.
    /// </summary>
    internal string StorageLocation => _location.Text.Trim();
    internal string? Position => string.IsNullOrWhiteSpace(_position.Text) ? null : _position.Text.Trim();

    internal DateOnly? KeepUntil =>
        _hasKeepUntil.Checked ? DateOnly.FromDateTime(_keepUntil.Value.Date) : null;

    internal SampleStorageDialog(ILocalizer localizer, IUserDialogs dialogs, StoredSampleRow row)
    {
        _localizer = localizer;
        _dialogs = dialogs;

        Text = $"{localizer["Sample.Shelve"]} — {row.Barcode}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 250);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var identity = new Label
        {
            Text = $"{row.PatientName}   ·   {row.AccessionNumber}   ·   {row.SpecimenTypeName}",
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.Accent,
            AutoSize = false,
            Bounds = new Rectangle(20, 14, 440, 20)
        };

        var locationLabel = UiTheme.FieldLabel(localizer["Sample.Storage"]);
        locationLabel.Location = new Point(20, 46);
        _location.Location = new Point(20, 64);
        _location.Text = row.StorageLocation ?? string.Empty;

        var positionLabel = UiTheme.FieldLabel(localizer["Sample.StoragePosition"]);
        positionLabel.Location = new Point(280, 46);
        _position.Location = new Point(280, 64);
        _position.Text = row.StoragePosition ?? string.Empty;
        _position.Tag = UiTags.LatinValue;

        _hasKeepUntil.Text = localizer["Sample.KeepUntil"];
        _hasKeepUntil.AutoSize = true;
        _hasKeepUntil.Location = new Point(20, 104);
        _hasKeepUntil.Checked = row.KeepUntil is not null;

        _keepUntil.Bounds = new Rectangle(20, 126, 200, UiTheme.InputHeight);
        _keepUntil.Format = DateTimePickerFormat.Short;
        _keepUntil.Enabled = _hasKeepUntil.Checked;
        _keepUntil.Value = row.KeepUntil?.ToDateTime(TimeOnly.MinValue)
                           ?? DateTime.Today.AddDays(7);

        _hasKeepUntil.CheckedChanged += (_, _) => _keepUntil.Enabled = _hasKeepUntil.Checked;

        var notice = new Label
        {
            Text = localizer["Sample.KeepUntilNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 160, 440, 30)
        };

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 200, 210, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 200, 210, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([identity, locationLabel, _location, positionLabel, _position,
                           _hasKeepUntil, _keepUntil, notice, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_location.Text))
        {
            _dialogs.Warn(_localizer["Sample_StorageLocationRequired"]);
            _location.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Splitting an aliquot off a tube.
/// <para>
/// The purpose is asked for because it is what an operator needs later: a fraction labelled only
/// with a barcode tells the person holding it nothing about whether it may be used.
/// </para>
/// </summary>
internal sealed class SampleAliquotDialog : Form
{
    private readonly TextBox _volume = UiTheme.Input(120);
    private readonly TextBox _purpose = UiTheme.Input(280);
    private readonly TextBox _location = UiTheme.Input(240);

    internal decimal? VolumeMl =>
        decimal.TryParse(_volume.Text.Replace(',', '.'),
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value > 0m
            ? value : null;

    internal string? Purpose => string.IsNullOrWhiteSpace(_purpose.Text) ? null : _purpose.Text.Trim();

    internal string? StorageLocation =>
        string.IsNullOrWhiteSpace(_location.Text) ? null : _location.Text.Trim();

    internal SampleAliquotDialog(ILocalizer localizer, StoredSampleRow row)
    {
        Text = $"{localizer["Sample.CreateAliquot"]} — {row.Barcode}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 260);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var parent = new Label
        {
            Text = $"{row.PatientName}   ·   {row.SpecimenTypeName}"
                   + (row.VolumeMl is { } volume ? $"   ·   {volume:0.#} mL" : string.Empty),
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.Accent,
            AutoSize = false,
            Bounds = new Rectangle(20, 14, 440, 20)
        };

        var notice = new Label
        {
            Text = localizer["Sample.AliquotNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 40, 440, 44)
        };

        var volumeLabel = UiTheme.FieldLabel(localizer["Sample.Volume"]);
        volumeLabel.Location = new Point(20, 92);
        _volume.Location = new Point(20, 110);
        _volume.Tag = UiTags.LatinValue;
        _volume.TextAlign = HorizontalAlignment.Right;

        var purposeLabel = UiTheme.FieldLabel(localizer["Sample.AliquotPurpose"]);
        purposeLabel.Location = new Point(160, 92);
        _purpose.Location = new Point(160, 110);

        var locationLabel = UiTheme.FieldLabel(localizer["Sample.Storage"]);
        locationLabel.Location = new Point(20, 148);
        _location.Location = new Point(20, 166);
        _location.Text = row.StorageLocation ?? string.Empty;

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 210, 210, UiTheme.ButtonHeight);
        ok.DialogResult = DialogResult.OK;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 210, 210, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([parent, notice, volumeLabel, _volume, purposeLabel, _purpose,
                           locationLabel, _location, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}
