using MarocLIS.Application.Patients;
using MarocLIS.Desktop.Forms;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Composition;

/// <summary>
/// Opens the patient editor.
/// <para>
/// A factory rather than a direct registration because the form takes a patient id — creation and
/// editing are the same form — and because two screens open it: the patient list and the order
/// counter. A walk-in patient is registered and ordered for in the same breath, so the order screen
/// must be able to create a file without sending the clerk elsewhere.
/// </para>
/// </summary>
public sealed class PatientEditorFactory(
    PatientRegistrationService service,
    IPatientQueries queries,
    IUserDialogs dialogs,
    ILocalizer localizer,
    ICultureManager cultureManager)
{
    /// <summary>
    /// Shows the editor modally and returns the file that was created or edited, or null if the
    /// operator backed out. A returned id may be an <em>existing</em> patient: the duplicate check
    /// lets the clerk say "this is the person I already have", which is the outcome that check
    /// exists to produce.
    /// </summary>
    public Guid? Show(IWin32Window? owner, Guid? patientId = null)
    {
        using var form = new PatientEditorForm(service, queries, dialogs, localizer,
                                                cultureManager, patientId);

        return form.ShowDialog(owner) == DialogResult.OK ? form.ResultPatientId : null;
    }

    /// <summary>
    /// Opens a provisional "X-URG" file for a patient who cannot be identified yet, and returns
    /// it, or null if the operator backed out or the creation failed (the failure is shown).
    /// </summary>
    public async Task<Guid?> CreateProvisionalAsync(IWin32Window? owner)
    {
        using var dialog = new ProvisionalPatientDialog(localizer);

        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;

        var created = await service.RegisterProvisionalAsync(dialog.Gender, dialog.EstimatedAge);

        if (created.IsFailure)
        {
            dialogs.ShowFailure(created, localizer["Patient.Provisional"]);
            return null;
        }

        return created.Value;
    }
}
