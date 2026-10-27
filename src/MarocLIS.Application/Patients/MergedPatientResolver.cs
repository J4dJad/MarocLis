using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.Patients;

/// <summary>
/// The file an order's patient now lives in. A merge leaves the duplicate's orders where they were
/// and points the duplicate at the surviving file, whose identity, sex and date of birth are the
/// corrected ones: whatever interprets, prints or sends a result follows that pointer.
/// </summary>
public static class MergedPatientResolver
{
    /// <summary>
    /// The files from the order's own patient to the surviving one, in that order: one file when
    /// it was never merged. Bounded by the files already seen, so a corrupt pointer cycle ends.
    /// </summary>
    public static async Task<IReadOnlyList<Patient>> ChainAsync(this IRepository<Patient> patients, Guid patientId,
                                                                bool withDetails = false, CancellationToken ct = default)
    {
        var chain = new List<Patient>();
        var visited = new HashSet<Guid>();
        var next = (Guid?)patientId;

        while (next is { } id && visited.Add(id))
        {
            var patient = withDetails
                ? await patients.GetByIdWithDetailsAsync(id, ct)
                : await patients.GetByIdAsync(id, ct);
            if (patient is null) break;

            chain.Add(patient);
            next = patient.MergedIntoPatientId;
        }

        return chain;
    }

    /// <summary>The surviving file of <paramref name="patientId"/>: itself when never merged; null when not found.</summary>
    public static async Task<Patient?> SurvivorAsync(this IRepository<Patient> patients, Guid patientId,
                                                     bool withDetails = false, CancellationToken ct = default) =>
        (await patients.ChainAsync(patientId, withDetails, ct)).LastOrDefault();
}
