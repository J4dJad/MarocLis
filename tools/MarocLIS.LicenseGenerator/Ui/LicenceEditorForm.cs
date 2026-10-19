using System.Text.RegularExpressions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Licensing;
using MarocLIS.LicenseGenerator.Core;

namespace MarocLIS.LicenseGenerator.Ui;

/// <summary>
/// One licence's terms: a new one, a replacement or a renewal. Every value the licensing design
/// left open — the trial's length, the warning, the grace, the bench days, the server binding, the
/// updates of a perpetual licence — is a field here, filled with the design's proposal.
/// </summary>
internal sealed partial class LicenceEditorForm : Form
{
    private readonly DateOnly _today;
    private readonly IReadOnlyList<LicencePacks.Pack> _packs;
    private readonly LicenceTerms _original;
    private readonly bool? _bindDefault;
    private bool _loading;

    private readonly TextBox _number = Field(readOnly: true);
    private readonly Label _version = Look.Text(string.Empty, Look.Bold);
    private readonly ComboBox _kind = Choice();
    private readonly ComboBox _pack = Choice();
    private readonly TextBox _laboratory = Field();
    private readonly TextBox _authorisation = Field();
    private readonly TextBox _site = Field();
    private readonly TextBox _contact = Field();
    private readonly DateTimePicker _start = Date();
    private readonly DateTimePicker _end = Date();
    private readonly DateTimePicker _updates = Date();
    private readonly NumericUpDown _workstations = Number(1, 999);
    private readonly CheckBox _bind = new() { Text = "Lier la licence au serveur du laboratoire", AutoSize = true, Font = Look.Body };
    private readonly TextBox _serverId = Field();
    private readonly Button _loadRequest = Look.Secondary("Charger une demande…", Glyph.FolderOpen);
    private readonly NumericUpDown _warning = Number(0, 365);
    private readonly NumericUpDown _grace = Number(0, 365);
    private readonly NumericUpDown _bench = Number(0, 365);
    private readonly CheckedListBox _modules = new()
    {
        CheckOnClick = true,
        IntegralHeight = false,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Look.Body,
        Dock = DockStyle.Fill
    };
    private readonly Label _problem = Look.Text(string.Empty, Look.Body, UiTheme.Critical);

    private static readonly LicenceKind[] Kinds = [LicenceKind.Annual, LicenceKind.Perpetual, LicenceKind.Trial, LicenceKind.Training];

    /// <summary>The terms to sign, once the form is accepted.</summary>
    public LicenceTerms? Result { get; private set; }

    public string? Contact => string.IsNullOrWhiteSpace(_contact.Text) ? null : _contact.Text.Trim();

    public LicenceEditorForm(string title, LicenceTerms terms, IReadOnlyList<LicencePacks.Pack> packs, DateOnly today,
                             string? contact = null, bool fixedKind = false, bool? bind = null)
    {
        _today = today;
        _bindDefault = bind;
        _packs = packs;
        _original = terms;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1000, 700);
        BackColor = UiTheme.SurfaceRaised;
        Font = Look.Body;

        BuildLayout();

        foreach (var kind in Kinds) _kind.Items.Add(GeneratorTexts.Kind(kind));
        _pack.Items.Add("— modules choisis un à un —");
        foreach (var pack in packs) _pack.Items.Add($"{pack.Name} ({pack.Workstations} postes, {pack.Modules.Count} modules)");
        foreach (var module in LicenceModules.All) _modules.Items.Add(GeneratorTexts.Module(module));

        _kind.Enabled = !fixedKind;
        Show(terms);
        _contact.Text = contact ?? string.Empty;

        _kind.SelectedIndexChanged += (_, _) => KindChanged();
        _pack.SelectedIndexChanged += (_, _) => PackChosen();
        _bind.CheckedChanged += (_, _) => _serverId.Enabled = _loadRequest.Enabled = _bind.Checked;
        _loadRequest.Click += (_, _) => LoadRequest();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(20, 16, 20, 12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var numberRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _number.Width = 170;
        _version.Margin = new Padding(10, 6, 0, 0);
        numberRow.Controls.AddRange([_number, _version]);

        Row(fields, "Numéro", numberRow);
        Row(fields, "Type", _kind);
        Row(fields, "Pack", _pack);
        Row(fields, "Laboratoire", _laboratory);
        Row(fields, "N° d'autorisation", _authorisation);
        Row(fields, "Site", _site);
        Row(fields, "Contact (relances)", _contact);
        Row(fields, "Début", _start);
        Row(fields, "Fin", _end);
        Row(fields, "Mises à jour jusqu'au", _updates);
        Row(fields, "Postes simultanés", _workstations);

        var server = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        _serverId.Width = 240;
        _serverId.Font = Look.Mono;
        _serverId.PlaceholderText = "XXXX-XXXX-XXXX-XXXX-XXXX";
        var serverLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        serverLine.Controls.AddRange([_serverId, _loadRequest]);
        _loadRequest.Margin = new Padding(8, 0, 0, 0);
        server.Controls.AddRange([_bind, serverLine]);
        Row(fields, "Serveur", server);

        var periods = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var (label, box) in new[] { ("préavis", _warning), ("grâce", _grace), ("paillasse", _bench) })
        {
            box.Width = 64;
            periods.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 4, 0), ForeColor = UiTheme.TextSecondary });
            periods.Controls.Add(box);
            box.Margin = new Padding(0, 0, 14, 0);
        }

        Row(fields, "Périodes (jours)", periods);

        var hint = Look.Text("Préavis : bandeau orange avant la fin. Grâce : jours après la fin encore en pleine "
                             + "utilisation. Paillasse : jours de lecture seule où les dossiers en cours peuvent "
                             + "encore être saisis, validés et diffusés.", Look.Small, UiTheme.TextSecondary);
        hint.MaximumSize = new Size(500, 0);
        fields.Controls.Add(new Label(), 0, fields.RowCount);
        fields.Controls.Add(hint, 1, fields.RowCount++);

        var modules = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(16, 0, 0, 0) };
        modules.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        modules.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        modules.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        modules.Controls.Add(Look.Text("Modules optionnels", Look.Bold), 0, 0);
        modules.Controls.Add(_modules, 0, 1);
        var socle = Look.Text("Le socle (patients, demandes, prélèvements, résultats, validation, comptes rendus, "
                              + "facturation, caisse, catalogue, paramètres) est toujours inclus et n'est pas listé.",
                              Look.Small, UiTheme.TextSecondary);
        socle.MaximumSize = new Size(300, 0);
        modules.Controls.Add(socle, 0, 2);

        var sign = Look.Primary("Signer et enregistrer…", Glyph.Signature);
        var cancel = Look.Secondary("Annuler", Glyph.X);
        cancel.DialogResult = DialogResult.Cancel;
        sign.Click += (_, _) => Accept();
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.AddRange([cancel, sign]);

        root.Controls.Add(fields, 0, 0);
        root.Controls.Add(modules, 1, 0);
        _problem.MaximumSize = new Size(840, 0);
        root.Controls.Add(_problem, 0, 1);
        root.SetColumnSpan(_problem, 2);
        root.Controls.Add(buttons, 0, 2);
        root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
    }

    private static void Row(TableLayoutPanel table, string label, Control control)
    {
        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(0, 7, 8, 0)
        };
        control.Margin = new Padding(0, 2, 0, 6);
        table.Controls.Add(caption, 0, table.RowCount);
        table.Controls.Add(control, 1, table.RowCount);
        table.RowCount++;
    }

    private static TextBox Field(bool readOnly = false) => new() { Width = 360, ReadOnly = readOnly, Font = Look.Body };
    private static ComboBox Choice() => new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDownList, Font = Look.Body };
    private static DateTimePicker Date() => new() { Width = 160, Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy", Font = Look.Body };
    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 90, Font = Look.Body };

    private void Show(LicenceTerms terms)
    {
        _loading = true;
        _number.Text = terms.Number;
        _version.Text = $"version {terms.Version}";
        _kind.SelectedIndex = Array.IndexOf(Kinds, terms.Kind);
        _pack.SelectedIndex = 1 + _packs.ToList().FindIndex(p =>
            p.Workstations == terms.Workstations && p.Modules.Order().SequenceEqual(terms.Modules.Order()));
        _laboratory.Text = terms.Laboratory;
        _authorisation.Text = terms.Authorisation ?? string.Empty;
        _site.Text = terms.Site ?? string.Empty;
        _start.Value = ToDate(terms.StartsOn);
        _workstations.Value = Math.Clamp(terms.Workstations, 1, 999);
        _bind.Checked = _bindDefault ?? terms.ServerId is not null;
        _serverId.Text = terms.ServerId ?? string.Empty;
        _serverId.Enabled = _loadRequest.Enabled = _bind.Checked;
        _warning.Value = terms.WarningDays;
        _grace.Value = terms.GraceDays;
        _bench.Value = terms.BenchClearingDays;
        ShowDates(terms);
        for (var i = 0; i < LicenceModules.All.Count; i++)
            _modules.SetItemChecked(i, terms.HasModule(LicenceModules.All[i]));
        _loading = false;
    }

    private void ShowDates(LicenceTerms terms)
    {
        _end.Enabled = terms.Kind is LicenceKind.Trial or LicenceKind.Annual;
        _updates.Enabled = terms.Kind == LicenceKind.Perpetual;
        _end.Value = ToDate(terms.EndsOn ?? terms.StartsOn);
        _updates.Value = ToDate(terms.UpdatesUntil ?? terms.StartsOn.AddYears(1));
        _workstations.Enabled = terms.Kind != LicenceKind.Training;
    }

    private void KindChanged()
    {
        if (_loading || _kind.SelectedIndex < 0) return;

        var kind = Kinds[_kind.SelectedIndex];
        var terms = LicenceDrafts.Apply(Read(), kind, _today);
        _loading = true;
        ShowDates(terms);
        _workstations.Value = Math.Clamp(terms.Workstations, 1, 999);
        _bind.Checked = LicenceDrafts.BindsByDefault(kind);
        _loading = false;
    }

    private void PackChosen()
    {
        if (_loading || _pack.SelectedIndex < 1) return;

        var pack = _packs[_pack.SelectedIndex - 1];
        if (Kinds[_kind.SelectedIndex] != LicenceKind.Training) _workstations.Value = Math.Clamp(pack.Workstations, 1, 999);
        for (var i = 0; i < LicenceModules.All.Count; i++)
            _modules.SetItemChecked(i, pack.Modules.Contains(LicenceModules.All[i]));
    }

    private void LoadRequest()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Demande de licence du laboratoire",
            Filter = $"Demande de licence (*{LicenceRequest.Extension})|*{LicenceRequest.Extension}|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var request = LicenceRequest.Parse(File.ReadAllText(dialog.FileName));
        if (request.IsFailure)
        {
            _problem.Text = "Ce fichier n'est pas une demande de licence MarocLIS.";
            return;
        }

        Fill(request.Value);
    }

    /// <summary>The laboratory's request fills what it knows: the server first, then the names left empty.</summary>
    public void Fill(LicenceRequest request)
    {
        _bind.Checked = true;
        _serverId.Text = request.ServerId;
        if (_laboratory.Text.Length == 0) _laboratory.Text = request.Laboratory ?? request.Site;
        if (_site.Text.Length == 0) _site.Text = request.Site;
        if (_authorisation.Text.Length == 0) _authorisation.Text = request.Authorisation ?? string.Empty;
        _problem.Text = request.CurrentNumber is { } current && current != _number.Text
            ? $"Le laboratoire a déjà la licence {current} (version {request.CurrentVersion}) : pour la prolonger, "
              + "utilisez plutôt « Remplacer » ou « Renouveler » sur celle-ci."
            : string.Empty;
    }

    private LicenceTerms Read()
    {
        var kind = _kind.SelectedIndex >= 0 ? Kinds[_kind.SelectedIndex] : _original.Kind;
        return _original with
        {
            Kind = kind,
            Laboratory = _laboratory.Text.Trim(),
            Authorisation = Blank(_authorisation.Text),
            Site = Blank(_site.Text),
            StartsOn = DateOnly.FromDateTime(_start.Value),
            EndsOn = kind is LicenceKind.Trial or LicenceKind.Annual ? DateOnly.FromDateTime(_end.Value) : null,
            UpdatesUntil = kind == LicenceKind.Perpetual ? DateOnly.FromDateTime(_updates.Value) : null,
            Workstations = kind == LicenceKind.Training ? 1 : (int)_workstations.Value,
            Modules = [.. LicenceModules.All.Where((_, i) => _modules.GetItemChecked(i))],
            ServerId = _bind.Checked ? ServerIdentity.Normalise(_serverId.Text) : null,
            WarningDays = (int)_warning.Value,
            GraceDays = (int)_grace.Value,
            BenchClearingDays = (int)_bench.Value
        };
    }

    private void Accept()
    {
        var terms = Read();

        var problems = terms.Problems().Select(GeneratorTexts.Problem).ToList();
        if (_bind.Checked && !ServerIdPattern().IsMatch(terms.ServerId ?? string.Empty))
            problems.Add("L'identifiant serveur se lit sur l'onglet Licence du laboratoire, ou se charge depuis sa demande.");
        if (terms.Kind is LicenceKind.Trial or LicenceKind.Annual && terms.EndsOn < _today)
            problems.Add("Cette licence serait déjà échue : MarocLIS la refuserait à l'import.");

        if (problems.Count > 0)
        {
            _problem.Text = string.Join(Environment.NewLine, problems);
            return;
        }

        Result = terms;
        DialogResult = DialogResult.OK;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static DateTime ToDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    [GeneratedRegex("^([0-9A-F]{4}-){4}[0-9A-F]{4}$")]
    private static partial Regex ServerIdPattern();
}
