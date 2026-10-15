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

    private readonly Label _heading = new();
    private readonly TextBox _search = UiTheme.Input(260);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();

    private readonly Button _call = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _sms = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _abandon = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<RecollectionRow> _rows = [];

    public RecollectionView(RecollectionService recollections, IUserDialogs dialogs,
                            ILocalizer localizer, ICultureManager cultureManager,
                            ILogger<RecollectionView>? logger = null)
        : base(localizer, cultureManager)
    {
        _recollections = recollections;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Recollections");

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
        _refresh.Bounds = new Rectangle(272, 27, 110, UiTheme.ButtonHeight - 4);

        bar.Controls.AddRange([_heading, _search, _refresh]);

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

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_grid);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.Surface };

        _call.Bounds = new Rectangle(0, 10, 220, UiTheme.ButtonHeight);
        _sms.Bounds = new Rectangle(230, 10, 200, UiTheme.ButtonHeight);
        _abandon.Bounds = new Rectangle(440, 10, 220, UiTheme.ButtonHeight);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(672, 16, 640, 20);

        actions.Controls.AddRange([_call, _sms, _abandon, _status]);

        Controls.Add(host);
        Controls.Add(actions);
        Controls.Add(bar);
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
    }

    private static bool IsOverdue(RecollectionRow row) =>
        row.ContactCount == 0 && DateTimeOffset.Now - row.RejectedAt > TimeSpan.FromHours(24);

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not RecollectionRow row) return;

        if (row.Priority != OrderPriority.Routine || IsOverdue(row))
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

        if (_rows.Count > 0) Render();
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

        var rows = term.Length == 0
            ? _rows
            : _rows.Where(r => r.AccessionNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
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

        _heading.Text = T("Recollection.Heading", _rows.Count, _rows.Count(r => r.ContactCount == 0));
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
