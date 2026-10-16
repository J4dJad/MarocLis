using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Patients to bring back after a refused tube.
/// <para>
/// Every replacement tube not drawn yet, oldest refusal first, with the patient's number and the
/// calls already made. The row leaves the list on its own when the new tube is drawn on the
/// collection screen; when the patient will not come, it is closed here with a reason.
/// </para>
/// </summary>
public sealed class RecollectionView : LocalizedView
{
    private readonly RecollectionService _recollections;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<RecollectionView>? _logger;

    private readonly PageHead _head = new();
    private readonly FilterRow _filters = new();
    private readonly SearchField _searchField = new(340);
    private TextBox _search => _searchField.Box;
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly ListCard _card;
    private readonly AsideCard _aside = new();

    private readonly Button _call = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _sms = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _abandon = UiTheme.SecondaryButton(string.Empty);
    private Label _status => _card.Footer;

    // The chips filter the loaded list: everyone, those never reached, those the red rows mark.
    private enum Filter { All, NotContacted, Priority }
    private Filter _filter = Filter.All;
    private readonly ChipButton _allChip;
    private readonly ChipButton _notContactedChip;
    private readonly ChipButton _priorityChip;

    private IReadOnlyList<RecollectionRow> _rows = [];

    public RecollectionView(RecollectionService recollections, IUserDialogs dialogs,
                            ILocalizer localizer, ICultureManager cultureManager,
                            ILogger<RecollectionView>? logger = null)
        : base(localizer, cultureManager)
    {
        _recollections = recollections;
        _dialogs = dialogs;
        _logger = logger;

        _card = new ListCard(_grid);
        _allChip = _filters.Chip((_, _) => Choose(Filter.All));
        _notContactedChip = _filters.Chip((_, _) => Choose(Filter.NotContacted));
        _priorityChip = _filters.Chip((_, _) => Choose(Filter.Priority));

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Recollections");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(24, 16, 24, 16);

        _head.Add(_refresh, _call);
        ShortcutHints.Attach(_refresh, "F5");

        _filters.Controls.Add(_searchField);
        _filters.Controls.SetChildIndex(_searchField, 0);

        void Add(string name, int weight, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("rejected", 9, UiTheme.Monospace);
        Add("accession", 10, UiTheme.Monospace);
        Add("patient", 15);
        Add("phone", 11, UiTheme.Monospace);
        Add("analyses", 12, UiTheme.Small);
        Add("reason", 16, UiTheme.Small);
        Add("barcode", 10, UiTheme.Monospace);
        Add("contact", 17, UiTheme.Small);

        // The selected patient's card: who to call, why, and the two actions taken from there.
        _aside.AddActions(_sms, _abandon);

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

    private void WireBehaviour()
    {
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Render();
        };

        _refresh.Click += async (_, _) => await ActivateAsync();
        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _call.Click += async (_, _) => await RunBusyAsync(RecordCallAsync);
        _sms.Click += async (_, _) => await RunBusyAsync(SendSmsAsync);
        _abandon.Click += async (_, _) => await RunBusyAsync(AbandonAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Orders.Edit, _call, _sms);
        PermissionGate.Require(Permissions.Orders.Cancel, _abandon);

        _grid.CellFormatting += OnCellFormatting;
        _grid.SelectionChanged += (_, _) => ShowSelected();
    }

    private void Choose(Filter filter)
    {
        _filter = filter;
        Render();
    }

    private static bool IsOverdue(RecollectionRow row) =>
        row.ContactCount == 0 && DateTimeOffset.Now - row.RejectedAt > TimeSpan.FromHours(24);

    /// <summary>The rows drawn in red: an urgent file, or a patient not reached within a day.</summary>
    private static bool IsPriority(RecollectionRow row) => row.Priority != OrderPriority.Routine || IsOverdue(row);

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not RecollectionRow row) return;

        if (IsPriority(row))
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.ContactCount > 0)
        {
            e.CellStyle!.ForeColor = UiTheme.TextSecondary;
        }
    }

    protected override void ApplyTexts()
    {
        _search.PlaceholderText = T("Recollection.SearchPlaceholder");
        _refresh.Text = T("Action.Refresh");
        _head.Title.Text = T("Nav.Recollections");
        _aside.Empty.Text = T("Recollection.SelectRow");
        _call.Text = T("Recollection.RecordCall");
        _sms.Text = T("Recollection.SendSms");
        _abandon.Text = T("Recollection.Abandon");

        _grid.Column("rejected").HeaderText = T("Recollection.RejectedAt");
        _grid.Column("accession").HeaderText = T("Order.AccessionNumber");
        _grid.Column("patient").HeaderText = T("Patient.Title");
        _grid.Column("phone").HeaderText = T("Recollection.Phone");
        _grid.Column("analyses").HeaderText = T("Recollection.Analyses");
        _grid.Column("reason").HeaderText = T("Recollection.Reason");
        _grid.Column("barcode").HeaderText = T("Recollection.NewTube");
        _grid.Column("contact").HeaderText = T("Recollection.LastContact");

        Render();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _recollections.GetPendingAsync(ct);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            _sms.Enabled = _recollections.CanSendSms && PermissionGate.Allows(Permissions.Orders.Edit);
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the recollection list failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    private void Render()
    {
        var term = _search.Text.Trim();

        var rows = _filter switch
        {
            Filter.NotContacted => _rows.Where(r => r.ContactCount == 0),
            Filter.Priority => _rows.Where(IsPriority),
            _ => _rows
        };

        rows = term.Length == 0
            ? rows
            : rows.Where(r => r.AccessionNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                               || r.PatientName.Contains(term, StringComparison.OrdinalIgnoreCase)
                               || r.ReplacementBarcode.Contains(term, StringComparison.OrdinalIgnoreCase)
                               || (r.Phone?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in rows)
        {
            var reason = T($"RejectionReason.{row.Reason}") +
                         (string.IsNullOrWhiteSpace(row.Comment) ? string.Empty : $" — {row.Comment}");

            var contact = row.LastContactAt is { } at
                ? $"{at:dd/MM HH:mm}  {row.LastContactDetail}" +
                  (row.ContactCount > 1 ? $"  (×{row.ContactCount})" : string.Empty)
                : T("Recollection.NotContacted");

            var index = _grid.Rows.Add(
                row.RejectedAt.ToString("dd/MM HH:mm"),
                row.AccessionNumber,
                row.PatientName,
                row.Phone ?? string.Empty,
                row.AnalysisCodes,
                reason,
                row.ReplacementBarcode,
                contact);

            _grid.Rows[index].Tag = row;
        }

        _grid.ResumeLayout(performLayout: true);

        var notContacted = _rows.Count(r => r.ContactCount == 0);
        _head.Count.Text = T("Recollection.Count", _rows.Count, notContacted);
        _allChip.Text = $"{T("Recollection.FilterAll")} · {_rows.Count}";
        _notContactedChip.Text = $"{T("Recollection.NotContacted")} · {notContacted}";
        _priorityChip.Text = $"{T("Recollection.FilterPriority")} · {_rows.Count(IsPriority)}";
        _allChip.Selected = _filter == Filter.All;
        _notContactedChip.Selected = _filter == Filter.NotContacted;
        _priorityChip.Selected = _filter == Filter.Priority;
        _filters.PerformLayout();

        ShowSelected();
    }

    /// <summary>The card of the highlighted patient, or the line that asks for one.</summary>
    private void ShowSelected()
    {
        if (_grid.CurrentRow?.Tag is not RecollectionRow row)
        {
            _aside.ShowRow(false);
            return;
        }

        _aside.Heading.Text = row.PatientName;
        _aside.Subtitle.Text = row.Phone ?? T("Recollection.NoPhone");
        _aside.SetPills(
            (row.Priority != OrderPriority.Routine ? $"! {T($"Priority.{row.Priority}")}" : string.Empty, EntryPaint.Warning),
            (IsOverdue(row) ? $"! {T("Recollection.Overdue")}" : string.Empty, EntryPaint.Critical),
            (row.ContactCount == 0 ? $"○ {T("Recollection.NotContacted")}" : $"✓ {T("Recollection.Contacted", row.ContactCount)}",
             row.ContactCount == 0 ? EntryPaint.Waiting : Pilotage.Ok));
        _aside.SetFacts(
            (T("Order.AccessionNumber"), row.AccessionNumber, true),
            (T("Recollection.RejectedAt"), row.RejectedAt.ToString("dd/MM/yyyy HH:mm"), true),
            (T("Recollection.Analyses"), row.AnalysisCodes, false),
            (T("Recollection.Reason"), T($"RejectionReason.{row.Reason}")
                + (string.IsNullOrWhiteSpace(row.Comment) ? string.Empty : $" — {row.Comment}"), false),
            (T("Recollection.NewTube"), row.ReplacementBarcode, true),
            (T("Recollection.LastContact"), row.LastContactAt is { } at
                ? $"{at:dd/MM HH:mm}  {row.LastContactDetail}" : T("Recollection.NotContacted"), false));
        _aside.ShowRow(true);
    }

    private RecollectionRow? Current()
    {
        if (_grid.CurrentRow?.Tag is RecollectionRow row) return row;

        _dialogs.Warn(T("Recollection.SelectRow"));
        return null;
    }

    // ---- Actions -------------------------------------------------------------------------------------

    private async Task RecordCallAsync()
    {
        if (Current() is not { } row) return;

        var note = _dialogs.PromptForText(
            T("Recollection.CallPrompt", row.PatientName, row.Phone ?? "—"), T("Recollection.RecordCall"));

        if (note is null) return;

        await RunAsync(() => _recollections.RecordContactAsync(row.ReplacementSampleId, RecallChannel.Phone, note),
                       T("Recollection.CallRecorded", row.AccessionNumber));
    }

    private async Task SendSmsAsync()
    {
        if (Current() is not { } row) return;

        var phone = _dialogs.PromptForText(
            T("Recollection.SmsPrompt", row.PatientName), T("Recollection.SendSms"), row.Phone);

        if (string.IsNullOrWhiteSpace(phone)) return;

        await RunAsync(() => _recollections.SendRecallSmsAsync(row.ReplacementSampleId, phone),
                       T("Recollection.SmsSent", phone.Trim()));
    }

    private async Task AbandonAsync()
    {
        if (Current() is not { } row) return;

        var reason = _dialogs.PromptForReason(
            T("Recollection.AbandonPrompt", row.AccessionNumber, row.AnalysisCodes), T("Recollection.Abandon"));

        if (reason is null) return;

        await RunAsync(() => _recollections.AbandonAsync(row.ReplacementSampleId, reason),
                       T("Recollection.Abandoned", row.AccessionNumber));
    }

    private async Task RunAsync(Func<Task<Domain.Common.Result>> action, string done)
    {
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await action();

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _status.ForeColor = UiTheme.Normal;
            _status.Text = done;
            await ActivateAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Recollection action failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}
