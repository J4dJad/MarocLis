using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Relations;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Relations;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;
using static MarocLIS.Desktop.Views.ReportGrids;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The delegate's visits to prescribers — with the next visits that have come due — and the
/// conventions with clinics, companies and partner laboratories, with the signed document.
/// </summary>
public sealed class RelationsView : LocalizedView
{
    private readonly RelationsService _relations;
    private readonly ICurrentUser _currentUser;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<RelationsView>? _logger;

    private readonly PeriodBar _bar;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _visits = UiTheme.Grid();
    private readonly DataGridView _due = UiTheme.Grid();
    private readonly DataGridView _partnerships = UiTheme.Grid();
    private readonly TabPage _visitPage;
    private readonly TabPage _duePage;
    private readonly TabPage _partnershipPage;

    // The main action of the tab in view is the page's primary button; the rare ones are under
    // « Plus d'actions ».
    private readonly Button _newVisit = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editVisit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _newPartnership = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editPartnership = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly Button _more;
    private readonly ToolStripMenuItem _deleteVisit = Pilotage.MenuItem("Action.Delete");
    private readonly ToolStripMenuItem _attach = Pilotage.MenuItem();
    private readonly ToolStripMenuItem _openFile = Pilotage.MenuItem();
    private readonly CheckBox _inactive = new() { AutoSize = true };
    private readonly Label _notice = new()
    {
        Dock = DockStyle.Top, Height = 30, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
    };

    private int _visitCount, _dueCount, _partnershipCount;
    private bool _loaded;

    public RelationsView(RelationsService relations, ICurrentUser currentUser, IDocumentOutput output,
                         IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                         ILogger<RelationsView>? logger = null)
        : base(localizer, cultureManager)
    {
        _relations = relations;
        _currentUser = currentUser;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;
        _bar = new PeriodBar(localizer, primaryCompute: false);
        _more = Pilotage.MoreButton(_moreMenu);
        Padding = new Padding(24, 16, 24, 16);

        foreach (var grid in new[] { _visits, _due })
        {
            AddColumn(grid, "date", 9);
            AddColumn(grid, "prescriber", 20);
            AddColumn(grid, "institution", 14);
            AddColumn(grid, "delegate", 14);
            AddColumn(grid, "purpose", 13);
            AddColumn(grid, "notes", 26);
            AddColumn(grid, "next", 9);
        }

        AddColumn(_partnerships, "partner", 20);
        AddColumn(_partnerships, "partnerKind", 10);
        AddColumn(_partnerships, "kind", 14);
        AddColumn(_partnerships, "reference", 10);
        AddColumn(_partnerships, "start", 9);
        AddColumn(_partnerships, "end", 9);
        AddColumn(_partnerships, "state", 10);
        AddColumn(_partnerships, "discount", 7, numeric: true);
        AddColumn(_partnerships, "contact", 14);
        AddColumn(_partnerships, "file", 12);

        foreach (var grid in new[] { _visits, _due })
        {
            grid.Column("date").DefaultCellStyle.Font = UiTheme.Monospace;
            grid.Column("next").DefaultCellStyle.Font = UiTheme.Monospace;
        }
        _partnerships.Column("start").DefaultCellStyle.Font = UiTheme.Monospace;
        _partnerships.Column("end").DefaultCellStyle.Font = UiTheme.Monospace;
        _partnerships.Column("reference").DefaultCellStyle.Font = UiTheme.Monospace;

        // The state of a convention as a pill: in force, ending soon, or out of force.
        Pilotage.Pills(_partnerships, "state", row => row.Tag is not PartnershipRow p ? null
            : !p.IsActive ? (UiTheme.SurfaceSunken, UiTheme.TextSecondary, "○")
            : p.EndingSoon ? (UiTheme.AbnormalBackground, UiTheme.Abnormal, "!")
            : p.InForce ? (UiTheme.NormalBackground, UiTheme.Normal, "✓")
            : (UiTheme.SurfaceSunken, UiTheme.TextSecondary, "○"));

        _visitPage = Pilotage.CardPage(_visits);
        _duePage = Pilotage.CardPage(_due);
        _partnershipPage = Pilotage.CardPage(_partnerships);
        _tabs.TabPages.AddRange([_visitPage, _duePage, _partnershipPage]);

        var manage = currentUser.Has(Permissions.Relations.Manage);
        foreach (var button in new[] { _newVisit, _editVisit, _newPartnership, _editPartnership })
            button.Visible = manage;
        _deleteVisit.Available = _attach.Available = manage;

        _moreMenu.Items.AddRange([_deleteVisit, _attach, _openFile]);
        _bar.Extra.Controls.AddRange([_editVisit, _editPartnership, _more]);
        _bar.Lead.Controls.AddRange([_newVisit, _newPartnership]);
        // The tick box goes at the end of the notice line: beside the dates it ran off a
        // 1366-pixel screen.
        var noticeLine = new Panel { Dock = DockStyle.Top, Height = _notice.Height, BackColor = UiTheme.Surface };
        _notice.Dock = DockStyle.Fill;
        _inactive.Dock = DockStyle.Right;
        _inactive.Padding = new Padding(12, 0, 0, 0);
        noticeLine.Controls.Add(_notice);
        noticeLine.Controls.Add(_inactive);

        Controls.Add(_tabs);
        Controls.Add(noticeLine);
        Controls.Add(_bar);

        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _bar.Export.Click += async (_, _) =>
            await ExportAsync(Pilotage.GridOf(_tabs.SelectedTab), _output, _dialogs,
                              _bar.Export.Text, Localizer);
        _tabs.SelectedIndexChanged += (_, _) => UpdateButtons();
        foreach (var grid in new[] { _visits, _due, _partnerships })
            grid.SelectionChanged += (_, _) => UpdateButtons();
        _inactive.CheckedChanged += async (_, _) => await LoadAsync();
        _newVisit.Click += async (_, _) => await EditVisitAsync(null);
        _editVisit.Click += async (_, _) => { if (SelectedVisit is { } v) await EditVisitAsync(v); };
        _visits.CellDoubleClick += async (_, _) => { if (manage && SelectedVisit is { } v) await EditVisitAsync(v); };
        _deleteVisit.Click += async (_, _) => await DeleteVisitAsync();
        _newPartnership.Click += async (_, _) => await EditPartnershipAsync(null);
        _editPartnership.Click += async (_, _) => { if (SelectedPartnership is { } p) await EditPartnershipAsync(p.Id); };
        _partnerships.CellDoubleClick += async (_, _) => { if (manage && SelectedPartnership is { } p) await EditPartnershipAsync(p.Id); };
        _attach.Click += async (_, _) => await AttachAsync();
        _openFile.Click += async (_, _) => await OpenFileAsync();
    }

    public override string Title => T("Nav.Relations");

    private bool OnPartnerships => _tabs.SelectedTab == _partnershipPage;

    private DelegateVisitRow? SelectedVisit =>
        (_tabs.SelectedTab == _duePage ? _due : _visits).CurrentRow?.Tag as DelegateVisitRow;

    private PartnershipRow? SelectedPartnership => _partnerships.CurrentRow?.Tag as PartnershipRow;

    private void UpdateButtons()
    {
        var manage = _currentUser.Has(Permissions.Relations.Manage);
        _newVisit.Visible = _editVisit.Visible = manage && !OnPartnerships;
        _deleteVisit.Available = manage && !OnPartnerships;
        _newPartnership.Visible = _editPartnership.Visible = manage && OnPartnerships;
        _attach.Available = manage && OnPartnerships;
        _openFile.Available = _inactive.Visible = OnPartnerships;

        // Only with a row to act on: with an empty list « Modifier », « Supprimer » and the
        // document actions were offered and did nothing when clicked.
        _editVisit.Enabled = _deleteVisit.Enabled = SelectedVisit is not null;
        _editPartnership.Enabled = _attach.Enabled = SelectedPartnership is not null;
        _openFile.Enabled = SelectedPartnership is { FileName: not null };
        Pilotage.SyncMore(_more, _moreMenu);
    }

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Relations.Title");
        _bar.Compute.Text = T("Action.Refresh");
        _visitPage.Text = T("Relations.Visits");
        _duePage.Text = T("Relations.DueVisits");
        _partnershipPage.Text = T("Relations.Partnerships");
        _newVisit.Text = T("Relations.NewVisit");
        _editVisit.Text = T("Action.Edit");
        _deleteVisit.Text = T("Action.Delete");
        _newPartnership.Text = T("Relations.NewPartnership");
        _editPartnership.Text = T("Action.Edit");
        _attach.Text = T("Relations.AttachDocument");
        _openFile.Text = T("Relations.OpenDocument");
        _inactive.Text = T("Relations.ShowInactive");
        _more.Text = T("ResultEntry.MoreActions");
        _notice.Text = T("Relations.FollowUpNotice");

        foreach (var grid in new[] { _visits, _due, _partnerships })
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderText = T($"Relations.Column.{column.Name}");

        if (_loaded) ShowCounts();
        UpdateButtons();
    }

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            UseWaitCursor = true;

            var visits = await _relations.GetVisitsAsync(_bar.From, _bar.To, null, ct);
            if (visits.IsFailure) { _dialogs.ShowFailure(visits, Title); return; }
            var due = await _relations.GetDueVisitsAsync(ct);
            var partnerships = await _relations.GetPartnershipsAsync(_inactive.Checked, ct);

            FillVisits(_visits, visits.Value);
            _visitCount = visits.Value.Count;
            if (due.IsSuccess) { FillVisits(_due, due.Value); _dueCount = due.Value.Count; }
            if (partnerships.IsSuccess) { FillPartnerships(partnerships.Value); _partnershipCount = partnerships.Value.Count; }
            _loaded = true;
            ShowCounts();
            UpdateButtons();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the relations failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), Title);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void ShowCounts() =>
        _bar.Subtitle.Text = T("Relations.Counts", _visitCount, _dueCount, _partnershipCount);

    private void FillVisits(DataGridView grid, IReadOnlyList<DelegateVisitRow> rows)
    {
        grid.Rows.Clear();
        foreach (var v in rows)
        {
            var index = grid.Rows.Add(Date(v.VisitDate), v.PrescriberName, v.Institution ?? string.Empty, v.DelegateName,
                                      T($"VisitPurpose.{v.Purpose}"), v.Notes ?? string.Empty, Date(v.NextVisitOn));
            grid.Rows[index].Tag = v;
            if (v.NextVisitDue) grid.Rows[index].Cells["next"].Style.ForeColor = UiTheme.Overdue;
        }
    }

    private void FillPartnerships(IReadOnlyList<PartnershipRow> rows)
    {
        _partnerships.Rows.Clear();
        foreach (var p in rows)
        {
            var state = !p.IsActive ? T("Relations.State.Inactive")
                : p.EndingSoon ? T("Relations.State.EndingSoon")
                : p.InForce ? T("Relations.State.InForce")
                : T("Relations.State.NotInForce");
            var index = _partnerships.Rows.Add(p.PartnerName, T($"PartnerKind.{p.PartnerKind}"),
                                               T($"PartnershipKind.{p.Kind}"), p.Reference ?? string.Empty,
                                               Date(p.StartDate), Date(p.EndDate), state,
                                               p.DiscountPercent is { } d ? $"{d:0.##} %" : string.Empty,
                                               string.Join(" · ", new[] { p.ContactName, p.ContactPhone }
                                                   .Where(s => !string.IsNullOrWhiteSpace(s))),
                                               p.FileName ?? string.Empty);
            _partnerships.Rows[index].Tag = p;
            if (p.EndingSoon) _partnerships.Rows[index].DefaultCellStyle.BackColor = UiTheme.AbnormalBackground;
        }
    }

    // ---- Visits --------------------------------------------------------------------------------------

    private async Task EditVisitAsync(DelegateVisitRow? existing)
    {
        var options = await _relations.GetOptionsAsync(existing is null ? null : [existing.PrescriberId, existing.DelegateUserId]);
        if (options.IsFailure) { _dialogs.ShowFailure(options, Title); return; }

        ComboBox prescriber = null!, delegateUser = null!, purpose = null!;
        DateTimePicker date = null!, next = null!;
        TextBox notes = null!;

        using var dialog = new FieldDialog(Localizer, existing is null ? T("Relations.NewVisit") : T("Relations.EditVisit"), () =>
        {
            if (FieldDialog.Selected<Guid>(prescriber) == Guid.Empty || FieldDialog.Selected<Guid>(delegateUser) == Guid.Empty)
            {
                _dialogs.Warn(T("Relations.ChoosePrescriberAndDelegate"), Title);
                return false;
            }
            return true;
        }, intro: T("Relations.VisitIntro"));

        date = dialog.Date(T("Field.Date"), existing?.VisitDate);
        prescriber = dialog.Choice(T("Relations.Column.prescriber"),
                                   options.Value.Prescribers.Select(p => (p.Id, Display(p))), existing?.PrescriberId ?? Guid.Empty);
        delegateUser = dialog.Choice(T("Relations.Column.delegate"),
                                     options.Value.Staff.Select(s => (s.Id, Display(s))),
                                     existing?.DelegateUserId ?? _currentUser.UserId);
        purpose = dialog.Choice(T("Relations.Column.purpose"),
                                Enum.GetValues<VisitPurpose>().Select(p => (p, T($"VisitPurpose.{p}"))),
                                existing?.Purpose ?? VisitPurpose.FollowUp);
        notes = dialog.TextField(T("Relations.Column.notes"), existing?.Notes, DelegateVisit.NotesMaxLength, multiline: true);
        next = dialog.Date(T("Relations.Column.next"), existing?.NextVisitOn, optional: true);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var saved = await _relations.SaveVisitAsync(new SaveDelegateVisitRequest
        {
            Id = existing?.Id,
            VisitDate = FieldDialog.Value(date)!.Value,
            PrescriberId = FieldDialog.Selected<Guid>(prescriber),
            DelegateUserId = FieldDialog.Selected<Guid>(delegateUser),
            Purpose = FieldDialog.Selected<VisitPurpose>(purpose),
            Notes = FieldDialog.Value(notes),
            NextVisitOn = FieldDialog.Value(next)
        });

        if (saved.IsFailure) { _dialogs.ShowFailure(saved, Title); return; }
        await LoadAsync();
    }

    // A record the visit or convention already names, since deactivated, stays selectable and says so.
    private string Display(RelationLookup lookup) =>
        lookup.IsActive ? lookup.Display : $"{lookup.Display} {T("Contracts.InactiveSuffix")}";

    private async Task DeleteVisitAsync()
    {
        if (SelectedVisit is not { } visit) return;
        var reason = _dialogs.PromptForReason(T("Message.ConfirmDelete", $"{Date(visit.VisitDate)} — {visit.PrescriberName}"));
        if (reason is null) return;

        var deleted = await _relations.DeleteVisitAsync(visit.Id, reason);
        if (deleted.IsFailure) { _dialogs.ShowFailure(deleted, Title); return; }
        await LoadAsync();
    }

    // ---- Conventions ---------------------------------------------------------------------------------

    private async Task EditPartnershipAsync(Guid? id)
    {
        PartnershipDetail? existing = null;
        if (id is { } found)
        {
            var loaded = await _relations.GetPartnershipAsync(found);
            if (loaded.IsFailure) { _dialogs.ShowFailure(loaded, Title); return; }
            existing = loaded.Value;
        }

        var options = await _relations.GetOptionsAsync(existing?.PrescriberId is { } linkedId ? [linkedId] : null);
        if (options.IsFailure) { _dialogs.ShowFailure(options, Title); return; }

        TextBox name = null!;
        using var dialog = new FieldDialog(Localizer, existing is null ? T("Relations.NewPartnership") : T("Relations.EditPartnership"), () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                _dialogs.Warn(T("Partnership_PartnerRequired"), Title);
                return false;
            }
            return true;
        }, height: 640, intro: T("Relations.PartnershipIntro"));

        name = dialog.TextField(T("Relations.Column.partner"), existing?.PartnerName);
        var partnerKind = dialog.Choice(T("Relations.Column.partnerKind"),
                                        Enum.GetValues<PartnerKind>().Select(k => (k, T($"PartnerKind.{k}"))),
                                        existing?.PartnerKind ?? PartnerKind.Clinic);
        var prescriber = dialog.Choice(T("Relations.LinkedPrescriber"),
                                       new[] { (Guid.Empty, "—") }.Concat(options.Value.Prescribers.Select(p => (p.Id, Display(p)))),
                                       existing?.PrescriberId ?? Guid.Empty);
        var kind = dialog.Choice(T("Relations.Column.kind"),
                                 Enum.GetValues<PartnershipKind>().Select(k => (k, T($"PartnershipKind.{k}"))),
                                 existing?.Kind ?? PartnershipKind.CareConvention);
        var reference = dialog.TextField(T("Relations.Column.reference"), existing?.Reference, 60);
        var start = dialog.Date(T("Relations.Column.start"), existing?.StartDate);
        var end = dialog.Date(T("Relations.Column.end"), existing?.EndDate, optional: true);
        var terms = dialog.TextField(T("Relations.Terms"), existing?.Terms, Partnership.TermsMaxLength, multiline: true);
        var discount = dialog.Number(T("Relations.Column.discount"), existing?.DiscountPercent ?? 0m, 0m, 100m, 2);
        var paymentDays = dialog.Number(T("Relations.PaymentTermsDays"), existing?.PaymentTermsDays ?? 0, 0, 365);
        var ice = dialog.TextField("ICE", existing?.PartnerIce, 20);
        var contact = dialog.TextField(T("Relations.ContactName"), existing?.ContactName, 150);
        var phone = dialog.TextField(T("Field.Phone"), existing?.ContactPhone, 30);
        var email = dialog.TextField(T("Field.Email"), existing?.ContactEmail, 200);
        var notes = dialog.TextField(T("Field.Notes"), existing?.Notes, 1000, multiline: true);
        var active = dialog.Check(T("Field.Active"), existing?.IsActive ?? true);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var linked = FieldDialog.Selected<Guid>(prescriber);
        var saved = await _relations.SavePartnershipAsync(new SavePartnershipRequest
        {
            Id = existing?.Id,
            PartnerName = name.Text,
            PartnerKind = FieldDialog.Selected<PartnerKind>(partnerKind),
            PrescriberId = linked == Guid.Empty ? null : linked,
            PartnerIce = FieldDialog.Value(ice),
            ContactName = FieldDialog.Value(contact),
            ContactPhone = FieldDialog.Value(phone),
            ContactEmail = FieldDialog.Value(email),
            Kind = FieldDialog.Selected<PartnershipKind>(kind),
            Reference = FieldDialog.Value(reference),
            StartDate = FieldDialog.Value(start)!.Value,
            EndDate = FieldDialog.Value(end),
            Terms = FieldDialog.Value(terms),
            DiscountPercent = discount.Value == 0m ? null : discount.Value,
            PaymentTermsDays = paymentDays.Value == 0m ? null : (int)paymentDays.Value,
            Notes = FieldDialog.Value(notes),
            IsActive = active.Checked
        });

        if (saved.IsFailure) { _dialogs.ShowFailure(saved, Title); return; }
        await LoadAsync();
    }

    private async Task AttachAsync()
    {
        if (SelectedPartnership is not { } partnership) return;

        using var picker = new OpenFileDialog
        {
            Title = UserDialogs.CaptionOf(_attach.Text, _attach.Text),
            Filter = $"PDF, images, Office ({SafeDocumentFile.PickerPattern})|{SafeDocumentFile.PickerPattern}",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] content;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > PartnershipDocument.MaximumBytes)
            {
                _dialogs.Warn(T("Purchasing_FileTooLarge", PartnershipDocument.MaximumBytes / (1024 * 1024)), _attach.Text);
                return;
            }
            content = await File.ReadAllBytesAsync(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T("Documents.FileUnreadable")}\n{ex.Message}", _attach.Text);
            return;
        }

        var attached = await _relations.AttachDocumentAsync(partnership.Id, Path.GetFileName(picker.FileName), content);
        if (attached.IsFailure) { _dialogs.ShowFailure(attached, _attach.Text); return; }
        await LoadAsync();
    }

    private async Task OpenFileAsync()
    {
        if (SelectedPartnership is not { FileName: not null } partnership) return;

        var file = await _relations.GetDocumentAsync(partnership.Id);
        if (file.IsFailure) { _dialogs.ShowFailure(file, _openFile.Text); return; }

        var opened = await _output.ViewAsync(file.Value.Content, Path.GetExtension(file.Value.FileName));
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _openFile.Text);
    }
}
