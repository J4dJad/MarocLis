using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Chooses the Westgard rules in force for one control.
/// <para>
/// Each rule carries its own description from the catalogue — the same string the evaluator quotes
/// when the rule fires — so the person choosing a strategy reads exactly what the bench will read
/// when it rejects a run. Nothing here is abbreviated to a code the reader has to already know.
/// </para>
/// <para>
/// The two shortcut buttons exist because these are the two real answers in practice: the classic
/// multirule set for a routine chemistry or haematology control, and 1₃ₛ alone for a method whose
/// imprecision makes the tighter rules fire on noise. A laboratory that wants something else ticks
/// it, but it should not have to assemble the common cases by hand.
/// </para>
/// </summary>
internal sealed class WestgardRuleDialog : Form
{
    /// <summary>
    /// The Westgard multirule set: warning rule plus the rejection rules that detect random and
    /// systematic error. What most laboratories run on a two-level control.
    /// </summary>
    private const WestgardRule ClassicMultirule =
        WestgardRule.OneTwoS | WestgardRule.OneThreeS | WestgardRule.TwoTwoS |
        WestgardRule.R4S | WestgardRule.FourOneS | WestgardRule.TenX;

    private readonly CheckedListBox _list = new();
    private readonly Label _summary = new();
    private readonly ILocalizer _localizer;

    private readonly (WestgardRule Rule, string LabelKey, string DescriptionKey)[] _rules =
    [
        (WestgardRule.OneTwoS,         "Qc.Rule_1_2s",    "Qc_Rule_1_2s"),
        (WestgardRule.OneThreeS,       "Qc.Rule_1_3s",    "Qc_Rule_1_3s"),
        (WestgardRule.TwoTwoS,         "Qc.Rule_2_2s",    "Qc_Rule_2_2s"),
        (WestgardRule.R4S,             "Qc.Rule_R_4s",    "Qc_Rule_R_4s"),
        (WestgardRule.ThreeOneS,       "Qc.Rule_3_1s",    "Qc_Rule_3_1s"),
        (WestgardRule.FourOneS,        "Qc.Rule_4_1s",    "Qc_Rule_4_1s"),
        (WestgardRule.TwoOfThreeTwoS,  "Qc.Rule_2of3_2s", "Qc_Rule_2of3_2s"),
        (WestgardRule.SevenT,          "Qc.Rule_7T",      "Qc_Rule_7T"),
        (WestgardRule.EightX,          "Qc.Rule_8x",      "Qc_Rule_8x"),
        (WestgardRule.TenX,            "Qc.Rule_10x",     "Qc_Rule_10x"),
        (WestgardRule.TwelveX,         "Qc.Rule_12x",     "Qc_Rule_12x")
    ];

    internal WestgardRule Rules { get; private set; }

    internal WestgardRuleDialog(ILocalizer localizer, string parameterCode, string lotNumber,
                                WestgardRule current)
    {
        _localizer = localizer;
        Rules = current;

        Text = $"{localizer["Quality.EditRules"]} — {parameterCode} · {lotNumber}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 470);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var heading = new Label
        {
            Text = localizer["Quality.RuleSelectionExplanation"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 14, 580, 44)
        };

        _list.Bounds = new Rectangle(20, 64, 580, 210);
        _list.CheckOnClick = true;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Font = UiTheme.Body;

        foreach (var (rule, labelKey, _) in _rules)
        {
            var index = _list.Items.Add(localizer[labelKey]);
            _list.SetItemChecked(index, current.HasFlag(rule));
        }

        _list.SelectedIndexChanged += (_, _) => ShowDescription();
        _list.ItemCheck += (_, _) => BeginInvoke(ShowDescription);

        _summary.Bounds = new Rectangle(20, 282, 580, 64);
        _summary.Font = UiTheme.Small;
        _summary.ForeColor = UiTheme.TextSecondary;
        _summary.AutoSize = false;

        var classic = UiTheme.SecondaryButton(localizer["Quality.ClassicMultirule"]);
        classic.Bounds = new Rectangle(20, 356, 350, UiTheme.ButtonHeight);
        classic.Click += (_, _) => Apply(ClassicMultirule);

        var minimal = UiTheme.SecondaryButton(localizer["Quality.SingleRuleOnly"]);
        minimal.Bounds = new Rectangle(380, 356, 220, UiTheme.ButtonHeight);
        minimal.Click += (_, _) => Apply(WestgardRule.OneThreeS);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 420, 280, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(310, 420, 290, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([heading, _list, _summary, classic, minimal, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        // The explanation above the list is the dialog's sentence: no second one under the title.
        QhDialog.Dress(this, localizer, null);

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        ShowDescription();
    }

    private void Apply(WestgardRule preset)
    {
        for (var i = 0; i < _rules.Length; i++)
            _list.SetItemChecked(i, preset.HasFlag(_rules[i].Rule));

        ShowDescription();
    }

    /// <summary>
    /// Shows the selected rule's own description, plus a warning when nothing is ticked — a
    /// control with no rules records points and never rejects a run, which looks like a control
    /// that is passing.
    /// </summary>
    private void ShowDescription()
    {
        var lines = new List<string>(2);

        if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _rules.Length)
            lines.Add(_localizer[_rules[_list.SelectedIndex].DescriptionKey]);

        var count = _list.CheckedIndices.Count;

        if (count == 0)
        {
            _summary.ForeColor = UiTheme.Critical;
            lines.Add(_localizer["Quality.NoRulesWarning"]);
        }
        else
        {
            _summary.ForeColor = UiTheme.TextSecondary;
            lines.Add(_localizer.Format("Quality.RuleCount", count));
        }

        _summary.Text = string.Join(Environment.NewLine, lines);
    }

    private void Accept()
    {
        var selected = WestgardRule.None;

        foreach (var index in _list.CheckedIndices.Cast<int>())
            selected |= _rules[index].Rule;

        Rules = selected;
        DialogResult = DialogResult.OK;
        Close();
    }
}
