using System.Globalization;
using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The reference data an installation has to be able to create.
/// <para>
/// Five tabs, and each one unblocks a screen that was previously unusable on a real installation:
/// without an instrument the equipment register is empty, without a QC material and target the
/// control entry sheet can never show a row, without a stock item there is nothing to receive a lot
/// against, and without a prescriber an order can never record who asked for it — which an AMO
/// claim needs.
/// </para>
/// <para>
/// The instrument tab flags a missing interval prominently. That is not tidiness: the control which
/// stops an analyser producing patient results while its calibration is lapsed computes "lapsed"
/// from the interval, so a null interval silently disarms it.
/// </para>
/// </summary>
public sealed class MasterDataView : LocalizedView
{
    private readonly MasterDataService _master;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<MasterDataView>? _logger;

    private readonly TabControl _tabs = new();
    private readonly TabPage _instrumentTab = new();
    private readonly TabPage _qcTab = new();
    private readonly TabPage _stockTab = new();
    private readonly TabPage _supplierTab = new();
    private readonly TabPage _prescriberTab = new();
    private readonly TabPage _payerTab = new();

    private readonly DataGridView _instruments = UiTheme.Grid();
    private readonly DataGridView _qcMaterials = UiTheme.Grid();
    private readonly DataGridView _qcTargets = UiTheme.Grid();
    private readonly DataGridView _stock = UiTheme.Grid();
    private readonly DataGridView _suppliers = UiTheme.Grid();
    private readonly DataGridView _prescribers = UiTheme.Grid();
    private readonly DataGridView _payers = UiTheme.Grid();

    private readonly Button _newInstrument = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editInstrument = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _instrumentNotice = new();

    private readonly Button _newMaterial = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editMaterial = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _newTarget = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editTarget = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _qcNotice = new();

    private readonly Button _newStock = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editStock = UiTheme.SecondaryButton(string.Empty);

    private readonly Button _newSupplier = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editSupplier = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _supplierNotice = new();

    private readonly TextBox _prescriberSearch = UiTheme.Input(260);
    private readonly Button _newPrescriber = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editPrescriber = UiTheme.SecondaryButton(string.Empty);

    private readonly Button _newPayer = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editPayer = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _payerNotice = new();

    private readonly CheckBox _includeInactive = new();
    private readonly Label _status = new();

    private IReadOnlyList<InstrumentMasterRow> _instrumentRows = [];
    private IReadOnlyList<QcMaterialRow> _materialRows = [];
    private IReadOnlyList<QcTargetRow> _targetRows = [];
    private IReadOnlyList<StockItemMasterRow> _stockRows = [];
    private IReadOnlyList<SupplierRow> _supplierRows = [];
    private IReadOnlyList<PrescriberRow> _prescriberRows = [];
    private IReadOnlyList<PayerRow> _payerRows = [];
    private IReadOnlyList<ParameterOption> _parameters = [];

    public MasterDataView(MasterDataService master, IUserDialogs dialogs, ILocalizer localizer,
                          ICultureManager cultureManager, ILogger<MasterDataView>? logger = null)
        : base(localizer, cultureManager)
    {
        _master = master;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.MasterData");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = UiTheme.Surface };

        _includeInactive.Font = UiTheme.Small;
        _includeInactive.ForeColor = UiTheme.TextSecondary;
        _includeInactive.AutoSize = true;
        _includeInactive.Location = new Point(0, 8);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(300, 8, 1100, 20);

        bar.Controls.AddRange([_includeInactive, _status]);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = UiTheme.Body;
        _tabs.Padding = new Point(14, 6);

        BuildInstrumentTab();
        BuildQcTab();
        BuildStockTab();
        BuildSupplierTab();
        BuildPrescriberTab();
        BuildPayerTab();

        _tabs.TabPages.AddRange([_instrumentTab, _qcTab, _stockTab, _supplierTab,
                                 _prescriberTab, _payerTab]);

        Controls.Add(_tabs);
        Controls.Add(bar);
    }

    private static void Columns(DataGridView grid,
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

    private static Panel Actions(int height, params Control[] controls)
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = height,
                                BackColor = UiTheme.Surface };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private void BuildInstrumentTab()
    {
        _instrumentTab.BackColor = UiTheme.Surface;
        _instrumentTab.Padding = new Padding(UiTheme.Gutter);

        Columns(_instruments,
            ("code", 10, UiTheme.MonospaceBold, false),
            ("name", 20, null, false),
            ("kind", 11, UiTheme.Small, false),
            ("status", 10, UiTheme.SmallBold, false),
            ("serial", 11, UiTheme.Monospace, false),
            ("location", 9, UiTheme.Small, false),
            ("maintenanceInterval", 8, UiTheme.Monospace, true),
            ("calibrationInterval", 8, UiTheme.Monospace, true),
            ("qctargets", 6, UiTheme.Monospace, true),
            ("interface", 7, UiTheme.SmallBold, false));

        _instruments.Dock = DockStyle.Fill;

        _newInstrument.Bounds = new Rectangle(0, 8, 200, UiTheme.ButtonHeight);
        _editInstrument.Bounds = new Rectangle(210, 8, 150, UiTheme.ButtonHeight);

        _instrumentNotice.Font = UiTheme.Small;
        _instrumentNotice.AutoSize = false;
        _instrumentNotice.Bounds = new Rectangle(0, 44, 1300, 32);

        _instrumentTab.Controls.Add(_instruments);
        _instrumentTab.Controls.Add(Actions(80, _newInstrument, _editInstrument, _instrumentNotice));
    }

    private void BuildQcTab()
    {
        _qcTab.BackColor = UiTheme.Surface;
        _qcTab.Padding = new Padding(UiTheme.Gutter);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterDistance = 240, SplitterWidth = 6, BackColor = UiTheme.Surface
        };

        Columns(_qcMaterials,
            ("lot", 14, UiTheme.MonospaceBold, false),
            ("name", 22, null, false),
            ("level", 10, UiTheme.Small, false),
            ("expiry", 11, UiTheme.Monospace, false),
            ("days", 7, UiTheme.MonospaceBold, true),
            ("manufacturer", 14, UiTheme.Small, false),
            ("targets", 8, UiTheme.Monospace, true),
            ("state", 11, UiTheme.SmallBold, false));

        _qcMaterials.Dock = DockStyle.Fill;

        _newMaterial.Bounds = new Rectangle(0, 8, 220, UiTheme.ButtonHeight);
        _editMaterial.Bounds = new Rectangle(230, 8, 150, UiTheme.ButtonHeight);

        var top = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        top.Controls.Add(_qcMaterials);
        top.Controls.Add(Actions(44, _newMaterial, _editMaterial));

        Columns(_qcTargets,
            ("lot", 12, UiTheme.Monospace, false),
            ("level", 8, UiTheme.Small, false),
            ("parameter", 18, null, false),
            ("instrument", 10, UiTheme.Monospace, false),
            ("assigned", 14, UiTheme.Monospace, true),
            ("laboratory", 14, UiTheme.Monospace, true),
            ("points", 6, UiTheme.Monospace, true),
            ("rules", 7, UiTheme.Monospace, true),
            ("state", 11, UiTheme.SmallBold, false));

        _qcTargets.Dock = DockStyle.Fill;

        _newTarget.Bounds = new Rectangle(0, 8, 240, UiTheme.ButtonHeight);
        _editTarget.Bounds = new Rectangle(250, 8, 150, UiTheme.ButtonHeight);

        _qcNotice.Font = UiTheme.Small;
        _qcNotice.AutoSize = false;
        _qcNotice.Bounds = new Rectangle(0, 44, 1300, 32);

        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        bottom.Controls.Add(_qcTargets);
        bottom.Controls.Add(Actions(80, _newTarget, _editTarget, _qcNotice));

        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(bottom);

        _qcTab.Controls.Add(split);
    }

    private void BuildStockTab()
    {
        _stockTab.BackColor = UiTheme.Surface;
        _stockTab.Padding = new Padding(UiTheme.Gutter);

        Columns(_stock,
            ("code", 11, UiTheme.MonospaceBold, false),
            ("name", 24, null, false),
            ("kind", 12, UiTheme.Small, false),
            ("unit", 8, UiTheme.Small, false),
            ("reorder", 9, UiTheme.Monospace, true),
            ("minimum", 9, UiTheme.Monospace, true),
            ("storage", 12, UiTheme.Small, false),
            ("supplier", 13, UiTheme.Small, false),
            ("flags", 8, UiTheme.SmallBold, false));

        _stock.Dock = DockStyle.Fill;

        _newStock.Bounds = new Rectangle(0, 8, 200, UiTheme.ButtonHeight);
        _editStock.Bounds = new Rectangle(210, 8, 150, UiTheme.ButtonHeight);

        _stockTab.Controls.Add(_stock);
        _stockTab.Controls.Add(Actions(44, _newStock, _editStock));
    }

    private void BuildSupplierTab()
    {
        _supplierTab.BackColor = UiTheme.Surface;
        _supplierTab.Padding = new Padding(UiTheme.Gutter);

        Columns(_suppliers,
            ("code", 10, UiTheme.MonospaceBold, false),
            ("name", 22, null, false),
            ("ice", 12, UiTheme.Monospace, false),
            ("contact", 14, UiTheme.Small, false),
            ("phone", 11, UiTheme.Monospace, false),
            ("lead", 7, UiTheme.Monospace, true),
            ("approved", 10, UiTheme.SmallBold, false),
            ("evaluation", 11, UiTheme.Monospace, false),
            ("items", 6, UiTheme.Monospace, true));

        _suppliers.Dock = DockStyle.Fill;

        _newSupplier.Bounds = new Rectangle(0, 8, 200, UiTheme.ButtonHeight);
        _editSupplier.Bounds = new Rectangle(210, 8, 150, UiTheme.ButtonHeight);

        _supplierNotice.Font = UiTheme.Small;
        _supplierNotice.AutoSize = false;
        _supplierNotice.Bounds = new Rectangle(0, 44, 1300, 32);

        _supplierTab.Controls.Add(_suppliers);
        _supplierTab.Controls.Add(Actions(80, _newSupplier, _editSupplier, _supplierNotice));
    }

    private void BuildPrescriberTab()
    {
        _prescriberTab.BackColor = UiTheme.Surface;
        _prescriberTab.Padding = new Padding(UiTheme.Gutter);

        Columns(_prescribers,
            ("name", 20, null, false),
            ("kind", 13, UiTheme.Small, false),
            ("speciality", 14, UiTheme.Small, false),
            ("order", 10, UiTheme.Monospace, false),
            ("inpe", 10, UiTheme.Monospace, false),
            ("institution", 16, UiTheme.Small, false),
            ("phone", 11, UiTheme.Monospace, false),
            ("delivery", 12, UiTheme.Small, false),
            ("orders", 6, UiTheme.Monospace, true));

        _prescribers.Dock = DockStyle.Fill;

        var search = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = UiTheme.Surface };
        _prescriberSearch.Location = new Point(0, 4);
        search.Controls.Add(_prescriberSearch);

        _newPrescriber.Bounds = new Rectangle(0, 8, 210, UiTheme.ButtonHeight);
        _editPrescriber.Bounds = new Rectangle(220, 8, 150, UiTheme.ButtonHeight);

        _prescriberTab.Controls.Add(_prescribers);
        _prescriberTab.Controls.Add(Actions(44, _newPrescriber, _editPrescriber));
        _prescriberTab.Controls.Add(search);
    }

    /// <summary>
    /// The payer tab. Last, because it is the one an administrator visits least often and the one
    /// whose contents a reception clerk never needs.
    /// <para>
    /// The rate column is shown as a percentage and right-aligned next to the reference-tariff
    /// flag, because those two together are the whole of how a covered invoice divides: a rate
    /// applied to the laboratory's own price list instead of the TNR silently shifts money from the
    /// patient to the payer, and the payer rejects the claim weeks later.
    /// </para>
    /// </summary>
    private void BuildPayerTab()
    {
        _payerTab.BackColor = UiTheme.Surface;
        _payerTab.Padding = new Padding(UiTheme.Gutter);

        Columns(_payers,
            ("code", 9, UiTheme.MonospaceBold, false),
            ("name", 20, null, false),
            ("kind", 13, UiTheme.Small, false),
            ("rate", 8, UiTheme.MonospaceBold, true),
            ("tnr", 7, UiTheme.Small, false),
            ("thirdparty", 9, UiTheme.Small, false),
            ("agreement", 11, UiTheme.Monospace, false),
            ("terms", 7, UiTheme.Monospace, true),
            ("patients", 7, UiTheme.Monospace, true),
            ("openinvoices", 7, UiTheme.Monospace, true),
            ("problems", 14, UiTheme.Small, false));

        _payers.Dock = DockStyle.Fill;

        _payerNotice.Font = UiTheme.Small;
        _payerNotice.ForeColor = UiTheme.TextSecondary;
        _payerNotice.AutoSize = false;
        _payerNotice.Bounds = new Rectangle(380, 12, 900, 30);

        _newPayer.Bounds = new Rectangle(0, 8, 190, UiTheme.ButtonHeight);
        _editPayer.Bounds = new Rectangle(200, 8, 150, UiTheme.ButtonHeight);

        _payerTab.Controls.Add(_payers);
        _payerTab.Controls.Add(Actions(52, _newPayer, _editPayer, _payerNotice));
    }

    private void WireBehaviour()
    {
        _includeInactive.CheckedChanged += async (_, _) => await ActivateAsync();

        _newInstrument.Click += async (_, _) => await EditInstrumentAsync(null);
        _editInstrument.Click += async (_, _) =>
            await EditInstrumentAsync(_instruments.CurrentRow?.Tag as InstrumentMasterRow);

        _newMaterial.Click += async (_, _) => await EditMaterialAsync(null);
        _editMaterial.Click += async (_, _) =>
            await EditMaterialAsync(_qcMaterials.CurrentRow?.Tag as QcMaterialRow);

        _newTarget.Click += async (_, _) => await EditTargetAsync(null);
        _editTarget.Click += async (_, _) =>
            await EditTargetAsync(_qcTargets.CurrentRow?.Tag as QcTargetRow);

        _newStock.Click += async (_, _) => await EditStockAsync(null);
        _editStock.Click += async (_, _) =>
            await EditStockAsync(_stock.CurrentRow?.Tag as StockItemMasterRow);

        _newSupplier.Click += async (_, _) => await EditSupplierAsync(null);
        _editSupplier.Click += async (_, _) =>
            await EditSupplierAsync(_suppliers.CurrentRow?.Tag as SupplierRow);

        _newPrescriber.Click += async (_, _) => await EditPrescriberAsync(null);
        _editPrescriber.Click += async (_, _) =>
            await EditPrescriberAsync(_prescribers.CurrentRow?.Tag as PrescriberRow);

        _newPayer.Click += async (_, _) => await EditPayerAsync(null);
        _editPayer.Click += async (_, _) =>
            await EditPayerAsync(_payers.CurrentRow?.Tag as PayerRow);

        _prescriberSearch.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadPrescribersAsync(); }
        };

        // Targets follow the selected material: a laboratory runs two or three levels of the same
        // control, and seeing every target for every lot at once is unreadable.
        _qcMaterials.SelectionChanged += async (_, _) => await LoadTargetsAsync();

        _instruments.CellFormatting += OnInstrumentFormatting;
        _qcMaterials.CellFormatting += OnMaterialFormatting;
        _qcTargets.CellFormatting += OnTargetFormatting;
        _stock.CellFormatting += OnStockFormatting;
        _suppliers.CellFormatting += OnSupplierFormatting;
        _prescribers.CellFormatting += OnPrescriberFormatting;
        _payers.CellFormatting += OnPayerFormatting;

        _payers.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0) await EditPayerAsync(_payers.CurrentRow?.Tag as PayerRow);
        };
    }

    // ---- Conditional formatting ---------------------------------------------------------------------

    private void OnInstrumentFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        // Bounds check then index, as everywhere else: DataGridViewRowCollection is a non-generic
        // IList, so the LINQ operators do not apply to it.
        if (e.RowIndex < 0 || e.RowIndex >= _instruments.Rows.Count) return;
        if (_instruments.Rows[e.RowIndex].Tag is not InstrumentMasterRow row) return;

        // A missing interval is the sharpest defect on this screen: the overdue indicators and the
        // calibration guard are both computed from it, so a null silently disarms them.
        if (row.IntervalsIncomplete)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.Status == EquipmentStatus.Decommissioned)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
    }

    private void OnMaterialFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _qcMaterials.Rows.Count) return;
        if (_qcMaterials.Rows[e.RowIndex].Tag is not QcMaterialRow row) return;

        if (!row.IsUsable)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.DaysToExpiry <= 30 || row.TargetCount == 0)
        {
            // No target means the material cannot be entered against anything, so it is present
            // but unusable in practice.
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnTargetFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _qcTargets.Rows.Count) return;
        if (_qcTargets.Rows[e.RowIndex].Tag is not QcTargetRow row) return;

        if (!row.HasUsableLimits)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (!row.IsActive)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
        else if (row.LaboratoryMean is null)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnStockFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _stock.Rows.Count) return;
        if (_stock.Rows[e.RowIndex].Tag is not StockItemMasterRow row) return;

        if (!row.IsActive) e.CellStyle!.ForeColor = UiTheme.TextMuted;
        else if (row.IsCritical) e.CellStyle!.Font = UiTheme.BodyBold;
    }

    private void OnSupplierFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _suppliers.Rows.Count) return;
        if (_suppliers.Rows[e.RowIndex].Tag is not SupplierRow row) return;

        if (row.IsEvaluationOverdue)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
        else if (!row.IsActive)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
    }

    private void OnPrescriberFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _prescribers.Rows.Count) return;
        if (_prescribers.Rows[e.RowIndex].Tag is not PrescriberRow row) return;

        if (!row.IsActive) { e.CellStyle!.ForeColor = UiTheme.TextMuted; return; }

        // No INPE means an AMO claim carrying this prescriber's prescription cannot be filed.
        if (string.IsNullOrWhiteSpace(row.Inpe)
            && row.Kind is PrescriberKind.GeneralPractitioner or PrescriberKind.Specialist)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnPayerFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _payers.Rows.Count) return;
        if (_payers.Rows[e.RowIndex].Tag is not PayerRow row) return;

        if (!row.IsActive) { e.CellStyle!.ForeColor = UiTheme.TextMuted; return; }

        // An expired convention is the sharpest defect here: invoices keep being raised against
        // the payer and every one of them will come back rejected.
        if (row.IsAgreementExpired)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.Problems.Count > 0)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    protected override void ApplyTexts()
    {
        _instrumentTab.Text = T("Nav.Instruments");
        _qcTab.Text = T("Nav.QualityControl");
        _stockTab.Text = T("Inventory.Items");
        _supplierTab.Text = T("Nav.Suppliers");
        _prescriberTab.Text = T("Order.Prescribers");
        _payerTab.Text = T("Coverage.Payers");

        _includeInactive.Text = T("Admin.IncludeInactive");

        _newInstrument.Text = T("Equipment.NewInstrument");
        _newMaterial.Text = T("Quality.NewQcMaterial");
        _newTarget.Text = T("Quality.NewQcTarget");
        _newStock.Text = T("Inventory.NewItem");
        _newSupplier.Text = T("Inventory.NewSupplier");
        _newPrescriber.Text = T("Order.NewPrescriber");
        _newPayer.Text = T("Coverage.NewPayer");

        foreach (var button in new[] { _editInstrument, _editMaterial, _editTarget, _editStock,
                                        _editSupplier, _editPrescriber, _editPayer })
            button.Text = T("Action.Edit");

        _prescriberSearch.PlaceholderText = T("App.SearchPlaceholder");

        _instrumentNotice.ForeColor = UiTheme.TextSecondary;
        _instrumentNotice.Text = T("Equipment.IntervalNotice");
        _qcNotice.ForeColor = UiTheme.TextSecondary;
        _qcNotice.Text = T("Quality.TargetNotice");
        _supplierNotice.ForeColor = UiTheme.TextSecondary;
        _supplierNotice.Text = T("Inventory.SupplierEvaluationNotice");
        _payerNotice.ForeColor = UiTheme.TextSecondary;
        _payerNotice.Text = T("Coverage.PayerTariffNotice");

        Header(_instruments, ("code", "Field.Code"), ("name", "Field.Name"),
               ("kind", "Equipment.Kind"), ("status", "Field.Status"),
               ("serial", "Equipment.SerialNumber"), ("location", "Equipment.Location"),
               ("maintenanceInterval", "Equipment.MaintenanceInterval"),
               ("calibrationInterval", "Equipment.CalibrationInterval"),
               ("qctargets", "Quality.QcTargets"), ("interface", "Nav.Instruments"));

        Header(_qcMaterials, ("lot", "Quality.QcLot"), ("name", "Field.Name"),
               ("level", "Quality.QcLevel"), ("expiry", "Inventory.Expiry"),
               ("days", "Field.Date"), ("manufacturer", "Equipment.Manufacturer"),
               ("targets", "Quality.QcTargets"), ("state", "Field.Status"));

        Header(_qcTargets, ("lot", "Quality.QcLot"), ("level", "Quality.QcLevel"),
               ("parameter", "Result.Parameter"), ("instrument", "Result.Instrument"),
               ("assigned", "Quality.AssignedLimits"),
               ("laboratory", "Quality.LaboratoryLimits"), ("points", "Quality.PointCount"),
               ("rules", "Quality.Rules"), ("state", "Field.Status"));

        Header(_stock, ("code", "Field.Code"), ("name", "Inventory.Item"),
               ("kind", "Field.Name"), ("unit", "Field.Unit"),
               ("reorder", "Inventory.ReorderPoint"), ("minimum", "Inventory.MinimumStock"),
               ("storage", "Sample.Storage"), ("supplier", "Inventory.Supplier"),
               ("flags", "Field.Status"));

        Header(_suppliers, ("code", "Field.Code"), ("name", "Field.Name"),
               ("ice", "Site.Ice"), ("contact", "Inventory.SupplierContact"),
               ("phone", "Site.Phone"), ("lead", "Inventory.LeadTime"),
               ("approved", "Inventory.Approved"),
               ("evaluation", "Inventory.LastEvaluation"), ("items", "Inventory.Items"));

        Header(_prescribers, ("name", "Order.Prescriber"), ("kind", "Field.Name"),
               ("speciality", "Order.Speciality"), ("order", "Admin.OrderNumber"),
               ("inpe", "Site.Inpe"), ("institution", "Order.Institution"),
               ("phone", "Site.Phone"), ("delivery", "Report.Delivery"),
               ("orders", "Order.Plural"));

        Header(_payers, ("code", "Field.Code"), ("name", "Field.Name"),
               ("kind", "Coverage.PayerKind"), ("rate", "Coverage.ReimbursementRate"),
               ("tnr", "Coverage.ReferenceTariff"), ("thirdparty", "Coverage.ThirdPartyPayment"),
               ("agreement", "Coverage.AgreementNumber"), ("terms", "Coverage.PaymentTerms"),
               ("patients", "Patient.Plural"), ("openinvoices", "Billing.Outstanding"),
               ("problems", "Claim.Blockers"));

        RenderAll();
    }

    private void Header(DataGridView grid, params (string Column, string Key)[] headers)
    {
        if (grid.Columns.Count == 0) return;

        foreach (var (column, key) in headers)
            if (grid.Columns.Contains(column)) grid.Column(column).HeaderText = T(key);
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var inactive = _includeInactive.Checked;

            var instruments = await _master.GetInstrumentsAsync(inactive, ct);
            if (instruments.IsSuccess) _instrumentRows = instruments.Value;
            else _dialogs.ShowFailure(instruments);

            var materials = await _master.GetQcMaterialsAsync(inactive, ct);
            if (materials.IsSuccess) _materialRows = materials.Value;

            var stock = await _master.GetStockItemsAsync(inactive, ct);
            if (stock.IsSuccess) _stockRows = stock.Value;

            var suppliers = await _master.GetSuppliersAsync(inactive, ct);
            if (suppliers.IsSuccess) _supplierRows = suppliers.Value;

            // Not surfaced as a dialog on failure: a reception supervisor holding Equipment.View
            // but not Billing.View has legitimate business on the other five tabs, and a modal
            // refusal every time they open the screen would be noise, not a control.
            var payers = await _master.GetPayersAsync(inactive, ct);
            if (payers.IsSuccess) _payerRows = payers.Value;

            var parameters = await _master.GetParametersAsync(ct);
            if (parameters.IsSuccess) _parameters = parameters.Value;

            await LoadPrescribersAsync(ct);

            RenderAll();
            await LoadTargetsAsync(ct);

            ShowCompleteness();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the reference data failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    /// <summary>
    /// Says which screens are still unusable, because that is what an administrator setting up an
    /// installation needs to know and it is not obvious from five separate empty grids.
    /// </summary>
    private void ShowCompleteness()
    {
        var blockers = new List<string>(4);

        if (_instrumentRows.Count == 0) blockers.Add(T("Nav.Equipment"));
        if (_materialRows.Count == 0 || _targetRows.Count == 0) blockers.Add(T("Nav.QualityControl"));
        if (_stockRows.Count == 0) blockers.Add(T("Nav.Inventory"));
        if (_prescriberRows.Count == 0) blockers.Add(T("Order.Prescribers"));

        // A payer carrying a problem is listed here too. Unlike the others this is not an empty
        // grid — the seeder ships ten payers — so nothing on screen would otherwise suggest that
        // the rates they were seeded with have never been confirmed against the convention.
        if (_payerRows.Any(p => p.Problems.Count > 0)) blockers.Add(T("Coverage.Payers"));

        if (blockers.Count == 0)
        {
            _status.ForeColor = UiTheme.Normal;
            _status.Text = "✓ " + T("Admin.MasterDataComplete");
            return;
        }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = "⚠ " + T("Admin.MasterDataIncomplete") + " — " + string.Join(" · ", blockers);
    }

    private async Task LoadPrescribersAsync(CancellationToken ct = default)
    {
        var result = await _master.GetPrescribersAsync(
            string.IsNullOrWhiteSpace(_prescriberSearch.Text) ? null : _prescriberSearch.Text.Trim(),
            _includeInactive.Checked, ct);

        if (result.IsSuccess) { _prescriberRows = result.Value; RenderPrescribers(); }
    }

    private async Task LoadTargetsAsync(CancellationToken ct = default)
    {
        var material = (_qcMaterials.CurrentRow?.Tag as QcMaterialRow)?.QcMaterialId;

        var result = await _master.GetQcTargetsAsync(material, ct);

        if (result.IsSuccess) { _targetRows = result.Value; RenderTargets(); }
    }

    private void RenderAll()
    {
        RenderInstruments();
        RenderMaterials();
        RenderTargets();
        RenderStock();
        RenderSuppliers();
        RenderPrescribers();
        RenderPayers();
    }

    private void RenderInstruments()
    {
        if (_instruments.Columns.Count == 0) return;

        _instruments.Rows.Clear();

        foreach (var row in _instrumentRows)
        {
            var index = _instruments.Rows.Add(
                row.Code, row.Name.For(Localizer.CurrentLanguage),
                T($"EquipmentKind.{row.Kind}"), T($"EquipmentStatus.{row.Status}"),
                row.SerialNumber ?? string.Empty, row.Location ?? string.Empty,
                row.PreventiveMaintenanceIntervalDays is { } m ? $"{m} j" : "—",
                row.CalibrationIntervalDays is { } c ? $"{c} j" : "—",
                row.QcTargetCount.ToString(),
                row.HasInterfaceConfigured ? "✓" : string.Empty);

            _instruments.Rows[index].Tag = row;
        }

        _editInstrument.Enabled = _instruments.Rows.Count > 0;
    }

    private void RenderMaterials()
    {
        if (_qcMaterials.Columns.Count == 0) return;

        _qcMaterials.Rows.Clear();

        foreach (var row in _materialRows)
        {
            var state = !row.IsActive ? T("Admin.Inactive")
                : row.DaysToExpiry < 0 ? T("Inventory.Expired")
                : row.TargetCount == 0 ? T("Quality.NoTarget")
                : T("Inventory.Usable");

            var index = _qcMaterials.Rows.Add(
                row.LotNumber, row.Name.For(Localizer.CurrentLanguage),
                T($"QcLevel.{row.Level}"), row.ExpiryDate.ToString("dd/MM/yy"),
                row.DaysToExpiry.ToString(), row.Manufacturer ?? string.Empty,
                row.TargetCount.ToString(), state);

            _qcMaterials.Rows[index].Tag = row;
        }

        _editMaterial.Enabled = _qcMaterials.Rows.Count > 0;
        if (_qcMaterials.Rows.Count > 0) _qcMaterials.Rows[0].Selected = true;
    }

    private void RenderTargets()
    {
        if (_qcTargets.Columns.Count == 0) return;

        _qcTargets.Rows.Clear();

        foreach (var row in _targetRows)
        {
            var state = !row.HasUsableLimits ? T("Qc_NoTargetEstablished")
                : !row.IsActive ? T("Admin.Inactive")
                : row.LaboratoryMean is null ? T("Quality.SupplierLimits")
                : T("Quality.LaboratoryLimits");

            var index = _qcTargets.Rows.Add(
                row.MaterialLotNumber, T($"QcLevel.{row.Level}"),
                $"{row.ParameterCode} — {row.ParameterName.For(Localizer.CurrentLanguage)}",
                row.InstrumentCode,
                row.AssignedMean is { } am && row.AssignedSd is { } asd
                    ? $"{am:N3} ± {asd:N3}" : "—",
                row.LaboratoryMean is { } lm && row.LaboratorySd is { } lsd
                    ? $"{lm:N3} ± {lsd:N3}" : "—",
                row.LaboratoryPointCount.ToString(),
                CountRules(row.ActiveRules).ToString(),
                state);

            _qcTargets.Rows[index].Tag = row;
        }

        _editTarget.Enabled = _qcTargets.Rows.Count > 0;
    }

    private static int CountRules(WestgardRule rules) =>
        Enum.GetValues<WestgardRule>()
            .Count(r => r != WestgardRule.None && rules.HasFlag(r));

    private void RenderStock()
    {
        if (_stock.Columns.Count == 0) return;

        _stock.Rows.Clear();

        foreach (var row in _stockRows)
        {
            var flags = new List<string>(3);
            if (row.IsCritical) flags.Add("★");
            if (row.IsHazardous) flags.Add("☣");
            if (row.LotCount == 0) flags.Add("∅");

            var index = _stock.Rows.Add(
                row.Code, row.Name.For(Localizer.CurrentLanguage),
                T($"StockItemKind.{row.Kind}"), row.UnitOfMeasure,
                row.ReorderPoint.ToString("N2"), row.MinimumStock.ToString("N2"),
                T($"StorageCondition.{row.StorageCondition}"),
                row.SupplierName ?? string.Empty, string.Join(" ", flags));

            _stock.Rows[index].Tag = row;
        }

        _editStock.Enabled = _stock.Rows.Count > 0;
    }

    private void RenderSuppliers()
    {
        if (_suppliers.Columns.Count == 0) return;

        _suppliers.Rows.Clear();

        foreach (var row in _supplierRows)
        {
            var index = _suppliers.Rows.Add(
                row.Code, row.Name, row.Ice ?? string.Empty, row.ContactName ?? string.Empty,
                row.Phone ?? string.Empty, $"{row.LeadTimeDays} j",
                row.IsApproved ? T("Inventory.Approved") : string.Empty,
                row.LastEvaluationDate is { } date
                    ? $"{date:dd/MM/yy}" + (row.EvaluationScore is { } score ? $" ({score})" : string.Empty)
                    : "—",
                row.StockItemCount.ToString());

            _suppliers.Rows[index].Tag = row;
        }

        _editSupplier.Enabled = _suppliers.Rows.Count > 0;
    }

    private void RenderPrescribers()
    {
        if (_prescribers.Columns.Count == 0) return;

        _prescribers.Rows.Clear();

        foreach (var row in _prescriberRows)
        {
            var index = _prescribers.Rows.Add(
                Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic
                    && row.FullNameArabic is { Length: > 0 }
                        ? row.FullNameArabic : row.FullName,
                T($"PrescriberKind.{row.Kind}"),
                row.Speciality?.For(Localizer.CurrentLanguage) ?? string.Empty,
                row.ProfessionalOrderNumber ?? string.Empty,
                row.Inpe ?? string.Empty,
                row.InstitutionName ?? string.Empty,
                row.Phone ?? string.Empty,
                T($"ReportDeliveryPreference.{row.DeliveryPreference}"),
                row.OrderCount.ToString());

            _prescribers.Rows[index].Tag = row;
        }

        _editPrescriber.Enabled = _prescribers.Rows.Count > 0;
    }

    private void RenderPayers()
    {
        if (_payers.Columns.Count == 0) return;

        _payers.Rows.Clear();

        foreach (var row in _payerRows)
        {
            var index = _payers.Rows.Add(
                row.Code,
                row.Name.For(Localizer.CurrentLanguage),
                T($"PayerKind.{row.Kind}"),
                // The percentage, because that is the unit a convention is written in. The stored
                // value is the fraction the pricing calculator needs.
                $"{row.ReimbursementPercent:0.##} %",
                row.UsesNationalReferenceTariff ? T("Coverage.Tnr") : string.Empty,
                row.ThirdPartyPaymentAgreement ? T("Action.Yes") : string.Empty,
                row.AgreementNumber ?? string.Empty,
                $"{row.PaymentTermsDays} j",
                row.CoveredPatientCount.ToString(CultureInfo.CurrentCulture),
                row.OpenInvoiceCount.ToString(CultureInfo.CurrentCulture),
                string.Join(" · ", row.Problems.Select(T)));

            _payers.Rows[index].Tag = row;
        }

        _editPayer.Enabled = _payers.Rows.Count > 0;
    }

    // ---- Editors -------------------------------------------------------------------------------------

    private async Task EditInstrumentAsync(InstrumentMasterRow? row)
    {
        using var dialog = new InstrumentEditorDialog(Localizer, _dialogs, row, _supplierRows);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _master.SaveInstrumentAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Nav.Instruments")); return; }

        await ActivateAsync();
    }

    private async Task EditMaterialAsync(QcMaterialRow? row)
    {
        using var dialog = new QcMaterialEditorDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _master.SaveQcMaterialAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Quality.NewQcMaterial")); return; }

        await ActivateAsync();
    }

    private async Task EditTargetAsync(QcTargetRow? row)
    {
        if (_materialRows.Count == 0)
        {
            _dialogs.Warn(T("Quality.NeedMaterialFirst"), T("Quality.NewQcTarget"));
            return;
        }

        if (_instrumentRows.Count == 0)
        {
            _dialogs.Warn(T("Quality.NeedInstrumentFirst"), T("Quality.NewQcTarget"));
            return;
        }

        if (_parameters.Count == 0)
        {
            _dialogs.Warn(T("Quality.NeedParameterFirst"), T("Quality.NewQcTarget"));
            return;
        }

        using var dialog = new QcTargetEditorDialog(Localizer, _dialogs, row, _materialRows,
                                                     _instrumentRows, _parameters);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _master.SaveQcTargetAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Quality.NewQcTarget")); return; }

        await ActivateAsync();
    }

    private async Task EditStockAsync(StockItemMasterRow? row)
    {
        using var dialog = new StockItemEditorDialog(Localizer, _dialogs, row, _supplierRows);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _master.SaveStockItemAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Inventory.NewItem")); return; }

        await ActivateAsync();
    }

    private async Task EditSupplierAsync(SupplierRow? row)
    {
        using var dialog = new SupplierEditorDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _master.SaveSupplierAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Inventory.NewSupplier")); return; }

        await ActivateAsync();
    }

    private async Task EditPrescriberAsync(PrescriberRow? row)
    {
        using var dialog = new PrescriberEditorDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _master.SavePrescriberAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.NewPrescriber")); return; }

        await ActivateAsync();
    }

    private async Task EditPayerAsync(PayerRow? row)
    {
        using var dialog = new PayerEditorDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _master.SavePayerAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Coverage.NewPayer")); return; }

        await ActivateAsync();
    }
}
