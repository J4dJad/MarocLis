using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Input;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;
using static MarocLIS.Desktop.Views.ReportGrids;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The discount dialogs of the workstation: « Remise… » at the counter, and the negotiated prices
/// of a convention with their editor. What they offer and refuse is <see cref="DiscountScreenRules"/>',
/// the same as the web's; what is saved, <see cref="DiscountService"/>'s.
/// </summary>
internal static class DiscountDialogs
{
    private static string Say(ILocalizer localizer, FieldRefusal refusal) =>
        refusal.Typed is { } typed ? localizer.Format(refusal.Key, typed) : localizer[refusal.Key];

    /// <summary>
    /// « Remise… »: the convention (the prescriber's, another, or none), a profile or a rate typed,
    /// the reason and, beyond the ceiling, the approver's name and password. Null when cancelled.
    /// </summary>
    internal static CounterDiscount? AskCounter(IWin32Window owner, ILocalizer localizer, IUserDialogs dialogs,
                                                CounterDiscountOptions options, CounterDiscount current, decimal gross)
    {
        var culture = CultureInfo.CurrentCulture;
        CounterDiscount? chosen = null;
        Func<(CounterDiscount?, FieldRefusal?)> read = () => (current, null);

        using var dialog = new FieldDialog(localizer, localizer["Discounts.DialogTitle"], () =>
        {
            var (discount, refusal) = read();
            if (refusal is not null)
            {
                dialogs.Warn(Say(localizer, refusal), localizer["Discounts.DialogTitle"]);
                return false;
            }
            chosen = discount;
            return true;
        }, height: 600, intro: localizer["Discounts.DialogHint"]);

        var convention = dialog.Choice(localizer["Discounts.Convention"],
            new[] { (DiscountScreenRules.AutomaticConvention, localizer["Discounts.AutomaticConvention"]),
                    (DiscountScreenRules.NoConvention, localizer["Discounts.NoConvention"]) }
                .Concat(options.Conventions.Select(c => (c.Id.ToString(), DiscountScreenRules.ConventionOption(c, culture)))),
            DiscountScreenRules.ConventionChoice(current));
        var profile = dialog.Choice(localizer["Discounts.Profile"],
            new[] { (string.Empty, localizer["Discounts.NoProfile"]) }
                .Concat(options.Profiles.Select(p => (p.Id.ToString(), DiscountScreenRules.ProfileOption(p, culture))))
                .Append((DiscountScreenRules.ManualProfile, localizer["Discounts.ManualRate"])),
            DiscountScreenRules.ProfileChoice(current));
        var percent = dialog.TextField(localizer["Discounts.Percent"], current.Percent?.ToString("0.##", culture), 8);
        var reason = dialog.TextField(localizer["Discounts.Reason"], current.Reason, BillingScreenRules.DiscountReasonMaxLength);
        var estimate = dialog.Note(string.Empty);
        dialog.Note(localizer["Billing.DiscountApprovalHeading"]);
        var approver = dialog.TextField(localizer["Billing.DiscountApprover"], current.Approval?.UserName, DiscountEntry.ApproverMaxLength);
        var password = dialog.TextField(localizer["Billing.DiscountApproverPassword"], null, DiscountEntry.ApproverPasswordMaxLength);
        password.UseSystemPasswordChar = true;
        foreach (var box in new[] { percent, approver, password }) box.RightToLeft = RightToLeft.No;

        string Choice(ComboBox combo) => FieldDialog.Selected<string>(combo) ?? string.Empty;

        void Update()
        {
            var manual = Choice(profile) == DiscountScreenRules.ManualProfile;
            percent.Enabled = manual;
            var rate = DiscountScreenRules.CounterPercent(Choice(profile), options.Profiles, percent.Text);
            if (!manual) percent.Text = rate?.ToString("0.##", culture) ?? string.Empty;
            estimate.Text = rate is { } r && gross > 0m
                ? localizer.Format("Discounts.Estimate", Money.Of(Money.Round(gross * r / 100m)).Format(localizer.CurrentLanguage),
                                   Money.Of(gross).Format(localizer.CurrentLanguage))
                : string.Empty;
            approver.Enabled = password.Enabled = DiscountScreenRules.NeedsApproval(rate, gross, options.Cap);
        }

        profile.SelectedIndexChanged += (_, _) => Update();
        percent.TextChanged += (_, _) => { if (percent.Enabled) Update(); };
        Update();

        read = () => DiscountScreenRules.ReadCounter(Choice(convention), Choice(profile), options.Profiles, percent.Text,
                                                     reason.Text, approver.Enabled ? approver.Text : null,
                                                     password.Enabled ? password.Text : null, gross, options.Cap);

        return dialog.ShowDialog(owner) == DialogResult.OK ? chosen : null;
    }

    /// <summary>
    /// « Tarifs négociés » of a convention: read with the conventions; added, changed and withdrawn
    /// with a reason by whom may manage the tariffs — the buttons greyed for others.
    /// </summary>
    internal static async Task ShowPricesAsync(IWin32Window owner, ILocalizer localizer, IUserDialogs dialogs, DiscountService discounts,
                                               ICatalogQueries catalog, ICurrentUser user, Guid partnershipId, string partnerName)
    {
        var culture = CultureInfo.CurrentCulture;
        var language = localizer.CurrentLanguage;
        var manage = DiscountScreenRules.CanManage(user.Has);
        IReadOnlyList<PartnershipPriceRow> prices = [];

        using var form = new Form
        {
            Text = localizer.Format("Discounts.PricesTitle", partnerName),
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = UiTheme.SurfaceRaised,
            Font = UiTheme.Body,
            ClientSize = new Size(860, 520),
            RightToLeft = language == AppLanguage.Arabic ? RightToLeft.Yes : RightToLeft.No,
            RightToLeftLayout = language == AppLanguage.Arabic
        };

        var grid = UiTheme.Grid();
        grid.Dock = DockStyle.Fill;
        foreach (var column in DiscountScreenRules.PriceColumns)
        {
            AddColumn(grid, column.Name, column.Name == "analysis" ? 26 : 12, column.Numeric);
            grid.Columns[column.Name]!.HeaderText = localizer[column.CaptionKey];
        }

        var add = UiTheme.SecondaryButton(localizer["Discounts.NewPrice"]);
        var edit = UiTheme.SecondaryButton(localizer["Discounts.EditPrice"]);
        var remove = UiTheme.SecondaryButton(localizer["Discounts.RemovePrice"]);
        var close = UiTheme.PrimaryButton(localizer["Action.Close"]);
        close.DialogResult = DialogResult.Cancel;
        ButtonIcons.Set(add, Glyph.Plus, always: true);
        ButtonIcons.Set(edit, Glyph.Pencil, always: true);
        ButtonIcons.Set(remove, Glyph.Trash2, always: true);

        var foot = DialogFrame.Foot(null, close);
        var flow = (FlowLayoutPanel)foot.Controls[0];
        foreach (var button in new[] { remove, edit, add })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(120, 34);
            button.Margin = new Padding(8, 0, 0, 0);
            flow.Controls.Add(button);
        }

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8), BackColor = UiTheme.SurfaceRaised };
        body.Controls.Add(grid);
        form.Controls.Add(body);
        form.Controls.Add(foot);
        form.Controls.Add(DialogFrame.Head(form.Text, localizer["Discounts.PricesIntro"], form.ClientSize.Width));
        form.CancelButton = close;

        // Greyed, not hidden, without the tariffs right: the foot keeps its places.
        PermissionGate.Require(Permissions.Billing.ManageTariffs, add, edit, remove);

        PartnershipPriceRow? Selected() => grid.CurrentRow?.Tag as PartnershipPriceRow;
        void Buttons() => edit.Enabled = remove.Enabled = manage && Selected() is not null;

        string Amount(decimal value) => Money.Of(value).Format(language);

        async Task LoadAsync()
        {
            var read = await discounts.GetPricesAsync(partnershipId);
            if (read.IsFailure) { dialogs.ShowFailure(read, form.Text); return; }
            prices = read.Value;
            grid.Rows.Clear();
            var rows = DiscountScreenRules.PriceRows(prices, localizer.Format, Amount, language, culture);
            for (var i = 0; i < rows.Count; i++)
            {
                var index = grid.Rows.Add([.. rows[i].Cells]);
                grid.Rows[index].Tag = prices[i];
                if (!prices[i].InForce) grid.Rows[index].DefaultCellStyle.ForeColor = UiTheme.TextSecondary;
            }
            Buttons();
        }

        async Task EditAsync(PartnershipPriceRow? row)
        {
            if (!manage) return;
            var request = await AskPriceAsync(form, localizer, dialogs, catalog, partnershipId, row);
            if (request is null) return;
            var saved = await discounts.SavePriceAsync(request);
            if (saved.IsFailure) { dialogs.ShowFailure(saved, form.Text); return; }
            await LoadAsync();
        }

        add.Click += async (_, _) => await EditAsync(null);
        edit.Click += async (_, _) => { if (Selected() is { } row) await EditAsync(row); };
        grid.CellDoubleClick += async (_, _) => { if (manage && Selected() is { } row) await EditAsync(row); };
        grid.SelectionChanged += (_, _) => Buttons();
        remove.Click += async (_, _) =>
        {
            if (Selected() is not { } row) return;
            var reason = dialogs.PromptForReason($"{localizer["Discounts.RemovePriceReason"]} — {row.AnalysisCode} {row.AnalysisName.For(language)}",
                                                 localizer["Discounts.RemovePrice"], maxLength: 300);
            if (reason is null) return;
            var removed = await discounts.RemovePriceAsync(row.Id, reason);
            if (removed.IsFailure) { dialogs.ShowFailure(removed, form.Text); return; }
            await LoadAsync();
        };

        form.Shown += async (_, _) => await LoadAsync();
        form.ShowDialog(owner);
    }

    /// <summary>The editor of one negotiated price; the analysis found by its code or name. Null when cancelled.</summary>
    private static async Task<SavePartnershipPriceRequest?> AskPriceAsync(IWin32Window owner, ILocalizer localizer, IUserDialogs dialogs,
                                                                          ICatalogQueries catalog, Guid partnershipId, PartnershipPriceRow? row)
    {
        var culture = CultureInfo.CurrentCulture;
        var language = localizer.CurrentLanguage;
        var initial = DiscountScreenRules.PriceFormFor(row, DateOnly.FromDateTime(DateTime.Today), culture);
        SavePartnershipPriceRequest? request = null;
        Func<PartnershipPriceForm> read = () => initial;

        using var dialog = new FieldDialog(localizer, localizer[row is null ? "Discounts.NewPrice" : "Discounts.EditPrice"], () =>
        {
            var (refusal, checkedRequest) = DiscountScreenRules.Check(read(), partnershipId, row?.Id);
            if (refusal is not null)
            {
                dialogs.Warn(Say(localizer, refusal), localizer["Discounts.PriceEditorTitle"]);
                return false;
            }
            request = checkedRequest;
            return true;
        }, height: 460, intro: localizer["Discounts.PriceEditorTitle"]);

        TextBox? search = null;
        if (row is null) search = dialog.TextField(localizer["Discounts.Analysis"], null, 60);
        IEnumerable<(Guid Value, string Display)> current = row is null
            ? []
            : [(row.AnalysisTestId, $"{row.AnalysisCode} — {row.AnalysisName.For(language)}")];
        var analysis = dialog.Choice<Guid>(localizer["Discounts.Analysis"], current, row?.AnalysisTestId ?? Guid.Empty);
        analysis.Enabled = row is null;
        var kind = dialog.Choice(localizer["Discounts.Value"],
                                 new[] { (true, localizer["Discounts.FixedPrice"]), (false, localizer["Discounts.RatePrice"]) }, initial.IsFixedPrice);
        var value = dialog.TextField(localizer["Discounts.Value"], initial.Value, 12);
        value.RightToLeft = RightToLeft.No;
        var from = dialog.Date(localizer["Discounts.ValidFrom"], initial.ValidFrom);
        var until = dialog.Date(localizer["Discounts.ValidUntil"], initial.ValidUntil, optional: true);

        if (search is not null)
        {
            search.TextChanged += async (_, _) =>
            {
                var term = search.Text.Trim();
                if (term.Length == 0) return;
                var found = await catalog.SearchPickerAsync(term, null, DateOnly.FromDateTime(DateTime.Today), 20);
                if (search.Text.Trim() != term) return;
                analysis.Items.Clear();
                foreach (var item in found.Where(f => !f.IsPanel))
                    analysis.Items.Add(new DialogChoice(item.Id, $"{item.Code} — {item.Name.For(language)} ({Money.Of(item.Price).Format(language)})"));
                if (analysis.Items.Count > 0) analysis.SelectedIndex = 0;
            };
        }

        read = () => new PartnershipPriceForm
        {
            AnalysisTestId = FieldDialog.Selected<Guid>(analysis) is var id && id != Guid.Empty ? id : null,
            IsFixedPrice = FieldDialog.Selected<bool>(kind),
            Value = value.Text,
            ValidFrom = FieldDialog.Value(from) ?? initial.ValidFrom,
            ValidUntil = FieldDialog.Value(until)
        };

        await Task.Yield();
        return dialog.ShowDialog(owner) == DialogResult.OK ? request : null;
    }
}
