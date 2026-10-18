using System.Globalization;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Licensing;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// « Nombre de postes atteint (n / n) »: shown to the workstation one too many, with the
/// workstations signed in, so whoever is at the counter can ask a colleague to sign out — or see
/// which PC was left on — rather than call the vendor.
/// </summary>
public sealed class WorkstationLimitForm : LocalizedForm
{
    private readonly WorkstationClaim _claim;

    private readonly Label _heading = new();
    private readonly Label _explanation = new();
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _close = UiTheme.PrimaryButton(string.Empty);

    public WorkstationLimitForm(WorkstationClaim claim, ILocalizer localizer, ICultureManager cultureManager)
        : base(localizer, cultureManager)
    {
        _claim = claim;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 380);
        BackColor = UiTheme.SurfaceRaised;

        _heading.Font = UiTheme.SectionTitle;
        _heading.ForeColor = UiTheme.CriticalInk;
        _heading.AutoSize = false;
        _heading.Bounds = new Rectangle(20, 18, 600, 26);

        _explanation.Font = UiTheme.Body;
        _explanation.ForeColor = UiTheme.TextSecondary;
        _explanation.AutoSize = false;
        _explanation.Bounds = new Rectangle(20, 50, 600, 58);

        void Add(string name, int weight, Font? font = null) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("machine", 30, UiTheme.Monospace);
        Add("user", 30);
        Add("since", 20, UiTheme.Monospace);
        Add("seen", 20, UiTheme.Monospace);

        _grid.Dock = DockStyle.None;
        _grid.Bounds = new Rectangle(20, 114, 600, 196);

        _close.Bounds = new Rectangle(500, 326, 120, 34);
        _close.Click += (_, _) => Close();
        AcceptButton = _close;
        CancelButton = _close;

        Controls.AddRange([_heading, _explanation, _grid, _close]);

        foreach (var workstation in claim.Active)
            _grid.Rows.Add(workstation.MachineName, workstation.UserName ?? "—",
                           Time(workstation.Since), Time(workstation.LastSeenAt));
    }

    private static string Time(DateTimeOffset at) =>
        at.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    protected override void ApplyTexts()
    {
        Text = T("Licence.Workstations.Title");
        _heading.Text = T("Licence.Workstations.Reached", _claim.InUse, _claim.Limit);
        _explanation.Text = T("Licence.Workstations.Explanation", Workstation.SilenceTimeout.TotalMinutes);
        _grid.Columns["machine"]!.HeaderText = T("Licence.Workstations.Machine");
        _grid.Columns["user"]!.HeaderText = T("Licence.Workstations.User");
        _grid.Columns["since"]!.HeaderText = T("Licence.Workstations.Since");
        _grid.Columns["seen"]!.HeaderText = T("Licence.Workstations.LastSeen");
        _close.Text = T("Action.Close");
    }
}
