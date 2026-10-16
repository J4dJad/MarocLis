using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Instruments;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The analyser interfaces: what is connected, what it is translating, and what came down the wire.
/// <para>
/// Three panes, because supervising an interface is three questions asked in sequence. Is the link
/// up? That is the top list, with the live state of the host beside the persisted last-connection
/// and last-error on each row. Is it translating correctly? That is the mapping table — the
/// interface's whole safety story, since an unmapped code is a dropped result and a mis-mapped one
/// puts a potassium value in a sodium field. And what actually arrived? That is the message log,
/// which holds every frame verbatim and can replay one.
/// </para>
/// <para>
/// The host runs on one workstation and the configuration is editable from any, so this screen says
/// which it is looking at: a link shown as stopped on a reception terminal means "not driven from
/// here", not "not working". Getting that wrong would have an operator disable a working interface.
/// </para>
/// </summary>
public sealed class InstrumentInterfaceView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly InstrumentInterfaceService _interfaces;
    private readonly MarocLIS.Application.Administration.MasterDataService _masterData;
    private readonly InstrumentLinkMonitor _monitor;
    private readonly InstrumentHostOptions _options;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<InstrumentInterfaceView>? _logger;

    private readonly Label _hostState = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);

    private readonly DataGridView _links = UiTheme.Grid();
    private readonly DataGridView _mappings = UiTheme.Grid();
    private readonly DataGridView _messages = UiTheme.Grid();

    private readonly Label _linkHeading = new();
    private readonly Label _mappingHeading = new();
    private readonly Label _messageHeading = new();

    private readonly Button _newLink = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editLink = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _toggleLink = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _editMappings = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _viewRaw = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _testFrame = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _replay = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _purgeLog = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();
    private readonly Label _pageTitle = new();

    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 5000 };

    private IReadOnlyList<InterfaceRow> _rows = [];

    public InstrumentInterfaceView(InstrumentInterfaceService interfaces,
                                   MarocLIS.Application.Administration.MasterDataService masterData,
                                   InstrumentLinkMonitor monitor, InstrumentHostOptions options,
                                   IUserDialogs dialogs, ICurrentUser currentUser,
                                   ILocalizer localizer, ICultureManager cultureManager,
                                   ILogger<InstrumentInterfaceView>? logger = null)
        : base(localizer, cultureManager)
    {
        _interfaces = interfaces;
        _masterData = masterData;
        _monitor = monitor;
        _options = options;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Instruments");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = AdminLayout.Page;

        BuildLinkColumns();
        BuildMappingColumns();
        BuildMessageColumns();

        foreach (var grid in new[] { _links, _mappings, _messages }) AdminLayout.ListGrid(grid);

        var outer = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 220,
            SplitterWidth = AdminLayout.Gap,
            BackColor = UiTheme.Surface
        };

        outer.Panel1.Controls.Add(AdminLayout.CardSection(_linkHeading, _links,
                                                          AdminLayout.Actions(_newLink, _toggleLink, _editLink)));

        var inner = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 620,
            SplitterWidth = AdminLayout.Gap,
            BackColor = UiTheme.Surface
        };

        // One primary action on the screen, the new interface; the others are the cards' own.
        inner.Panel1.Controls.Add(AdminLayout.CardSection(_mappingHeading, _mappings,
                                                          AdminLayout.Actions(null, _testFrame, _editMappings),
                                                          footer: true));
        inner.Panel2.Controls.Add(AdminLayout.CardSection(_messageHeading, _messages,
                                                          AdminLayout.Actions(null, _viewRaw, _purgeLog, _replay),
                                                          footer: true));

        outer.Panel2.Controls.Add(inner);

        _status.TextAlign = ContentAlignment.MiddleLeft;

        Controls.Add(outer);
        Controls.Add(AdminLayout.Note(_status, 26));
        Controls.Add(AdminLayout.PageHeader(_pageTitle, _hostState, AdminLayout.Actions(null, _refresh)));
    }

    private static void AddColumns(DataGridView grid,
                                    params (string Name, int Weight, Font? Font, bool Right)[] columns)
    {
        grid.Columns.Clear();

        foreach (var (name, weight, font, right) in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });
        }
    }

    private void BuildLinkColumns() => AddColumns(_links,
        ("instrument", 14, UiTheme.MonospaceBold, false),
        ("protocol", 10, UiTheme.Small, false),
        ("endpoint", 20, UiTheme.Monospace, false),
        ("direction", 11, UiTheme.Small, false),
        ("live", 11, UiTheme.SmallBold, false),
        ("mappings", 8, UiTheme.Monospace, true),
        ("today", 8, UiTheme.Monospace, true),
        ("lastmessage", 10, UiTheme.Monospace, false),
        ("flags", 14, UiTheme.SmallBold, false));

    private void BuildMappingColumns() => AddColumns(_mappings,
        ("code", 14, UiTheme.MonospaceBold, false),
        ("parameter", 22, UiTheme.Monospace, false),
        ("name", 24, null, false),
        ("units", 14, UiTheme.Monospace, false),
        ("conversion", 12, UiTheme.Monospace, true),
        ("flags", 16, UiTheme.SmallBold, false));

    private void BuildMessageColumns() => AddColumns(_messages,
        ("when", 16, UiTheme.Monospace, false),
        ("direction", 10, UiTheme.Small, false),
        ("specimens", 20, UiTheme.Monospace, false),
        ("counts", 12, UiTheme.Monospace, true),
        ("status", 16, UiTheme.SmallBold, false),
        ("issues", 26, UiTheme.Small, false));

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();

        _links.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadDetailAsync());
        _links.CellFormatting += OnLinkFormatting;
        _links.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _editLink.Enabled) await EditLinkAsync(); };

        _mappings.CellFormatting += OnMappingFormatting;

        AdminLayout.PillColumn(_links, "live", r =>
        {
            if (r.Tag is not InterfaceRow link || !_options.EnableInstrumentHost || !link.IsEnabled)
                return AdminLayout.Wait;
            return _monitor.For(link.Id)?.Status switch
            {
                InstrumentLinkStatus.Running => AdminLayout.Ok,
                InstrumentLinkStatus.Failed or InstrumentLinkStatus.Misconfigured => AdminLayout.Crit,
                InstrumentLinkStatus.Stopped => AdminLayout.Wait,
                _ => AdminLayout.Run
            };
        });

        AdminLayout.PillColumn(_messages, "status", r => r.Tag is MessageLogRow message
            ? message.Status switch
            {
                InstrumentMessageProcessingStatus.Imported => AdminLayout.Ok,
                InstrumentMessageProcessingStatus.Failed => AdminLayout.Crit,
                InstrumentMessageProcessingStatus.PartiallyImported
                    or InstrumentMessageProcessingStatus.AwaitingAcceptance => AdminLayout.Warn,
                InstrumentMessageProcessingStatus.Received => AdminLayout.Run,
                _ => AdminLayout.Wait
            }
            : null);
        _messages.CellFormatting += OnMessageFormatting;
        _messages.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _viewRaw.Enabled) await ViewRawAsync(); };

        _newLink.Click += async (_, _) => await EditLinkAsync(create: true);
        _editLink.Click += async (_, _) => await EditLinkAsync();
        _toggleLink.Click += async (_, _) => await ToggleAsync();
        _editMappings.Click += async (_, _) => await EditMappingsAsync();
        _testFrame.Click += async (_, _) =>
        {
            if (Selected is { } link)
                await TestFrameAsync(FindForm(), link, link.Protocol, link.Format, SelectedMessage);
        };
        _viewRaw.Click += async (_, _) => await ViewRawAsync();

        // The raw message carries patient results, so reading it takes the results right as well.
        PermissionGate.Require(Permissions.Results.View, _viewRaw);
        _replay.Click += async (_, _) => await ReplayAsync();
        _purgeLog.Click += async (_, _) => await PurgeLogAsync();

        // The live state lives in this process's memory, so it changes without anything being
        // saved. Polled rather than pushed: a five-second refresh is well inside what an operator
        // watching a link come up will tolerate, and it needs no cross-thread plumbing.
        _poll.Tick += (_, _) => RefreshHostState();
    }

    protected override void ApplyTexts()
    {
        _pageTitle.Text = T("Nav.Instruments");
        _refresh.Text = T("Action.Refresh");
        _newLink.Text = T("Interface.New");
        _editLink.Text = T("Interface.Edit");
        _editMappings.Text = T("Interface.EditMappings");
        _testFrame.Text = T("Interface.TestFrame");
        _viewRaw.Text = T("Interface.ViewRaw");
        _replay.Text = T("Interface.Replay");
        _purgeLog.Text = T("Interface.PurgeLog");

        _mappingHeading.Text = T("Interface.Mappings");
        _messageHeading.Text = T("Interface.MessageLog");

        if (_links.Columns.Count > 0)
        {
            _links.Column("instrument").HeaderText = T("Result.Instrument");
            _links.Column("protocol").HeaderText = T("Interface.Protocol");
            _links.Column("endpoint").HeaderText = T("Interface.Endpoint");
            _links.Column("direction").HeaderText = T("Interface.Direction");
            _links.Column("live").HeaderText = T("Interface.LiveState");
            _links.Column("mappings").HeaderText = T("Interface.Mappings");
            _links.Column("today").HeaderText = T("Interface.MessagesToday");
            _links.Column("lastmessage").HeaderText = T("Interface.LastMessage");
            _links.Column("flags").HeaderText = T("Field.Status");
        }

        if (_mappings.Columns.Count > 0)
        {
            _mappings.Column("code").HeaderText = T("Interface.InstrumentCode");
            _mappings.Column("parameter").HeaderText = T("Catalog.Parameter");
            _mappings.Column("name").HeaderText = T("Field.Name");
            _mappings.Column("units").HeaderText = T("Field.Unit");
            _mappings.Column("conversion").HeaderText = T("Interface.Conversion");
            _mappings.Column("flags").HeaderText = T("Field.Status");
        }

        if (_messages.Columns.Count > 0)
        {
            _messages.Column("when").HeaderText = T("Field.DateTime");
            _messages.Column("direction").HeaderText = T("Interface.MessageDirection");
            _messages.Column("specimens").HeaderText = T("Sample.Plural");
            _messages.Column("counts").HeaderText = T("Result.Plural");
            _messages.Column("status").HeaderText = T("Field.Status");
            _messages.Column("issues").HeaderText = T("Interface.Issues");
        }

        RefreshHostState();

        if (_rows.Count > 0) RenderLinks();
        RefreshActions();
    }

    /// <summary>
    /// States plainly whether this workstation drives the analysers, and why not when it does not.
    /// <para>
    /// The distinction matters operationally: a link shown stopped on a reception terminal means
    /// "not driven from here", and an operator who reads that as "broken" will disable a working
    /// interface or go looking for a fault on the analyser.
    /// </para>
    /// </summary>
    private void RefreshHostState()
    {
        if (!_options.EnableInstrumentHost)
        {
            _hostState.ForeColor = UiTheme.TextSecondary;
            _hostState.Text = T("Interface.HostNotThisWorkstation");
            return;
        }

        if (_monitor.HostError is { Length: > 0 } error)
        {
            _hostState.ForeColor = UiTheme.Critical;

            _hostState.Text = error == "Interface_ServiceAccountMissing"
                ? T("Interface.ServiceAccountMissing", _options.ServiceAccountUserName)
                : T("Interface.HostError", error);

            return;
        }

        var states = _monitor.Snapshot();

        var running = states.Count(s => s.Status == InstrumentLinkStatus.Running);
        var failed = states.Count(s => s.Status is InstrumentLinkStatus.Failed
                                                 or InstrumentLinkStatus.Misconfigured);

        _hostState.ForeColor = failed > 0 ? UiTheme.Critical
                                          : running > 0 ? UiTheme.Normal
                                                        : UiTheme.TextSecondary;

        _hostState.Text = T("Interface.HostRunning", running, failed);

        if (_links.Rows.Count > 0) _links.Invalidate();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _interfaces.GetInterfacesAsync(ct);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            RenderLinks();

            await LoadDetailAsync(ct);

            RefreshHostState();
            _poll.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the analyser interfaces failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    private void RenderLinks()
    {
        var language = Localizer.CurrentLanguage;
        var selected = Selected?.Id;

        _links.SuspendLayout();
        _links.Rows.Clear();

        foreach (var row in _rows)
        {
            var live = _monitor.For(row.Id);

            var flags = new List<string>(4);

            if (row.EquipmentBlockKey is { } blocked) flags.Add(T(blocked, row.InstrumentCode));
            if (!row.IsEnabled) flags.Add(T("Interface.Disabled"));
            if (row.IsUnusable) flags.Add(T("Interface.NoMappings"));
            if (row.AutoAcceptResults) flags.Add(T("Interface.AutoAccept"));
            if (row.LastError is { Length: > 0 }) flags.Add(T("Interface.HasError"));

            var index = _links.Rows.Add(
                $"{row.InstrumentCode}  {row.InstrumentName.For(language)}",
                T($"InstrumentProtocol.{row.Protocol}"),
                row.Endpoint,
                T($"InstrumentDirection.{row.Direction}"),
                DescribeLive(row, live),
                row.EnabledMappingCount == row.MappingCount
                    ? row.MappingCount.ToString()
                    : $"{row.EnabledMappingCount}/{row.MappingCount}",
                row.FailedToday > 0 ? $"{row.MessagesToday} ({row.FailedToday})"
                                    : row.MessagesToday.ToString(),
                row.LastMessageAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "—",
                string.Join(" · ", flags));

            _links.Rows[index].Tag = row;
        }

        _links.ResumeLayout(performLayout: true);

        if (selected is { } id)
        {
            foreach (DataGridViewRow row in _links.Rows)
            {
                if (row.Tag is InterfaceRow candidate && candidate.Id == id)
                {
                    row.Selected = true;
                    break;
                }
            }
        }

        if (_links.CurrentRow is null && _links.Rows.Count > 0) _links.SelectFirstRow();

        _linkHeading.Text = $"{T("Nav.Instruments")} — {_rows.Count}";

        RefreshActions();
    }

    private string DescribeLive(InterfaceRow row, InstrumentLinkState? live)
    {
        if (!_options.EnableInstrumentHost) return T("Interface.NotDrivenHere");
        if (!row.IsEnabled) return T("Interface.Disabled");

        return live is null
            ? T("Interface.Starting")
            : T($"InstrumentLinkStatus.{live.Status}");
    }

    private async Task LoadDetailAsync(CancellationToken ct = default)
    {
        _mappings.Rows.Clear();
        _messages.Rows.Clear();

        if (Selected is not { } row)
        {
            RefreshActions();
            return;
        }

        var language = Localizer.CurrentLanguage;

        var mappings = await _interfaces.GetMappingsAsync(row.Id, ct);

        if (mappings.IsFailure) { _dialogs.ShowFailure(mappings); return; }

        _mappings.SuspendLayout();

        foreach (var mapping in mappings.Value)
        {
            var flags = new List<string>(3);

            if (!mapping.IsEnabled) flags.Add(T("Interface.MappingDisabled"));
            if (mapping.UnitMismatchWithoutConversion) flags.Add(T("Interface.UnitMismatch"));
            if (mapping.CodedWithoutMap) flags.Add(T("Interface.NoCodeMap"));

            var index = _mappings.Rows.Add(
                mapping.InstrumentTestCode,
                $"{mapping.AnalysisCode}/{mapping.ParameterCode}",
                mapping.ParameterName.For(language),
                mapping.InstrumentUnit is { Length: > 0 } instrumentUnit
                    && !string.Equals(instrumentUnit, mapping.CatalogueUnit,
                                       StringComparison.OrdinalIgnoreCase)
                    ? $"{instrumentUnit} → {mapping.CatalogueUnit ?? "—"}"
                    : mapping.CatalogueUnit ?? string.Empty,
                mapping.ConversionFactor is { } factor
                    ? $"×{factor:0.####}{(mapping.ConversionOffset is { } offset and not 0m ? $" {offset:+0.###;-0.###}" : string.Empty)}"
                    : string.Empty,
                string.Join(" · ", flags));

            _mappings.Rows[index].Tag = mapping;
        }

        _mappings.ResumeLayout(performLayout: true);

        _mappingHeading.Text = $"{T("Interface.Mappings")} — {row.InstrumentCode}";

        var messages = await _interfaces.GetMessagesAsync(row.Id, 200, ct);

        if (messages.IsFailure) { _dialogs.ShowFailure(messages); return; }

        _messages.SuspendLayout();

        foreach (var message in messages.Value)
        {
            var index = _messages.Rows.Add(
                message.OccurredAt.ToLocalTime().ToString("dd/MM HH:mm:ss"),
                T($"MessageDirection.{message.Direction}"),
                message.SpecimenIds ?? string.Empty,
                message.ResultCount is { } total
                    ? $"{message.ImportedCount ?? 0}/{total}"
                    : string.Empty,
                T($"InstrumentMessageProcessingStatus.{message.Status}")
                + (message.WasReplayed ? $" · {T("Interface.Replayed")}" : string.Empty),
                FormatIssues(message.Issues));

            _messages.Rows[index].Tag = message;
        }

        _messages.ResumeLayout(performLayout: true);

        _messageHeading.Text = $"{T("Interface.MessageLog")} — {messages.Value.Count}";

        RefreshActions();
    }

    /// <summary>
    /// Issues are stored as resource keys with a tab-separated detail, so the log reads in whichever
    /// language the operator is using rather than in whatever language the sink was written in.
    /// </summary>
    private string FormatIssues(string? issues)
    {
        if (string.IsNullOrWhiteSpace(issues)) return string.Empty;

        var lines = issues.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var parts = line.Trim().Split('\t', 2);

                var text = Localizer[parts[0]];

                if (parts.Length < 2) return text;

                // A message that names its value ("La valeur {0} est…") takes the detail in place;
                // the others get it after, in brackets.
                return text.Contains("{0}", StringComparison.Ordinal)
                    ? text.Replace("{0}", parts[1], StringComparison.Ordinal)
                    : $"{text} ({parts[1]})";
            });

        return string.Join(" · ", lines);
    }

    // ---- Formatting ----------------------------------------------------------------------------

    private void OnLinkFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _links.Rows.Count) return;
        if (_links.Rows[e.RowIndex].Tag is not InterfaceRow row) return;

        if (!row.IsEnabled)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            return;
        }

        var live = _monitor.For(row.Id);

        if (live?.Status is InstrumentLinkStatus.Failed or InstrumentLinkStatus.Misconfigured
            || row.IsUnusable || row.EquipmentBlockKey is not null)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.HasRecentFailures || row.LastError is { Length: > 0 })
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
        else if (live?.Status == InstrumentLinkStatus.Running)
        {
            e.CellStyle!.ForeColor = UiTheme.Normal;
        }
    }

    private void OnMappingFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _mappings.Rows.Count) return;
        if (_mappings.Rows[e.RowIndex].Tag is not MappingRow mapping) return;

        if (!mapping.IsEnabled) e.CellStyle!.ForeColor = UiTheme.TextMuted;
        else if (mapping.UnitMismatchWithoutConversion)
        {
            // The one mapping fault that produces a plausible wrong number rather than an error.
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (mapping.CodedWithoutMap)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnMessageFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _messages.Rows.Count) return;
        if (_messages.Rows[e.RowIndex].Tag is not MessageLogRow message) return;

        switch (message.Status)
        {
            case InstrumentMessageProcessingStatus.Failed:
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
                break;

            case InstrumentMessageProcessingStatus.PartiallyImported
                 or InstrumentMessageProcessingStatus.AwaitingAcceptance:
                e.CellStyle!.ForeColor = UiTheme.Abnormal;
                break;

            case InstrumentMessageProcessingStatus.Imported:
                e.CellStyle!.ForeColor = UiTheme.Normal;
                break;

            case InstrumentMessageProcessingStatus.Ignored:
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
                break;
        }
    }

    // ---- Selection and actions -----------------------------------------------------------------

    private InterfaceRow? Selected => _links.CurrentRow?.Tag as InterfaceRow;
    private MessageLogRow? SelectedMessage => _messages.CurrentRow?.Tag as MessageLogRow;

    private void RefreshActions()
    {
        var configure = _currentUser.Has(Permissions.Instruments.Configure);

        _newLink.Enabled = configure;
        _editLink.Enabled = configure && Selected is not null;
        _editMappings.Enabled = configure && Selected is not null;
        _testFrame.Enabled = configure && Selected is not null
                             && _currentUser.Has(Permissions.Results.View);

        _toggleLink.Enabled = Selected is not null
                              && _currentUser.Has(Permissions.Instruments.StartStop);

        _toggleLink.Text = Selected is { IsEnabled: true } ? T("Interface.Disable")
                                                            : T("Interface.Enable");

        _viewRaw.Enabled = SelectedMessage is not null;

        // Disabled is a hint, not the control: the service re-checks the permission and the sink
        // re-checks the status. Clicking a stale row gets a refusal with a reason, not a wrong write.
        _replay.Enabled = SelectedMessage is { CanReplay: true }
                          && _currentUser.Has(Permissions.Instruments.ReplayMessages);

        // Not tied to a selected row: the purge acts on the whole log, by age, and is available
        // whether or not the operator has clicked a line.
        _purgeLog.Enabled = _currentUser.Has(Permissions.Administration.RunMaintenance);
    }

    private async Task EditLinkAsync(bool create = false)
    {
        var existing = create ? null : Selected;

        if (!create && existing is null) return;

        (Guid Id, string Code)? instrument = null;

        if (create)
        {
            // The analyser is chosen first, and an empty choice is reported here rather than after
            // the operator has described a whole frame configuration for nothing.
            var instruments = await _masterData.GetInstrumentsAsync();

            if (instruments.IsFailure) { _dialogs.ShowFailure(instruments); return; }

            var free = instruments.Value
                .Where(i => !i.HasInterfaceConfigured && i.Kind == EquipmentKind.Analyser)
                .OrderBy(i => i.Code)
                .ToList();

            if (free.Count == 0)
            {
                _dialogs.Warn(T("Interface_NoInstrumentAvailable"), T("Interface.New"));
                return;
            }

            using var picker = new InterfaceInstrumentDialog(Localizer, _dialogs, free);

            if (picker.ShowDialog(FindForm()) != DialogResult.OK || picker.Choice is null) return;

            instrument = picker.Choice;
        }

        using var dialog = new InstrumentInterfaceDialog(
            Localizer, _dialogs, existing, instrument,
            existing is null || !_currentUser.Has(Permissions.Results.View)
                ? null
                : (owner, protocol, format) => _ = TestFrameAsync(owner, existing, protocol, format, null));

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _interfaces.SaveInterfaceAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, dialog.Text); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Interface.SavedTakesEffect", _options.ConfigurationPollSeconds);

        await ActivateAsync();
    }

    private async Task EditMappingsAsync()
    {
        if (Selected is not { } row) return;

        var current = await _interfaces.GetMappingsAsync(row.Id);

        if (current.IsFailure) { _dialogs.ShowFailure(current); return; }

        using var dialog = new InstrumentMappingDialog(Localizer, _dialogs, row, current.Value,
                                                     search => _interfaces.GetMappableParametersAsync(search));

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _interfaces.SaveMappingsAsync(row.Id, dialog.Mappings);

        if (result.IsFailure) { _dialogs.ShowFailure(result, dialog.Text); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await LoadDetailAsync();
        await ActivateAsync();
    }

    /// <summary>
    /// Opens "Tester une trame" for a link, with the protocol and format to try — the saved ones from
    /// this screen, or the ones being edited from the configuration dialog. A selected inbound frame
    /// is loaded and read straight away, which is the usual question: why did this one not import?
    /// </summary>
    private async Task TestFrameAsync(IWin32Window? owner, InterfaceRow link,
                                      InstrumentProtocol protocol, MessageFormatSettings format,
                                      MessageLogRow? message)
    {
        var mappings = await _interfaces.GetMappingsAsync(link.Id);
        if (mappings.IsFailure) { _dialogs.ShowFailure(mappings); return; }

        var parameters = mappings.Value
            .GroupBy(m => m.TestParameterId)
            .ToDictionary(g => g.Key, g => g.First().ParameterCode);

        string? initial = null;

        if (message is { Direction: MessageDirection.Inbound })
        {
            var raw = await _interfaces.GetRawMessageAsync(message.Id);
            if (raw.IsSuccess) initial = raw.Value;
        }

        async Task<string?> LoadLastAsync()
        {
            var recent = await _interfaces.GetMessagesAsync(link.Id, 50);
            if (recent.IsFailure) return null;

            var inbound = recent.Value.FirstOrDefault(
                m => m.Direction == MessageDirection.Inbound && (m.ResultCount ?? 0) > 0);
            if (inbound is null) return null;

            var raw = await _interfaces.GetRawMessageAsync(inbound.Id);
            return raw.IsSuccess ? raw.Value : null;
        }

        using var dialog = new InstrumentFrameTestDialog(
            Localizer, _dialogs,
            $"{link.InstrumentCode} · {T($"InstrumentProtocol.{protocol}")}",
            frame => _interfaces.PreviewMessageAsync(link.Id, protocol, format, frame),
            LoadLastAsync, parameters, initial);

        dialog.ShowDialog(owner);
    }

    /// <summary>
    /// Enables or disables a link, against a recorded reason.
    /// <para>
    /// The reason is not bureaucracy: taking an analyser interface off the air means results stop
    /// arriving, and the next shift needs to know whether that was deliberate. The commonest answer
    /// is "analyser in maintenance", which is exactly the fact worth keeping.
    /// </para>
    /// </summary>
    private async Task ToggleAsync()
    {
        if (Selected is not { } row) return;

        var enabling = !row.IsEnabled;

        var reason = _dialogs.PromptForReason(
            T(enabling ? "Interface.EnablePrompt" : "Interface.DisablePrompt", row.InstrumentCode),
            T(enabling ? "Interface.Enable" : "Interface.Disable"));

        if (reason is null) return;

        var result = await _interfaces.SetEnabledAsync(row.Id, enabling, reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Nav.Instruments")); return; }

        _status.ForeColor = enabling ? UiTheme.Normal : UiTheme.Abnormal;
        _status.Text = T("Interface.SavedTakesEffect", _options.ConfigurationPollSeconds);

        await ActivateAsync();
    }

    /// <summary>
    /// Shows the frame exactly as it arrived.
    /// <para>
    /// Verbatim and not prettified. When a clinician disputes a result this is the evidence of what
    /// the analyser transmitted, and a rendering of it — reflowed, re-encoded, control characters
    /// stripped — would no longer be evidence of anything.
    /// </para>
    /// </summary>
    private async Task ViewRawAsync()
    {
        if (SelectedMessage is not { } message) return;

        var result = await _interfaces.GetRawMessageAsync(message.Id);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Interface.ViewRaw")); return; }

        using var dialog = new InstrumentRawMessageDialog(Localizer, message, result.Value,
                                                           FormatIssues(message.Issues));

        dialog.ShowDialog(FindForm());
    }

    /// <summary>
    /// Re-imports the selected frame.
    /// <para>
    /// Confirmed first, and the confirmation says what it will do: a replay writes the analyser's
    /// values into a patient file. It is the deliberate counterpart of leaving auto-acceptance off,
    /// so the person clicking it has to be the person who decided the transmission is sound.
    /// </para>
    /// </summary>
    private async Task ReplayAsync()
    {
        if (SelectedMessage is not { } message) return;

        if (!message.CanReplay)
        {
            _dialogs.Warn(T("Interface.ReplayNotPossible"), T("Interface.Replay"));
            return;
        }

        if (!_dialogs.Confirm(T("Interface.ReplayConfirm"), T("Interface.Replay"))) return;

        var result = await _interfaces.ReplayMessageAsync(message.Id);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Interface.Replay")); return; }

        // Zero stored is not a failure: the frame may have been re-parsed and every value still
        // refused. Saying "done" without the count would hide that.
        _dialogs.Info(
            result.Value == 0
                ? T("Interface.ReplayNothing")
                : Localizer.Format("Interface.ReplayDone", result.Value),
            T("Interface.Replay"));

        // Reloaded rather than patched in place: the replay changes the row's status, its imported
        // count and its replayed stamp, and re-reading is the only way the grid shows what the
        // database now holds.
        await LoadDetailAsync();
    }

    /// <summary>
    /// Purges frames past the configured retention.
    /// <para>
    /// The scope is read and shown <em>before</em> the confirmation, with the cutoff date and the
    /// count of frames being kept back because they are still replayable. A purge that reports what
    /// it did only afterwards is a purge nobody could have consented to — and the retained count is
    /// usually the interesting number, because it is unfinished work the laboratory has accumulated.
    /// </para>
    /// </summary>
    private async Task PurgeLogAsync()
    {
        var census = await _interfaces.GetMessageLogCensusAsync();

        if (census.IsFailure) { _dialogs.ShowFailure(census, T("Interface.PurgeLog")); return; }

        var scope = census.Value;

        if (scope.Purgeable == 0)
        {
            // Said plainly rather than shown as a no-op: an operator who clicks and sees nothing
            // happen assumes the button is broken, not that the log is within retention.
            _dialogs.Info(T("Interface.PurgeLogNothing", _options.MessageLogRetentionDays),
                          T("Interface.PurgeLog"));

            if (scope.RetainedReplayable > 0)
                _dialogs.Warn(T("Interface.PurgeLogRetained", scope.RetainedReplayable),
                              T("Interface.PurgeLog"));

            return;
        }

        var cutoff = scope.OldestPurgeable is { } oldest
            ? oldest.ToLocalTime().ToString("dd/MM/yyyy")
            : string.Empty;

        if (!_dialogs.Confirm(
                T("Interface.PurgeLogScope", scope.Purgeable, cutoff, scope.RetainedReplayable),
                T("Interface.PurgeLog")))
            return;

        var reason = _dialogs.PromptForReason(T("Interface.PurgeLogPrompt"),
                                               T("Interface.PurgeLog"));

        if (string.IsNullOrWhiteSpace(reason)) return;

        var result = await _interfaces.PurgeMessageLogAsync(reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Interface.PurgeLog")); return; }

        _dialogs.Info(T("Interface.PurgeLogDone", result.Value), T("Interface.PurgeLog"));

        if (scope.RetainedReplayable > 0)
            _dialogs.Warn(T("Interface.PurgeLogRetained", scope.RetainedReplayable),
                          T("Interface.PurgeLog"));

        await LoadDetailAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
        }

        base.Dispose(disposing);
    }
}
