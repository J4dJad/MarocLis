using MarocLIS.Desktop.Infrastructure;
using MarocLIS.LicenseGenerator.Core;

namespace MarocLIS.LicenseGenerator.Ui;

/// <summary>Asks for a passphrase; twice when one is being chosen, so a typing slip does not lose the key.</summary>
internal sealed class PassphraseForm : Form
{
    private readonly TextBox _first = new() { UseSystemPasswordChar = true, Width = 360, Font = Look.Body };
    private readonly TextBox _second = new() { UseSystemPasswordChar = true, Width = 360, Font = Look.Body };
    private readonly Label _problem = Look.Text(string.Empty, Look.Small, UiTheme.Critical);
    private readonly bool _choosing;

    public string Passphrase => _first.Text;

    public PassphraseForm(string title, string explanation, bool choosing)
    {
        _choosing = choosing;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = UiTheme.SurfaceRaised;
        Font = Look.Body;

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(20)
        };

        var intro = Look.Text(explanation);
        intro.MaximumSize = new Size(380, 0);
        layout.Controls.Add(intro);
        layout.Controls.Add(Look.Text(choosing ? "Phrase de passe (12 caractères au moins)" : "Phrase de passe", Look.Bold));
        layout.Controls.Add(_first);
        if (choosing)
        {
            layout.Controls.Add(Look.Text("Retapez-la", Look.Bold));
            layout.Controls.Add(_second);
        }

        layout.Controls.Add(_problem);

        var ok = Look.Primary(choosing ? "Valider" : "Déverrouiller", choosing ? Glyph.Check : Glyph.LockOpen);
        var cancel = Look.Secondary("Annuler", Glyph.X);
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (_, _) => Accept();
        layout.Controls.Add(Look.Bar(ok, cancel));

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Accept()
    {
        if (_choosing)
        {
            try
            {
                KeyVault.CheckPassphrase(_first.Text);
            }
            catch (ArgumentException ex)
            {
                _problem.Text = ex.Message.Split(" (", 2)[0];
                return;
            }

            if (_first.Text != _second.Text)
            {
                _problem.Text = "Les deux saisies diffèrent.";
                return;
            }
        }
        else if (_first.Text.Length == 0)
        {
            return;
        }

        DialogResult = DialogResult.OK;
    }
}
