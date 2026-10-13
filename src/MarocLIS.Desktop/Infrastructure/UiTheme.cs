using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The visual language of the application.
/// <para>
/// Restrained on purpose. A laboratory operator looks at this screen for eight hours and needs to
/// spot one abnormal value in a list of forty; a colourful interface makes that harder, not easier.
/// Colour is reserved for clinical meaning — abnormal, critical, overdue — and everything else is
/// greyscale so that when colour appears it means something.
/// </para>
/// <para>
/// Every clinical state also carries a <b>glyph</b>. Roughly one man in twelve has a colour-vision
/// deficiency, reports get printed in monochrome, and a cue that exists only as a hue is a cue that
/// some users never receive.
/// </para>
/// </summary>
public static class UiTheme
{
    // ---- Neutrals -------------------------------------------------------------------------------

    public static readonly Color Surface = Color.FromArgb(0xFA, 0xFA, 0xFB);
    public static readonly Color SurfaceRaised = Color.White;
    public static readonly Color SurfaceSunken = Color.FromArgb(0xF0, 0xF1, 0xF3);
    public static readonly Color Border = Color.FromArgb(0xD8, 0xDA, 0xDE);
    public static readonly Color BorderStrong = Color.FromArgb(0xB4, 0xB8, 0xBF);

    public static readonly Color TextPrimary = Color.FromArgb(0x1C, 0x1E, 0x21);
    public static readonly Color TextSecondary = Color.FromArgb(0x5C, 0x61, 0x6A);
    public static readonly Color TextMuted = Color.FromArgb(0x8A, 0x8F, 0x98);
    public static readonly Color TextOnAccent = Color.White;

    // ---- Accent ---------------------------------------------------------------------------------

    /// <summary>Single accent, used for focus, selection and the primary action only.</summary>
    public static readonly Color Accent = Color.FromArgb(0x1B, 0x5E, 0x8C);
    public static readonly Color AccentHover = Color.FromArgb(0x16, 0x4E, 0x75);
    public static readonly Color AccentSubtle = Color.FromArgb(0xE6, 0xEE, 0xF4);

    // ---- Clinical semantics ---------------------------------------------------------------------

    /// <summary>Out of the reference interval but not critical.</summary>
    public static readonly Color Abnormal = Color.FromArgb(0x9A, 0x5B, 0x00);
    public static readonly Color AbnormalBackground = Color.FromArgb(0xFD, 0xF4, 0xE3);

    /// <summary>A panic value: immediately actionable, must be communicated.</summary>
    public static readonly Color Critical = Color.FromArgb(0xA8, 0x1B, 0x1B);
    public static readonly Color CriticalBackground = Color.FromArgb(0xFC, 0xEB, 0xEB);

    public static readonly Color Normal = Color.FromArgb(0x1E, 0x6B, 0x3A);
    public static readonly Color NormalBackground = Color.FromArgb(0xEC, 0xF6, 0xEF);

    /// <summary>Past its promised turnaround.</summary>
    public static readonly Color Overdue = Color.FromArgb(0xA8, 0x1B, 0x1B);

    /// <summary>Awaiting an action from someone.</summary>
    public static readonly Color Pending = Color.FromArgb(0x5C, 0x61, 0x6A);

    // ---- Typography -----------------------------------------------------------------------------

    private const string UiFamily = "Segoe UI";

    /// <summary>
    /// Arabic UI font. Segoe UI covers Arabic on Windows, but a Naskh face is far more readable at
    /// small sizes for long Arabic strings, so it is preferred when installed.
    /// </summary>
    private const string ArabicFamily = "Segoe UI";

    public static Font Body { get; } = new(UiFamily, 9f);
    public static Font BodyBold { get; } = new(UiFamily, 9f, FontStyle.Bold);
    public static Font Small { get; } = new(UiFamily, 8f);
    public static Font SmallBold { get; } = new(UiFamily, 8f, FontStyle.Bold);
    public static Font SectionTitle { get; } = new(UiFamily, 11f, FontStyle.Bold);
    public static Font PageTitle { get; } = new(UiFamily, 15f, FontStyle.Bold);

    /// <summary>
    /// Fixed-pitch, for result values, barcodes and accession numbers.
    /// <para>
    /// Not decoration: a monospaced face keeps digits in vertical alignment down a column, which is
    /// how a technician scanning forty values spots the one with an extra digit. It also prevents
    /// the 1/l and 0/O confusion that a proportional face invites when a value is read aloud.
    /// </para>
    /// </summary>
    public static Font Monospace { get; } = new("Consolas", 9.5f);
    public static Font MonospaceBold { get; } = new("Consolas", 9.5f, FontStyle.Bold);

    public static Font ForLanguage(Domain.Common.AppLanguage language, float size = 9f,
                                   FontStyle style = FontStyle.Regular) =>
        new(language == Domain.Common.AppLanguage.Arabic ? ArabicFamily : UiFamily, size, style);

    // ---- Metrics --------------------------------------------------------------------------------

    public const int Gutter = 12;
    public const int GutterTight = 6;
    public const int RowHeight = 24;
    public const int InputHeight = 26;
    public const int ButtonHeight = 30;
    public const int SidebarWidth = 220;

    // ---- Clinical state → appearance ------------------------------------------------------------

    /// <summary>
    /// Appearance for a result, by interpretation. Returns a glyph alongside the colours so a
    /// caller cannot accidentally convey the state by colour alone.
    /// </summary>
    public static (Color Foreground, Color Background, string Glyph, bool Bold) ForInterpretation(
        ResultInterpretation interpretation) => interpretation switch
    {
        ResultInterpretation.CriticalLow => (Critical, CriticalBackground, "↓↓", true),
        ResultInterpretation.CriticalHigh => (Critical, CriticalBackground, "↑↑", true),
        ResultInterpretation.Low => (Abnormal, AbnormalBackground, "↓", true),
        ResultInterpretation.High => (Abnormal, AbnormalBackground, "↑", true),
        ResultInterpretation.Abnormal => (Abnormal, AbnormalBackground, "*", true),
        ResultInterpretation.Normal => (TextPrimary, SurfaceRaised, string.Empty, false),
        _ => (TextMuted, SurfaceRaised, string.Empty, false)
    };

    /// <summary>
    /// A short glyph string for the flags carried by a result. Shown in its own narrow column, so
    /// a technician sees at a glance which values need attention and why.
    /// </summary>
    public static string FlagGlyphs(ResultFlag flags)
    {
        var glyphs = new List<string>(4);

        if (flags.HasFlag(ResultFlag.QcOutOfControl)) glyphs.Add("CIQ");
        if (flags.HasFlag(ResultFlag.DeltaCheckFailed)) glyphs.Add("Δ");
        if (flags.HasFlag(ResultFlag.SampleDefect)) glyphs.Add("!");
        if (flags.HasFlag(ResultFlag.Diluted)) glyphs.Add("×");
        if (flags.HasFlag(ResultFlag.Corrected)) glyphs.Add("R");
        if (flags.HasFlag(ResultFlag.Subcontracted)) glyphs.Add("ST");
        if (flags.HasFlag(ResultFlag.OutsideMeasuringRange)) glyphs.Add("≷");

        return string.Join(" ", glyphs);
    }

    public static Color ForPriority(OrderPriority priority) => priority switch
    {
        OrderPriority.VitalEmergency => Critical,
        OrderPriority.Urgent => Abnormal,
        _ => TextSecondary
    };

    public static Color ForOrderStatus(LabOrderStatus status) => status switch
    {
        LabOrderStatus.Cancelled => TextMuted,
        LabOrderStatus.Reported => Normal,
        LabOrderStatus.Validated => Normal,
        LabOrderStatus.PartiallyValidated => Accent,
        _ => Pending
    };

    // ---- Control helpers ------------------------------------------------------------------------

    public static Button PrimaryButton(string text) => new()
    {
        Text = text,
        Height = ButtonHeight,
        FlatStyle = FlatStyle.Flat,
        BackColor = Accent,
        ForeColor = TextOnAccent,
        Font = BodyBold,
        Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false,
        Padding = new Padding(10, 0, 10, 0)
    };

    public static Button SecondaryButton(string text) => new()
    {
        Text = text,
        Height = ButtonHeight,
        FlatStyle = FlatStyle.Flat,
        BackColor = SurfaceRaised,
        ForeColor = TextPrimary,
        Font = Body,
        Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false,
        Padding = new Padding(10, 0, 10, 0)
    };

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Small,
        ForeColor = TextSecondary,
        Margin = new Padding(0, 4, 0, 1)
    };

    public static TextBox Input(int width = 200) => new()
    {
        Width = width,
        Height = InputHeight,
        Font = Body,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SurfaceRaised,
        ForeColor = TextPrimary
    };

    /// <summary>
    /// A grid configured the way a laboratory list has to behave: read-only, full-row selection,
    /// no add-row, and sized to content. The defaults WinForms ships would let a technician edit a
    /// validated value straight in the grid.
    /// </summary>
    public static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill,
        BackgroundColor = SurfaceRaised,
        BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        EnableHeadersVisualStyles = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        Font = Body,
        GridColor = Border,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceSunken,
            ForeColor = TextSecondary,
            Font = SmallBold,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 6, 0),
            SelectionBackColor = SurfaceSunken,
            SelectionForeColor = TextSecondary
        },
        DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceRaised,
            ForeColor = TextPrimary,
            SelectionBackColor = AccentSubtle,
            SelectionForeColor = TextPrimary,
            Padding = new Padding(6, 0, 6, 0)
        },
        RowTemplate = { Height = RowHeight }
    };
}
