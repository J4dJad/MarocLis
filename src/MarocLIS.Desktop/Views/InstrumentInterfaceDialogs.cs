using System.Globalization;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Instruments;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Configuring one analyser link.
/// <para>
/// The transport decides which half of this form means anything, so the irrelevant half is disabled
/// as it changes. A serial analyser has no host and port; a file-drop interface has no baud rate.
/// Leaving both sets enabled invites somebody to fill in a TCP port on a serial link and then spend
/// an afternoon wondering why it will not connect.
/// </para>
/// <para>
/// Auto-acceptance is the one setting on this form that can put a wrong number in a patient file
/// without anybody looking at it, so it carries its own warning and the service refuses it while
/// the mapping table is empty.
/// </para>
/// </summary>
internal sealed class InstrumentInterfaceDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly Guid _instrumentId;

    private readonly ComboBox _protocol, _transport, _direction, _encoding, _dialect;
    private readonly TextBox _serialPort, _host, _tcpPort, _inbound, _outbound, _pattern,
                             _processed;
    private readonly ComboBox _baud, _dataBits, _parity, _stopBits, _handshake;
    private readonly NumericUpDown _ackTimeout, _maxRetries;
    private readonly CheckBox _listenAsServer, _enabled, _autoAccept, _raiseOnUnmapped,
                              _routeControls;
    private readonly Label _transportNotice;

    // --- message format: the right-hand column ---------------------------------------------------
    private readonly ComboBox _profile, _codeComponent, _specimenField, _specimenComponent,
                              _unmappedNonNumeric;
    private readonly TextBox _codeSuffix, _resultTypes;
    private readonly CheckBox _keepFlags;
    private readonly Button _testFrame;
    private readonly Label _formatNotice;
    private bool _applyingProfile;

    internal InterfaceRequest? Request { get; private set; }

    /// <param name="newInstrument">
    /// For a new link, the analyser it belongs to, chosen beforehand in
    /// <see cref="InterfaceInstrumentDialog"/>.
    /// </param>
    /// <param name="testFrame">
    /// Opens the "Tester une trame" window with the protocol and format as they stand on this form,
    /// saved or not. Null for a link not yet created: there is no mapping table to test against.
    /// </param>
    internal InstrumentInterfaceDialog(ILocalizer localizer, IUserDialogs dialogs,
                                       InterfaceRow? existing,
                                       (Guid Id, string Code)? newInstrument = null,
                                       Action<IWin32Window, InstrumentProtocol, MessageFormatSettings>? testFrame = null)
        : base(localizer, dialogs, existing is null ? "Interface.New" : "Interface.Edit",
               existing?.InstrumentCode ?? newInstrument?.Code, 1060, 620)
    {
        _id = existing?.Id;
        _instrumentId = existing?.InstrumentId ?? newInstrument?.Id ?? Guid.Empty;

        // The choices, the presets and the checks are InterfaceEditorRules', shared with the web.
        var form = InterfaceEditorRules.FormOf(existing);

        _protocol = Choice("Interface.Protocol", 20, 16, 200, form.Protocol, "InstrumentProtocol",
                           // Only the protocols this build has drivers for.
                           [.. InterfaceEditorRules.Protocols]);

        _transport = Choice("Interface.Transport", 232, 16, 200, form.Transport, "InstrumentTransportKind",
                            [.. InterfaceEditorRules.Transports]);

        _direction = Choice("Interface.Direction", 444, 16, 232, form.Direction, "InstrumentDirection");

        _transportNotice = Notice(20, 62, 656, 30, "Interface.SerialNotice");

        // --- serial ----------------------------------------------------------------------------

        _serialPort = TextField("Interface.SerialPort", 20, 96, 120, existing?.SerialPortName,
                           latin: true, mono: true);

        _baud = Picker("Interface.BaudRate", 152, 96, 110, [.. InterfaceEditorRules.BaudRates], form.BaudRate);

        _dataBits = Picker("Interface.DataBits", 274, 96, 90, [.. InterfaceEditorRules.DataBitChoices], form.DataBits);

        // Stored under the serial port's own names (« None », « XOnXOff »), shown in the
        // operator's language: the French screen showed the English words.
        _parity = Picker("Interface.Parity", 376, 96, 110,
                         SerialValues("Parity", InterfaceEditorRules.Parities), form.Parity);

        _stopBits = Picker("Interface.StopBits", 498, 96, 80,
                           SerialValues("StopBits", InterfaceEditorRules.StopBitChoices), form.StopBits);

        _handshake = Picker("Interface.Handshake", 590, 96, 86,
                            SerialValues("Handshake", InterfaceEditorRules.Handshakes), form.Handshake);
        _handshake.DropDownWidth = 170;

        // --- TCP -------------------------------------------------------------------------------

        _host = TextField("Interface.Host", 20, 144, 240, existing?.Host, latin: true, mono: true);
        _tcpPort = TextField("Interface.TcpPort", 272, 144, 100, existing?.TcpPort?.ToString(),
                        latin: true, right: true, mono: true);

        _listenAsServer = Check("Interface.ListenAsServer", 384, 164,
                                 existing?.ListenAsServer ?? false);

        Notice(20, 190, 656, 30, "Interface.ListenNotice");

        // --- file drop -------------------------------------------------------------------------

        _inbound = TextField("Interface.InboundDirectory", 20, 226, 320, existing?.InboundDirectory,
                        latin: true);
        _pattern = TextField("Interface.FilePattern", 352, 226, 120, existing?.FilePattern,
                        latin: true, mono: true);

        _processed = TextField("Interface.ProcessedDirectory", 484, 226, 192,
                          existing?.ProcessedDirectory, latin: true);

        _outbound = TextField("Interface.OutboundDirectory", 20, 274, 320, existing?.OutboundDirectory,
                         latin: true);

        Notice(352, 292, 324, 30, "Interface.ProcessedNotice");

        // As long as the interface's columns: a longer path or host failed the save of the whole
        // configuration with no word on which field.
        _serialPort.MaxLength = InterfaceEditorRules.SerialPortMaxLength;
        _host.MaxLength = InterfaceEditorRules.HostMaxLength;
        _inbound.MaxLength = _processed.MaxLength = _outbound.MaxLength = InterfaceEditorRules.DirectoryMaxLength;
        _pattern.MaxLength = InterfaceEditorRules.FilePatternMaxLength;
        _tcpPort.MaxLength = InterfaceEditorRules.TcpPortMaxLength;

        // --- protocol settings -----------------------------------------------------------------

        _encoding = Picker("Interface.Encoding", 20, 326, 160, [.. InterfaceEditorRules.Encodings], form.Encoding);

        _ackTimeout = Number("Interface.AckTimeout", 196, 326, 160,
                              existing?.AcknowledgementTimeoutSeconds ?? 15, max: InterfaceEditorRules.AcknowledgementTimeoutMax);

        _maxRetries = Number("Interface.MaxRetries", 372, 326, 120, existing?.MaxRetries ?? 3,
                              max: InterfaceEditorRules.MaxRetriesMax);

        // HL7's vendor reading (Mindray BS / BC / CL): greyed on any other protocol.
        _dialect = Choice("Interface.Hl7Dialect", 508, 326, 168, form.Hl7Dialect, "Hl7Dialect",
                          [.. InterfaceEditorRules.Dialects]);
        _dialect.DropDownWidth = 240;

        Notice(20, 374, 656, 30, "Interface.EncodingNotice");

        // --- behaviour -------------------------------------------------------------------------

        _raiseOnUnmapped = Check("Interface.RaiseOnUnmapped", 20, 410,
                                  existing?.RaiseIssueOnUnmappedCode ?? true);

        _routeControls = Check("Interface.RouteControls", 20, 434,
                                existing?.RouteControlResultsToQc ?? true);

        _autoAccept = Check("Interface.AutoAccept", 20, 458, existing?.AutoAcceptResults ?? false,
                             bold: true);

        Notice(20, 482, 656, 44, "Interface.AutoAcceptNotice", UiTheme.Abnormal);

        _enabled = Check("Interface.Enabled", 20, 534, existing?.IsEnabled ?? false, bold: true);

        Notice(180, 530, 496, 30, "Interface.EnabledNotice");

        // --- message format ----------------------------------------------------------------------

        var format = existing?.Format ?? MessageFormatSettings.Default;
        const int fx = 712, fw = 328;

        var section = new Label
        {
            Text = Localizer["Interface.FormatSection"],
            Font = UiTheme.BodyBold,
            ForeColor = UiTheme.Accent,
            AutoSize = false,
            Bounds = new Rectangle(fx, 16, fw, 20)
        };
        Controls.Add(section);
        Controls.Add(new Label
        {
            BackColor = UiTheme.Border,
            Bounds = new Rectangle(fx - 14, 16, 1, 540)
        });

        _profile = Picker("Interface.FormatProfile", fx, 40, fw,
                          [.. InterfaceEditorRules.FormatProfiles.Select(p => Localizer[p.Key])],
                          Localizer[InterfaceEditorRules.ProfileFor(form.Protocol, format)]);
        _profile.Font = UiTheme.Body;

        _codeComponent = Picker("Interface.FormatCodeComponent", fx, 88, 150, Components(),
                                Component(format.TestCodeComponent));
        _codeSuffix = TextField("Interface.FormatCodeSuffix", fx + 162, 88, fw - 162,
                                format.TestCodeSuffix, latin: true, mono: true);

        _specimenField = Picker("Interface.FormatSpecimenField", fx, 136, 150, [Auto], Auto);
        _specimenComponent = Picker("Interface.FormatSpecimenComponent", fx + 162, 136, fw - 162,
                                    Components(), Component(format.SpecimenIdComponent));

        _resultTypes = TextField("Interface.FormatResultTypes", fx, 184, fw,
                                 format.AcceptedResultTypes, latin: true, mono: true);
        // The lengths the service accepts: past them the save refused the whole configuration.
        _codeSuffix.MaxLength = InterfaceEditorRules.CodeSuffixMaxLength;
        _resultTypes.MaxLength = InterfaceEditorRules.ResultTypesMaxLength;

        Notice(fx, 230, fw, 46, "Interface.FormatResultTypesNotice");

        _unmappedNonNumeric = Choice("Interface.FormatUnmappedNonNumeric", fx, 280, fw,
                                     format.UnmappedNonNumeric, "UnmappedNonNumericHandling");

        _keepFlags = Check("Interface.KeepInstrumentFlags", fx, 332, format.KeepInstrumentFlags);

        _formatNotice = Notice(fx, 356, fw, 76, "Interface.FormatNotice");

        _testFrame = UiTheme.SecondaryButton(Localizer["Interface.TestFrame"]);
        _testFrame.Bounds = new Rectangle(fx, 440, fw, UiTheme.ButtonHeight);
        _testFrame.Visible = testFrame is not null;
        _testFrame.Click += (_, _) =>
            testFrame?.Invoke(this, Selected<InstrumentProtocol>(_protocol), CurrentFormat());
        Controls.Add(_testFrame);

        if (testFrame is null)
            Notice(fx, 440, fw, 34, "Interface.TestFrameAfterSave");

        RefreshSpecimenFields(format.SpecimenIdField);

        _profile.SelectedIndexChanged += (_, _) => ApplyProfile();

        foreach (var combo in new[] { _codeComponent, _specimenField, _specimenComponent })
            combo.SelectedIndexChanged += (_, _) => SyncProfile();

        _codeSuffix.TextChanged += (_, _) => SyncProfile();
        _resultTypes.TextChanged += (_, _) => SyncProfile();

        _dialect.SelectedIndexChanged += (_, _) =>
        {
            SyncProfile();
            RefreshFormatNotice();
        };

        _protocol.SelectedIndexChanged += (_, _) =>
        {
            RefreshSpecimenFields(_specimenField.SelectedItem?.ToString());
            RefreshDialect();
            SyncProfile();
            RefreshFormatNotice();
        };

        RefreshDialect();

        RefreshFormatNotice();

        _transport.SelectedIndexChanged += (_, _) => RefreshTransport();

        RefreshTransport();

        Buttons(574, 1060);
        UseAdminFrame();
    }

    // ---- message format -------------------------------------------------------------------------

    private const string Auto = "Auto";

    private static string[] Components() =>
        [.. InterfaceEditorRules.Components.Select(c => c?.ToString(CultureInfo.InvariantCulture) ?? Auto)];

    private static string Component(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? Auto;

    private static int? ComponentOf(ComboBox combo) =>
        int.TryParse(combo.SelectedItem?.ToString(), out var value) ? value : null;

    private void RefreshSpecimenFields(string? current)
    {
        var protocol = Selected<InstrumentProtocol>(_protocol);
        var kept = InterfaceEditorRules.OfferedField(protocol, current);

        _specimenField.Items.Clear();
        _specimenField.Items.Add(Auto);

        foreach (var (value, caption) in InterfaceEditorRules.SpecimenFields(protocol))
        {
            // ASTM fields are named by their place in the O record, which is what the manuals print.
            var index = _specimenField.Items.Add(caption);
            if (string.Equals(value, kept, StringComparison.OrdinalIgnoreCase)) _specimenField.SelectedIndex = index;
        }

        if (_specimenField.SelectedIndex < 0) _specimenField.SelectedIndex = 0;

        // POCT1-A is XML and the INTEGRA's lines have fixed fields: no component to choose.
        var delimited = InterfaceEditorRules.IsDelimited(protocol);
        _codeComponent.Enabled = _specimenComponent.Enabled = delimited;

        if (!delimited)
        {
            Select(_codeComponent, Auto);
            Select(_specimenComponent, Auto);
        }
    }

    private void ApplyProfile()
    {
        if (_applyingProfile) return;

        var key = InterfaceEditorRules.FormatProfiles.ElementAtOrDefault(_profile.SelectedIndex)?.Key;
        if (key is null || key == InterfaceEditorRules.CustomProfile) return;

        _applyingProfile = true;
        try
        {
            var form = ReadForm();
            InterfaceEditorRules.ApplyProfile(form, key);
            WriteForm(form);
        }
        finally
        {
            _applyingProfile = false;
        }
    }

    /// <summary>A field changed by hand moves the preset to whichever one now matches, or "Personnalisé".</summary>
    private void SyncProfile()
    {
        if (_applyingProfile) return;

        _applyingProfile = true;
        try
        {
            var key = InterfaceEditorRules.ProfileFor(Selected<InstrumentProtocol>(_protocol), CurrentFormat());
            var index = InterfaceEditorRules.FormatProfiles.ToList().FindIndex(p => p.Key == key);
            if (index >= 0) _profile.SelectedIndex = index;
        }
        finally
        {
            _applyingProfile = false;
        }
    }

    /// <summary>Selects an enum value in a combo filled by <c>Choice</c>.</summary>
    private static void SelectValue<TEnum>(ComboBox combo, TEnum value) where TEnum : struct, Enum
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is EnumChoice item && item.Value == Convert.ToInt32(value))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>On an INTEGRA or a Mindray link the notice says where its own settings are, instead of the ASTM advice.</summary>
    private void RefreshFormatNotice() =>
        _formatNotice.Text = Localizer[InterfaceEditorRules.FormatNoticeKey(Selected<InstrumentProtocol>(_protocol),
                                                                            Selected<Hl7Dialect>(_dialect))];

    /// <summary>The dialect is HL7's: back to Standard and greyed on any other protocol.</summary>
    private void RefreshDialect()
    {
        var offered = InterfaceEditorRules.IsDialectOffered(Selected<InstrumentProtocol>(_protocol));
        if (!offered) SelectValue(_dialect, Hl7Dialect.Standard);
        _dialect.Enabled = offered;
    }

    private static void Select(ComboBox combo, string value)
    {
        var index = combo.Items.IndexOf(value);
        for (var i = 0; index < 0 && i < combo.Items.Count; i++)
            if (combo.Items[i] is SerialChoice choice && choice.Value == value) index = i;
        combo.SelectedIndex = index >= 0 ? index : 0;
    }

    /// <summary>A serial setting under its port name, with the label the operator reads.</summary>
    private sealed record SerialChoice(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    private SerialChoice[] SerialValues(string setting, IEnumerable<string> values) =>
        [.. values.Select(v => new SerialChoice(v, Localizer[InterfaceEditorRules.SerialKey(setting, v)]))];

    private static string? SerialValue(ComboBox combo) => (combo.SelectedItem as SerialChoice)?.Value;

    private MessageFormatSettings CurrentFormat() => InterfaceEditorRules.Format(ReadForm());

    /// <summary>The form as the boxes stand, for the shared rules.</summary>
    private InterfaceForm ReadForm()
    {
        var field = _specimenField.SelectedItem?.ToString();

        return new InterfaceForm
        {
            Protocol = Selected<InstrumentProtocol>(_protocol),
            Transport = Selected<InstrumentTransportKind>(_transport),
            Direction = Selected<InstrumentDirection>(_direction),
            IsEnabled = _enabled.Checked,
            SerialPortName = _serialPort.Text,
            BaudRate = _baud.SelectedItem?.ToString() ?? string.Empty,
            DataBits = _dataBits.SelectedItem?.ToString() ?? string.Empty,
            Parity = SerialValue(_parity) ?? "None",
            StopBits = SerialValue(_stopBits) ?? "One",
            Handshake = SerialValue(_handshake) ?? "None",
            Host = _host.Text,
            TcpPort = _tcpPort.Text,
            ListenAsServer = _listenAsServer.Checked,
            InboundDirectory = _inbound.Text,
            FilePattern = _pattern.Text,
            ProcessedDirectory = _processed.Text,
            OutboundDirectory = _outbound.Text,
            Encoding = _encoding.SelectedItem?.ToString() ?? "ISO-8859-1",
            AcknowledgementTimeoutSeconds = (int)_ackTimeout.Value,
            MaxRetries = (int)_maxRetries.Value,
            RaiseIssueOnUnmappedCode = _raiseOnUnmapped.Checked,
            RouteControlResultsToQc = _routeControls.Checked,
            AutoAcceptResults = _autoAccept.Checked,
            TestCodeComponent = ComponentOf(_codeComponent),
            TestCodeSuffix = _codeSuffix.Text,
            SpecimenIdField = field is null or Auto ? null : field.Replace("O-", ""),
            SpecimenIdComponent = ComponentOf(_specimenComponent),
            AcceptedResultTypes = _resultTypes.Text,
            UnmappedNonNumeric = Selected<UnmappedNonNumericHandling>(_unmappedNonNumeric),
            KeepInstrumentFlags = _keepFlags.Checked,
            Hl7Dialect = Selected<Hl7Dialect>(_dialect)
        };
    }

    /// <summary>Puts a preset's values back in the boxes it changes: the protocol, the line and the format.</summary>
    private void WriteForm(InterfaceForm form)
    {
        SelectValue(_protocol, form.Protocol);
        SelectValue(_transport, form.Transport);
        SelectValue(_direction, form.Direction);

        Select(_baud, form.BaudRate);
        Select(_dataBits, form.DataBits);
        Select(_parity, form.Parity);
        Select(_stopBits, form.StopBits);
        Select(_handshake, form.Handshake);
        Select(_encoding, form.Encoding);

        // A Mindray preset also sets the line: the LIS listens, on the family's port.
        _tcpPort.Text = form.TcpPort;
        _listenAsServer.Checked = form.ListenAsServer;
        SelectValue(_dialect, form.Hl7Dialect);
        SelectValue(_unmappedNonNumeric, form.UnmappedNonNumeric);

        _ackTimeout.Value = Math.Min(_ackTimeout.Maximum, form.AcknowledgementTimeoutSeconds);
        _maxRetries.Value = Math.Min(_maxRetries.Maximum, form.MaxRetries);

        Select(_codeComponent, Component(form.TestCodeComponent));
        _codeSuffix.Text = form.TestCodeSuffix;
        Select(_specimenField, form.SpecimenIdField is null ? Auto
                               : InterfaceEditorRules.SpecimenFields(form.Protocol).First(f => f.Value == form.SpecimenIdField).Caption);
        Select(_specimenComponent, Component(form.SpecimenIdComponent));
        _resultTypes.Text = form.AcceptedResultTypes;

        RefreshDialect();
        RefreshTransport();
        RefreshFormatNotice();
    }

    /// <summary>A fixed list of values, where free text would only be a way to mistype one.</summary>
    private ComboBox Picker(string key, int x, int y, int width, string[] values, string current) =>
        Picker(key, x, y, width, values.Cast<object>().ToArray(), current);

    private ComboBox Picker(string key, int x, int y, int width, object[] values, string current)
    {
        Label(key, x, y);

        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(x, y + 18, width, UiTheme.InputHeight),
            Font = UiTheme.Monospace
        };

        foreach (var value in values)
        {
            var index = combo.Items.Add(value);
            if (string.Equals((value as SerialChoice)?.Value ?? value.ToString(), current, StringComparison.OrdinalIgnoreCase))
                combo.SelectedIndex = index;
        }

        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;

        Controls.Add(combo);
        return combo;
    }

    private void RefreshTransport()
    {
        var fields = InterfaceEditorRules.Fields(Selected<InstrumentTransportKind>(_transport), _listenAsServer.Checked);

        Enable(_serialPort, fields.Serial);
        foreach (var combo in new[] { _baud, _dataBits, _parity, _stopBits, _handshake })
            combo.Enabled = fields.Serial;

        Enable(_host, fields.Host);
        Enable(_tcpPort, fields.TcpPort);
        _listenAsServer.Enabled = fields.Listen;

        Enable(_inbound, fields.File);
        Enable(_pattern, fields.File);
        Enable(_processed, fields.File);
        Enable(_outbound, fields.File);

        _transportNotice.Text = Localizer[fields.NoticeKey];
    }

    private static void Enable(TextBox box, bool enabled)
    {
        box.Enabled = enabled;
        box.BackColor = enabled ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken;
    }

    protected override void Accept()
    {
        var form = ReadForm();

        // A link exists for an analyser, not on its own: chosen first, from the equipment screen.
        if (InterfaceEditorRules.Check(form, _instrumentId) is { } refusal)
        {
            Dialogs.Warn(Localizer[refusal.Key], Text);
            FieldOf(refusal.Field)?.Focus();
            return;
        }

        // Said before the save, not after the refusal. Auto-acceptance is the setting that can put
        // an analyser's number in a patient file with nobody looking at it.
        if (InterfaceEditorRules.NeedsAutoAcceptConfirmation(form)
            && !Dialogs.Confirm(Localizer["Interface.ConfirmAutoAccept"], Text))
            return;

        Request = InterfaceEditorRules.ToRequest(form, _id, _instrumentId);

        DialogResult = DialogResult.OK;
        Close();
    }

    private TextBox? FieldOf(string field) => field switch
    {
        "serialport" => _serialPort,
        "tcpport" => _tcpPort,
        "host" => _host,
        "inbound" => _inbound,
        _ => null
    };
}

/// <summary>
/// Editing the code translation table.
/// <para>
/// This table is the interface's whole safety story: the analyser sends "K" and the table decides
/// whether that becomes potassium or nothing at all. An unmapped code is a dropped result the
/// technician has to notice; a mis-mapped one puts a value in the wrong field and nobody notices.
/// </para>
/// <para>
/// The unit the analyser reports is shown beside the catalogue's, because the conversion factor is
/// the most dangerous number on this screen — a wrong one is a hundredfold error on a report, and
/// it is the only mapping fault that produces a plausible result rather than an error.
/// </para>
/// </summary>
internal sealed class InstrumentMappingDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly InterfaceRow _link;
    private readonly Func<string?, Task<Result<IReadOnlyList<MappableParameter>>>> _searchParameters;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _summary = new();
    private readonly int _retired;

    internal IReadOnlyList<MappingRequest> Mappings { get; private set; } = [];

    internal InstrumentMappingDialog(ILocalizer localizer, IUserDialogs dialogs,
                                     InterfaceRow link, IReadOnlyList<MappingRow> existing,
                                     Func<string?, Task<Result<IReadOnlyList<MappableParameter>>>> searchParameters)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _link = link;
        _searchParameters = searchParameters;

        Text = $"{localizer["Interface.EditMappings"]} — {link.InstrumentCode}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(960, 560);
        MinimumSize = new Size(820, 440);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var notice = new Label
        {
            Text = localizer["Interface.MappingNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(16, 12, 928, 44)
        };

        BuildColumns();

        _grid.Dock = DockStyle.None;
        _grid.Bounds = new Rectangle(16, 64, 928, 390);
        // New codes come in through "Add parameters", which ties each to a catalogue parameter.
        // A blank line typed at the bottom of the grid had no parameter and was dropped on save.
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = true;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;

        // The shared grid style is read-only with whole-row selection, which left every code on
        // this screen impossible to type or correct. Cells are edited here, one at a time.
        _grid.ReadOnly = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.MultiSelect = true;

        var language = localizer.CurrentLanguage;

        // A removed code is kept, switched off, so old messages can still be read through it; it
        // is not shown again. Re-adding the parameter under the same code brings it back.
        // The lines and the retired count are InterfaceEditorRules', shared with the web.
        _retired = InterfaceEditorRules.Retired(existing);

        foreach (var line in InterfaceEditorRules.Lines(existing))
        {
            var index = _grid.Rows.Add(
                line.Code,
                InterfaceEditorRules.ParameterLabel(line.Original, language),
                line.InstrumentUnit,
                line.Original.CatalogueUnit ?? string.Empty,
                line.Factor,
                line.Offset,
                line.CodedValueMap);

            _grid.Rows[index].Tag = line.Original;
        }

        _summary.Font = UiTheme.Small;
        _summary.ForeColor = UiTheme.TextSecondary;
        _summary.AutoSize = false;
        _summary.Bounds = new Rectangle(16, 462, 928, 20);

        var add = UiTheme.SecondaryButton(localizer["Interface.AddParameters"]);
        add.Bounds = new Rectangle(16, 492, 240, UiTheme.ButtonHeight);
        add.Click += async (_, _) => await AddParametersAsync();

        var remove = UiTheme.SecondaryButton(localizer["Interface.RemoveMapping"]);
        remove.Bounds = new Rectangle(268, 492, 200, UiTheme.ButtonHeight);
        remove.Click += (_, _) => RemoveSelected();

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(536, 492, 200, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(744, 492, 200, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        add.Anchor = remove.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        ok.Anchor = cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _summary.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _grid.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
        notice.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

        Controls.AddRange([notice, _grid, _summary, add, remove, ok, cancel]);

        CancelButton = cancel;
        AdminDialogFrame.Attach(this, localizer, AdminDialogFrame.Lead(localizer, nameof(InstrumentMappingDialog)));

        _grid.CellFormatting += OnFormatting;
        _grid.UserDeletedRow += (_, _) => RefreshSummary();

        RefreshSummary();
    }

    /// <summary>Takes the lines holding a selected cell out of the table; saving switches them off.</summary>
    private void RemoveSelected()
    {
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();

        var rows = _grid.SelectedCells.Cast<DataGridViewCell>()
                        .Select(c => c.OwningRow)
                        .Where(r => r is not null && !r.IsNewRow)
                        .Distinct()
                        .ToList();

        if (rows.Count == 0 && _grid.CurrentRow is { IsNewRow: false } current) rows.Add(current);

        foreach (var row in rows) _grid.Rows.Remove(row!);

        RefreshSummary();
    }

    /// <summary>
    /// Links catalogue parameters to this analyser: each one picked comes in as a new line whose
    /// analyser code starts as the laboratory's own code, to be overwritten with the code the
    /// analyser actually transmits.
    /// </summary>
    private async Task AddParametersAsync()
    {
        var mapped = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Tag is MappingRow)
            .Select(r => ((MappingRow)r.Tag!).TestParameterId)
            .ToHashSet();

        using var picker = new MappableParameterPicker(_localizer, _dialogs, _searchParameters, mapped);

        if (picker.ShowDialog(this) != DialogResult.OK || picker.Picked.Count == 0) return;

        var language = _localizer.CurrentLanguage;
        DataGridViewRow? first = null;

        foreach (var parameter in picker.Picked)
        {
            var line = InterfaceEditorRules.NewLine(parameter, _link.Id);
            var index = _grid.Rows.Add(
                line.Code,
                InterfaceEditorRules.ParameterLabel(line.Original, language),
                string.Empty,
                parameter.UnitSymbol ?? string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

            var row = _grid.Rows[index];
            row.Tag = line.Original;

            // The code to check: it is the laboratory's, not yet the analyser's.
            row.Cells["code"].Style.BackColor = UiTheme.AbnormalBackground;
            first ??= row;
        }

        RefreshSummary();

        if (first is null) return;

        _grid.FirstDisplayedScrollingRowIndex = first.Index;
        _grid.CurrentCell = first.Cells["code"];
        _grid.BeginEdit(selectAll: true);
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "code", HeaderText = _localizer["Interface.InstrumentCode"], FillWeight = 11,
            // Each editable column as long as its field: a longer entry failed the whole table's save.
            MaxInputLength = InterfaceEditorRules.MappingCodeMaxLength,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.MonospaceBold }
        });

        // The parameter is read-only text here: picking one needs a searchable list of several
        // hundred, which is a dialog of its own rather than a drop-down in a grid cell.
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "parameter", HeaderText = _localizer["Catalog.Parameter"], FillWeight = 30,
            ReadOnly = true
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "instrumentUnit", HeaderText = _localizer["Interface.InstrumentUnit"],
            FillWeight = 11, MaxInputLength = InterfaceEditorRules.MappingUnitMaxLength,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Monospace }
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "catalogueUnit", HeaderText = _localizer["Interface.CatalogueUnit"],
            FillWeight = 11, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Monospace }
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "factor", HeaderText = _localizer["Interface.ConversionFactor"], FillWeight = 10,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.Monospace,
                Alignment = DataGridViewContentAlignment.MiddleRight
            }
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "offset", HeaderText = _localizer["Interface.ConversionOffset"], FillWeight = 9,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.Monospace,
                Alignment = DataGridViewContentAlignment.MiddleRight
            }
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "codeMap", HeaderText = _localizer["Interface.CodedValueMap"], FillWeight = 12,
            MaxInputLength = InterfaceEditorRules.CodedValueMapMaxLength,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Monospace }
        });
    }

    private void OnFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;

        var row = _grid.Rows[e.RowIndex];
        if (row.IsNewRow || LineOf(row) is not { } line) return;

        if (InterfaceEditorRules.IsUnitMismatch(line))
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
    }

    private void RefreshSummary()
    {
        var total = _grid.Rows.Cast<DataGridViewRow>().Count(r => !r.IsNewRow);

        _summary.Text = InterfaceEditorRules.Summary(total, _retired, _localizer.Format);
    }

    /// <summary>A line of the grid as the shared rules read it.</summary>
    private static MappingLine? LineOf(DataGridViewRow row) => row.Tag is MappingRow original
        ? new MappingLine
        {
            Original = original,
            Code = row.Cells["code"].Value?.ToString() ?? string.Empty,
            InstrumentUnit = row.Cells["instrumentUnit"].Value?.ToString() ?? string.Empty,
            Factor = row.Cells["factor"].Value?.ToString() ?? string.Empty,
            Offset = row.Cells["offset"].Value?.ToString() ?? string.Empty,
            CodedValueMap = row.Cells["codeMap"].Value?.ToString() ?? string.Empty
        }
        : null;

    private void Accept()
    {
        var rows = _grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && r.Tag is MappingRow).ToList();
        var (mappings, refusal, _) = InterfaceEditorRules.Check([.. rows.Select(r => LineOf(r)!)]);

        if (refusal is not null)
        {
            _dialogs.Warn(_localizer[refusal.Key], Text);
            return;
        }

        Mappings = mappings;

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Picks the catalogue parameters to link to an analyser.
/// <para>
/// Searched rather than listed: the catalogue holds several hundred parameters. Typing an
/// analysis code brings all of its parameters at once, which is how an analyser is set up in
/// practice: the blood count of a haematology analyser, the ionogram of a chemistry one.
/// Parameters this analyser already maps are shown but cannot be ticked, since one parameter
/// takes one code.
/// </para>
/// </summary>
internal sealed class MappableParameterPicker : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly Func<string?, Task<Result<IReadOnlyList<MappableParameter>>>> _search;
    private readonly HashSet<Guid> _alreadyMapped;

    private readonly TextBox _query = UiTheme.Input(300);
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _ok = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _all = UiTheme.SecondaryButton(string.Empty);
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };

    internal IReadOnlyList<MappableParameter> Picked { get; private set; } = [];

    internal MappableParameterPicker(ILocalizer localizer, IUserDialogs dialogs,
                                     Func<string?, Task<Result<IReadOnlyList<MappableParameter>>>> search,
                                     HashSet<Guid> alreadyMapped)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _search = search;
        _alreadyMapped = alreadyMapped;

        Text = localizer["Interface.AddParameters"];
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(620, 400);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var prompt = new Label
        {
            Text = localizer["Interface.AddParametersPrompt"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(16, 12, 728, 20),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
        };

        _query.Bounds = new Rectangle(16, 38, 568, UiTheme.InputHeight);
        _query.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _query.PlaceholderText = localizer["Interface.AddParametersSearch"];

        // The usual case is the whole analysis: every parameter of the blood count at once.
        _all.Text = localizer["Interface.TickAll"];
        _all.Bounds = new Rectangle(594, 37, 150, UiTheme.ButtonHeight);
        _all.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _all.Click += (_, _) => TickAll();

        _grid.Dock = DockStyle.None;
        _grid.Bounds = new Rectangle(16, 78, 728, 378);
        _grid.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
        _grid.MultiSelect = true;
        BuildColumns();

        _ok.Text = localizer["Interface.AddParametersConfirm"];
        _ok.Bounds = new Rectangle(16, 468, 356, UiTheme.ButtonHeight);
        _ok.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(388, 468, 356, UiTheme.ButtonHeight);
        cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([prompt, _query, _all, _grid, _ok, cancel]);
        CancelButton = cancel;
        AcceptButton = _ok;
        AdminDialogFrame.Attach(this, localizer, AdminDialogFrame.Lead(localizer, nameof(MappableParameterPicker)));

        _query.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await LoadAsync(); };
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 0) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, _) => RefreshOk();
        _grid.CellFormatting += OnFormatting;

        Shown += async (_, _) => { _query.Focus(); await LoadAsync(); };
        RefreshOk();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();
        base.Dispose(disposing);
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();
        _grid.ReadOnly = false;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "pick", HeaderText = string.Empty, FillWeight = 5 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "analysis", HeaderText = _localizer["Catalog.Analysis"], FillWeight = 12, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.MonospaceBold }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "code", HeaderText = _localizer["Field.Code"], FillWeight = 12, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Monospace }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name", HeaderText = _localizer["Catalog.Parameter"], FillWeight = 40, ReadOnly = true
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "unit", HeaderText = _localizer["Interface.CatalogueUnit"], FillWeight = 12, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Monospace }
        });
    }

    private async Task LoadAsync()
    {
        if (IsDisposed) return;

        var result = await _search(_query.Text);
        if (IsDisposed) return;

        if (result.IsFailure) { _dialogs.ShowFailure(result, Text); return; }

        var ticked = Ticked().Select(p => p.TestParameterId).ToHashSet();
        var language = _localizer.CurrentLanguage;

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var parameter in result.Value)
        {
            var mapped = _alreadyMapped.Contains(parameter.TestParameterId);
            var index = _grid.Rows.Add(
                !mapped && ticked.Contains(parameter.TestParameterId),
                parameter.AnalysisCode,
                parameter.ParameterCode,
                mapped
                    ? $"{parameter.ParameterName.For(language)}  ({_localizer["Interface.AlreadyMapped"]})"
                    : parameter.ParameterName.For(language),
                parameter.UnitSymbol ?? string.Empty);

            var row = _grid.Rows[index];
            row.Tag = parameter;
            row.Cells["pick"].ReadOnly = mapped;
        }

        _grid.ResumeLayout();
        RefreshOk();
    }

    private void OnFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not MappableParameter parameter) return;
        if (_alreadyMapped.Contains(parameter.TestParameterId)) e.CellStyle!.ForeColor = UiTheme.TextMuted;
    }

    private void TickAll()
    {
        _grid.EndEdit();
        foreach (DataGridViewRow row in _grid.Rows)
            if (!row.Cells["pick"].ReadOnly) row.Cells["pick"].Value = true;
        RefreshOk();
    }

    private IEnumerable<MappableParameter> Ticked() =>
        _grid.Rows.Cast<DataGridViewRow>()
             .Where(r => r.Cells["pick"].Value is true && r.Tag is MappableParameter)
             .Select(r => (MappableParameter)r.Tag!);

    private void RefreshOk()
    {
        var count = Ticked().Count();
        _ok.Enabled = count > 0;
        _ok.Text = count > 0
            ? $"{_localizer["Interface.AddParametersConfirm"]} ({count})"
            : _localizer["Interface.AddParametersConfirm"];
    }

    private void Accept()
    {
        Picked = [.. Ticked()];
        if (Picked.Count == 0) return;

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Shows one frame exactly as it went over the wire.
/// <para>
/// Verbatim, in a monospaced font, with the control characters made visible rather than stripped.
/// When a clinician disputes a result this is the evidence of what the analyser transmitted, and a
/// reflowed, re-encoded rendering of it would be evidence of nothing.
/// </para>
/// </summary>
internal sealed class InstrumentRawMessageDialog : Form
{
    internal InstrumentRawMessageDialog(ILocalizer localizer, MessageLogRow message, string raw,
                                        string issues)
    {
        Text = $"{localizer["Interface.ViewRaw"]} — "
               + message.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(600, 360);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var header = new Label
        {
            Text = InstrumentInterfaceScreenRules.RawHeader(message, localizer.Format),
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.Accent,
            Dock = DockStyle.Top,
            Height = 22
        };

        var issueLabel = new Label
        {
            Text = issues,
            Font = UiTheme.Small,
            ForeColor = issues.Length == 0 ? UiTheme.TextSecondary : UiTheme.Abnormal,
            Dock = DockStyle.Top,
            Height = issues.Length == 0 ? 0 : 44
        };

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = UiTheme.Monospace,
            BackColor = UiTheme.SurfaceSunken,
            // A Latin wire format. Mirrored in Arabic it would be unreadable and, worse,
            // misleading — the operator needs to see the byte order the analyser actually sent.
            Tag = UiTags.LatinValue,
            Text = InstrumentInterfaceScreenRules.MakeControlCharactersVisible(raw)
        };

        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Dock = DockStyle.Bottom;
        close.Height = UiTheme.ButtonHeight + 8;
        close.DialogResult = DialogResult.OK;

        Controls.Add(box);
        Controls.Add(close);
        Controls.Add(issueLabel);
        Controls.Add(header);

        CancelButton = close;
        AcceptButton = close;
        AdminDialogFrame.Attach(this, localizer, AdminDialogFrame.Lead(localizer, nameof(InstrumentRawMessageDialog)));
    }
}

/// <summary>
/// Chooses the analyser a new link is for, before the link itself is described.
/// <para>
/// A link belongs to an analyser and cannot exist on its own, so the question comes first: asking
/// it after the operator has filled in a whole frame configuration, and then refusing the save,
/// wastes their work. Only analysers without a link are offered.
/// </para>
/// </summary>
internal sealed class InterfaceInstrumentDialog : MasterDataEditor
{
    private readonly ComboBox _instrument;
    private readonly IReadOnlyList<InstrumentMasterRow> _instruments;

    internal (Guid Id, string Code)? Choice { get; private set; }

    internal InterfaceInstrumentDialog(ILocalizer localizer, IUserDialogs dialogs,
                                       IReadOnlyList<InstrumentMasterRow> instruments)
        : base(localizer, dialogs, "Interface.New", null, 460, 190)
    {
        _instruments = instruments;

        _instrument = Lookup("Instrument.Title", 20, 16, 420,
                             instruments.Select(i => (i.InstrumentId,
                                 $"{i.Code} — {i.Name.For(localizer.CurrentLanguage)}")),
                             null, allowNone: false);

        Notice(20, 66, 420, 44, "Interface.PickInstrumentNotice");

        Buttons(130, 460);
        UseAdminFrame();
    }

    protected override void Accept()
    {
        if (SelectedId(_instrument) is not { } id) return;

        Choice = (id, _instruments.First(i => i.InstrumentId == id).Code);
        DialogResult = DialogResult.OK;
        Close();
    }
}
