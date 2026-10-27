using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Infrastructure.Persistence.Demo;

/// <summary>
/// Keeps the demonstration accounts from becoming a way into a laboratory's real data.
/// <para>
/// The <c>demo.*</c> accounts have their passwords published in <see cref="DemoDataSeeder.Users"/>,
/// and one of them is a responsible biologist who may release results. They are harmless on a
/// demonstration database and dangerous on any other: a production installation started once with
/// <c>Application:SeedDemoDataOnFirstRun</c> on — an empty database passes the seeder's own
/// check — keeps them after the laboratory has registered its first real patient, and anyone who
/// has read the source can then sign in as the biologist.
/// </para>
/// <para>
/// So once the database holds real data (see <see cref="FindRealDataAsync"/>) an account that still
/// answers to its published password is deactivated: at every start, whatever the flag says, and at
/// its own sign-in, which closes the window between the first real patient and the next start. Not
/// a forced password change at first sign-in instead: on a demonstration machine that would make
/// every demonstration begin with four password screens, and on a production one the first person
/// to sign in would choose the new password — whoever that is. An account whose password has been
/// changed is no longer a published credential and is left to the administrator.
/// </para>
/// </summary>
public static class DemoAccountGuard
{
    /// <summary>Whether this user name and password are one of the published demonstration pairs.</summary>
    public static bool IsPublishedCredential(string userName, string password) =>
        DemoDataSeeder.Users.Any(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(u.Password, password, StringComparison.Ordinal));

    /// <summary>
    /// Says what, if anything, shows that the database holds the laboratory's own data rather than
    /// demonstration data: a patient whose record number does not carry
    /// <see cref="DemoDataSeeder.DemoPrefix"/>, or an issued invoice that is not a demonstration
    /// patient's. Returns <c>null</c> on an empty database or one holding demonstration data only.
    /// <para>
    /// Deleted and other sites' rows count too: a soft-deleted real patient is still a real
    /// patient, and a demonstration must never land in a database that has ever held one.
    /// </para>
    /// </summary>
    public static async Task<string?> FindRealDataAsync(LisDbContext db, CancellationToken ct = default)
    {
        const string prefix = DemoDataSeeder.DemoPrefix;

        var realPatients = await db.Patients.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(p => !p.MedicalRecordNumber.StartsWith(prefix), ct);
        if (realPatients > 0)
            return PluralText.French("{0} {0:plural:patient|patients} hors démonstration", realPatients);

        // Every invoice the demonstration issues is a DEMO- patient's (and so is a credit note
        // correcting one, which carries the original's patient); a draft has consumed no number.
        var realInvoices = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled)
            .CountAsync(i => i.PatientId == null
                             || !db.Patients.IgnoreQueryFilters().Any(
                                    p => p.Id == i.PatientId
                                         && p.MedicalRecordNumber.StartsWith(prefix)), ct);
        if (realInvoices > 0)
            return PluralText.French("{0} {0:plural:facture émise|factures émises} hors démonstration", realInvoices);

        return null;
    }

    /// <summary>
    /// Deactivates every active demonstration account that still has its published password, when
    /// the database holds real data. Returns the user names deactivated, empty when there was
    /// nothing to do — which is the usual answer and costs one query on a production database.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DisableExposedAccountsAsync(
        LisDbContext db, IPasswordHasher passwordHasher, CancellationToken ct = default)
    {
        var names = DemoDataSeeder.Users.Select(u => u.UserName).ToList();

        var active = await db.Users.IgnoreQueryFilters()
            .Where(u => u.IsActive && names.Contains(u.UserName))
            .ToListAsync(ct);

        if (active.Count == 0) return [];
        if (await FindRealDataAsync(db, ct) is null) return [];

        var disabled = new List<string>();

        foreach (var user in active)
        {
            var published = DemoDataSeeder.Users.First(u => u.UserName == user.UserName).Password;
            if (!passwordHasher.Verify(published, user.PasswordHash)) continue;

            user.Deactivate();
            disabled.Add(user.UserName);
        }

        if (disabled.Count > 0) await SaveDeactivationsAsync(db, ct);

        return disabled;
    }

    /// <summary>
    /// Saves the deactivations made on <paramref name="db"/>'s tracked accounts. A sign-in of one of
    /// them stamped meanwhile (it writes the row directly) leaves the tracked row stale: the row is
    /// read again and the deactivation applied to it again, instead of failing — a deactivation is
    /// never lost to a sign-in at the same moment.
    /// </summary>
    public static async Task SaveDeactivationsAsync(LisDbContext db, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException conflict) when (attempt < 3)
            {
                foreach (var entry in conflict.Entries)
                {
                    await entry.ReloadAsync(ct);
                    if (entry.Entity is AppUser user) user.Deactivate();
                }
            }
        }
    }
}
