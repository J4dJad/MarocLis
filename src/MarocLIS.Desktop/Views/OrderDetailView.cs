using System.ComponentModel;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// One order, in full, with everything that can still be done to it.
/// <para>
/// This is the screen an operator reaches for when something has changed since the counter: the
/// prescriber telephones to add a TSH, the clerk typed the wrong prescriber, an analyser gave an
/// implausible value and the analysis must be re-run, the patient left before the draw. Without it
/// the only way to record any of those is to register a second order — which splits one episode of
/// care across two file numbers, bills the draw twice, and makes the turnaround figures fiction.
/// </para>
/// <para>
/// The layout follows the questions in the order they get asked. Who is this and what was asked
/// for, at the top. Which analyses, in what state, in the middle — because "why is the TSH not on
/// the report?" is the commonest question and a cancelled row with its reason is the answer. The
/// tubes below that, because the second commonest question is "where is the tube?".
/// </para>
/// <para>
/// The site's recent files are listed on the left, newest first, and the screen opens on the
/// latest of them. It used to open on a lone search box that did nothing until an exact accession
/// number was typed — which read as an empty, broken screen, and sent operators who had only a
/// patient's name back to the patient screen to find the number. The box now filters the list by
/// number, name, CIN or file number as the operator types; Enter still resolves an exact accession
/// number or tube barcode, which is what a scanner sends.
/// </para>
/// </summary>
public sealed class OrderDetailView : LocalizedView
{
    private readonly OrderAmendmentService _orders;
    private readonly ICatalogQueries _catalogQueries;
    private readonly ISampleLabelRenderer _labels;
    private readonly ILabelPrinter _labelPrinter;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<OrderDetailView>? _logger;

    private readonly TextBox _search = UiTheme.Input(240);
    private readonly Label _accession = new();
    private readonly Label _statusBadge = new();
    private readonly Label _patient = new();
    private readonly Label _prescriber = new();
    private readonly Label _coverage = new();
    private readonly Label _clinical = new();
    private readonly Label _cancellation = new();

    private readonly DataGridView _items = UiTheme.Grid();
    private readonly DataGridView _samples = UiTheme.Grid();

    private readonly Button _addAnalyses = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancelItem = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _repeatItem = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _editHeader = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _cancelOrder = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _printLabels = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _printReport = UiTheme.SecondaryButton(string.Empty);
    private readonly Application.Samples.SampleWorkflowService _samplesWorkflow;
    private readonly Application.Reports.ReportReleaseService _reports;
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private readonly DataGridView _list = UiTheme.Grid();
    private readonly Label _listCount = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };

    private OrderDetail? _detail;
    private IReadOnlyList<OrderListRow> _rows = [];

    /// <summary>
    /// Set while the list is being repopulated or its selection moved in code, so that the
    /// selection events this raises are not taken for the operator picking a row — which would
    /// reload, and log a consultation of, a file nobody asked to open.
    /// </summary>
    private bool _populatingList;

    // One load of each kind in flight at a time: a newer filter or a quicker arrow key supersedes
    // the older request, which would otherwise finish last and overwrite what is on screen.
    private CancellationTokenSource? _listLoad;
    private CancellationTokenSource? _detailLoad;

    /// <summary>
    /// Set by the caller before activation, to open a particular file. The view also has its own
    /// list and search box, so it is usable from the sidebar without a prior selection.
    /// <para>
    /// Hidden from designer serialization: the screens here are built in code, so nothing would
    /// consume a designer-written value.
    /// </para>
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Guid? OrderId { get; set; }

    public OrderDetailView(OrderAmendmentService orders, ICatalogQueries catalogQueries,
                           Application.Samples.SampleWorkflowService samplesWorkflow,
                           Application.Reports.ReportReleaseService reports,
                           ISampleLabelRenderer labels, ILabelPrinter labelPrinter,
                           IDocumentOutput documents, IUserDialogs dialogs,
                           ICurrentUser currentUser, IClock clock,
                           ILocalizer localizer, ICultureManager cultureManager,
                           ILogger<OrderDetailView>? logger = null)
        : base(localizer, cultureManager)
    {
        _orders = orders;
        _catalogQueries = catalogQueries;
        _samplesWorkflow = samplesWorkflow;
        _reports = reports;
        _labels = labels;
        _labelPrinter = labelPrinter;
        _documents = documents;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title =>
        _detail is null ? T("Order.Title") : $"{T("Order.Title")} {_detail.AccessionNumber}";

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        // --- the list of files, on the left ----------------------------------------------------

        var searchBar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = UiTheme.Surface };

        _search.Tag = UiTags.LatinValue;
        _search.Font = UiTheme.Body;
        _search.Location = new Point(0, 4);

        _refresh.Size = new Size(110, UiTheme.ButtonHeight - 4);

        _listCount.Font = UiTheme.Small;
        _listCount.ForeColor = UiTheme.TextMuted;
        _listCount.AutoSize = false;
        _listCount.AutoEllipsis = true;
        _listCount.Location = new Point(0, 32);
        _listCount.Height = 18;

        // Sized from the bar rather than fixed: the bar is a share of the window, and a fixed-width
        // box either leaves the refresh button hanging in mid-air or pushes it out of sight.
        searchBar.Resize += (_, _) =>
        {
            var width = searchBar.ClientSize.Width;
            _refresh.Location = new Point(Math.Max(0, width - _refresh.Width), 3);
            _search.Width = Math.Max(80, width - _refresh.Width - 8);
            _listCount.Width = width;
        };

        searchBar.Controls.AddRange([_search, _refresh, _listCount]);

        BuildListColumns();

        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(searchBar);

        // --- header ----------------------------------------------------------------------------

        var header = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = UiTheme.Surface };

        _accession.Font = UiTheme.PageTitle;
        _accession.ForeColor = UiTheme.TextPrimary;
        _accession.AutoSize = false;
        _accession.Bounds = new Rectangle(0, 0, 300, 28);

        _statusBadge.Font = UiTheme.SmallBold;
        _statusBadge.AutoSize = false;
        _statusBadge.TextAlign = ContentAlignment.MiddleCenter;
        _statusBadge.Bounds = new Rectangle(306, 4, 150, 20);

        _cancellation.Font = UiTheme.SmallBold;
        _cancellation.ForeColor = UiTheme.Critical;
        _cancellation.AutoSize = false;
        _cancellation.Bounds = new Rectangle(466, 4, 400, 20);

        _patient.Font = UiTheme.SectionTitle;
        _patient.ForeColor = UiTheme.Accent;
        _patient.AutoSize = false;
        _patient.Bounds = new Rectangle(0, 32, 700, 22);

        _prescriber.Font = UiTheme.Small;
        _prescriber.ForeColor = UiTheme.TextSecondary;
        _prescriber.AutoSize = false;
        _prescriber.Bounds = new Rectangle(0, 56, 700, 18);

        _coverage.Font = UiTheme.Small;
        _coverage.ForeColor = UiTheme.TextSecondary;
        _coverage.AutoSize = false;
        _coverage.Bounds = new Rectangle(0, 76, 700, 18);

        _clinical.Font = UiTheme.Small;
        _clinical.ForeColor = UiTheme.TextSecondary;
        _clinical.AutoSize = false;
        _clinical.Bounds = new Rectangle(0, 96, 700, 18);

        Label[] stretched = [_cancellation, _patient, _prescriber, _coverage, _clinical];

        // One line each, stretched to the panel and cut with an ellipsis: the detail now shares the
        // screen with the list, and a line laid out for the full width would run under the edge.
        foreach (var label in stretched) label.AutoEllipsis = true;

        header.Resize += (_, _) =>
        {
            foreach (var label in stretched)
                label.Width = Math.Max(40, header.ClientSize.Width - label.Left);
        };

        header.Controls.AddRange([_accession, _statusBadge, _patient, _prescriber, _coverage,
                                  _clinical, _cancellation]);

        // --- items over tubes ------------------------------------------------------------------

        BuildItemColumns();
        BuildSampleColumns();

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 300,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        split.Panel1.Controls.Add(_items);
        split.Panel2.Controls.Add(_samples);

        // --- actions ---------------------------------------------------------------------------

        // A flow rather than fixed positions, wrapping onto a second row when the detail panel is
        // narrow: seven buttons laid out for the full width would otherwise fall off its edge.
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 6, 0, 0),
            BackColor = UiTheme.Surface
        };

        Button[] buttons = [_addAnalyses, _cancelItem, _repeatItem, _editHeader, _cancelOrder,
                            _printLabels, _printReport];

        foreach (var button in buttons)
        {
            button.Size = new Size(button == _repeatItem ? 170 : 190, UiTheme.ButtonHeight);
            button.Margin = new Padding(0, 4, 10, 0);
        }

        actions.Controls.AddRange(buttons);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        _status.Dock = DockStyle.Bottom;
        _status.Height = 22;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        var detailHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        detailHost.Controls.Add(split);
        detailHost.Controls.Add(_status);
        detailHost.Controls.Add(actions);
        detailHost.Controls.Add(header);

        // --- list beside detail ----------------------------------------------------------------

        // About a third for the list: enough for the number, the name and the state, which is all
        // it takes to pick a file, and the rest for the file itself.
        var outer = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 390,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        outer.Panel1.Padding = new Padding(0, 0, UiTheme.GutterTight, 0);
        outer.Panel1.Controls.Add(listHost);
        outer.Panel2.Padding = new Padding(UiTheme.GutterTight, 0, 0, 0);
        outer.Panel2.Controls.Add(detailHost);

        Controls.Add(outer);
    }

    private void BuildListColumns()
    {
        void Add(string name, int weight, Font? font = null, bool right = false)
            => _list.Columns.Add(new DataGridViewTextBoxColumn
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

        Add("accession", 18, UiTheme.Monospace);
        Add("patient", 28);
        Add("registered", 17, UiTheme.Monospace);
        Add("status", 17, UiTheme.Small);
        Add("priority", 12, UiTheme.Small);
        Add("count", 8, UiTheme.Monospace, right: true);
    }

    private void BuildItemColumns()
    {
        void Add(string name, int weight, Font? font = null, bool right = false)
            => _items.Columns.Add(new DataGridViewTextBoxColumn
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

        Add("code", 9, UiTheme.MonospaceBold);
        Add("name", 26);
        Add("department", 8, UiTheme.Small);
        Add("status", 12, UiTheme.SmallBold);
        Add("progress", 8, UiTheme.Monospace, right: true);
        Add("barcode", 11, UiTheme.Monospace);
        Add("price", 9, UiTheme.Monospace, right: true);
        Add("share", 9, UiTheme.Monospace, right: true);
        Add("note", 20, UiTheme.Small);
    }

    private void BuildSampleColumns()
    {
        void Add(string name, int weight, Font? font = null, bool right = false)
            => _samples.Columns.Add(new DataGridViewTextBoxColumn
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

        Add("sequence", 5, UiTheme.Monospace, right: true);
        Add("barcode", 14, UiTheme.MonospaceBold);
        Add("specimen", 18);
        Add("container", 14, UiTheme.Small);
        Add("status", 13, UiTheme.SmallBold);
        Add("collected", 12, UiTheme.Monospace);
        Add("received", 12, UiTheme.Monospace);
        Add("storage", 12, UiTheme.Monospace);
    }

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            // Down moves into the list, so the keyboard alone gets from a typed name to a file.
            if (e.KeyCode == Keys.Down && _list.Rows.Count > 0)
            {
                e.SuppressKeyPress = true;
                _list.Focus();
                return;
            }

            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _debounce.Stop();
            await SearchAsync();
        };

        // Debounced like the patient search: a query per keystroke while a name is still being
        // typed would be wasted, and 300 ms is short enough to feel immediate.
        _search.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };

        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await LoadListAsync();
        };

        _list.SelectionChanged += async (_, _) => await OpenSelectedAsync();
        _list.CellFormatting += OnListFormatting;

        _refresh.Click += async (_, _) => await ActivateAsync();

        _addAnalyses.Click += async (_, _) => await AddAnalysesAsync();
        _cancelItem.Click += async (_, _) => await CancelItemAsync();
        _repeatItem.Click += async (_, _) => await RepeatItemAsync();
        _editHeader.Click += async (_, _) => await EditHeaderAsync();
        _cancelOrder.Click += async (_, _) => await CancelOrderAsync();
        _printLabels.Click += async (_, _) => await ReprintLabelsAsync();
        _printReport.Click += async (_, _) => await OpenReportAsync();

        _items.SelectionChanged += (_, _) => RefreshActions();
        _items.CellFormatting += OnItemFormatting;
        _samples.CellFormatting += OnSampleFormatting;
    }

    private void OnListFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _list.Rows.Count) return;
        if (_list.Rows[e.RowIndex].Tag is not OrderListRow row) return;

        var column = _list.Columns[e.ColumnIndex].Name;

        // Cancelled files stay listed, greyed: "what happened to that file?" is a question this
        // screen answers, and a file filtered out of the list cannot be opened to answer it.
        if (row.Status == LabOrderStatus.Cancelled)
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        else if (column == "status")
            e.CellStyle!.ForeColor = UiTheme.ForOrderStatus(row.Status);
        else if (column == "priority")
            e.CellStyle!.ForeColor = UiTheme.ForPriority(row.Priority);
    }

    private void OnItemFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _items.Rows.Count) return;
        if (_items.Rows[e.RowIndex].Tag is not OrderItemDetail item) return;

        if (item.IsCancelled)
        {
            // Struck through rather than hidden: a cancelled analysis and its reason are the
            // answer to "why is this not on the report?".
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            // CellStyle.Font is null when the cell inherits the grid's font, which is the normal
            // case here — and Font's prototype constructor throws on a null prototype.
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Strikeout);
            return;
        }

        if (item.Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported)
            e.CellStyle!.ForeColor = UiTheme.Normal;
        else if (item.RunCount > 1)
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        else if (item.IsOverdueAt(_clock.Now))
            e.CellStyle!.ForeColor = UiTheme.Overdue;
    }

    private void OnSampleFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _samples.Rows.Count) return;
        if (_samples.Rows[e.RowIndex].Tag is not OrderSampleDetail sample) return;

        if (sample.Status is SampleStatus.Rejected or SampleStatus.Discarded)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (sample.Status == SampleStatus.AwaitingCollection)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
    }

    protected override void ApplyTexts()
    {
        _search.PlaceholderText = T("Order.SearchPlaceholder");

        _addAnalyses.Text = T("Order.AddAnalyses");
        _cancelItem.Text = T("Order.CancelAnalysis");
        _repeatItem.Text = T("Order.RepeatAnalysis");
        _editHeader.Text = T("Order.EditHeader");
        _cancelOrder.Text = T("Order.CancelOrder");
        _printLabels.Text = T("Action.PrintLabels");
        _printReport.Text = T("Order.PrintReport");
        _refresh.Text = T("Action.Refresh");

        if (_list.Columns.Count > 0)
        {
            _list.Column("accession").HeaderText = T("Order.AccessionNumber");
            _list.Column("patient").HeaderText = T("Patient.Title");
            _list.Column("registered").HeaderText = T("Order.RegisteredAt");
            _list.Column("status").HeaderText = T("Field.Status");
            _list.Column("priority").HeaderText = T("Field.Priority");
            _list.Column("count").HeaderText = T("Catalog.Analyses");
        }

        RenderList();

        if (_items.Columns.Count > 0)
        {
            _items.Column("code").HeaderText = T("Catalog.Analysis");
            _items.Column("name").HeaderText = T("Field.Name");
            _items.Column("department").HeaderText = T("Field.Department");
            _items.Column("status").HeaderText = T("Field.Status");
            _items.Column("progress").HeaderText = T("Result.Plural");
            _items.Column("barcode").HeaderText = T("Sample.Barcode");
            _items.Column("price").HeaderText = T("Billing.Net");
            _items.Column("share").HeaderText = T("Billing.PatientShare");
            _items.Column("note").HeaderText = T("Field.Comment");
        }

        if (_samples.Columns.Count > 0)
        {
            _samples.Column("sequence").HeaderText = T("Sample.Sequence");
            _samples.Column("barcode").HeaderText = T("Sample.Barcode");
            _samples.Column("specimen").HeaderText = T("Sample.SpecimenType");
            _samples.Column("container").HeaderText = T("Sample.ContainerType");
            _samples.Column("status").HeaderText = T("Field.Status");
            _samples.Column("collected").HeaderText = T("Sample.CollectedAt");
            _samples.Column("received").HeaderText = T("Sample.ReceivedAt");
            _samples.Column("storage").HeaderText = T("Sample.Storage");
        }

        if (_detail is not null) Render(_detail);
        RefreshActions();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the list, keeps the file on screen selected in it, and loads that file — or, when
    /// nothing has been chosen yet, the newest file of the list, so the screen never opens empty.
    /// <para>
    /// Called again after every action, which is why the selection is carried over rather than
    /// reset: an operator who has just cancelled an analysis expects to be looking at the same
    /// file, not at whichever one was registered last.
    /// </para>
    /// </summary>
    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadListAsync(ct);

        if (ct.IsCancellationRequested) return;

        OrderId ??= _rows.Count > 0 ? _rows[0].OrderId : null;
        SelectListRow(OrderId);

        if (OrderId is null)
        {
            _search.Focus();
            return;
        }

        await LoadDetailAsync(ct);
    }

    /// <summary>
    /// Fetches the list for what is in the search box and renders it. Returns false when the
    /// load failed or was superseded by a newer one.
    /// </summary>
    private async Task<bool> LoadListAsync(CancellationToken ct = default)
    {
        _listLoad?.Cancel();
        _listLoad?.Dispose();
        _listLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Taken before the await: the source is disposed when a newer load replaces it.
        var token = _listLoad.Token;
        var term = _search.Text.Trim();

        try
        {
            var result = await _orders.SearchOrdersAsync(term, token);

            if (token.IsCancellationRequested) return false;

            if (result.IsFailure) { _dialogs.ShowFailure(result); return false; }

            _rows = result.Value;
            RenderList();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the order list for {Term} failed.", term);
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
            return false;
        }
    }

    private async Task LoadDetailAsync(CancellationToken ct = default)
    {
        if (OrderId is not { } orderId) return;

        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
        _detailLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var token = _detailLoad.Token;

        try
        {
            var result = await _orders.GetDetailAsync(orderId, token);

            if (token.IsCancellationRequested) return;

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _detail = result.Value;
            Render(_detail);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading order {OrderId} failed.", orderId);
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    /// <summary>Opens the file the operator selected in the list.</summary>
    private async Task OpenSelectedAsync()
    {
        if (_populatingList) return;
        if (_list.CurrentRow?.Tag is not OrderListRow row) return;
        if (row.OrderId == OrderId && _detail?.OrderId == row.OrderId) return;

        OrderId = row.OrderId;
        await LoadDetailAsync();
    }

    /// <summary>
    /// Resolves what the operator typed — an accession number or a tube barcode — to an order.
    /// Both, because the thing in their hand is as often a tube as a paper file.
    /// <para>
    /// When it is neither — a name, a CIN, part of a number — Enter opens the first file of the
    /// filtered list instead of answering "not found": the operator pressed Enter to get to a file,
    /// and the list already says which one fits best.
    /// </para>
    /// </summary>
    private async Task SearchAsync()
    {
        var term = _search.Text.Trim();

        try
        {
            if (term.Length == 0)
            {
                await LoadListAsync();
                return;
            }

            var resolved = await _orders.ResolveOrderIdAsync(term);

            if (resolved.IsFailure) { _dialogs.ShowFailure(resolved); return; }

            if (resolved.Value is { } orderId)
            {
                OrderId = orderId;
                await ActivateAsync();
                return;
            }

            if (!await LoadListAsync()) return;

            if (_rows.Count == 0)
            {
                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = T("Order.NotFoundForTerm", term);
                return;
            }

            OrderId = _rows[0].OrderId;
            SelectListRow(OrderId);
            await LoadDetailAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Searching orders for {Term} failed.", term);
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    private void RenderList()
    {
        if (_list.Columns.Count == 0) return;

        _populatingList = true;

        try
        {
            _list.SuspendLayout();
            _list.Rows.Clear();

            foreach (var row in _rows)
            {
                var index = _list.Rows.Add(
                    row.AccessionNumber,
                    row.PatientName,
                    row.RegisteredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    T($"OrderStatus.{row.Status}"),
                    T($"Priority.{row.Priority}"),
                    row.AnalysisCount);

                _list.Rows[index].Tag = row;
            }

            _list.ResumeLayout(performLayout: true);
        }
        finally
        {
            _populatingList = false;
        }

        SelectListRow(OrderId);

        var term = _search.Text.Trim();

        // Said when the cap is reached, so that a file missing from the list reads as "search for
        // it", not as "it does not exist".
        _listCount.Text = _rows.Count == 0 && term.Length > 0
            ? T("Order.NotFoundForTerm", term)
            : _rows.Count >= IOrderQueries.ListPageSize
                ? T("Order.ListTruncated", _rows.Count)
                : T("Order.ListCount", _rows.Count);
    }

    /// <summary>
    /// Moves the list's selection to the given order without opening it, or clears the selection
    /// when the order is not in the list — a file opened from the patient screen may be older than
    /// the newest two hundred, and is still shown, just not highlighted.
    /// </summary>
    private void SelectListRow(Guid? orderId)
    {
        _populatingList = true;

        try
        {
            var match = orderId is null
                ? null
                : _list.Rows.Cast<DataGridViewRow>()
                            .FirstOrDefault(r => r.Tag is OrderListRow row && row.OrderId == orderId);

            if (match is null)
            {
                _list.ClearSelection();
                _list.CurrentCell = null;
                return;
            }

            _list.CurrentCell = match.Cells[0];
            match.Selected = true;
        }
        finally
        {
            _populatingList = false;
        }
    }

    private void Render(OrderDetail detail)
    {
        var language = Localizer.CurrentLanguage;

        _accession.Text = detail.AccessionNumber;

        _statusBadge.Text = T($"OrderStatus.{detail.Status}");
        _statusBadge.ForeColor = UiTheme.ForOrderStatus(detail.Status);

        var age = detail.DateOfBirth is { } dob
            ? $" · {T("Patient.Age")} {AgeInYears(dob)}"
            : string.Empty;

        _patient.Text = $"{detail.PatientName}{age} · {T($"Gender.{detail.Gender}")} · "
                        + $"{T("Patient.RecordNumber")} {detail.MedicalRecordNumber}"
                        + (detail.PhoneNumber is { Length: > 0 } phone ? $" · {phone}" : string.Empty);

        var prescriber = detail.PrescriberName ?? detail.PrescriberFreeText;

        _prescriber.Text =
            $"{T("Order.Prescriber")} : {prescriber ?? T("Order.NoPrescriber")}"
            + (detail.PrescriberInpe is { Length: > 0 } inpe ? $" (INPE {inpe})" : string.Empty)
            + (detail.PrescriptionDate is { } prescribed
                ? $" · {T("Order.PrescriptionDate")} {prescribed:dd/MM/yyyy}"
                : string.Empty)
            + $" · {T("Order.RegisteredAt")} {detail.RegisteredAt.ToLocalTime():dd/MM/yyyy HH:mm}"
            + $" · {detail.RegisteredByName}"
            + $" · {T($"OrderOrigin.{detail.Origin}")} · {T($"Priority.{detail.Priority}")}";

        _coverage.Text = detail.PayerName is { Length: > 0 } payer
            ? $"{payer}"
              + (detail.ReimbursementRate is { } rate ? $" — {rate:P0}" : string.Empty)
              + (detail.PriorAgreementNumber is { Length: > 0 } agreement
                  ? $" · {T("Coverage.PriorAgreement")} {agreement}"
                  : string.Empty)
            : T("Billing.PayerNotCovered");

        var clinical = new List<string>(4);
        if (detail.ClinicalInformation is { Length: > 0 } info) clinical.Add(info);
        if (detail.DiagnosisCode is { Length: > 0 } code) clinical.Add(code);
        if (detail.WardOrUnit is { Length: > 0 } ward)
            clinical.Add(detail.RoomOrBed is { Length: > 0 } bed ? $"{ward} / {bed}" : ward);
        if (detail.PatientFasted is false) clinical.Add(T("Order.NotFasted"));
        if (detail.InternalNote is { Length: > 0 } note) clinical.Add($"[{note}]");

        _clinical.Text = string.Join("   ·   ", clinical);

        _cancellation.Text = detail.CancelledAt is { } cancelled
            ? $"{T("OrderStatus.Cancelled")} {cancelled.ToLocalTime():dd/MM/yyyy HH:mm} — "
              + detail.CancellationReason
            : detail.InvoiceNumber is { Length: > 0 } invoice
                ? $"{T("Billing.Invoice")} {invoice}"
                : string.Empty;

        _cancellation.ForeColor = detail.CancelledAt is not null
            ? UiTheme.Critical
            : UiTheme.TextSecondary;

        RenderItems(detail, language);
        RenderSamples(detail, language);

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = $"{detail.LiveItemCount} {T("Catalog.Analyses").ToLowerInvariant()} · "
                       + $"{Money.Of(detail.ChargedTotal).Format(language)} · "
                       + $"{T("Billing.PatientShare")} "
                       + Money.Of(detail.PatientShareTotal).Format(language);

        RefreshActions();
    }

    private void RenderItems(OrderDetail detail, AppLanguage language)
    {
        _items.SuspendLayout();
        _items.Rows.Clear();

        foreach (var item in detail.Items)
        {
            var note = item.IsCancelled
                ? item.CancellationReason
                : item.RunCount > 1
                    ? $"{T("Result.Run")} {item.RunCount} — {item.RepeatReason}"
                    : item.ValidatedByName is { Length: > 0 } validator
                        ? $"{T("Validation.ValidatedBy")} {validator}"
                        : item.IsSubcontracted ? T("Catalog.Subcontracted") : string.Empty;

            var index = _items.Rows.Add(
                item.Code,
                item.Name.For(language),
                item.DepartmentCode ?? string.Empty,
                T($"OrderItemStatus.{item.Status}"),
                item.ResultCount == 0 ? string.Empty
                                      : $"{item.EnteredResultCount}/{item.ResultCount}",
                item.SampleBarcode ?? string.Empty,
                Money.Of(item.ChargedPrice).Format(language),
                Money.Of(item.PatientShare).Format(language),
                note ?? string.Empty);

            _items.Rows[index].Tag = item;
        }

        _items.ResumeLayout(performLayout: true);

        // Select the first live row rather than the first row: the cancelled ones are history and
        // are not what an operator wants an action pointed at.
        var live = _items.Rows.Cast<DataGridViewRow>()
                              .FirstOrDefault(r => r.Tag is OrderItemDetail { IsCancelled: false });

        if (live is not null) live.Selected = true;
    }

    private void RenderSamples(OrderDetail detail, AppLanguage language)
    {
        _samples.SuspendLayout();
        _samples.Rows.Clear();

        foreach (var sample in detail.Samples)
        {
            var index = _samples.Rows.Add(
                sample.SequenceInOrder,
                sample.Barcode,
                sample.SpecimenTypeName.For(language),
                sample.ContainerName ?? string.Empty,
                T($"SampleStatus.{sample.Status}"),
                sample.CollectedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? string.Empty,
                sample.ReceivedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? string.Empty,
                sample.StorageLocation ?? string.Empty);

            _samples.Rows[index].Tag = sample;
        }

        _samples.ResumeLayout(performLayout: true);
    }

    private OrderItemDetail? SelectedItem => _items.CurrentRow?.Tag as OrderItemDetail;

    private void RefreshActions()
    {
        var detail = _detail;
        var item = SelectedItem;

        var editable = detail is { IsCancelled: false };

        _addAnalyses.Enabled = editable && _currentUser.Has(Permissions.Orders.Edit);
        _editHeader.Enabled = editable && _currentUser.Has(Permissions.Orders.Edit);

        _cancelOrder.Enabled = detail is { CanCancel: true }
                               && _currentUser.Has(Permissions.Orders.Cancel);

        _printLabels.Enabled = detail is { IsCancelled: false }
                               && _currentUser.Has(Permissions.Samples.Reprint);

        _printReport.Enabled = detail is not null && _currentUser.Has(Permissions.Reports.View);

        _cancelItem.Enabled = item is { CanCancel: true }
                              && _currentUser.Has(Permissions.Orders.Cancel);

        _repeatItem.Enabled = item is { CanRepeat: true }
                              && _currentUser.Has(Permissions.Results.Enter);
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task AddAnalysesAsync()
    {
        if (_detail is not { } detail) return;

        using var dialog = new AnalysisPickerDialog(Localizer, _catalogQueries, _dialogs, _clock,
                                                     detail.AccessionNumber, detail.PatientName,
                                                     [.. detail.Items.Where(i => !i.IsCancelled)
                                                                     .Select(i => i.AnalysisTestId)]);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _orders.AddAnalysesAsync(detail.OrderId, dialog.AnalysisIds,
                                                     dialog.PanelIds, dialog.Reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.AddAnalyses")); return; }

        var amendment = result.Value;

        // Warnings are shown, not swallowed: "added onto a tube already in the laboratory" is a
        // job for somebody — go and check there is serum left — and it is not visible anywhere else.
        if (amendment.Warnings.Count > 0)
            _dialogs.Warn(Localizer.Describe(amendment.Warnings), T("Order.AddAnalyses"));

        // A new tube means a new label. Printed here rather than left to the collection screen,
        // because the person who just added the analysis is the one standing by the printer.
        await PrintLabelsAsync(detail.AccessionNumber, amendment.Labels);

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Order.AnalysesAdded", amendment.Pricing.Acts.Count,
                         amendment.Pricing.Total.Format(Localizer.CurrentLanguage));

        await ActivateAsync();
    }

    private async Task CancelItemAsync()
    {
        if (_detail is not { } detail || SelectedItem is not { } item) return;

        var reason = _dialogs.PromptForReason(
            T("Order.CancelAnalysisPrompt", item.Code, item.Name.For(Localizer.CurrentLanguage)),
            T("Order.CancelAnalysis"));

        if (reason is null) return;

        var result = await _orders.CancelItemAsync(detail.OrderId, item.ItemId, reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.CancelAnalysis")); return; }

        // The charge goes with the analysis while the order is uninvoiced; once invoiced, the money
        // is put right by a credit note, and saying so here saves the operator discovering it later.
        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = detail.IsInvoiced
            ? T("Order.CancelledItemNeedsCreditNote", item.Code)
            : $"{item.Code} — {T("OrderItemStatus.Cancelled")}";

        await ActivateAsync();
    }

    private async Task RepeatItemAsync()
    {
        if (_detail is not { } detail || SelectedItem is not { } item) return;

        var reason = _dialogs.PromptForReason(
            T("Order.RepeatAnalysisPrompt", item.Code, item.RunCount),
            T("Order.RepeatAnalysis"));

        if (reason is null) return;

        var result = await _orders.RepeatItemAsync(detail.OrderId, item.ItemId, reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.RepeatAnalysis")); return; }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = T("Order.RepeatRecorded", item.Code, item.RunCount + 1);

        await ActivateAsync();
    }

    private async Task EditHeaderAsync()
    {
        if (_detail is not { } detail) return;

        using var dialog = new OrderHeaderDialog(Localizer, _dialogs, detail);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _orders.AmendHeaderAsync(dialog.Request, dialog.Reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.EditHeader")); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await ActivateAsync();
    }

    private async Task CancelOrderAsync()
    {
        if (_detail is not { } detail) return;

        // Named in the confirmation, with the tube count, because cancelling a file also discards
        // its tubes and the operator should know that before they click rather than after.
        var live = detail.Samples.Count(
            s => s.Status is not (SampleStatus.Rejected or SampleStatus.Discarded));

        if (!_dialogs.Confirm(
                T("Order.ConfirmCancelOrder", detail.AccessionNumber, detail.LiveItemCount, live),
                T("Order.CancelOrder")))
            return;

        var reason = _dialogs.PromptForReason(
            T("Order.CancelOrderPrompt", detail.AccessionNumber), T("Order.CancelOrder"));

        if (reason is null) return;

        var result = await _orders.CancelOrderAsync(detail.OrderId, reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.CancelOrder")); return; }

        _status.ForeColor = UiTheme.Critical;
        _status.Text = detail.IsInvoiced
            ? T("Order.CancelledNeedsCreditNote", detail.InvoiceNumber ?? string.Empty)
            : T("OrderStatus.Cancelled");

        await ActivateAsync();
    }

    /// <summary>
    /// Renders and opens the labels for tubes the addition created. A label failure must not lose
    /// the amendment — the analyses are on the order, and the labels can be reprinted.
    /// </summary>
    /// <summary>
    /// Reprints every label of the file, against a reason: a reprinted label is a second barcode
    /// for the same tube, which is how two tubes end up carrying one identity, so it is recorded.
    /// </summary>
    private async Task ReprintLabelsAsync()
    {
        if (_detail is not { } detail) return;

        var reason = _dialogs.PromptForReason(T("Sample.ReprintReasonPrompt"), T("Action.PrintLabels"));
        if (reason is null) return;

        try
        {
            var labels = await _samplesWorkflow.ReprintLabelsAsync(detail.OrderId, reason);

            if (labels.IsFailure) { _dialogs.ShowFailure(labels, T("Action.PrintLabels")); return; }

            await PrintLabelsAsync(detail.AccessionNumber, labels.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reprinting the labels of {Accession} failed.", detail.AccessionNumber);
            _dialogs.Warn(T("Action.PrintLabels") + " — " + T("Message.UnexpectedError"));
        }
    }

    /// <summary>
    /// Opens the latest released report of the file, exactly as it was archived — a reprint shows
    /// what the clinician was given, never a fresh rendering of today's data.
    /// </summary>
    private async Task OpenReportAsync()
    {
        if (_detail is not { } detail) return;

        try
        {
            var register = await _reports.GetRegisterAsync(detail.AccessionNumber, withinDays: 3650);

            if (register.IsFailure) { _dialogs.ShowFailure(register, _printReport.Text); return; }

            var latest = register.Value
                .Where(r => r.LabOrderId == detail.OrderId)
                .OrderByDescending(r => r.VersionNumber)
                .FirstOrDefault();

            if (latest is null)
            {
                _dialogs.Info(T("Order.NoReportYet"), _printReport.Text);
                return;
            }

            var pdf = await _reports.GetStoredPdfAsync(latest.ReportId);

            if (pdf.IsFailure) { _dialogs.ShowFailure(pdf, _printReport.Text); return; }

            var opened = await _documents.OpenAsync(pdf.Value, "pdf");

            if (opened.IsFailure) _dialogs.ShowFailure(opened, _printReport.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Opening the report of {Accession} failed.", detail.AccessionNumber);
            _dialogs.Warn(_printReport.Text + " — " + T("Message.UnexpectedError"));
        }
    }

    private async Task PrintLabelsAsync(string accessionNumber, IReadOnlyList<SampleLabel> labels)
    {
        if (labels.Count == 0) return;

        try
        {
            var emitted = await LabelOutput.EmitAsync(_labelPrinter, _documents, _labels.Render, labels);

            if (emitted.IsFailure) _dialogs.ShowFailure(emitted, T("Action.PrintLabels"));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Rendering the added sample labels failed.");
            _dialogs.Warn(T("Action.PrintLabels") + " — " + T("Message.UnexpectedError"));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounce.Dispose();
            _listLoad?.Cancel();
            _listLoad?.Dispose();
            _detailLoad?.Cancel();
            _detailLoad?.Dispose();
        }

        base.Dispose(disposing);
    }

    private int AgeInYears(DateOnly dateOfBirth)
    {
        var today = _clock.Today;
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age)) age--;
        return age;
    }
}
