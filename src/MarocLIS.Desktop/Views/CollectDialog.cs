using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Input;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// « Encaissement » at the workstation, wherever the patient is: at the end of « Nouvelle demande »,
/// on the file, at the report counter and from « À encaisser ». It reads the file's balance, the
/// user's last method and the till, opens <see cref="CollectDialog"/>, and offers the receipt as a
/// PDF in the viewer (never a printer). The money is <see cref="CounterCollectionService"/>'s — the
/// invoicing service, the deposits and the settlements of « Facturation », nothing of its own — and
/// the checks are <see cref="CounterCollectionRules"/>', the same as the web's panel.
/// </summary>
public sealed class CounterCollectionFlow
{
    // Offered once a session: a clerk who said « no » is not asked again at every screen.
    private static readonly HashSet<Guid> TillOfferedTo = [];

    private readonly CounterCollectionService _collection;
    private readonly BillingService _billing;
    private readonly IInvoiceRenderer _renderer;
    private readonly IDocumentOutput _documents;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IUserDialogs _dialogs;
    private readonly ILocalizer _localizer;
    private readonly ILogger<CounterCollectionFlow>? _logger;

    public CounterCollectionFlow(CounterCollectionService collection, BillingService billing, IInvoiceRenderer renderer,
                                 IDocumentOutput documents, ICurrentUser user, IClock clock, IUserDialogs dialogs,
                                 ILocalizer localizer, ILogger<CounterCollectionFlow>? logger = null)
    {
        _collection = collection;
        _billing = billing;
        _renderer = renderer;
        _documents = documents;
        _user = user;
        _clock = clock;
        _dialogs = dialogs;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>Whether the user takes payments: without the right, « Encaisser » is not offered.</summary>
    public bool CanCollect => CounterCollectionRules.CanCollect(_user.Has);

    /// <summary>Whether « Encaisser » is offered on a file whose payment status is <paramref name="payment"/>.</summary>
    public bool Offers(OrderPayment? payment) => CounterCollectionRules.Offers(payment, _user.Has);

    internal ILocalizer Localizer => _localizer;

    internal IUserDialogs Dialogs => _dialogs;

    internal DateTime Today => _clock.Today.ToDateTime(TimeOnly.MinValue);

    /// <summary>
    /// Opens « Encaissement » on a file and returns what was taken, or null when nothing was
    /// (« Plus tard », nothing due, a refusal).
    /// </summary>
    /// <param name="intro">Lines said above the figures: the registration's breakdown and labels.</param>
    public async Task<CollectionDone?> CollectAsync(IWin32Window? owner, Guid labOrderId, IReadOnlyList<string>? intro = null)
    {
        if (!CanCollect) return null;

        CounterBalance balance;
        PaymentMethod method;
        TillState? till;

        try
        {
            var read = await _collection.GetBalanceAsync(labOrderId);
            if (read.IsFailure)
            {
                _dialogs.ShowFailure(read, _localizer["Collect.Title"]);
                return null;
            }

            balance = read.Value;
            method = await _collection.GetPreferredMethodAsync();
            var state = await _collection.GetTillStateAsync();
            till = state.IsSuccess ? state.Value : null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reading the file's balance failed.");
            _dialogs.Warn(_localizer[FailureMessages.KeyFor(ex)], _localizer["Collect.Title"]);
            return null;
        }

        using var dialog = new CollectDialog(this, balance, method, till, intro ?? [], _user.Has(Permissions.Billing.CreateInvoice));
        dialog.ShowDialog(owner);
        return dialog.Done;
    }

    /// <summary>
    /// At the start of the counter: with no till open, offers to open it with its float, once a
    /// session, so cash is not refused at the first patient. A till left open at another site is said.
    /// </summary>
    public async Task OfferTillAsync()
    {
        if (!CanCollect || !TillOfferedTo.Add(_user.UserId)) return;

        try
        {
            var state = await _collection.GetTillStateAsync();
            if (state.IsFailure) return;

            if (state.Value == TillState.OpenElsewhere)
            {
                _dialogs.Warn(_localizer["CashSession_OpenOnAnotherSite"], _localizer["Collect.OpenTillTitle"]);
                return;
            }

            if (!CounterCollectionRules.OffersTillOpening(state.Value, _user.Has)) return;

            var typed = _dialogs.PromptForText(_localizer["Collect.TillNotOpen"] + Environment.NewLine + _localizer["Payment.OpeningFloat"],
                                               _localizer["Collect.OpenTillTitle"], "0.00", CashDeskRules.AmountMaxLength);
            if (typed is null) return;

            if (await OpenTillAsync(typed) is { } said) _dialogs.Info(said, _localizer["Collect.OpenTillTitle"]);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Offering to open the till failed.");
            _dialogs.Warn(_localizer[FailureMessages.KeyFor(ex)], _localizer["Collect.OpenTillTitle"]);
        }
    }

    /// <summary>Opens the till with the float typed, as « Caisse » does; returns what to say, or null when refused (said).</summary>
    internal async Task<string?> OpenTillAsync(string floatText)
    {
        if (CashDeskRules.CheckOpening(floatText) is { } refusal)
        {
            _dialogs.Warn(refusal.Typed is { } typed ? _localizer.Format(refusal.Key, typed) : _localizer[refusal.Key],
                          _localizer["Collect.OpenTillTitle"]);
            return null;
        }

        var opening = TypedNumber.Parse(floatText) ?? 0m;
        var result = await _billing.OpenCashSessionAsync(opening);
        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, _localizer["Collect.OpenTillTitle"]);
            return null;
        }

        return _localizer.Format("Collect.TillOpened", Money.Of(opening).Format(_localizer.CurrentLanguage));
    }

    internal Task<Result<CollectionDone>> TakeAsync(CollectRequest request) => _collection.CollectAsync(request);

    /// <summary>The receipt of what was taken, opened in the PDF viewer like every document of the workstation.</summary>
    internal async Task OpenReceiptAsync(CollectionDone done)
    {
        var title = _localizer["Collect.ReceiptPdf"];
        try
        {
            var document = await _collection.GetReceiptAsync(done);
            if (document.IsFailure)
            {
                _dialogs.ShowFailure(document, title);
                return;
            }

            var language = _localizer.CurrentLanguage;
            var bytes = await Task.Run(() => _renderer.RenderReceipt(document.Value, language));
            var opened = await _documents.OpenAsync(bytes, "pdf");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, title);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Rendering the receipt failed.");
            _dialogs.Warn(_localizer[FailureMessages.KeyFor(ex)], title);
        }
    }

    /// <summary>What was done, as the dialog and the screens' status lines say it.</summary>
    public string Say(CollectionDone done)
    {
        string Mad(decimal amount) => Money.Of(amount).Format(_localizer.CurrentLanguage);
        return _localizer.Format(CounterCollectionRules.DoneKey(done), Mad(done.Amount),
                                 done.InvoiceNumber ?? done.DepositNumber ?? string.Empty, Mad(done.RemainingDue));
    }
}

/// <summary>
/// The « Encaissement » dialog: the patient's share as computed (after the payer and the
/// discounts), what was already paid and what is due, then « Tout payer » (F9), « Acompte » (F10,
/// its amount editable) or « Plus tard » (Échap). The method opens on the user's last one; cash
/// shows the change to give back, and the till when none is open. Done, it says what was taken and
/// offers the receipt.
/// </summary>
internal sealed class CollectDialog : Form
{
    private const int StartX = 20;
    private const int SecondX = 290;
    private const int FieldWidth = 250;
    private const int ContentWidth = 520;

    private readonly CounterCollectionFlow _flow;
    private readonly ILocalizer _localizer;
    private readonly CounterBalance _balance;
    private TillState? _till;

    private readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _amount = UiTheme.Input(FieldWidth);
    private readonly TextBox _tendered = UiTheme.Input(FieldWidth);
    private readonly TextBox _reference = UiTheme.Input(FieldWidth);
    private readonly TextBox _bank = UiTheme.Input(FieldWidth);
    private readonly DateTimePicker _maturity = new() { Format = DateTimePickerFormat.Short };
    private readonly TextBox _float = UiTheme.Input(120);
    private readonly Label _methodLabel = UiTheme.FieldLabel(string.Empty);
    private readonly Label _amountLabel = UiTheme.FieldLabel(string.Empty);
    private readonly Label _tenderedLabel = UiTheme.FieldLabel(string.Empty);
    private readonly Label _referenceLabel = UiTheme.FieldLabel(string.Empty);
    private readonly Label _bankLabel = UiTheme.FieldLabel(string.Empty);
    private readonly Label _maturityLabel = UiTheme.FieldLabel(string.Empty);
    private readonly Label _change = new() { AutoSize = false, Font = UiTheme.SectionTitle, ForeColor = UiTheme.Accent, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _tillText = new() { AutoSize = false, Font = UiTheme.SmallBold, ForeColor = UiTheme.Abnormal };
    private readonly Button _openTill = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _warning = new() { AutoSize = false, Font = UiTheme.SmallBold, ForeColor = UiTheme.Abnormal, Visible = false };
    private readonly Label _shortcuts = new() { AutoSize = false, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary };
    private readonly Label _doneText = new() { AutoSize = false, Font = UiTheme.SectionTitle, ForeColor = UiTheme.Normal, Visible = false };
    private readonly Label _changeGiven = new() { AutoSize = false, Font = UiTheme.SectionTitle, ForeColor = UiTheme.Accent, Visible = false };

    private readonly Button _later = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _deposit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _payAll = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _receipt = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _close = UiTheme.PrimaryButton(string.Empty);

    private readonly List<Control> _entry = [];
    private bool _busy;

    /// <summary>What was taken; null when nothing was.</summary>
    internal CollectionDone? Done { get; private set; }

    internal CollectDialog(CounterCollectionFlow flow, CounterBalance balance, PaymentMethod lastMethod, TillState? till,
                           IReadOnlyList<string> intro, bool issuesInvoices)
    {
        _flow = flow;
        _localizer = flow.Localizer;
        _balance = balance;
        _till = till;

        Text = _localizer.Format("Collect.TitleOf", $"{balance.AccessionNumber} · {balance.PatientName}");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(ContentWidth + 2 * StartX, 400);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;
        KeyPreview = true;

        BuildLayout(intro, CounterCollectionRules.DefaultMethod(lastMethod), issuesInvoices);

        _method.SelectedIndexChanged += (_, _) => RefreshFields();
        _amount.TextChanged += (_, _) => RefreshFigures();
        _tendered.TextChanged += (_, _) => RefreshFigures();
        _payAll.Click += async (_, _) => await TakeAsync(CollectionKind.Full);
        _deposit.Click += async (_, _) => await TakeAsync(CollectionKind.Deposit);
        _openTill.Click += async (_, _) => await OpenTillAsync();
        _receipt.Click += async (_, _) => await ReceiptAsync();

        RefreshFields();
    }

    private string Mad(decimal amount) => Money.Of(amount).Format(_localizer.CurrentLanguage);

    private string Say(string caption, string value) => _localizer.Format("App.LabelValue", caption, value);

    private static int Measure(string text, Font font, int width) =>
        string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, font, new Size(width, 0), TextFormatFlags.WordBreak).Height + 2;

    private void BuildLayout(IReadOnlyList<string> intro, PaymentMethod method, bool issuesInvoices)
    {
        var y = 0;

        if (intro.Count > 0)
        {
            var lines = string.Join(Environment.NewLine, intro);
            var height = Measure(lines, UiTheme.Small, ContentWidth);
            Controls.Add(new Label
            {
                Text = lines, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, AutoSize = false,
                UseMnemonic = false, Bounds = new Rectangle(StartX, y, ContentWidth, height)
            });
            y += height + 10;
        }

        // The breakdown: the file's total, less the discount, the payer's share and what was paid.
        void Figure(string key, decimal amount, bool less)
        {
            Controls.Add(new Label
            {
                Text = _localizer[key], Font = UiTheme.Body, ForeColor = UiTheme.TextSecondary, AutoSize = false,
                UseMnemonic = false, Bounds = new Rectangle(StartX, y, 300, 22), TextAlign = ContentAlignment.MiddleLeft
            });
            Controls.Add(new Label
            {
                Text = (less ? "− " : string.Empty) + Mad(amount), Font = UiTheme.Monospace, ForeColor = UiTheme.TextPrimary,
                AutoSize = false, Bounds = new Rectangle(StartX + 300, y, ContentWidth - 300, 22), TextAlign = ContentAlignment.MiddleRight
            });
            y += 22;
        }

        Figure("Collect.Total", _balance.Total, less: false);
        if (_balance.Discount > 0m) Figure("Collect.Discount", _balance.Discount, less: true);
        if (_balance.PayerShare > 0m) Figure("Collect.PayerShare", _balance.PayerShare, less: true);
        if (_balance.AlreadyPaid > 0m) Figure("Collect.AlreadyPaid", _balance.AlreadyPaid, less: true);

        y += 6;
        Controls.Add(new Panel { BackColor = UiTheme.Border, Bounds = new Rectangle(StartX, y, ContentWidth, 1) });
        y += 8;

        Controls.Add(new Label
        {
            Text = _localizer["Collect.Due"], Font = UiTheme.SectionTitle, ForeColor = UiTheme.TextPrimary, AutoSize = false,
            Bounds = new Rectangle(StartX, y, 300, 36), TextAlign = ContentAlignment.MiddleLeft
        });
        Controls.Add(new Label
        {
            Text = Mad(_balance.Due), Font = UiTheme.Kpi, ForeColor = UiTheme.Accent, AutoSize = false,
            Bounds = new Rectangle(StartX + 300, y, ContentWidth - 300, 36), TextAlign = ContentAlignment.MiddleRight
        });
        y += 40;

        var note = _balance switch
        {
            { InvoiceNumber: { } number, Payment.CoveredOrders: > 1 } => _localizer.Format("Collect.SharedInvoice", number, _balance.Payment.CoveredOrders),
            { InvoiceNumber: { } number } => _localizer.Format("Collect.OnInvoice", number),
            _ => _localizer[issuesInvoices ? "Collect.NotInvoiced" : "Collect.NotInvoicedNoIssue"]
        };
        if (_balance.Due <= 0m) note = _localizer["Collect.NothingDue"];
        var noteHeight = Measure(note, UiTheme.Small, ContentWidth);
        Controls.Add(new Label
        {
            Text = note, Font = UiTheme.Small, ForeColor = _balance.Due <= 0m ? UiTheme.Normal : UiTheme.TextSecondary,
            AutoSize = false, UseMnemonic = false, Bounds = new Rectangle(StartX, y, ContentWidth, noteHeight)
        });
        y += noteHeight + 10;

        // What was taken, in the place of the fields once done.
        _doneText.Bounds = new Rectangle(StartX, y, ContentWidth, 48);
        _doneText.UseMnemonic = false;
        _changeGiven.Bounds = new Rectangle(StartX, y + 52, ContentWidth, 28);

        // Method and the deposit's amount.
        _methodLabel.Location = new Point(StartX, y);
        _method.Bounds = new Rectangle(StartX, y + 18, FieldWidth, UiTheme.InputHeight);
        foreach (var choice in BillingScreenRules.CounterMethods)
            _method.Items.Add(new MethodChoice(choice, _localizer[$"PaymentMethod.{choice}"]));
        _method.SelectedIndex = Math.Max(0, BillingScreenRules.CounterMethods.ToList().IndexOf(method));

        _amountLabel.Location = new Point(SecondX, y);
        _amount.Bounds = new Rectangle(SecondX, y + 18, FieldWidth, UiTheme.InputHeight);
        _amount.Tag = UiTags.LatinValue;
        _amount.TextAlign = HorizontalAlignment.Right;
        _amount.Font = UiTheme.MonospaceBold;
        _amount.MaxLength = EditorValues.AmountMaxLength;
        _amount.Text = _balance.Due.ToString("0.00", CultureInfo.InvariantCulture);
        y += 56;

        // Cash: the sum handed over and the change. Otherwise: the reference and the bank, in the same place.
        _tenderedLabel.Location = new Point(StartX, y);
        _tendered.Bounds = new Rectangle(StartX, y + 18, FieldWidth, UiTheme.InputHeight);
        _tendered.Tag = UiTags.LatinValue;
        _tendered.TextAlign = HorizontalAlignment.Right;
        _tendered.Font = UiTheme.Monospace;
        _tendered.MaxLength = EditorValues.AmountMaxLength;
        _change.Bounds = new Rectangle(SecondX, y + 14, FieldWidth, 30);

        _referenceLabel.Location = new Point(StartX, y);
        _reference.Bounds = new Rectangle(StartX, y + 18, FieldWidth, UiTheme.InputHeight);
        _reference.Tag = UiTags.LatinValue;
        _reference.MaxLength = BillingScreenRules.ReferenceMaxLength;
        _bankLabel.Location = new Point(SecondX, y);
        _bank.Bounds = new Rectangle(SecondX, y + 18, FieldWidth, UiTheme.InputHeight);
        _bank.MaxLength = BillingScreenRules.BankMaxLength;
        y += 56;

        // Cash with no till open: open it here. A cheque or a bill of exchange: its maturity.
        _tillText.Bounds = new Rectangle(StartX, y, ContentWidth, 18);
        _float.Bounds = new Rectangle(StartX, y + 20, 120, UiTheme.InputHeight);
        _float.Tag = UiTags.LatinValue;
        _float.TextAlign = HorizontalAlignment.Right;
        _float.MaxLength = CashDeskRules.AmountMaxLength;
        _float.Text = "0.00";
        _openTill.Bounds = new Rectangle(StartX + 130, y + 18, 200, UiTheme.ButtonHeight);
        ButtonIcons.Set(_openTill, Glyph.LockOpen);

        _maturityLabel.Location = new Point(StartX, y);
        _maturity.Bounds = new Rectangle(StartX, y + 18, FieldWidth, UiTheme.InputHeight);
        _maturity.Value = _flow.Today;
        y += 56;

        _warning.Bounds = new Rectangle(StartX, y, ContentWidth, 34);
        y += 36;
        _shortcuts.Bounds = new Rectangle(StartX, y, ContentWidth, 18);

        _entry.AddRange([_methodLabel, _method, _amountLabel, _amount, _tenderedLabel, _tendered, _change,
                         _referenceLabel, _reference, _bankLabel, _bank, _tillText, _float, _openTill,
                         _maturityLabel, _maturity, _warning, _shortcuts]);

        Controls.AddRange([.. _entry, _doneText, _changeGiven, _later, _deposit, _payAll]);

        _methodLabel.Text = _localizer["Payment.Method"];
        _amountLabel.Text = _localizer["Collect.DepositAmount"];
        _tenderedLabel.Text = _localizer["Collect.Tendered"];
        _referenceLabel.Text = _localizer["Payment.Reference"];
        _bankLabel.Text = _localizer["Payment.Bank"];
        _maturityLabel.Text = _localizer["Payment.Maturity"];
        _tillText.Text = _localizer["Collect.TillNotOpen"];
        _openTill.Text = _localizer["Payment.OpenSession"];
        _shortcuts.Text = _localizer["Collect.Shortcuts"];
        _later.Text = _localizer["Collect.Later"];
        _deposit.Text = _localizer["Collect.Deposit"];
        _payAll.Text = _localizer["Collect.PayAll"];
        _receipt.Text = _localizer["Collect.ReceiptPdf"];
        _close.Text = _localizer["Action.Close"];

        ButtonIcons.Set(_later, Glyph.Clock);
        ButtonIcons.Set(_deposit, Glyph.HandCoins);
        ButtonIcons.Set(_payAll, Glyph.Banknote);
        ButtonIcons.Set(_receipt, Glyph.Receipt);
        ShortcutHints.Attach(_deposit, CounterCollectionRules.DepositShortcut);
        ShortcutHints.Attach(_payAll, CounterCollectionRules.FullShortcut);

        _later.DialogResult = DialogResult.Cancel;
        CancelButton = _later;

        if (_balance.Due <= 0m)
        {
            foreach (var control in _entry) control.Visible = false;
            _deposit.Enabled = _payAll.Enabled = false;
        }

        FinanceDialogFrame.Apply(this, _localizer["Collect.Title"], [_later], _deposit, _payAll);

        // The done state's buttons take the places of « Plus tard » and « Tout payer ».
        _receipt.Bounds = _later.Bounds with { Width = Math.Max(_later.Width, TextRenderer.MeasureText(_receipt.Text, _receipt.Font).Width + 48) };
        _close.Bounds = _payAll.Bounds;
        _receipt.Visible = _close.Visible = false;
        _close.DialogResult = DialogResult.OK;
        Controls.AddRange([_receipt, _close]);

        // Mirrored in Arabic like the other dialogs; the amounts stay left to right.
        RtlLayout.Apply(this, _localizer.CurrentLanguage);
    }

    private PaymentMethod SelectedMethod =>
        _method.SelectedItem is MethodChoice choice ? choice.Value : PaymentMethod.Cash;

    /// <summary>Shows only what the chosen method needs: the sum handed over and the till for cash, the reference otherwise.</summary>
    private void RefreshFields()
    {
        if (Done is not null || _balance.Due <= 0m) return;

        var method = SelectedMethod;
        var fields = BillingScreenRules.FieldsFor(method);
        var cash = method == PaymentMethod.Cash;
        var tillOffered = cash && _till is { } till && CounterCollectionRules.OffersTillOpening(till, _ => true);

        _tenderedLabel.Visible = _tendered.Visible = _change.Visible = cash;
        _referenceLabel.Visible = _reference.Visible = !cash && fields.Reference;
        _bankLabel.Visible = _bank.Visible = !cash && fields.Bank;
        _tillText.Visible = _float.Visible = _openTill.Visible = tillOffered;
        _maturityLabel.Visible = _maturity.Visible = !cash && fields.Maturity;

        RefreshFigures();
    }

    /// <summary>The deposit's amount, else the whole due: what the change and the cash warning are read against.</summary>
    private string AmountText =>
        string.IsNullOrWhiteSpace(_amount.Text) ? _balance.Due.ToString("0.00", CultureInfo.InvariantCulture) : _amount.Text;

    private void RefreshFigures()
    {
        if (Done is not null) return;

        var method = SelectedMethod;
        var amount = CounterCollectionRules.Amount(CollectionKind.Deposit, AmountText, _balance.Due) ?? _balance.Due;
        _change.Text = CounterCollectionRules.Change(method, _tendered.Text, amount) is { } change
            ? _localizer.Format("Collect.Change", Mad(change))
            : string.Empty;

        if (BillingScreenRules.WarnsCash(method, AmountText))
        {
            _warning.Text = _localizer.Format("Payment_CashThresholdWarning", Payment.CashPaymentWarningThresholdMad);
            _warning.Visible = true;
        }
        else
        {
            _warning.Visible = false;
        }
    }

    /// <summary>F9 takes the whole due, F10 the deposit typed: the keys of the web's panel.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F9:
                if (_payAll.Visible && _payAll.Enabled) _payAll.PerformClick();
                return true;
            case Keys.F10:
                if (_deposit.Visible && _deposit.Enabled) _deposit.PerformClick();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_balance.Due <= 0m) return;

        // Cash: the clerk types what the patient handed over; otherwise F9 is all it takes.
        if (_tendered.Visible) _tendered.Focus();
        else _payAll.Focus();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _later.Enabled = _receipt.Enabled = _close.Enabled = _openTill.Enabled = !busy;
        _deposit.Enabled = _payAll.Enabled = !busy && _balance.Due > 0m && Done is null;
    }

    private async Task TakeAsync(CollectionKind kind)
    {
        if (_busy || Done is not null || _balance.Due <= 0m) return;

        var method = SelectedMethod;
        if (CounterCollectionRules.Check(kind, _amount.Text, _balance.Due, _balance.IsInvoiced, method,
                                         _reference.Text, _tendered.Text) is { } refusal)
        {
            _flow.Dialogs.Warn(refusal switch
            {
                { Typed: { } typed } => _localizer.Format(refusal.Key, typed),
                { Amount: { } amount } => _localizer.Format(refusal.Key, Mad(amount)),
                _ => _localizer[refusal.Key]
            }, Text);

            var box = refusal.Field switch
            {
                "reference" => _reference,
                "tendered" => _tendered,
                _ => _amount
            };
            if (box.Visible)
            {
                box.Focus();
                box.SelectAll();
            }
            return;
        }

        var fields = BillingScreenRules.FieldsFor(method);
        var request = new CollectRequest(
            _balance.LabOrderId, kind, CounterCollectionRules.Amount(kind, _amount.Text, _balance.Due)!.Value, method,
            fields.Reference && !string.IsNullOrWhiteSpace(_reference.Text) ? _reference.Text.Trim() : null,
            fields.Bank && !string.IsNullOrWhiteSpace(_bank.Text) ? _bank.Text.Trim() : null,
            fields.Maturity ? DateOnly.FromDateTime(_maturity.Value.Date) : null);

        SetBusy(true);
        try
        {
            var result = await _flow.TakeAsync(request);
            if (result.IsFailure)
            {
                _flow.Dialogs.ShowFailure(result, Text);
                return;
            }

            ShowDone(result.Value, CounterCollectionRules.Change(method, _tendered.Text, result.Value.Amount));
        }
        catch (Exception ex)
        {
            _flow.Dialogs.Warn(_localizer[FailureMessages.KeyFor(ex)], Text);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void ShowDone(CollectionDone done, decimal? change)
    {
        Done = done;

        foreach (var control in _entry) control.Visible = false;
        _later.Visible = _deposit.Visible = _payAll.Visible = false;

        _doneText.Text = _flow.Say(done);
        _doneText.Visible = true;
        if (change is > 0m)
        {
            _changeGiven.Text = _localizer.Format("Collect.ChangeGiven", Mad(change.Value));
            _changeGiven.Visible = true;
        }

        _receipt.Visible = _close.Visible = true;
        CancelButton = _close;
        AcceptButton = _close;
        _close.Focus();
    }

    private async Task OpenTillAsync()
    {
        if (_busy) return;

        SetBusy(true);
        try
        {
            if (await _flow.OpenTillAsync(_float.Text) is not { } said) return;

            _till = TillState.Open;
            RefreshFields();
            _flow.Dialogs.Info(said, _localizer["Collect.OpenTillTitle"]);
        }
        catch (Exception ex)
        {
            _flow.Dialogs.Warn(_localizer[FailureMessages.KeyFor(ex)], _localizer["Collect.OpenTillTitle"]);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private async Task ReceiptAsync()
    {
        if (_busy || Done is not { } done) return;

        SetBusy(true);
        try
        {
            await _flow.OpenReceiptAsync(done);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private sealed record MethodChoice(PaymentMethod Value, string Display)
    {
        public override string ToString() => Display;
    }
}
