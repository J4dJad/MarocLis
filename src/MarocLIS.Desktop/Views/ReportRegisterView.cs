using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Reports;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The register of released reports: what was issued, to whom it went, and what has not gone
/// anywhere yet.
/// <para>
/// Three service methods had no caller before this screen, and their absence was not three missing
/// conveniences but one compounding defect. <c>GetStoredPdfAsync</c> unreachable meant a reprint
/// re-rendered the report against today's catalogue instead of returning the document actually
/// issued. <c>RecordDeliveryAsync</c> unreachable meant no report was ever marked delivered — and
/// because the amendment guard tested exactly that flag, re-releasing quietly minted v2 and v3 of
/// the same report with none of them marked as a rectification. The guard has been changed to
/// treat any reissue of a <em>final</em> report as an amendment, so it no longer depends on
/// clerical bookkeeping; recording the hand-over is now possible as well.
/// </para>
/// <para>
/// The list leads on what has been released and not handed over, with a critical result first.
/// ISO 15189 §5.9 obliges the laboratory to make sure a panic value reaches a clinician, so an
/// undelivered report carrying one is the row somebody has to act on today — not a row to be
/// found by scrolling.
/// </para>
/// </summary>
public sealed class ReportRegisterView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly ReportReleaseService _reports;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ReportRegisterView>? _logger;

    private readonly TextBox _search = UiTheme.Input(280);
    private readonly CheckBox _onlyUndelivered = new();
    private readonly ComboBox _window = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _heading = new();

    private readonly DataGridView _register = UiTheme.Grid();
    private readonly DataGridView _deliveries = UiTheme.Grid();
    private readonly Label _deliveryHeading = new();

    private readonly Button _reprint = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _recordDelivery = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _amend = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<ReportRegisterRow> _rows = [];

    public ReportRegisterView(ReportReleaseService reports, IDocumentOutput documents, IUserDialogs dialogs,
                              ICurrentUser currentUser,
                              ILocalizer localizer, ICultureManager cultureManager,
                              ILogger<ReportRegisterView>? logger = null)
        : base(localizer, cultureManager)
    {
        _reports = reports;
        _documents = documents;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Reports");

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
        _search.Tag = UiTags.LatinValue;

        _onlyUndelivered.Font = UiTheme.Small;
        _onlyUndelivered.ForeColor = UiTheme.TextSecondary;
        _onlyUndelivered.AutoSize = true;
        _onlyUndelivered.Location = new Point(296, 34);

        _window.DropDownStyle = ComboBoxStyle.DropDownList;
        _window.FlatStyle = FlatStyle.Flat;
        _window.Bounds = new Rectangle(620, 28, 160, UiTheme.InputHeight);

        _refresh.Bounds = new Rectangle(792, 27, 110, UiTheme.ButtonHeight - 4);

        bar.Controls.AddRange([_heading, _search, _onlyUndelivered, _window, _refresh]);

        BuildRegisterColumns();
        BuildDeliveryColumns();

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 330,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        split.Panel1.Controls.Add(_register);

        _deliveryHeading.Font = UiTheme.SmallBold;
        _deliveryHeading.ForeColor = UiTheme.TextSecondary;
        _deliveryHeading.Dock = DockStyle.Top;
        _deliveryHeading.Height = 22;

        split.Panel2.Controls.Add(_deliveries);
        split.Panel2.Controls.Add(_deliveryHeading);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.Surface };

        _reprint.Bounds = new Rectangle(0, 10, 200, UiTheme.ButtonHeight);
        _recordDelivery.Bounds = new Rectangle(210, 10, 220, UiTheme.ButtonHeight);
        _amend.Bounds = new Rectangle(440, 10, 220, UiTheme.ButtonHeight);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(672, 16, 700, 20);

        actions.Controls.AddRange([_reprint, _recordDelivery, _amend, _status]);

        Controls.Add(split);
        Controls.Add(actions);
        Controls.Add(bar);
    }

    private void BuildRegisterColumns()
    {
        void Add(string name, int weight, Font? font = null, bool right = false)
            => _register.Columns.Add(new DataGridViewTextBoxColumn
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

        Add("accession", 11, UiTheme.MonospaceBold);
        Add("patient", 18);
        Add("version", 6, UiTheme.MonospaceBold, right: true);
        Add("completeness", 9, UiTheme.SmallBold);
        Add("released", 11, UiTheme.Monospace);
        Add("signatory", 13, UiTheme.Small);
        Add("prescriber", 13, UiTheme.Small);
        Add("delivery", 13, UiTheme.SmallBold);
        Add("flags", 12, UiTheme.SmallBold);
    }

    private void BuildDeliveryColumns()
    {
        void Add(string name, int weight, Font? font = null)
            => _deliveries.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("when", 14, UiTheme.Monospace);
        Add("channel", 14, UiTheme.Small);
        Add("recipientkind", 14, UiTheme.Small);
        Add("recipient", 18);
        Add("bearer", 16, UiTheme.Small);
        Add("status", 10, UiTheme.SmallBold);
        Add("by", 14, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await ActivateAsync();
        };

        _onlyUndelivered.CheckedChanged += async (_, _) => await ActivateAsync();
        _window.SelectedIndexChanged += async (_, _) => await ActivateAsync();
        _refresh.Click += async (_, _) => await ActivateAsync();

        _register.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadDeliveriesAsync());
        _register.CellFormatting += OnRegisterFormatting;
        _register.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await ReprintAsync(); };

        _reprint.Click += async (_, _) => await ReprintAsync();
        _recordDelivery.Click += async (_, _) => await RecordDeliveryAsync();
        _amend.Click += async (_, _) => await AmendAsync();
    }

    private void OnRegisterFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _register.Rows.Count) return;
        if (_register.Rows[e.RowIndex].Tag is not ReportRegisterRow row) return;

        if (row.IsSuperseded)
        {
            // Superseded versions stay in the list — a reprint of what a clinician actually holds
            // is a legitimate request — but they are visibly not the current document.
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            return;
        }

        if (row.IsUrgentToDeliver)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.AwaitingDelivery)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
        else if (row.IsAmendment)
        {
            e.CellStyle!.ForeColor = UiTheme.Accent;
        }
    }

    protected override void ApplyTexts()
    {
        _search.PlaceholderText = T("Report.RegisterSearchPlaceholder");
        _onlyUndelivered.Text = T("Report.OnlyUndelivered");
        _refresh.Text = T("Action.Refresh");

        _reprint.Text = T("Report.Reprint");
        _recordDelivery.Text = T("Report.RecordDelivery");
        _amend.Text = T("Report.IssueAmendment");

        _deliveryHeading.Text = T("Report.Deliveries");

        RefreshWindows();

        if (_register.Columns.Count > 0)
        {
            _register.Column("accession").HeaderText = T("Order.AccessionNumber");
            _register.Column("patient").HeaderText = T("Patient.Title");
            _register.Column("version").HeaderText = T("Report.Version");
            _register.Column("completeness").HeaderText = T("Report.Completeness");
            _register.Column("released").HeaderText = T("Report.ReleasedAt");
            _register.Column("signatory").HeaderText = T("Report.Signatory");
            _register.Column("prescriber").HeaderText = T("Order.Prescriber");
            _register.Column("delivery").HeaderText = T("Report.Delivery");
            _register.Column("flags").HeaderText = T("Field.Status");
        }

        if (_deliveries.Columns.Count > 0)
        {
            _deliveries.Column("when").HeaderText = T("Field.DateTime");
            _deliveries.Column("channel").HeaderText = T("Report.Channel");
            _deliveries.Column("recipientkind").HeaderText = T("Report.RecipientKind");
            _deliveries.Column("recipient").HeaderText = T("Report.Recipient");
            _deliveries.Column("bearer").HeaderText = T("Report.Bearer");
            _deliveries.Column("status").HeaderText = T("Field.Status");
            _deliveries.Column("by").HeaderText = T("Report.HandedOverBy");
        }

        if (_rows.Count > 0) Render();
        RefreshActions();
    }

    private void RefreshWindows()
    {
        var selected = _window.SelectedIndex;

        _window.Items.Clear();

        foreach (var days in new[] { 7, 30, 90, 365 })
            _window.Items.Add(new EnumChoice(days, T("Report.WithinDays", days)));

        _window.SelectedIndex = selected >= 0 ? selected : 1;
    }

    // ---- Loading -------------------------------------------------------------------------------------

    private int SelectedWindow => (_window.SelectedItem as EnumChoice)?.Value ?? 30;

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _reports.GetRegisterAsync(
                string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim(),
                _onlyUndelivered.Checked, SelectedWindow, ct);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();

            await LoadDeliveriesAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the report register failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    private void Render()
    {
        var language = Localizer.CurrentLanguage;
        var selected = Selected?.ReportId;

        _register.SuspendLayout();
        _register.Rows.Clear();

        foreach (var row in _rows)
        {
            var flags = new List<string>(4);

            if (row.IsSuperseded) flags.Add(T("Report.Superseded"));
            if (row.IsAmendment) flags.Add(T($"ReportAmendmentKind.{row.AmendmentKind}"));
            if (row.ContainsCriticalResults) flags.Add(T("Flag.Critical"));
            if (row.ContainsReservations) flags.Add(T("Report.WithReservations"));
            if (row.ContainsSubcontractedResults) flags.Add(T("Catalog.Subcontracted"));
            if (!row.AllResultsAccredited) flags.Add(T("Report.PartlyOutsideScope"));

            var delivery = row.WasDelivered
                ? $"{row.LastDeliveredAt!.Value.ToLocalTime():dd/MM/yy HH:mm}"
                  + (row.DeliveryCount > 1 ? $" ({row.DeliveryCount})" : string.Empty)
                : row.AwaitingDelivery
                    ? T($"ReportDeliveryPreference.{row.PrescriberPreference}")
                    : "—";

            var index = _register.Rows.Add(
                row.AccessionNumber,
                language == AppLanguage.Arabic && row.PatientNameArabic is { Length: > 0 } arabic
                    ? arabic
                    : row.PatientName,
                $"v{row.VersionNumber}",
                T($"ReportCompleteness.{row.Completeness}"),
                row.ReleasedAt.ToLocalTime().ToString("dd/MM/yy HH:mm"),
                row.SignatoryName,
                row.PrescriberName ?? string.Empty,
                delivery,
                string.Join(" · ", flags));

            _register.Rows[index].Tag = row;
        }

        _register.ResumeLayout(performLayout: true);

        if (selected is { } id)
        {
            foreach (DataGridViewRow row in _register.Rows)
            {
                if (row.Tag is ReportRegisterRow candidate && candidate.ReportId == id)
                {
                    row.Selected = true;
                    break;
                }
            }
        }

        if (_register.CurrentRow is null && _register.Rows.Count > 0)
            _register.SelectFirstRow();

        var undelivered = _rows.Count(r => r.AwaitingDelivery);
        var urgent = _rows.Count(r => r.IsUrgentToDeliver);

        var parts = new List<string>(3) { $"{_rows.Count}" };

        if (undelivered > 0)
            parts.Add($"{undelivered} {T("Report.AwaitingDelivery").ToLowerInvariant()}");

        if (urgent > 0)
            parts.Add($"{urgent} {T("Report.CriticalUndelivered").ToLowerInvariant()}");

        _heading.Text = $"{T("Report.Register")} — {string.Join(" · ", parts)}";
        _heading.ForeColor = urgent > 0 ? UiTheme.Critical : UiTheme.TextPrimary;

        RefreshActions();
    }

    private async Task LoadDeliveriesAsync(CancellationToken ct = default)
    {
        _deliveries.Rows.Clear();

        if (Selected is not { } row)
        {
            _deliveryHeading.Text = T("Report.Deliveries");
            RefreshActions();
            return;
        }

        var result = await _reports.GetDeliveriesAsync(row.ReportId, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _deliveries.SuspendLayout();

        foreach (var delivery in result.Value)
        {
            var index = _deliveries.Rows.Add(
                (delivery.CompletedAt ?? delivery.RequestedAt).ToLocalTime()
                    .ToString("dd/MM/yy HH:mm"),
                T($"DeliveryChannel.{delivery.Channel}"),
                T($"ReportRecipientKind.{delivery.RecipientKind}"),
                delivery.RecipientName ?? delivery.RecipientIdentifier ?? string.Empty,
                delivery.BearerName is { Length: > 0 } bearer
                    ? $"{bearer}{(delivery.BearerCin is { Length: > 0 } cin ? $" ({cin})" : string.Empty)}"
                    : string.Empty,
                T($"DeliveryStatus.{delivery.Status}"),
                delivery.HandedOverByName ?? string.Empty);

            var grid = _deliveries.Rows[index];
            grid.Tag = delivery;

            if (delivery.Status == DeliveryStatus.Failed)
                grid.DefaultCellStyle.ForeColor = UiTheme.Critical;
            else if (delivery.Status == DeliveryStatus.Delivered)
                grid.DefaultCellStyle.ForeColor = UiTheme.Normal;
        }

        _deliveries.ResumeLayout(performLayout: true);

        _deliveryHeading.Text = result.Value.Count == 0
            ? $"{T("Report.Deliveries")} — {T("Report.NoDeliveryRecorded")}"
            : $"{T("Report.Deliveries")} — {row.AccessionNumber} v{row.VersionNumber}";

        _deliveryHeading.ForeColor = result.Value.Count == 0 && row.AwaitingDelivery
            ? UiTheme.Abnormal
            : UiTheme.TextSecondary;

        RefreshActions();
    }

    private ReportRegisterRow? Selected => _register.CurrentRow?.Tag as ReportRegisterRow;

    private void RefreshActions()
    {
        var row = Selected;

        _reprint.Enabled = row is { HasStoredPdf: true }
                           && _currentUser.Has(Permissions.Reports.ReprintValidated);

        _recordDelivery.Enabled = row is not null
                                  && (_currentUser.Has(Permissions.Reports.Print)
                                      || _currentUser.Has(Permissions.Reports.Send));

        // An amendment supersedes the current version, so it is offered on that version only.
        _amend.Enabled = row is { IsSuperseded: false }
                         && _currentUser.Has(Permissions.Reports.Amend);
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the stored PDF — the document that was issued, byte for byte, with its hash checked
    /// against the archive. Never a re-render: a report re-rendered two years later would come out
    /// against a catalogue whose reference intervals and units have moved, which would make it a
    /// different document bearing the same number.
    /// </summary>
    private async Task ReprintAsync()
    {
        if (Selected is not { } row) return;

        var result = await _reports.GetStoredPdfAsync(row.ReportId);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Report.Reprint")); return; }

        try
        {
            var opened = await _documents.OpenAsync(result.Value, "pdf");

            if (opened.IsFailure) { _dialogs.ShowFailure(opened, T("Report.Reprint")); return; }

            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Report.ReprintLogged", row.AccessionNumber, row.VersionNumber);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Opening the stored report failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), T("Report.Reprint"));
        }
    }

    private async Task RecordDeliveryAsync()
    {
        if (Selected is not { } row) return;

        using var dialog = new ReportDeliveryDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _reports.RecordDeliveryAsync(
            row.ReportId, dialog.Channel, dialog.RecipientKind, dialog.RecipientName,
            dialog.RecipientIdentifier, dialog.BearerName, dialog.BearerCin,
            dialog.AuthorizingConsentId);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Report.RecordDelivery")); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Report.DeliveryRecorded", row.AccessionNumber, row.VersionNumber);

        await ActivateAsync();
    }

    /// <summary>
    /// Issues a rectified version.
    /// <para>
    /// The reason is mandatory and printed on the face of the new report, because a clinician
    /// holding the previous one needs to know what changed — an amended report that does not say
    /// what it amends is worse than no amendment, since the recipient has two documents and no way
    /// to tell which to act on.
    /// </para>
    /// </summary>
    private async Task AmendAsync()
    {
        if (Selected is not { } row) return;

        using var dialog = new ReportAmendmentDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _reports.ReleaseAsync(row.LabOrderId, dialog.Language, dialog.Conclusion,
                                                  dialog.Kind, dialog.Reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Report.IssueAmendment")); return; }

        var released = result.Value;

        try
        {
            var opened = await _documents.OpenAsync(released.Pdf, "pdf");

            if (opened.IsFailure) _dialogs.ShowFailure(opened, T("Report.IssueAmendment"));
        }
        catch (Exception ex)
        {
            // The amendment exists either way; it can be reprinted from this list.
            _logger?.LogError(ex, "Opening the amended report failed.");
        }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = T("Report.AmendmentIssued", released.VersionNumber, row.AccessionNumber);

        await ActivateAsync();
    }
}
