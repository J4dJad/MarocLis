using MarocLIS.Application.Billing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Picks which settlement a correction applies to.
/// <para>
/// A dialog rather than assuming the latest one, because an invoice is routinely settled in parts —
/// some cash at the counter, the balance by transfer a week later — and refunding or bouncing the
/// wrong part puts the invoice's balance right but its payment history wrong. The reference and the
/// maturity date are shown because that is what the operator is holding: a bank advice naming a
/// cheque number, not a payment id.
/// </para>
/// </summary>
internal sealed class PaymentPickerDialog : Form
{
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _ok;

    internal PaymentRow? Selected { get; private set; }

    internal PaymentPickerDialog(ILocalizer localizer, string invoiceNumber,
                                 IReadOnlyList<PaymentRow> payments, string actionLabel)
    {
        Text = $"{actionLabel} — {invoiceNumber}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 320);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        void Add(string name, int w, Font? f = null, bool right = false)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = w, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = f ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("date", 14, UiTheme.Monospace);
        Add("method", 18, UiTheme.Small);
        Add("amount", 14, UiTheme.MonospaceBold, right: true);
        Add("reference", 18, UiTheme.Monospace);
        Add("bank", 18, UiTheme.Small);
        Add("by", 18, UiTheme.Small);

        _grid.Column("date").HeaderText = localizer["Field.Date"];
        _grid.Column("method").HeaderText = localizer["Payment.Method"];
        _grid.Column("amount").HeaderText = localizer["Payment.Amount"];
        _grid.Column("reference").HeaderText = localizer["Payment.Reference"];
        _grid.Column("bank").HeaderText = localizer["Payment.Bank"];
        _grid.Column("by").HeaderText = localizer["Field.CreatedBy"];

        _grid.Dock = DockStyle.None;
        _grid.Bounds = new Rectangle(20, 20, 600, 210);

        foreach (var payment in payments)
        {
            var index = _grid.Rows.Add(
                payment.PaymentDate.ToString("dd/MM/yyyy"),
                localizer[$"PaymentMethod.{payment.Method}"],
                $"{payment.Amount:N2}",
                payment.Reference ?? string.Empty,
                payment.BankName ?? string.Empty,
                payment.ReceivedByName);

            _grid.Rows[index].Tag = payment;
        }

        _ok = UiTheme.PrimaryButton(actionLabel);
        _ok.Bounds = new Rectangle(20, 246, 300, UiTheme.ButtonHeight);
        _ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(330, 246, 290, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_grid, _ok, cancel]);

        CancelButton = cancel;

        if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;

        _grid.SelectionChanged += (_, _) => _ok.Enabled = _grid.CurrentRow?.Tag is PaymentRow;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Accept(); };

        _ok.Enabled = _grid.CurrentRow?.Tag is PaymentRow;
    }

    private void Accept()
    {
        if (_grid.CurrentRow?.Tag is not PaymentRow payment) return;

        Selected = payment;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Records the payer's answer to a submitted claim.
/// <para>
/// Only the outcomes a payer can actually return are offered — accepted, part-accepted, rejected,
/// resubmitted, settled. The rejection reason becomes mandatory for a refusal or a part-payment
/// because the payer states it once, and it is what decides whether the claim is corrected and
/// resent or written off.
/// </para>
/// </summary>
internal sealed class ClaimOutcomeDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _outcome = new();
    private readonly TextBox _reason = UiTheme.Input(440);
    private readonly Label _notice = new();

    internal ClaimStatus Outcome =>
        (ClaimStatus)((_outcome.SelectedItem as EnumChoice)?.Value ?? (int)ClaimStatus.Accepted);

    internal string? RejectionReason =>
        string.IsNullOrWhiteSpace(_reason.Text) ? null : _reason.Text.Trim();

    internal ClaimOutcomeDialog(ILocalizer localizer, IUserDialogs dialogs, int invoiceCount)
    {
        _localizer = localizer;
        _dialogs = dialogs;

        Text = localizer["Billing.RecordClaimOutcome"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 280);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var count = new Label
        {
            Text = localizer.Format("Billing.ClaimOutcomeForCount", invoiceCount),
            Font = UiTheme.BodyBold,
            AutoSize = false,
            Bounds = new Rectangle(20, 16, 460, 22)
        };

        var outcomeLabel = UiTheme.FieldLabel(localizer["Billing.ClaimOutcome"]);
        outcomeLabel.Location = new Point(20, 50);

        _outcome.DropDownStyle = ComboBoxStyle.DropDownList;
        _outcome.FlatStyle = FlatStyle.Flat;
        _outcome.Bounds = new Rectangle(20, 68, 300, UiTheme.InputHeight);

        foreach (var status in new[]
                 {
                     ClaimStatus.Accepted, ClaimStatus.Settled, ClaimStatus.PartiallyAccepted,
                     ClaimStatus.Rejected, ClaimStatus.Resubmitted
                 })
        {
            _outcome.Items.Add(new EnumChoice((int)status, localizer[$"ClaimStatus.{status}"]));
        }

        _outcome.SelectedIndex = 0;
        _outcome.SelectedIndexChanged += (_, _) => RefreshReason();

        _notice.Font = UiTheme.Small;
        _notice.ForeColor = UiTheme.Abnormal;
        _notice.AutoSize = false;
        _notice.Bounds = new Rectangle(20, 102, 460, 30);

        var reasonLabel = UiTheme.FieldLabel(localizer["Billing.ClaimRejectionReason"]);
        reasonLabel.Name = "reasonLabel";
        reasonLabel.Location = new Point(20, 140);

        _reason.Location = new Point(20, 158);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 226, 220, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 226, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([count, outcomeLabel, _outcome, _notice, reasonLabel, _reason, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        RefreshReason();
    }

    private bool ReasonRequired =>
        Outcome is ClaimStatus.Rejected or ClaimStatus.PartiallyAccepted;

    private void RefreshReason()
    {
        _notice.Text = ReasonRequired ? _localizer["Claim_RejectionReasonRequired"] : string.Empty;

        _reason.Enabled = ReasonRequired;
        if (!ReasonRequired) _reason.Text = string.Empty;

        if (Controls["reasonLabel"] is { } label)
            label.ForeColor = ReasonRequired ? UiTheme.Abnormal : UiTheme.TextMuted;
    }

    private void Accept()
    {
        if (ReasonRequired && string.IsNullOrWhiteSpace(_reason.Text))
        {
            _dialogs.Warn(_localizer["Claim_RejectionReasonRequired"]);
            _reason.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Confirms an issue and, for whoever is allowed to grant one, carries the discount.
/// <para>
/// The orders are listed rather than counted. Selection is done with checkboxes in a grid that also
/// holds work for other patients, and an operator who mis-clicked has no way of noticing from the
/// words "3 dossiers" — the accession numbers are what they can check against the paperwork in
/// their hand.
/// </para>
/// <para>
/// The discount block is absent, not disabled, for an operator without
/// <c>Billing.ApplyDiscount</c>: a greyed field invites them to go and ask why it is greyed, and the
/// answer is never one they can act on. The net is computed live because a discount is almost always
/// agreed as a final amount — "make it 300" — and making the operator work backwards from a
/// percentage to the reduction that lands on 300 is how the wrong figure gets typed.
/// </para>
/// </summary>
internal sealed class InvoiceIssueDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly decimal _gross;
    private readonly bool _canDiscount;

    private readonly TextBox _discount = UiTheme.Input(140);
    private readonly TextBox _reason = UiTheme.Input(440);
    private readonly Label _net = new();

    /// <summary>Null rather than zero when no discount is granted, so the service skips the check.</summary>
    internal decimal? DiscountAmount => ParsedDiscount is { } value and > 0m ? value : null;

    internal string? DiscountReason =>
        string.IsNullOrWhiteSpace(_reason.Text) ? null : _reason.Text.Trim();

    private decimal? ParsedDiscount
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_discount.Text)) return 0m;

            // Both separators accepted: the keypad on a counter terminal produces a full stop and
            // the French keyboard a comma, and the operator should not have to know which.
            return decimal.TryParse(_discount.Text.Replace(',', '.'),
                                     System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out var value)
                ? value
                : null;
        }
    }

    internal InvoiceIssueDialog(ILocalizer localizer, IUserDialogs dialogs,
                                IReadOnlyList<BillableOrderRow> orders, Money gross,
                                bool canDiscount)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _gross = gross.Amount;
        _canDiscount = canDiscount;

        Text = localizer["Billing.IssueInvoice"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, canDiscount ? 400 : 290);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var language = localizer.CurrentLanguage;

        var patient = new Label
        {
            Text = orders[0].PatientName,
            Font = UiTheme.SectionTitle,
            ForeColor = UiTheme.TextPrimary,
            AutoSize = false,
            Bounds = new Rectangle(20, 16, 480, 24)
        };

        var payer = new Label
        {
            Text = orders[0].PayerName ?? localizer["Billing.PayerNotCovered"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 42, 480, 18)
        };

        var list = new ListBox
        {
            Bounds = new Rectangle(20, 66, 480, 92),
            BorderStyle = BorderStyle.FixedSingle,
            Font = UiTheme.Monospace,
            BackColor = UiTheme.SurfaceSunken,
            SelectionMode = SelectionMode.None,
            IntegralHeight = false
        };

        foreach (var order in orders)
        {
            list.Items.Add($"{order.AccessionNumber}   "
                           + $"{order.AnalysisCount} {localizer["Catalog.Analyses"]}   "
                           + $"{Money.Of(order.Total).Format(language)}");
        }

        var grossLabel = new Label
        {
            Text = $"{localizer["Billing.Gross"]}   {gross.Format(language)}",
            Font = UiTheme.BodyBold,
            AutoSize = false,
            Bounds = new Rectangle(20, 168, 480, 22)
        };

        Controls.AddRange([patient, payer, list, grossLabel]);

        var buttonTop = 240;

        if (canDiscount)
        {
            var discountLabel = UiTheme.FieldLabel(localizer["Billing.Discount"]);
            discountLabel.Location = new Point(20, 200);

            _discount.Location = new Point(20, 218);
            _discount.Tag = UiTags.LatinValue;
            _discount.TextAlign = HorizontalAlignment.Right;
            _discount.TextChanged += (_, _) => RefreshNet();

            _net.Font = UiTheme.BodyBold;
            _net.ForeColor = UiTheme.Accent;
            _net.AutoSize = false;
            _net.Bounds = new Rectangle(180, 222, 320, 22);

            var reasonLabel = UiTheme.FieldLabel(localizer["Billing.DiscountReason"]);
            reasonLabel.Location = new Point(20, 256);

            _reason.Location = new Point(20, 274);

            var notice = new Label
            {
                Text = localizer["Billing.DiscountNotice"],
                Font = UiTheme.Small,
                ForeColor = UiTheme.TextSecondary,
                AutoSize = false,
                Bounds = new Rectangle(20, 306, 480, 34)
            };

            Controls.AddRange([discountLabel, _discount, _net, reasonLabel, _reason, notice]);

            buttonTop = 350;
            RefreshNet();
        }

        var ok = UiTheme.PrimaryButton(localizer["Billing.IssueInvoice"]);
        ok.Bounds = new Rectangle(20, buttonTop, 230, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(270, buttonTop, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([ok, cancel]);

        // No AcceptButton: issuing burns a number from a series that tax law requires to be
        // unbroken, so it is a deliberate click rather than a reflexive Enter on a scanned barcode.
        CancelButton = cancel;
    }

    private void RefreshNet()
    {
        if (ParsedDiscount is not { } discount || discount < 0m || discount > _gross)
        {
            _net.ForeColor = UiTheme.Critical;
            _net.Text = _localizer["Billing.DiscountInvalid"];
            return;
        }

        _net.ForeColor = UiTheme.Accent;
        _net.Text = $"{_localizer["Billing.Net"]}   "
                    + Money.Of(_gross - discount).Format(_localizer.CurrentLanguage);
    }

    private void Accept()
    {
        if (_canDiscount)
        {
            if (ParsedDiscount is not { } discount)
            {
                _dialogs.Warn(_localizer["Billing.DiscountInvalid"], Text);
                _discount.Focus();
                return;
            }

            if (discount < 0m)
            {
                _dialogs.Warn(_localizer["Invoice_DiscountNegative"], Text);
                _discount.Focus();
                return;
            }

            if (discount > _gross)
            {
                _dialogs.Warn(_localizer["Invoice_DiscountExceedsGross"], Text);
                _discount.Focus();
                return;
            }

            // Checked here as well as in the domain so the operator is told before a transaction is
            // opened, and told next to the field they have to fill.
            if (discount > 0m && string.IsNullOrWhiteSpace(_reason.Text))
            {
                _dialogs.Warn(_localizer["Invoice_DiscountReasonRequired"], Text);
                _reason.Focus();
                return;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
