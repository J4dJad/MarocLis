using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Quality;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Document control: the register of procedures, instructions and forms, and their versions.
/// <para>
/// The upper grid answers "which version is in force, and is its review due?"; the lower one is
/// the life of the selected document, version by version. The buttons follow the life of a
/// version — draft, review, approval, entry into force — and the service refuses any step taken out
/// of order, or by the author on their own text, with a message that says why.
/// </para>
/// </summary>
public sealed class QualityDocumentsView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly DocumentControlService _documents;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<QualityDocumentsView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _versions = UiTheme.Grid();
    private readonly Label _heading = new();
    private readonly Label _count = new();
    private readonly Label _versionsHeading = new();
    private readonly CheckBox _includeInactive = new();

    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _newDocument = UiTheme.PrimaryButton(string.Empty);
    // Secondary, as the other steps: a new document is the screen's one primary action.
    private readonly Button _newVersion = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _submit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _review = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _approve = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _makeEffective = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _open = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _acknowledge = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _withdraw = UiTheme.SecondaryButton(string.Empty);

    private readonly Label _message = new();

    private IReadOnlyList<ControlledDocumentRow> _rows = [];
    private IReadOnlyList<DocumentVersionRow> _versionRows = [];

    public QualityDocumentsView(DocumentControlService documents, IDocumentOutput output,
                                IUserDialogs dialogs, ILocalizer localizer,
                                ICultureManager cultureManager,
                                ILogger<QualityDocumentsView>? logger = null)
        : base(localizer, cultureManager)
    {
        _documents = documents;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Documents");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = QhPage.PagePadding;

        BuildDocumentColumns();
        BuildVersionColumns();

        var header = QhPage.Header(_heading, _count, _refresh, _newDocument);
        var toolbar = QhPage.Toolbar(QhPage.Chip(_includeInactive), _includeInactive);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 280,
            SplitterWidth = 14,
            BackColor = UiTheme.Surface
        };

        // The register, with what concerns the whole document at its foot: the file, reading it,
        // withdrawing it — and why it needs attention.
        var registerBody = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        _grid.Dock = DockStyle.Fill;
        registerBody.Controls.Add(_grid);
        registerBody.Controls.Add(new QhSelectionBar(_message, _open, _acknowledge, _withdraw));
        var registerCard = QhPage.Card(registerBody);
        registerCard.Dock = DockStyle.Fill;

        // The versions, with the steps of a version's life at their foot, in their order.
        var versionsBody = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        _versions.Dock = DockStyle.Fill;
        versionsBody.Controls.Add(_versions);
        versionsBody.Controls.Add(new QhSelectionBar(null, _newVersion, _submit, _review, _approve, _makeEffective));
        var versionsCard = QhPage.TitledCard(_versionsHeading, null, versionsBody);
        versionsCard.Dock = DockStyle.Fill;

        split.Panel1.Controls.Add(registerCard);
        split.Panel2.Controls.Add(versionsCard);

        Controls.Add(QhPage.Frame(2, header, toolbar, split));
    }

    private static void AddColumn(DataGridView grid, string name, int weight, Font? font = null,
                                  DataGridViewContentAlignment align =
                                      DataGridViewContentAlignment.MiddleLeft) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body, Alignment = align }
        });

    private void BuildDocumentColumns()
    {
        _grid.Columns.Clear();
        AddColumn(_grid, "reference", 10, UiTheme.Monospace);
        AddColumn(_grid, "title", 26);
        AddColumn(_grid, "kind", 11, UiTheme.Small);
        AddColumn(_grid, "process", 7, UiTheme.Small);
        AddColumn(_grid, "effective", 7, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(_grid, "effectiveFrom", 9, UiTheme.Monospace);
        AddColumn(_grid, "review", 9, UiTheme.Monospace);
        AddColumn(_grid, "latest", 12, UiTheme.Small);
        AddColumn(_grid, "owner", 11, UiTheme.Small);
        AddColumn(_grid, "read", 6, UiTheme.SmallBold, DataGridViewContentAlignment.MiddleCenter);
    }

    private void BuildVersionColumns()
    {
        _versions.Columns.Clear();
        AddColumn(_versions, "version", 6, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(_versions, "status", 10, UiTheme.SmallBold);
        AddColumn(_versions, "summary", 22);
        AddColumn(_versions, "file", 14, UiTheme.Small);
        AddColumn(_versions, "author", 11, UiTheme.Small);
        AddColumn(_versions, "created", 9, UiTheme.Monospace);
        AddColumn(_versions, "reviewer", 11, UiTheme.Small);
        AddColumn(_versions, "approver", 11, UiTheme.Small);
        AddColumn(_versions, "from", 8, UiTheme.Monospace);
        AddColumn(_versions, "withdrawn", 8, UiTheme.Monospace);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _includeInactive.CheckedChanged += async (_, _) => await LoadAsync();

        _grid.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadVersionsAsync());
        _grid.CellFormatting += OnDocumentFormatting;
        QhGridPills.Attach(_versions, "status", r => r.Tag is DocumentVersionRow version
            ? version.Status switch
            {
                DocumentVersionStatus.Effective => QhTone.Ok,
                DocumentVersionStatus.Approved or DocumentVersionStatus.Reviewed => QhTone.Info,
                DocumentVersionStatus.UnderReview => QhTone.Warn,
                DocumentVersionStatus.Draft => QhTone.Wait,
                _ => QhTone.Muted
            }
            : null);
        _versions.SelectionChanged += (_, _) => RefreshButtons();

        _newDocument.Click += async (_, _) => await CreateDocumentAsync();
        _newVersion.Click += async (_, _) => await AddVersionAsync();
        _submit.Click += async (_, _) => await StepAsync(
            v => _documents.SubmitForReviewAsync(SelectedDocument!.Id, v.VersionNumber), _submit.Text);
        _review.Click += async (_, _) => await StepAsync(
            v => _documents.ReviewAsync(SelectedDocument!.Id, v.VersionNumber), _review.Text);
        _approve.Click += async (_, _) => await StepAsync(
            v => _documents.ApproveAsync(SelectedDocument!.Id, v.VersionNumber), _approve.Text);
        _makeEffective.Click += async (_, _) => await MakeEffectiveAsync();
        _open.Click += async (_, _) => await OpenAsync();
        _acknowledge.Click += async (_, _) => await AcknowledgeAsync();
        _withdraw.Click += async (_, _) => await ToggleActiveAsync();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Quality.ManageDocuments, _newDocument, _newVersion, _submit, _review);
        PermissionGate.Require(Permissions.Quality.ApproveDocument, _approve, _makeEffective, _withdraw);
    }

    private void OnDocumentFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not ControlledDocumentRow row) return;

        if (!row.IsActive)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            return;
        }

        // Overdue review, or nothing in force at all: either way staff are working from a text the
        // laboratory has not currently vouched for. Said in the warning ink on the cell concerned.
        var column = _grid.Columns[e.ColumnIndex].Name;
        if ((column == "review" && row.IsReviewOverdue)
            || (column is "effective" or "effectiveFrom" && row.EffectiveVersion is null)
            || column == "reference" && (row.IsReviewOverdue || row.EffectiveVersion is null))
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
            e.CellStyle.Font = UiTheme.MonospaceBold;
        }
    }

    protected override void ApplyTexts()
    {
        _includeInactive.Text = T("Documents.IncludeInactive");
        _refresh.Text = T("Action.Refresh");
        _newDocument.Text = T("Documents.NewDocument");
        _newVersion.Text = T("Documents.NewVersion");
        _submit.Text = T("Documents.Submit");
        _review.Text = T("Documents.Review");
        _approve.Text = T("Documents.Approve");
        _makeEffective.Text = T("Documents.MakeEffective");
        _open.Text = T("Documents.OpenFile");
        _acknowledge.Text = T("Documents.Acknowledge");

        if (_grid.Columns.Count > 0)
        {
            _grid.Column("reference").HeaderText = T("Documents.Reference");
            _grid.Column("title").HeaderText = T("Documents.DocumentTitle");
            _grid.Column("kind").HeaderText = T("Documents.Kind");
            _grid.Column("process").HeaderText = T("Documents.Process");
            _grid.Column("effective").HeaderText = T("Documents.EffectiveVersion");
            _grid.Column("effectiveFrom").HeaderText = T("Documents.EffectiveFrom");
            _grid.Column("review").HeaderText = T("Documents.NextReview");
            _grid.Column("latest").HeaderText = T("Documents.LatestVersion");
            _grid.Column("owner").HeaderText = T("Documents.Owner");
            _grid.Column("read").HeaderText = T("Documents.Read");
        }

        if (_versions.Columns.Count > 0)
        {
            _versions.Column("version").HeaderText = T("Documents.Version");
            _versions.Column("status").HeaderText = T("Field.Status");
            _versions.Column("summary").HeaderText = T("Documents.ChangeSummary");
            _versions.Column("file").HeaderText = T("Documents.File");
            _versions.Column("author").HeaderText = T("Documents.Author");
            _versions.Column("created").HeaderText = T("Field.CreatedAt");
            _versions.Column("reviewer").HeaderText = T("Documents.Reviewer");
            _versions.Column("approver").HeaderText = T("Documents.Approver");
            _versions.Column("from").HeaderText = T("Documents.EffectiveFrom");
            _versions.Column("withdrawn").HeaderText = T("Documents.WithdrawnOn");
        }

        RenderHeading();
        if (_rows.Count > 0) Render();
        if (_versionRows.Count > 0) RenderVersions();
        RefreshButtons();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(null, ct);

    private ControlledDocumentRow? SelectedDocument => _grid.CurrentRow?.Tag as ControlledDocumentRow;

    /// <summary>The version the buttons act on: the one selected, else the newest.</summary>
    private DocumentVersionRow? SelectedVersion =>
        _versions.CurrentRow?.Tag as DocumentVersionRow
        ?? (_versionRows.Count > 0 ? _versionRows[^1] : null);

    private async Task LoadAsync(Guid? keep = null, CancellationToken ct = default)
    {
        try
        {
            keep ??= SelectedDocument?.Id;

            var result = await _documents.GetRegisterAsync(_includeInactive.Checked, ct);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _rows = result.Value;
            Render(keep);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the document register failed.");
            _dialogs.Warn(T("Message.UnexpectedError"));
        }
    }

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var index = _grid.Rows.Add(
                row.Reference,
                row.Title.For(Localizer.CurrentLanguage),
                T($"DocumentKind.{row.Kind}"),
                row.ProcessCode ?? string.Empty,
                row.EffectiveVersion is { } v ? $"v{v}" : "—",
                row.EffectiveFrom?.ToString("dd/MM/yy") ?? "—",
                row.NextReviewDue is { } due
                    ? row.IsReviewOverdue ? $"! {due:dd/MM/yy}" : due.ToString("dd/MM/yy")
                    : "—",
                row.LatestVersion is { } latest && latest != row.EffectiveVersion
                    ? $"v{latest} · {T($"DocumentVersionStatus.{row.LatestStatus}")}"
                    : string.Empty,
                row.OwnerName ?? string.Empty,
                !row.RequiresAcknowledgement || row.EffectiveVersion is null
                    ? string.Empty
                    : row.AcknowledgedByCurrentUser ? "✓" : "!");

            _grid.Rows[index].Tag = row;
            if (row.Id == keep) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
        RenderHeading();

        if (keep is null && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
        if (_grid.Rows.Count == 0)
        {
            _versionRows = [];
            RenderVersions();
        }
    }

    private void RenderHeading()
    {
        var overdue = _rows.Count(r => r.IsActive && r.IsReviewOverdue);
        var unread = _rows.Count(r => r.RequiresAcknowledgement && r.EffectiveVersion is not null
                                      && !r.AcknowledgedByCurrentUser);

        _heading.Text = T("Documents.Title");

        var text = Localizer.Format("QualityHr.Documents.Count", _rows.Count);
        if (overdue > 0) text += $" · {T("Documents.ReviewOverdue")} {overdue}";
        if (unread > 0) text += $" · {T("Documents.ToRead")} {unread}";
        _count.Text = text;
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            if (SelectedDocument is not { } document)
            {
                _versionRows = [];
                RenderVersions();
                return;
            }

            var result = await _documents.GetVersionsAsync(document.Id);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            // A slow answer for a document the operator has already moved off is discarded.
            if (SelectedDocument?.Id != document.Id) return;

            _versionRows = result.Value;
            RenderVersions();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading document versions failed.");
        }
    }

    private void RenderVersions()
    {
        _versions.SuspendLayout();
        _versions.Rows.Clear();

        foreach (var row in _versionRows)
        {
            var index = _versions.Rows.Add(
                $"v{row.VersionNumber}",
                T($"DocumentVersionStatus.{row.Status}"),
                row.ChangeSummary?.For(Localizer.CurrentLanguage) ?? string.Empty,
                row.FileName ?? string.Empty,
                row.AuthorName,
                row.CreatedAt.ToString("dd/MM/yy"),
                row.ReviewedByName ?? string.Empty,
                row.ApprovedByName ?? string.Empty,
                row.EffectiveFrom?.ToString("dd/MM/yy") ?? string.Empty,
                row.WithdrawnOn?.ToString("dd/MM/yy") ?? string.Empty);

            _versions.Rows[index].Tag = row;
        }

        _versions.ResumeLayout(performLayout: true);

        // The newest version is the one work happens on, so it is the one selected.
        if (_versions.Rows.Count > 0)
            _versions.CurrentCell = _versions.Rows[^1].Cells[0];

        _versionsHeading.Text = SelectedDocument is { } document
            ? $"{T("Documents.Versions")} — {document.Reference}"
            : T("Documents.Versions");

        RefreshButtons();
    }

    /// <summary>
    /// Enables only the step that fits the selected version's state. The service is still the
    /// judge — this only spares the operator a button that can do nothing.
    /// </summary>
    private void RefreshButtons()
    {
        var document = SelectedDocument;
        var version = SelectedVersion;

        _newVersion.Enabled = document is { IsActive: true };
        _submit.Enabled = version?.Status == DocumentVersionStatus.Draft;
        _review.Enabled = version?.Status == DocumentVersionStatus.UnderReview;
        _approve.Enabled = version?.Status is DocumentVersionStatus.UnderReview
                                            or DocumentVersionStatus.Reviewed;
        _makeEffective.Enabled = version?.Status == DocumentVersionStatus.Approved;
        _open.Enabled = version is { HasContent: true };
        _acknowledge.Enabled = document is { EffectiveVersion: not null, AcknowledgedByCurrentUser: false };
        _withdraw.Enabled = document is not null;
        _withdraw.Text = document is { IsActive: false } ? T("Documents.Reactivate") : T("Documents.Withdraw");

        _message.ForeColor = UiTheme.TextSecondary;
        _message.Text = document switch
        {
            null => string.Empty,
            { EffectiveVersion: null } => T("Documents.NoEffectiveVersionWarning"),
            { IsReviewOverdue: true } => T("Documents.ReviewOverdueWarning"),
            _ => string.Empty
        };
        if (document is { EffectiveVersion: null } or { IsReviewOverdue: true })
            _message.ForeColor = UiTheme.Abnormal;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task CreateDocumentAsync()
    {
        using var dialog = new ControlledDocumentDialog(Localizer, _dialogs);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        var result = await _documents.CreateAsync(dialog.Request);
        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, _newDocument.Text);
            return;
        }

        await LoadAsync(keep: result.Value);

        // A new document has no text yet; the next thing to do is attach its first draft.
        if (_dialogs.Confirm(T("Documents.AddFirstVersionQuestion"), _newDocument.Text))
            await AddVersionAsync();
    }

    private async Task AddVersionAsync()
    {
        if (SelectedDocument is not { } document) return;

        using var dialog = new DocumentVersionDialog(Localizer, _dialogs, document.Reference);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Content is null) return;

        var result = await _documents.AddDraftAsync(document.Id, dialog.ChangeSummary, dialog.Content,
                                                    dialog.FileName);
        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, _newVersion.Text);
            return;
        }

        await LoadAsync(keep: document.Id);
    }

    private async Task StepAsync(Func<DocumentVersionRow, Task<Result>> step, string title)
    {
        if (SelectedDocument is not { } document || SelectedVersion is not { } version) return;

        var result = await step(version);
        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, title);
            return;
        }

        await LoadAsync(keep: document.Id);
    }

    private async Task MakeEffectiveAsync()
    {
        if (SelectedDocument is not { } document || SelectedVersion is not { } version) return;

        using var dialog = new EffectiveDateDialog(Localizer, _dialogs,
                                                   $"{document.Reference} v{version.VersionNumber}",
                                                   document.EffectiveVersion);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _documents.MakeEffectiveAsync(document.Id, version.VersionNumber,
                                                         dialog.EffectiveFrom);
        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, _makeEffective.Text);
            return;
        }

        await LoadAsync(keep: document.Id);
    }

    private async Task OpenAsync()
    {
        if (SelectedDocument is not { } document || SelectedVersion is not { } version) return;

        try
        {
            var content = await _documents.GetContentAsync(document.Id, version.VersionNumber);
            if (content.IsFailure)
            {
                _dialogs.ShowFailure(content, _open.Text);
                return;
            }

            var extension = Path.GetExtension(content.Value.FileName).TrimStart('.');
            var opened = await _output.OpenAsync(content.Value.Content,
                                                 extension.Length == 0 ? "bin" : extension);
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _open.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Opening a controlled document failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _open.Text);
        }
    }

    private async Task AcknowledgeAsync()
    {
        if (SelectedDocument is not { EffectiveVersion: { } effective } document) return;

        if (!_dialogs.Confirm(T("Documents.AcknowledgeQuestion", document.Reference, effective),
                              _acknowledge.Text))
            return;

        var result = await _documents.AcknowledgeAsync(document.Id);
        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, _acknowledge.Text);
            return;
        }

        await LoadAsync(keep: document.Id);
    }

    private async Task ToggleActiveAsync()
    {
        if (SelectedDocument is not { } document) return;

        var reason = _dialogs.PromptForReason(
            T(document.IsActive ? "Documents.WithdrawPrompt" : "Documents.ReactivatePrompt",
              document.Reference),
            _withdraw.Text);
        if (reason is null) return;

        var result = await _documents.SetActiveAsync(document.Id, !document.IsActive, reason);
        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, _withdraw.Text);
            return;
        }

        await LoadAsync(keep: document.Id);
    }
}

/// <summary>Creating a controlled document: its identity in the register, before any text.</summary>
internal sealed class ControlledDocumentDialog : MasterDataEditor
{
    private readonly TextBox _reference, _titleFr, _titleAr, _titleEn, _process;
    private readonly ComboBox _kind;
    private readonly NumericUpDown _reviewPeriod;
    private readonly CheckBox _acknowledgement;

    internal CreateControlledDocumentRequest? Request { get; private set; }

    internal ControlledDocumentDialog(ILocalizer localizer, IUserDialogs dialogs)
        : base(localizer, dialogs, "Documents.NewDocument", null, 560, 400)
    {
        _reference = TextField("Documents.Reference", 20, 14, 180, null, latin: true, mono: true);
        _kind = Choice("Documents.Kind", 220, 14, 200, DocumentKind.Procedure, "DocumentKind");
        _process = TextField("Documents.Process", 440, 14, 100, null, latin: true);

        _titleFr = TextField("Documents.TitleFr", 20, 68, 520);
        _titleAr = TextField("Documents.TitleAr", 20, 122, 520, arabic: true);
        _titleEn = TextField("Documents.TitleEn", 20, 176, 520);

        _reviewPeriod = Number("Documents.ReviewPeriod", 20, 230, 120, 24, 120);
        _acknowledgement = Check("Documents.RequiresAcknowledgement", 180, 252, false);

        Notice(20, 290, 520, 46, "Documents.NewDocumentNotice");

        Buttons(352, 560);
        QhDialog.Dress(this, localizer, "QualityHr.Hint.Document");
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_reference.Text))
        {
            Dialogs.Warn(Localizer["Document_ReferenceRequired"], Text);
            _reference.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_titleFr.Text))
        {
            Dialogs.Warn(Localizer["Document_TitleRequired"], Text);
            _titleFr.Focus();
            return;
        }

        Request = new CreateControlledDocumentRequest
        {
            Reference = _reference.Text.Trim(),
            Title = new MultilingualText(_titleFr.Text.Trim(), Blank(_titleAr), Blank(_titleEn)),
            Kind = Selected<DocumentKind>(_kind),
            ProcessCode = Blank(_process),
            ReviewPeriodMonths = Math.Max(1, (int)_reviewPeriod.Value),
            RequiresAcknowledgement = _acknowledgement.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A new draft: the file and what changed. The file is read here, once.</summary>
internal sealed class DocumentVersionDialog : MasterDataEditor
{
    private readonly TextBox _summaryFr, _summaryAr, _file;

    internal MultilingualText? ChangeSummary { get; private set; }
    internal byte[]? Content { get; private set; }
    internal string FileName { get; private set; } = string.Empty;

    internal DocumentVersionDialog(ILocalizer localizer, IUserDialogs dialogs, string reference)
        : base(localizer, dialogs, "Documents.NewVersion", reference, 560, 300)
    {
        _file = TextField("Documents.File", 20, 14, 390, null, latin: true);
        _file.ReadOnly = true;

        var browse = UiTheme.SecondaryButton(localizer["Documents.Browse"]);
        browse.Bounds = new Rectangle(420, 31, 120, UiTheme.ButtonHeight);
        browse.Click += (_, _) => Browse();
        Controls.Add(browse);

        _summaryFr = TextField("Documents.ChangeSummaryFr", 20, 68, 520);
        _summaryAr = TextField("Documents.ChangeSummaryAr", 20, 122, 520, arabic: true);

        Notice(20, 180, 520, 46, "Documents.NewVersionNotice");

        Buttons(252, 560);
        QhDialog.Dress(this, localizer, "QualityHr.Hint.DocumentVersion");
    }

    private void Browse()
    {
        using var picker = new OpenFileDialog
        {
            Title = Localizer["Documents.Browse"],
            Filter = $"PDF, images, Office ({SafeDocumentFile.PickerPattern})|{SafeDocumentFile.PickerPattern}",
            CheckFileExists = true
        };

        if (picker.ShowDialog(this) == DialogResult.OK) _file.Text = picker.FileName;
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_file.Text) || !File.Exists(_file.Text))
        {
            Dialogs.Warn(Localizer["Document_ContentRequired"], Text);
            return;
        }

        var info = new FileInfo(_file.Text);
        if (SafeDocumentFile.ContentTypeFor(info.Name) is null)
        {
            Dialogs.Warn(Localizer["Document_FileType"], Text);
            return;
        }
        if (info.Length > DocumentControlService.MaximumContentBytes)
        {
            Dialogs.Warn(Localizer.Format("Document_ContentTooLarge",
                                          DocumentControlService.MaximumContentBytes / (1024 * 1024)),
                         Text);
            return;
        }

        try
        {
            Content = File.ReadAllBytes(info.FullName);
        }
        catch (IOException ex)
        {
            Dialogs.Warn($"{Localizer["Documents.FileUnreadable"]}\n{ex.Message}", Text);
            return;
        }

        FileName = info.Name;
        ChangeSummary = string.IsNullOrWhiteSpace(_summaryFr.Text)
            ? null
            : new MultilingualText(_summaryFr.Text.Trim(), Blank(_summaryAr));

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Choosing when an approved version comes into force.</summary>
internal sealed class EffectiveDateDialog : MasterDataEditor
{
    private readonly DateTimePicker _date;

    internal DateOnly EffectiveFrom => DateOnly.FromDateTime(_date.Value.Date);

    internal EffectiveDateDialog(ILocalizer localizer, IUserDialogs dialogs, string subject,
                                 int? replacedVersion)
        : base(localizer, dialogs, "Documents.MakeEffective", subject, 460, 220)
    {
        Label("Documents.EffectiveFrom", 20, 14);

        _date = new DateTimePicker
        {
            Bounds = new Rectangle(20, 32, 180, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            MinDate = DateTime.Today,
            Value = DateTime.Today
        };
        Controls.Add(_date);

        var notice = Notice(20, 74, 420, 70, "Documents.MakeEffectiveNotice");
        if (replacedVersion is { } previous)
            notice.Text = localizer.Format("Documents.ReplacesVersion", previous) + " " + notice.Text;

        Buttons(170, 460);
        QhDialog.Dress(this, localizer, "QualityHr.Hint.EffectiveDate");
    }

    protected override void Accept()
    {
        DialogResult = DialogResult.OK;
        Close();
    }
}
