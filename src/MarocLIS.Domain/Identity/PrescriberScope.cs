namespace MarocLIS.Domain.Identity;

/// <summary>
/// Decides whether a signed-in account is an outside prescriber restricted to their own patients.
/// <para>
/// The <see cref="SystemRoles.Prescriber"/> role carries the ordinary read permissions
/// (<c>patients.view</c>, <c>results.view</c>, …), because the screens it needs are the laboratory's
/// own. What makes it "their own patients" is this scope: an account it applies to sees only the
/// orders that name its prescriber, and the patients on those orders. The scope is decided once, at
/// sign-in, and enforced by the data layer's query filters, so no screen can forget it.
/// </para>
/// <para>
/// Who is scoped: an account linked to a prescriber, holding the prescriber role, or recorded as a
/// prescribing physician — unless it also holds a laboratory working permission, in which case it is
/// staff (a biologist who also prescribes) and sees the laboratory's files as staff do. An account
/// that is scoped but linked to no prescriber sees nothing at all: an unfinished set-up must fail
/// closed, not open onto every patient.
/// </para>
/// </summary>
public static class PrescriberScope
{
    /// <summary>
    /// Permissions only laboratory staff hold. Any one of them means the account works in the
    /// laboratory and is not restricted to the prescriptions of one doctor.
    /// </summary>
    public static IReadOnlyList<string> StaffPermissions { get; } =
    [
        Permissions.Patients.Create,
        Permissions.Patients.Edit,
        Permissions.Orders.Create,
        Permissions.Orders.Edit,
        Permissions.Samples.Collect,
        Permissions.Samples.Receive,
        Permissions.Results.Enter,
        Permissions.Results.TechnicalValidate,
        Permissions.Results.BiologicalValidate,
        Permissions.Billing.View,
        Permissions.Administration.ViewAuditTrail,
        Permissions.Administration.ManageUsers
    ];

    /// <summary>Whether the account sees only the orders of its own prescriber.</summary>
    public static bool Applies(Guid? prescriberId, ProfessionalQualification qualification,
                               IEnumerable<string> roleCodes, IReadOnlySet<string> permissions)
    {
        var prescriberAccount = prescriberId is not null
                                || qualification == ProfessionalQualification.PrescribingPhysician
                                || roleCodes.Contains(SystemRoles.Prescriber, StringComparer.OrdinalIgnoreCase);

        return prescriberAccount && !StaffPermissions.Any(permissions.Contains);
    }
}
