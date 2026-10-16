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
/// Third-party payment: what is waiting to be claimed, what has been sent, and what came back.
/// <para>
/// AMO reimbursement is where a Moroccan laboratory's cash actually comes from, and it was the
/// largest remaining gap. `SubmitClaimBatchAsync` was reachable from the invoice ledger, but
/// nothing showed the batches afterwards — so once an invoice was submitted it left the screens
/// entirely, and an unanswered batch or a rejected invoice was invisible until somebody reconciled
/// a bank statement by hand.
/// </para>
/// <para>
/// Three panes, in the order the money moves. What is claimable, with whatever blocks each invoice
/// stated per row rather than discovered at submission — a claim refused for a missing INPE comes
/// back weeks later as a letter, by which time the work is done and the reagent is spent. Then the
/// batches, chasing-first. Then the invoices inside the selected batch, refusals first, because a
/// refusal needs a decision: correct and resend, or write off.
/// </para>
/// </summary>
public sealed class ClaimBatchView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly BillingService _billing;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<ClaimBatchView>? _logger;

    private const string ColSelected = "selected";

    private readonly Label _heading = new();
    private readonly Label _outstanding = new();
    private readonly ComboBox _window = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);

    private readonly DataGridView _candidates = UiTheme.Grid();
    private readonly DataGridView _batches = UiTheme.Grid();
    private readonly DataGridView _invoices = UiTheme.Grid();

    private readonly Label _candidateHeading = new();
    private readonly Label _batchHeading = new();
    private readonly Label _invoiceHeading = new();

    private readonly Button _submit = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _selectClaimable = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _recordOutcome = UiTheme.PrimaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<ClaimCandidateRow> _candidateRows = [];
    private IReadOnlyList<ClaimBatchRow> _batchRows = [];
    private IReadOnlyList<ClaimInvoiceRow> _invoiceRows = [];

    public ClaimBatchView(BillingService billing, IUserDialogs dialogs, ICurrentUser currentUser,
                          IClock clock, ILocalizer localizer, ICultureManager cultureManager,
                          ILogger<ClaimBatchView>? logger = null)
        : base(localizer, cultureManager)
    {
        _billing = billing;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Claims");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(24, 20, 24, 12);

        _window.DropDownStyle = ComboBoxStyle.DropDownList;
        _window.FlatStyle = FlatStyle.Flat;
        _window.Width = 170;

        // The title, what is left to recover in grey beside it, the period of the batches and the
        // refresh at the far end.
        var header = FinanceUi.PageHeader(_heading, _outstanding, _window, _refresh);
        _window.Margin = new Padding(8, 5, 0, 0);

        BuildCandidateColumns();
        BuildBatchColumns();
        BuildInvoiceColumns();

        var outer = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 240,
            SplitterWidth = 12,
            BackColor = UiTheme.Surface
        };

        // Three cards, in the order the money moves: what can be claimed, the batches sent, and
        // what the selected batch holds.
        outer.Panel1.Controls.Add(new FinanceCard(
            _candidates, FinanceUi.CardHeader(_candidateHeading, null, _selectClaimable, _submit)));

        var inner = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 560,
            SplitterWidth = 12,
            BackColor = UiTheme.Surface
        };

        inner.Panel1.Controls.Add(new FinanceCard(_batches, FinanceUi.CardHeader(_batchHeading, null, _recordOutcome)));
        inner.Panel2.Controls.Add(new FinanceCard(_invoices, FinanceUi.CardHeader(_invoiceHeading, null)));

        outer.Panel2.Controls.Add(inner);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = UiTheme.Surface, Tag = UiTags.OwnLayout };
        FinanceUi.Status(_status);
        footer.Controls.Add(_status);

        // The answer of a batch and the state of an invoice as pills.
        FinanceUi.PillColumn(_batches, "outcome", row => row.Tag is ClaimBatchRow batch
            ? batch.IsAwaitingOutcome
                ? batch.NeedsChasing(_clock.Today) ? FinanceUi.PillCritical : FinanceUi.PillRunning
                : batch.RejectedCount > 0 ? FinanceUi.PillWarning : FinanceUi.PillDone
            : null);

        FinanceUi.PillColumn(_invoices, "status", row => row.Tag is ClaimInvoiceRow invoice
            ? invoice.IsAbandoned ? FinanceUi.PillCritical
              : invoice.WasRejected ? FinanceUi.PillWarning
              : invoice.ClaimStatus == ClaimStatus.Settled ? FinanceUi.PillDone
              : FinanceUi.PillRunning
            : null);

        Controls.Add(outer);
        Controls.Add(footer);
        Controls.Add(header);
    }

    private static void AddColumns(DataGridView grid,
                                    params (string Name, int Weight, Font? Font, bool Right)[] columns)
    {
        foreach (var (name, weight, font, right) in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });
        }
    }

    private void BuildCandidateColumns()
    {
        _candidates.Columns.Clear();

        // A tick box, because a batch is a deliberate selection: one payer's invoices go together,
        // and sending a mixed batch is how a payer rejects the whole thing.
        _candidates.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColSelected, HeaderText = string.Empty, FillWeight = 5
        });

        AddColumns(_candidates,
            ("number", 13, UiTheme.MonospaceBold, false),
            ("issued", 10, UiTheme.Monospace, false),
            ("age", 6, UiTheme.Monospace, true),
            ("payer", 17, null, false),
            ("patient", 18, null, false),
            ("total", 11, UiTheme.Monospace, true),
            ("payershare", 11, UiTheme.MonospaceBold, true),
            ("blockers", 19, UiTheme.SmallBold, false));

        _candidates.EditMode = DataGridViewEditMode.EditOnEnter;
        _candidates.Column(ColSelected).ReadOnly = false;
    }

    private void BuildBatchColumns() => AddColumns(_batches,
        ("reference", 20, UiTheme.MonospaceBold, false),
        ("payer", 20, null, false),
        ("submitted", 12, UiTheme.Monospace, false),
        ("age", 7, UiTheme.Monospace, true),
        ("invoices", 7, UiTheme.Monospace, true),
        ("claimed", 14, UiTheme.MonospaceBold, true),
        ("settled", 14, UiTheme.Monospace, true),
        ("outcome", 16, UiTheme.SmallBold, false));

    private void BuildInvoiceColumns() => AddColumns(_invoices,
        ("number", 16, UiTheme.MonospaceBold, false),
        ("patient", 22, null, false),
        ("payershare", 14, UiTheme.Monospace, true),
        ("paid", 14, UiTheme.Monospace, true),
        ("status", 16, UiTheme.SmallBold, false),
        ("reason", 28, UiTheme.Small, false));

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _window.SelectedIndexChanged += async (_, _) => await LoadBatchesAsync();

        _batches.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadInvoicesAsync());

        _candidates.CellFormatting += OnCandidateFormatting;
        _batches.CellFormatting += OnBatchFormatting;
        _invoices.CellFormatting += OnInvoiceFormatting;

        // Committed immediately so the running total beside the button tracks the ticks instead of
        // lagging a cell behind.
        _candidates.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_candidates.IsCurrentCellDirty)
                _candidates.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _candidates.CellValueChanged += (_, _) => RefreshSelectionSummary();

        _selectClaimable.Click += (_, _) => SelectClaimable();
        _submit.Click += async (_, _) => await RunActionAsync(SubmitAsync);
        _recordOutcome.Click += async (_, _) => await RunActionAsync(RecordOutcomeAsync);
    }

    /// <summary>
    /// One action at a time, with the actions held disabled until it ends: the reload inside the
    /// submission moves the selection, whose RefreshActions re-enabled the button mid-await, and a
    /// second click sent a second batch.
    /// </summary>
    private async Task RunActionAsync(Func<Task> action)
    {
        var ran = await RunBusyAsync(async () =>
        {
            RefreshActions();
            await action();
        });

        if (ran && !IsDisposed) RefreshActions();
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.Claims");
        _refresh.Text = T("Action.Refresh");
        _selectClaimable.Text = T("Claim.SelectClaimable");
        _submit.Text = T("Billing.SubmitClaim");
        _recordOutcome.Text = T("Billing.RecordClaimOutcome");

        _candidateHeading.Text = T("Claim.Claimable");
        _batchHeading.Text = T("Claim.Batches");
        _invoiceHeading.Text = T("Claim.BatchContents");

        RefreshWindows();

        if (_candidates.Columns.Count > 0)
        {
            _candidates.Column("number").HeaderText = T("Billing.Number");
            _candidates.Column("issued").HeaderText = T("Billing.IssueDate");
            _candidates.Column("age").HeaderText = T("Claim.AgeDays");
            _candidates.Column("payer").HeaderText = T("Coverage.Payer");
            _candidates.Column("patient").HeaderText = T("Patient.Title");
            _candidates.Column("total").HeaderText = T("Billing.Net");
            _candidates.Column("payershare").HeaderText = T("Billing.PayerShare");
            _candidates.Column("blockers").HeaderText = T("Claim.Blockers");
        }

        if (_batches.Columns.Count > 0)
        {
            _batches.Column("reference").HeaderText = T("Billing.ClaimReference");
            _batches.Column("payer").HeaderText = T("Coverage.Payer");
            _batches.Column("submitted").HeaderText = T("Claim.SubmittedOn");
            _batches.Column("age").HeaderText = T("Claim.AgeDays");
            _batches.Column("invoices").HeaderText = T("Billing.Invoices");
            _batches.Column("claimed").HeaderText = T("Claim.Claimed");
            _batches.Column("settled").HeaderText = T("Billing.Paid");
            _batches.Column("outcome").HeaderText = T("Billing.ClaimOutcome");
        }

        if (_invoices.Columns.Count > 0)
        {
            _invoices.Column("number").HeaderText = T("Billing.Number");
            _invoices.Column("patient").HeaderText = T("Patient.Title");
            _invoices.Column("payershare").HeaderText = T("Billing.PayerShare");
            _invoices.Column("paid").HeaderText = T("Billing.Paid");
            _invoices.Column("status").HeaderText = T("Field.Status");
            _invoices.Column("reason").HeaderText = T("Billing.ClaimRejectionReason");
        }

        if (_candidateRows.Count > 0) RenderCandidates();
        if (_batchRows.Count > 0) RenderBatches();

        RefreshActions();
    }

    private void RefreshWindows()
    {
        var selected = _window.SelectedIndex;

        _window.Items.Clear();

        foreach (var days in new[] { 30, 90, 180, 365 })
            _window.Items.Add(new EnumChoice(days, T("Report.WithinDays", days)));

        _window.SelectedIndex = selected >= 0 ? selected : 2;
    }

    private int SelectedWindow => (_window.SelectedItem as EnumChoice)?.Value ?? 180;

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var candidates = await _billing.GetClaimCandidatesAsync(ct);

            if (candidates.IsFailure) { _dialogs.ShowFailure(candidates); return; }

            _candidateRows = candidates.Value;
            RenderCandidates();

            await LoadBatchesAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the claim register failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    private void RenderCandidates()
    {
        var language = Localizer.CurrentLanguage;

        _candidates.SuspendLayout();
        _candidates.Rows.Clear();

        foreach (var row in _candidateRows)
        {
            var blockers = row.Blockers.Count == 0
                ? string.Empty
                : string.Join(" · ", row.Blockers.Select(key => Localizer[key]));

            var index = _candidates.Rows.Add(
                false,
                row.Number,
                row.IssueDate.ToString("dd/MM/yy"),
                row.DaysSinceIssue.ToString(),
                row.PayerName,
                row.PatientName,
                Money.Of(row.Total).Format(language),
                Money.Of(row.PayerShare).Format(language),
                blockers);

            _candidates.Rows[index].Tag = row;
        }

        _candidates.ResumeLayout(performLayout: true);

        var claimable = _candidateRows.Count(c => c.IsClaimable);
        var blocked = _candidateRows.Count - claimable;

        var waiting = Money.Of(_candidateRows.Where(c => c.IsClaimable).Sum(c => c.PayerShare))
                           .Format(language);

        _candidateHeading.Text = blocked == 0
            ? T("Claim.ClaimableSummary", claimable, waiting)
            : T("Claim.ClaimableSummaryBlocked", claimable, waiting, blocked);

        _candidateHeading.ForeColor = blocked > 0 ? UiTheme.Abnormal : UiTheme.TextPrimary;

        RefreshSelectionSummary();
    }

    private async Task LoadBatchesAsync(CancellationToken ct = default)
    {
        var result = await _billing.GetClaimBatchesAsync(SelectedWindow, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _batchRows = result.Value;
        RenderBatches();

        await LoadInvoicesAsync(ct);
    }

    private void RenderBatches()
    {
        var language = Localizer.CurrentLanguage;
        var today = _clock.Today;
        var selected = SelectedBatch?.ClaimReference;

        _batches.SuspendLayout();
        _batches.Rows.Clear();

        foreach (var row in _batchRows)
        {
            var outcome = row.IsAwaitingOutcome
                ? row.NeedsChasing(today) ? T("Claim.NeedsChasing") : T("Claim.AwaitingOutcome")
                : row.RejectedCount > 0
                    ? T("Claim.PartlyRejected", row.RejectedCount)
                    : T("Claim.Accepted");

            var index = _batches.Rows.Add(
                row.ClaimReference,
                row.PayerName,
                row.SubmittedOn.ToString("dd/MM/yy"),
                row.DaysOutstanding(today).ToString(),
                row.InvoiceCount.ToString(),
                Money.Of(row.PayerShareClaimed).Format(language),
                Money.Of(row.Settled).Format(language),
                outcome);

            _batches.Rows[index].Tag = row;
        }

        _batches.ResumeLayout(performLayout: true);

        if (selected is { } reference)
        {
            foreach (DataGridViewRow row in _batches.Rows)
            {
                if (row.Tag is ClaimBatchRow candidate
                    && candidate.ClaimReference == reference)
                {
                    row.Selected = true;
                    break;
                }
            }
        }

        if (_batches.CurrentRow is null && _batches.Rows.Count > 0) _batches.SelectFirstRow();

        var chasing = _batchRows.Count(b => b.NeedsChasing(today));
        var unsettled = Money.Of(_batchRows.Sum(b => b.Unsettled)).Format(language);

        _batchHeading.Text = chasing == 0
            ? T("Claim.BatchSummary", _batchRows.Count, unsettled)
            : T("Claim.BatchSummaryChasing", _batchRows.Count, unsettled, chasing);

        _batchHeading.ForeColor = chasing > 0 ? UiTheme.Critical : UiTheme.TextPrimary;

        _heading.Text = T("Nav.Claims");
        _outstanding.Text = T("Claim.Outstanding", unsettled);
        _outstanding.ForeColor = chasing > 0 ? UiTheme.Critical : UiTheme.TextSecondary;

        RefreshActions();
    }

    private async Task LoadInvoicesAsync(CancellationToken ct = default)
    {
        _invoices.Rows.Clear();

        if (SelectedBatch is not { } batch)
        {
            _invoiceRows = [];
            _invoiceHeading.Text = T("Claim.BatchContents");
            RefreshActions();
            return;
        }

        var result = await _billing.GetClaimInvoicesAsync(batch.ClaimReference, batch.PayerId, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _invoiceRows = result.Value;

        var language = Localizer.CurrentLanguage;

        _invoices.SuspendLayout();

        foreach (var row in _invoiceRows)
        {
            var index = _invoices.Rows.Add(
                row.Number,
                row.PatientName,
                Money.Of(row.PayerShare).Format(language),
                Money.Of(row.Paid).Format(language),
                T($"ClaimStatus.{row.ClaimStatus}"),
                row.ClaimRejectionReason ?? string.Empty);

            _invoices.Rows[index].Tag = row;
        }

        _invoices.ResumeLayout(performLayout: true);

        _invoiceHeading.Text = $"{T("Claim.BatchContents")} — {batch.ClaimReference}";

        RefreshActions();
    }

    // ---- Formatting ----------------------------------------------------------------------------

    private void OnCandidateFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _candidates.Rows.Count) return;
        if (_candidates.Rows[e.RowIndex].Tag is not ClaimCandidateRow row) return;

        if (!row.IsClaimable)
        {
            // Blocked, and the blocker is named in its own column. Red because this is money the
            // laboratory has earned and cannot ask for yet.
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.DaysSinceIssue > 60)
        {
            // The payer's own deadline runs from the date of the act, not from the date somebody
            // got round to claiming.
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnBatchFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _batches.Rows.Count) return;
        if (_batches.Rows[e.RowIndex].Tag is not ClaimBatchRow row) return;

        if (row.NeedsChasing(_clock.Today))
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.RejectedCount > 0) e.CellStyle!.ForeColor = UiTheme.Abnormal;
        else if (!row.IsAwaitingOutcome && row.Unsettled == 0m) e.CellStyle!.ForeColor = UiTheme.Normal;
    }

    private void OnInvoiceFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _invoices.Rows.Count) return;
        if (_invoices.Rows[e.RowIndex].Tag is not ClaimInvoiceRow row) return;

        if (row.IsAbandoned)
        {
            // Rejected and not resubmitted: earned and abandoned, which is the row this screen
            // exists to surface.
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.WasRejected) e.CellStyle!.ForeColor = UiTheme.Abnormal;
        else if (row.ClaimStatus == ClaimStatus.Settled) e.CellStyle!.ForeColor = UiTheme.Normal;
    }

    // ---- Selection -----------------------------------------------------------------------------

    private ClaimBatchRow? SelectedBatch => _batches.CurrentRow?.Tag as ClaimBatchRow;

    private List<ClaimCandidateRow> Ticked =>
    [
        .. _candidates.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[ColSelected].Value is true && r.Tag is ClaimCandidateRow)
            .Select(r => (ClaimCandidateRow)r.Tag!)
    ];

    /// <summary>
    /// Ticks every invoice that can actually be claimed, for the commonest action on this screen:
    /// send everything that is ready. Blocked invoices are left alone rather than ticked and then
    /// refused, so the selection is always submittable.
    /// </summary>
    private void SelectClaimable()
    {
        _candidates.EndEdit();

        foreach (DataGridViewRow row in _candidates.Rows)
        {
            if (row.Tag is not ClaimCandidateRow candidate) continue;

            row.Cells[ColSelected].Value = candidate.IsClaimable;
        }

        RefreshSelectionSummary();
    }

    private void RefreshSelectionSummary()
    {
        var ticked = Ticked;

        if (ticked.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = string.Empty;
            RefreshActions();
            return;
        }

        var payers = ticked.Select(t => t.PayerId).Distinct().Count();
        var value = Money.Of(ticked.Sum(t => t.PayerShare)).Format(Localizer.CurrentLanguage);

        _status.ForeColor = payers > 1 ? UiTheme.Critical : UiTheme.Accent;

        _status.Text = payers > 1
            ? T("Claim.MixedPayerSelection", ticked.Count, payers)
            : T("Claim.SelectionSummary", ticked.Count, value);

        RefreshActions();
    }

    private void RefreshActions()
    {
        var ticked = Ticked;
        var idle = !IsBusy;

        var onePayer = idle && ticked.Count > 0
                       && ticked.Select(t => t.PayerId).Distinct().Count() == 1;

        // One payer per batch, and every member claimable. A payer that receives a batch containing
        // another payer's invoices rejects the batch, not the invoice.
        _submit.Enabled = onePayer && ticked.All(t => t.IsClaimable)
                          && _currentUser.Has(Permissions.Billing.ExportAccounting);

        _selectClaimable.Enabled = _candidateRows.Any(c => c.IsClaimable);

        _recordOutcome.Enabled = idle && SelectedBatch is not null
                                 && _currentUser.Has(Permissions.Billing.ExportAccounting);
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task SubmitAsync()
    {
        _candidates.EndEdit();

        var ticked = Ticked;

        if (ticked.Count == 0) return;

        var payer = ticked[0].PayerName;
        var value = Money.Of(ticked.Sum(t => t.PayerShare)).Format(Localizer.CurrentLanguage);

        if (!_dialogs.Confirm(T("Claim.ConfirmSubmit", ticked.Count, payer, value),
                               T("Billing.SubmitClaim")))
            return;

        _submit.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _billing.SubmitClaimBatchAsync(
                [.. ticked.Select(t => t.InvoiceId)]);

            if (result.IsFailure)
            {
                // Reported per invoice by the service, so the operator is told which one and why
                // rather than that the batch failed.
                _dialogs.ShowFailure(result, T("Billing.SubmitClaim"));
                await ActivateAsync();
                return;
            }

            var batch = result.Value;

            if (batch.Skipped.Count == 0)
            {
                _status.ForeColor = UiTheme.Normal;
                _status.Text = T("Claim.Submitted", batch.Reference, batch.Submitted, value);
            }
            else
            {
                // The batch went, without some invoices: its reference first, then why the others
                // stayed behind. Shown as a plain failure, it read as if nothing had been sent.
                var partial = T("Claim.PartiallySubmitted", batch.Reference, batch.Submitted,
                                batch.Skipped.Count);

                _status.ForeColor = UiTheme.Critical;
                _status.Text = partial;
                _dialogs.Warn(partial + Environment.NewLine + Environment.NewLine
                              + Localizer.Describe(batch.Skipped),
                              T("Billing.SubmitClaim"));
            }

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            RefreshActions();
        }
    }

    /// <summary>
    /// Records the payer's answer across the selected batch.
    /// <para>
    /// Applied to the batch rather than to one invoice because that is how a payer answers: one
    /// advice note covering a reference, with the refusals listed on it. Recording them one at a
    /// time is how half a batch ends up still showing as unanswered.
    /// </para>
    /// </summary>
    private async Task RecordOutcomeAsync()
    {
        if (SelectedBatch is not { } batch) return;

        if (_invoiceRows.Count == 0) return;

        using var dialog = new ClaimOutcomeDialog(Localizer, _dialogs, _invoiceRows.Count);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _billing.RecordClaimOutcomeAsync(
            [.. _invoiceRows.Select(i => i.InvoiceId)], dialog.Outcome, dialog.RejectionReason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Billing.RecordClaimOutcome"));
            await LoadBatchesAsync();
            return;
        }

        // Invoices already settled one by one keep their own answer; the operator is told which.
        if (result.Value.Skipped.Count > 0)
            _dialogs.Warn(Localizer.Describe(result.Value.Skipped), T("Billing.RecordClaimOutcome"));

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Claim.OutcomeRecorded", batch.ClaimReference, result.Value.Updated);

        await LoadBatchesAsync();
    }
}
