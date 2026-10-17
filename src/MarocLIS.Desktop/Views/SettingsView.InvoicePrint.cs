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
            box.Bounds = new Rectangle(x, y, 740, 24);
            return box;
        }

        Text(_invoicePrintIntro, UiTheme.Small, UiTheme.TextSecondary, new Rectangle(0, 0, 760, 40));

        Text(_invoiceFormatLabel, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 50, 760, 22));
        _invoiceFormat.DropDownStyle = ComboBoxStyle.DropDownList;
        _invoiceFormat.FlatStyle = FlatStyle.Flat;
        _invoiceFormat.Bounds = new Rectangle(0, 74, 520, UiTheme.InputHeight);

        Text(_standardOptionsLabel, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 124, 760, 22));
        Option(_standardNationalCode, 0, 150);
        Option(_standardCoefficient, 0, 176);
        Option(_standardWords, 0, 202);

        Text(_cotationOptionsLabel, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 242, 760, 22));
        Option(_cotationLinePrice, 0, 268);
        Option(_cotationZeroFee, 0, 294);
        Option(_cotationWords, 0, 320);
        Option(_cotationLetterValue, 0, 346);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised, AutoScroll = true };
        body.Controls.AddRange(
        [
            _invoicePrintIntro, _invoiceFormatLabel, _invoiceFormat,
            _standardOptionsLabel, _standardNationalCode, _standardCoefficient, _standardWords,
            _cotationOptionsLabel, _cotationLinePrice, _cotationZeroFee, _cotationWords, _cotationLetterValue
        ]);

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
                               _cotationLinePrice, _cotationZeroFee, _cotationWords, _cotationLetterValue);
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
        _invoicePrintStatus.Text = string.Empty;
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
        _invoicePrintStatus.ForeColor = UiTheme.Normal;
        _invoicePrintStatus.Text = T("InvoicePrint.Saved");
    }
}
