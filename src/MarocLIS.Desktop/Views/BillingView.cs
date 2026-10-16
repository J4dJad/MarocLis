using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Invoicing and settlement.
/// <para>
/// Two lists, because they answer two different questions. The top list is "what have we done and
/// not billed?" — the laboratory's unbilled work, which is money not yet asked for. The bottom is
/// "what have we billed and not been paid for?" — the receivables, aged.
/// </para>
/// <para>
/// Issuing is one action, not a draft-then-issue pair. A number is allocated from a gapless series
/// at the moment of issue, and holding one across two user actions is precisely how a gap appears
/// in a series that Moroccan tax law requires to be unbroken.
/// </para>
/// </summary>
public sealed class BillingView : LocalizedView
{
    private const string ColSelected = "selected";

    private readonly BillingService _billing;
    private readonly IInvoiceRenderer _invoiceRenderer;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<BillingView>? _logger;

    private readonly Label _title = new();
    private readonly Label _count = new();
    private readonly Label _pendingHeading = new();
    private readonly Label _pendingCount = new();
    private readonly Label _ledgerHeading = new();
    private readonly DataGridView _pending = UiTheme.Grid();
    private readonly DataGridView _ledger = UiTheme.Grid();

    private readonly TextBox _search = new();

    // The status filter is kept as the list it always was, off screen: the chips choose in it, and
    // the ledger is loaded from it as before.
    private readonly ComboBox _statusFilter = new();
    private readonly FlowLayoutPanel _chips = new();
    private IReadOnlyDictionary<InvoiceStatus, int> _statusCounts = new Dictionary<InvoiceStatus, int>();

    private readonly Button _issue = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _deposit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refundDeposit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _pay = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _creditNote = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);

    // Less frequent, under « Plus d'actions ▾ »: the corrections and the third-party lifecycle.
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly MenuAction _refund = new();
    private readonly MenuAction _bounced = new();
    private readonly MenuAction _submitClaim = new();
    private readonly MenuAction _claimOutcome = new();
    private readonly MenuAction _payerSettlement = new();
    private readonly Button _printInvoice = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _printReceipt = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<BillableOrderRow> _pendingRows = [];
    private Application.Abstractions.PagedResult<InvoiceRow> _ledgerPage =
        Application.Abstractions.PagedResult<InvoiceRow>.Empty();

    public BillingView(BillingService billing, IInvoiceRenderer invoiceRenderer,
                       IDocumentOutput documents, IUserDialogs dialogs, ICurrentUser currentUser,
                       ILocalizer localizer, ICultureManager cultureManager,
                       ILogger<BillingView>? logger = null)
        : base(localizer, cultureManager)
    {
        _billing = billing;
        _invoiceRenderer = invoiceRenderer;
        _documents = documents;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Billing");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(24, 20, 24, 16);

        // --- page header ------------------------------------------------------------------------------

        var header = FinanceUi.PageHeader(_title, _count, _refresh);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 230,
            SplitterWidth = 12,
            BackColor = UiTheme.Surface
        };

        // --- unbilled work ----------------------------------------------------------------------------

        BuildPendingColumns();

        var pendingCard = new FinanceCard(
            _pending, FinanceUi.CardHeader(_pendingHeading, _pendingCount, _deposit, _refundDeposit, _issue));

        // --- receivables ------------------------------------------------------------------------------

        _ledgerHeading.Font = UiTheme.SectionTitle;
        _ledgerHeading.ForeColor = UiTheme.TextPrimary;
        _ledgerHeading.AutoSize = true;
        _ledgerHeading.Margin = new Padding(0, 6, 16, 0);

        _statusFilter.DropDownStyle = ComboBoxStyle.DropDownList;

        _search.Tag = UiTags.LatinValue;

        _chips.AutoSize = true;
        _chips.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _chips.WrapContents = false;
        _chips.BackColor = UiTheme.Surface;

        var filters = FinanceUi.FilterRow(_ledgerHeading, new FinanceSearchField(_search, 340), _chips);
        _ledgerHeading.Margin = new Padding(0, 6, 16, 0);
        _chips.Margin = new Padding(0, 0, 0, 0);

        BuildLedgerColumns();

        // Settlement at the far end; a refund and a bounced cheque one click further, away from the
        // button that takes money.
        var more = FinanceUi.MoreButton(_moreMenu, Localizer, _refund, _bounced, _submitClaim, _claimOutcome,
                                        _payerSettlement);
        var ledgerBar = FinanceUi.ActionBar(_status, [more, _creditNote, _printInvoice, _printReceipt], _pay);
        var ledgerCard = new FinanceCard(_ledger, footer: ledgerBar);

        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        bottom.Controls.Add(ledgerCard);
        bottom.Controls.Add(filters);

        split.Panel1.Controls.Add(pendingCard);
        split.Panel2.Controls.Add(bottom);

        Controls.Add(split);
        Controls.Add(header);
    }

    /// <summary>The status chips, with the ledger's count for each under the current search.</summary>
    private void RenderChips()
    {
        _chips.SuspendLayout();
        _chips.Controls.Clear();

        for (var i = 0; i < _statusFilter.Items.Count; i++)
        {
            if (_statusFilter.Items[i] is not StatusChoice choice) continue;

            var count = choice.Value is { } status
                ? _statusCounts.GetValueOrDefault(status)
                : _statusCounts.Values.Sum();
            var text = choice.Value is null
                ? T("Finance.ChipAll", count)
                : $"{choice.Display} · {count}";

            var index = i;
            var chip = new ChipButton { Text = text, Selected = _statusFilter.SelectedIndex == i, Height = 30 };
            chip.Margin = new Padding(0, 2, 6, 2);
            chip.Click += (_, _) =>
            {
                if (_statusFilter.SelectedIndex == index) return;
                _statusFilter.SelectedIndex = index;
                foreach (var other in _chips.Controls.OfType<ChipButton>()) other.Selected = other == chip;
            };
            chip.RightToLeft = RightToLeft;
            _chips.Controls.Add(chip);
        }

        _chips.ResumeLayout(performLayout: true);
    }

    private void RenderCount()
    {
        var invoiceable = _pendingRows.Count(r => r.IsInvoiceable);
        _count.Text = T("Finance.BillingCount", invoiceable, _statusCounts.Values.Sum());
    }

    private void BuildPendingColumns()
    {
        _pending.Columns.Clear();
        _pending.ReadOnly = false;

        _pending.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColSelected, FillWeight = 5,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _pending.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("accession", 13, UiTheme.Monospace);
        Add("patient", 22);
        Add("registered", 11, UiTheme.Monospace);
        Add("payer", 17, UiTheme.Small);
        Add("analyses", 7, UiTheme.Body, DataGridViewContentAlignment.MiddleRight);
        Add("total", 9, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("payershare", 8, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("patientshare", 8, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("deposit", 8, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
    }

    private void BuildLedgerColumns()
    {
        _ledger.Columns.Clear();
        _ledger.ReadOnly = false;

        _ledger.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColSelected, FillWeight = 4,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _ledger.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("number", 15, UiTheme.Monospace);
        Add("kind", 9, UiTheme.Small);
        Add("date", 9, UiTheme.Monospace);
        Add("billedto", 20);
        Add("total", 9, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("paid", 8, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("outstanding", 9, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("overdue", 7, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("status", 10, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _statusFilter.SelectedIndexChanged += async (_, _) => await LoadLedgerAsync();

        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await LoadLedgerAsync();
        };

        // Every write runs through RunBusyAsync: a double-click records one payment, not two, and
        // leaving the screen mid-write no longer disposes the context under it.
        _issue.Click += async (_, _) => await RunBusyAsync(IssueAsync);
        _pay.Click += async (_, _) => await RunBusyAsync(RecordPaymentAsync);
        _refund.Click += async (_, _) => await RunBusyAsync(RefundAsync);
        _bounced.Click += async (_, _) => await RunBusyAsync(MarkBouncedAsync);
        _claimOutcome.Click += async (_, _) => await RunBusyAsync(RecordClaimOutcomeAsync);
        _deposit.Click += async (_, _) => await RunBusyAsync(TakeDepositAsync);
        _refundDeposit.Click += async (_, _) => await RunBusyAsync(RefundDepositAsync);
        _payerSettlement.Click += async (_, _) => await RunBusyAsync(RecordPayerSettlementAsync);
        _creditNote.Click += async (_, _) => await RunBusyAsync(IssueCreditNoteAsync);
        _submitClaim.Click += async (_, _) => await RunBusyAsync(SubmitClaimAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Billing.CreateInvoice, _issue);
        PermissionGate.Require(Permissions.Billing.RecordPayment, _pay, _bounced, _deposit);
        PermissionGate.Require(Permissions.Billing.CreditNote, _refund, _refundDeposit);
        PermissionGate.Require(Permissions.Billing.ExportAccounting, _claimOutcome, _submitClaim,
                               _payerSettlement);
        PermissionGate.Require(Permissions.Billing.CreditNote, _creditNote);
        _printInvoice.Click += async (_, _) => await RunBusyAsync(() => PrintAsync(receipt: false));
        _printReceipt.Click += async (_, _) => await RunBusyAsync(() => PrintAsync(receipt: true));

        _ledger.CellFormatting += OnLedgerFormatting;

        // The status as a pill: a symbol and the word, on the pair of its state.
        FinanceUi.PillColumn(_ledger, "status", row => row.Tag is InvoiceRow invoice
            ? invoice.Status switch
            {
                InvoiceStatus.Issued => FinanceUi.PillRunning,
                InvoiceStatus.PartiallyPaid => FinanceUi.PillWarning,
                InvoiceStatus.Paid => FinanceUi.PillDone,
                InvoiceStatus.WrittenOff => FinanceUi.PillWaiting,
                _ => null
            }
            : null);
        _pending.CellFormatting += OnPendingFormatting;

        // The print buttons follow the selection. A ticked box only reaches the cell's value once
        // the edit is committed, so the commit happens as soon as the box is clicked.
        _ledger.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_ledger.IsCurrentCellDirty
                && _ledger.CurrentCell?.OwningColumn?.Name == ColSelected)
                _ledger.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _ledger.CellValueChanged += (_, _) => UpdatePrintButtons();
        _ledger.SelectionChanged += (_, _) => UpdatePrintButtons();
        UpdatePrintButtons();
    }

    private void OnLedgerFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _ledger.Rows.Count) return;
        if (_ledger.Rows[e.RowIndex].Tag is not InvoiceRow row) return;

        // Ageing bands. Colour plus the day count in its own column, so the state is legible
        // without relying on hue.
        if (row.DaysOverdue > 60)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.DaysOverdue > 0)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
        else if (row.Status == InvoiceStatus.Paid)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }

        // A credit note is a reversal; showing it in the same weight as an invoice invites
        // double-counting when the desk totals a column by eye.
        if (row.Kind == InvoiceKind.CreditNote)
            e.CellStyle!.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Italic);
    }

    /// <summary>
    /// A cancelled request is listed only because it still holds a deposit to hand back; it is
    /// greyed so nobody tries to invoice it. So is an invoiced one listed for its leftover deposit.
    /// </summary>
    private void OnPendingFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _pending.Rows.Count) return;
        if (_pending.Rows[e.RowIndex].Tag is not BillableOrderRow row) return;

        if (row.Status == Domain.Orders.LabOrderStatus.Cancelled)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Strikeout);
        }
        else if (row.InvoiceId is not null)
        {
            e.CellStyle!.ForeColor = _pending.Columns[e.ColumnIndex].Name == "deposit"
                ? UiTheme.Abnormal
                : UiTheme.TextMuted;
        }
        else if (row.DepositHeld > 0m && _pending.Columns[e.ColumnIndex].Name == "deposit")
        {
            e.CellStyle!.ForeColor = UiTheme.Normal;
        }
    }

    protected override void ApplyTexts()
    {
        _title.Text = T("Nav.Billing");
        _pendingHeading.Text = T("Billing.Uninvoiced");
        _ledgerHeading.Text = T("Billing.Invoices");
        _search.PlaceholderText = T("Finance.InvoiceSearchHint");
        _issue.Text = T("Billing.IssueInvoice");
        _pay.Text = T("Payment.Title");
        _refund.Text = T("Payment.Refund");
        _bounced.Text = T("Payment.MarkBounced");
        _claimOutcome.Text = T("Billing.RecordClaimOutcome");
        _deposit.Text = T("Deposit.Take");
        _refundDeposit.Text = T("Deposit.Refund");
        _payerSettlement.Text = T("Claim.Settlement");
        _creditNote.Text = T("Billing.CreditNote");
        _submitClaim.Text = T("Billing.SubmitClaim");
        _printInvoice.Text = T("Billing.PrintInvoice");
        _printReceipt.Text = T("Billing.PrintReceipt");
        _refresh.Text = T("Action.Refresh");

        var selected = _statusFilter.SelectedIndex;
        _statusFilter.BeginUpdate();
        _statusFilter.Items.Clear();
        _statusFilter.Items.Add(new StatusChoice(null, T("Action.SelectAll")));

        foreach (var status in (InvoiceStatus[])
                 [InvoiceStatus.Issued, InvoiceStatus.PartiallyPaid,
                  InvoiceStatus.Paid, InvoiceStatus.WrittenOff])
            _statusFilter.Items.Add(new StatusChoice(status, T($"InvoiceStatus.{status}")));

        _statusFilter.SelectedIndex = selected >= 0 ? selected : 0;
        _statusFilter.EndUpdate();
        RenderChips();
        RenderCount();

        if (_pending.Columns.Count > 0)
        {
            _pending.Column(ColSelected).HeaderText = string.Empty;
            _pending.Column("accession").HeaderText = T("Order.AccessionNumber");
            _pending.Column("patient").HeaderText = T("Patient.Title");
            _pending.Column("registered").HeaderText = T("Order.RegisteredAt");
            _pending.Column("payer").HeaderText = T("Coverage.Payer");
            _pending.Column("analyses").HeaderText = T("Catalog.Analyses");
            _pending.Column("total").HeaderText = T("Billing.TotalDue");
            _pending.Column("payershare").HeaderText = T("Billing.PayerShare");
            _pending.Column("patientshare").HeaderText = T("Billing.PatientShare");
            _pending.Column("deposit").HeaderText = T("Deposit.Title");
        }

        if (_ledger.Columns.Count > 0)
        {
            _ledger.Column(ColSelected).HeaderText = string.Empty;
            _ledger.Column("number").HeaderText = T("Billing.Number");
            _ledger.Column("kind").HeaderText = T("Field.Name");
            _ledger.Column("date").HeaderText = T("Billing.IssueDate");
            _ledger.Column("billedto").HeaderText = T("Billing.BilledTo");
            _ledger.Column("total").HeaderText = T("Billing.TotalDue");
            _ledger.Column("paid").HeaderText = T("Billing.Paid");
            _ledger.Column("outstanding").HeaderText = T("Billing.Outstanding");
            _ledger.Column("overdue").HeaderText = T("Order.Overdue");
            _ledger.Column("status").HeaderText = T("Field.Status");
        }

        if (_pendingRows.Count > 0) RenderPending();
        if (_ledgerPage.Items.Count > 0) RenderLedger();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadPendingAsync(ct);
        await LoadLedgerAsync(ct);
    }

    private async Task LoadPendingAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _billing.GetUninvoicedOrdersAsync(ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _pendingRows = result.Value;
            RenderPending();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading uninvoiced orders failed.");
        }
    }

    private void RenderPending()
    {
        _pending.SuspendLayout();
        _pending.Rows.Clear();

        foreach (var row in _pendingRows)
        {
            var index = _pending.Rows.Add(
                false,
                row.AccessionNumber,
                row.PatientName,
                row.RegisteredAt.ToString("dd/MM HH:mm"),
                // Already invoiced, listed only for its leftover deposit: shown in place of the payer,
                // which no longer matters for it.
                row.InvoiceId is not null
                    ? T("Billing.InvoicedDepositToRefund", row.InvoiceNumber ?? string.Empty)
                    : row.PayerName ?? T("PayerKind.SelfPay"),
                row.AnalysisCount.ToString(),
                Money.Of(row.Total).Format(Localizer.CurrentLanguage),
                row.PayerShare > 0m
                    ? Money.Of(row.PayerShare).Format(Localizer.CurrentLanguage)
                    : string.Empty,
                Money.Of(row.PatientShare).Format(Localizer.CurrentLanguage),
                row.DepositHeld > 0m
                    ? Money.Of(row.DepositHeld).Format(Localizer.CurrentLanguage)
                    : string.Empty);

            _pending.Rows[index].Tag = row;
        }

        _pending.ResumeLayout(performLayout: true);

        // Counted only what is still to invoice: a cancelled or invoiced request listed for its
        // deposit is not.
        var invoiceable = _pendingRows.Where(r => r.IsInvoiceable).ToList();
        var total = Money.Of(invoiceable.Sum(r => r.Total));

        _pendingHeading.Text = T("Billing.Uninvoiced");
        _pendingCount.Text = $"{invoiceable.Count} · " + total.Format(Localizer.CurrentLanguage);

        // Capped at the most recent: said, so an older order missing from the list is not taken
        // for one that needs no invoice.
        if (_pendingRows.Count >= IBillingQueries.UninvoicedOrdersLimit)
            _pendingCount.Text += " — " + T("Billing.UninvoicedTruncated", IBillingQueries.UninvoicedOrdersLimit);

        RenderCount();
    }

    private async Task LoadLedgerAsync(CancellationToken ct = default)
    {
        try
        {
            var status = (_statusFilter.SelectedItem as StatusChoice)?.Value;

            var result = await _billing.GetInvoicesAsync(status, _search.Text.Trim(), 1, ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _ledgerPage = result.Value;
            RenderLedger();

            // The counts on the chips, for the same search; a failure leaves the last ones.
            var counts = await _billing.GetInvoiceStatusCountsAsync(_search.Text.Trim(), ct);
            if (counts.IsSuccess)
            {
                _statusCounts = counts.Value;
                RenderChips();
                RenderCount();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the invoice ledger failed.");
        }
    }

    private void RenderLedger()
    {
        _ledger.SuspendLayout();
        _ledger.Rows.Clear();

        foreach (var row in _ledgerPage.Items)
        {
            var index = _ledger.Rows.Add(
                false,
                row.Number,
                T(row.Kind == InvoiceKind.CreditNote ? "Billing.CreditNote" : "Billing.Invoice"),
                row.IssueDate.ToString("dd/MM/yy"),
                row.BilledToName,
                Money.Of(row.Total).Format(Localizer.CurrentLanguage),
                row.Paid > 0m ? Money.Of(row.Paid).Format(Localizer.CurrentLanguage) : string.Empty,
                row.Outstanding > 0m
                    ? Money.Of(row.Outstanding).Format(Localizer.CurrentLanguage)
                    : string.Empty,
                row.DaysOverdue > 0 ? $"{row.DaysOverdue} j" : string.Empty,
                T($"InvoiceStatus.{row.Status}"));

            _ledger.Rows[index].Tag = row;

            // A write-off closes the balance but is not in the paid column: said where it is missing.
            if (row.WrittenOff > 0m)
                _ledger.Rows[index].Cells["paid"].ToolTipText =
                    T("Billing.WrittenOffTooltip", Money.Of(row.WrittenOff).Format(Localizer.CurrentLanguage));
        }

        _ledger.ResumeLayout(performLayout: true);
        UpdatePrintButtons();

        var outstanding = Money.Of(_ledgerPage.Items.Sum(r => r.Outstanding));

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = $"{_ledgerPage.Items.Count} / {_ledgerPage.TotalCount} · " +
                       $"{T("Billing.Outstanding")} {outstanding.Format(Localizer.CurrentLanguage)}";
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private static List<T2> Selected<T2>(DataGridView grid) where T2 : class =>
    [
        .. grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[ColSelected].Value is true && r.Tag is T2)
            .Select(r => (T2)r.Tag!)
    ];

    private async Task IssueAsync()
    {
        _pending.EndEdit();

        var selected = Selected<BillableOrderRow>(_pending);

        if (selected.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        // Several orders can share one invoice only if they belong to one patient — a single
        // document addressed to two people is not a document anybody can settle.
        if (selected.Select(s => s.PatientId).Distinct().Count() > 1)
        {
            _dialogs.Warn(T("Invoice_MultiplePatients"), T("Billing.IssueInvoice"));
            return;
        }

        // An invoiced request is listed only for the deposit it still holds: it is refunded, not
        // invoiced again. Named here, rather than the whole issue failing on one row nobody can spot.
        var invoiced = selected.Where(s => s.InvoiceId is not null).ToList();

        if (invoiced.Count > 0)
        {
            _dialogs.Warn(T("Invoice_OrderAlreadyInvoiced",
                            string.Join(", ", invoiced.Select(s => s.AccessionNumber))),
                          T("Billing.IssueInvoice"));
            return;
        }

        // Nor orders registered with different payers: each was split with its own at registration.
        // The service refuses it too; said here before the issue dialog is filled in for nothing.
        if (selected.Select(s => (s.PayerId, s.DirectBilling)).Distinct().Count() > 1)
        {
            _dialogs.Warn(T("Invoice_MultiplePayers",
                            string.Join(", ", selected.Select(s => s.AccessionNumber))),
                          T("Billing.IssueInvoice"));
            return;
        }

        // Invoiced again after a credit note reversed the first invoice, which went to the patient
        // although the order had a payer: the usual correction is a third-party box left unticked at
        // registration, so the operator is offered to address this one to the payer — when the
        // payer has an agreement in force, without which its share cannot be claimed.
        Guid? payerOverride = null;

        if (selected[0] is { PayerId: { } payerId, DirectBilling: false, PayerAgreementValid: true }
            && selected.Any(s => s.PreviouslyInvoiced)
            && _dialogs.Confirm(T("Billing.ReinvoiceToPayer", selected[0].PayerName ?? string.Empty),
                                T("Billing.IssueInvoice")))
            payerOverride = payerId;

        var total = Money.Of(selected.Sum(s => s.Total));

        // The confirmation and the discount are one dialog, not a confirm followed by an optional
        // second step: a discount decided after the operator has already confirmed the total is a
        // discount applied to a document they were told was final.
        using var issue = new InvoiceIssueDialog(
            Localizer, _dialogs, selected, total,
            canDiscount: _currentUser.Has(Permissions.Billing.ApplyDiscount));

        if (issue.ShowDialog(FindForm()) != DialogResult.OK) return;

        _issue.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _billing.IssueInvoiceAsync(
                [.. selected.Select(s => s.LabOrderId)],
                payerIdOverride: payerOverride,
                discountAmount: issue.DiscountAmount,
                discountReason: issue.DiscountReason);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _status.ForeColor = UiTheme.Normal;
            _status.Text = T("Message.Saved");

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _issue.Enabled = true;
        }
    }

    /// <summary>
    /// Refunds a settled payment.
    /// <para>
    /// Distinct from a credit note, and the distinction matters to both the accountant and the
    /// patient: a credit note says the laboratory withdrew the charge, a refund says money went
    /// back. A cash refund also has to reach the open till, or the shift closes on a shortfall
    /// nobody can explain.
    /// </para>
    /// </summary>
    private async Task RefundAsync() => await CorrectPaymentAsync(bounce: false);

    /// <summary>
    /// Marks a cheque or bill of exchange returned unpaid. The invoice goes back to outstanding and
    /// re-enters the receivables ageing, which is what a bounced cheque actually means.
    /// </summary>
    private async Task MarkBouncedAsync() => await CorrectPaymentAsync(bounce: true);

    private async Task CorrectPaymentAsync(bool bounce)
    {
        _ledger.EndEdit();

        var selected = Selected<InvoiceRow>(_ledger);

        if (selected.Count != 1)
        {
            _status.Text = T("Billing.SelectOneInvoice");
            return;
        }

        var invoice = selected[0];

        var payments = await _billing.GetPaymentsAsync(invoice.InvoiceId);

        if (payments.IsFailure)
        {
            _dialogs.ShowFailure(payments);
            return;
        }

        // Only the ones the chosen correction can actually apply to: a bounce is meaningless for
        // cash, and neither applies to a payment already refunded.
        var candidates = bounce
            ? payments.Value.Where(p => p.CanBounce).ToList()
            : payments.Value.Where(p => p.IsCorrectable).ToList();

        if (candidates.Count == 0)
        {
            _dialogs.Info(T(bounce ? "Payment.NoBounceablePayment" : "Payment.NoRefundablePayment"),
                          T(bounce ? "Payment.MarkBounced" : "Payment.Refund"));
            return;
        }

        using var picker = new PaymentPickerDialog(
            Localizer, invoice.Number, candidates,
            T(bounce ? "Payment.MarkBounced" : "Payment.Refund"));

        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } payment) return;

        var reason = _dialogs.PromptForReason(
            T(bounce ? "Payment.BouncePrompt" : "Payment.RefundPrompt",
              $"{payment.Amount:N2} DH", T($"PaymentMethod.{payment.Method}")),
            T(bounce ? "Payment.MarkBounced" : "Payment.Refund"));

        if (reason is null) return;

        var result = bounce
            ? await _billing.MarkPaymentBouncedAsync(invoice.InvoiceId, payment.PaymentId, reason)
            : await _billing.RefundPaymentAsync(invoice.InvoiceId, payment.PaymentId, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = $"{invoice.Number} — {T(bounce ? "PaymentStatus.Bounced" : "PaymentStatus.Refunded")}";

        await LoadLedgerAsync();
    }

    /// <summary>
    /// Records what the payer answered on a submitted claim.
    /// <para>
    /// Without this the claim status froze at <c>Submitted</c> for ever, so everything sent stayed
    /// outstanding on the books regardless of what the payer paid — which made the receivables
    /// figure meaningless for the one revenue stream that matters most.
    /// </para>
    /// </summary>
    private async Task RecordClaimOutcomeAsync()
    {
        _ledger.EndEdit();

        var selected = Selected<InvoiceRow>(_ledger);

        if (selected.Count == 0)
        {
            _status.Text = T("Billing.SelectOneInvoice");
            return;
        }

        var submitted = selected
            .Where(i => i.ClaimStatus is not (ClaimStatus.NotApplicable or ClaimStatus.Prepared))
            .ToList();

        if (submitted.Count == 0)
        {
            _dialogs.Warn(T("Billing.NoSubmittedClaims"), T("Billing.RecordClaimOutcome"));
            return;
        }

        using var dialog = new ClaimOutcomeDialog(Localizer, _dialogs, submitted.Count);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _billing.RecordClaimOutcomeAsync(
            [.. submitted.Select(i => i.InvoiceId)], dialog.Outcome, dialog.RejectionReason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Billing.RecordClaimOutcome"));
            return;
        }

        // Invoices whose own settlement was already entered keep it; the operator is told which.
        if (result.Value.Skipped.Count > 0)
            _dialogs.Warn(Localizer.Describe(result.Value.Skipped), T("Billing.RecordClaimOutcome"));

        _status.ForeColor = dialog.Outcome is ClaimStatus.Rejected
            ? UiTheme.Critical
            : UiTheme.Normal;

        _status.Text = $"{result.Value.Updated} × {T($"ClaimStatus.{dialog.Outcome}")}";

        await LoadLedgerAsync();
    }

    /// <summary>
    /// Takes a deposit on one request before it is invoiced. It is turned into a settlement on the
    /// invoice automatically when the invoice is issued.
    /// </summary>
    private async Task TakeDepositAsync()
    {
        _pending.EndEdit();

        var selected = Selected<BillableOrderRow>(_pending);

        if (selected.Count != 1)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Deposit.SelectOneOrder");
            return;
        }

        var order = selected[0];

        if (order.Status == Domain.Orders.LabOrderStatus.Cancelled)
        {
            _dialogs.Warn(T("Deposit_OrderCancelled", order.AccessionNumber), T("Deposit.Take"));
            return;
        }

        if (order.InvoiceId is not null)
        {
            _dialogs.Warn(T("Invoice_OrderAlreadyInvoiced", order.AccessionNumber), T("Deposit.Take"));
            return;
        }

        // The patient's share under direct billing, as the service caps it.
        if (order.DepositCeiling <= 0m)
        {
            _dialogs.Info(T("Deposit.FullyCovered"), T("Deposit.Take"));
            return;
        }

        using var dialog = PaymentDialog.ForDeposit(Localizer, _dialogs, order);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.DepositRequest is null) return;

        var result = await _billing.TakeDepositAsync(dialog.DepositRequest);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Deposit.Take"));
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Deposit.Taken", result.Value, order.AccessionNumber);

        await LoadPendingAsync();
    }

    /// <summary>
    /// Hands back everything still held on the selected request, usually because it was cancelled.
    /// One reason covers all of its deposits.
    /// </summary>
    private async Task RefundDepositAsync()
    {
        _pending.EndEdit();

        var selected = Selected<BillableOrderRow>(_pending);

        if (selected.Count != 1)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Deposit.SelectOneOrder");
            return;
        }

        var order = selected[0];

        var deposits = await _billing.GetDepositsAsync(order.LabOrderId);

        if (deposits.IsFailure)
        {
            _dialogs.ShowFailure(deposits);
            return;
        }

        var held = deposits.Value.Where(d => d.Status == DepositStatus.Held && d.Remaining > 0m).ToList();

        if (held.Count == 0)
        {
            _dialogs.Info(T("Deposit.NoneHeld"), T("Deposit.Refund"));
            return;
        }

        var reason = _dialogs.PromptForReason(
            T("Deposit.RefundPrompt", order.AccessionNumber,
              Money.Of(held.Sum(d => d.Remaining)).Format(Localizer.CurrentLanguage),
              string.Join(", ", held.Select(d => $"{d.Number} ({T($"PaymentMethod.{d.Method}")})"))),
            T("Deposit.Refund"));

        if (reason is null) return;

        var refunded = 0;

        foreach (var deposit in held)
        {
            var result = await _billing.RefundDepositAsync(deposit.DepositId, reason);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result, T("Deposit.Refund"));
                break;
            }

            refunded++;
        }

        // Said refunded only when every deposit was: a refusal (no open till, say) left the money
        // held while the status bar announced it handed back.
        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = refunded == held.Count
            ? T("Deposit.Refunded", order.AccessionNumber)
            : T("Deposit.RefundedPartly", order.AccessionNumber, refunded, held.Count);

        await LoadPendingAsync();
    }

    /// <summary>
    /// Records what the payer actually paid on one invoice's share, and what happens to the rest:
    /// left for the patient, or written off.
    /// </summary>
    private async Task RecordPayerSettlementAsync()
    {
        _ledger.EndEdit();

        var selected = Selected<InvoiceRow>(_ledger);

        if (selected.Count != 1)
        {
            _status.Text = T("Billing.SelectOneInvoice");
            return;
        }

        var invoice = selected[0];

        if (invoice.PayerShare <= 0m
            || invoice.ClaimStatus is ClaimStatus.NotApplicable or ClaimStatus.Prepared)
        {
            _dialogs.Warn(T("Billing.NoSubmittedClaims"), T("Claim.Settlement"));
            return;
        }

        if (invoice.PayerAcceptedAmount is not null)
        {
            _dialogs.Warn(T("Claim_SettlementAlreadyRecorded", invoice.Number), T("Claim.Settlement"));
            return;
        }

        using var dialog = new PayerSettlementDialog(Localizer, _dialogs, invoice);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _billing.RecordPayerSettlementAsync(
            invoice.InvoiceId, dialog.AcceptedAmount, dialog.Shortfall, dialog.Reason,
            dialog.RemittanceReference);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Claim.Settlement"));
            return;
        }

        _status.ForeColor = dialog.AcceptedAmount < invoice.PayerShare ? UiTheme.Abnormal : UiTheme.Normal;
        _status.Text = T("Claim.SettlementRecorded", invoice.Number,
                         Money.Of(dialog.AcceptedAmount).Format(Localizer.CurrentLanguage),
                         Money.Of(invoice.PayerShare).Format(Localizer.CurrentLanguage));

        await LoadLedgerAsync();
    }

    private async Task RecordPaymentAsync()
    {
        _ledger.EndEdit();

        var selected = Selected<InvoiceRow>(_ledger);

        if (selected.Count != 1)
        {
            _status.Text = T("Billing.SelectOneInvoice");
            return;
        }

        var invoice = selected[0];

        if (invoice.Outstanding <= 0m)
        {
            _dialogs.Info(T("InvoiceStatus.Paid"));
            return;
        }

        // What is left is the payer's share, settled through the claim.
        if (invoice.PatientPayable <= 0m)
        {
            _dialogs.Info(T("Payment_ExceedsPatientShare",
                            Money.Of(0m).Format(Localizer.CurrentLanguage)));
            return;
        }

        using var dialog = new PaymentDialog(Localizer, invoice, _dialogs);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        var result = await _billing.RecordPaymentAsync(dialog.Request);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await LoadLedgerAsync();
    }

    /// <summary>
    /// Issues a credit note. The only lawful correction to an issued invoice, so the confirmation
    /// says so explicitly rather than presenting it as a deletion.
    /// </summary>
    private async Task IssueCreditNoteAsync()
    {
        _ledger.EndEdit();

        var selected = Selected<InvoiceRow>(_ledger);

        if (selected.Count != 1)
        {
            _status.Text = T("Billing.SelectOneInvoice");
            return;
        }

        var invoice = selected[0];

        if (invoice.Kind == InvoiceKind.CreditNote)
        {
            _dialogs.Warn(T("Invoice_CannotCreditACreditNote"));
            return;
        }

        // Already reversed in full: a second credit note would credit the same charge twice. The
        // service refuses it as well; this saves the operator typing a reason for nothing.
        if (invoice.Creditable <= 0m)
        {
            _dialogs.Warn(T("Invoice_AlreadyFullyCredited", invoice.Number));
            return;
        }

        // Sent to the payer or answered by it: refused by the service as well, said here before
        // the reason is typed.
        if (Invoice.ClaimBlocksCreditNote(invoice.ClaimStatus))
        {
            _dialogs.Warn(T("Invoice_CreditNoteClaimSent", invoice.Number));
            return;
        }

        var reason = _dialogs.PromptForReason(
            T("Billing.CreditNoteReasonPrompt", invoice.Number),
            T("Billing.CreditNote"), minimumLength: 10);

        if (reason is null) return;

        // Off until the service answers: a second click would issue a second credit note.
        _creditNote.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _billing.IssueCreditNoteAsync(invoice.InvoiceId, reason);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _status.ForeColor = UiTheme.Normal;
            _status.Text = T("Message.Saved");

            // Both lists: a full credit note frees its orders, which come back to be invoiced.
            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _creditNote.Enabled = true;
        }
    }

    private async Task SubmitClaimAsync()
    {
        _ledger.EndEdit();

        var selected = Selected<InvoiceRow>(_ledger)
            .Where(i => i.ClaimStatus is ClaimStatus.Prepared or ClaimStatus.Rejected)
            .ToList();

        if (selected.Count == 0)
        {
            _status.Text = T("Billing.NoClaimableInvoices");
            return;
        }

        // Off until the service answers: a double click used to send the same invoices in two
        // batches. The service re-checks the claim status as well.
        _submitClaim.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _billing.SubmitClaimBatchAsync(
                [.. selected.Select(i => i.InvoiceId)]);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                await LoadLedgerAsync();
                return;
            }

            var batch = result.Value;

            // Sent, even if some invoices were left out: the reference comes first, then why the
            // others were not sent, so nobody concludes from the refusals that nothing went.
            if (batch.Skipped.Count == 0)
                _dialogs.Info(T("Billing.ClaimReference") + " : " + batch.Reference, T("Billing.SubmitClaim"));
            else
                _dialogs.Warn(T("Claim.PartiallySubmitted", batch.Reference, batch.Submitted,
                                batch.Skipped.Count)
                              + Environment.NewLine + Environment.NewLine
                              + Localizer.Describe(batch.Skipped),
                              T("Billing.SubmitClaim"));

            await LoadLedgerAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _submitClaim.Enabled = true;
        }
    }

    // ---- Printing ------------------------------------------------------------------------------------

    /// <summary>
    /// The invoice the print buttons act on: the single ticked row when exactly one is ticked, or the
    /// highlighted row when none is. Several ticked rows print nothing — which of them was meant is
    /// not something to guess with an accounting document.
    /// </summary>
    private InvoiceRow? PrintTarget()
    {
        var ticked = Selected<InvoiceRow>(_ledger);

        return ticked.Count switch
        {
            1 => ticked[0],
            0 => _ledger.CurrentRow?.Tag as InvoiceRow,
            _ => null
        };
    }

    private void UpdatePrintButtons()
    {
        var enabled = PrintTarget() is not null;
        _printInvoice.Enabled = enabled;
        _printReceipt.Enabled = enabled;
    }

    /// <summary>
    /// Renders the selected invoice, or its payment receipt, and opens it in the PDF viewer where
    /// the operator prints it.
    /// </summary>
    private async Task PrintAsync(bool receipt)
    {
        _ledger.EndEdit();

        var title = T(receipt ? "Billing.PrintReceipt" : "Billing.PrintInvoice");

        if (PrintTarget() is not { } invoice)
        {
            _status.Text = T("Billing.SelectOneInvoice");
            return;
        }

        var button = receipt ? _printReceipt : _printInvoice;
        button.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var document = await _billing.GetInvoiceDocumentAsync(invoice.InvoiceId);

            if (document.IsFailure)
            {
                _dialogs.ShowFailure(document, title);
                return;
            }

            var language = Localizer.CurrentLanguage;

            // QuestPDF lays out synchronously; off the UI thread so a long invoice does not freeze
            // the screen while it renders.
            var bytes = await Task.Run(() => receipt
                ? _invoiceRenderer.RenderReceipt(document.Value, language)
                : _invoiceRenderer.RenderInvoice(document.Value, language));

            var opened = await _documents.OpenAsync(bytes, "pdf");

            if (opened.IsFailure)
            {
                _dialogs.ShowFailure(opened, title);
                return;
            }

            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = $"{invoice.Number} — {title}";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Printing invoice {Number} failed.", invoice.Number);
            _dialogs.Warn(T("Message.UnexpectedError"), title);
        }
        finally
        {
            Cursor = Cursors.Default;
            UpdatePrintButtons();
        }
    }

    private sealed record StatusChoice(InvoiceStatus? Value, string Display)
    {
        public override string ToString() => Display;
    }
}

/// <summary>
/// Settlement entry.
/// <para>
/// The method drives which fields are required, because a cheque without its number is not
/// traceable and a transfer without a reference cannot be reconciled against a bank statement.
/// A cash amount above the statutory threshold is warned about <em>before</em> the money is taken,
/// which is the only point at which the warning is useful.
/// </para>
/// </summary>
internal sealed class PaymentDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly Guid? _invoiceId;
    private readonly decimal _ceiling;
    private readonly string _ceilingCaption;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _method = new();
    private readonly TextBox _amount = UiTheme.Input(140);
    private readonly TextBox _reference = UiTheme.Input(200);
    private readonly TextBox _bank = UiTheme.Input(200);
    private readonly DateTimePicker _maturity = new();
    private readonly Label _referenceLabel = new();
    private readonly Label _bankLabel = new();
    private readonly Label _maturityLabel = new();
    private readonly Label _warning = new();

    internal RecordPaymentRequest? Request { get; private set; }

    /// <summary>Set when the dialog was opened for a deposit rather than an invoice.</summary>
    internal TakeDepositRequest? DepositRequest { get; private set; }

    private readonly Guid? _orderId;

    internal PaymentDialog(ILocalizer localizer, InvoiceRow invoice, IUserDialogs dialogs)
        // On an invoice whose payer share is claimed from the payer, the patient's part only: asked
        // for the whole balance, the patient paid the payer's share too, and it was claimed again.
        : this(localizer, dialogs, $"{localizer["Payment.Title"]} — {invoice.Number}",
               localizer[invoice.PatientPayable < invoice.Outstanding
                   ? "Payment.PatientShareDue" : "Billing.Outstanding"],
               invoice.PatientPayable, invoice.InvoiceId, null)
    {
    }

    /// <summary>
    /// A deposit on an order not yet invoiced. The ceiling is what the order still leaves to take:
    /// what the patient will owe (the patient's share under direct billing) less the deposits
    /// already held.
    /// </summary>
    internal static PaymentDialog ForDeposit(ILocalizer localizer, IUserDialogs dialogs,
                                             BillableOrderRow order)
        => new(localizer, dialogs, $"{localizer["Deposit.Take"]} — {order.AccessionNumber}",
               localizer["Deposit.Available"], order.DepositCeiling,
               null, order.LabOrderId);

    private PaymentDialog(ILocalizer localizer, IUserDialogs dialogs, string title,
                          string ceilingCaption, decimal ceiling, Guid? invoiceId, Guid? orderId)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _ceilingCaption = ceilingCaption;
        _ceiling = ceiling;
        _invoiceId = invoiceId;
        _orderId = orderId;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(440, 330);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        BuildLayout();

        _method.SelectedIndexChanged += (_, _) => RefreshFieldVisibility();
        _amount.TextChanged += (_, _) => RefreshWarning();

        RefreshFieldVisibility();
    }

    private void BuildLayout()
    {
        var outstanding = new Label
        {
            Text = $"{_ceilingCaption} : " +
                   Money.Of(_ceiling).Format(_localizer.CurrentLanguage),
            Font = UiTheme.SectionTitle,
            ForeColor = UiTheme.Accent,
            AutoSize = true,
            Location = new Point(20, 16)
        };

        var methodLabel = UiTheme.FieldLabel(_localizer["Payment.Method"]);
        methodLabel.Location = new Point(20, 52);

        _method.DropDownStyle = ComboBoxStyle.DropDownList;
        _method.FlatStyle = FlatStyle.Flat;
        _method.Bounds = new Rectangle(20, 70, 200, UiTheme.InputHeight);

        foreach (var method in (PaymentMethod[])
                 [PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.Cheque,
                  PaymentMethod.BankTransfer, PaymentMethod.BillOfExchange,
                  PaymentMethod.MobilePayment])
            _method.Items.Add(new MethodChoice(method, _localizer[$"PaymentMethod.{method}"]));

        _method.SelectedIndex = 0;

        var amountLabel = UiTheme.FieldLabel(_localizer["Payment.Amount"]);
        amountLabel.Location = new Point(240, 52);

        _amount.Bounds = new Rectangle(240, 70, 160, UiTheme.InputHeight);
        _amount.Tag = UiTags.LatinValue;
        _amount.TextAlign = HorizontalAlignment.Right;
        _amount.Font = UiTheme.MonospaceBold;
        // Pre-filled with the balance: settling in full is by far the commonest case.
        _amount.Text = _ceiling.ToString("0.00");

        _referenceLabel.Location = new Point(20, 106);
        _reference.Bounds = new Rectangle(20, 124, 200, UiTheme.InputHeight);
        _reference.Tag = UiTags.LatinValue;

        _bankLabel.Location = new Point(240, 106);
        _bank.Bounds = new Rectangle(240, 124, 160, UiTheme.InputHeight);

        _maturityLabel.Location = new Point(20, 160);
        _maturity.Bounds = new Rectangle(20, 178, 200, UiTheme.InputHeight);
        _maturity.Format = DateTimePickerFormat.Short;

        _warning.Bounds = new Rectangle(20, 212, 390, 40);
        _warning.Font = UiTheme.SmallBold;
        _warning.ForeColor = UiTheme.Abnormal;
        _warning.AutoSize = false;
        _warning.Visible = false;

        var ok = UiTheme.PrimaryButton(_localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 264, 200, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(_localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(240, 264, 160, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([outstanding, methodLabel, _method, amountLabel, _amount,
                           _referenceLabel, _reference, _bankLabel, _bank,
                           _maturityLabel, _maturity, _warning, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        _referenceLabel.Text = _localizer["Payment.Reference"];
        _bankLabel.Text = _localizer["Payment.Bank"];
        _maturityLabel.Text = _localizer["Payment.Maturity"];

        FinanceDialogFrame.Apply(this, _localizer["Finance.Dialog.Payment"], [], cancel, ok);
    }

    private PaymentMethod SelectedMethod =>
        _method.SelectedItem is MethodChoice choice ? choice.Value : PaymentMethod.Cash;

    /// <summary>Shows only the fields the chosen method actually needs.</summary>
    private void RefreshFieldVisibility()
    {
        var method = SelectedMethod;

        var needsReference = method is PaymentMethod.Cheque or PaymentMethod.BankTransfer
                                    or PaymentMethod.BillOfExchange or PaymentMethod.Card;

        var needsBank = method is PaymentMethod.Cheque or PaymentMethod.BankTransfer
                                or PaymentMethod.BillOfExchange;

        var needsMaturity = method is PaymentMethod.Cheque or PaymentMethod.BillOfExchange;

        _referenceLabel.Visible = _reference.Visible = needsReference;
        _bankLabel.Visible = _bank.Visible = needsBank;
        _maturityLabel.Visible = _maturity.Visible = needsMaturity;

        RefreshWarning();
    }

    private void RefreshWarning()
    {
        if (SelectedMethod != PaymentMethod.Cash || !TryParseAmount(out var amount))
        {
            _warning.Visible = false;
            return;
        }

        // Moroccan tax law penalises cash settlement above this threshold. Warned before the
        // money is taken, not after the receipt is printed.
        if (amount > Payment.CashPaymentWarningThresholdMad)
        {
            _warning.Text = _localizer.Format("Payment_CashThresholdWarning",
                Payment.CashPaymentWarningThresholdMad);
            _warning.Visible = true;
        }
        else
        {
            _warning.Visible = false;
        }
    }

    private bool TryParseAmount(out decimal amount) =>
        decimal.TryParse(_amount.Text.Replace(',', '.'),
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out amount);

    private void Accept()
    {
        if (!TryParseAmount(out var amount) || amount <= 0m)
        {
            _dialogs.Warn(_localizer["Payment_AmountMustBePositive"]);
            _amount.Focus();
            return;
        }

        if (amount > _ceiling)
        {
            _dialogs.Warn(_localizer.Format(_orderId is null ? "Payment_ExceedsOutstanding" : "Deposit_ExceedsOrderTotal",
                Money.Of(_ceiling).Format(_localizer.CurrentLanguage)));
            _amount.Focus();
            return;
        }

        var method = SelectedMethod;

        if (_reference.Visible && string.IsNullOrWhiteSpace(_reference.Text)
            && method is PaymentMethod.Cheque or PaymentMethod.BillOfExchange)
        {
            _dialogs.Warn(_localizer["Payment_ReferenceRequired"]);
            _reference.Focus();
            return;
        }

        if (method == PaymentMethod.BankTransfer && string.IsNullOrWhiteSpace(_reference.Text))
        {
            _dialogs.Warn(_localizer["Payment_TransferReferenceRequired"]);
            _reference.Focus();
            return;
        }

        var reference = string.IsNullOrWhiteSpace(_reference.Text) ? null : _reference.Text.Trim();
        var bank = string.IsNullOrWhiteSpace(_bank.Text) ? null : _bank.Text.Trim();
        DateOnly? maturity = _maturity.Visible ? DateOnly.FromDateTime(_maturity.Value.Date) : null;

        if (_orderId is { } orderId)
        {
            DepositRequest = new TakeDepositRequest(orderId, amount, method, reference, bank, maturity);
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        Request = new RecordPaymentRequest(
            _invoiceId!.Value,
            amount,
            method,
            string.IsNullOrWhiteSpace(_reference.Text) ? null : _reference.Text.Trim(),
            string.IsNullOrWhiteSpace(_bank.Text) ? null : _bank.Text.Trim(),
            _maturity.Visible ? DateOnly.FromDateTime(_maturity.Value.Date) : null,
            null);

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record MethodChoice(PaymentMethod Value, string Display)
    {
        public override string ToString() => Display;
    }
}
