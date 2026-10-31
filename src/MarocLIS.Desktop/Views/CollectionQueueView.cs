using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// « À encaisser »: the site's files of the day on which the patient still owes something, oldest
/// first, a double-click (or « Encaisser », Entrée) opening « Encaissement » on the file. The till is
/// offered when the screen opens with none open. What is listed is <see cref="CounterCollectionService"/>'s
/// (the report counter's own payment status), for billing.payment.record, as on the web.
/// </summary>
public sealed class CollectionQueueView : LocalizedView
{
    private readonly CounterCollectionService _collection;
    private readonly CounterCollectionFlow _flow;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<CollectionQueueView>? _logger;

    private readonly Label _heading = new() { AutoSize = true, Font = UiTheme.PageTitle, ForeColor = UiTheme.TextPrimary };
    private readonly Label _intro = new() { AutoSize = true, Font = UiTheme.Body, ForeColor = UiTheme.TextSecondary, UseMnemonic = false };
    private readonly Label _total = new() { AutoSize = true, Font = UiTheme.BodyBold, ForeColor = UiTheme.TextPrimary, UseMnemonic = false };
    private readonly Label _status = new() { AutoSize = true, Font = UiTheme.Body, ForeColor = UiTheme.Normal, UseMnemonic = false };
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _collect = UiTheme.PrimaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();

    private IReadOnlyList<CollectionQueueRow> _rows = [];

    public CollectionQueueView(CounterCollectionService collection, CounterCollectionFlow flow, IUserDialogs dialogs,
                               ILocalizer localizer, ICultureManager cultureManager,
                               ILogger<CollectionQueueView>? logger = null)
        : base(localizer, cultureManager)
    {
        _collection = collection;
        _flow = flow;
        _dialogs = dialogs;
        _logger = logger;
        Padding = new Padding(24, 16, 24, 16);
        BackColor = UiTheme.Surface;

        foreach (var (name, weight, numeric) in new[]
                 {
                     ("time", 8, false), ("reference", 12, false), ("patient", 26, false),
                     ("state", 22, false), ("paid", 12, true), ("due", 12, true)
                 })
            ReportGrids.AddColumn(_grid, name, weight, numeric);
        _grid.Column("time").DefaultCellStyle.Font = UiTheme.Monospace;
        _grid.Column("reference").DefaultCellStyle.Font = UiTheme.MonospaceBold;
        _grid.Column("paid").DefaultCellStyle.Font = UiTheme.Monospace;
        _grid.Column("due").DefaultCellStyle.Font = UiTheme.MonospaceBold;
        _grid.Column("due").DefaultCellStyle.ForeColor = UiTheme.Accent;
        _grid.Dock = DockStyle.Fill;

        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 10),
            BackColor = UiTheme.Surface
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var button in new[] { _refresh, _collect })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 34);
            button.Margin = new Padding(8, 0, 0, 0);
            button.Anchor = AnchorStyles.Right;
        }

        _intro.Margin = new Padding(0, 4, 0, 6);
        _total.Margin = new Padding(0, 2, 0, 0);
        _status.Margin = new Padding(0, 2, 0, 0);

        head.Controls.Add(_heading, 0, 0);
        head.Controls.Add(_refresh, 1, 0);
        head.Controls.Add(_collect, 2, 0);
        head.Controls.Add(_intro, 0, 1);
        head.SetColumnSpan(_intro, 3);
        var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = UiTheme.Surface };
        line.Controls.AddRange([_total, _status]);
        head.Controls.Add(line, 0, 2);
        head.SetColumnSpan(line, 3);

        var card = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised, Padding = new Padding(1) };
        UiChrome.AsCard(card);
        card.Controls.Add(_grid);

        Controls.Add(card);
        Controls.Add(head);

        // Whoever takes no payment has no « Encaisser » (the menu does not offer the screen either).
        PermissionGate.Require(Permissions.Billing.RecordPayment, _collect);
        ShortcutHints.Attach(_refresh, "F5");

        _refresh.Click += async (_, _) => await RunBusyAsync(() => LoadAsync());
        _collect.Click += async (_, _) => await RunBusyAsync(CollectSelectedAsync);
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await RunBusyAsync(CollectSelectedAsync); };
        _grid.SelectionChanged += (_, _) => _collect.Enabled = _grid.CurrentRow?.Tag is CollectionQueueRow;
    }

    public override string Title => T("Nav.ToCollect");

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.ToCollect");
        _intro.Text = T("Collect.QueueIntro");
        _refresh.Text = T("Action.Refresh");
        _collect.Text = T("Collect.Action");
        foreach (var name in new[] { "time", "reference", "patient", "state", "paid", "due" })
            _grid.Column(name).HeaderText = T($"Collect.Column.{name}");
        Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        await _flow.OfferTillAsync();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5:
                if (_refresh.Enabled) _refresh.PerformClick();
                return true;
            case Keys.Enter when _grid.Focused:
                if (_collect.Enabled) _collect.PerformClick();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _collection.GetQueueAsync(ct);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result, T("Nav.ToCollect"));
                return;
            }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the collection queue failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), T("Nav.ToCollect"));
        }
    }

    private string Mad(decimal amount) => Money.Of(amount).Format(Localizer.CurrentLanguage);

    private void Render()
    {
        var selected = (_grid.CurrentRow?.Tag as CollectionQueueRow)?.LabOrderId;
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var state = T(CounterCollectionRules.StateKey(row.State));
            var index = _grid.Rows.Add(
                row.RegisteredAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                row.AccessionNumber,
                row.PatientName,
                row.InvoiceNumber is { } number ? $"{state} · {number}" : state,
                row.AlreadyPaid > 0m ? Mad(row.AlreadyPaid) : "—",
                Mad(row.Due));
            _grid.Rows[index].Tag = row;
            if (row.LabOrderId == selected) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _total.Text = _rows.Count == 0
            ? T("Collect.QueueEmpty")
            : T("Collect.QueueTotal", _rows.Count, Mad(CounterCollectionRules.QueueTotal(_rows)));
        _total.ForeColor = _rows.Count == 0 ? UiTheme.Normal : UiTheme.TextPrimary;
        _collect.Enabled = _grid.CurrentRow?.Tag is CollectionQueueRow;
    }

    private async Task CollectSelectedAsync()
    {
        if (_grid.CurrentRow?.Tag is not CollectionQueueRow row || PermissionGate.IsBlocked(_collect)) return;

        var done = await _flow.CollectAsync(FindForm(), row.LabOrderId);
        if (done is null) return;

        _status.Text = "  " + _flow.Say(done);
        await LoadAsync();
    }
}
