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
        Padding = new Padding(UiTheme.Gutter * 2);

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.Dock = DockStyle.Top;
        _heading.Height = 40;

        // --- no open session ------------------------------------------------------------------------

        _closedPanel.Dock = DockStyle.Top;
        _closedPanel.Height = 150;
        _closedPanel.BackColor = UiTheme.SurfaceRaised;
        _closedPanel.Padding = new Padding(UiTheme.Gutter);
        _closedPanel.Visible = false;

        _openingFloatLabel.Font = UiTheme.Small;
        _openingFloatLabel.ForeColor = UiTheme.TextSecondary;
        _openingFloatLabel.AutoSize = true;
        _openingFloatLabel.Location = new Point(UiTheme.Gutter, 20);

        _openingFloat.Location = new Point(UiTheme.Gutter, 40);
        _openingFloat.Font = UiTheme.MonospaceBold;
        _openingFloat.TextAlign = HorizontalAlignment.Right;
        _openingFloat.Tag = UiTags.LatinValue;
        _openingFloat.Text = "0.00";

        _open.Bounds = new Rectangle(UiTheme.Gutter, 84, 260, UiTheme.ButtonHeight);

        _closedPanel.Controls.AddRange([_openingFloatLabel, _openingFloat, _open]);

        // --- open session ---------------------------------------------------------------------------

        _openPanel.Dock = DockStyle.Fill;
        _openPanel.BackColor = UiTheme.Surface;
        _openPanel.Visible = false;

        _sessionInfo.Font = UiTheme.Body;
        _sessionInfo.ForeColor = UiTheme.TextSecondary;
        _sessionInfo.AutoSize = false;
        _sessionInfo.Bounds = new Rectangle(0, 4, 700, 22);

        // The takings breakdown, monospaced so the columns line up and a total can be checked
        // by eye against the drawer.
        _breakdown.Font = UiTheme.Monospace;
        _breakdown.ForeColor = UiTheme.TextPrimary;
        _breakdown.AutoSize = false;
        _breakdown.Bounds = new Rectangle(0, 34, 460, 170);
        _breakdown.BackColor = UiTheme.SurfaceRaised;
        _breakdown.Padding = new Padding(UiTheme.Gutter);

        _expectedLabel.Font = UiTheme.Small;
        _expectedLabel.ForeColor = UiTheme.TextSecondary;
        _expectedLabel.AutoSize = true;
        _expectedLabel.Location = new Point(490, 40);

        _expected.Font = new Font("Consolas", 16f, FontStyle.Bold);
        _expected.ForeColor = UiTheme.Accent;
        _expected.AutoSize = true;
        _expected.Location = new Point(490, 58);

        _countedLabel.Font = UiTheme.Small;
        _countedLabel.ForeColor = UiTheme.TextSecondary;
        _countedLabel.AutoSize = true;
        _countedLabel.Location = new Point(490, 96);

        _counted.Location = new Point(490, 114);
        _counted.Font = new Font("Consolas", 14f, FontStyle.Bold);
        _counted.TextAlign = HorizontalAlignment.Right;
        _counted.Height = 30;
        _counted.Tag = UiTags.LatinValue;

        _varianceLabel.Font = UiTheme.Small;
        _varianceLabel.ForeColor = UiTheme.TextSecondary;
        _varianceLabel.AutoSize = true;
        _varianceLabel.Location = new Point(490, 152);

        _variance.Font = new Font("Consolas", 16f, FontStyle.Bold);
        _variance.AutoSize = true;
        _variance.Location = new Point(490, 170);

        _explanationLabel.Font = UiTheme.Small;
        _explanationLabel.ForeColor = UiTheme.TextSecondary;
        _explanationLabel.AutoSize = true;
        _explanationLabel.Location = new Point(0, 218);

        _explanation.Location = new Point(0, 236);
        _explanation.Width = 680;

        _close.Bounds = new Rectangle(0, 278, 260, UiTheme.ButtonHeight + 6);
        _refresh.Bounds = new Rectangle(272, 278, 130, UiTheme.ButtonHeight + 6);

        // A counter does not hold a day's takings in the drawer. Without a way to record banking,
        // the expected cash at closing includes money already at the bank and every shift that
        // banked anything closes on a large unexplained variance.
        _withdraw.Bounds = new Rectangle(412, 278, 250, UiTheme.ButtonHeight + 6);

        _status.Bounds = new Rectangle(0, 322, 680, 40);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.AutoSize = false;

        _openPanel.Controls.AddRange([_sessionInfo, _breakdown, _expectedLabel, _expected,
                                      _countedLabel, _counted, _varianceLabel, _variance,
                                      _explanationLabel, _explanation, _close, _refresh,
                                      _withdraw, _status]);

        Controls.Add(_openPanel);
        Controls.Add(_closedPanel);
        Controls.Add(_heading);
    }

    private void WireBehaviour()
    {
        _open.Click += async (_, _) => await OpenSessionAsync();
        _close.Click += async (_, _) => await CloseSessionAsync();
        _refresh.Click += async (_, _) => await ActivateAsync();
        _withdraw.Click += async (_, _) => await WithdrawAsync();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Billing.RecordPayment, _open);
        PermissionGate.Require(Permissions.Billing.CloseCashSession, _close, _withdraw);

        _counted.TextChanged += (_, _) => RefreshVariance();
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Payment.CashSession");
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
                _openingFloat.Focus();
                return;
            }

            _summary = result.Value;
            _closedPanel.Visible = false;
            _openPanel.Visible = true;

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
