using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Reporting;

namespace MarocLIS.Web.Printing;

/// <summary>The labels a save has just produced, and the right that save needed.</summary>
public enum NewLabels
{
    /// <summary>« Nouvelle demande »: every tube of the file (Orders.Create).</summary>
    Registration,

    /// <summary>« Ajouter des analyses » on the file: the new tubes only (Orders.Edit).</summary>
    AddedAnalyses,

    /// <summary>A tube refused at reception with a recollection: its replacement (Samples.Reject).</summary>
    Replacement
}

/// <summary>A label PDF ready to be downloaded.</summary>
public sealed record LabelPdf(string FileName, byte[] Content, int Count);

/// <summary>
/// The tube labels of the browser: the workstation's label PDF (<see cref="ISampleLabelRenderer"/>,
/// 50 × 25 mm, one page per label), downloaded to be printed on the label printer at 100 %.
/// <para>
/// The browser cannot reach the label printer, so where the workstation prints (LabelOutput) the
/// web hands over the same PDF the workstation falls back to when no printer is configured. The
/// rights are the workstation's: the labels of a save need the save's right — the workstation
/// prints them as part of it, unrecorded — and any other copy is a reprint, which goes through
/// <see cref="SampleWorkflowService.ReprintLabelsAsync"/>: Samples.Reprint, a reason, and a
/// LabelReprinted event on each tube with the reason in the audit trail.
/// </para>
/// </summary>
public sealed class LabelPdfService(ISampleLabelRenderer renderer, IPermissionGuard guard, SampleWorkflowService workflow)
{
    /// <summary>The right the save that produced the labels needed, as the workstation's gates.</summary>
    public static string RightFor(NewLabels source) => source switch
    {
        NewLabels.Registration => Permissions.Orders.Create,
        NewLabels.AddedAnalyses => Permissions.Orders.Edit,
        NewLabels.Replacement => Permissions.Samples.Reject,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    /// <summary>« etiquettes-2610-000015.pdf »: the file's number, with nothing a file name cannot hold.</summary>
    public static string FileName(string accessionNumber)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. accessionNumber.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c)]);
        return $"etiquettes-{(safe.Length == 0 ? "dossier" : safe)}.pdf";
    }

    /// <summary>
    /// The labels a save on this screen has just produced (they are the save's result, held by the
    /// screen), checked against the save's right.
    /// </summary>
    public Result<LabelPdf> ForNewTubes(NewLabels source, IReadOnlyList<SampleLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var permitted = guard.Require(RightFor(source));
        if (permitted.IsFailure) return Result.Failure<LabelPdf>(permitted.FirstError);

        return Render(labels);
    }

    /// <summary>
    /// Every label of the file again, against a reason — the workstation's reprint (« Imprimer les
    /// étiquettes » of the file and of the collection), recorded as it records it.
    /// </summary>
    public async Task<Result<LabelPdf>> ReprintAsync(Guid labOrderId, string reason, CancellationToken ct = default)
    {
        // Checked here as well as in the workflow, so a refusal renders and reads nothing.
        var permitted = guard.Require(Permissions.Samples.Reprint);
        if (permitted.IsFailure) return Result.Failure<LabelPdf>(permitted.FirstError);

        var labels = await workflow.ReprintLabelsAsync(labOrderId, reason, ct);
        if (labels.IsFailure) return Result.Failure<LabelPdf>(labels.Errors);

        return Render(labels.Value);
    }

    private Result<LabelPdf> Render(IReadOnlyList<SampleLabel> labels)
    {
        if (labels.Count == 0) return Result.Failure<LabelPdf>(Error.Validation("Label_NothingToPrint"));

        return Result.Success(new LabelPdf(FileName(labels[0].AccessionNumber), renderer.Render(labels), labels.Count));
    }
}
