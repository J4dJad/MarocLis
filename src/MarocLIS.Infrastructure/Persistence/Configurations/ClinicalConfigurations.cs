using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

public sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> builder)
    {
        builder.ToTable("Patients", "pat");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.MedicalRecordNumber).AsCode(30);
        builder.HasIndex(e => e.MedicalRecordNumber).IsUnique()
               .HasDatabaseName("UX_Patients_RecordNumber");

        builder.Property(e => e.LastName).AsSearchable(120);
        builder.Property(e => e.FirstName).AsSearchable(120);
        builder.Property(e => e.LastNameArabic).AsSearchable(120);
        builder.Property(e => e.FirstNameArabic).AsSearchable(120);
        builder.Property(e => e.SearchKey).AsSearchable(260);

        builder.Property(e => e.Cin).HasMaxLength(20);
        builder.Property(e => e.PassportNumber).HasMaxLength(30);
        builder.Property(e => e.NationalityCode).HasMaxLength(2).IsRequired();
        builder.Property(e => e.GuardianCin).HasMaxLength(20);
        builder.Property(e => e.GuardianFullName).AsSearchable(200);
        builder.Property(e => e.GuardianRelationship).HasMaxLength(60);
        builder.Property(e => e.PlaceOfBirth).HasMaxLength(120);
        builder.Property(e => e.AddressLine).HasMaxLength(400);
        builder.Property(e => e.City).AsSearchable(100);
        builder.Property(e => e.PostalCode).HasMaxLength(10);
        builder.Property(e => e.MobilePhone).HasMaxLength(20);
        builder.Property(e => e.AlternatePhone).HasMaxLength(20);
        builder.Property(e => e.Email).HasMaxLength(200);
        builder.Property(e => e.CurrentTreatment).HasMaxLength(1000);
        builder.Property(e => e.KnownAllergies).HasMaxLength(500);
        builder.Property(e => e.WeightKg).HasColumnType("decimal(6,2)");
        builder.Property(e => e.HeightCm).HasColumnType("decimal(6,2)");

        // The reception desk searches by name fragment far more than by anything else; this index
        // is what keeps that instant once the file count passes six figures.
        builder.HasIndex(e => new { e.SiteId, e.SearchKey }).HasDatabaseName("IX_Patients_Search");

        // The CIN is the identity proof, so a duplicate almost always means a duplicate file. Not
        // enforced as unique: minors and foreign patients legitimately have none, and two siblings
        // on one guardian's card do occur in practice.
        builder.HasIndex(e => e.Cin)
               .HasFilter("[Cin] IS NOT NULL")
               .HasDatabaseName("IX_Patients_Cin");

        builder.HasIndex(e => e.MobilePhone)
               .HasFilter("[MobilePhone] IS NOT NULL")
               .HasDatabaseName("IX_Patients_Mobile");

        // Drives the retention purge; filtered so the index stays small.
        builder.HasIndex(e => e.RetentionUntil)
               .HasFilter("[RetentionUntil] IS NOT NULL AND [IsAnonymized] = 0")
               .HasDatabaseName("IX_Patients_Retention");

        builder.HasMany(e => e.Coverages).WithOne()
               .HasForeignKey(c => c.PatientId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Consents).WithOne()
               .HasForeignKey(c => c.PatientId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Alerts).WithOne()
               .HasForeignKey(a => a.PatientId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PatientCoverageConfiguration : IEntityTypeConfiguration<PatientCoverage>
{
    public void Configure(EntityTypeBuilder<PatientCoverage> builder)
    {
        builder.ToTable("PatientCoverages", "pat");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.MembershipNumber).HasMaxLength(40).IsRequired();
        builder.Property(e => e.InsuredPersonName).AsSearchable(200);
        builder.Property(e => e.InsuredPersonCin).HasMaxLength(20);
        builder.Property(e => e.EmployerName).HasMaxLength(200);
        builder.Property(e => e.PriorAgreementNumber).HasMaxLength(60);
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Property(e => e.ReimbursementRateOverride).HasColumnType("decimal(6,4)");

        builder.HasOne(e => e.Payer).WithMany().HasForeignKey(e => e.PayerId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.PayerId, e.MembershipNumber })
               .HasDatabaseName("IX_PatientCoverages_Payer_Member");
    }
}

public sealed class PatientConsentConfiguration : IEntityTypeConfiguration<PatientConsent>
{
    public void Configure(EntityTypeBuilder<PatientConsent> builder)
    {
        builder.ToTable("PatientConsents", "pat");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SignedByName).HasMaxLength(200);
        builder.Property(e => e.SignedByCin).HasMaxLength(20);
        builder.Property(e => e.SignedByRelationship).HasMaxLength(60);
        builder.Property(e => e.FormVersion).HasMaxLength(40);
        builder.Property(e => e.EvidenceDocument).HasColumnType("varbinary(max)");
        builder.Property(e => e.EvidenceDocumentContentType).HasMaxLength(100);
        builder.Property(e => e.WithdrawalReason).HasMaxLength(500);
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.HasIndex(e => new { e.PatientId, e.Purpose, e.GivenAt })
               .HasDatabaseName("IX_PatientConsents_Patient_Purpose");
    }
}

public sealed class PatientAlertConfiguration : IEntityTypeConfiguration<PatientAlert>
{
    public void Configure(EntityTypeBuilder<PatientAlert> builder)
    {
        builder.ToTable("PatientAlerts", "pat");
        builder.HasKey(e => e.Id);
        builder.MapMultilingual(e => e.Message, "Message", maxLength: 500);
    }
}

public sealed class PayerConfiguration : IEntityTypeConfiguration<Payer>
{
    public void Configure(EntityTypeBuilder<Payer> builder)
    {
        builder.ToTable("Payers", "pat");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(30);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_Payers_Code");
        builder.MapMultilingual(e => e.Name, "Name");
        builder.Property(e => e.DefaultReimbursementRate).HasColumnType("decimal(6,4)");
        builder.Property(e => e.AgreementNumber).HasMaxLength(60);
        builder.Property(e => e.Ice).HasMaxLength(15);
        builder.Property(e => e.ContactName).HasMaxLength(200);
        builder.Property(e => e.Phone).HasMaxLength(30);
        builder.Property(e => e.Email).HasMaxLength(200);
        builder.Property(e => e.AddressLine).HasMaxLength(400);
        builder.Property(e => e.City).HasMaxLength(100);
        builder.Property(e => e.AccountingAccount).HasMaxLength(20);
    }
}

public sealed class PrescriberConfiguration : IEntityTypeConfiguration<Prescriber>
{
    public void Configure(EntityTypeBuilder<Prescriber> builder)
    {
        builder.ToTable("Prescribers", "ord");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.LastName).AsSearchable(120);
        builder.Property(e => e.FirstName).AsSearchable(120);
        builder.Property(e => e.FullNameArabic).AsSearchable(200);
        builder.Property(e => e.SearchKey).AsSearchable(260);
        builder.MapMultilingual(e => e.Speciality, "Speciality", frenchRequired: false, maxLength: 150);

        builder.Property(e => e.ProfessionalOrderNumber).HasMaxLength(40);
        builder.Property(e => e.Inpe).HasMaxLength(20);
        builder.Property(e => e.Ice).HasMaxLength(15);
        builder.Property(e => e.Phone).HasMaxLength(30);
        builder.Property(e => e.MobilePhone).HasMaxLength(20);
        builder.Property(e => e.CriticalResultPhone).HasMaxLength(20);
        builder.Property(e => e.Email).HasMaxLength(200);
        builder.Property(e => e.Fax).HasMaxLength(30);
        builder.Property(e => e.AddressLine).HasMaxLength(400);
        builder.Property(e => e.City).AsSearchable(100);
        builder.Property(e => e.InstitutionName).AsSearchable(200);
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Property(e => e.DiscountPercent).HasColumnType("decimal(6,3)");

        builder.HasIndex(e => e.SearchKey).HasDatabaseName("IX_Prescribers_Search");
        builder.HasIndex(e => e.Inpe).HasFilter("[Inpe] IS NOT NULL")
               .HasDatabaseName("IX_Prescribers_Inpe");
    }
}

public sealed class LabOrderConfiguration : IEntityTypeConfiguration<LabOrder>
{
    public void Configure(EntityTypeBuilder<LabOrder> builder)
    {
        builder.ToTable("LabOrders", "ord");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.AccessionNumber).AsCode(30);
        builder.HasIndex(e => e.AccessionNumber).IsUnique()
               .HasDatabaseName("UX_LabOrders_AccessionNumber");

        builder.Property(e => e.PrescriberFreeText).AsSearchable(200);
        builder.Property(e => e.WardOrUnit).HasMaxLength(120);
        builder.Property(e => e.RoomOrBed).HasMaxLength(60);
        builder.Property(e => e.ClinicalInformation).HasMaxLength(2000);
        builder.Property(e => e.DiagnosisCode).HasMaxLength(20);
        builder.Property(e => e.CurrentTreatment).HasMaxLength(1000);
        builder.Property(e => e.InternalNote).HasMaxLength(1000);
        builder.Property(e => e.DiscountReason).HasMaxLength(300);
        builder.Property(e => e.PriorAgreementNumber).HasMaxLength(60);
        builder.Property(e => e.PrescriptionScan).HasColumnType("varbinary(max)");
        builder.Property(e => e.PrescriptionScanContentType).HasMaxLength(100);
        builder.Property(e => e.DiscountPercent).HasColumnType("decimal(6,3)");
        builder.Property(e => e.ReimbursementRateAtRegistration).HasColumnType("decimal(6,4)");
        builder.Property(e => e.DirectBillingAtRegistration);

        builder.HasOne(e => e.Patient).WithMany().HasForeignKey(e => e.PatientId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Prescriber).WithMany().HasForeignKey(e => e.PrescriberId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Items).WithOne(i => i.LabOrder!)
               .HasForeignKey(i => i.LabOrderId).OnDelete(DeleteBehavior.Cascade);

        // The worklist is the busiest query in the application: "everything on this site, in this
        // state, ordered by priority then age". This composite index serves it directly.
        builder.HasIndex(e => new { e.SiteId, e.Status, e.Priority, e.RegisteredAt })
               .HasDatabaseName("IX_LabOrders_Worklist");

        builder.HasIndex(e => new { e.PatientId, e.RegisteredAt })
               .HasDatabaseName("IX_LabOrders_Patient_History");

        builder.HasIndex(e => e.DueAt)
               .HasFilter("[DueAt] IS NOT NULL AND [Status] < 7")
               .HasDatabaseName("IX_LabOrders_Overdue");
    }
}

public sealed class LabOrderItemConfiguration : IEntityTypeConfiguration<LabOrderItem>
{
    public void Configure(EntityTypeBuilder<LabOrderItem> builder)
    {
        builder.ToTable("LabOrderItems", "ord");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.BilledLetterKey).HasMaxLength(5);
        builder.Property(e => e.BilledActCode).HasMaxLength(30);
        builder.Property(e => e.SubcontractorReference).HasMaxLength(60);
        builder.Property(e => e.SubcontractDispatchNumber).HasMaxLength(30);
        builder.Property(e => e.CancellationReason).HasMaxLength(500);
        builder.Property(e => e.RepeatReason).HasMaxLength(500);

        builder.Property(e => e.ChargedPrice).HasColumnType("decimal(18,2)");
        builder.Property(e => e.ReferenceTariffBase).HasColumnType("decimal(18,2)");
        builder.Property(e => e.PayerShare).HasColumnType("decimal(18,2)");
        builder.Property(e => e.PatientShare).HasColumnType("decimal(18,2)");
        builder.Property(e => e.BilledCoefficient).HasColumnType("decimal(18,4)");

        builder.HasOne(e => e.AnalysisTest).WithMany().HasForeignKey(e => e.AnalysisTestId)
               .OnDelete(DeleteBehavior.Restrict);

        // The technical worklist: one department's pending work across all open orders.
        builder.HasIndex(e => new { e.DepartmentId, e.Status, e.Priority })
               .HasDatabaseName("IX_LabOrderItems_DepartmentWorklist");

        builder.HasIndex(e => e.SampleId)
               .HasFilter("[SampleId] IS NOT NULL")
               .HasDatabaseName("IX_LabOrderItems_Sample");

        builder.HasIndex(e => new { e.LabOrderId, e.AnalysisTestId })
               .HasDatabaseName("IX_LabOrderItems_Order_Analysis");
    }
}

public sealed class SampleConfiguration : IEntityTypeConfiguration<Sample>
{
    public void Configure(EntityTypeBuilder<Sample> builder)
    {
        builder.ToTable("Samples", "smp");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Barcode).AsCode(40);

        // The single most important constraint in the schema. A reused barcode attaches one
        // patient's tube to another patient's order; the database refuses it outright rather than
        // trusting every code path that might generate one.
        builder.HasIndex(e => e.Barcode).IsUnique().HasDatabaseName("UX_Samples_Barcode");

        builder.Property(e => e.CollectionSite).HasMaxLength(120);
        builder.Property(e => e.CollectionNote).HasMaxLength(500);
        builder.Property(e => e.RejectionComment).HasMaxLength(500);
        builder.Property(e => e.StorageLocation).HasMaxLength(100);
        builder.Property(e => e.StoragePosition).HasMaxLength(40);
        builder.Property(e => e.VolumeMl).HasColumnType("decimal(8,2)");
        builder.Property(e => e.ReceptionTemperatureCelsius).HasColumnType("decimal(5,2)");

        builder.HasMany(e => e.Events).WithOne()
               .HasForeignKey(v => v.SampleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Aliquots).WithOne()
               .HasForeignKey(a => a.ParentSampleId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.LabOrderId, e.SequenceInOrder })
               .HasDatabaseName("IX_Samples_Order");

        builder.HasIndex(e => new { e.SiteId, e.Status, e.CollectedAt })
               .HasDatabaseName("IX_Samples_Status");

        // Drives the serum-bank purge list.
        builder.HasIndex(e => e.KeepUntil)
               .HasFilter("[KeepUntil] IS NOT NULL AND [Status] <> 9")
               .HasDatabaseName("IX_Samples_KeepUntil");
    }
}

/// <summary>
/// The site's list of storage places. The name is unique per site, active or not: a place taken out
/// of use and then created again under the same name would be two entries of one fridge.
/// </summary>
public sealed class SampleStorageLocationConfiguration : IEntityTypeConfiguration<SampleStorageLocation>
{
    public void Configure(EntityTypeBuilder<SampleStorageLocation> builder)
    {
        builder.ToTable("StorageLocations", "smp");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(SampleStorageLocation.NameMaxLength).IsRequired();
        builder.HasIndex(e => new { e.SiteId, e.Name }).IsUnique()
               .HasDatabaseName("UX_StorageLocations_Site_Name");
    }
}

public sealed class SampleEventConfiguration : IEntityTypeConfiguration<SampleEvent>
{
    public void Configure(EntityTypeBuilder<SampleEvent> builder)
    {
        builder.ToTable("SampleEvents", "smp");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Detail).HasMaxLength(500);
        builder.Property(e => e.Workstation).HasMaxLength(100);
        builder.Property(e => e.TemperatureCelsius).HasColumnType("decimal(5,2)");
        builder.HasIndex(e => new { e.SampleId, e.OccurredAt }).HasDatabaseName("IX_SampleEvents_Sample");
    }
}

public sealed class SampleAliquotConfiguration : IEntityTypeConfiguration<SampleAliquot>
{
    public void Configure(EntityTypeBuilder<SampleAliquot> builder)
    {
        builder.ToTable("SampleAliquots", "smp");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Barcode).AsCode(40);
        builder.Property(e => e.StorageLocation).HasMaxLength(100);
        builder.Property(e => e.Purpose).HasMaxLength(200);
        builder.Property(e => e.VolumeMl).HasColumnType("decimal(8,2)");
        builder.HasIndex(e => e.Barcode).IsUnique().HasDatabaseName("UX_SampleAliquots_Barcode");
    }
}

public sealed class TestResultConfiguration : IEntityTypeConfiguration<TestResult>
{
    public void Configure(EntityTypeBuilder<TestResult> builder)
    {
        builder.ToTable("TestResults", "res");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.NumericValue).HasColumnType("decimal(18,6)");
        builder.Property(e => e.PreviousNumericValue).HasColumnType("decimal(18,6)");
        builder.Property(e => e.AppliedLowerLimit).HasColumnType("decimal(18,6)");
        builder.Property(e => e.AppliedUpperLimit).HasColumnType("decimal(18,6)");
        builder.Property(e => e.DilutionFactor).HasColumnType("decimal(12,4)");

        builder.Property(e => e.ValueOperator).HasMaxLength(2);
        builder.Property(e => e.TextValue).HasMaxLength(4000);
        builder.Property(e => e.CodedValue).HasMaxLength(60);
        builder.Property(e => e.RawInstrumentValue).HasMaxLength(200);
        builder.Property(e => e.AppliedReferenceRangeText).HasMaxLength(200);
        builder.Property(e => e.AppliedUnitSymbol).HasMaxLength(40);
        builder.Property(e => e.TechnicalComment).HasMaxLength(2000);
        builder.Property(e => e.InstrumentFlags).HasMaxLength(500);

        builder.MapMultilingual(e => e.BiologistComment, "BiologistComment",
                                frenchRequired: false, maxLength: 2000);

        builder.HasOne(e => e.TestParameter).WithMany().HasForeignKey(e => e.TestParameterId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Revisions).WithOne()
               .HasForeignKey(r => r.TestResultId).OnDelete(DeleteBehavior.Restrict);

        // One row per parameter per order item. Enforced so a double import from an analyser
        // cannot produce two competing values for the same measurand.
        builder.HasIndex(e => new { e.LabOrderItemId, e.TestParameterId })
               .IsUnique()
               .HasDatabaseName("UX_TestResults_Item_Parameter");

        builder.HasIndex(e => new { e.LabOrderId, e.Status }).HasDatabaseName("IX_TestResults_Order");

        builder.HasIndex(e => new { e.SiteId, e.DepartmentId, e.Status })
               .HasDatabaseName("IX_TestResults_Validation");

        // Serves the antériorité lookup and the delta check: this patient's previous value for
        // this parameter. Ordered descending on the date so the first row is the answer.
        builder.HasIndex(e => new { e.TestParameterId, e.ValidatedAt })
               .HasDatabaseName("IX_TestResults_Parameter_History");

        // Critical results awaiting communication — the worklist a biologist must clear.
        builder.HasIndex(e => e.Flags)
               .HasFilter("[Flags] <> 0")
               .HasDatabaseName("IX_TestResults_Flagged");
    }
}

public sealed class ResultRevisionConfiguration : IEntityTypeConfiguration<ResultRevision>
{
    public void Configure(EntityTypeBuilder<ResultRevision> builder)
    {
        builder.ToTable("ResultRevisions", "res", t => t.HasTrigger("TR_ResultRevisions_NoUpdate"));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Reason).HasMaxLength(500).IsRequired();
        builder.Property(e => e.PreviousNumericValue).HasColumnType("decimal(18,6)");
        builder.Property(e => e.PreviousValueOperator).HasMaxLength(2);
        builder.Property(e => e.PreviousTextValue).HasMaxLength(4000);
        builder.Property(e => e.PreviousCodedValue).HasMaxLength(60);
        builder.HasIndex(e => new { e.TestResultId, e.RevisionNumber })
               .IsUnique()
               .HasDatabaseName("UX_ResultRevisions_Result_Number");
    }
}

public sealed class CriticalResultNotificationConfiguration
    : IEntityTypeConfiguration<CriticalResultNotification>
{
    public void Configure(EntityTypeBuilder<CriticalResultNotification> builder)
    {
        builder.ToTable("CriticalResultNotifications", "res");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ReportedValue).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ParameterName).HasMaxLength(200);
        builder.Property(e => e.ContactUsed).HasMaxLength(100);
        builder.Property(e => e.RecipientName).HasMaxLength(200);
        builder.Property(e => e.RecipientRole).HasMaxLength(100);
        builder.Property(e => e.ReadBackValue).HasMaxLength(100);
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.HasIndex(e => new { e.SiteId, e.Outcome, e.AttemptedAt })
               .HasDatabaseName("IX_CriticalNotifications_Pending");

        builder.HasIndex(e => e.TestResultId).HasDatabaseName("IX_CriticalNotifications_Result");
    }
}

public sealed class LabReportConfiguration : IEntityTypeConfiguration<LabReport>
{
    public void Configure(EntityTypeBuilder<LabReport> builder)
    {
        builder.ToTable("LabReports", "rpt");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RenderedPdf).HasColumnType("varbinary(max)");
        builder.Property(e => e.ContentHash).HasMaxLength(64);
        builder.Property(e => e.AccessToken).HasMaxLength(64);
        builder.Property(e => e.SignatoryName).HasMaxLength(200);
        builder.Property(e => e.SignatoryQualification).HasMaxLength(120);
        builder.Property(e => e.SignatoryOrderNumber).HasMaxLength(40);
        builder.Property(e => e.AmendedParameterCodesCsv).HasMaxLength(1000);

        builder.MapMultilingual(e => e.AmendmentReason, "AmendmentReason",
                                frenchRequired: false, maxLength: 1000);
        builder.MapMultilingual(e => e.Conclusion, "Conclusion",
                                frenchRequired: false, maxLength: 4000);

        // The list of covered items is part of the frozen document, not a live relationship: a
        // reprint must show exactly what the released version showed.
        builder.Property(e => e.IncludedOrderItemIdsCsv).HasMaxLength(4000);
        builder.Ignore(e => e.IncludedOrderItemIds);

        builder.HasMany(e => e.Deliveries).WithOne()
               .HasForeignKey(d => d.LabReportId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.LabOrderId, e.VersionNumber })
               .IsUnique()
               .HasDatabaseName("UX_LabReports_Order_Version");

        builder.HasIndex(e => e.AccessToken)
               .IsUnique()
               .HasFilter("[AccessToken] IS NOT NULL")
               .HasDatabaseName("UX_LabReports_AccessToken");
    }
}

public sealed class LabReportDeliveryConfiguration : IEntityTypeConfiguration<LabReportDelivery>
{
    public void Configure(EntityTypeBuilder<LabReportDelivery> builder)
    {
        builder.ToTable("ReportDeliveries", "rpt");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.RecipientIdentifier).HasMaxLength(250);
        builder.Property(e => e.RecipientName).HasMaxLength(200);
        builder.Property(e => e.BearerName).HasMaxLength(200);
        builder.Property(e => e.BearerCin).HasMaxLength(20);
        builder.Property(e => e.FailureReason).HasMaxLength(500);
        builder.HasIndex(e => new { e.LabReportId, e.RequestedAt })
               .HasDatabaseName("IX_ReportDeliveries_Report");
    }
}
