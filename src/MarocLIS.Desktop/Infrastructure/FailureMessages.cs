using MarocLIS.Infrastructure.Persistence;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The message a screen shows for an exception it caught, kept free of WinForms so the test
/// project can compile this file and check it.
/// </summary>
internal static class FailureMessages
{
    public const string UnexpectedKey = "Message.UnexpectedError";

    /// <summary>
    /// The resource key for <paramref name="exception"/>: its own key when another workstation
    /// saved the same record first, otherwise the generic « Erreur inattendue ».
    /// <para>
    /// The screens catch every exception around a save so that a failure leaves them usable, and
    /// that catch showed « Erreur inattendue » for a conflict too, although the save was refused
    /// on purpose and the operator only has to reload. The application-wide handler already told
    /// the two apart; every screen now does the same, through this one rule.
    /// </para>
    /// </summary>
    public static string KeyFor(Exception? exception)
    {
        // Through the wrappers too: a task's AggregateException, a TargetInvocationException.
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is ConcurrencyConflictException conflict) return conflict.ResourceKey;

        return UnexpectedKey;
    }
}
