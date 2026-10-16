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
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        foreach (var label in new[] { _fromLabel, _toLabel })
        {
            label.Font = UiTheme.Small;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = true;
        }

        _fromLabel.Location = new Point(0, 42);
        _from.Bounds = new Rectangle(40, 38, 120, UiTheme.InputHeight);
        _toLabel.Location = new Point(172, 42);
        _to.Bounds = new Rectangle(208, 38, 120, UiTheme.InputHeight);
        _pendingOnly.Location = new Point(344, 40);
        _refresh.Bounds = new Rectangle(560, 34, 110, UiTheme.ButtonHeight);
        _reconcile.Bounds = new Rectangle(680, 34, 220, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_heading, _fromLabel, _from, _toLabel, _to, _pendingOnly, _refresh, _reconcile]);

        BankReconciliationView.AddColumns(_grid, ("opened", 11), ("closed", 11), ("operator", 15), ("workstation", 9),
                                          ("expected", 10), ("counted", 10), ("variance", 9), ("withdrawals", 10),
                                          ("noncash", 10), ("status", 14));
        foreach (DataGridViewColumn column in _grid.Columns)
            if (column.Index is >= 4 and <= 8) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not CashSessionReviewRow row) return;
            if (row.Variance is { } v && v != 0m && _grid.Columns[e.ColumnIndex].Name == "variance")
                e.CellStyle!.ForeColor = Math.Abs(v) > 10m ? UiTheme.Critical : UiTheme.Abnormal;
        };

        _detail.Dock = DockStyle.Bottom;
        _detail.Height = 70;
        _detail.Font = UiTheme.Small;
        _detail.ForeColor = UiTheme.TextSecondary;

        Controls.Add(_grid);
        Controls.Add(_detail);
        Controls.Add(bar);
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.CashSessionReview");
        _fromLabel.Text = T("Field.From");
        _toLabel.Text = T("Field.To");
        _pendingOnly.Text = T("Treasury.PendingOnly");
        _refresh.Text = T("Action.Refresh");
        _reconcile.Text = T("Treasury.ReconcileSession");

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

        _heading.Text = $"{T("Nav.CashSessionReview")} · {_rows.Count(r => !r.IsReconciled)}";
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
