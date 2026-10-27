using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
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

    private readonly PageHead _head = new();
    private readonly FilterRow _filters = new();
    private readonly SearchField _searchField = new(340);
    private TextBox _search => _searchField.Box;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly ListCard _card;
    private readonly AsideCard _aside = new();

    private readonly Button _shelve = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _aliquot = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _discard = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly Button _more;
    private readonly ToolStripMenuItem _purge = Pilotage.MenuItem("Sample.PurgeExpired");
    private Label _status => _card.Footer;

    // « Conservation dépassée » asks the server, as the tick box did; « Non rangés » filters the
    // loaded list.
    private bool _onlyExpired;
    private bool _onlyUnshelved;

    private StorageFilter Filter => _onlyExpired ? StorageFilter.PastRetention
        : _onlyUnshelved ? StorageFilter.Unshelved
        : StorageFilter.All;
    private readonly ChipButton _allChip;
    private readonly ChipButton _expiredChip;
    private readonly ChipButton _unshelvedChip;

    private IReadOnlyList<StoredSampleRow> _rows = [];

    public SampleStorageView(SampleWorkflowService samples, IUserDialogs dialogs,
                             ILocalizer localizer, ICultureManager cultureManager,
                             ILogger<SampleStorageView>? logger = null)
        : base(localizer, cultureManager)
    {
        _samples = samples;
        _dialogs = dialogs;
        _logger = logger;

        _card = new ListCard(_grid);
        _more = Pilotage.MoreButton(_moreMenu);
        _allChip = _filters.Chip(async (_, _) => await FilterAsync(expired: false, unshelved: false));
        _expiredChip = _filters.Chip(async (_, _) => await FilterAsync(expired: true, unshelved: false));
        _unshelvedChip = _filters.Chip(async (_, _) => await FilterAsync(expired: false, unshelved: true));

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.SerumBank");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(24, 16, 24, 16);

        _moreMenu.Items.Add(_purge);
        _head.Add(_refresh, _more, _shelve);
        ShortcutHints.Attach(_refresh, "F5");

        _filters.Controls.Add(_searchField);
        _filters.Controls.SetChildIndex(_searchField, 0);

        BuildColumns();

        // The selected tube's card: where it is, how long it stays, and what can be done with it.
        _aside.AddActions(_aliquot, _discard);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        body.Controls.Add(_card);
        body.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 16, BackColor = UiTheme.Surface });
        body.Controls.Add(_aside);

        Controls.Add(body);
        Controls.Add(Pilotage.Gap(14));
        Controls.Add(_filters);
        Controls.Add(Pilotage.Gap(10));
        Controls.Add(_head);
    }

    /// <summary>A chip: the server is asked again only when the past-retention filter changes.</summary>
    private async Task FilterAsync(bool expired, bool unshelved)
    {
        _onlyUnshelved = unshelved;
        if (_onlyExpired != expired)
        {
            _onlyExpired = expired;
            await ActivateAsync();
            return;
        }
        Render();
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

        // Read on the card of the selected tube: the list keeps the columns it is scanned by, and
        // fits beside the card at 1366 pixels.
        // The printed list (Ctrl+P) keeps them.
        foreach (var name in new[] { "specimen", "received", "volume", "aliquots" })
        {
            _grid.Column(name).Visible = false;
            _grid.Column(name).Tag = ListPrinting.PrintedWhenHidden;
        }
    }

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ActivateAsync(); }
        };

        _refresh.Click += async (_, _) => await ActivateAsync();

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _shelve.Click += async (_, _) => await RunBusyAsync(ShelveAsync);
        _aliquot.Click += async (_, _) => await RunBusyAsync(AliquotAsync);
        _discard.Click += async (_, _) => await RunBusyAsync(DiscardAsync);
        _purge.Click += async (_, _) => await RunBusyAsync(PurgeAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Samples.Receive, _shelve, _aliquot);
        PermissionGate.Require(Permissions.Samples.Reject, _discard);

        _grid.SelectionChanged += (_, _) => { RefreshActions(); ShowSelected(); };
        _grid.CellFormatting += OnCellFormatting;
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not StoredSampleRow row) return;

        // Past retention leads, because purging is the action this screen exists to enable; then the
        // tube received but nowhere; then a tube due within a week (SampleStorageScreenRules.Ink).
        switch (SampleStorageScreenRules.Ink(row))
        {
            case StorageInk.PastRetention:
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
                break;
            case StorageInk.Unshelved:
                e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
                e.CellStyle.ForeColor = UiTheme.Abnormal;
                break;
            case StorageInk.Soon:
                e.CellStyle!.ForeColor = UiTheme.Abnormal;
                break;
        }
    }

    protected override void ApplyTexts()
    {
        _search.PlaceholderText = T("Sample.StorageSearchPlaceholder");
        _head.Title.Text = T("Nav.SerumBank");
        _more.Text = T("ResultEntry.MoreActions");
        _aside.Empty.Text = T("Sample.SelectTube");

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
            // Named, and its signs explained on the header: a column headed by nothing but « ? »
            // in its cells read as a value nobody knew.
            _grid.Column("flags").HeaderText = T("Web.SampleFlags");
            _grid.Column("flags").ToolTipText = string.Join(" · ", $"? {T("Sample.NotShelved")}",
                                                            $"! {T("Sample.HasDefects")}",
                                                            $"R {T("Sample.WithReservation")}");
        }

        Render();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _samples.GetStoredSamplesAsync(
                SampleStorageScreenRules.Search(_search.Text), _onlyExpired, ct);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        // Left while the list loaded: its grid is emptied with the screen.
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                   && (IsDisposed || Disposing)) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the serum bank failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T(FailureMessages.KeyFor(ex));
        }
    }

    private void Render()
    {
        var kept = Selected?.SampleId;

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in SampleStorageScreenRules.Shown(_rows, Filter))
        {
            var index = _grid.Rows.Add(
                row.Barcode,
                row.AccessionNumber,
                row.PatientName,
                row.SpecimenTypeName,
                SampleStorageScreenRules.ReceivedAt(row),
                SampleStorageScreenRules.Place(row, T),
                SampleStorageScreenRules.KeepUntil(row),
                SampleStorageScreenRules.DaysLeft(row),
                SampleStorageScreenRules.Volume(row),
                SampleStorageScreenRules.Aliquots(row),
                SampleStorageScreenRules.Flags(row));

            _grid.Rows[index].Tag = row;
            _grid.Rows[index].Cells["flags"].ToolTipText = SampleStorageScreenRules.FlagsMeaning(row, T);
        }

        _grid.ResumeLayout(performLayout: true);

        _head.Count.Text = SampleStorageScreenRules.Count(_rows, T);

        // While the past-retention filter is on, the list holds only those tubes: the other
        // counts are not known and are not shown (SampleStorageScreenRules.Chips).
        var chips = SampleStorageScreenRules.Chips(_rows, Filter, T);
        _allChip.Text = chips[0].Caption;
        _expiredChip.Text = chips[1].Caption;
        _unshelvedChip.Text = chips[2].Caption;
        _allChip.Selected = !_onlyExpired && !_onlyUnshelved;
        _expiredChip.Selected = _onlyExpired;
        _unshelvedChip.Selected = _onlyUnshelved;
        _filters.PerformLayout();

        // The tube just shelved or aliquoted stays highlighted: back on the first line, the card
        // beside the list showed another patient's tube under the same buttons.
        var keptRow = kept is { } id
            ? _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is StoredSampleRow s && s.SampleId == id)
            : null;

        if (keptRow is { Index: > 0 }) _grid.MakeCurrent(keptRow);
        else if (_grid.Rows.Count > 0) _grid.SelectFirstRow();

        RefreshActions();
        ShowSelected();
    }

    /// <summary>The card of the highlighted tube, or the line that asks for one.</summary>
    private void ShowSelected()
    {
        if (Selected is not { } row)
        {
            _aside.ShowRow(false);
            return;
        }

        _aside.Heading.Font = Pilotage.CodeTitle;
        _aside.Heading.Text = row.Barcode;
        _aside.Subtitle.Text = SampleStorageScreenRules.Subtitle(row);
        _aside.SetPills([.. SampleStorageScreenRules.Pills(row, T).Select(p => (p.Text, p.Tone switch
        {
            "Critical" => EntryPaint.Critical,
            "Warning" => EntryPaint.Warning,
            _ => EntryPaint.Info
        }))]);
        _aside.SetFacts([.. SampleStorageScreenRules.Facts(row, T).Select(f => (f.Label, (string?)f.Value, true))]);
        _aside.ShowRow(true);
    }

    private StoredSampleRow? Selected => _grid.CurrentRow?.Tag as StoredSampleRow;

    private void RefreshActions()
    {
        // By tube and by right (SampleStorageScreenRules.Actions): aliquoting needs a tube that is
        // actually present and not already refused, the purge a tube past its retention. A menu entry
        // is not a control the gate can hold: the rule checks the right for it.
        var actions = SampleStorageScreenRules.Actions(Selected, _rows, p => PermissionGate.Allows(p));

        _shelve.Enabled = actions.Shelve;
        _discard.Enabled = actions.Discard;
        _aliquot.Enabled = actions.Aliquot;
        _purge.Enabled = actions.Purge;
        _purge.ForeColor = _purge.Enabled ? UiTheme.Abnormal : UiTheme.TextMuted;
        Pilotage.SyncMore(_more, _moreMenu);
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task ShelveAsync()
    {
        if (Selected is not { } row) return;

        using var dialog = new SampleStorageDialog(Localizer, _dialogs, row, await StoragePlacesAsync());

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _samples.MoveToStorageAsync(row.SampleId, dialog.StorageLocation,
                                                        dialog.Position, dialog.KeepUntil);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Sample.Shelve")); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = SampleStorageScreenRules.Shelved(row, dialog.StorageLocation);

        await ActivateAsync();
    }

    private async Task AliquotAsync()
    {
        if (Selected is not { } row) return;

        using var dialog = new SampleAliquotDialog(Localizer, _dialogs, row, await StoragePlacesAsync());

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _samples.CreateAliquotAsync(row.SampleId, dialog.VolumeMl,
                                                        dialog.Purpose, dialog.StorageLocation);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Sample.CreateAliquot")); return; }

        // The new barcode is what the operator has to write on the tube, so it is stated rather
        // than left to be found in a list.
        _dialogs.Info(T("Sample.AliquotCreated", result.Value), T("Sample.CreateAliquot"));

        await ActivateAsync();
    }

    /// <summary>
    /// The site's active storage places, read when a dialog opens so a place just created in the
    /// settings is offered. Empty when they cannot be read: the tube's own place is still shown.
    /// </summary>
    private async Task<IReadOnlyList<string>> StoragePlacesAsync()
    {
        try
        {
            var places = await _samples.GetStorageLocationsAsync();
            return places.IsSuccess ? places.Value : [];
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the storage places failed.");
            return [];
        }
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
                                             T("Sample.Discard"), maxLength: SampleStorageScreenRules.DiscardNoteMaxLength);

        if (note is null) return;

        var result = await _samples.DiscardAsync(row.SampleId, note);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Sample.Discard")); return; }

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = SampleStorageScreenRules.Discarded(row, T);

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
        var expired = SampleStorageScreenRules.PurgeCandidates(_rows);

        if (expired.Count == 0) return;

        if (!_dialogs.Confirm(T("Sample.ConfirmPurge", expired.Count), T("Sample.PurgeExpired")))
            return;

        var note = _dialogs.PromptForReason(T("Sample.PurgePrompt", expired.Count),
                                             T("Sample.PurgeExpired"), maxLength: SampleStorageScreenRules.DiscardNoteMaxLength);

        if (note is null) return;

        var discarded = 0;
        var failures = new List<string>();

        foreach (var row in expired)
        {
            var result = await _samples.DiscardAsync(row.SampleId, note);

            if (result.IsSuccess) discarded++;
            else failures.Add(row.Barcode);
        }

        var (outcome, partial) = SampleStorageScreenRules.PurgeOutcome(discarded, failures, T);
        _status.ForeColor = partial ? UiTheme.Abnormal : UiTheme.Normal;
        _status.Text = outcome;

        await ActivateAsync();
    }
}

/// <summary>Shelving a tube: where it went, and how long it is kept.</summary>
internal sealed class SampleStorageDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _location = new();
    private readonly IReadOnlyList<string> _places;
    private readonly TextBox _position = UiTheme.Input(180);
    private readonly DateTimePicker _keepUntil = new();
    private readonly CheckBox _hasKeepUntil = new();
    private readonly StoredSampleRow _row;

    /// <summary>
    /// Where the tube went. <c>StorageLocation</c>, not <c>Location</c>: that name belongs to
    /// <see cref="Form.Location"/> — the dialog's position on screen — and shadowing it means the
    /// next person to position this dialog, or to read its position, silently gets a shelf
    /// reference instead of a <see cref="Point"/>.
    /// </summary>
    internal string StorageLocation => StoragePlacePicker.Selected(_location) ?? string.Empty;
    internal string? Position => string.IsNullOrWhiteSpace(_position.Text) ? null : _position.Text.Trim();

    internal DateOnly? KeepUntil =>
        _hasKeepUntil.Checked ? DateOnly.FromDateTime(_keepUntil.Value.Date) : null;

    internal SampleStorageDialog(ILocalizer localizer, IUserDialogs dialogs, StoredSampleRow row,
                                 IReadOnlyList<string> places)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _places = places;
        _row = row;

        Text = SampleStorageScreenRules.ShelveTitle(row, localizer.Format);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        // The tube in the head, so the shelf is typed against the right one.
        var head = DialogFrame.Head(SampleStorageScreenRules.ShelveTitle(row, localizer.Format),
                                    SampleStorageScreenRules.ShelveSubtitle(row), 480);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised };

        var locationLabel = Pilotage.Caption(localizer["Sample.Storage"]);
        locationLabel.Location = new Point(20, 14);
        // The site's active places; the tube's own place kept and selected even when it is no
        // longer in the list.
        StoragePlacePicker.Configure(_location);
        _location.Bounds = new Rectangle(20, 34, 240, UiTheme.InputHeight);
        StoragePlacePicker.Fill(_location, places, row.StorageLocation, localizer["Sample.StorageChoose"],
                                place => localizer.Format("Sample.StorageOutOfList", place));

        var positionLabel = Pilotage.Caption(localizer["Sample.StoragePosition"]);
        positionLabel.Location = new Point(276, 14);
        _position.Bounds = new Rectangle(276, 34, 184, UiTheme.InputHeight);
        _position.Text = row.StoragePosition ?? string.Empty;
        _position.MaxLength = SampleStorageScreenRules.PositionMaxLength;
        _position.Tag = UiTags.LatinValue;

        _hasKeepUntil.Text = localizer["Sample.KeepUntil"];
        _hasKeepUntil.AutoSize = true;
        _hasKeepUntil.Font = UiTheme.SmallBold;
        _hasKeepUntil.ForeColor = UiTheme.TextSecondary;
        _hasKeepUntil.Location = new Point(20, 76);
        _hasKeepUntil.Checked = row.KeepUntil is not null;

        _keepUntil.Bounds = new Rectangle(20, 100, 200, UiTheme.InputHeight);
        _keepUntil.Format = DateTimePickerFormat.Short;
        _keepUntil.Enabled = _hasKeepUntil.Checked;
        _keepUntil.Value = SampleStorageScreenRules.ShelveFormFor(row, DateOnly.FromDateTime(DateTime.Today))
                                                   .KeepUntil.ToDateTime(TimeOnly.MinValue);

        _hasKeepUntil.CheckedChanged += (_, _) => _keepUntil.Enabled = _hasKeepUntil.Checked;

        var notice = new Label
        {
            Text = localizer["Sample.KeepUntilNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 136, 440, 34)
        };

        body.Controls.AddRange([locationLabel, _location, positionLabel, _position, _hasKeepUntil, _keepUntil, notice]);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.DialogResult = DialogResult.Cancel;

        ClientSize = new Size(480, head.Height + 180 + 58);
        Controls.Add(body);
        Controls.Add(DialogFrame.Foot(cancel, ok));
        Controls.Add(head);

        AcceptButton = ok;
        CancelButton = cancel;

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    /// <summary>
    /// A place is required — with none in the list, the operator is told where places are created —
    /// and a date already over is refused unless the tube already carries it
    /// (SampleStorageScreenRules.CheckShelve, the web's check).
    /// </summary>
    private void Accept()
    {
        var form = new ShelveForm
        {
            Location = StorageLocation,
            Position = Position,
            HasKeepUntil = _hasKeepUntil.Checked,
            KeepUntil = DateOnly.FromDateTime(_keepUntil.Value.Date)
        };

        var (refusal, _) = SampleStorageScreenRules.CheckShelve(form, _row, _places.Count, DateOnly.FromDateTime(DateTime.Today));

        if (refusal is not null)
        {
            _dialogs.Warn(_localizer[refusal.Key]);
            if (refusal.Field == SampleStorageScreenRules.KeepUntilField) _keepUntil.Focus();
            else _location.Focus();
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
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly TextBox _volume = UiTheme.Input(120);
    private readonly TextBox _purpose = UiTheme.Input(280);
    private readonly ComboBox _location = new();

    internal decimal? VolumeMl => EditorValues.ParseDecimal(_volume.Text);

    internal string? Purpose => string.IsNullOrWhiteSpace(_purpose.Text) ? null : _purpose.Text.Trim();

    /// <summary>The place chosen; null for the empty entry, which keeps the parent tube's place.</summary>
    internal string? StorageLocation => StoragePlacePicker.Selected(_location);

    internal SampleAliquotDialog(ILocalizer localizer, IUserDialogs dialogs, StoredSampleRow row,
                                 IReadOnlyList<string> places)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        Text = SampleStorageScreenRules.AliquotTitle(row, localizer.Format);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var head = DialogFrame.Head(SampleStorageScreenRules.AliquotTitle(row, localizer.Format),
                                    SampleStorageScreenRules.AliquotSubtitle(row, localizer.Format), 480);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised };

        var notice = new Label
        {
            Text = localizer["Sample.AliquotNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 12, 440, 34)
        };

        var volumeLabel = Pilotage.Caption(localizer["Sample.Volume"]);
        volumeLabel.Location = new Point(20, 52);
        _volume.Bounds = new Rectangle(20, 72, 120, UiTheme.InputHeight);
        _volume.Tag = UiTags.LatinValue;
        _volume.TextAlign = HorizontalAlignment.Right;
        _volume.MaxLength = SampleStorageScreenRules.VolumeMaxLength;

        var purposeLabel = Pilotage.Caption(localizer["Sample.AliquotPurpose"]);
        purposeLabel.Location = new Point(160, 52);
        _purpose.Bounds = new Rectangle(160, 72, 300, UiTheme.InputHeight);
        _purpose.MaxLength = SampleStorageScreenRules.PurposeMaxLength;

        var locationLabel = Pilotage.Caption(localizer["Sample.Storage"]);
        locationLabel.Location = new Point(20, 110);
        StoragePlacePicker.Configure(_location);
        _location.Bounds = new Rectangle(20, 130, 240, UiTheme.InputHeight);
        StoragePlacePicker.Fill(_location, places, row.StorageLocation, localizer["Sample.StorageNotSpecified"],
                                place => localizer.Format("Sample.StorageOutOfList", place));

        body.Controls.AddRange([notice, volumeLabel, _volume, purposeLabel, _purpose, locationLabel, _location]);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.DialogResult = DialogResult.Cancel;

        ClientSize = new Size(480, head.Height + 172 + 58);
        Controls.Add(body);
        Controls.Add(DialogFrame.Foot(cancel, ok));
        Controls.Add(head);

        AcceptButton = ok;
        CancelButton = cancel;

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    /// <summary>
    /// The volume is optional, but one typed and unreadable (or not positive) is said: it used to
    /// be dropped without a word and the aliquot recorded with no volume.
    /// </summary>
    private void Accept()
    {
        if (SampleStorageScreenRules.CheckAliquot(new AliquotForm { Volume = _volume.Text }).Refusal is not null)
        {
            _dialogs.Warn(_localizer["Sample_AliquotVolumeInvalid"]);
            _volume.Focus();
            _volume.SelectAll();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
