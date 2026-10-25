using System.Globalization;
using MarocLIS.Application.Catalog;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The two catalogue tabs built on the national nomenclature of biology acts (NABM):
/// « Nomenclature des actes », the official list read-only, and « Écarts avec la nomenclature »,
/// the analyses whose act code is missing or unknown or whose tariff departs from the official
/// coefficient, with the official coefficient applied as a new tariff version.
/// <para>
/// Kept in their own file so the catalogue screen's other tabs stay as they were. Both show the
/// laboratory's internal code beside the national one: the nomenclature adds a code, it never
/// replaces the one the laboratory created.
/// </para>
/// </summary>
public sealed partial class CatalogView
{
    private const int ActsTab = 5;
    private const int GapsTab = 6;

    // « Nomenclature des actes »
    private readonly TextBox _actSearch = new();
    private readonly DataGridView _actGrid = UiTheme.Grid();
    private readonly Label _actHeading = new();
    private readonly Label _actNote = new();
    private readonly ChipButton[] _decreeChips =
        [new() { Value = null }, new() { Value = "2005" }, new() { Value = "2012" }, new() { Value = "2016" }];
    private int? _decreeFilter;

    // « Écarts avec la nomenclature »
    private readonly DataGridView _gapGrid = UiTheme.Grid();
    private readonly Label _gapHeading = new();
    private readonly Label _gapNote = new();
    private readonly Button _applyOfficial = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editGapAnalysis = UiTheme.SecondaryButton(string.Empty);
    private readonly ChipButton[] _gapChips =
    [
        new() { Value = null },
        new() { Value = nameof(NomenclatureGapKind.MissingCode) },
        new() { Value = nameof(NomenclatureGapKind.UnknownCode) },
        new() { Value = nameof(NomenclatureGapKind.CoefficientDiffers) }
    ];
    private NomenclatureGapKind? _gapFilter;

    /// <summary>The acts, read once per activation: the editor picks from them, the tab lists them.</summary>
    private IReadOnlyList<NationalActRow> _acts = [];
    private IReadOnlyList<NomenclatureGapRow> _gapRows = [];

    private NomenclatureGapRow? SelectedGap => _gapGrid.CurrentRow?.Tag as NomenclatureGapRow;

    // ---- Layout ------------------------------------------------------------------------------------

    private TabPage BuildActsTab()
    {
        var page = new TabPage { BackColor = UiTheme.Surface };

        foreach (var chip in _decreeChips)
        {
            chip.Margin = new Padding(6, 0, 0, 0);
            chip.Anchor = AnchorStyles.None;
            chip.Click += (_, _) => ChooseDecree(chip.Value is { } year ? int.Parse(year, CultureInfo.InvariantCulture) : null);
        }
        _decreeChips[0].Margin = new Padding(12, 0, 0, 0);
        _decreeChips[0].Selected = true;

        var bar = AdminLayout.Row(AdminLayout.RowHeight, [new AdminSearchBox(_actSearch, 300), .. _decreeChips], []);

        AddColumns(_actGrid,
            ("code", 8, UiTheme.MonospaceBold, false),
            ("label", 40, null, false),
            ("coefficient", 9, UiTheme.Monospace, true),
            ("chapter", 28, UiTheme.Small, false),
            ("decree", 7, UiTheme.Monospace, false),
            ("used", 8, UiTheme.Monospace, true));

        page.Controls.Add(Below(Section(_actHeading, _actGrid, [], null)));
        page.Controls.Add(AdminLayout.Note(_actNote, 30));
        page.Controls.Add(bar);

        _actSearch.TextChanged += (_, _) => RenderActs();
        return page;
    }

    private TabPage BuildGapsTab()
    {
        var page = new TabPage { BackColor = UiTheme.Surface };

        foreach (var chip in _gapChips)
        {
            chip.Margin = new Padding(6, 0, 0, 0);
            chip.Anchor = AnchorStyles.None;
            chip.Click += (_, _) => ChooseGapKind(chip.Value is { } kind ? Enum.Parse<NomenclatureGapKind>(kind) : null);
        }
        _gapChips[0].Margin = Padding.Empty;
        _gapChips[0].Selected = true;

        var bar = AdminLayout.Row(AdminLayout.RowHeight, _gapChips, []);

        AddColumns(_gapGrid,
            ("code", 10, UiTheme.MonospaceBold, false),
            ("name", 24, null, false),
            ("act", 8, UiTheme.MonospaceBold, false),
            ("label", 22, UiTheme.Small, false),
            ("official", 9, UiTheme.Monospace, true),
            ("current", 9, UiTheme.Monospace, true),
            ("gap", 18, UiTheme.SmallBold, false));

        AdminLayout.PillColumn(_gapGrid, "gap", row => row.Tag is NomenclatureGapRow gap
            ? CatalogScreenRules.Tone(gap) switch
            {
                CatalogTone.Abnormal => AdminLayout.Warn,
                CatalogTone.Critical => AdminLayout.Crit,
                _ => AdminLayout.Wait
            }
            : null);

        page.Controls.Add(Below(Section(_gapHeading, _gapGrid, [_editGapAnalysis], _applyOfficial)));
        page.Controls.Add(AdminLayout.Note(_gapNote, 40));
        page.Controls.Add(bar);

        _gapGrid.SelectionChanged += (_, _) => RefreshNationalActActions();
        // The selection can change before the current row does (keyboard, programmatic moves): the
        // buttons follow the current row, so they are refreshed when it settles too.
        _gapGrid.CurrentCellChanged += (_, _) => RefreshNationalActActions();
        _gapGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && _editGapAnalysis.Enabled) await EditGapAnalysisAsync();
        };
        _applyOfficial.Click += async (_, _) => await ApplyOfficialCoefficientAsync();
        _editGapAnalysis.Click += async (_, _) => await EditGapAnalysisAsync();

        return page;
    }

    private void ApplyNationalActTexts()
    {
        if (_tabs.TabPages.Count > GapsTab)
        {
            _tabs.TabPages[ActsTab].Text = T("Nabm.ActsTab");
            _tabs.TabPages[GapsTab].Text = T("Nabm.GapsTab");
        }

        _actSearch.PlaceholderText = T("Nabm.SearchPlaceholder");
        _actNote.Text = T("Nabm.ActsNote");
        _gapNote.Text = T("Nabm.GapsNote");
        _applyOfficial.Text = T("Nabm.ApplyOfficial");
        _editGapAnalysis.Text = T("Catalog.EditAnalysis");

        void Header(DataGridView grid, string column, string key)
        {
            if (grid.Columns.Contains(column)) grid.Column(column).HeaderText = T(key);
        }

        Header(_actGrid, "code", "Nabm.ActCode");
        Header(_actGrid, "label", "Nabm.ActLabel");
        Header(_actGrid, "coefficient", "Billing.Coefficient");
        Header(_actGrid, "chapter", "Nabm.Chapter");
        Header(_actGrid, "decree", "Nabm.Decree");
        Header(_actGrid, "used", "Catalog.UsedBy");

        Header(_gapGrid, "code", "Nabm.InternalCode");
        Header(_gapGrid, "name", "Field.Name");
        Header(_gapGrid, "act", "Nabm.ActCode");
        Header(_gapGrid, "label", "Nabm.ActLabel");
        Header(_gapGrid, "official", "Nabm.OfficialCoefficient");
        Header(_gapGrid, "current", "Nabm.CurrentCoefficient");
        Header(_gapGrid, "gap", "Nabm.Gap");

        if (_acts.Count > 0) RenderActs();
        if (_gapRows.Count > 0) RenderGaps();
        else RefreshGapChips();
    }

    // ---- Nomenclature des actes ----------------------------------------------------------------

    /// <summary>Reads the acts once per activation; a failure leaves the editor without the picker, not the screen without a catalogue.</summary>
    private async Task LoadNationalActsAsync(CancellationToken ct = default)
    {
        var result = await _nationalActs.GetActsAsync(null, ct);
        if (result.IsFailure) return;

        _acts = result.Value;
        RenderActs();
    }

    private void ChooseDecree(int? year)
    {
        _decreeFilter = year;
        RenderActs();
    }

    private void RenderActs()
    {
        var selected = (_actGrid.CurrentRow?.Tag as NationalActRow)?.Id;
        var (matching, shown) = CatalogScreenRules.Acts(_acts, _actSearch.Text, _decreeFilter);

        _actGrid.SuspendLayout();
        _actGrid.Rows.Clear();

        foreach (var act in shown)
        {
            var index = _actGrid.Rows.Add(
                act.Code,
                act.Label,
                CatalogScreenRules.Coefficient(act),
                CatalogScreenRules.Chapter(act),
                act.DecreeYear.ToString(CultureInfo.InvariantCulture),
                act.AnalysisCount == 0 ? string.Empty : act.AnalysisCount.ToString(CultureInfo.InvariantCulture));

            var row = _actGrid.Rows[index];
            row.Tag = act;
            row.Cells["decree"].ToolTipText = NationalBiologyAct.DecreeReference(act.DecreeYear);
            if (!act.IsActive) row.DefaultCellStyle.ForeColor = UiTheme.TextMuted;
        }

        _actGrid.ResumeLayout(performLayout: true);
        RestoreSelection(_actGrid, selected, r => (r as NationalActRow)?.Id);

        string[] captions = [T("Nabm.ChipAll"), .. CatalogScreenRules.DecreeYears.Select(y => y.ToString(CultureInfo.InvariantCulture))];
        for (var i = 0; i < _decreeChips.Length; i++)
        {
            var year = _decreeChips[i].Value is { } y ? int.Parse(y, CultureInfo.InvariantCulture) : (int?)null;
            _decreeChips[i].Text = $"{captions[i]} · {matching.Count(a => year is null || a.DecreeYear == year)}";
            _decreeChips[i].Selected = year == _decreeFilter;
        }

        _actHeading.Text = $"{T("Nabm.ActsHeading")} — {shown.Count}";
    }

    // ---- Écarts avec la nomenclature -----------------------------------------------------------

    private async Task LoadGapsAsync(CancellationToken ct = default)
    {
        var result = await _nationalActs.GetGapsAsync(ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _gapRows = result.Value;
        RenderGaps();
    }

    private void ChooseGapKind(NomenclatureGapKind? kind)
    {
        _gapFilter = kind;
        RenderGaps();
    }

    private void RefreshGapChips()
    {
        string[] captions = [T("Nabm.ChipAll"), T("NabmGap.MissingCode"), T("NabmGap.UnknownCode"),
                             T("NabmGap.CoefficientDiffers")];

        for (var i = 0; i < _gapChips.Length; i++)
        {
            var kind = _gapChips[i].Value is { } k ? Enum.Parse<NomenclatureGapKind>(k) : (NomenclatureGapKind?)null;
            _gapChips[i].Text = $"{captions[i]} · {CatalogScreenRules.Gaps(_gapRows, kind).Count}";
            _gapChips[i].Selected = kind == _gapFilter;
        }
    }

    private void RenderGaps()
    {
        var language = Localizer.CurrentLanguage;
        var selected = SelectedGap?.AnalysisId;
        var shown = CatalogScreenRules.Gaps(_gapRows, _gapFilter);

        _gapGrid.SuspendLayout();
        _gapGrid.Rows.Clear();

        foreach (var gap in shown)
        {
            var index = _gapGrid.Rows.Add(
                gap.AnalysisCode,
                gap.AnalysisName.For(language),
                gap.NationalActCode ?? "—",
                gap.ActLabel ?? string.Empty,
                gap.OfficialCoefficient is { } official ? $"B {official:0.##}" : string.Empty,
                gap.CurrentCoefficient is { } current ? $"{current:0.##}" : "—",
                CatalogScreenRules.GapText(gap, T));

            _gapGrid.Rows[index].Tag = gap;
        }

        _gapGrid.ResumeLayout(performLayout: true);
        RestoreSelection(_gapGrid, selected, r => (r as NomenclatureGapRow)?.AnalysisId);

        RefreshGapChips();
        _gapHeading.Text = $"{T("Nabm.GapsHeading")} — {shown.Count}";
        RefreshNationalActActions();
    }

    private void RefreshNationalActActions()
    {
        var actions = CatalogScreenRules.Actions(_currentUser, new CatalogSelection(Gap: SelectedGap));
        _applyOfficial.Enabled = actions.ApplyOfficial;
        _editGapAnalysis.Enabled = actions.EditGapAnalysis;
    }

    /// <summary>
    /// Applies the official coefficient to the selected analysis, after saying what will happen: a
    /// new tariff version from today for each tariff in force that departs from it, past invoices
    /// untouched. The change is audited with the coefficients it replaced.
    /// </summary>
    private async Task ApplyOfficialCoefficientAsync()
    {
        if (SelectedGap is not { CanApplyOfficialCoefficient: true } gap) return;

        if (!_dialogs.Confirm(CatalogScreenRules.ConfirmApply(gap, _clock.Today, T), _applyOfficial.Text)) return;

        var result = await _nationalActs.ApplyOfficialCoefficientAsync(gap.AnalysisId);

        if (result.IsFailure) { _dialogs.ShowFailure(result, _applyOfficial.Text); return; }

        var next = NeighbourGap();

        _status.ForeColor = UiTheme.Normal;
        _status.Text = CatalogScreenRules.Applied(gap, result.Value, _clock.Today, T);

        await LoadGapsAsync();
        KeepPlace(gap, next);
        await LoadAnalysesAsync();
    }

    /// <summary>The line below the current one, or above it for the last: where to go once the current line is put right.</summary>
    private NomenclatureGapRow? NeighbourGap() =>
        SelectedGap is { } gap ? CatalogScreenRules.Neighbour(CatalogScreenRules.Gaps(_gapRows, _gapFilter), gap.AnalysisId) : null;

    /// <summary>
    /// A line put right leaves the list, and the selection went back to the top of it: its neighbour
    /// becomes current instead, so the analyses are corrected one after the other.
    /// </summary>
    private void KeepPlace(NomenclatureGapRow done, NomenclatureGapRow? neighbour)
    {
        if (neighbour is null || SelectedGap?.AnalysisId == done.AnalysisId) return;

        RestoreSelection(_gapGrid, neighbour.AnalysisId, r => (r as NomenclatureGapRow)?.AnalysisId);
        RefreshNationalActActions();
    }

    /// <summary>Opens the analysis editor on the analysis of the selected line, to set or correct its act code.</summary>
    private async Task EditGapAnalysisAsync()
    {
        if (SelectedGap is not { } gap) return;

        var row = _analyses.Rows.Cast<DataGridViewRow>()
                           .FirstOrDefault(r => (r.Tag as AnalysisRow)?.Id == gap.AnalysisId);

        // Hidden by the filters of the Analyses tab: they are cleared so the analysis can be shown.
        if (row is null)
        {
            _search.Text = string.Empty;
            _onlyToComplete = false;
            _departmentFilter.SelectedIndex = 0;
            await LoadAnalysesAsync();

            row = _analyses.Rows.Cast<DataGridViewRow>()
                           .FirstOrDefault(r => (r.Tag as AnalysisRow)?.Id == gap.AnalysisId);
        }

        if (row is null) return;

        var next = NeighbourGap();
        RestoreSelection(_analyses, gap.AnalysisId, r => (r as AnalysisRow)?.Id);
        await OnAnalysisSelectedAsync();
        await EditAnalysisAsync();
        await LoadGapsAsync();
        KeepPlace(gap, next);
    }
}

/// <summary>
/// Picks an act of the national nomenclature for the analysis editor, by code or by label. Only the
/// act code is returned; the analysis keeps the internal code the laboratory gave it.
/// </summary>
internal sealed class NationalActPickerDialog : MasterDataEditor
{
    private readonly IReadOnlyList<NationalActRow> _acts;
    private readonly TextBox _search;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _count;

    internal NationalActRow? Chosen { get; private set; }

    internal NationalActPickerDialog(ILocalizer localizer, IUserDialogs dialogs,
                                     IReadOnlyList<NationalActRow> acts, string? current)
        : base(localizer, dialogs, "Nabm.PickerTitle", null, 760, 560)
    {
        _acts = acts;

        _search = TextField("Action.Search", 20, 16, 400);
        _search.PlaceholderText = localizer["Nabm.SearchPlaceholder"];
        _search.MaxLength = 100;
        _count = Notice(436, 38, 304, 20, "Nabm.ActsHeading");

        _grid.Dock = DockStyle.None;
        _grid.Bounds = new Rectangle(20, 72, 720, 410);
        AdminLayout.ListGrid(_grid);

        foreach (var (name, header, weight, font, right) in new[]
                 {
                     ("code", localizer["Nabm.ActCode"], 12, UiTheme.MonospaceBold, false),
                     ("label", localizer["Nabm.ActLabel"], 58, UiTheme.Body, false),
                     ("coefficient", localizer["Billing.Coefficient"], 14, UiTheme.Monospace, true),
                     ("decree", localizer["Nabm.Decree"], 12, UiTheme.Monospace, false)
                 })
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });
        }

        Controls.Add(_grid);

        _search.TextChanged += (_, _) => Render();

        // Arrow keys move in the list while the search keeps the focus, so the act is chosen
        // without leaving the keyboard.
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Down or Keys.Up) || _grid.Rows.Count == 0) return;
            e.SuppressKeyPress = true;
            var at = _grid.CurrentRow?.Index ?? -1;
            var next = Math.Clamp(at + (e.KeyCode == Keys.Down ? 1 : -1), 0, _grid.Rows.Count - 1);
            _grid.MakeCurrent(_grid.Rows[next]);
        };

        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Accept(); };

        Render();

        // Opened on the code already typed, when it is one of the list.
        var code = NationalBiologyAct.Normalize(current);
        var kept = code is null
            ? null
            : _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as NationalActRow)?.Code == code);
        if (kept is not null) _grid.MakeCurrent(kept);

        Buttons(500, 760, "Action.Select");
        UseAdminFrame();

        ActiveControl = _search;
    }

    private void Render()
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var act in CatalogScreenRules.Pickable(_acts, _search.Text))
        {
            var index = _grid.Rows.Add(act.Code, act.Label, CatalogScreenRules.Coefficient(act),
                                       act.DecreeYear.ToString(CultureInfo.InvariantCulture));
            _grid.Rows[index].Tag = act;
            _grid.Rows[index].Cells["label"].ToolTipText = CatalogScreenRules.Chapter(act);
        }

        _grid.ResumeLayout(performLayout: true);
        if (_grid.Rows.Count > 0) _grid.SelectFirstRow();

        _count.Text = $"{Localizer["Nabm.ActsHeading"]} — {_grid.Rows.Count}";
    }

    protected override void Accept()
    {
        if (_grid.CurrentRow?.Tag is not NationalActRow act)
        {
            Dialogs.Warn(Localizer["Nabm_PickAnAct"], Text);
            _search.Focus();
            return;
        }

        Chosen = act;
        DialogResult = DialogResult.OK;
        Close();
    }
}
