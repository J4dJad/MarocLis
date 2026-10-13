using System.Text.Json;
using Microsoft.Extensions.Logging;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>One thing a shortcut can do: open a screen or run a shell-wide action.</summary>
public sealed record ShortcutAction(string Id, string Label, Keys DefaultKeys);

/// <summary>
/// The keyboard shortcuts of one user: the defaults, with whatever that user changed on top.
/// <para>
/// Kept per user rather than per workstation, because the reception PC is shared by several
/// clerks and each keeps the keys they learned. The changes are stored in
/// <c>%LOCALAPPDATA%\MarocLIS\shortcuts\{user}.json</c>, so only what differs from the defaults is
/// written and a later change of default reaches everyone who did not override it.
/// </para>
/// </summary>
public sealed class KeyboardShortcuts
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Dictionary<string, Keys> _defaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Keys> _overrides = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly ILogger? _logger;

    public KeyboardShortcuts(Guid userId, ILogger? logger = null, string? directory = null)
    {
        _logger = logger;
        _path = Path.Combine(
            directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarocLIS", "shortcuts"),
            $"{userId:N}.json");

        Load();
    }

    /// <summary>Declares an action and its default keys; called once per action by the shell.</summary>
    public void Define(string id, Keys defaultKeys) => _defaults[id] = defaultKeys;

    public Keys Default(string id) => _defaults.GetValueOrDefault(id);

    public Keys For(string id) =>
        _overrides.TryGetValue(id, out var keys) ? keys : _defaults.GetValueOrDefault(id);

    /// <summary>The action bound to <paramref name="keys"/>, if any.</summary>
    public string? Find(Keys keys)
    {
        if (keys == Keys.None) return null;
        return _defaults.Keys.FirstOrDefault(id => For(id) == keys);
    }

    /// <summary>Replaces this user's choices; anything equal to its default is not stored.</summary>
    public bool Save(IReadOnlyDictionary<string, Keys> chosen)
    {
        var overrides = chosen
            .Where(c => _defaults.ContainsKey(c.Key) && _defaults[c.Key] != c.Value)
            .ToDictionary(c => c.Key, c => (int)c.Value, StringComparer.Ordinal);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(overrides, Json));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving the keyboard shortcuts failed.");
            return false;
        }

        _overrides.Clear();
        foreach (var (id, keys) in overrides) _overrides[id] = (Keys)keys;
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var stored = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_path));
            foreach (var (id, keys) in stored ?? []) _overrides[id] = (Keys)keys;
        }
        catch (Exception ex)
        {
            // A damaged file must not cost the user the application; the defaults apply.
            _logger?.LogWarning(ex, "Reading the keyboard shortcuts failed.");
        }
    }

    /// <summary>
    /// Whether <paramref name="keys"/> can be a shortcut: it needs Ctrl or Alt, or must be a
    /// function key, otherwise it would steal letters typed into the fields.
    /// </summary>
    public static bool IsAssignable(Keys keys)
    {
        var code = keys & Keys.KeyCode;
        if (code is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return false;

        return (keys & (Keys.Control | Keys.Alt)) != 0 || code is >= Keys.F1 and <= Keys.F24;
    }

    /// <summary>"Ctrl+1", "Ctrl+Maj+S", "F5": the way the keys are written on screen.</summary>
    public static string Format(Keys keys, ILocalizer localizer)
    {
        if (keys == Keys.None) return string.Empty;

        var parts = new List<string>();
        if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
        if ((keys & Keys.Alt) != 0) parts.Add("Alt");
        if ((keys & Keys.Shift) != 0) parts.Add(localizer["Shortcut.ShiftKey"]);

        var code = keys & Keys.KeyCode;
        parts.Add(code switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => ((char)('0' + (code - Keys.NumPad0))).ToString(),
            Keys.Return => "Entrée",
            Keys.Delete => "Suppr",
            Keys.Oemcomma => ",",
            Keys.OemPeriod => ".",
            Keys.OemMinus => "-",
            Keys.Oemplus => "+",
            _ => code.ToString()
        });

        return string.Join("+", parts);
    }
}
