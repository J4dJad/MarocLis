namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// The message a screen shows for an exception it caught, for the workstation and the browser alike.
/// <para>
/// Every screen catches the exceptions around a save so that a failure leaves it usable. A save
/// refused because another workstation or browser saved the same record first is not an
/// unexpected error: the operator only has to reload and try again, and is told so. Anything else
/// stays « Erreur inattendue ».
/// </para>
/// </summary>
public static class ExceptionMessages
{
    public const string UnexpectedKey = "Message.UnexpectedError";

    /// <summary>The resource key for <paramref name="exception"/>, looking through its wrappers.</summary>
    public static string KeyFor(Exception? exception)
    {
        // Through the wrappers too: a task's AggregateException, a TargetInvocationException.
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is ConcurrencyConflictException conflict) return conflict.ResourceKey;

        return UnexpectedKey;
    }
}
