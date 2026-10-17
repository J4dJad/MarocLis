namespace MarocLIS.Application.HumanResources;

/// <summary>What became of the LIS account linked to a personnel file after its departure was saved.</summary>
public enum DepartedAccountState
{
    /// <summary>The exit date is past: the account was deactivated now.</summary>
    Deactivated,

    /// <summary>The exit date is today or ahead: the account will be deactivated the day after it.</summary>
    Scheduled,

    /// <summary>The account is already inactive (deactivated earlier, or by the departure now withdrawn).</summary>
    Inactive,

    /// <summary>
    /// The exit date is past but the account is the last active one able to manage the users:
    /// left active, so the installation keeps an administrator. Another one has to be named first.
    /// </summary>
    KeptLastAdministrator
}

public sealed record DepartedAccountOutcome(string UserName, DepartedAccountState State, DateOnly? ExitDate)
{
    /// <summary>The first day the account is off: the day after the exit date, the last day worked.</summary>
    public DateOnly? DeactivationDate => ExitDate?.AddDays(1);
}

/// <summary>
/// Deactivates the LIS account of an employee who has left (owner decision, cycle 5): the account
/// linked to a personnel file stops the day after the exit date, which is the last day worked. A
/// departure recorded after its date deactivates at once; one recorded on or before it is caught on
/// the day after by the sign-in and by the session check of the workstation still open. Each deactivation goes to the
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
    /// Deactivates every still-active account whose employee's exit date is past, or only the
    /// one of <paramref name="userId"/>. Returns the accounts deactivated.
    /// </summary>
    Task<IReadOnlyList<string>> DeactivateDueAsync(Guid? userId = null, CancellationToken ct = default);
}
