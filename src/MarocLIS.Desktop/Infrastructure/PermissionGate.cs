using System.Runtime.CompilerServices;
using MarocLIS.Application.Abstractions;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Greys out the buttons of a screen that the signed-in user has no right to use.
/// <para>
/// The services refuse an operation without its permission whatever the screen does, but a button
/// that is offered and then answered with "you do not have the right" is the screen promising
/// something it will not deliver: a technician found Approve, Refund and Release report enabled in
/// front of them. A gated button stays disabled for the life of the screen. Each screen also turns
/// its buttons on and off by the state of the selected row, so the gate re-applies itself whenever
/// that logic enables the button again.
/// </para>
/// <para>
/// Disabled rather than hidden: the buttons sit at fixed places in strips laid out by the screen,
/// and a missing one left a hole in the row.
/// </para>
/// </summary>
public static class PermissionGate
{
    private static readonly ConditionalWeakTable<Control, object> Blocked = new();

    /// <summary>The signed-in user, set by the shell once the session is open.</summary>
    public static ICurrentUser? User { get; set; }

    /// <summary>Whether the signed-in user holds at least one of <paramref name="permissions"/>.</summary>
    public static bool Allows(params string[] permissions) =>
        User is { } user && permissions.Any(user.Has);

    /// <summary>
    /// Keeps <paramref name="controls"/> disabled unless the user holds one of
    /// <paramref name="anyOf"/>.
    /// </summary>
    public static void Require(string[] anyOf, params Control[] controls)
    {
        if (Allows(anyOf)) return;

        foreach (var control in controls)
        {
            if (!Blocked.TryAdd(control, anyOf)) continue;

            control.Enabled = false;
            control.EnabledChanged += (_, _) =>
            {
                if (control.Enabled) control.Enabled = false;
            };
        }
    }

    public static void Require(string permission, params Control[] controls) =>
        Require([permission], controls);

    /// <summary>
    /// Whether <paramref name="control"/> was gated off. A double-click that does what the button
    /// does checks this, so the grid does not open the door the button keeps shut.
    /// </summary>
    public static bool IsBlocked(Control control) => Blocked.TryGetValue(control, out _);
}
