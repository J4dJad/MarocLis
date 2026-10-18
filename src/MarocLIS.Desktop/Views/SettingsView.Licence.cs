using System.Globalization;
using MarocLIS.Application.Licensing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The « Licence » tab: the licence in force as MarocLIS reads it — who it is for, until when, on
/// how many workstations and with which modules — the server's identifier, and the import of the
/// next licence. With its own right (<see cref="Permissions.Administration.License"/>), and open in
/// the read-only mode, when every other tab refuses its changes: it is how the laboratory leaves it.
/// </summary>
public sealed partial class SettingsView
{
    private readonly LicenceService? _licence;
    private readonly WorkstationPresenceService? _presence;
    private readonly IServiceScopeFactory? _scopes;

    private readonly TabPage _licenceTab = new();
    private readonly Label _licenceHeading = new();
    private readonly Label _licenceState = new();
    private readonly Label _licenceProblem = new();
    private readonly Label _licenceKeyWarning = new();
    private readonly Dictionary<string, (Label Caption, Label Value)> _licenceRows = new(StringComparer.Ordinal);
    private readonly Button _copyServerId = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _licenceHistoryHeading = new();
    private readonly DataGridView _licenceHistory = UiTheme.Grid();
    private readonly Button _importLicence = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _exportRequest = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _reloadLicence = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _licenceStatus = new();

    // The rows of the summary, in their order on the tab.
    private static readonly string[] LicenceRowKeys =
    [
        "laboratory", "number", "kind", "period", "remaining", "updates", "workstations", "modules",
        "closed", "server", "binding", "key"
    ];

    private const int LicenceColumnWidth = 760;
    private const int LicenceCaptionWidth = 200;

    private void BuildLicenceTab()
    {
        _licenceTab.BackColor = UiTheme.Surface;

        _licenceHeading.Font = UiTheme.BodyBold;
        _licenceHeading.ForeColor = UiTheme.TextPrimary;
        _licenceHeading.Bounds = new Rectangle(0, 0, 400, 22);

        // The state, as a pill at the end of the heading's line: « Valide », « Expirée »…
        _licenceState.Font = UiTheme.SmallBold;
        _licenceState.AutoSize = false;
        _licenceState.TextAlign = ContentAlignment.MiddleCenter;
        _licenceState.Bounds = new Rectangle(LicenceColumnWidth - 220, 0, 220, 22);

        _licenceProblem.Font = UiTheme.SmallBold;
        _licenceProblem.ForeColor = UiTheme.CriticalInk;
        _licenceProblem.BackColor = UiTheme.CriticalBackground;
        _licenceProblem.Padding = new Padding(8, 4, 8, 4);
        _licenceProblem.Bounds = new Rectangle(0, 30, LicenceColumnWidth, 42);

        var column = new Panel { Dock = DockStyle.Left, Width = LicenceColumnWidth, BackColor = UiTheme.SurfaceRaised };
        column.Controls.AddRange([_licenceHeading, _licenceState, _licenceProblem]);

        var y = 82;
        foreach (var key in LicenceRowKeys)
        {
            var tall = key is "modules" or "closed";
            var caption = new Label
            {
                Font = UiTheme.SmallBold, ForeColor = UiTheme.TextSecondary, AutoSize = false,
                Bounds = new Rectangle(0, y + 3, LicenceCaptionWidth - 8, 20)
            };
            var value = new Label
            {
                Font = UiTheme.Body, ForeColor = UiTheme.TextPrimary, AutoSize = false,
                Bounds = new Rectangle(LicenceCaptionWidth, y, LicenceColumnWidth - LicenceCaptionWidth, tall ? 60 : 24)
            };

            if (key is "number" or "server" or "key") value.Font = UiTheme.MonospaceBold;
            if (key == "server")
            {
                value.Width = 260;
                value.Tag = UiTags.LatinValue;
                _copyServerId.Bounds = new Rectangle(LicenceCaptionWidth + 268, y - 4, 110, 30);
                ButtonIcons.Set(_copyServerId, Glyph.CopyPlus);
                column.Controls.Add(_copyServerId);
            }

            _licenceRows[key] = (caption, value);
            column.Controls.AddRange([caption, value]);
            // Thirteen module names take three lines.
            y += tall ? 64 : 28;
        }

        _licenceKeyWarning.Font = UiTheme.SmallBold;
        _licenceKeyWarning.ForeColor = UiTheme.CriticalInk;
        _licenceKeyWarning.BackColor = UiTheme.CriticalBackground;
        _licenceKeyWarning.Padding = new Padding(8, 4, 8, 4);
        _licenceKeyWarning.Bounds = new Rectangle(0, y + 4, LicenceColumnWidth, 42);
        y += 56;

        _licenceHistoryHeading.Font = UiTheme.BodyBold;
        _licenceHistoryHeading.ForeColor = UiTheme.TextPrimary;
        _licenceHistoryHeading.Bounds = new Rectangle(0, y, 400, 22);

        void Add(string name, int weight, Font? font = null) =>
            _licenceHistory.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("number", 22, UiTheme.Monospace);
        Add("kind", 14);
        Add("imported", 20, UiTheme.Monospace);
        Add("by", 22);
        Add("replaced", 20, UiTheme.Monospace);
        _licenceHistory.Dock = DockStyle.None;
        _licenceHistory.Bounds = new Rectangle(0, y + 28, LicenceColumnWidth, 120);

        column.Height = y + 160;
        column.Controls.AddRange([_licenceKeyWarning, _licenceHistoryHeading, _licenceHistory]);

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
        card.Controls.Add(ActionBar(_licenceStatus, _importLicence, _exportRequest, _reloadLicence));
        UiChrome.AsCard(card);

        _licenceTab.Controls.Add(card);

        _copyServerId.Click += (_, _) =>
            LicenceFileActions.CopyServerId(_licence?.Current.ServerId, _dialogs, Localizer);
        _importLicence.Click += async (_, _) => await ImportLicenceAsync();
        _exportRequest.Click += async (_, _) =>
        {
            if (_scopes is not null)
                await LicenceFileActions.ExportRequestAsync(FindForm()!, _scopes, _dialogs, Localizer, _exportRequest.Text);
        };
        _reloadLicence.Click += async (_, _) => await LoadLicenceAsync(refresh: true);

        PermissionGate.Require(Permissions.Administration.License, _importLicence, _exportRequest);
    }

    /// <summary>Opens the screen on this tab: the banner's link to it.</summary>
    public void ShowLicenceTab()
    {
        if (_tabs.TabPages.Contains(_licenceTab)) _tabs.SelectedTab = _licenceTab;
    }

    private void ApplyLicenceTexts()
    {
        _licenceTab.Text = T("Licence.Tab");
        _licenceHeading.Text = T("Licence.Heading");
        _licenceHistoryHeading.Text = T("Licence.HistoryHeading");
        _licenceKeyWarning.Text = T("Licence.DevelopmentKey");

        foreach (var key in LicenceRowKeys)
            _licenceRows[key].Caption.Text = T($"Licence.Row.{key}");

        _licenceHistory.Columns["number"]!.HeaderText = T("Licence.Row.number");
        _licenceHistory.Columns["kind"]!.HeaderText = T("Licence.Row.kind");
        _licenceHistory.Columns["imported"]!.HeaderText = T("Licence.History.Imported");
        _licenceHistory.Columns["by"]!.HeaderText = T("Licence.History.By");
        _licenceHistory.Columns["replaced"]!.HeaderText = T("Licence.History.Replaced");

        _copyServerId.Text = T("Licence.CopyServerId");
        _importLicence.Text = T("Licence.Import");
        _exportRequest.Text = T("Licence.ExportRequest");
        _reloadLicence.Text = T("Action.Refresh");

        if (_licence is not null) ShowLicence(_licence.Current, _workstationsInUse, _licenceHistoryRows);
    }

    private int? _workstationsInUse;
    private IReadOnlyList<LicenceHistoryRow> _licenceHistoryRows = [];

    private async Task LoadLicenceAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (_licence is null || !_tabs.TabPages.Contains(_licenceTab)) return;

        var status = refresh ? await _licence.RefreshAsync(ct) : _licence.Current;

        _workstationsInUse = _presence is null ? null : (await _presence.ActiveAsync(ct)).Count;
        _licenceHistoryRows = await _licence.GetHistoryAsync(ct);

        ShowLicence(status, _workstationsInUse, _licenceHistoryRows);
        _licenceStatus.Text = string.Empty;
    }

    /// <summary>A cause written to follow « Lecture seule — », as a sentence of its own.</summary>
    private static string Sentence(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentUICulture) + text[1..] + (char.IsPunctuation(text[^1]) ? "" : ".");

    private void ShowLicence(LicenceStatus status, int? inUse, IReadOnlyList<LicenceHistoryRow> history)
    {
        var terms = status.Terms;
        var culture = CultureInfo.CurrentUICulture;
        string Date(DateOnly? date) => date?.ToString("d", culture) ?? "—";

        var (pillBack, pillInk) = status.State switch
        {
            LicenceState.Valid when status.IsExpiringSoon => (UiTheme.AbnormalBackground, UiTheme.Abnormal),
            LicenceState.Valid => (UiTheme.NormalBackground, UiTheme.Normal),
            _ => (UiTheme.CriticalBackground, UiTheme.CriticalInk)
        };
        _licenceState.Text = status.IsReadOnly && status.State != LicenceState.Pending
            ? $"{T($"LicenceState.{status.State}")} · {T("Licence.ReadOnly")}"
            : T($"LicenceState.{status.State}");
        _licenceState.BackColor = pillBack;
        _licenceState.ForeColor = pillInk;

        // Why the licence is not in force, in the words of the refusal; and a clock found behind.
        var problems = new List<string>();
        if (status.Problem is { } problem) problems.Add(Localizer.Describe(problem));
        else if (status.State == LicenceState.Missing) problems.Add(Sentence(T("Licence.Cause.Missing")));
        if (status.ClockRolledBack) problems.Add(T("Licence.ClockRolledBack", Date(status.LatestDateSeen)));
        if (status.IsReadOnly && status.BenchClearingUntil is { } bench && status.State != LicenceState.Pending)
            problems.Add(status.InBenchClearing ? T("Licence.BenchUntil", Date(bench)) : T("Licence.BenchOver"));
        _licenceProblem.Text = string.Join(" ", problems);
        _licenceProblem.Visible = problems.Count > 0;

        void Row(string key, string text, Color? colour = null)
        {
            var value = _licenceRows[key].Value;
            value.Text = text;
            value.ForeColor = colour ?? UiTheme.TextPrimary;
        }

        Row("laboratory", terms is null ? "—" : string.Join(" — ", new[] { terms.Laboratory, terms.Site, terms.Authorisation }
            .Where(s => !string.IsNullOrWhiteSpace(s))));
        Row("number", terms is null ? "—" : T("Licence.NumberVersion", terms.Number, terms.Version));
        Row("kind", terms is null ? "—" : T($"LicenceKind.{terms.Kind}"));
        Row("period", terms is null ? "—"
            : terms.EndsOn is { } end ? T("Licence.Period", Date(terms.StartsOn), Date(end))
            : T("Licence.PeriodOpen", Date(terms.StartsOn)));

        var level = LicenceNavigation.DaysLevel(status) switch
        {
            LicenceBannerLevel.Normal => UiTheme.Normal,
            LicenceBannerLevel.Warning => UiTheme.Abnormal,
            _ => UiTheme.Critical
        };
        Row("remaining", status.State switch
        {
            LicenceState.Valid when status.DaysRemaining is { } days => T("Licence.DaysRemaining", days),
            LicenceState.Valid => T("Licence.NoEnd"),
            LicenceState.Grace => T("Licence.GraceRemaining", status.GraceDaysRemaining),
            LicenceState.Expired => T("Licence.ExpiredOn", Date(terms?.EndsOn)),
            _ => "—"
        }, level);
        Row("updates", terms?.UpdatesUntil is { } updates ? Date(updates) : "—");

        var limit = status.WorkstationLimit ?? terms?.Workstations;
        Row("workstations", limit is null ? "—" : $"{inUse?.ToString(culture) ?? "?"} / {limit}");

        var open = terms is null ? [] : LicenceModules.All.Where(terms.HasModule).ToList();
        var closed = LicenceModules.All.Except(open).ToList();
        Row("modules", string.Join(", ", new[] { T("Licence.Base") }
            .Concat(open.Select(m => T(LicenceModules.NameKey(m))))));
        Row("closed", closed.Count == 0 ? "—" : string.Join(", ", closed.Select(m => T(LicenceModules.NameKey(m)))),
            UiTheme.TextSecondary);

        Row("server", status.ServerId ?? "—");
        Row("binding", terms is null ? "—"
            : terms.ServerId is null ? T("Licence.Unbound")
            : string.Equals(terms.ServerId, status.ServerId, StringComparison.Ordinal) ? T("Licence.BoundHere")
            : T("Licence.BoundElsewhere", terms.ServerId), terms?.ServerId is { } bound
              && !string.Equals(bound, status.ServerId, StringComparison.Ordinal) ? UiTheme.Critical : null);
        Row("key", _licence?.KeyFingerprint ?? "—");
        _licenceKeyWarning.Visible = _licence?.IsDevelopmentKey == true;

        _licenceHistory.Rows.Clear();
        foreach (var row in history)
            _licenceHistory.Rows.Add(T("Licence.NumberVersion", row.Number, row.Version), T($"LicenceKind.{row.Kind}"),
                                     row.ImportedAt.ToLocalTime().ToString("g", culture), row.ImportedBy ?? "—",
                                     row.ReplacedAt?.ToLocalTime().ToString("g", culture) ?? "—");
    }

    private async Task ImportLicenceAsync()
    {
        if (_scopes is null) return;

        var status = await LicenceFileActions.ImportAsync(FindForm()!, _scopes, _dialogs, Localizer, _importLicence.Text);
        if (status is null) return;

        // In force at once: the shell redraws its menu and banner from the same evaluation.
        await LoadLicenceAsync();
        _licenceStatus.ForeColor = UiTheme.Normal;
        _licenceStatus.Text = T("Licence.ImportedShort");
    }
}
