using System.Security.Cryptography;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Reports;

/// <summary>
/// Everything a report needs, assembled once and handed to the renderer. A flat, immutable
/// projection rather than live entities: the PDF is a frozen artefact, and rendering from entities
/// that can still change would make a reprint differ from the original.
/// </summary>
public sealed record ReportModel
{
    public required Guid LabOrderId { get; init; }
    public required string AccessionNumber { get; init; }
    public required AppLanguage Language { get; init; }
    public required int VersionNumber { get; init; }
    public required ReportCompleteness Completeness { get; init; }
    public ReportAmendmentKind AmendmentKind { get; init; }
    public MultilingualText? AmendmentReason { get; init; }
    public string? AmendedParameterCodes { get; init; }

    // ---- Laboratory identity (loi 12-01: the report identifies the laboratory) -----------------

    public required string LaboratoryName { get; init; }
    public string? LaboratoryLegalName { get; init; }
    public string? LaboratoryAddress { get; init; }
    public string? LaboratoryCity { get; init; }
    public string? LaboratoryPhone { get; init; }
    public string? LaboratoryEmail { get; init; }
    public string? MinistryAuthorizationNumber { get; init; }
    public string? AccreditationNumber { get; init; }
    public bool IsAccredited { get; init; }
    public byte[]? LogoPng { get; init; }

    // ---- Patient --------------------------------------------------------------------------------

    public required string PatientName { get; init; }
    public string? PatientNameArabic { get; init; }
    public required string MedicalRecordNumber { get; init; }
    public Gender Gender { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public int? AgeYears { get; init; }
    public string? Cin { get; init; }
    public bool IsPregnant { get; init; }

    // ---- Order context --------------------------------------------------------------------------

    public string? PrescriberName { get; init; }
    public string? PrescriberSpeciality { get; init; }
    public DateOnly? PrescriptionDate { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset? CollectedAt { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public required DateTimeOffset ReleasedAt { get; init; }
    public string? ClinicalInformation { get; init; }
    public bool IsUrgent { get; init; }

    // ---- Signature block ------------------------------------------------------------------------

    public required string SignatoryName { get; init; }
    public string? SignatoryNameArabic { get; init; }
    public required string SignatoryQualification { get; init; }
    public required string SignatoryOrderNumber { get; init; }
    public byte[]? SignaturePng { get; init; }

    // ---- Content ---------------------------------------------------------------------------------

    public required IReadOnlyList<ReportSection> Sections { get; init; }
    public MultilingualText? Conclusion { get; init; }

    public bool ContainsSubcontractedResults { get; init; }
    public bool ContainsReservations { get; init; }
    public bool ContainsCriticalResults { get; init; }
    public bool AllResultsAccredited { get; init; }

    /// <summary>Pre-analytical reservations disclosed at the foot of the report.</summary>
    public IReadOnlyList<string> Reservations { get; init; } = [];
}

/// <summary>One discipline's block on the report.</summary>
public sealed record ReportSection(
    string DepartmentCode,
    MultilingualText DepartmentName,
    int DisplayOrder,
    IReadOnlyList<ReportAnalysisBlock> Analyses);

public sealed record ReportAnalysisBlock(
    string AnalysisCode,
    MultilingualText AnalysisName,
    MultilingualText? Method,
    bool IsAccredited,
    bool IsSubcontracted,
    DateTimeOffset? ValidatedAt,
    IReadOnlyList<ReportLine> Lines,
    /// <summary>The partner laboratory that performed a subcontracted analysis.</summary>
    string? SubcontractorName = null);

/// <summary>One printed line: a result, or a section heading within a panel.</summary>
public sealed record ReportLine
{
    public required string ParameterCode { get; init; }
    public required MultilingualText ParameterName { get; init; }
    public bool IsHeading { get; init; }
    public int IndentLevel { get; init; }

    public required string Value { get; init; }
    public string? Unit { get; init; }
    public string? ReferenceRange { get; init; }
    public string? PreviousValue { get; init; }
    public DateOnly? PreviousDate { get; init; }

    public ResultInterpretation Interpretation { get; init; }
    public ResultFlag Flags { get; init; }

    /// <summary>Printed annotation: the technical comment, when the laboratory chose to disclose it.</summary>
    public string? Comment { get; init; }

    public MultilingualText? BiologistComment { get; init; }

    public bool IsAbnormal => Interpretation is not (ResultInterpretation.Normal
                                                 or ResultInterpretation.NotInterpretable);

    public bool IsCritical => Flags.HasFlag(ResultFlag.CriticalLow)
                              || Flags.HasFlag(ResultFlag.CriticalHigh);
}

/// <summary>Renders a <see cref="ReportModel"/> to PDF. Implemented in MarocLIS.Reporting.</summary>
public interface IReportRenderer
{
    byte[] Render(ReportModel model);

    /// <summary>Page count of the rendered document, for the "page n of m" footer and the archive.</summary>
    int CountPages(byte[] pdf);
}

public sealed record ReleasedReport(Guid ReportId, int VersionNumber, byte[] Pdf, string ContentHash);

/// <summary>
/// Assembles and releases a report.
/// <para>
/// A report is never edited. Releasing again produces a new version that states what changed and
/// why, and the previous version is retained and marked superseded — because the earlier version is
/// already in a clinician's hands and may have been acted on. The rendered PDF is stored rather
/// than regenerated, so a reprint two years later is the document that was actually issued and not
/// a fresh render against a catalogue that has since moved on.
/// </para>
/// </summary>
public sealed class ReportReleaseService(
    IRepository<LabOrder> orders,
    IRepository<Patient> patients,
    IRepository<LabSite> sites,
    IRepository<AnalysisTest> analyses,
    IRepository<Sample> samples,
    IRepository<AppUser> users,
    IRepository<LabReport> reports,
    IResultQueries resultQueries,
    IReportQueries reportQueries,
    IReportRenderer renderer,
    IFontAvailability fonts,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IDataAccessAuditor accessAuditor,
    IClock clock,
    IMessageSender messages)
{
    /// <summary>
    /// Releases a report for the analyses validated so far.
    /// <para>
    /// A partial report is legitimate and common — a culture takes three days while the biochemistry
    /// is ready in an hour — but it is marked as provisional on its face so a clinician cannot
    /// mistake it for the complete file.
    /// </para>
    /// </summary>
    public async Task<Result<ReleasedReport>> ReleaseAsync(
        Guid orderId,
        AppLanguage? language = null,
        MultilingualText? conclusion = null,
        ReportAmendmentKind amendmentKind = ReportAmendmentKind.None,
        MultilingualText? amendmentReason = null,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(amendmentKind == ReportAmendmentKind.None
            ? Permissions.Reports.Print
            : Permissions.Reports.Amend);

        if (permitted.IsFailure) return Result.Failure<ReleasedReport>(permitted.FirstError);

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure<ReleasedReport>(Error.NotFound("Order_NotFound"));

        var patient = await patients.GetByIdAsync(order.PatientId, ct);
        if (patient is null) return Result.Failure<ReleasedReport>(Error.NotFound("Order_PatientNotFound"));

        var site = await sites.GetByIdAsync(order.SiteId, ct);
        if (site is null) return Result.Failure<ReleasedReport>(Error.NotFound("Report_SiteNotFound"));

        // Refused rather than rendered in a substituted face. Arabic needs contextual forms, and a
        // font without them produces isolated letters — a document that looks complete, carries a
        // number and a signature, and cannot be read by the patient it is addressed to. A Latin
        // report in a substituted font is merely ugly, so only Arabic is blocked here; the missing
        // Latin font is reported at start-up instead.
        // The same resolution the render uses below, read early so the refusal happens before any
        // of the assembly work rather than after it.
        if ((language ?? order.ReportLanguage) == AppLanguage.Arabic
            && !fonts.IsAvailable(fonts.ArabicFamily))
            return Result.Failure<ReleasedReport>(
                Error.Regulatory("Report_ArabicFontMissing", fonts.ArabicFamily));

        // A site without a ministry authorisation and a responsible biologist may not release
        // results at all; checking here stops a defective document being produced in the first place.
        var operable = site.ValidateForOperation(clock.Today);
        if (operable.IsFailure) return Result.Failure<ReleasedReport>(operable.Errors);

        var biologist = await ResolveSignatoryAsync(order, ct);
        if (biologist.IsFailure) return Result.Failure<ReleasedReport>(biologist.FirstError);

        var stored = await resultQueries.GetResultsForOrderAsync(orderId, null, ct);

        // Only validated results go on a report. Anything else would be the laboratory publishing a
        // figure no biologist has stood behind.
        var releasable = stored
            .Where(r => r.Status is TestResultStatus.Validated or TestResultStatus.Reported
                                 or TestResultStatus.NotDone)
            .ToList();

        if (releasable.Count == 0)
            return Result.Failure<ReleasedReport>(Error.Validation("Report_NoResultsIncluded"));

        var includedItemIds = releasable.Select(r => r.LabOrderItemId).Distinct().ToList();

        var liveItems = order.Items.Where(i => !i.IsCancelled).ToList();
        var completeness = includedItemIds.Count == liveItems.Count
                           && liveItems.All(i => i.Status is LabOrderItemStatus.Validated
                                                          or LabOrderItemStatus.Reported)
            ? ReportCompleteness.Final
            : ReportCompleteness.Partial;

        var previousVersion = await reportQueries.GetLatestVersionAsync(orderId, ct);
        var versionNumber = (previousVersion?.VersionNumber ?? 0) + 1;

        // A reissue is an amendment whether the caller said so or not.
        //
        // Two cases, and the second is the one that matters. A report whose delivery was recorded
        // is plainly an amendment: the recipient holds a document this one contradicts. But a
        // **final** report is also an amendment on reissue even with no delivery on record, because
        // recording the hand-over is a clerical act that gets skipped — the counter prints the
        // report, the patient takes it, and nobody ticks the box. Trusting the delivery log alone
        // made this guard depend on bookkeeping that may not have happened, so re-releasing simply
        // minted v2, v3 of the same final report with none of them marked as a rectification.
        //
        // A *partial* report is different: it is superseded by the complete one in the normal
        // course of a file finishing, and calling that an amendment would make every ordinary
        // two-stage release look like a correction.
        //
        // So a delivered partial followed by a version that carries newly validated results is the
        // file completing, not a correction: the recipient's copy is not contradicted, only extended.
        // Reissuing a delivered partial with nothing new on it is still a reissue.
        var addsResults = releasable.Any(r => r.Status == TestResultStatus.Validated);

        if (amendmentKind == ReportAmendmentKind.None && previousVersion is not null
            && (previousVersion.Completeness == ReportCompleteness.Final
                || (previousVersion.WasDelivered && !addsResults)))
            return Result.Failure<ReleasedReport>(
                Error.Regulatory("Report_ReissueRequiresAmendmentReason"));

        var reportLanguage = language ?? order.ReportLanguage;

        var model = await BuildModelAsync(order, patient, site, biologist.Value, releasable,
                                           reportLanguage, versionNumber, completeness,
                                           amendmentKind, amendmentReason, conclusion, ct);

        var report = new LabReport(order.Id, order.SiteId, versionNumber, completeness,
                                   reportLanguage, clock.Now, biologist.Value.Id)
        {
            SignatoryName = model.SignatoryName,
            SignatoryQualification = model.SignatoryQualification,
            SignatoryOrderNumber = model.SignatoryOrderNumber,
            Conclusion = conclusion,
            AmendmentKind = amendmentKind,
            AmendmentReason = amendmentReason,
            SupersedesReportId = amendmentKind == ReportAmendmentKind.None ? null : previousVersion?.Id,
            ContainsSubcontractedResults = model.ContainsSubcontractedResults,
            ContainsReservations = model.ContainsReservations,
            ContainsCriticalResults = model.ContainsCriticalResults,
            AllResultsAccredited = model.AllResultsAccredited
        };

        report.Include(includedItemIds);

        var valid = report.ValidateForRelease();
        if (valid.IsFailure) return Result.Failure<ReleasedReport>(valid.Errors);

        var pdf = renderer.Render(model);
        var hash = Convert.ToHexString(SHA256.HashData(pdf));

        report.RenderedPdf = pdf;
        report.ContentHash = hash;
        report.PageCount = renderer.CountPages(pdf);

        // Opaque token for an SMS or email download link, with a bounded life.
        report.AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        report.AccessTokenExpiresAt = clock.Now.AddDays(30);

        await reports.AddAsync(report, ct);

        if (previousVersion is not null && amendmentKind != ReportAmendmentKind.None)
        {
            var superseded = await reports.GetByIdAsync(previousVersion.Id, ct);
            superseded?.MarkSupersededBy(report.Id);
        }

        foreach (var item in liveItems.Where(i => includedItemIds.Contains(i.Id)))
            item.MarkReported(clock.Now);

        foreach (var result in releasable) result.MarkReported();

        order.MarkFirstReported(clock.Now);
        order.RecomputeStatus();

        unitOfWork.AuditReason = amendmentKind == ReportAmendmentKind.None
            ? $"Édition du compte rendu v{versionNumber} — dossier {order.AccessionNumber}"
            : $"Compte rendu rectifié v{versionNumber} — dossier {order.AccessionNumber} : " +
              (amendmentReason?.Fr ?? string.Empty);

        await unitOfWork.SaveChangesAsync(ct);

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.ViewReport, order.PatientId,
                                     order.Id, "ReportRelease", releasable.Count, ct: ct);

        return Result.Success(new ReleasedReport(report.Id, versionNumber, pdf, hash));
    }

    /// <summary>
    /// The register of released reports for the operator's site.
    /// <para>
    /// Consulting it is itself a search over medical records, so it is logged under loi 08-09 with
    /// the row count — the same treatment the patient search gets.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<ReportRegisterRow>>> GetRegisterAsync(
        string? search = null, bool onlyUndelivered = false, int withinDays = 30,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Reports.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<ReportRegisterRow>>(permitted.FirstError);

        var rows = await reportQueries.GetRegisterAsync(
            currentUser.SiteId,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            onlyUndelivered,
            clock.Today.AddDays(-Math.Max(1, withinDays)),
            ct);

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.Search, recordCount: rows.Count,
                                      queryDescription: "Registre des comptes rendus", ct: ct);

        return Result.Success(rows);
    }

    /// <summary>
    /// Orders with biologically validated results that are on no report yet.
    /// <para>
    /// The validation worklist drops an order as soon as nothing on it awaits a biologist, so an
    /// order validated in part, with its other analyses still on the bench, used to be on no screen
    /// at all: validated, unreleased, and impossible to release until the rest caught up.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<PendingReportRow>>> GetPendingReleaseAsync(
        string? search = null, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Reports.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<PendingReportRow>>(permitted.FirstError);

        var rows = await reportQueries.GetPendingReleaseAsync(
            currentUser.SiteId, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), ct);

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.Search, recordCount: rows.Count,
                                      queryDescription: "Comptes rendus à émettre", ct: ct);

        return Result.Success(rows);
    }

    public async Task<Result<IReadOnlyList<ReportDeliveryRow>>> GetDeliveriesAsync(
        Guid reportId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Reports.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<ReportDeliveryRow>>(permitted.FirstError);

        return Result.Success(await reportQueries.GetDeliveriesAsync(reportId, ct));
    }

    /// <summary>
    /// Records a delivery, and for e-mail and SMS actually sends it.
    /// <para>
    /// Handing a report over is a disclosure of health data, so who took it, and on whose
    /// authority when it is not the patient, is part of the record. An e-mail carries the stored
    /// PDF; an SMS only says the report is ready, never a result. Sending to the patient
    /// electronically needs their consent to electronic delivery on file (loi 09-08). A failed
    /// send is kept as a failed attempt, so the register shows it was tried and why it did not go.
    /// When no e-mail or SMS account is configured the delivery is recorded as before, as a send
    /// the operator made by other means.
    /// </para>
    /// </summary>
    public async Task<Result<DeliveryOutcome>> RecordDeliveryAsync(
        Guid reportId, DeliveryChannel channel, ReportRecipientKind recipientKind,
        string? recipientName, string? recipientIdentifier,
        string? bearerName = null, string? bearerCin = null,
        Guid? authorizingConsentId = null, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Reports.Print, Permissions.Reports.Send);
        if (permitted.IsFailure) return Result.Failure<DeliveryOutcome>(permitted.FirstError);

        var report = await reports.GetByIdWithDetailsAsync(reportId, ct);
        if (report is null) return Result.Failure<DeliveryOutcome>(Error.NotFound("Report_NotFound"));

        // Release to anyone other than the patient or the prescriber needs the patient's explicit
        // third-party disclosure consent on file (loi 08-09).
        if (recipientKind == ReportRecipientKind.AuthorizedThirdParty && authorizingConsentId is null)
            return Result.Failure<DeliveryOutcome>(Error.Regulatory("Report_ThirdPartyRequiresConsent"));

        var order = await orders.GetByIdAsync(report.LabOrderId, ct);
        if (order is null) return Result.Failure<DeliveryOutcome>(Error.NotFound("Order_NotFound"));

        var electronic = channel is DeliveryChannel.Email or DeliveryChannel.SmsLink;

        if (electronic && recipientKind == ReportRecipientKind.Patient)
        {
            var patient = await patients.GetByIdWithDetailsAsync(order.PatientId, ct);

            if (patient is null || !patient.HasConsent(ConsentPurpose.ElectronicDelivery))
                return Result.Failure<DeliveryOutcome>(Error.Regulatory("Report_ElectronicDeliveryRequiresConsent"));
        }

        var delivery = new LabReportDelivery(report.Id, channel, recipientKind, clock.Now)
        {
            RecipientName = recipientName,
            RecipientIdentifier = recipientIdentifier,
            BearerName = bearerName,
            BearerCin = bearerCin,
            AuthorizingConsentId = authorizingConsentId,
            HandedOverByUserId = currentUser.UserId
        };

        var outcome = DeliveryResultKind.Recorded;
        string? failure = null;
        Error? failureError = null;

        if (channel == DeliveryChannel.Email && messages.CanSendEmail
            || channel == DeliveryChannel.SmsLink && messages.CanSendSms)
        {
            if (string.IsNullOrWhiteSpace(recipientIdentifier))
                return Result.Failure<DeliveryOutcome>(Error.Validation("Report_RecipientIdentifierRequired"));

            var site = await sites.GetByIdAsync(order.SiteId, ct);
            var laboratory = site?.Name.For(AppLanguage.French) ?? string.Empty;

            Result sent;

            if (channel == DeliveryChannel.Email)
            {
                if (report.RenderedPdf is not { Length: > 0 } pdf)
                    return Result.Failure<DeliveryOutcome>(Error.Conflict("Report_NoStoredPdf"));

                sent = await messages.SendEmailAsync(new EmailMessage(
                    recipientIdentifier,
                    $"Compte rendu d'analyses {order.AccessionNumber} — {laboratory}",
                    EmailBody(recipientName, order.AccessionNumber, report.VersionNumber, laboratory, site?.Phone),
                    [new EmailAttachment($"CR-{order.AccessionNumber}-v{report.VersionNumber}.pdf", pdf,
                                         "application/pdf")]), ct);
            }
            else
            {
                sent = await messages.SendSmsAsync(recipientIdentifier,
                    SmsText(order.AccessionNumber, laboratory, site?.Phone), ct);
            }

            if (sent.IsSuccess) outcome = DeliveryResultKind.Sent;
            else
            {
                outcome = DeliveryResultKind.Failed;
                failureError = sent.FirstError;
                failure = string.Join(" ", new[] { sent.FirstError.ResourceKey }
                                           .Concat(sent.FirstError.Args.Select(a => a?.ToString() ?? string.Empty)));
            }
        }

        // A counter hand-over is complete the moment it happens; an electronic send is only "sent"
        // until the channel confirms it.
        // An e-mail the server accepted carries the report itself, so it counts as delivered; an
        // SMS only says the report is ready, and the report still has to be collected.
        if (outcome == DeliveryResultKind.Failed) delivery.MarkFailed(clock.Now, failure!);
        else if (channel == DeliveryChannel.PrintedAtCounter
                 || outcome == DeliveryResultKind.Sent && channel == DeliveryChannel.Email)
            delivery.MarkDelivered(clock.Now);
        else delivery.MarkSent(clock.Now);

        report.RecordDelivery(delivery);

        unitOfWork.AuditReason = outcome == DeliveryResultKind.Failed
            ? $"Échec d'envoi du compte rendu v{report.VersionNumber} ({channel}) : {failure}"
            : $"Remise du compte rendu v{report.VersionNumber} ({channel})";
        await unitOfWork.SaveChangesAsync(ct);

        if (outcome != DeliveryResultKind.Failed)
            await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.Transmit, null, report.LabOrderId,
                                         $"Delivery/{channel}", ct: ct);

        return Result.Success(new DeliveryOutcome(outcome, failureError));
    }

    /// <summary>Whether e-mail and SMS are set up to really send, for the delivery form.</summary>
    public (bool Email, bool Sms) ElectronicChannels => (messages.CanSendEmail, messages.CanSendSms);

    private static string EmailBody(string? recipient, string accession, int version, string laboratory,
                                    string? phone) =>
        $"""
        Bonjour{(string.IsNullOrWhiteSpace(recipient) ? string.Empty : " " + recipient)},

        Veuillez trouver ci-joint le compte rendu d'analyses du dossier {accession} (version {version}).

        Ce document contient des données de santé confidentielles. Si vous n'en êtes pas le
        destinataire, merci de le supprimer et de nous en informer.

        {laboratory}{(string.IsNullOrWhiteSpace(phone) ? string.Empty : "\nTél. " + phone)}
        """;

    /// <summary>No result in an SMS: it says the report is ready, nothing about what it contains.</summary>
    private static string SmsText(string accession, string laboratory, string? phone) =>
        $"{laboratory} : votre compte rendu d'analyses (dossier {accession}) est disponible."
        + (string.IsNullOrWhiteSpace(phone) ? string.Empty : $" Tél. {phone}");

    /// <summary>
    /// Returns the stored PDF of a released version. Reprinting a validated report is a logged
    /// disclosure, not a neutral read.
    /// </summary>
    public async Task<Result<byte[]>> GetStoredPdfAsync(Guid reportId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Reports.ReprintValidated);
        if (permitted.IsFailure) return Result.Failure<byte[]>(permitted.FirstError);

        var report = await reports.GetByIdAsync(reportId, ct);
        if (report?.RenderedPdf is null)
            return Result.Failure<byte[]>(Error.NotFound("Report_NotFound"));

        // Detects silent corruption of the archive — and proves the PDF is the one issued.
        if (report.ContentHash is { } expected)
        {
            var actual = Convert.ToHexString(SHA256.HashData(report.RenderedPdf));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<byte[]>(Error.Conflict("Report_ArchiveIntegrityFailure"));
        }

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.ViewReport, null,
                                     report.LabOrderId, "ReportReprint", ct: ct);

        return Result.Success(report.RenderedPdf);
    }

    // ---- Model assembly -------------------------------------------------------------------------

    private async Task<Result<AppUser>> ResolveSignatoryAsync(LabOrder order, CancellationToken ct)
    {
        // The signatory is the biologist who validated the work, not whoever is at the keyboard.
        // Where several validated parts of the file, the one who released the most recent part
        // signs — they are the one who last took responsibility for the whole.
        var validatorId = order.Items
            .Where(i => !i.IsCancelled && i.ValidatedByUserId is not null)
            .OrderByDescending(i => i.ValidatedAt)
            .Select(i => i.ValidatedByUserId)
            .FirstOrDefault();

        if (validatorId is null)
            return Result.Failure<AppUser>(Error.Regulatory("Report_NoValidatedResults"));

        var biologist = await users.GetByIdAsync(validatorId.Value, ct);
        if (biologist is null)
            return Result.Failure<AppUser>(Error.NotFound("Report_SignatoryRequired"));

        if (string.IsNullOrWhiteSpace(biologist.ProfessionalOrderNumber))
            return Result.Failure<AppUser>(Error.Regulatory("Report_SignatoryOrderNumberRequired"));

        return Result.Success(biologist);
    }

    private async Task<ReportModel> BuildModelAsync(
        LabOrder order, Patient patient, LabSite site, AppUser biologist,
        IReadOnlyList<TestResult> releasable, AppLanguage language, int versionNumber,
        ReportCompleteness completeness, ReportAmendmentKind amendmentKind,
        MultilingualText? amendmentReason, MultilingualText? conclusion, CancellationToken ct)
    {
        var catalogue = new Dictionary<Guid, (AnalysisTest Test, TestParameter Parameter)>();

        foreach (var analysisId in order.Items.Select(i => i.AnalysisTestId).Distinct())
        {
            var test = await analyses.GetByIdWithDetailsAsync(analysisId, ct);
            if (test is null) continue;
            foreach (var parameter in test.Parameters) catalogue[parameter.Id] = (test, parameter);
        }

        // Partner laboratories named on the report, as ISO 15189 requires for subcontracted work.
        var partnerNames = new Dictionary<Guid, string>();

        foreach (var partnerId in order.Items.Where(i => i.IsSubcontracted)
                                             .Select(i => i.SubcontractorSiteId).OfType<Guid>().Distinct())
        {
            if (await sites.GetByIdAsync(partnerId, ct) is { } partner)
                partnerNames[partnerId] = partner.Name.For(language);
        }

        var reservations = new List<string>();
        DateTimeOffset? earliestCollection = null;
        DateTimeOffset? earliestReception = null;

        foreach (var sampleId in order.Items.Select(i => i.SampleId)
                                            .Where(id => id is not null)
                                            .Select(id => id!.Value).Distinct())
        {
            var sample = await samples.GetByIdAsync(sampleId, ct);
            if (sample is null) continue;

            if (sample.CollectedAt is { } collected &&
                (earliestCollection is null || collected < earliestCollection))
                earliestCollection = collected;

            if (sample.ReceivedAt is { } received &&
                (earliestReception is null || received < earliestReception))
                earliestReception = received;

            // A defect the laboratory accepted is disclosed, so the clinician can weigh it.
            if (sample.IsAcceptedWithReservation && sample.HasDefects)
                reservations.Add($"{sample.Barcode} : {sample.Defects}");
        }

        // Group by discipline, then by analysis, preserving the catalogue's display order so the
        // report reads the same way every time.
        var sections = releasable
            .Select(result =>
            {
                var found = catalogue.TryGetValue(result.TestParameterId, out var entry);
                return (Result: result, Found: found, entry.Test, entry.Parameter);
            })
            .Where(x => x.Found)
            .GroupBy(x => x.Test.Department?.Code ?? "AUTRE")
            .Select(departmentGroup =>
            {
                var first = departmentGroup.First();

                var blocks = departmentGroup
                    .GroupBy(x => x.Test)
                    .OrderBy(g => g.Key.DisplayOrder)
                    .ThenBy(g => g.Key.Code, StringComparer.Ordinal)
                    .Select(analysisGroup =>
                    {
                        var item = order.Items.FirstOrDefault(
                            i => i.AnalysisTestId == analysisGroup.Key.Id);

                        var lines = analysisGroup
                            .OrderBy(x => x.Parameter.DisplayOrder)
                            .Where(x => !x.Parameter.IsInternalOnly)
                            .Select(x => BuildLine(x.Result, x.Parameter, language))
                            .ToList();

                        // Section headings of a panel are printed even though they carry no value.
                        foreach (var heading in analysisGroup.Key.Parameters
                                     .Where(p => p.ValueType == ResultValueType.Heading))
                        {
                            lines.Insert(
                                Math.Min(lines.Count, Math.Max(0, heading.DisplayOrder - 1)),
                                new ReportLine
                                {
                                    ParameterCode = heading.Code,
                                    ParameterName = heading.Name,
                                    IsHeading = true,
                                    IndentLevel = heading.IndentLevel,
                                    Value = string.Empty
                                });
                        }

                        return new ReportAnalysisBlock(
                            analysisGroup.Key.Code,
                            analysisGroup.Key.Name,
                            analysisGroup.Key.Method,
                            analysisGroup.Key.IsAccredited,
                            item?.IsSubcontracted ?? analysisGroup.Key.IsSubcontracted,
                            item?.ValidatedAt,
                            lines,
                            item is { IsSubcontracted: true, SubcontractorSiteId: { } partnerId }
                                ? partnerNames.GetValueOrDefault(partnerId)
                                : null);
                    })
                    .ToList();

                return new ReportSection(
                    departmentGroup.Key,
                    first.Test.Department?.Name ?? new MultilingualText(departmentGroup.Key),
                    first.Test.Department?.DisplayOrder ?? 999,
                    blocks);
            })
            .OrderBy(s => s.DisplayOrder)
            .ToList();

        var allLines = sections.SelectMany(s => s.Analyses).SelectMany(a => a.Lines).ToList();

        return new ReportModel
        {
            LabOrderId = order.Id,
            AccessionNumber = order.AccessionNumber,
            Language = language,
            VersionNumber = versionNumber,
            Completeness = completeness,
            AmendmentKind = amendmentKind,
            AmendmentReason = amendmentReason,

            LaboratoryName = site.Name.For(language),
            LaboratoryLegalName = site.LegalName,
            LaboratoryAddress = site.AddressLine.For(language),
            LaboratoryCity = site.City,
            LaboratoryPhone = site.Phone,
            LaboratoryEmail = site.Email,
            MinistryAuthorizationNumber = site.HealthMinistryAuthorizationNumber,
            AccreditationNumber = site.AccreditationNumber,
            IsAccredited = site.IsAccredited,
            LogoPng = site.LogoPng,

            PatientName = patient.FullName,
            PatientNameArabic = patient.FullNameArabic,
            MedicalRecordNumber = patient.MedicalRecordNumber,
            Gender = patient.Gender,
            DateOfBirth = patient.DateOfBirth,
            AgeYears = patient.AgeInYears(clock.Today),
            Cin = patient.Cin,
            IsPregnant = patient.IsPregnant,

            PrescriberName = order.Prescriber?.DisplayName(language) ?? order.PrescriberFreeText,
            PrescriberSpeciality = order.Prescriber?.Speciality?.For(language),
            PrescriptionDate = order.PrescriptionDate,
            RegisteredAt = order.RegisteredAt,
            CollectedAt = earliestCollection,
            ReceivedAt = earliestReception,
            ReleasedAt = clock.Now,
            ClinicalInformation = order.ClinicalInformation,
            IsUrgent = order.IsUrgent,

            SignatoryName = biologist.FullName,
            SignatoryNameArabic = biologist.FullNameArabic,
            SignatoryQualification = biologist.Qualification.ToString(),
            SignatoryOrderNumber = biologist.ProfessionalOrderNumber ?? string.Empty,
            SignaturePng = biologist.SignaturePng,

            Sections = sections,
            Conclusion = conclusion,
            Reservations = reservations,

            ContainsSubcontractedResults = sections.SelectMany(s => s.Analyses).Any(a => a.IsSubcontracted),
            ContainsReservations = reservations.Count > 0,
            ContainsCriticalResults = allLines.Any(l => l.IsCritical),
            AllResultsAccredited = sections.SelectMany(s => s.Analyses).All(a => a.IsAccredited)
        };
    }

    private static ReportLine BuildLine(TestResult result, TestParameter parameter, AppLanguage language)
    {
        var codedLabel = result.CodedValue is null
            ? null
            : parameter.CodedOptions
                .FirstOrDefault(o => string.Equals(o.Code, result.CodedValue,
                                                   StringComparison.OrdinalIgnoreCase))
                ?.Label.For(language);

        return new ReportLine
        {
            ParameterCode = parameter.Code,
            ParameterName = parameter.Name,
            IndentLevel = parameter.IndentLevel,
            // Coded answers print their label, not their code: a patient reads "Négatif", not "NEG".
            Value = codedLabel ?? result.FormatValue(language),
            Unit = result.AppliedUnitSymbol ?? parameter.Unit?.DisplaySymbol,
            ReferenceRange = result.AppliedReferenceRangeText,
            // The requested report language, matching Value above — the previous result sits in the
            // column beside the current one and the two must share a decimal separator.
            PreviousValue = result.PreviousNumericValue?.ToString(
                "N" + Math.Clamp(result.AppliedDecimals, 0, 6), language.ToCulture()),
            PreviousDate = result.PreviousResultDate,
            Interpretation = result.Interpretation,
            Flags = result.Flags,
            Comment = result.PrintTechnicalComment ? result.TechnicalComment : null,
            BiologistComment = result.BiologistComment
        };
    }
}

/// <summary>Read-side queries for report versioning.</summary>
public interface IReportQueries
{
    /// <summary>The most recent version released for an order, or null when none has been.</summary>
    Task<LabReport?> GetLatestVersionAsync(Guid orderId, CancellationToken ct = default);

    Task<IReadOnlyList<LabReport>> GetVersionsAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>Resolves a download token to its report, honouring the expiry.</summary>
    Task<LabReport?> GetByAccessTokenAsync(string token, DateTimeOffset now,
                                            CancellationToken ct = default);

    /// <summary>
    /// The register: every version released on a site, newest first, with the patient and the
    /// delivery state. This is the list a laboratory is asked to produce when somebody telephones
    /// about a report — "what did we send, to whom, and when?".
    /// </summary>
    Task<IReadOnlyList<ReportRegisterRow>> GetRegisterAsync(Guid siteId, string? search,
                                                             bool onlyUndelivered, DateOnly from,
                                                             CancellationToken ct = default);

    /// <summary>
    /// Orders on a site carrying validated results that no report includes yet, urgent first.
    /// </summary>
    Task<IReadOnlyList<PendingReportRow>> GetPendingReleaseAsync(Guid siteId, string? search,
                                                                 CancellationToken ct = default);

    /// <summary>The deliveries recorded against one report.</summary>
    Task<IReadOnlyList<ReportDeliveryRow>> GetDeliveriesAsync(Guid reportId,
                                                               CancellationToken ct = default);
}

/// <summary>An order with validated results waiting for a report.</summary>
public sealed record PendingReportRow(
    Guid LabOrderId,
    string AccessionNumber,
    string PatientName,
    string? PatientNameArabic,
    OrderPriority Priority,
    /// <summary>Validated results not yet on a report.</summary>
    int ValidatedResults,
    /// <summary>Analyses still short of biological validation; zero means the report is final.</summary>
    int AwaitingAnalyses,
    DateTimeOffset? LastValidatedAt,
    /// <summary>Versions already released for the order; above zero, this one completes a partial.</summary>
    int ReleasedVersions);

public enum DeliveryResultKind
{
    /// <summary>Recorded only: handed over, or sent by other means.</summary>
    Recorded = 0,

    /// <summary>Sent by the application's e-mail or SMS account.</summary>
    Sent = 1,

    /// <summary>The send was attempted and failed; the attempt is on record.</summary>
    Failed = 2
}

/// <summary>What became of a delivery; <see cref="Failure"/> says why a send did not go.</summary>
public sealed record DeliveryOutcome(DeliveryResultKind Kind, Error? Failure = null);

/// <summary>One released version in the register.</summary>
public sealed record ReportRegisterRow(
    Guid ReportId,
    Guid LabOrderId,
    Guid PatientId,
    string AccessionNumber,
    string PatientName,
    string? PatientNameArabic,
    string? PatientPhone,
    int VersionNumber,
    ReportCompleteness Completeness,
    ReportAmendmentKind AmendmentKind,
    MultilingualText? AmendmentReason,
    AppLanguage Language,
    DateTimeOffset ReleasedAt,
    string SignatoryName,
    bool ContainsCriticalResults,
    bool ContainsReservations,
    bool ContainsSubcontractedResults,
    bool AllResultsAccredited,
    bool HasStoredPdf,
    int PageCount,
    Guid? SupersededByReportId,
    int DeliveryCount,
    DateTimeOffset? LastDeliveredAt,
    string? PrescriberName,
    /// <summary>How the prescriber asked for it — which channel the operator should reach for.</summary>
    ReportDeliveryPreference PrescriberPreference,
    string? PatientEmail = null,
    string? PrescriberEmail = null)
{
    public bool IsSuperseded => SupersededByReportId is not null;

    public bool IsAmendment => AmendmentKind != ReportAmendmentKind.None;

    public bool WasDelivered => LastDeliveredAt is not null;

    /// <summary>
    /// A current final report that has never been handed over. The laboratory has finished the
    /// work and the patient does not have it — which is the whole point of keeping a register.
    /// </summary>
    public bool AwaitingDelivery =>
        !IsSuperseded && !WasDelivered && Completeness == ReportCompleteness.Final;

    /// <summary>
    /// Undelivered and carrying a critical result. ISO 15189 §5.9 obliges the laboratory to make
    /// sure a panic value reaches a clinician, so this is the row that has to be acted on today.
    /// </summary>
    public bool IsUrgentToDeliver => AwaitingDelivery && ContainsCriticalResults;
}

/// <summary>One recorded hand-over or transmission of a report.</summary>
public sealed record ReportDeliveryRow(
    Guid DeliveryId,
    DeliveryChannel Channel,
    ReportRecipientKind RecipientKind,
    DeliveryStatus Status,
    string? RecipientName,
    string? RecipientIdentifier,
    string? BearerName,
    string? BearerCin,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    int AttemptCount,
    string? FailureReason,
    string? HandedOverByName,
    bool HasAuthorizingConsent);
