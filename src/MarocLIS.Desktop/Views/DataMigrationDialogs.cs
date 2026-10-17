using MarocLIS.Application.Migration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;
using Microsoft.Win32;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// « Se connecter à l'ancienne base »: the kind of database first — SQL Server or SQL Server
/// Express, MySQL or MariaDB, or any other through ODBC — then only the fields that kind needs.
/// The connection is tested before the dialog closes, and the databases the login sees are listed
/// for SQL Server and MySQL. Nothing is ever written to that database.
/// </summary>
internal sealed class SourceConnectionDialog : LocalizedForm
{
    private const int FieldX = 20;
    private const int FullWidth = 520;

    private readonly DataMigrationService _migration;
    private readonly IUserDialogs _dialogs;

    private readonly Label _typeLabel = Caption(), _serverLabel = Caption(), _portLabel = Caption(),
                           _dsnLabel = Caption(), _stringLabel = Caption(), _userLabel = Caption(),
                           _passwordLabel = Caption(), _databaseLabel = Caption(), _authLabel = Caption();
    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _server = UiTheme.Input(360);
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 3306, Width = 140 };
    private readonly ComboBox _dsn = new() { DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, MaxLength = 128 };
    private readonly TextBox _connectionString = UiTheme.Input(FullWidth);
    private readonly RadioButton _windowsAuth = new() { AutoSize = true };
    private readonly RadioButton _sqlAuth = new() { AutoSize = true };
    private readonly TextBox _user = UiTheme.Input(250);
    private readonly TextBox _password = UiTheme.Input(250);
    private readonly ComboBox _database = new() { DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, MaxLength = 128 };
    private readonly Button _listDatabases = UiTheme.SecondaryButton(string.Empty);
    private readonly CheckBox _remember = new() { AutoSize = true };
    private readonly Label _status = new();
    private readonly Button _test = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _connect = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    private bool _busy;

    public SourceConnectionDialog(DataMigrationService migration, IUserDialogs dialogs, ILocalizer localizer,
                                  ICultureManager cultureManager, SourceConnectionSettings? initial,
                                  bool rememberPassword)
        : base(localizer, cultureManager)
    {
        _migration = migration;
        _dialogs = dialogs;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(FullWidth + 2 * FieldX, 380);
        BackColor = UiTheme.SurfaceRaised;

        _server.MaxLength = 200;
        _connectionString.MaxLength = 1000;
        _user.MaxLength = 128;
        _password.MaxLength = 128;
        _password.UseSystemPasswordChar = true;
        foreach (var box in new Control[] { _server, _connectionString, _user, _password, _dsn, _database })
            box.Tag = UiTags.LatinValue;

        BuildLayout();
        Fill(initial ?? new SourceConnectionSettings());
        _remember.Checked = rememberPassword;

        _type.SelectedIndexChanged += (_, _) => ShowFields();
        _windowsAuth.CheckedChanged += (_, _) => ShowFields();
        _test.Click += async (_, _) => await TestAsync(close: false);
        _connect.Click += async (_, _) => await TestAsync(close: true);
        _listDatabases.Click += async (_, _) => await ListDatabasesAsync();
        _cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = _connect;
        CancelButton = _cancel;

        ShowFields();
    }

    /// <summary>The settings the operator connected with, password included (held in memory only).</summary>
    public SourceConnectionSettings Settings { get; private set; } = new();

    public bool RememberPassword => _remember.Checked;

    private static Label Caption() => new() { AutoSize = true, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary };

    private SourceProvider Provider => _type.SelectedIndex switch
    {
        1 => SourceProvider.MySql,
        2 => SourceProvider.Odbc,
        _ => SourceProvider.SqlServer
    };

    private void BuildLayout()
    {
        _type.Width = FullWidth;
        _dsn.Width = FullWidth;
        _database.Width = 360;

        Place(_typeLabel, _type, 4, FieldX);
        Place(_serverLabel, _server, 58, FieldX);
        Place(_portLabel, _port, 58, FieldX + 380);
        Place(_dsnLabel, _dsn, 58, FieldX);

        _authLabel.Location = new Point(FieldX, 112);
        _windowsAuth.Location = new Point(FieldX, 132);
        _sqlAuth.Location = new Point(FieldX + 250, 132);
        Place(_stringLabel, _connectionString, 112, FieldX);

        Place(_userLabel, _user, 166, FieldX);
        Place(_passwordLabel, _password, 166, FieldX + 270);
        Place(_databaseLabel, _database, 220, FieldX);
        _listDatabases.Bounds = new Rectangle(FieldX + 380, 237, 140, UiTheme.ButtonHeight);

        _remember.Location = new Point(FieldX, 278);
        _remember.Font = UiTheme.Body;

        _status.Bounds = new Rectangle(FieldX, 304, FullWidth, 40);
        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.AutoEllipsis = true;

        _test.Bounds = new Rectangle(FieldX, 350, 170, UiTheme.ButtonHeight);
        _cancel.Bounds = new Rectangle(FieldX + 250, 350, 110, UiTheme.ButtonHeight);
        _connect.Bounds = new Rectangle(FieldX + 370, 350, 150, UiTheme.ButtonHeight);

        Controls.AddRange([_typeLabel, _type, _serverLabel, _server, _portLabel, _port, _dsnLabel, _dsn,
                           _authLabel, _windowsAuth, _sqlAuth, _stringLabel, _connectionString,
                           _userLabel, _user, _passwordLabel, _password, _databaseLabel, _database, _listDatabases,
                           _remember, _status, _test, _cancel, _connect]);

        AdminDialogFrame.Attach(this, Localizer, AdminDialogFrame.Lead(Localizer, nameof(SourceConnectionDialog)),
                                () => Localizer["Migration.ConnectTitle"]);
    }

    private void Place(Label label, Control field, int y, int x)
    {
        label.Location = new Point(x, y);
        field.Location = new Point(x, y + 18);
        Controls.Add(label);
        Controls.Add(field);
    }

    private void Fill(SourceConnectionSettings settings)
    {
        _type.Items.AddRange(["", "", ""]);
        _type.SelectedIndex = settings.Provider switch { SourceProvider.MySql => 1, SourceProvider.Odbc => 2, _ => 0 };
        _server.Text = settings.Server ?? string.Empty;
        _port.Value = Math.Clamp(settings.Port ?? 3306, 1, 65535);
        _dsn.Text = settings.OdbcDsn ?? string.Empty;
        _connectionString.Text = settings.OdbcConnectionString ?? string.Empty;
        _windowsAuth.Checked = settings.IntegratedSecurity || (settings.Provider == SourceProvider.SqlServer && settings.UserName is null);
        _sqlAuth.Checked = !_windowsAuth.Checked;
        _user.Text = settings.UserName ?? string.Empty;
        _password.Text = settings.Password ?? string.Empty;
        _database.Text = settings.Database ?? string.Empty;

        foreach (var dsn in OdbcDataSources()) _dsn.Items.Add(dsn);
    }

    protected override void ApplyTexts()
    {
        Text = T("Migration.ConnectTitle");
        _typeLabel.Text = T("Migration.DatabaseType");
        var selected = _type.SelectedIndex;
        _type.Items[0] = T("Migration.TypeSqlServer");
        _type.Items[1] = T("Migration.TypeMySql");
        _type.Items[2] = T("Migration.TypeOdbc");
        _type.SelectedIndex = selected;

        _serverLabel.Text = T("Migration.Server");
        _portLabel.Text = T("Migration.Port");
        _dsnLabel.Text = T("Migration.OdbcDsn");
        _stringLabel.Text = T("Migration.OdbcConnectionString");
        _authLabel.Text = T("Migration.Authentication");
        _windowsAuth.Text = T("Migration.WindowsAuthentication");
        _sqlAuth.Text = T("Migration.SqlAuthentication");
        _userLabel.Text = T("Migration.User");
        _passwordLabel.Text = T("Migration.Password");
        _databaseLabel.Text = T("Migration.Database");
        _listDatabases.Text = T("Migration.ListDatabases");
        _remember.Text = T("Migration.RememberPassword");
        _test.Text = T("Migration.TestConnection");
        _connect.Text = T("Migration.Connect");
        _cancel.Text = T("Action.Cancel");
        ShowFields();
    }

    private string T(string key) => Localizer[key];

    private void ShowFields()
    {
        var provider = Provider;
        var sql = provider == SourceProvider.SqlServer;
        var odbc = provider == SourceProvider.Odbc;

        _serverLabel.Visible = _server.Visible = !odbc;
        _portLabel.Visible = _port.Visible = provider == SourceProvider.MySql;
        _dsnLabel.Visible = _dsn.Visible = odbc;
        _stringLabel.Visible = _connectionString.Visible = odbc;
        _authLabel.Visible = _windowsAuth.Visible = _sqlAuth.Visible = sql;
        _databaseLabel.Visible = _database.Visible = _listDatabases.Visible = !odbc;

        var login = !sql || _sqlAuth.Checked;
        _user.Enabled = _password.Enabled = login;
        _remember.Enabled = login;

        _serverLabel.Text = T(sql ? "Migration.Server" : "Migration.ServerHost");
        _server.PlaceholderText = sql ? @".\SQLEXPRESS" : "localhost";
        _connectionString.PlaceholderText = @"Driver={…};Dbq=C:\…";
    }

    private SourceConnectionSettings Read() => new()
    {
        Provider = Provider,
        Server = Blank(_server.Text),
        Port = Provider == SourceProvider.MySql ? (int)_port.Value : null,
        Database = Provider == SourceProvider.Odbc ? null : Blank(_database.Text),
        IntegratedSecurity = Provider == SourceProvider.SqlServer && _windowsAuth.Checked,
        UserName = Provider == SourceProvider.SqlServer && _windowsAuth.Checked ? null : Blank(_user.Text),
        Password = Provider == SourceProvider.SqlServer && _windowsAuth.Checked ? null : _password.Text,
        OdbcDsn = Provider == SourceProvider.Odbc ? Blank(_dsn.Text) : null,
        OdbcConnectionString = Provider == SourceProvider.Odbc ? Blank(_connectionString.Text) : null
    };

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private string? Missing(SourceConnectionSettings settings)
    {
        if (settings.Provider == SourceProvider.Odbc)
            return settings.OdbcDsn is null && settings.OdbcConnectionString is null ? T("Migration_OdbcSourceRequired") : null;
        if (settings.Server is null) return T("Migration.ServerRequired");
        if (!settings.IntegratedSecurity && settings.UserName is null) return T("Migration.UserRequired");
        return null;
    }

    private async Task TestAsync(bool close)
    {
        if (_busy) return;
        var settings = Read();
        if (Missing(settings) is { } missing) { Show(missing, error: true); return; }

        // A database must be chosen before the dialog closes: the table list is read from it.
        if (close && settings.Provider != SourceProvider.Odbc && settings.Database is null)
        {
            Show(T("Migration.DatabaseRequired"), error: true);
            return;
        }

        await Busy(async () =>
        {
            Show(T("Migration.Connecting"), error: false);
            var tested = await _migration.TestConnectionAsync(settings);
            if (tested.IsFailure) { Show(Localizer.Describe(tested.Errors), error: true); return; }

            Show(T("Migration.ConnectionOk", tested.Value), error: false);
            if (!close) return;

            Settings = settings;
            DialogResult = DialogResult.OK;
            Close();
        });
    }

    private async Task ListDatabasesAsync()
    {
        if (_busy) return;
        var settings = Read();
        if (Missing(settings) is { } missing) { Show(missing, error: true); return; }

        await Busy(async () =>
        {
            Show(T("Migration.Connecting"), error: false);
            var databases = await _migration.ListDatabasesAsync(settings);
            if (databases.IsFailure) { Show(Localizer.Describe(databases.Errors), error: true); return; }

            var typed = _database.Text;
            _database.Items.Clear();
            foreach (var name in databases.Value) _database.Items.Add(name);
            _database.Text = typed;
            if (string.IsNullOrWhiteSpace(typed) && _database.Items.Count > 0) _database.SelectedIndex = 0;
            Show(T("Migration.DatabasesListed", databases.Value.Count), error: false);
            if (_database.Items.Count > 0) _database.DroppedDown = true;
        });
    }

    private string T(string key, params object?[] args) => Localizer.Format(key, args);

    private async Task Busy(Func<Task> work)
    {
        _busy = true;
        UseWaitCursor = true;
        _test.Enabled = _connect.Enabled = _listDatabases.Enabled = false;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Show(T(FailureMessages.KeyFor(ex)), error: true);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            if (!IsDisposed) _test.Enabled = _connect.Enabled = _listDatabases.Enabled = true;
        }
    }

    private void Show(string message, bool error)
    {
        if (IsDisposed) return;
        _status.Text = message;
        _status.ForeColor = error ? UiTheme.Critical : UiTheme.TextSecondary;
    }

    /// <summary>The data source names declared on the workstation, for the user and for all users.</summary>
    private static IEnumerable<string> OdbcDataSources()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return names;

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources");
                if (key is null) continue;
                foreach (var name in key.GetValueNames()) names.Add(name);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }

        return names;
    }
}

/// <summary>
/// « Requête SELECT… »: a query on the old database, for a table the old application spreads over
/// several. Checked by <see cref="SourceQueryGuard"/> — one SELECT, nothing that writes — before
/// it is kept, and again before it is sent.
/// </summary>
internal sealed class SourceQueryDialog : LocalizedForm
{
    private readonly Label _caption = new() { AutoSize = true, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary };
    private readonly TextBox _query = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        AcceptsReturn = true,
        AcceptsTab = false,
        MaxLength = SourceQueryGuard.MaxLength,
        BorderStyle = BorderStyle.FixedSingle,
        Tag = UiTags.LatinValue
    };
    private readonly Label _status = new() { AutoSize = false, AutoEllipsis = true };
    private readonly Button _check = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _use = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    public SourceQueryDialog(ILocalizer localizer, ICultureManager cultureManager, string? query)
        : base(localizer, cultureManager)
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(680, 360);
        BackColor = UiTheme.SurfaceRaised;

        _query.Font = UiTheme.Monospace;
        _query.Text = query ?? "SELECT *\r\nFROM ";
        _query.RightToLeft = RightToLeft.No;

        _caption.Location = new Point(20, 4);
        _query.Bounds = new Rectangle(20, 22, 640, 250);
        _status.Bounds = new Rectangle(20, 278, 640, 36);
        _status.Font = UiTheme.Small;
        _check.Bounds = new Rectangle(20, 320, 140, UiTheme.ButtonHeight);
        _cancel.Bounds = new Rectangle(390, 320, 110, UiTheme.ButtonHeight);
        _use.Bounds = new Rectangle(510, 320, 150, UiTheme.ButtonHeight);
        Controls.AddRange([_caption, _query, _status, _check, _cancel, _use]);

        _check.Click += (_, _) => Check();
        _use.Click += (_, _) =>
        {
            if (!Check()) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        _cancel.DialogResult = DialogResult.Cancel;
        CancelButton = _cancel;

        AdminDialogFrame.Attach(this, Localizer, AdminDialogFrame.Lead(Localizer, nameof(SourceQueryDialog)),
                                () => Localizer["Migration.QueryTitle"]);
    }

    public string Query => _query.Text.Trim();

    protected override void ApplyTexts()
    {
        Text = Localizer["Migration.QueryTitle"];
        _caption.Text = Localizer["Migration.Query"];
        _check.Text = Localizer["Migration.CheckQuery"];
        _use.Text = Localizer["Migration.UseQuery"];
        _cancel.Text = Localizer["Action.Cancel"];
    }

    private bool Check()
    {
        var checkedQuery = SourceQueryGuard.Check(_query.Text);
        _status.Text = checkedQuery.IsSuccess ? Localizer["Migration.QueryAccepted"] : Localizer.Describe(checkedQuery.Errors);
        _status.ForeColor = checkedQuery.IsSuccess ? UiTheme.Normal : UiTheme.Critical;
        return checkedQuery.IsSuccess;
    }
}
