using MarocLIS.Application.Administration;
using MarocLIS.Application.Instruments;
using MarocLIS.Desktop.Infrastructure;
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

    private readonly ComboBox _protocol, _transport, _direction, _encoding;
    private readonly TextBox _serialPort, _host, _tcpPort, _inbound, _outbound, _pattern,
                             _processed;
    private readonly ComboBox _baud, _dataBits, _parity, _stopBits, _handshake;
    private readonly NumericUpDown _ackTimeout, _maxRetries;
    private readonly CheckBox _listenAsServer, _enabled, _autoAccept, _raiseOnUnmapped,
                              _routeControls;
    private readonly Label _transportNotice;

    internal InterfaceRequest? Request { get; private set; }

    /// <param name="newInstrument">
    /// For a new link, the analyser it belongs to, chosen beforehand in
    /// <see cref="InterfaceInstrumentDialog"/>.
    /// </param>
    internal InstrumentInterfaceDialog(ILocalizer localizer, IUserDialogs dialogs,
                                       InterfaceRow? existing,
                                       (Guid Id, string Code)? newInstrument = null)
        : base(localizer, dialogs, existing is null ? "Interface.New" : "Interface.Edit",
               existing?.InstrumentCode ?? newInstrument?.Code, 700, 620)
    {
        _id = existing?.Id;
        _instrumentId = existing?.InstrumentId ?? newInstrument?.Id ?? Guid.Empty;

        _protocol = Choice("Interface.Protocol", 20, 16, 200,
                           existing?.Protocol ?? InstrumentProtocol.AstmE1394,
                           "InstrumentProtocol",
                           // Only the two protocols this build has drivers for. Offering POCT1-A
                           // with no driver behind it would be a setting that silently does nothing.
                           [InstrumentProtocol.AstmE1394, InstrumentProtocol.Hl7V2]);

        _transport = Choice("Interface.Transport", 232, 16, 200,
                            existing?.Transport ?? InstrumentTransportKind.Serial,
                            "InstrumentTransportKind",
                            [InstrumentTransportKind.Serial, InstrumentTransportKind.Tcp,
                             InstrumentTransportKind.FileDrop]);

        _direction = Choice("Interface.Direction", 444, 16, 232,
                            existing?.Direction ?? InstrumentDirection.Unidirectional,
                            "InstrumentDirection");

        _transportNotice = Notice(20, 62, 656, 30, "Interface.SerialNotice");

        // --- serial ----------------------------------------------------------------------------

        _serialPort = TextField("Interface.SerialPort", 20, 96, 120, existing?.SerialPortName,
                           latin: true, mono: true);

        _baud = Picker("Interface.BaudRate", 152, 96, 110,
                       ["1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200"],
                       (existing?.BaudRate ?? 9600).ToString());

        _dataBits = Picker("Interface.DataBits", 274, 96, 90, ["7", "8"],
                           (existing?.DataBits ?? 8).ToString());

        _parity = Picker("Interface.Parity", 376, 96, 110,
                         ["None", "Even", "Odd", "Mark", "Space"], existing?.Parity ?? "None");

        _stopBits = Picker("Interface.StopBits", 498, 96, 80, ["One", "OnePointFive", "Two"],
                           existing?.StopBits ?? "One");

        _handshake = Picker("Interface.Handshake", 590, 96, 86,
                            ["None", "XOnXOff", "RequestToSend", "RequestToSendXOnXOff"],
                            existing?.Handshake ?? "None");

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

        // --- protocol settings -----------------------------------------------------------------

        _encoding = Picker("Interface.Encoding", 20, 326, 160,
                           ["ISO-8859-1", "UTF-8", "ASCII", "Windows-1252", "Windows-1256"],
                           existing?.Encoding ?? "ISO-8859-1");

        _ackTimeout = Number("Interface.AckTimeout", 196, 326, 160,
                              existing?.AcknowledgementTimeoutSeconds ?? 15, max: 300);

        _maxRetries = Number("Interface.MaxRetries", 372, 326, 120, existing?.MaxRetries ?? 3,
                              max: 10);

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

        _transport.SelectedIndexChanged += (_, _) => RefreshTransport();

        RefreshTransport();

        Buttons(574, 700);
    }

    /// <summary>A fixed list of values, where free text would only be a way to mistype one.</summary>
    private ComboBox Picker(string key, int x, int y, int width, string[] values, string current)
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
            if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
                combo.SelectedIndex = index;
        }

        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;

        Controls.Add(combo);
        return combo;
    }

    private void RefreshTransport()
    {
        var transport = Selected<InstrumentTransportKind>(_transport);

        var serial = transport == InstrumentTransportKind.Serial;
        var tcp = transport == InstrumentTransportKind.Tcp;
        var file = transport == InstrumentTransportKind.FileDrop;

        Enable(_serialPort, serial);
        foreach (var combo in new[] { _baud, _dataBits, _parity, _stopBits, _handshake })
            combo.Enabled = serial;

        Enable(_host, tcp && !_listenAsServer.Checked);
        Enable(_tcpPort, tcp);
        _listenAsServer.Enabled = tcp;

        Enable(_inbound, file);
        Enable(_pattern, file);
        Enable(_processed, file);
        Enable(_outbound, file);

        _transportNotice.Text = Localizer[transport switch
        {
            InstrumentTransportKind.Serial => "Interface.SerialNotice",
            InstrumentTransportKind.Tcp => "Interface.TcpNotice",
            _ => "Interface.FileDropNotice"
        }];
    }

    private static void Enable(TextBox box, bool enabled)
    {
        box.Enabled = enabled;
        box.BackColor = enabled ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken;
    }

    protected override void Accept()
    {
        if (_instrumentId == Guid.Empty)
        {
            // A link exists for an analyser, not on its own. Creating one needs the instrument
            // chosen first, which is done from the equipment screen.
            Dialogs.Warn(Localizer["Interface_InstrumentRequired"], Text);
            return;
        }

        var transport = Selected<InstrumentTransportKind>(_transport);

        if (transport == InstrumentTransportKind.Serial
            && string.IsNullOrWhiteSpace(_serialPort.Text))
        {
            Dialogs.Warn(Localizer["Interface_SerialPortRequired"], Text);
            _serialPort.Focus();
            return;
        }

        int? tcpPort = int.TryParse(_tcpPort.Text.Trim(), out var parsedPort) ? parsedPort : null;

        if (transport == InstrumentTransportKind.Tcp)
        {
            if (tcpPort is null or < 1 or > 65535)
            {
                Dialogs.Warn(Localizer["Interface_PortInvalid"], Text);
                _tcpPort.Focus();
                return;
            }

            if (!_listenAsServer.Checked && string.IsNullOrWhiteSpace(_host.Text))
            {
                Dialogs.Warn(Localizer["Interface_HostRequired"], Text);
                _host.Focus();
                return;
            }
        }

        if (transport == InstrumentTransportKind.FileDrop
            && string.IsNullOrWhiteSpace(_inbound.Text))
        {
            Dialogs.Warn(Localizer["Interface_InboundDirectoryRequired"], Text);
            _inbound.Focus();
            return;
        }

        // Said before the save, not after the refusal. Auto-acceptance is the setting that can put
        // an analyser's number in a patient file with nobody looking at it.
        if (_autoAccept.Checked
            && !Dialogs.Confirm(Localizer["Interface.ConfirmAutoAccept"], Text))
            return;

        Request = new InterfaceRequest
        {
            Id = _id,
            InstrumentId = _instrumentId,
            Protocol = Selected<InstrumentProtocol>(_protocol),
            Transport = transport,
            Direction = Selected<InstrumentDirection>(_direction),
            IsEnabled = _enabled.Checked,

            SerialPortName = Blank(_serialPort),
            BaudRate = int.TryParse(_baud.SelectedItem?.ToString(), out var baud) ? baud : 9600,
            DataBits = int.TryParse(_dataBits.SelectedItem?.ToString(), out var bits) ? bits : 8,
            Parity = _parity.SelectedItem?.ToString() ?? "None",
            StopBits = _stopBits.SelectedItem?.ToString() ?? "One",
            Handshake = _handshake.SelectedItem?.ToString() ?? "None",

            Host = Blank(_host),
            TcpPort = tcpPort,
            ListenAsServer = _listenAsServer.Checked,

            InboundDirectory = Blank(_inbound),
            OutboundDirectory = Blank(_outbound),
            FilePattern = Blank(_pattern),
            ProcessedDirectory = Blank(_processed),

            Encoding = _encoding.SelectedItem?.ToString() ?? "ISO-8859-1",
            AcknowledgementTimeoutSeconds = (int)_ackTimeout.Value,
            MaxRetries = (int)_maxRetries.Value,

            AutoAcceptResults = _autoAccept.Checked,
            RaiseIssueOnUnmappedCode = _raiseOnUnmapped.Checked,
            RouteControlResultsToQc = _routeControls.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
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

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _summary = new();

    internal IReadOnlyList<MappingRequest> Mappings { get; private set; } = [];

    internal InstrumentMappingDialog(ILocalizer localizer, IUserDialogs dialogs,
                                     InterfaceRow link, IReadOnlyList<MappingRow> existing)
    {
        _localizer = localizer;
        _dialogs = dialogs;

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
        _grid.AllowUserToAddRows = true;
        _grid.AllowUserToDeleteRows = true;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;

        var language = localizer.CurrentLanguage;

        foreach (var mapping in existing)
        {
            var index = _grid.Rows.Add(
                mapping.InstrumentTestCode,
                $"{mapping.AnalysisCode}/{mapping.ParameterCode}  {mapping.ParameterName.For(language)}",
                mapping.InstrumentUnit ?? string.Empty,
                mapping.CatalogueUnit ?? string.Empty,
                mapping.ConversionFactor?.ToString("0.######") ?? string.Empty,
                mapping.ConversionOffset?.ToString("0.######") ?? string.Empty,
                mapping.CodedValueMap ?? string.Empty,
                mapping.IsEnabled);

            _grid.Rows[index].Tag = mapping;
        }

        _summary.Font = UiTheme.Small;
        _summary.ForeColor = UiTheme.TextSecondary;
        _summary.AutoSize = false;
        _summary.Bounds = new Rectangle(16, 462, 928, 20);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(16, 492, 456, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(488, 492, 456, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([notice, _grid, _summary, ok, cancel]);

        CancelButton = cancel;

        _grid.CellFormatting += OnFormatting;

        RefreshSummary();
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "code", HeaderText = _localizer["Interface.InstrumentCode"], FillWeight = 11,
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
            FillWeight = 11,
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
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Monospace }
        });

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "enabled", HeaderText = _localizer["Field.Active"], FillWeight = 6
        });
    }

    private void OnFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;

        var row = _grid.Rows[e.RowIndex];
        if (row.IsNewRow) return;

        var instrumentUnit = row.Cells["instrumentUnit"].Value?.ToString();
        var catalogueUnit = row.Cells["catalogueUnit"].Value?.ToString();
        var factor = row.Cells["factor"].Value?.ToString();

        var mismatch = !string.IsNullOrWhiteSpace(instrumentUnit)
                       && !string.IsNullOrWhiteSpace(catalogueUnit)
                       && !string.Equals(instrumentUnit, catalogueUnit,
                                          StringComparison.OrdinalIgnoreCase)
                       && string.IsNullOrWhiteSpace(factor);

        if (mismatch)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.Cells["enabled"].Value is false)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
    }

    private void RefreshSummary()
    {
        var total = _grid.Rows.Cast<DataGridViewRow>().Count(r => !r.IsNewRow);
        var enabled = _grid.Rows.Cast<DataGridViewRow>()
                                .Count(r => !r.IsNewRow && r.Cells["enabled"].Value is true);

        _summary.Text = _localizer.Format("Interface.MappingSummary", total, enabled);
    }

    private void Accept()
    {
        var mappings = new List<MappingRequest>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            if (row.Tag is not MappingRow original) continue;

            var code = row.Cells["code"].Value?.ToString();

            if (string.IsNullOrWhiteSpace(code))
            {
                _dialogs.Warn(_localizer["Interface_MappingCodeRequired"], Text);
                return;
            }

            if (!TryParse(row.Cells["factor"].Value?.ToString(), out var factor)
                || !TryParse(row.Cells["offset"].Value?.ToString(), out var offset))
            {
                _dialogs.Warn(_localizer["Interface_ConversionNotNumeric"], Text);
                return;
            }

            if (factor is 0m)
            {
                _dialogs.Warn(_localizer["Interface_ConversionFactorZero"], Text);
                return;
            }

            mappings.Add(new MappingRequest(
                code.Trim(),
                original.TestParameterId,
                row.Cells["instrumentUnit"].Value?.ToString(),
                factor,
                offset,
                row.Cells["codeMap"].Value?.ToString(),
                row.Cells["enabled"].Value is true));
        }

        Mappings = mappings;

        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool TryParse(string? text, out decimal? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text)) return true;

        if (!decimal.TryParse(text.Replace(',', '.'),
                               System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;

        value = parsed;
        return true;
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
            Text = $"{localizer[$"MessageDirection.{message.Direction}"]}"
                   + $"   ·   {localizer[$"InstrumentMessageProcessingStatus.{message.Status}"]}"
                   + (message.ResultCount is { } total
                       ? $"   ·   {message.ImportedCount ?? 0}/{total} {localizer["Result.Plural"].ToLowerInvariant()}"
                       : string.Empty)
                   + (message.SpecimenIds is { Length: > 0 } specimens ? $"   ·   {specimens}" : string.Empty),
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
            Text = MakeControlCharactersVisible(raw)
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
    }

    /// <summary>
    /// Replaces the ASTM and MLLP control characters with their names.
    /// <para>
    /// Shown rather than stripped: half of all interface faults are a framing problem, and an
    /// &lt;ENQ&gt; that never got its &lt;ACK&gt; is the diagnosis. Stripped, the frame looks fine
    /// and the fault is invisible.
    /// </para>
    /// </summary>
    private static string MakeControlCharactersVisible(string raw)
    {
        var builder = new System.Text.StringBuilder(raw.Length + 32);

        foreach (var character in raw)
        {
            builder.Append(character switch
            {
                '\x05' => "<ENQ>",
                '\x06' => "<ACK>",
                '\x15' => "<NAK>",
                '\x04' => "<EOT>" + Environment.NewLine,
                '\x02' => "<STX>",
                '\x03' => "<ETX>",
                '\x17' => "<ETB>",
                '\x0B' => "<VT>",
                '\x1C' => "<FS>",
                '\r' => "<CR>" + Environment.NewLine,
                '\n' => string.Empty,
                _ => character.ToString()
            });
        }

        return builder.ToString();
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
    }

    protected override void Accept()
    {
        if (SelectedId(_instrument) is not { } id) return;

        Choice = (id, _instruments.First(i => i.InstrumentId == id).Code);
        DialogResult = DialogResult.OK;
        Close();
    }
}
