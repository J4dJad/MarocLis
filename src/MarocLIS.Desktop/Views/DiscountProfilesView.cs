using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Input;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;
using static MarocLIS.Desktop.Views.ReportGrids;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// « Profils de remise »: the discounts the counter picks by name (« Personnel 50 % », « Étudiant
/// 10 % »), their rate, dates and whether they are offered today. Created and changed with the
/// tariffs right, the buttons greyed for others; a profile is switched off, never deleted. The
/// rules are <see cref="DiscountScreenRules"/>', the same as the web's.
/// </summary>
public sealed class DiscountProfilesView : LocalizedView
{
    private readonly DiscountService _discounts;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<DiscountProfilesView>? _logger;

    private readonly PeriodBar _bar;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _intro = new()
    {
        Dock = DockStyle.Top, Height = 30, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
    };

    private IReadOnlyList<DiscountProfileRow> _profiles = [];

    public DiscountProfilesView(DiscountService discounts, IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                                ILogger<DiscountProfilesView>? logger = null)
        : base(localizer, cultureManager)
    {
        _discounts = discounts;
        _dialogs = dialogs;
        _logger = logger;
        _bar = new PeriodBar(localizer, withPeriod: false, primaryCompute: false);
        Padding = new Padding(24, 16, 24, 16);

        foreach (var column in DiscountScreenRules.ProfileColumns)
            AddColumn(_grid, column.Name, column.Name == "name" ? 30 : 14, column.Numeric);

        _bar.Export.Visible = false;
        _bar.Extra.Controls.Add(_edit);
        _bar.Lead.Controls.Add(_new);
        ButtonIcons.Set(_new, Glyph.Plus, always: true);
        ButtonIcons.Set(_edit, Glyph.Pencil, always: true);

        var host = new TabControl { Dock = DockStyle.Fill };
        host.TabPages.Add(Pilotage.CardPage(_grid));
        Controls.Add(host);
        Controls.Add(_intro);
        Controls.Add(_bar);

        PermissionGate.Require(Permissions.Billing.ManageTariffs, _new, _edit);

        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _new.Click += async (_, _) => await EditAsync(null);
        _edit.Click += async (_, _) => { if (Selected is { } row) await EditAsync(row); };
        _grid.CellDoubleClick += async (_, _) => { if (!PermissionGate.IsBlocked(_edit) && Selected is { } row) await EditAsync(row); };
        _grid.SelectionChanged += (_, _) => _edit.Enabled = Selected is not null;
    }

    public override string Title => T("Nav.DiscountProfiles");

    private DiscountProfileRow? Selected => _grid.CurrentRow?.Tag as DiscountProfileRow;

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Discounts.ProfilesTitle");
        _bar.Compute.Text = T("Action.Refresh");
        _new.Text = T("Discounts.NewProfile");
        _edit.Text = T("Discounts.EditProfile");
        _intro.Text = T("Discounts.ProfilesIntro");
        foreach (var column in DiscountScreenRules.ProfileColumns) _grid.Columns[column.Name]!.HeaderText = T(column.CaptionKey);
        Render();
    }

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _discounts.GetProfilesAsync(ct);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result, Title);
                return;
            }
            _profiles = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reading the discount profiles failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), Title);
        }
    }

    private void Render()
    {
        _grid.Rows.Clear();
        var rows = DiscountScreenRules.ProfileRows(_profiles, T, CultureInfo.CurrentCulture);
        for (var i = 0; i < rows.Count; i++)
        {
            var index = _grid.Rows.Add([.. rows[i].Cells]);
            _grid.Rows[index].Tag = _profiles[i];
            if (!_profiles[i].AvailableToday) _grid.Rows[index].DefaultCellStyle.ForeColor = UiTheme.TextSecondary;
        }
        _bar.Subtitle.Text = Count(_profiles.Count);
        _edit.Enabled = Selected is not null;
    }

    private async Task EditAsync(DiscountProfileRow? row)
    {
        var form = DiscountScreenRules.FormFor(row, CultureInfo.CurrentCulture);
        Func<DiscountProfileForm> read = () => form;
        SaveDiscountProfileRequest? request = null;

        using var dialog = new FieldDialog(Localizer, T(row is null ? "Discounts.NewProfile" : "Discounts.EditProfile"), () =>
        {
            var (refusal, checkedRequest) = DiscountScreenRules.Check(read(), row?.Id);
            if (refusal is not null)
            {
                _dialogs.Warn(Say(refusal), Title);
                return false;
            }
            request = checkedRequest;
            return true;
        }, height: 360, intro: T("Discounts.ProfileEditorTitle"));

        var name = dialog.TextField(T("Discounts.Name"), form.Name, Domain.Billing.DiscountProfile.NameMaxLength);
        var percent = dialog.TextField(T("Discounts.Percent"), form.Percent, 8);
        percent.RightToLeft = RightToLeft.No;
        var active = dialog.Check(T("Discounts.Active"), form.IsActive);
        var from = dialog.Date(T("Discounts.ValidFrom"), form.ValidFrom, optional: true);
        var until = dialog.Date(T("Discounts.ValidUntil"), form.ValidUntil, optional: true);

        read = () => new DiscountProfileForm
        {
            Name = name.Text, Percent = percent.Text, IsActive = active.Checked,
            ValidFrom = FieldDialog.Value(from), ValidUntil = FieldDialog.Value(until)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || request is null) return;

        var saved = await _discounts.SaveProfileAsync(request);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, Title); return; }
        await LoadAsync();
    }

    private string Say(FieldRefusal refusal) =>
        refusal.Typed is { } typed ? Localizer.Format(refusal.Key, typed) : T(refusal.Key);
}
