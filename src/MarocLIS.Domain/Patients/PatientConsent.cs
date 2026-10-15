using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Patients;

/// <summary>
/// A recorded consent. Loi n° 08-09 requires the patient's prior consent for the processing of
/// health data, and specific written consent for genetic testing and for HIV serology; the CNDP
/// expects the laboratory to be able to produce the evidence. Consents are append-only: a
/// withdrawal is a new state on the same row, never a deletion.
/// </summary>
public sealed class PatientConsent : Entity, IAuditable
{
    private PatientConsent() { }

    public PatientConsent(Guid patientId, ConsentPurpose purpose, ConsentChannel channel,
                          DateTimeOffset givenAt, Guid collectedByUserId)
    {
        PatientId = patientId;
        Purpose = purpose;
        Channel = channel;
        GivenAt = givenAt;
        CollectedByUserId = collectedByUserId;
    }

    public Guid PatientId { get; private set; }
    public ConsentPurpose Purpose { get; private set; }
    public ConsentChannel Channel { get; private set; }

    public DateTimeOffset GivenAt { get; private set; }

    /// <summary>The operator who collected it — part of the evidence chain.</summary>
    public Guid CollectedByUserId { get; private set; }

    /// <summary>
    /// Who actually consented. For a minor or a protected adult this is the guardian, and the
    /// relationship is recorded so the file shows on whose authority the analysis was run.
    /// </summary>
    public string? SignedByName { get; set; }
    public string? SignedByCin { get; set; }
    public string? SignedByRelationship { get; set; }

    /// <summary>Language the consent form was presented in — it must be one the patient understands.</summary>
    public AppLanguage PresentedInLanguage { get; set; } = AppLanguage.French;

    /// <summary>Version of the consent text presented, so the exact wording can be reproduced.</summary>
    public string? FormVersion { get; set; }

    /// <summary>Scan of the signed paper form, or a captured signature bitmap.</summary>
    public byte[]? EvidenceDocument { get; set; }
    public string? EvidenceDocumentContentType { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public DateTimeOffset? WithdrawnAt { get; private set; }
    public Guid? WithdrawnByUserId { get; private set; }
    public string? WithdrawalReason { get; private set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public bool IsEffectiveOn(DateOnly date) =>
        WithdrawnAt is null && (ExpiresOn is null || ExpiresOn >= date);

    public void Withdraw(DateTimeOffset now, Guid byUserId, string? reason)
    {
        if (WithdrawnAt is not null) return;
        WithdrawnAt = now;
        WithdrawnByUserId = byUserId;
        WithdrawalReason = reason;
    }

    /// <summary>Purposes that the law or professional practice requires to be in writing.</summary>
    public static bool RequiresWrittenEvidence(ConsentPurpose purpose) => purpose is
        ConsentPurpose.GeneticTesting or
        ConsentPurpose.HivSerology or
        ConsentPurpose.ResearchUse or
        ConsentPurpose.SampleBiobanking or
        ConsentPurpose.PrenatalScreening;
}

public enum ConsentPurpose
{
    /// <summary>General consent to the analyses prescribed and to the processing of the file.</summary>
    Diagnostic = 0,

    /// <summary>Genetic analysis — written, specific and informed consent is mandatory.</summary>
    GeneticTesting = 1,

    /// <summary>HIV serology — specific consent and counselling obligations apply.</summary>
    HivSerology = 2,

    /// <summary>Prenatal screening.</summary>
    PrenatalScreening = 3,

    /// <summary>Retention of the residual sample beyond the analysis.</summary>
    SampleBiobanking = 4,

    /// <summary>Use of anonymised data for research or publication.</summary>
    ResearchUse = 5,

    /// <summary>Delivery of the report by email or SMS link rather than in person.</summary>
    ElectronicDelivery = 6,

    /// <summary>Disclosure of results to a named third party (employer, family member).</summary>
    ThirdPartyDisclosure = 7,

    /// <summary>Subcontracting of the analysis to a partner laboratory.</summary>
    Subcontracting = 8,

    /// <summary>
    /// Follow-up SMS from the laboratory: reminders that a monitoring analysis is due, and the
    /// short satisfaction survey. Never advertising; withdrawing it stops both.
    /// </summary>
    FollowUpMessages = 9
}

public enum ConsentChannel
{
    /// <summary>Signed paper form, scanned into the file.</summary>
    WrittenForm = 0,

    /// <summary>Signature captured on a pad or touchscreen.</summary>
    ElectronicSignature = 1,

    /// <summary>Verbal consent recorded by the operator — acceptable only for the diagnostic purpose.</summary>
    Verbal = 2,

    /// <summary>Consent given through a patient portal.</summary>
    OnlinePortal = 3
}
