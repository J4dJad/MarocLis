using System.Drawing;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The colours of <see cref="UiTheme"/>, kept free of WinForms so the test project can check them
/// against the design system's contrast rules.
/// </summary>
public static partial class UiTheme
{
    // The values are the tokens of the laboratory's design system « LIS · A Fluent clair » (light
    // theme): a neutral work surface, data on white, one teal for action, and status colours kept
    // for what matters. The member names predate it and are kept, so every screen follows.

    // ---- Neutrals -------------------------------------------------------------------------------

    /// <summary>surface: the work area behind the cards and grids.</summary>
    public static readonly Color Surface = Color.FromArgb(0xF6, 0xF8, 0xF9);

    /// <summary>surface-raised: header, navigation, cards, grids, fields.</summary>
    public static readonly Color SurfaceRaised = Color.White;

    /// <summary>surface-sunken: grid column headers, inactive areas.</summary>
    public static readonly Color SurfaceSunken = Color.FromArgb(0xF1, 0xF4, 0xF6);

    /// <summary>line: card borders, separators, grid lines.</summary>
    public static readonly Color Border = Color.FromArgb(0xDC, 0xE3, 0xE8);

    /// <summary>line-strong: field and secondary-button borders (3:1 on the surface).</summary>
    public static readonly Color BorderStrong = Color.FromArgb(0xC3, 0xCD, 0xD4);

    /// <summary>ink.</summary>
    public static readonly Color TextPrimary = Color.FromArgb(0x14, 0x21, 0x2B);

    /// <summary>ink-muted: secondary text and metadata.</summary>
    public static readonly Color TextSecondary = Color.FromArgb(0x51, 0x60, 0x6B);

    /// <summary>A lighter ink-muted for hints and shortcuts, still 4.5:1 on the surface.</summary>
    public static readonly Color TextMuted = Color.FromArgb(0x66, 0x74, 0x80);

    /// <summary>on-accent.</summary>
    public static readonly Color TextOnAccent = Color.White;

    // ---- Accent ---------------------------------------------------------------------------------

    /// <summary>Single accent, used for the primary action, the active entry and the status bar only.</summary>
    public static readonly Color Accent = Color.FromArgb(0x0B, 0x6E, 0x79);
    public static readonly Color AccentHover = Color.FromArgb(0x09, 0x5C, 0x65);
    public static readonly Color AccentPressed = Color.FromArgb(0x08, 0x51, 0x5A);

    /// <summary>accent-soft: active navigation entry, selected row.</summary>
    public static readonly Color AccentSubtle = Color.FromArgb(0xE3, 0xF1, 0xF2);

    /// <summary>accent-ink: text on accent-soft.</summary>
    public static readonly Color AccentInk = Color.FromArgb(0x08, 0x51, 0x5A);

    // ---- Clinical semantics ---------------------------------------------------------------------

    /// <summary>Out of the reference interval but not critical; a high value (warning-ink).</summary>
    public static readonly Color Abnormal = Color.FromArgb(0x8C, 0x35, 0x05);
    public static readonly Color AbnormalBackground = Color.FromArgb(0xFE, 0xF0, 0xE6);

    /// <summary>A panic value: immediately actionable, must be communicated.</summary>
    public static readonly Color Critical = Color.FromArgb(0xB4, 0x23, 0x18);
    public static readonly Color CriticalBackground = Color.FromArgb(0xFD, 0xEC, 0xEA);

    /// <summary>critical-ink: text on <see cref="CriticalBackground"/> when it is not a figure.</summary>
    public static readonly Color CriticalInk = Color.FromArgb(0x8A, 0x1C, 0x12);

    public static readonly Color Normal = Color.FromArgb(0x1E, 0x6B, 0x3A);
    public static readonly Color NormalBackground = Color.FromArgb(0xE7, 0xF4, 0xEC);

    /// <summary>
    /// In progress, to validate, and a low result (info-ink). Blue against orange for low against
    /// high, rather than green against red, which a colour-blind reader cannot tell apart.
    /// </summary>
    public static readonly Color Info = Color.FromArgb(0x1F, 0x4E, 0x9A);
    public static readonly Color InfoBackground = Color.FromArgb(0xE8, 0xEE, 0xFA);

    /// <summary>Past its promised turnaround (warning-ink, as the design system has it).</summary>
    public static readonly Color Overdue = Color.FromArgb(0x8C, 0x35, 0x05);

    /// <summary>Awaiting an action from someone.</summary>
    public static readonly Color Pending = Color.FromArgb(0x51, 0x60, 0x6B);
}
