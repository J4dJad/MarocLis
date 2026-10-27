using MarocLIS.Infrastructure.Persistence;

namespace MarocLIS.Web.Components;

/// <summary>
/// The message a web screen shows for an exception it caught: « modifié par un autre poste » when
/// someone saved the same record first, as the workstation says, otherwise « Erreur inattendue ».
/// </summary>
public static class WebFailures
{
    public static string KeyFor(Exception? exception) => ExceptionMessages.KeyFor(exception);
}
