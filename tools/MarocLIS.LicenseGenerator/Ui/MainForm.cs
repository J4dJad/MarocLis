using System.Globalization;
using System.Security.Cryptography;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Licensing;
using MarocLIS.LicenseGenerator.Core;

namespace MarocLIS.LicenseGenerator.Ui;

/// <summary>
/// The generator's window: the licences issued, the renewals to chase, the packs the form offers,
/// and the signing key.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly KeyVault _vault;
    private readonly string _registryPath;
    private readonly string _packsPath;
    private readonly Func<DateOnly> _today;

    private LicenceRegistry _registry;
    private List<LicencePacks.Pack> _packs;

    // Unlocked once per session, on the first signature; never written anywhere in clear.
    private ECDsa? _key;

    private readonly Label _keyStatus = Look.Text(string.Empty, Look.Body, UiTheme.TextOnAccent);
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Font = Look.Body, Padding = new Point(14, 6) };

    private readonly DataGridView _issued = Look.Grid();
    private readonly TextBox _filter = new() { Width = 260, Font = Look.Body, PlaceholderText = "Rechercher un laboratoire, un numéro…" };
    private readonly Button _new = Look.Primary("Nouvelle licence…", Glyph.FilePlus);
    private readonly Button _fromRequest = Look.Secondary("Depuis une demande…", Glyph.FolderOpen);
    private readonly Button _replace = Look.Secondary("Remplacer…", Glyph.RefreshCw);
    private readonly Button _saveFile = Look.Secondary("Enregistrer le fichier .lic…", Glyph.Download);
    private readonly Button _revoke = Look.Secondary("Révoquer…", Glyph.Ban);
    private readonly Button _revocationLine = Look.Secondary("Copier la ligne de révocation", Glyph.CopyPlus);

    private readonly DataGridView _renewals = Look.Grid();
    private readonly Label _renewalsHeading = Look.Text(string.Empty, Look.Bold);
    private readonly Button _renew = Look.Primary("Renouveler…", Glyph.CalendarCheck);
    private readonly Button _csv = Look.Secondary("Exporter en CSV…", Glyph.FileSpreadsheet);

    private readonly ListBox _packList = new() { Dock = DockStyle.Fill, Font = Look.Body, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _packName = new() { Width = 280, Font = Look.Body };
    private readonly NumericUpDown _packWorkstations = new() { Minimum = 1, Maximum = 999, Width = 90, Font = Look.Body };
    private readonly CheckedListBox _packModules = new() { CheckOnClick = true, Width = 300, Height = 290, Font = Look.Body, BorderStyle = BorderStyle.FixedSingle };

    private readonly Label _keyHeading = Look.Text(string.Empty, Look.Heading);
    private readonly TextBox _fingerprint = new() { ReadOnly = true, Width = 420, Font = Look.Mono, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _keyFolder = Look.Text(string.Empty, Look.Small, UiTheme.TextSecondary);
    private readonly Button _createKey = Look.Primary("Créer la clé…", Glyph.KeyRound);
    private readonly Button _exportPublic = Look.Secondary("Exporter la clé publique…", Glyph.Download);
    private readonly Button _backup = Look.Secondary("Exporter une sauvegarde chiffrée…", Glyph.ShieldCheck);
    private readonly Button _restore = Look.Secondary("Restaurer une sauvegarde…", Glyph.RotateCcw);

    public MainForm(string folder, Func<DateOnly>? today = null)
    {
        _vault = new KeyVault(folder);
        _registryPath = Path.Combine(folder, LicenceRegistry.FileName);
        _packsPath = Path.Combine(folder, PackPresets.FileName);
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
        _registry = LicenceRegistry.Load(_registryPath);
        _packs = [.. PackPresets.Load(_packsPath)];

        Text = "Générateur de licences MarocLIS";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 720);
        MinimumSize = new Size(980, 600);
        BackColor = UiTheme.Surface;
        Font = Look.Body;

        Controls.Add(_tabs);
        Controls.Add(Header());

        _tabs.TabPages.Add(IssuedTab());
        _tabs.TabPages.Add(RenewalsTab());
        _tabs.TabPages.Add(PacksTab());
        _tabs.TabPages.Add(KeyTab());

        RefreshAll();

        // Nothing can be signed before the key exists: the first start opens on its creation.
        if (!_vault.Exists) _tabs.SelectedIndex = 3;
    }

    /// <summary>Opens a tab by its position, for the documentation's screenshots.</summary>
    public void ShowTab(int index) => _tabs.SelectedIndex = index;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _key?.Dispose();
        base.Dispose(disposing);
    }

    // ---- Layout ----------------------------------------------------------------------------------

    private Panel Header()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = UiTheme.Accent, Padding = new Padding(20, 0, 20, 0) };
        var title = Look.Text("Générateur de licences MarocLIS", Look.Heading, UiTheme.TextOnAccent);
        title.Location = new Point(20, 14);
        var icon = new PictureBox { Image = Glyphs.Render(Glyph.KeyRound, UiTheme.TextOnAccent, 22), Size = new Size(28, 24), Location = new Point(20, 16) };
        title.Location = new Point(52, 14);
        _keyStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _keyStatus.AutoSize = true;
        header.Controls.AddRange([icon, title, _keyStatus]);
        header.Resize += (_, _) => _keyStatus.Location = new Point(header.Width - _keyStatus.Width - 20, 19);
        _keyStatus.SizeChanged += (_, _) => _keyStatus.Location = new Point(header.Width - _keyStatus.Width - 20, 19);
        return header;
    }

    private static TabPage Page(string text) => new(text) { BackColor = UiTheme.Surface, Padding = new Padding(16) };

    private TabPage IssuedTab()
    {
        var page = Page("Licences émises");

        Look.AddColumn(_issued, "number", "Numéro", 110);
        Look.AddColumn(_issued, "version", "Version", 55, right: true);
        Look.AddColumn(_issued, "laboratory", "Laboratoire", 190);
        Look.AddColumn(_issued, "kind", "Type", 80);
        Look.AddColumn(_issued, "period", "Validité", 170);
        Look.AddColumn(_issued, "workstations", "Postes", 55, right: true);
        Look.AddColumn(_issued, "modules", "Modules", 70, right: true);
        Look.AddColumn(_issued, "server", "Serveur", 170);
        Look.AddColumn(_issued, "state", "État", 110);
        _issued.SelectionChanged += (_, _) => UpdateIssuedButtons();
        _issued.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Replace(); };

        _new.Click += (_, _) => NewLicence(null);
        _fromRequest.Click += (_, _) => NewFromRequest();
        _replace.Click += (_, _) => Replace();
        _saveFile.Click += (_, _) => SaveFile(Selected(_issued));
        _revoke.Click += (_, _) => Revoke();
        _revocationLine.Click += (_, _) => CopyRevocationLine();
        _filter.TextChanged += (_, _) => FillIssued();
        _filter.Margin = new Padding(16, 4, 0, 0);

        page.Controls.Add(_issued);
        page.Controls.Add(Look.Bar(_new, _fromRequest, _replace, _saveFile, _revoke, _revocationLine, _filter));
        return page;
    }

    private TabPage RenewalsTab()
    {
        var page = Page("Renouvellements");

        Look.AddColumn(_renewals, "number", "Numéro", 110);
        Look.AddColumn(_renewals, "laboratory", "Laboratoire", 200);
        Look.AddColumn(_renewals, "kind", "Type", 80);
        Look.AddColumn(_renewals, "due", "Échéance", 90);
        Look.AddColumn(_renewals, "left", "Jours restants", 80, right: true);
        Look.AddColumn(_renewals, "workstations", "Postes", 55, right: true);
        Look.AddColumn(_renewals, "contact", "Contact", 180);
        _renewals.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == _renewals.Columns["left"]!.Index && e.Value is string text
                && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out var left))
                e.CellStyle!.ForeColor = left < 0 ? UiTheme.Critical : left <= 30 ? UiTheme.Abnormal : UiTheme.TextPrimary;
        };
        _renewals.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Renew(); };

        _renew.Click += (_, _) => Renew();
        _csv.Click += (_, _) => ExportCsv();

        page.Controls.Add(_renewals);
        page.Controls.Add(Look.Bar(_renew, _csv));
        page.Controls.Add(_renewalsHeading);
        _renewalsHeading.Dock = DockStyle.Top;
        _renewalsHeading.Padding = new Padding(0, 0, 0, 10);
        return page;
    }

    private TabPage PacksTab()
    {
        var page = Page("Packs");

        foreach (var module in LicenceModules.All) _packModules.Items.Add(GeneratorTexts.Module(module));
        _packList.SelectedIndexChanged += (_, _) => ShowPack();

        var add = Look.Secondary("Nouveau pack", Glyph.Plus);
        var save = Look.Primary("Enregistrer le pack", Glyph.Save);
        var delete = Look.Secondary("Supprimer", Glyph.Trash2);
        var reset = Look.Secondary("Packs d'origine", Glyph.RotateCcw);
        add.Click += (_, _) => AddPack();
        save.Click += (_, _) => SavePack();
        delete.Click += (_, _) => DeletePack();
        reset.Click += (_, _) => ResetPacks();

        var editor = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20, 6, 0, 0) };
        var note = Look.Text("Un pack ne fait que préremplir le formulaire : le fichier de licence liste toujours les modules un à un, "
                             + "et chaque licence peut s'écarter de son pack.", Look.Small, UiTheme.TextSecondary);
        note.MaximumSize = new Size(560, 0);
        editor.Controls.AddRange([
            note,
            Look.Text("Nom", Look.Bold), _packName,
            Look.Text("Postes simultanés", Look.Bold), _packWorkstations,
            Look.Text("Modules", Look.Bold), _packModules,
            Look.Bar(save, delete)
        ]);

        var left = new Panel { Dock = DockStyle.Left, Width = 360 };
        left.Controls.Add(_packList);
        left.Controls.Add(Look.Bar(add, reset));

        page.Controls.Add(editor);
        page.Controls.Add(left);
        return page;
    }

    private TabPage KeyTab()
    {
        var page = Page("Clé de signature");

        _createKey.Click += (_, _) => CreateKey();
        _exportPublic.Click += (_, _) => ExportPublicKey();
        _backup.Click += (_, _) => ExportBackup();
        _restore.Click += (_, _) => RestoreBackup();

        var steps = Look.Text(
            "1. Créez la clé une seule fois, sur l'ordinateur réservé aux licences, avec une phrase de passe d'au moins 12 caractères.\n"
            + "2. Exportez aussitôt une sauvegarde chiffrée sur deux supports gardés à part (coffre, clé USB) : sans elle, une panne "
            + "de cet ordinateur rend impossible toute nouvelle licence pour les installations existantes.\n"
            + "3. Exportez la clé publique et remplacez src/MarocLIS.Application/Licensing/licence-public-key.pem dans le code "
            + "source avant de compiler la version livrée : le script d'installation refuse la clé de développement.\n"
            + "4. Vérifiez que l'empreinte affichée par MarocLIS (Paramètres › Licence) est celle-ci.",
            Look.Body, UiTheme.TextSecondary);
        steps.MaximumSize = new Size(900, 0);

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        layout.Controls.AddRange([
            _keyHeading,
            Look.Text("Empreinte de la clé publique", Look.Bold), _fingerprint,
            _keyFolder,
            Look.Bar(_createKey, _exportPublic, _backup, _restore),
            steps
        ]);
        _fingerprint.Margin = new Padding(0, 0, 0, 6);
        _keyFolder.Margin = new Padding(0, 0, 0, 16);

        page.Controls.Add(layout);
        return page;
    }

    // ---- Display ---------------------------------------------------------------------------------

    private void RefreshAll()
    {
        var fingerprint = _vault.Fingerprint();
        _keyStatus.Text = fingerprint is null ? "Aucune clé de signature" : $"Clé {fingerprint}";
        _keyHeading.Text = fingerprint is null
            ? "Aucune clé de signature sur cet ordinateur"
            : fingerprint == LicenceKeys.DevelopmentFingerprint
                ? "Clé de DÉVELOPPEMENT — ne signez pas de licence réelle avec"
                : "Clé de signature en place";
        _fingerprint.Text = fingerprint ?? "—";
        _keyFolder.Text = $"Dossier : {_vault.Folder}";
        _createKey.Enabled = fingerprint is null;
        _exportPublic.Enabled = _backup.Enabled = fingerprint is not null;
        _new.Enabled = _fromRequest.Enabled = fingerprint is not null;

        FillIssued();
        FillRenewals();
        FillPacks();
    }

    private void FillIssued()
    {
        var today = _today();
        var filter = _filter.Text.Trim();
        _issued.Rows.Clear();

        foreach (var licence in _registry.Licences.OrderByDescending(l => l.IssuedAt))
        {
            var t = licence.Terms;
            if (filter.Length > 0
                && !t.Number.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !t.Laboratory.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !(t.Site ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var index = _issued.Rows.Add(t.Number, t.Version, t.Laboratory, GeneratorTexts.Kind(t.Kind), Period(t),
                                         t.Workstations, t.Modules.Count, t.ServerId ?? "non liée", State(licence, today));
            var row = _issued.Rows[index];
            row.Tag = licence;
            row.Cells["modules"].ToolTipText = string.Join(Environment.NewLine, t.Modules.Select(GeneratorTexts.Module));
            if (licence.IsRevoked) row.DefaultCellStyle.ForeColor = UiTheme.Critical;
            else if (!_registry.IsCurrent(licence)) row.DefaultCellStyle.ForeColor = UiTheme.TextMuted;
        }

        UpdateIssuedButtons();
    }

    private void FillRenewals()
    {
        var today = _today();
        var due = _registry.Renewals(today);
        _renewalsHeading.Text = due.Count == 0
            ? $"Aucune licence n'arrive à échéance dans les {LicenceRegistry.RenewalHorizonDays} jours."
            : $"{due.Count} licence(s) à renouveler : échéance dans les {LicenceRegistry.RenewalHorizonDays} jours, ou déjà passée sans renouvellement.";

        _renewals.Rows.Clear();
        foreach (var licence in due)
        {
            var end = licence.RenewalDue!.Value;
            var index = _renewals.Rows.Add(licence.Number, licence.Terms.Laboratory, GeneratorTexts.Kind(licence.Terms.Kind),
                                           end.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture),
                                           (end.DayNumber - today.DayNumber).ToString(CultureInfo.CurrentCulture),
                                           licence.Terms.Workstations, licence.Contact ?? string.Empty);
            _renewals.Rows[index].Tag = licence;
        }

        _renew.Enabled = _csv.Enabled = due.Count > 0;
    }

    private void FillPacks(int select = 0)
    {
        _packList.Items.Clear();
        foreach (var pack in _packs) _packList.Items.Add($"{pack.Name} — {pack.Workstations} postes");
        if (_packList.Items.Count > 0) _packList.SelectedIndex = Math.Clamp(select, 0, _packList.Items.Count - 1);
    }

    private static string Period(LicenceTerms t) => t.Kind switch
    {
        LicenceKind.Perpetual => $"depuis le {t.StartsOn:dd/MM/yyyy}, MAJ → {t.UpdatesUntil:dd/MM/yyyy}",
        LicenceKind.Training => $"depuis le {t.StartsOn:dd/MM/yyyy}, sans fin",
        _ => $"{t.StartsOn:dd/MM/yyyy} → {t.EndsOn:dd/MM/yyyy}"
    };

    private string State(IssuedLicence licence, DateOnly today)
    {
        if (licence.IsRevoked) return $"Révoquée le {licence.RevokedOn:dd/MM/yyyy}";
        if (!_registry.IsCurrent(licence)) return "Remplacée";
        if (licence.Terms.EndsOn is { } end && end < today)
            return end.AddDays(licence.Terms.GraceDays) >= today ? "En grâce" : "Échue";
        return "En vigueur";
    }

    private static IssuedLicence? Selected(DataGridView grid) => grid.CurrentRow?.Tag as IssuedLicence;

    private void UpdateIssuedButtons()
    {
        var licence = Selected(_issued);
        var signing = _vault.Exists;
        _replace.Enabled = signing && licence is not null && !licence.IsRevoked;
        _saveFile.Enabled = licence is not null;
        _revoke.Enabled = licence is not null && !licence.IsRevoked;
        _revocationLine.Enabled = licence?.IsRevoked == true;
    }

    // ---- Licences --------------------------------------------------------------------------------

    private void NewLicence(LicenceRequest? request)
    {
        var today = _today();
        var pack = _packs.FirstOrDefault(p => p.Name == LicencePacks.Professional.Name) ?? _packs.FirstOrDefault() ?? LicencePacks.Professional;
        var draft = LicenceDrafts.For(LicenceKind.Annual, _registry.NextNumber(today.Year), today, pack);

        using var editor = new LicenceEditorForm("Nouvelle licence", draft, _packs, today,
                                                 bind: LicenceDrafts.BindsByDefault(draft.Kind));
        if (request is not null) editor.Fill(request);
        if (editor.ShowDialog(this) == DialogResult.OK) Issue(editor.Result!, editor.Contact);
    }

    private void NewFromRequest()
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
            Warn("Ce fichier n'est pas une demande de licence MarocLIS.");
            return;
        }

        // A laboratory that already has a licence from this register asks for its replacement.
        var existing = request.Value.CurrentNumber is { } number
            ? _registry.Current().FirstOrDefault(l => l.Number == number)
            : null;
        if (existing is not null)
        {
            var terms = _registry.Replacement(existing, _today()) with { ServerId = request.Value.ServerId };
            Edit($"Remplacement de {existing.Number} — nouveau serveur", terms, existing.Contact);
            return;
        }

        NewLicence(request.Value);
    }

    private void Replace()
    {
        if (Selected(_issued) is not { IsRevoked: false } licence || !_vault.Exists) return;
        Edit($"Remplacement de {licence.Number} (version {licence.Version})", _registry.Replacement(licence, _today()), licence.Contact);
    }

    private void Renew()
    {
        if (Selected(_renewals) is not { } licence) return;
        Edit($"Renouvellement de {licence.Number}", _registry.Renewal(licence, _today()), licence.Contact);
    }

    private void Edit(string title, LicenceTerms terms, string? contact)
    {
        using var editor = new LicenceEditorForm(title, terms, _packs, _today(), contact, fixedKind: false);
        if (editor.ShowDialog(this) == DialogResult.OK) Issue(editor.Result!, editor.Contact);
    }

    /// <summary>Signs, checks the signature against the public key, records, and offers to save the file.</summary>
    private void Issue(LicenceTerms terms, string? contact)
    {
        if (UnlockedKey() is not { } key) return;

        var text = LicenceFile.Sign(terms, key);

        // The file is read back exactly as MarocLIS will read it, before anyone receives it.
        using (var publicKey = LicenceKeys.ImportPublicKey(File.ReadAllText(_vault.PublicKeyFile)))
        {
            var parsed = LicenceFile.Parse(text);
            if (parsed.IsFailure || !LicenceFile.Verify(parsed.Value, publicKey))
            {
                Warn("La licence signée ne se vérifie pas avec la clé publique de ce dossier : rien n'a été enregistré.");
                return;
            }
        }

        var issued = new IssuedLicence
        {
            Terms = LicenceFile.Normalised(terms),
            Content = text,
            IssuedAt = DateTimeOffset.Now,
            Contact = contact
        };
        _registry.Add(issued);
        _registry.Save(_registryPath);
        RefreshAll();

        SaveFile(issued);
    }

    private void SaveFile(IssuedLicence? licence)
    {
        if (licence is null) return;

        using var dialog = new SaveFileDialog
        {
            Title = "Fichier de licence à envoyer au laboratoire",
            FileName = LicenceDrafts.FileName(licence.Terms),
            Filter = "Licence MarocLIS (*.lic)|*.lic"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        File.WriteAllText(dialog.FileName, licence.Content);
    }

    private void Revoke()
    {
        if (Selected(_issued) is not { IsRevoked: false } licence) return;

        using var prompt = new TextPrompt($"Révoquer {licence.Number} version {licence.Version}",
            "Motif (impayé, fichier perdu ou volé…). La licence sera refusée par les versions de MarocLIS compilées "
            + "avec la ligne de révocation ; celles déjà installées ne le savent qu'à leur mise à jour.");
        if (prompt.ShowDialog(this) != DialogResult.OK) return;

        var revoked = _registry.Revoke(licence, _today(), prompt.Value);
        _registry.Save(_registryPath);
        RefreshAll();
        CopyLine(revoked);
    }

    private void CopyRevocationLine()
    {
        if (Selected(_issued) is { IsRevoked: true } licence) CopyLine(licence);
    }

    private void CopyLine(IssuedLicence licence)
    {
        var line = LicenceRegistry.RevocationLine(licence);
        Clipboard.SetText(line);
        MessageBox.Show(this,
            "Ligne copiée. Ajoutez-la à src/MarocLIS.Application/Licensing/licence-revoked.txt puis compilez et "
            + "livrez une nouvelle version :" + Environment.NewLine + Environment.NewLine + line,
            "Ligne de révocation", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Relances de renouvellement",
            FileName = $"renouvellements-{_today():yyyy-MM-dd}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // With a byte-order mark: Excel then reads the accents as UTF-8.
        File.WriteAllText(dialog.FileName, LicenceRegistry.RenewalsCsv(_registry.Renewals(_today()), _today()),
                          new System.Text.UTF8Encoding(true));
    }

    // ---- Packs -----------------------------------------------------------------------------------

    private void ShowPack()
    {
        if (_packList.SelectedIndex < 0 || _packList.SelectedIndex >= _packs.Count) return;
        var pack = _packs[_packList.SelectedIndex];
        _packName.Text = pack.Name;
        _packWorkstations.Value = Math.Clamp(pack.Workstations, 1, 999);
        for (var i = 0; i < LicenceModules.All.Count; i++)
            _packModules.SetItemChecked(i, pack.Modules.Contains(LicenceModules.All[i]));
    }

    private void AddPack()
    {
        _packs.Add(new LicencePacks.Pack("Nouveau pack", 2, []));
        PackPresets.Save(_packsPath, _packs);
        FillPacks(_packs.Count - 1);
    }

    private void SavePack()
    {
        if (_packList.SelectedIndex < 0 || string.IsNullOrWhiteSpace(_packName.Text)) return;

        var index = _packList.SelectedIndex;
        _packs[index] = new LicencePacks.Pack(_packName.Text.Trim(), (int)_packWorkstations.Value,
                                              [.. LicenceModules.All.Where((_, i) => _packModules.GetItemChecked(i))]);
        PackPresets.Save(_packsPath, _packs);
        FillPacks(index);
    }

    private void DeletePack()
    {
        if (_packList.SelectedIndex < 0 || _packs.Count <= 1) return;
        _packs.RemoveAt(_packList.SelectedIndex);
        PackPresets.Save(_packsPath, _packs);
        FillPacks();
    }

    private void ResetPacks()
    {
        _packs = [.. LicencePacks.Defaults];
        PackPresets.Save(_packsPath, _packs);
        FillPacks();
    }

    // ---- Key -------------------------------------------------------------------------------------

    private ECDsa? UnlockedKey()
    {
        if (_key is not null) return _key;
        if (!_vault.Exists)
        {
            Warn("Créez d'abord la clé de signature (onglet « Clé de signature »).");
            _tabs.SelectedIndex = 3;
            return null;
        }

        while (true)
        {
            using var prompt = new PassphraseForm("Déverrouiller la clé",
                "La phrase de passe de la clé de signature, une fois par session.", choosing: false);
            if (prompt.ShowDialog(this) != DialogResult.OK) return null;

            Cursor = Cursors.WaitCursor;
            _key = _vault.Unlock(prompt.Passphrase);
            Cursor = Cursors.Default;
            if (_key is not null) return _key;

            Warn("Phrase de passe incorrecte.");
        }
    }

    private void CreateKey()
    {
        using var prompt = new PassphraseForm("Créer la clé de signature",
            "La clé privée sera enregistrée chiffrée avec cette phrase de passe. Sans elle, la clé est inutilisable : "
            + "notez-la et gardez-la à part de la sauvegarde.", choosing: true);
        if (prompt.ShowDialog(this) != DialogResult.OK) return;

        Cursor = Cursors.WaitCursor;
        var fingerprint = _vault.Create(prompt.Passphrase);
        Cursor = Cursors.Default;
        RefreshAll();

        MessageBox.Show(this,
            $"Clé créée. Empreinte : {fingerprint}{Environment.NewLine}{Environment.NewLine}"
            + "Exportez maintenant une sauvegarde chiffrée, puis la clé publique pour MarocLIS.",
            "Clé de signature", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExportPublicKey()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Clé publique à intégrer à MarocLIS",
            FileName = "licence-public-key.pem",
            Filter = "Clé publique (*.pem)|*.pem"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        File.Copy(_vault.PublicKeyFile, dialog.FileName, overwrite: true);
    }

    private void ExportBackup()
    {
        if (UnlockedKey() is not { } key) return;

        using var prompt = new PassphraseForm("Phrase de passe de la sauvegarde",
            "La sauvegarde est chiffrée avec cette phrase de passe (la même que celle de la clé, ou une autre). "
            + "Gardez-la séparément du fichier.", choosing: true);
        if (prompt.ShowDialog(this) != DialogResult.OK) return;

        using var dialog = new SaveFileDialog
        {
            Title = "Sauvegarde chiffrée de la clé",
            FileName = $"cle-licences-{_today():yyyy-MM-dd}{KeyVault.BackupExtension}",
            Filter = $"Sauvegarde de clé (*{KeyVault.BackupExtension})|*{KeyVault.BackupExtension}"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        KeyVault.ExportBackup(key, prompt.Passphrase, dialog.FileName);
    }

    private void RestoreBackup()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Sauvegarde de la clé",
            Filter = $"Sauvegarde de clé (*{KeyVault.BackupExtension})|*{KeyVault.BackupExtension}|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        using var backupPrompt = new PassphraseForm("Phrase de passe de la sauvegarde", "Celle choisie à l'export de la sauvegarde.", choosing: false);
        if (backupPrompt.ShowDialog(this) != DialogResult.OK) return;

        using var newPrompt = new PassphraseForm("Phrase de passe sur cet ordinateur",
            "La clé restaurée sera chiffrée sur cet ordinateur avec cette phrase de passe.", choosing: true);
        if (newPrompt.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var fingerprint = _vault.RestoreBackup(dialog.FileName, backupPrompt.Passphrase, newPrompt.Passphrase);
            if (fingerprint is null)
            {
                Warn("Phrase de passe de la sauvegarde incorrecte.");
                return;
            }

            _key?.Dispose();
            _key = null;
            RefreshAll();
            MessageBox.Show(this, $"Clé restaurée. Empreinte : {fingerprint}", "Clé de signature",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            Warn(ex.Message);
        }
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}

/// <summary>One line of text asked for: the reason of a revocation.</summary>
internal sealed class TextPrompt : Form
{
    private readonly TextBox _value = new() { Width = 440, Font = Look.Body };

    public string Value => _value.Text.Trim();

    public TextPrompt(string title, string explanation)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = UiTheme.SurfaceRaised;
        Font = Look.Body;

        var ok = Look.Primary("Révoquer", Glyph.Ban);
        var cancel = Look.Secondary("Annuler", Glyph.X);
        ok.Click += (_, _) => { if (Value.Length > 0) DialogResult = DialogResult.OK; };
        cancel.DialogResult = DialogResult.Cancel;

        var intro = Look.Text(explanation);
        intro.MaximumSize = new Size(440, 0);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(20) };
        layout.Controls.AddRange([intro, _value, Look.Bar(ok, cancel)]);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
