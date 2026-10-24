using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Picks analyses to add to an order that already exists.
/// <para>
/// The same picker as order entry, with two differences that matter. What is already on the order
/// is listed as already present rather than silently absent — an operator told "the TSH is not
/// there" needs to see that it is, dated, not to search a list and conclude the system lost it. And
/// a reason is mandatory: an analysis appearing on a file after the fact is a change to a medical
/// request, and ISO 15189 requires changes to a request to say who asked and why. In practice the
/// answer is "prescriber telephoned", which is exactly the fact worth keeping.
/// </para>
/// </summary>
internal sealed class AnalysisPickerDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly ICatalogQueries _catalogQueries;
    private readonly IUserDialogs _dialogs;
    private readonly IClock _clock;
    private readonly HashSet<Guid> _alreadyOnOrder;

    private readonly TextBox _search = UiTheme.Input(320);
    private readonly DataGridView _catalog = UiTheme.Grid();
    private readonly ListBox _basket = new();
    private readonly TextBox _reason = UiTheme.Input(460);
    private readonly Label _total = new();

    private readonly List<CatalogPickerItem> _items = [];
    private readonly List<CatalogPickerItem> _chosen = [];

    internal IReadOnlyList<Guid> AnalysisIds => [.. _chosen.Where(i => !i.IsPanel).Select(i => i.Id)];
    internal IReadOnlyList<Guid> PanelIds => [.. _chosen.Where(i => i.IsPanel).Select(i => i.Id)];
    internal string Reason => _reason.Text.Trim();

    internal AnalysisPickerDialog(ILocalizer localizer, ICatalogQueries catalogQueries,
                                  IUserDialogs dialogs, IClock clock,
                                  string accessionNumber, string patientName,
                                  IReadOnlyList<Guid> alreadyOnOrder)
    {
        _localizer = localizer;
        _catalogQueries = catalogQueries;
        _dialogs = dialogs;
        _clock = clock;
        _alreadyOnOrder = [.. alreadyOnOrder];

        Text = $"{localizer["Order.AddAnalyses"]} — {accessionNumber}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 560);
        MinimumSize = new Size(760, 480);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var identity = new Label
        {
            Text = $"{patientName}   ·   {accessionNumber}",
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.Accent,
            AutoSize = false,
            Bounds = new Rectangle(16, 12, 860, 20)
        };

        _search.Bounds = new Rectangle(16, 36, 320, UiTheme.InputHeight);
        _search.PlaceholderText = localizer["Order.CatalogSearchPlaceholder"];

        BuildColumns();

        _catalog.Dock = DockStyle.None;
        _catalog.Bounds = new Rectangle(16, 70, 560, 340);
        _catalog.MultiSelect = false;

        var basketLabel = UiTheme.FieldLabel(localizer["Order.Basket"]);
        basketLabel.Location = new Point(592, 52);

        _basket.Bounds = new Rectangle(592, 70, 288, 310);
        _basket.BorderStyle = BorderStyle.FixedSingle;
        _basket.Font = UiTheme.Small;
        _basket.BackColor = UiTheme.SurfaceSunken;

        _total.Font = UiTheme.BodyBold;
        _total.ForeColor = UiTheme.Accent;
        _total.AutoSize = false;
        _total.Bounds = new Rectangle(592, 386, 288, 22);

        var remove = UiTheme.SecondaryButton(localizer["Action.Remove"]);
        remove.Bounds = new Rectangle(592, 412, 288, UiTheme.ButtonHeight - 4);
        remove.Click += (_, _) => RemoveSelected();

        var reasonLabel = UiTheme.FieldLabel(localizer["Order.AmendmentReason"]);
        reasonLabel.Location = new Point(16, 418);

        _reason.Bounds = new Rectangle(16, 436, 560, UiTheme.InputHeight);
        // As long as the reasons asked elsewhere on the file: the audit trail keeps 1000
        // characters and cut a longer reason without a word.
        _reason.MaxLength = 500;

        var notice = new Label
        {
            Text = localizer["Order.AddAnalysesNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(16, 466, 860, 30)
        };

        // Cancel first, the action at the far end, as in « Modifier l'en-tête » and the other dialogs.
        var ok = UiTheme.PrimaryButton(localizer["Order.AddAnalyses"]);
        ok.Bounds = new Rectangle(456, 506, 420, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(16, 506, 420, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([identity, _search, _catalog, basketLabel, _basket, _total, remove,
                           reasonLabel, _reason, notice, ok, cancel]);

        CancelButton = cancel;

        // A plain form, so the screens' column fitting is applied here: the codes and amounts came
        // out as « BI… » and « 89… ».
        GridColumns.Apply(this);

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);

        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SearchAsync();
        };

        _catalog.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) AddToBasket(e.RowIndex); };
        _catalog.CellFormatting += OnCellFormatting;

        RefreshTotal();

        Shown += async (_, _) => await SearchAsync();
    }

    private void BuildColumns()
    {
        void Add(string name, string header, int weight, Font? font = null, bool right = false)
            => _catalog.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("code", _localizer["Catalog.Analysis"], 12, UiTheme.MonospaceBold);
        Add("name", _localizer["Field.Name"], 34);
        Add("department", _localizer["Field.Department"], 14, UiTheme.Small);
        Add("price", _localizer["Billing.Net"], 12, UiTheme.Monospace, right: true);
        Add("notes", _localizer["Field.Comment"], 28, UiTheme.Small);
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _catalog.Rows.Count) return;
        if (_catalog.Rows[e.RowIndex].Tag is not CatalogPickerItem item) return;

        if (_alreadyOnOrder.Contains(item.Id))
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            e.CellStyle.BackColor = UiTheme.SurfaceSunken;
        }
        else if (_chosen.Any(c => c.Id == item.Id))
        {
            e.CellStyle!.ForeColor = UiTheme.Normal;
            e.CellStyle.Font = UiTheme.BodyBold;
        }
    }

    private async Task SearchAsync()
    {
        try
        {
            var found = await _catalogQueries.SearchPickerAsync(
                _search.Text, payerId: null, _clock.Today);

            _items.Clear();
            _items.AddRange(found);

            _catalog.SuspendLayout();
            _catalog.Rows.Clear();

            foreach (var item in _items)
            {
                var notes = new List<string>(4);

                if (_alreadyOnOrder.Contains(item.Id)) notes.Add(_localizer["Order.AlreadyOnOrder"]);
                if (item.IsPanel)
                    notes.Add($"{item.ComponentCount} {_localizer["Catalog.Analyses"].ToLowerInvariant()}");
                if (item.ConsentRequirement != ConsentRequirement.None)
                    notes.Add(_localizer["Consent.Required"]);
                if (item.IsSubcontracted) notes.Add(_localizer["Catalog.Subcontracted"]);

                var index = _catalog.Rows.Add(
                    item.Code,
                    item.Name.For(_localizer.CurrentLanguage),
                    item.DepartmentCode ?? string.Empty,
                    Money.Of(item.Price).Format(_localizer.CurrentLanguage),
                    string.Join(" · ", notes));

                _catalog.Rows[index].Tag = item;
            }

            _catalog.ResumeLayout(performLayout: true);
        }
        catch (Exception)
        {
            // The dialog stays usable with whatever the previous search returned; the failure is
            // surfaced when the operator tries to confirm with an empty basket.
        }
    }

    private void AddToBasket(int rowIndex)
    {
        if (_catalog.Rows[rowIndex].Tag is not CatalogPickerItem item) return;

        if (_alreadyOnOrder.Contains(item.Id))
        {
            _dialogs.Warn(_localizer.Format("Order_AnalysisAlreadyPresent", item.Code), Text);
            return;
        }

        if (_chosen.Any(c => c.Id == item.Id)) return;

        _chosen.Add(item);
        RefreshBasket();
    }

    private void RemoveSelected()
    {
        if (_basket.SelectedIndex < 0 || _basket.SelectedIndex >= _chosen.Count) return;

        _chosen.RemoveAt(_basket.SelectedIndex);
        RefreshBasket();
    }

    private void RefreshBasket()
    {
        _basket.Items.Clear();

        foreach (var item in _chosen)
        {
            _basket.Items.Add($"{item.Code}  {item.Name.For(_localizer.CurrentLanguage)}  "
                              + Money.Of(item.Price).Format(_localizer.CurrentLanguage));
        }

        RefreshTotal();
        _catalog.Invalidate();
    }

    private void RefreshTotal() =>
        _total.Text = $"{_chosen.Count} · "
                      + Money.Of(_chosen.Sum(c => c.Price)).Format(_localizer.CurrentLanguage);

    private void Accept()
    {
        // The rules of a correction of the request: one analysis at least, and its reason.
        if (OrderFileRules.AmendmentProblems(_chosen.Count, _reason.Text).FirstOrDefault() is { } problem)
        {
            _dialogs.Warn(_localizer[problem], Text);
            if (problem == "Order_AmendmentReasonRequired") _reason.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Corrects an order's header.
/// <para>
/// The prescriber is why this exists. It is copied at a counter from a handwritten prescription, it
/// is wrong often, and it is printed on the report and carried onto the AMO claim under the
/// prescriber's INPE — so a wrong one becomes a claim rejected weeks later for a reason nobody
/// traces back to a typing error at reception.
/// </para>
/// <para>
/// Coverage is shown but not editable. Changing the payer after registration would change the split
/// on acts already priced and possibly already invoiced; that is a credit note and a re-issue, not
/// a field edit, and offering it as one would be a lie about what the system can undo.
/// </para>
/// </summary>
internal sealed class OrderHeaderDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly OrderDetail _detail;

    private readonly TextBox _prescriber = UiTheme.Input(320);
    private readonly DateTimePicker _prescriptionDate = new();
    private readonly CheckBox _hasPrescriptionDate = new();
    private readonly ComboBox _priority = new();
    private readonly ComboBox _language = new();
    private readonly TextBox _clinical = UiTheme.Input(560);
    private readonly TextBox _diagnosis = UiTheme.Input(180);
    private readonly TextBox _ward = UiTheme.Input(240);
    private readonly TextBox _room = UiTheme.Input(140);
    private readonly TextBox _note = UiTheme.Input(560);
    private readonly TextBox _reason = UiTheme.Input(560);

    internal string Reason => _reason.Text.Trim();

    // A directory prescriber keeps its id and carries no free text; the field is read-only in
    // that case (OrderFileRules.HeaderRequest).
    internal AmendOrderHeaderRequest Request => OrderFileRules.HeaderRequest(
        _detail,
        _prescriber.Text,
        _hasPrescriptionDate.Checked ? DateOnly.FromDateTime(_prescriptionDate.Value.Date) : null,
        (OrderPriority)((_priority.SelectedItem as EnumChoice)?.Value ?? (int)_detail.Priority),
        _clinical.Text,
        _diagnosis.Text,
        _ward.Text,
        _room.Text,
        _note.Text,
        _language.SelectedItem is EnumChoice
            ? LanguageChoices.Selected(_language)
            : LanguageChoices.Coerce(_detail.ReportLanguage));

    internal OrderHeaderDialog(ILocalizer localizer, IUserDialogs dialogs, OrderDetail detail)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _detail = detail;

        // The lengths of the order's columns: a longer text failed the save with an unexpected error.
        foreach (var (box, length) in new[]
                 {
                     (_prescriber, 200), (_clinical, 2000), (_diagnosis, 20), (_ward, 120), (_room, 60),
                     (_note, 1000), (_reason, 500)
                 })
            box.MaxLength = length;

        Text = $"{localizer["Order.EditHeader"]} — {detail.AccessionNumber}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 470);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var identity = new Label
        {
            Text = $"{detail.PatientName}   ·   {detail.AccessionNumber}",
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.Accent,
            AutoSize = false,
            Bounds = new Rectangle(20, 14, 580, 20)
        };

        var coverage = new Label
        {
            Text = (detail.PayerName ?? localizer["Billing.PayerNotCovered"])
                   + "   —   " + localizer["Order.CoverageNotEditable"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 36, 580, 30)
        };

        var prescriberLabel = UiTheme.FieldLabel(localizer["Order.Prescriber"]);
        prescriberLabel.Location = new Point(20, 72);
        _prescriber.Location = new Point(20, 90);
        _prescriber.Text = detail.PrescriberName ?? detail.PrescriberFreeText ?? string.Empty;
        _prescriber.ReadOnly = detail.PrescriberId is not null;

        // A prescriber chosen from the directory is not retyped here: the row carries an INPE and a
        // telephone number for critical results, and overwriting the name while leaving the id
        // pointing elsewhere would make the report and the claim disagree.
        if (detail.PrescriberId is not null) _prescriber.BackColor = UiTheme.SurfaceSunken;

        _hasPrescriptionDate.Text = localizer["Order.PrescriptionDate"];
        _hasPrescriptionDate.AutoSize = true;
        _hasPrescriptionDate.Location = new Point(360, 70);
        _hasPrescriptionDate.Checked = detail.PrescriptionDate is not null;

        _prescriptionDate.Bounds = new Rectangle(360, 90, 220, UiTheme.InputHeight);
        _prescriptionDate.Format = DateTimePickerFormat.Short;
        _prescriptionDate.Enabled = _hasPrescriptionDate.Checked;
        _prescriptionDate.Value = detail.PrescriptionDate?.ToDateTime(TimeOnly.MinValue)
                                  ?? DateTime.Today;
        // Not after the file's registration, as the service requires: a later date was refused
        // only once the dialog was filled in.
        _prescriptionDate.MaxDate = OrderFileRules.MaxPrescriptionDate(detail, DateOnly.FromDateTime(DateTime.Today))
                                                  .ToDateTime(TimeOnly.MinValue);

        _hasPrescriptionDate.CheckedChanged +=
            (_, _) => _prescriptionDate.Enabled = _hasPrescriptionDate.Checked;

        var priorityLabel = UiTheme.FieldLabel(localizer["Field.Priority"]);
        priorityLabel.Location = new Point(20, 126);
        _priority.DropDownStyle = ComboBoxStyle.DropDownList;
        _priority.FlatStyle = FlatStyle.Flat;
        _priority.Bounds = new Rectangle(20, 144, 220, UiTheme.InputHeight);

        foreach (OrderPriority priority in Enum.GetValues<OrderPriority>())
            _priority.Items.Add(new EnumChoice((int)priority, localizer[$"Priority.{priority}"]));

        _priority.SelectedIndex = Array.IndexOf(Enum.GetValues<OrderPriority>(), detail.Priority);

        var languageLabel = UiTheme.FieldLabel(localizer["Order.ReportLanguage"]);
        languageLabel.Location = new Point(260, 126);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.FlatStyle = FlatStyle.Flat;
        _language.Bounds = new Rectangle(260, 144, 220, UiTheme.InputHeight);

        // Only the languages the laboratory works in, selected by value: a file registered in one
        // switched off since shows French, the language its report now goes out in.
        LanguageChoices.Fill(_language, l => localizer[$"AppLanguage.{l}"], detail.ReportLanguage);

        var clinicalLabel = UiTheme.FieldLabel(localizer["Order.ClinicalInformation"]);
        clinicalLabel.Location = new Point(20, 180);
        _clinical.Location = new Point(20, 198);
        _clinical.Text = detail.ClinicalInformation ?? string.Empty;

        var diagnosisLabel = UiTheme.FieldLabel(localizer["Order.DiagnosisCode"]);
        diagnosisLabel.Location = new Point(20, 234);
        _diagnosis.Location = new Point(20, 252);
        _diagnosis.Text = detail.DiagnosisCode ?? string.Empty;
        _diagnosis.Tag = UiTags.LatinValue;

        var wardLabel = UiTheme.FieldLabel(localizer["Order.Ward"]);
        wardLabel.Location = new Point(220, 234);
        _ward.Location = new Point(220, 252);
        _ward.Text = detail.WardOrUnit ?? string.Empty;

        var roomLabel = UiTheme.FieldLabel(localizer["Order.Room"]);
        roomLabel.Location = new Point(470, 234);
        _room.Location = new Point(470, 252);
        _room.Text = detail.RoomOrBed ?? string.Empty;

        var noteLabel = UiTheme.FieldLabel(localizer["Order.InternalNote"]);
        noteLabel.Location = new Point(20, 288);
        _note.Location = new Point(20, 306);
        _note.Text = detail.InternalNote ?? string.Empty;

        var reasonLabel = UiTheme.FieldLabel(localizer["Order.AmendmentReason"]);
        reasonLabel.Location = new Point(20, 342);
        _reason.Location = new Point(20, 360);

        var notice = new Label
        {
            Text = localizer["Order.AmendmentNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 390, 580, 30)
        };

        // « Annuler » then the primary at the far end, as in every other dialog.
        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(20, 426, 280, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(320, 426, 280, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        Controls.AddRange([identity, coverage, prescriberLabel, _prescriber, _hasPrescriptionDate,
                           _prescriptionDate, priorityLabel, _priority, languageLabel, _language,
                           clinicalLabel, _clinical, diagnosisLabel, _diagnosis, wardLabel, _ward,
                           roomLabel, _room, noteLabel, _note, reasonLabel, _reason, notice,
                           cancel, ok]);

        // Enter saves (every field is a single line); the reason is still required by Accept.
        AcceptButton = ok;
        CancelButton = cancel;

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    private void Accept()
    {
        if (OrderFileRules.AmendmentProblems(0, _reason.Text, needsAnalyses: false).FirstOrDefault() is { } problem)
        {
            _dialogs.Warn(_localizer[problem], Text);
            _reason.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
