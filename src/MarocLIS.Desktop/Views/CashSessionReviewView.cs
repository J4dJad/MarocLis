using System.Globalization;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The closed tills waiting for the supervisor's sign-off (visa des caisses): the count, the
/// variance and its explanation, the cash banked during the shift. A session is signed off with a
/// comment, required when the till did not balance, and never by the operator who ran it.
/// </summary>
public sealed class CashSessionReviewView : LocalizedView
{
    private readonly CashSessionReviewService _review;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<CashSessionReviewView>? _logger;

    private readonly Label _heading = new();
    private readonly Label _fromLabel = new();
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short };
    private readonly Label _toLabel = new();
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short };
    private readonly CheckBox _pendingOnly = new() { AutoSize = true, Checked = true };
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _reconcile = UiTheme.PrimaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _detail = new();

    // Direction A: the count beside the title, chips in place of the tick box, and the selected
    // till's figures in a card at the side with the sign-off button.
    private readonly Label _count = new();
    private readonly ChipButton _pendingChip = new() { Height = 30 };
    private readonly ChipButton _allChip = new() { Height = 30 };
    private readonly Label _asideTitle = new();
    private readonly Label _asideWhen = new();
    private readonly PillLabel _asidePill = new();
    private readonly Label[] _factCaptions = [.. Enumerable.Range(0, 5).Select(_ => new Label())];
    private readonly Label[] _factValues = [.. Enumerable.Range(0, 5).Select(_ => new Label())];

    private IReadOnlyList<CashSessionReviewRow> _rows = [];

    public CashSessionReviewView(CashSessionReviewService review, IUserDialogs dialogs, ILocalizer localizer,
                                 ICultureManager cultureManager, ILogger<CashSessionReviewView>? logger = null)
        : base(localizer, cultureManager)
    {
        _review = review;
        _dialogs = dialogs;
        _logger = logger;

        _from.Value = DateTime.Today.AddDays(-30);
        _to.Value = DateTime.Today;

        BuildLayout();

        _refresh.Click += async (_, _) => await ActivateAsync();
        _pendingOnly.CheckedChanged += async (_, _) => await ActivateAsync();
        _reconcile.Click += async (_, _) => await RunBusyAsync(ReconcileAsync);
        _grid.SelectionChanged += (_, _) => ShowDetail();

        PermissionGate.Require(Permissions.Billing.ReconcileCashSession, _reconcile);
    }

    public override string Title => T("Nav.CashSessionReview");

    private CashSessionReviewRow? Current => _grid.CurrentRow?.Tag as CashSessionReviewRow;

    private void BuildLayout()
    {
        Padding = new Padding(24, 20, 24, 16);

        var header = FinanceUi.PageHeader(_heading, _count, _refresh);

        _from.Width = 120;
        _to.Width = 120;

        // « À viser seulement » is kept as the tick box it was, off screen: the chips set it, and
        // its change reloads the list as before.
        _pendingChip.Click += (_, _) => _pendingOnly.Checked = true;
        _allChip.Click += (_, _) => _pendingOnly.Checked = false;
        _pendingOnly.CheckedChanged += (_, _) => RenderChips();

        var filters = FinanceUi.FilterRow(FinanceUi.Inline(_fromLabel, _from), FinanceUi.Inline(_toLabel, _to),
                                          _pendingChip, _allChip);

        BankReconciliationView.AddColumns(_grid, ("opened", 11), ("closed", 11), ("operator", 15), ("workstation", 9),
                                          ("expected", 10), ("counted", 10), ("variance", 9), ("withdrawals", 10),
                                          ("noncash", 10), ("status", 14));
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            if (column.Index is >= 4 and <= 8)
            {
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                column.DefaultCellStyle.Font = UiTheme.Monospace;
            }
            else if (column.Index <= 1)
            {
                column.DefaultCellStyle.Font = UiTheme.Monospace;
            }
        }

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not CashSessionReviewRow row) return;
            if (row.Variance is { } v && v != 0m && _grid.Columns[e.ColumnIndex].Name == "variance")
                e.CellStyle!.ForeColor = Math.Abs(v) > 10m ? UiTheme.Critical : UiTheme.Abnormal;
        };

        FinanceUi.PillColumn(_grid, "status", row => row.Tag is CashSessionReviewRow session
            ? session.IsReconciled ? FinanceUi.PillDone : FinanceUi.PillWarning
            : null);

        var gridCard = new FinanceCard(_grid) { Margin = new Padding(0, 0, 8, 0) };

        // --- the selected till -------------------------------------------------------------------

        _asideTitle.Font = UiTheme.SectionTitle;
        _asideTitle.ForeColor = UiTheme.TextPrimary;
        _asideTitle.AutoSize = true;
        _asideTitle.Margin = new Padding(0, 0, 0, 2);

        FinanceUi.Muted(_asideWhen);
        _asideWhen.AutoSize = true;
        _asideWhen.Margin = new Padding(0, 0, 0, 8);

        _asidePill.Margin = new Padding(0, 0, 0, 10);

        var facts = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = _factValues.Length,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = UiTheme.SurfaceRaised,
            Dock = DockStyle.Top
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < _factValues.Length; i++)
        {
            FinanceUi.Muted(_factCaptions[i]);
            _factCaptions[i].AutoSize = true;
            _factCaptions[i].Margin = new Padding(0, 3, 8, 3);
            _factValues[i].Font = UiTheme.Monospace;
            _factValues[i].ForeColor = UiTheme.TextPrimary;
            _factValues[i].AutoSize = true;
            _factValues[i].Margin = new Padding(0, 3, 0, 3);
            facts.Controls.Add(_factCaptions[i], 0, i);
            facts.Controls.Add(_factValues[i], 1, i);
        }

        _detail.Dock = DockStyle.Fill;
        _detail.AutoSize = false;
        _detail.Font = UiTheme.Small;
        _detail.ForeColor = UiTheme.TextSecondary;
        _detail.Margin = new Padding(0);

        FinanceUi.Fit(_reconcile);
        _reconcile.Margin = new Padding(0, 8, 0, 0);
        _reconcile.Anchor = AnchorStyles.Left;

        var aside = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(16),
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        aside.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) aside.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        aside.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        aside.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        aside.Controls.Add(_asideTitle, 0, 0);
        aside.Controls.Add(_asideWhen, 0, 1);
        aside.Controls.Add(_asidePill, 0, 2);
        aside.Controls.Add(facts, 0, 3);
        aside.Controls.Add(_detail, 0, 4);
        aside.Controls.Add(_reconcile, 0, 5);

        var asideCard = new FinanceCard(aside) { Margin = new Padding(8, 0, 0, 0) };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(gridCard, 0, 0);
        body.Controls.Add(asideCard, 1, 0);

        Controls.Add(body);
        Controls.Add(filters);
        Controls.Add(header);
    }

    private void RenderChips()
    {
        var pending = _rows.Count(r => !r.IsReconciled);

        // « Toutes » counts only once the whole period is loaded; « À viser » counts in both cases.
        _pendingChip.Text = $"{T("Treasury.SessionToReview")} · {pending}";
        _allChip.Text = _pendingOnly.Checked ? T("Finance.AllSessions") : $"{T("Finance.AllSessions")} · {_rows.Count}";
        _pendingChip.Selected = _pendingOnly.Checked;
        _allChip.Selected = !_pendingOnly.Checked;
        _count.Text = T("Finance.ToReviewCount", pending);
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.CashSessionReview");
        _fromLabel.Text = T("Field.From");
        _toLabel.Text = T("Field.To");
        _pendingOnly.Text = T("Treasury.PendingOnly");
        _refresh.Text = T("Action.Refresh");
        _reconcile.Text = T("Treasury.ReconcileSession");

        string[] facts = [T("Payment.ExpectedCash"), T("Payment.CountedCash"), T("Payment.Variance"),
                          T("Payment.Withdrawals"), T("Treasury.NonCashReceipts")];
        for (var i = 0; i < facts.Length; i++) _factCaptions[i].Text = facts[i];

        string[] headers =
        [
            T("Treasury.OpenedAt"), T("Treasury.ClosedAt"), T("Treasury.Operator"), T("Treasury.Workstation"),
            T("Payment.ExpectedCash"), T("Payment.CountedCash"), T("Payment.Variance"), T("Payment.Withdrawals"),
            T("Treasury.NonCashReceipts"), T("Field.Status")
        ];
        for (var i = 0; i < headers.Length; i++) _grid.Columns[i].HeaderText = headers[i];

        Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _review.GetSessionsAsync(DateOnly.FromDateTime(_from.Value.Date),
                                                        DateOnly.FromDateTime(_to.Value.Date), _pendingOnly.Checked, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the tills to sign off failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private string Amount(decimal? value) => value is { } v ? Money.Of(v).Format(Localizer.CurrentLanguage) : "—";

    private static string When(DateTimeOffset? at) =>
        at?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—";

    private void Render()
    {
        _grid.Rows.Clear();
        foreach (var r in _rows)
        {
            var index = _grid.Rows.Add(When(r.OpenedAt), When(r.ClosedAt), r.OperatorName, r.Workstation ?? string.Empty,
                                       Amount(r.ExpectedClosingCash), Amount(r.CountedCash), Amount(r.Variance),
                                       Amount(r.CashWithdrawals), Amount(r.NonCashReceipts),
                                       r.IsReconciled ? T("Treasury.SessionReconciled") : T("Treasury.SessionToReview"));
            _grid.Rows[index].Tag = r;
        }

        _heading.Text = T("Nav.CashSessionReview");
        RenderChips();
        ShowDetail();
    }

    private void ShowDetail()
    {
        var row = Current;
        // Disabled up front on one's own till: the domain refuses it (four eyes), and it used to say
        // so only after the comment had been typed.
        var ownTill = row is { IsReconciled: false } && PermissionGate.User?.UserId == row.OperatorUserId;

        _reconcile.Enabled = row is not null && PermissionGate.User is { } user
                             && row.CanBeReconciledBy(user.UserId)
                             && PermissionGate.Allows(Permissions.Billing.ReconcileCashSession);

        RenderAside(row);

        if (row is null) { _detail.Text = string.Empty; return; }

        var lines = new List<string>();
        if (ownTill) lines.Add(T("CashSession_ReconcilerIsOperator"));
        if (!string.IsNullOrWhiteSpace(row.VarianceExplanation))
            lines.Add($"{T("Payment.VarianceExplanation")} : {row.VarianceExplanation}");
        if (row.IsReconciled)
            lines.Add(T("Treasury.ReconciledBy", row.ReconciledByName ?? "?", When(row.ReconciledAt),
                        row.ReconciliationComment ?? "—"));
        _detail.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>The selected till in the side card: who, when, its state and its figures.</summary>
    private void RenderAside(CashSessionReviewRow? row)
    {
        _asideTitle.Text = row?.OperatorName ?? T("Message.NothingSelected");
        _asideWhen.Text = row is null ? string.Empty
            : $"{When(row.OpenedAt)} → {When(row.ClosedAt)}" + (row.Workstation is { } post ? $" · {post}" : string.Empty);

        if (row is null)
        {
            _asidePill.Show(string.Empty, EntryPaint.Waiting);
        }
        else
        {
            var pill = row.IsReconciled ? FinanceUi.PillDone : FinanceUi.PillWarning;
            _asidePill.Show($"{pill.Glyph} {(row.IsReconciled ? T("Treasury.SessionReconciled") : T("Treasury.SessionToReview"))}",
                            (pill.Back, pill.Fore));
        }

        decimal?[] values = row is null ? [null, null, null, null, null]
            : [row.ExpectedClosingCash, row.CountedCash, row.Variance, row.CashWithdrawals, row.NonCashReceipts];
        for (var i = 0; i < values.Length; i++) _factValues[i].Text = row is null ? string.Empty : Amount(values[i]);

        _factValues[2].ForeColor = row?.Variance is { } v && v != 0m
            ? Math.Abs(v) > 10m ? UiTheme.Critical : UiTheme.Abnormal
            : UiTheme.TextPrimary;
    }

    private async Task ReconcileAsync()
    {
        if (Current is not { IsReconciled: false } row) return;

        // A till that balanced is signed off with an optional comment; one that did not needs one.
        string? comment;
        if (row.Variance is { } v && v != 0m)
        {
            comment = _dialogs.PromptForReason(T("Treasury.ReconcileCommentPrompt", row.OperatorName, Amount(v)),
                                               _reconcile.Text,
                                               maxLength: Domain.Billing.CashSession.ReconciliationCommentMaxLength);
            if (comment is null) return;
        }
        else
        {
            comment = _dialogs.PromptForText(T("Treasury.ReconcileOptionalPrompt", row.OperatorName), _reconcile.Text,
                                             maxLength: Domain.Billing.CashSession.ReconciliationCommentMaxLength);
            if (comment is null) return;
        }

        var result = await _review.ReconcileAsync(row.Id, comment);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _reconcile.Text); return; }

        await ActivateAsync();
    }
}
