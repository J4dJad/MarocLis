using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Analyses sent to partner laboratories: what is waiting to leave, and what has not come back.
/// <para>
/// One list, in the order the work is done: tubes to send first, then those sent, oldest first,
/// with the late ones in red. Sending numbers a dispatch slip per partner and prints it at once,
/// because the slip is what travels in the cool box and what the partner signs. The return needs no
/// button here: entering the partner's results on the ordinary result entry screen takes the act
/// off this list.
/// </para>
/// </summary>
public sealed class SubcontractingView : LocalizedView
{
    private const string ColSelected = "selected";

    private readonly SubcontractingService _subcontracting;
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<SubcontractingView>? _logger;

    private readonly PageHead _head = new();
    private readonly FilterRow _filters = new();
    private readonly SearchField _searchField = new(340);
    private TextBox _search => _searchField.Box;
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly ListCard _card;

    private readonly Button _send = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _reprint = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly Button _more;
    private readonly ToolStripMenuItem _reference = Pilotage.MenuItem("Subcontract.PartnerReference");
    private Label _status => _card.Footer;

    // The stage filter, as chips: all, to send, awaiting return, late — the order of the old list.
    private int _stage;
    private readonly ChipButton[] _stageChips;

    private IReadOnlyList<SubcontractRow> _rows = [];

    public SubcontractingView(SubcontractingService subcontracting, IListPdfRenderer lists,
                              IDocumentOutput documents, IUserDialogs dialogs, ICurrentUser currentUser,
                              ILocalizer localizer, ICultureManager cultureManager,
                              ILogger<SubcontractingView>? logger = null)
        : base(localizer, cultureManager)
    {
        _subcontracting = subcontracting;
        _lists = lists;
        _documents = documents;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _logger = logger;

        _card = new ListCard(_grid);
        _more = Pilotage.MoreButton(_moreMenu);
        _stageChips = [.. Enumerable.Range(0, 4).Select(i => _filters.Chip((_, _) => ChooseStage(i)))];

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Subcontracting");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(24, 16, 24, 16);

        _moreMenu.Items.Add(_reference);
        _head.Add(_refresh, _more, _reprint, _send);
        ShortcutHints.Attach(_refresh, "F5");

        _filters.Controls.Add(_searchField);
        _filters.Controls.SetChildIndex(_searchField, 0);

        BuildColumns();

        // The stage as a pill: to send in accent, awaiting return in grey, late in red.
        Pilotage.Pills(_grid, "stage", row => row.Tag is not SubcontractRow r ? null
            : IsLate(r) ? (UiTheme.CriticalBackground, UiTheme.CriticalInk, "!")
            : r.Stage == SubcontractStage.ToSend ? (UiTheme.InfoBackground, UiTheme.Info, "→")
            : (UiTheme.SurfaceSunken, UiTheme.TextPrimary, "◐"));

        Controls.Add(_card);
        Controls.Add(Pilotage.Gap(14));
        Controls.Add(_filters);
        Controls.Add(Pilotage.Gap(10));
        Controls.Add(_head);
    }

    private void ChooseStage(int stage)
    {
        _stage = stage;
        Render();
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();
        _grid.ReadOnly = false;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColSelected, FillWeight = 4,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        void Add(string name, int weight, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("stage", 9, UiTheme.SmallBold);
        Add("accession", 10, UiTheme.Monospace);
        Add("patient", 14);
        Add("analysis", 15);
        Add("barcode", 10, UiTheme.Monospace);
        Add("partner", 13, UiTheme.Small);
        Add("sent", 16, UiTheme.Monospace);
        Add("reference", 9, UiTheme.Monospace);
        Add("due", 9, UiTheme.Monospace);
    }

    private void WireBehaviour()
    {
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Render();
        };

        _refresh.Click += async (_, _) => await ActivateAsync();

        _send.Click += async (_, _) => await RunBusyAsync(SendAsync);
        _reprint.Click += async (_, _) => await ReprintAsync();
        _reference.Click += async (_, _) => await RunBusyAsync(SetReferenceAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Samples.Subcontract, _send);
        // A menu entry is not a control the gate can hold: the right is checked here instead.
        _reference.Enabled = PermissionGate.Allows(Permissions.Samples.Subcontract);

        _grid.CellFormatting += OnCellFormatting;

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.OwningColumn?.Name == ColSelected)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _grid.SelectionChanged += (_, _) => RefreshActions();
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == ColSelected) RefreshActions();
        };
    }

    /// <summary>
    /// Each action is offered for the rows it applies to — the ticked ones, or the highlighted one —
    /// as <see cref="Ticked"/> reads them. « Envoyer » and « Réimprimer » stayed enabled on an
    /// empty list and answered with a line in the footer.
    /// </summary>
    private void RefreshActions()
    {
        var ticked = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[ColSelected].Value is true)
            .Select(r => r.Tag).OfType<SubcontractRow>().ToList();

        if (ticked.Count == 0 && _grid.CurrentRow?.Tag is SubcontractRow current) ticked.Add(current);

        _send.Enabled = ticked.Any(r => r.Stage == SubcontractStage.ToSend);
        _reprint.Enabled = ticked.Any(r => r.DispatchNumber is not null);
        // A menu entry is not a control the gate can hold: the right is checked here instead.
        _reference.Enabled = PermissionGate.Allows(Permissions.Samples.Subcontract)
                             && ticked.Any(r => r.Stage == SubcontractStage.AwaitingReturn);
    }

    private bool IsLate(SubcontractRow row) =>
        row.Stage == SubcontractStage.AwaitingReturn && row.DueAt is { } due && due < DateTimeOffset.Now;

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not SubcontractRow row) return;

        if (IsLate(row))
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.Stage == SubcontractStage.ToSend)
        {
            if (_grid.Columns[e.ColumnIndex].Name == "stage") e.CellStyle!.ForeColor = UiTheme.Accent;
            if (row.Priority != OrderPriority.Routine) e.CellStyle!.ForeColor = UiTheme.Critical;
        }
        else
        {
            e.CellStyle!.ForeColor = UiTheme.TextSecondary;
        }
    }

    protected override void ApplyTexts()
    {
        _search.PlaceholderText = T("Subcontract.SearchPlaceholder");
        _send.Text = T("Subcontract.Send");
        _reprint.Text = T("Subcontract.ReprintSlip");
        _reference.Text = T("Subcontract.PartnerReference");
        _more.Text = T("ResultEntry.MoreActions");
        _head.Title.Text = T("Nav.Subcontracting");
        _refresh.Text = T("Action.Refresh");


        if (_grid.Columns.Count > 0)
        {
            _grid.Column(ColSelected).HeaderText = string.Empty;
            _grid.Column("stage").HeaderText = T("Field.Status");
            _grid.Column("accession").HeaderText = T("Order.AccessionNumber");
            _grid.Column("patient").HeaderText = T("Patient.Title");
            _grid.Column("analysis").HeaderText = T("Catalog.Analysis");
            _grid.Column("barcode").HeaderText = T("Sample.Barcode");
            _grid.Column("partner").HeaderText = T("Catalog.SubcontractorSite");
            _grid.Column("sent").HeaderText = T("Subcontract.Slip");
            _grid.Column("reference").HeaderText = T("Subcontract.PartnerReferenceShort");
            _grid.Column("due").HeaderText = T("Subcontract.DueAt");

            // The status is drawn as a pill, wider than its words: sized for the widest one, or
            // « À envoyer » came out as « → À envo… » at 1366.
            _grid.Column("stage").MinimumWidth = new[] { "Subcontract.StageToSend", "Subcontract.StageAwaitingReturn", "Subcontract.Late" }
                .Max(key => EntryPaint.MeasurePill($"→ {T(key)}", UiTheme.SmallBold, 8).Width) + 16;
        }

        Render();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _subcontracting.GetWorklistAsync(ct);

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
            _logger?.LogError(ex, "Loading the subcontracting list failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T(FailureMessages.KeyFor(ex));
        }
    }

    private IEnumerable<SubcontractRow> ShownRows()
    {
        var rows = _stage switch
        {
            1 => _rows.Where(r => r.Stage == SubcontractStage.ToSend),
            2 => _rows.Where(r => r.Stage == SubcontractStage.AwaitingReturn),
            3 => _rows.Where(IsLate),
            _ => _rows
        };

        var term = _search.Text.Trim();

        if (term.Length > 0)
            rows = rows.Where(r => r.AccessionNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || r.PatientName.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || (r.Barcode?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (r.DispatchNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (r.PartnerReference?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));

        return rows;
    }

    private void Render()
    {
        var language = Localizer.CurrentLanguage;
        var kept = (_grid.CurrentRow?.Tag as SubcontractRow)?.ItemId;

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in ShownRows())
        {
            var index = _grid.Rows.Add(
                false,
                IsLate(row)
                    ? T("Subcontract.Late")
                    : T($"Subcontract.Stage{row.Stage}"),
                row.AccessionNumber,
                row.PatientName,
                $"{row.AnalysisCode}  {row.AnalysisName.For(language)}",
                row.Barcode ?? string.Empty,
                row.PartnerName?.For(language) ?? T("Subcontract.NoPartner"),
                row.SentAt is { } sentAt ? $"{row.DispatchNumber}  {sentAt:dd/MM HH:mm}" : string.Empty,
                row.PartnerReference ?? string.Empty,
                row.Stage == SubcontractStage.AwaitingReturn
                    ? row.DueAt?.ToString("dd/MM HH:mm") ?? string.Empty
                    : string.Empty);

            _grid.Rows[index].Tag = row;
        }

        _grid.ResumeLayout(performLayout: true);

        // The act just given its partner reference stays highlighted, rather than the first line.
        if (kept is { } keptId
            && _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is SubcontractRow s && s.ItemId == keptId)
                is { Index: > 0 } keptRow)
            _grid.MakeCurrent(keptRow);

        RefreshActions();

        var toSend = _rows.Count(r => r.Stage == SubcontractStage.ToSend);
        var awaiting = _rows.Count(r => r.Stage == SubcontractStage.AwaitingReturn);
        var late = _rows.Count(IsLate);

        _head.Count.Text = T("Subcontract.Count", toSend, awaiting, late);

        var counts = new[] { _rows.Count, toSend, awaiting, late };
        var captions = new[] { "Subcontract.StageAll", "Subcontract.StageToSend", "Subcontract.StageAwaitingReturn", "Subcontract.Late" };
        for (var i = 0; i < _stageChips.Length; i++)
        {
            _stageChips[i].Text = $"{T(captions[i])} · {counts[i]}";
            _stageChips[i].Selected = i == _stage;
        }
        _filters.PerformLayout();
    }

    private List<SubcontractRow> Ticked()
    {
        _grid.EndEdit();

        var ticked = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[ColSelected].Value is true && r.Tag is SubcontractRow)
            .Select(r => (SubcontractRow)r.Tag!)
            .ToList();

        // Nothing ticked: the highlighted row is what the operator means.
        if (ticked.Count == 0 && _grid.CurrentRow?.Tag is SubcontractRow current) ticked.Add(current);

        return ticked;
    }

    // ---- Actions -------------------------------------------------------------------------------------

    private async Task SendAsync()
    {
        var selected = Ticked().Where(r => r.Stage == SubcontractStage.ToSend).ToList();

        if (selected.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Subcontract_NothingSelected");
            return;
        }

        var partners = await _subcontracting.GetPartnersAsync();
        if (partners.IsFailure) { _dialogs.ShowFailure(partners); return; }

        if (partners.Value.Count == 0)
        {
            _dialogs.Warn(T("Catalog_NoPartnerSiteConfigured"), T("Subcontract.Send"));
            return;
        }

        using var dialog = new SubcontractDispatchDialog(Localizer, selected, partners.Value);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _send.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _subcontracting.DispatchAsync(
                [.. selected.Select(r => r.ItemId)], dialog.PartnerOverride);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result, T("Subcontract.Send"));
                return;
            }

            _status.ForeColor = UiTheme.Normal;
            _status.Text = T("Subcontract.Sent", string.Join(", ", result.Value.Select(d => d.Number)));

            foreach (var dispatch in result.Value) await PrintSlipAsync(dispatch.Number);

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            RefreshActions();
        }
    }

    private async Task ReprintAsync()
    {
        var slips = Ticked().Select(r => r.DispatchNumber).OfType<string>().Distinct().ToList();

        if (slips.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Subcontract.SelectSentRow");
            return;
        }

        foreach (var number in slips) await PrintSlipAsync(number);
    }

    private async Task PrintSlipAsync(string number)
    {
        try
        {
            var slip = await _subcontracting.GetSlipAsync(number);
            if (slip.IsFailure) { _dialogs.ShowFailure(slip, T("Subcontract.ReprintSlip")); return; }

            var opened = await _documents.OpenAsync(_lists.Render(BuildSlip(slip.Value)), "pdf");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, T("Action.Print"));
        }
        catch (Exception ex)
        {
            // Logged with its detail; the operator gets the message in their language, not the
            // runtime's English text.
            _logger?.LogError(ex, "Printing dispatch slip {Number} failed.", number);
            _dialogs.Error(T(FailureMessages.KeyFor(ex)), T("Action.Print"));
        }
    }

    /// <summary>
    /// The slip: one line per analysis, with what the partner needs to identify the tube and the
    /// patient, and a line at the end for the courier's and the partner's signatures.
    /// </summary>
    private ListDocument BuildSlip(SubcontractSlip slip)
    {
        var language = Localizer.CurrentLanguage;

        var rows = slip.Items.Select(i => (IReadOnlyList<string>)
        [
            i.AccessionNumber,
            i.PatientName,
            $"{T($"Gender.{i.Gender}")} {i.DateOfBirth?.ToString("dd/MM/yyyy") ?? string.Empty}".Trim(),
            i.Barcode ?? string.Empty,
            $"{i.AnalysisCode}  {i.AnalysisName.For(language)}",
            i.ContainerName?.For(language) ?? string.Empty,
            i.CollectedAt?.ToString("dd/MM/yyyy HH:mm") ?? string.Empty,
            i.Priority == OrderPriority.Routine ? string.Empty : T($"Priority.{i.Priority}")
        ]).ToList();

        rows.Add(["", "", "", "", "", "", "", ""]);
        rows.Add([T("Subcontract.SignatureCourier"), "", "", "", T("Subcontract.SignaturePartner"), "", "", ""]);

        return new ListDocument(
            $"{T("Subcontract.SlipTitle")} {slip.Number}",
            T("Subcontract.SlipCaption", _currentUser.SiteCode,
              $"{slip.Partner.Code}  {slip.Partner.Name.For(language)}",
              slip.SentAt.ToString("dd/MM/yyyy HH:mm"), slip.SentByName, slip.Items.Count),
            [
                T("Order.AccessionNumber"), T("Patient.Title"), T("Subcontract.SexBirth"),
                T("Sample.Barcode"), T("Catalog.Analysis"), T("Subcontract.Container"),
                T("Sample.CollectedAt"), T("Subcontract.Priority")
            ],
            rows,
            language.IsRightToLeft());
    }

    private async Task SetReferenceAsync()
    {
        var row = Ticked().FirstOrDefault(r => r.Stage == SubcontractStage.AwaitingReturn);

        if (row is null)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Subcontract.SelectSentRow");
            return;
        }

        var value = _dialogs.PromptForText(
            T("Subcontract.PartnerReferencePrompt", row.AccessionNumber, row.PartnerName?.For(Localizer.CurrentLanguage)),
            T("Subcontract.PartnerReference"), row.PartnerReference, maxLength: 60);

        if (value is null) return;

        var result = await _subcontracting.SetPartnerReferenceAsync(row.ItemId, value);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Subcontract.PartnerReference"));
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await ActivateAsync();
    }
}

/// <summary>
/// Confirms a send-out. The partner defaults to the one the catalogue names for each analysis; a
/// different one can be chosen for the whole batch, for the day the usual one cannot take samples.
/// </summary>
internal sealed class SubcontractDispatchDialog : Form
{
    private readonly ComboBox _partner = new();

    internal Guid? PartnerOverride =>
        _partner.SelectedItem is PartnerChoice { Id: { } id } ? id : null;

    internal SubcontractDispatchDialog(ILocalizer localizer, IReadOnlyList<SubcontractRow> rows,
                                       IReadOnlyList<SubcontractPartner> partners)
    {
        var language = localizer.CurrentLanguage;

        Text = localizer["Subcontract.Send"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var head = DialogFrame.Head(localizer["Subcontract.Send"],
                                    localizer.Format("Subcontract.SendSummary", rows.Count,
                                                     rows.Select(r => r.LabOrderId).Distinct().Count()), 520);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised };

        var list = new ListBox
        {
            Bounds = new Rectangle(20, 14, 480, 150),
            Font = UiTheme.Monospace,
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle
        };

        foreach (var row in rows)
            list.Items.Add($"{row.AccessionNumber}  ·  {row.AnalysisCode}  ·  {row.Barcode}  →  " +
                           (row.PartnerCode ?? localizer["Subcontract.NoPartner"]));

        var partnerLabel = Pilotage.Caption(localizer["Catalog.SubcontractorSite"]);
        partnerLabel.Location = new Point(20, 176);

        _partner.DropDownStyle = ComboBoxStyle.DropDownList;
        _partner.FlatStyle = FlatStyle.Flat;
        _partner.Bounds = new Rectangle(20, 196, 480, UiTheme.InputHeight);
        _partner.Items.Add(new PartnerChoice(null, localizer["Subcontract.PartnerFromCatalogue"]));

        foreach (var partner in partners)
            _partner.Items.Add(new PartnerChoice(partner.Id, $"{partner.Code}  {partner.Name.For(language)}"));

        _partner.SelectedIndex = 0;

        body.Controls.AddRange([list, partnerLabel, _partner]);

        var ok = UiTheme.PrimaryButton(localizer["Subcontract.SendAndPrint"]);
        ok.DialogResult = DialogResult.OK;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.DialogResult = DialogResult.Cancel;

        ClientSize = new Size(520, head.Height + 238 + 58);
        Controls.Add(body);
        Controls.Add(DialogFrame.Foot(cancel, ok));
        Controls.Add(head);

        AcceptButton = ok;
        CancelButton = cancel;

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    private sealed record PartnerChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }
}
