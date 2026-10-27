using MarocLIS.Infrastructure.Persistence;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The message a screen shows for an exception it caught, kept free of WinForms so the test
/// project can compile this file and check it. The rule itself is shared with the web screens
/// (<see cref="ExceptionMessages"/>).
/// </summary>
internal static class FailureMessages
{
    public const string UnexpectedKey = ExceptionMessages.UnexpectedKey;

    /// <summary>
    /// The resource key for <paramref name="exception"/>: its own key when another workstation
    /// saved the same record first, otherwise the generic « Erreur inattendue ».
    /// </summary>
    public static string KeyFor(Exception? exception) => ExceptionMessages.KeyFor(exception);
}
