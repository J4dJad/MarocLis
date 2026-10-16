using MarocLIS.Application.Billing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The till — opening, the running shift, and closing with reconciliation.
/// <para>
/// The closing figure is the point of this screen. The operator counts the drawer and types what
/// they counted; the variance is computed and, beyond tolerance, must be explained before the
/// session will close. That is deliberate friction: variance by operator per shift is exactly what
/// an internal audit examines, and a shortfall that can be clicked away is not a control.
/// </para>
/// <para>
/// The expected figure is re-derived from the settled payments rather than read from a running
/// counter, so a drift between the two shows up as a variance rather than being hidden by it.
/// </para>
/// </summary>
public sealed class CashSessionView : LocalizedView
{
    private readonly BillingService _billing;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<CashSessionView>? _logger;

    private readonly Label _heading = new();
    private readonly Label _openTitle = new();
    private readonly Label _breakdownTitle = new();
    private readonly Label _closingTitle = new();
    private readonly Panel _closedPanel = new();
    private readonly Panel _openPanel = new();

    // Opening
    private readonly Label _openingFloatLabel = new();
    private readonly TextBox _openingFloat = UiTheme.Input(160);
    private readonly Button _open = UiTheme.PrimaryButton(string.Empty);

    // Running session
    private readonly Label _sessionInfo = new();
    private readonly Label _breakdown = new();
    private readonly Label _expectedLabel = new();
    private readonly Label _expected = new();
    private readonly Label _countedLabel = new();
    private readonly TextBox _counted = UiTheme.Input(180);
    private readonly Label _varianceLabel = new();
    private readonly Label _variance = new();
    private readonly Label _explanationLabel = new();
    private readonly TextBox _explanation = UiTheme.Input(420);
    private readonly Button _close = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _withdraw = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private CashSessionSummary? _summary;

    /// <summary>
    /// Variance the laboratory tolerates without an explanation, in dirham. Matches the domain's
    /// default so the screen and the rule agree; the domain is the one that enforces it.
    /// </summary>
    private const decimal ToleranceMad = 10m;

    public CashSessionView(BillingService billing, IUserDialogs dialogs,
                           ILocalizer localizer, ICultureManager cultureManager,
                           ILogger<CashSessionView>? logger = null)
        : base(localizer, cultureManager)
    {
        _billing = billing;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.CashDesk");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(24, 20, 24, 16);

        // The title, and the open session's operator, opening time and count of payments in grey.
        var header = FinanceUi.PageHeader(_heading, _sessionInfo);

        // --- no open session: a card to open one ----------------------------------------------------

        _closedPanel.Dock = DockStyle.Fill;
        _closedPanel.BackColor = UiTheme.Surface;
        _closedPanel.Visible = false;

        _openingFloat.Font = UiTheme.MonospaceBold;
        _openingFloat.TextAlign = HorizontalAlignment.Right;
        _openingFloat.Tag = UiTags.LatinValue;
        _openingFloat.Text = "0.00";

        var openForm = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            Padding = new Padding(16, 4, 16, 16),
            BackColor = UiTheme.SurfaceRaised
        };
        openForm.Controls.Add(FinanceUi.Stacked(_openingFloatLabel, _openingFloat));

        FinanceUi.Fit(_open);
        _open.Margin = new Padding(0, 22, 0, 0);
        openForm.Controls.Add(_open);

        var openCard = new FinanceCard(openForm, FinanceUi.CardHeader(_openTitle, null))
        {
            Dock = DockStyle.Top,
            Height = 160
        };
        _closedPanel.Controls.Add(openCard);

        // --- open session: the takings, the count and the closing ---------------------------------

        _openPanel.Dock = DockStyle.Fill;
        _openPanel.BackColor = UiTheme.Surface;
        _openPanel.Visible = false;

        // The takings breakdown, monospaced so the columns line up and a total can be checked
        // by eye against the drawer.
        _breakdown.Font = UiTheme.Monospace;
        _breakdown.ForeColor = UiTheme.TextPrimary;
        _breakdown.AutoSize = false;
        _breakdown.BackColor = UiTheme.SurfaceRaised;
        _breakdown.Padding = new Padding(16, 4, 16, 12);
        _breakdown.RightToLeft = RightToLeft.No;

        var breakdownCard = new FinanceCard(_breakdown, FinanceUi.CardHeader(_breakdownTitle, null));

        // The closing: what the drawer should hold, what was counted, the difference.
        _expected.Font = UiTheme.Kpi;
        _expected.ForeColor = UiTheme.Accent;
        _variance.Font = UiTheme.Kpi;

        var expectedTile = new KpiTile(_expected, _expectedLabel) { Margin = new Padding(0, 0, 8, 0) };
        var varianceTile = new KpiTile(_variance, _varianceLabel) { Margin = new Padding(8, 0, 0, 0) };

        var tiles = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 92,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(16, 4, 16, 12),
            BackColor = UiTheme.SurfaceRaised
        };
        tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tiles.Controls.Add(expectedTile, 0, 0);
        tiles.Controls.Add(varianceTile, 1, 0);
        // Inside a white card: the tiles take the sunken surface to stand out from it.
        foreach (var tile in new Control[] { expectedTile, varianceTile }) tile.BackColor = UiTheme.SurfaceSunken;
        foreach (var label in new[] { _expected, _expectedLabel, _variance, _varianceLabel }) label.BackColor = UiTheme.SurfaceSunken;

        _counted.Font = UiTheme.Mono(14f, FontStyle.Bold);
        _counted.TextAlign = HorizontalAlignment.Right;
        _counted.Tag = UiTags.LatinValue;
        _counted.Width = 200;

        _explanation.Width = 420;

        var countForm = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            Padding = new Padding(16, 4, 16, 8),
            BackColor = UiTheme.SurfaceRaised
        };
        countForm.Controls.Add(FinanceUi.Stacked(_countedLabel, _counted));

        // The explanation appears only when it is required; its caption goes with it.
        var explanation = FinanceUi.Stacked(_explanationLabel, _explanation);
        _explanation.VisibleChanged += (_, _) => explanation.Visible = _explanation.Visible;
        countForm.Controls.Add(explanation);

        var closingBody = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised };
        closingBody.Controls.Add(countForm);
        closingBody.Controls.Add(tiles);

        var closingCard = new FinanceCard(closingBody, FinanceUi.CardHeader(_closingTitle, null));

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 12),
            BackColor = UiTheme.Surface
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        breakdownCard.Margin = new Padding(0, 0, 8, 0);
        closingCard.Margin = new Padding(8, 0, 0, 0);
        cards.Controls.Add(breakdownCard, 0, 0);
        cards.Controls.Add(closingCard, 1, 0);

        // A counter does not hold a day's takings in the drawer. Without a way to record banking,
        // the expected cash at closing includes money already at the bank and every shift that
        // banked anything closes on a large unexplained variance.
        var bar = FinanceUi.ActionBar(_status, [_refresh, _withdraw], _close);
        UiChrome.AsCard(bar);

        _openPanel.Controls.Add(cards);
        _openPanel.Controls.Add(bar);

        Controls.Add(_openPanel);
        Controls.Add(_closedPanel);
        Controls.Add(header);
    }

    private void WireBehaviour()
    {
        // RunBusyAsync: one click, one write, and the screen is not torn down under a pending close.
        _open.Click += async (_, _) => await RunBusyAsync(OpenSessionAsync);
        _close.Click += async (_, _) => await RunBusyAsync(CloseSessionAsync);
        _refresh.Click += async (_, _) => await ActivateAsync();
        _withdraw.Click += async (_, _) => await RunBusyAsync(WithdrawAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Billing.RecordPayment, _open);
        PermissionGate.Require(Permissions.Billing.CloseCashSession, _close, _withdraw);

        _counted.TextChanged += (_, _) => RefreshVariance();
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Payment.CashSession");
        _openTitle.Text = T("Payment.OpenSession");
        _breakdownTitle.Text = T("Finance.SessionDetail");
        _closingTitle.Text = T("Payment.CloseSession");
        _openingFloatLabel.Text = T("Payment.OpeningFloat");
        _open.Text = T("Payment.OpenSession");
        _expectedLabel.Text = T("Payment.ExpectedCash");
        _countedLabel.Text = T("Payment.CountedCash");
        _varianceLabel.Text = T("Payment.Variance");
        _explanationLabel.Text = T("Payment.VarianceExplanation");
        _close.Text = T("Payment.CloseSession");
        _refresh.Text = T("Action.Refresh");
        _withdraw.Text = T("Payment.RecordWithdrawal");

        if (_summary is not null) RenderSummary(_summary);
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _billing.GetOpenCashSessionAsync(ct);

            if (result.IsFailure)
            {
                // No open session is the normal state at the start of a shift, not an error.
                _summary = null;
                _closedPanel.Visible = true;
                _openPanel.Visible = false;
                _sessionInfo.Visible = false;
                _openingFloat.Focus();
                return;
            }

            _summary = result.Value;
            _closedPanel.Visible = false;
            _openPanel.Visible = true;
            _sessionInfo.Visible = true;

            RenderSummary(_summary);
            _counted.Focus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the cash session failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private void RenderSummary(CashSessionSummary summary)
    {
        var language = Localizer.CurrentLanguage;

        _sessionInfo.Text = $"{summary.OperatorName}   ·   " +
                            $"{T("Payment.CashSession")} {summary.OpenedAt:dd/MM/yyyy HH:mm}   ·   " +
                            $"{summary.PaymentCount} {T("Payment.Plural").ToLowerInvariant()}";

        string Line(string labelKey, decimal amount) =>
            // A positive width right-aligns, a negative one left-aligns. The amounts line up in one
            // column so the operator can read the cash drawer down the page against the till.
            $"{T(labelKey),-32}{Money.Of(amount).Format(language),18}";

        var lines = new List<string>
        {
            Line("Payment.OpeningFloat", summary.OpeningFloat),
            new string('-', 50),
            Line("PaymentMethod.Cash", summary.CashReceipts),
            Line("PaymentMethod.Cheque", summary.ChequeReceipts),
            Line("PaymentMethod.Card", summary.CardReceipts),
            Line("PaymentMethod.BankTransfer", summary.TransferReceipts)
        };

        if (summary.CashRefunds > 0m) lines.Add(Line("Payment.Refunds", -summary.CashRefunds));
        if (summary.CashWithdrawals > 0m) lines.Add(Line("Payment.Withdrawals", -summary.CashWithdrawals));

        lines.Add(new string('-', 50));

        // Only cash is reconciled against the drawer; the other methods are reported for the
        // shift record but settle through the bank.
        lines.Add(Line("Payment.ExpectedCash", summary.ExpectedClosingCash));

        _breakdown.Text = string.Join(Environment.NewLine, lines);

        _expected.Text = Money.Of(summary.ExpectedClosingCash).Format(language);

        if (string.IsNullOrWhiteSpace(_counted.Text))
            _counted.Text = summary.ExpectedClosingCash.ToString("0.00");

        RefreshVariance();
    }

    /// <summary>
    /// Recomputes and presents the variance as the operator types.
    /// <para>
    /// Sign matters and is shown: a surplus and a shortfall are different problems, and collapsing
    /// them to an absolute value hides which one the laboratory has.
    /// </para>
    /// </summary>
    private void RefreshVariance()
    {
        if (_summary is null) return;

        if (!TryParseCounted(out var counted))
        {
            _variance.Text = "—";
            _variance.ForeColor = UiTheme.TextMuted;
            _explanationLabel.Visible = _explanation.Visible = false;
            return;
        }

        var variance = Money.Round(counted - _summary.ExpectedClosingCash);

        _variance.Text = (variance > 0m ? "+" : string.Empty) +
                         Money.Of(variance).Format(Localizer.CurrentLanguage);

        var beyondTolerance = Math.Abs(variance) > ToleranceMad;

        _variance.ForeColor = variance == 0m
            ? UiTheme.Normal
            : beyondTolerance ? UiTheme.Critical : UiTheme.Abnormal;

        // The explanation field only appears when it is actually required, so its presence is
        // itself the signal that the variance is not acceptable.
        _explanationLabel.Visible = _explanation.Visible = beyondTolerance;

        _status.ForeColor = beyondTolerance ? UiTheme.Critical : UiTheme.TextSecondary;
        _status.Text = beyondTolerance
            ? T("Payment.VarianceRequiresExplanation", ToleranceMad)
            : string.Empty;
    }

    private bool TryParseCounted(out decimal value) =>
        decimal.TryParse(_counted.Text.Replace(',', '.'),
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out value);

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task OpenSessionAsync()
    {
        if (!decimal.TryParse(_openingFloat.Text.Replace(',', '.'),
                              System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out var opening)
            || opening < 0m)
        {
            _dialogs.Warn(T("Payment_AmountMustBePositive"));
            _openingFloat.Focus();
            return;
        }

        _open.Enabled = false;

        try
        {
            var result = await _billing.OpenCashSessionAsync(opening);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            await ActivateAsync();
        }
        finally
        {
            _open.Enabled = true;
        }
    }

    /// <summary>
    /// Records cash taken out of the drawer and banked mid-shift.
    /// <para>
    /// The amount comes out of the expected closing cash, which is the point: money at the bank is
    /// not money in the till, and a shift that banked without recording it closes on a shortfall
    /// that looks exactly like a loss.
    /// </para>
    /// </summary>
    private async Task WithdrawAsync()
    {
        var answer = _dialogs.PromptForText(T("Payment.WithdrawalAmountPrompt"),
                                             T("Payment.RecordWithdrawal"));

        if (string.IsNullOrWhiteSpace(answer)) return;

        if (!decimal.TryParse(answer.Replace(',', '.'),
                               System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var amount)
            || amount <= 0m)
        {
            _dialogs.Warn(Localizer.Format("Result_NotANumber", answer));
            return;
        }

        var reason = _dialogs.PromptForReason(
            T("Payment.WithdrawalReasonPrompt", $"{amount:N2}"),
            T("Payment.RecordWithdrawal"));

        if (reason is null) return;

        var result = await _billing.RecordCashWithdrawalAsync(amount, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Payment.RecordWithdrawal"));
            return;
        }

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = $"{T("Payment.Withdrawals")} −{amount:N2} DH";

        await ActivateAsync();
    }

    private async Task CloseSessionAsync()
    {
        if (_summary is null) return;

        if (!TryParseCounted(out var counted) || counted < 0m)
        {
            _dialogs.Warn(T("Payment_AmountMustBePositive"));
            _counted.Focus();
            return;
        }

        var variance = Money.Round(counted - _summary.ExpectedClosingCash);
        var explanation = _explanation.Text.Trim();

        if (Math.Abs(variance) > ToleranceMad && explanation.Length == 0)
        {
            _dialogs.Warn(
                Localizer.Format("CashSession_VarianceExplanationRequired", variance),
                T("Payment.CloseSession"));

            _explanation.Focus();
            return;
        }

        if (!_dialogs.Confirm(
                T("Payment.ConfirmClose",
                  Money.Of(counted).Format(Localizer.CurrentLanguage),
                  Money.Of(variance).Format(Localizer.CurrentLanguage)),
                T("Payment.CloseSession")))
            return;

        _close.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _billing.CloseCashSessionAsync(
                counted, explanation.Length == 0 ? null : explanation);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _dialogs.Info(
                $"{T("Payment.Variance")} : {Money.Of(result.Value).Format(Localizer.CurrentLanguage)}",
                T("Payment.CloseSession"));

            _counted.Clear();
            _explanation.Clear();

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _close.Enabled = true;
        }
    }
}
