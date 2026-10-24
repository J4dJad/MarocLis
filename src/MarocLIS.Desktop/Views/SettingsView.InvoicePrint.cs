using MarocLIS.Application.Administration;
using MarocLIS.Application.Billing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The « Factures » tab: the default format of a patient invoice, and the display options of each
/// format. Saved as installation settings; they apply to the next print or reprint and never change
/// an amount.
/// </summary>
public sealed partial class SettingsView
{
    private readonly InvoicePrintSettingsService? _invoicePrint;
    private readonly TabPage _invoicePrintTab = new();
    private readonly Label _invoicePrintIntro = new();
    private readonly Label _invoiceFormatLabel = new();
    private readonly ComboBox _invoiceFormat = new();
    private readonly Label _standardOptionsLabel = new();
    private readonly CheckBox _standardNationalCode = new();
    private readonly CheckBox _standardCoefficient = new();
    private readonly CheckBox _standardWords = new();
    private readonly Label _cotationOptionsLabel = new();
    private readonly CheckBox _cotationLinePrice = new();
    private readonly CheckBox _cotationZeroFee = new();
    private readonly CheckBox _cotationWords = new();
    private readonly CheckBox _cotationLetterValue = new();
    private readonly Button _saveInvoicePrint = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _reloadInvoicePrint = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _invoicePrintStatus = new();
    private InvoicePrintSettings _invoicePrintSettings = InvoicePrintSettings.Default;

    // The discount ceiling (« Plafond des remises »): saved by the same button, traced on its own.
    private readonly Label _capHeading = new();
    private readonly Label _capIntro = new();
    private readonly Label _capPercentLabel = new();
    private readonly TextBox _capPercent = UiTheme.Input(120);
    private readonly Label _capAmountLabel = new();
    private readonly TextBox _capAmount = UiTheme.Input(200);
    private DiscountCapSettings _discountCap = DiscountCapSettings.Default;

    private void BuildInvoicePrintTab()
    {
        _invoicePrintTab.BackColor = UiTheme.Surface;

        static Label Text(Label label, Font font, Color colour, Rectangle bounds)
        {
            label.Font = font;
            label.ForeColor = colour;
            label.AutoSize = false;
            label.Bounds = bounds;
            return label;
        }

        static CheckBox Option(CheckBox box, int x, int y)
        {
            box.Font = UiTheme.Body;
            box.AutoSize = false;
            box.Bounds = new Rectangle(x, y, 490, 24);
            return box;
        }

        Text(_invoicePrintIntro, UiTheme.Small, UiTheme.TextSecondary, new Rectangle(0, 0, 760, 40));

        Text(_invoiceFormatLabel, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 50, 760, 22));
        _invoiceFormat.DropDownStyle = ComboBoxStyle.DropDownList;
        _invoiceFormat.FlatStyle = FlatStyle.Flat;
        _invoiceFormat.Bounds = new Rectangle(0, 74, 520, UiTheme.InputHeight);

        Text(_standardOptionsLabel, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 124, 490, 22));
        Option(_standardNationalCode, 0, 150);
        Option(_standardCoefficient, 0, 176);
        Option(_standardWords, 0, 202);

        Text(_cotationOptionsLabel, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 242, 490, 22));
        Option(_cotationLinePrice, 0, 268);
        Option(_cotationZeroFee, 0, 294);
        Option(_cotationWords, 0, 320);
        Option(_cotationLetterValue, 0, 346);

        // The discount ceiling beside the format options, in the second half of the column: below
        // them it fell under the fold at 1366×768.
        Text(_capHeading, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(510, 124, 250, 22));
        Text(_capIntro, UiTheme.Small, UiTheme.TextSecondary, new Rectangle(510, 148, 250, 92));
        // Captions 24 px high: the Arabic font needs it.
        Text(_capPercentLabel, UiTheme.SmallBold, UiTheme.TextSecondary, new Rectangle(510, 246, 250, 24));
        _capPercent.Location = new Point(510, 272);
        Text(_capAmountLabel, UiTheme.SmallBold, UiTheme.TextSecondary, new Rectangle(510, 308, 250, 24));
        _capAmount.Location = new Point(510, 334);
        foreach (var box in new[] { _capPercent, _capAmount })
        {
            box.Tag = UiTags.LatinValue;
            box.TextAlign = HorizontalAlignment.Right;
            box.MaxLength = EditorValues.AmountMaxLength;
        }

        // The settings in a column of their own width, on the reading side: in Arabic the column
        // goes to the right of the card and each field to the right of the column, under its label,
        // where the fixed positions had left the list of formats on the left, far from its caption.
        var column = new Panel { Dock = DockStyle.Left, Width = 760, BackColor = UiTheme.SurfaceRaised };
        column.Controls.AddRange(
        [
            _invoicePrintIntro, _invoiceFormatLabel, _invoiceFormat,
            _standardOptionsLabel, _standardNationalCode, _standardCoefficient, _standardWords,
            _cotationOptionsLabel, _cotationLinePrice, _cotationZeroFee, _cotationWords, _cotationLetterValue,
            _capHeading, _capIntro, _capPercentLabel, _capPercent, _capAmountLabel, _capAmount
        ]);
        var leftToRight = column.Controls.Cast<Control>().ToDictionary(c => c, c => c.Left);
        column.RightToLeftChanged += (_, _) =>
        {
            var rtl = column.RightToLeft == RightToLeft.Yes;
            column.Dock = rtl ? DockStyle.Right : DockStyle.Left;
            foreach (var (control, left) in leftToRight)
                control.Left = rtl ? column.Width - left - control.Width : left;
        };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised, AutoScroll = true };
        body.Controls.Add(column);

        var card = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised,
                               Padding = new Padding(20, 16, 2, 1) };
        card.Controls.Add(body);
        card.Controls.Add(ActionBar(_invoicePrintStatus, _saveInvoicePrint, _reloadInvoicePrint));
        UiChrome.AsCard(card);

        _invoicePrintTab.Controls.Add(card);

        _saveInvoicePrint.Click += async (_, _) => await SaveInvoicePrintAsync();
        _reloadInvoicePrint.Click += async (_, _) => await LoadInvoicePrintAsync();

        // Read by anyone who may open the screen, changed only with the right to manage settings;
        // the service refuses the others in any case.
        PermissionGate.Require(Permissions.Administration.ManageSettings, _saveInvoicePrint, _invoiceFormat,
                               _standardNationalCode, _standardCoefficient, _standardWords,
                               _cotationLinePrice, _cotationZeroFee, _cotationWords, _cotationLetterValue,
                               _capPercent, _capAmount);
    }

    private void ApplyInvoicePrintTexts()
    {
        _invoicePrintTab.Text = T("InvoicePrint.SettingsTab");
        _invoicePrintIntro.Text = T("InvoicePrint.SettingsIntro");
        _invoiceFormatLabel.Text = T("InvoicePrint.DefaultFormat");
        _standardOptionsLabel.Text = T("InvoicePrint.StandardOptions");
        _standardNationalCode.Text = T("InvoicePrint.OptNationalCode");
        _standardCoefficient.Text = T("InvoicePrint.OptCoefficient");
        _standardWords.Text = T("InvoicePrint.OptWords");
        _cotationOptionsLabel.Text = T("InvoicePrint.CotationOptions");
        _cotationLinePrice.Text = T("InvoicePrint.OptLinePrice");
        _cotationZeroFee.Text = T("InvoicePrint.OptZeroFee");
        _cotationWords.Text = T("InvoicePrint.OptWords");
        _cotationLetterValue.Text = T("InvoicePrint.OptLetterValue");
        _capHeading.Text = T("InvoicePrint.DiscountCap");
        _capIntro.Text = T("InvoicePrint.DiscountCapIntro");
        _capPercentLabel.Text = T("InvoicePrint.DiscountCapPercent");
        _capAmountLabel.Text = T("InvoicePrint.DiscountCapAmount");
        _capAmount.PlaceholderText = T("InvoicePrint.DiscountCapNoAmount");
        _saveInvoicePrint.Text = T("Action.Save");
        _reloadInvoicePrint.Text = T("Action.Refresh");

        var selected = _invoiceFormat.SelectedIndex;
        _invoiceFormat.BeginUpdate();
        _invoiceFormat.Items.Clear();
        _invoiceFormat.Items.Add(new EnumChoice((int)InvoicePrintFormat.Standard, T("InvoicePrint.FormatStandard")));
        _invoiceFormat.Items.Add(new EnumChoice((int)InvoicePrintFormat.CotationB, T("InvoicePrint.FormatCotationB")));
        _invoiceFormat.SelectedIndex = selected >= 0 ? selected : (int)_invoicePrintSettings.DefaultFormat;
        _invoiceFormat.EndUpdate();
    }

    private async Task LoadInvoicePrintAsync(CancellationToken ct = default)
    {
        if (_invoicePrint is null) return;

        var result = await _invoicePrint.GetAsync(ct);
        if (result.IsFailure) return;

        _invoicePrintSettings = result.Value;
        ShowInvoicePrint(_invoicePrintSettings);

        var cap = await _invoicePrint.GetDiscountCapAsync(ct);
        if (cap.IsSuccess)
        {
            _discountCap = cap.Value;
            ShowDiscountCap(_discountCap);
        }

        _invoicePrintStatus.Text = string.Empty;
    }

    private void ShowDiscountCap(DiscountCapSettings cap)
    {
        (_capPercent.Text, _capAmount.Text) = SettingsScreenRules.DiscountCapTexts(cap, System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>The ceiling as typed, or null after telling the operator which box is wrong.</summary>
    private DiscountCapSettings? ReadDiscountCap()
    {
        var (cap, refusal) = SettingsScreenRules.ReadDiscountCap(_capPercent.Text, _capAmount.Text);
        if (refusal is not null)
        {
            _dialogs.Warn(T(refusal.Key), T("InvoicePrint.DiscountCap"));
            (refusal.Field == "cap-amount" ? _capAmount : _capPercent).Focus();
        }

        return cap;
    }

    private void ShowInvoicePrint(InvoicePrintSettings settings)
    {
        _invoiceFormat.SelectedIndex = (int)settings.DefaultFormat;
        _standardNationalCode.Checked = settings.StandardShowNationalActCode;
        _standardCoefficient.Checked = settings.StandardShowCoefficient;
        _standardWords.Checked = settings.StandardShowAmountInWords;
        _cotationLinePrice.Checked = settings.CotationShowLinePrice;
        _cotationZeroFee.Checked = settings.CotationShowCollectionFeeWhenZero;
        _cotationWords.Checked = settings.CotationShowAmountInWords;
        _cotationLetterValue.Checked = settings.CotationShowLetterKeyValue;
    }

    private async Task SaveInvoicePrintAsync()
    {
        if (_invoicePrint is null) return;

        if (ReadDiscountCap() is not { } cap) return;

        var settings = _invoicePrintSettings with
        {
            DefaultFormat = _invoiceFormat.SelectedItem is EnumChoice choice
                ? (InvoicePrintFormat)choice.Value
                : InvoicePrintFormat.Standard,
            StandardShowNationalActCode = _standardNationalCode.Checked,
            StandardShowCoefficient = _standardCoefficient.Checked,
            StandardShowAmountInWords = _standardWords.Checked,
            CotationShowLinePrice = _cotationLinePrice.Checked,
            CotationShowCollectionFeeWhenZero = _cotationZeroFee.Checked,
            CotationShowAmountInWords = _cotationWords.Checked,
            CotationShowLetterKeyValue = _cotationLetterValue.Checked
        };

        var saved = await _invoicePrint.SaveAsync(settings);

        if (saved.IsFailure)
        {
            _dialogs.ShowFailure(saved, T("InvoicePrint.SettingsTab"));
            return;
        }

        _invoicePrintSettings = settings;

        var capSaved = await _invoicePrint.SaveDiscountCapAsync(cap);

        if (capSaved.IsFailure)
        {
            _dialogs.ShowFailure(capSaved, T("InvoicePrint.DiscountCap"));
            return;
        }

        _discountCap = cap;
        ShowDiscountCap(cap);
        _invoicePrintStatus.ForeColor = UiTheme.Normal;
        _invoicePrintStatus.Text = T("InvoicePrint.Saved");
    }
}
