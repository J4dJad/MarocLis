using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The prompts and confirmations the application needs, behind an interface so use-case code never
/// calls <c>MessageBox</c> directly and can be exercised without a message pump.
/// </summary>
public interface IUserDialogs
{
    void Info(string message, string? title = null);

    void Warn(string message, string? title = null);

    void Error(string message, string? title = null);

    /// <summary>Renders a domain failure in the operator's language.</summary>
    void ShowFailure(Result result, string? title = null);

    bool Confirm(string message, string? title = null);

    /// <summary>
    /// Asks for the business justification that the regulation requires for certain operations —
    /// a result revision, a discount, a QC override, a cancellation. Returns null if the operator
    /// backs out, and refuses an empty answer, because "reason: (blank)" in an audit trail is
    /// worse than no field at all.
    /// </summary>
    string? PromptForReason(string prompt, string? title = null, int minimumLength = 5);

    string? PromptForText(string prompt, string? title = null, string? initialValue = null);
}

public sealed class UserDialogs(ILocalizer localizer, ICultureManager cultureManager) : IUserDialogs
{
    private string DefaultTitle => localizer["App.Title"];

    /// <summary>
    /// Views pass a button's text as the title, so it may carry a mnemonic ("&amp;Demander…") that
    /// a caption would show literally. "&amp;&amp;" stays a single ampersand; a trailing ellipsis goes.
    /// </summary>
    internal static string CaptionOf(string? title, string fallback)
    {
        if (string.IsNullOrWhiteSpace(title)) return fallback;
        var caption = title.Replace("&&", "\0").Replace("&", "").Replace("\0", "&").Trim();
        caption = caption.TrimEnd('…').TrimEnd('.').TrimEnd();
        return caption.Length == 0 ? fallback : caption;
    }

    private string Caption(string? title) => CaptionOf(title, DefaultTitle);

    /// <summary>
    /// Message boxes need their reading order and alignment flipped explicitly for Arabic; unlike
    /// a Form, a MessageBox does not inherit the thread's RTL setting.
    /// </summary>
    private MessageBoxOptions Options => cultureManager.IsRightToLeft
        ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign
        : (MessageBoxOptions)0;

    public void Info(string message, string? title = null) =>
        MessageBox.Show(message, Caption(title), MessageBoxButtons.OK,
                        MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Options);

    public void Warn(string message, string? title = null) =>
        MessageBox.Show(message, Caption(title), MessageBoxButtons.OK,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, Options);

    public void Error(string message, string? title = null) =>
        MessageBox.Show(message, Caption(title), MessageBoxButtons.OK,
                        MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Options);

    public void ShowFailure(Result result, string? title = null)
    {
        if (result.IsSuccess) return;

        var message = localizer.Describe(result.Errors);

        // A regulatory refusal is not an error the operator did wrong — it is the system declining
        // to let them do something the law forbids. Presented as a warning, not a failure.
        var regulatory = result.Errors.Any(e => e.Code is "regulatory" or "forbidden");

        if (regulatory) Warn(message, title);
        else Error(message, title);
    }

    public bool Confirm(string message, string? title = null) =>
        MessageBox.Show(message, Caption(title), MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        // Default to "No": these confirmations guard cancellations and deletions,
                        // and a reflexive Enter should not be the one that goes through.
                        MessageBoxDefaultButton.Button2, Options) == DialogResult.Yes;

    public string? PromptForReason(string prompt, string? title = null, int minimumLength = 5)
    {
        while (true)
        {
            var answer = PromptForText(prompt, title ?? localizer["Message.ReasonRequiredTitle"]);

            if (answer is null) return null;   // The operator backed out.

            if (answer.Trim().Length >= minimumLength) return answer.Trim();

            Warn(localizer["Message.ReasonRequiredPrompt"],
                 localizer["Message.ReasonRequiredTitle"]);
        }
    }

    public string? PromptForText(string prompt, string? title = null, string? initialValue = null)
    {
        using var dialog = new TextPromptForm(prompt, Caption(title), initialValue,
                                              cultureManager.IsRightToLeft,
                                              localizer["Action.Ok"], localizer["Action.Cancel"]);

        return dialog.ShowDialog() == DialogResult.OK ? dialog.Value : null;
    }
}

/// <summary>
/// A small modal text prompt. Written by hand because WinForms ships no equivalent, and pulling in
/// VisualBasic's <c>InputBox</c> brings a whole assembly and cannot be localised or mirrored.
/// </summary>
internal sealed class TextPromptForm : Form
{
    private readonly TextBox _input;

    internal string Value => _input.Text;

    internal TextPromptForm(string prompt, string title, string? initialValue, bool rightToLeft,
                            string okText, string cancelText)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 168);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;
        RightToLeft = rightToLeft ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = rightToLeft;

        var label = new Label
        {
            Text = prompt,
            AutoSize = false,
            Bounds = new Rectangle(16, 14, 428, 40),
            ForeColor = UiTheme.TextPrimary
        };

        _input = new TextBox
        {
            Bounds = new Rectangle(16, 60, 428, 56),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Text = initialValue ?? string.Empty,
            Font = UiTheme.Body
        };

        var ok = UiTheme.PrimaryButton(okText);
        ok.Bounds = new Rectangle(268, 126, 84, UiTheme.ButtonHeight);
        ok.DialogResult = DialogResult.OK;

        var cancel = UiTheme.SecondaryButton(cancelText);
        cancel.Bounds = new Rectangle(360, 126, 84, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([label, _input, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        // Ctrl+Enter accepts, so a multi-line justification can contain newlines without the
        // first Enter submitting a one-word reason.
        _input.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                DialogResult = DialogResult.OK;
                e.Handled = true;
            }
        };
    }
}
