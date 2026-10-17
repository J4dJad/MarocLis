namespace MarocLIS.Application.HumanResources;

/// <summary>What became of the LIS account linked to a personnel file after its departure was saved.</summary>
public enum DepartedAccountState
{
    /// <summary>The exit date is reached: the account was deactivated now.</summary>
    Deactivated,

    /// <summary>The exit date is ahead: the account will be deactivated on that day.</summary>
    Scheduled,

    /// <summary>The account is already inactive (deactivated earlier, or by the departure now withdrawn).</summary>
    Inactive,

    /// <summary>
    /// The exit date is reached but the account is the last active one able to manage the users:
    /// left active, so the installation keeps an administrator. Another one has to be named first.
    /// </summary>
    KeptLastAdministrator
}

public sealed record DepartedAccountOutcome(string UserName, DepartedAccountState State, DateOnly? ExitDate);

/// <summary>
/// Deactivates the LIS account of an employee who has left (owner decision, cycle 5): the account
/// linked to a personnel file stops on the exit date, the last day worked included. A departure
/// recorded on or after its date deactivates at once; one recorded ahead is caught on the day by
/// the sign-in and by the session check of the workstation still open. Each deactivation goes to the
/// audit trail and to the security log with the departure as its reason.
/// </summary>
public interface IDepartedAccounts
{
    /// <summary>
    /// Applies the departure of one personnel file to its linked account, after the departure (or
    /// its correction or withdrawal) was saved. Null when no account is linked.
    /// </summary>
    Task<DepartedAccountOutcome?> ApplyExitAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>
    /// Deactivates every still-active account whose employee's exit date is reached, or only the
    /// one of <paramref name="userId"/>. Returns the accounts deactivated.
    /// </summary>
    Task<IReadOnlyList<string>> DeactivateDueAsync(Guid? userId = null, CancellationToken ct = default);
}
