using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// ---- Billing -----------------------------------------------------------------------------------

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices", "bil");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Number).AsCode(40);
        builder.Property(e => e.BilledToName).HasMaxLength(250).IsRequired();
        builder.Property(e => e.BilledToIce).HasMaxLength(15);
        builder.Property(e => e.BilledToFiscalId).HasMaxLength(20);
        builder.Property(e => e.BilledToAddress).HasMaxLength(400);
        builder.Property(e => e.BilledToCity).HasMaxLength(100);
        builder.Property(e => e.CoveredOrderNumbersCsv).HasMaxLength(4000);
        builder.Property(e => e.AmountInWords).HasMaxLength(500);
        builder.Property(e => e.CorrectsInvoiceNumber).HasMaxLength(40);
        builder.Property(e => e.CorrectionReason).HasMaxLength(500);
        builder.Property(e => e.ClaimReference).HasMaxLength(60);
        builder.Property(e => e.ClaimRejectionReason).HasMaxLength(500);
        builder.Property(e => e.BiologistInpe).HasMaxLength(20);
        builder.Property(e => e.PrescriberInpe).HasMaxLength(20);
        builder.Property(e => e.PrescriberName).HasMaxLength(200);
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Property(e => e.DiscountReason).HasMaxLength(300);
        builder.Property(e => e.RenderedPdf).HasColumnType("varbinary(max)");
        builder.Property(e => e.ContentHash).HasMaxLength(64);

        // Total of the credit notes issued against the invoice; what is owed is derived from it.
        builder.Property(e => e.CreditedAmount).HasColumnType("decimal(18,2)");
        builder.Ignore(e => e.OutstandingAmount);
        builder.Ignore(e => e.CreditableAmount);
        builder.Ignore(e => e.IsFullyCredited);

        builder.HasMany(e => e.Lines).WithOne()
               .HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.Payments).WithOne()
               .HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Restrict);

        // The accounting series must be unbroken and unambiguous. Filtered on issued invoices so a
        // draft, which has no number yet, does not collide with another draft.
        builder.HasIndex(e => new { e.SiteId, e.Kind, e.Number })
               .IsUnique()
               .HasFilter("[Status] <> 0")
               .HasDatabaseName("UX_Invoices_Site_Kind_Number");

        builder.HasIndex(e => new { e.SiteId, e.IssueDate }).HasDatabaseName("IX_Invoices_Site_Date");
        builder.HasIndex(e => new { e.PatientId, e.IssueDate }).HasDatabaseName("IX_Invoices_Patient");

        // Receivables ageing.
        builder.HasIndex(e => new { e.PayerId, e.Status, e.DueDate })
               .HasDatabaseName("IX_Invoices_Receivables");
    }
}

public sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("InvoiceLines", "bil");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Description).HasMaxLength(300).IsRequired();
        builder.Property(e => e.DescriptionArabic).HasMaxLength(300);
        builder.Property(e => e.AnalysisCode).HasMaxLength(30);
        builder.Property(e => e.NationalActCode).HasMaxLength(30);
        builder.Property(e => e.LetterKey).HasMaxLength(5);
        builder.Property(e => e.Coefficient).HasColumnType("decimal(18,4)");
        builder.Property(e => e.LetterKeyValue).HasColumnType("decimal(18,4)");
        builder.Property(e => e.VatRate).HasColumnType("decimal(6,4)");
        builder.Property(e => e.ReimbursementRate).HasColumnType("decimal(6,4)");

        // Computed on the entity; never stored, so they cannot drift from the stored inputs.
        builder.Ignore(e => e.GrossAmount);
        builder.Ignore(e => e.NetAmount);
        builder.Ignore(e => e.VatAmount);
        builder.Ignore(e => e.TotalAmount);
        builder.Ignore(e => e.PatientShare);
        builder.Ignore(e => e.ExcessOverReferenceTariff);

        builder.HasIndex(e => new { e.InvoiceId, e.LineNumber })
               .IsUnique()
               .HasDatabaseName("UX_InvoiceLines_Invoice_Line");

        builder.HasIndex(e => e.LabOrderItemId)
               .HasFilter("[LabOrderItemId] IS NOT NULL")
               .HasDatabaseName("IX_InvoiceLines_OrderItem");
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments", "bil");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Reference).HasMaxLength(60);
        builder.Property(e => e.BankName).HasMaxLength(120);
        builder.Property(e => e.RemittanceReference).HasMaxLength(60);
        builder.Property(e => e.BounceReason).HasMaxLength(500);
        builder.Property(e => e.RefundReason).HasMaxLength(500);
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Ignore(e => e.RequiresTraceableMethodWarning);

        builder.HasIndex(e => new { e.SiteId, e.PaymentDate, e.Method })
               .HasDatabaseName("IX_Payments_Site_Date_Method");

        builder.HasIndex(e => e.CashSessionId)
               .HasFilter("[CashSessionId] IS NOT NULL")
               .HasDatabaseName("IX_Payments_CashSession");
    }
}

public sealed class DepositConfiguration : IEntityTypeConfiguration<Deposit>
{
    public void Configure(EntityTypeBuilder<Deposit> builder)
    {
        builder.ToTable("Deposits", "bil");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Number).HasMaxLength(40).IsRequired();
        builder.Property(e => e.Reference).HasMaxLength(60);
        builder.Property(e => e.BankName).HasMaxLength(120);
        builder.Property(e => e.RefundReason).HasMaxLength(500);
        builder.Ignore(e => e.Remaining);

        builder.HasIndex(e => e.Number).IsUnique().HasDatabaseName("UX_Deposits_Number");
        builder.HasIndex(e => new { e.LabOrderId, e.Status }).HasDatabaseName("IX_Deposits_Order_Status");
        builder.HasIndex(e => e.CashSessionId)
               .HasFilter("[CashSessionId] IS NOT NULL")
               .HasDatabaseName("IX_Deposits_CashSession");
    }
}

public sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTable("CashSessions", "bil");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Workstation).HasMaxLength(100);
        builder.Property(e => e.VarianceExplanation).HasMaxLength(1000);
        builder.Ignore(e => e.ExpectedClosingCash);
        builder.Ignore(e => e.Variance);
        builder.Ignore(e => e.TotalReceipts);

        // One open till per operator at a time; otherwise cash cannot be attributed on closing.
        builder.HasIndex(e => new { e.OperatorUserId, e.Status })
               .IsUnique()
               .HasFilter("[Status] = 0")
               .HasDatabaseName("UX_CashSessions_OpenPerOperator");
    }
}

// ---- Quality -----------------------------------------------------------------------------------

public sealed class QcMaterialConfiguration : IEntityTypeConfiguration<QcMaterial>
{
    public void Configure(EntityTypeBuilder<QcMaterial> builder)
    {
        builder.ToTable("QcMaterials", "qc");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.LotNumber).AsCode(60);
        builder.MapMultilingual(e => e.Name, "Name");
        builder.Property(e => e.Manufacturer).HasMaxLength(150);
        builder.Property(e => e.CatalogueReference).HasMaxLength(100);
        builder.HasIndex(e => new { e.LotNumber, e.Level })
               .IsUnique()
               .HasDatabaseName("UX_QcMaterials_Lot_Level");
    }
}

public sealed class QcTargetConfiguration : IEntityTypeConfiguration<QcTarget>
{
    public void Configure(EntityTypeBuilder<QcTarget> builder)
    {
        builder.ToTable("QcTargets", "qc");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.AssignedMean).HasColumnType("decimal(18,6)");
        builder.Property(e => e.AssignedSd).HasColumnType("decimal(18,6)");
        builder.Property(e => e.LaboratoryMean).HasColumnType("decimal(18,6)");
        builder.Property(e => e.LaboratorySd).HasColumnType("decimal(18,6)");
        builder.Property(e => e.MaxAcceptableCvPercent).HasColumnType("decimal(8,3)");

        builder.Ignore(e => e.EffectiveMean);
        builder.Ignore(e => e.EffectiveSd);
        builder.Ignore(e => e.CvPercent);
        builder.Ignore(e => e.IsCvAcceptable);

        builder.HasIndex(e => new { e.QcMaterialId, e.TestParameterId, e.InstrumentId })
               .IsUnique()
               .HasDatabaseName("UX_QcTargets_Material_Parameter_Instrument");
    }
}

public sealed class QcResultConfiguration : IEntityTypeConfiguration<QcResult>
{
    public void Configure(EntityTypeBuilder<QcResult> builder)
    {
        builder.ToTable("QcResults", "qc");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Value).HasColumnType("decimal(18,6)");
        builder.Property(e => e.TargetMean).HasColumnType("decimal(18,6)");
        builder.Property(e => e.TargetSd).HasColumnType("decimal(18,6)");
        builder.Property(e => e.ZScore).HasColumnType("decimal(12,4)");
        builder.Property(e => e.CorrectiveActionTaken).HasMaxLength(2000);
        builder.Property(e => e.OverrideJustification).HasMaxLength(1000);
        builder.Property(e => e.Comment).HasMaxLength(1000);

        // A new lot's acceptance point, kept out of the routine series and statistics.
        builder.Property(e => e.IsLotEvaluation);

        // The Levey-Jennings chart reads a window of points for one target, newest last.
        builder.HasIndex(e => new { e.QcTargetId, e.MeasuredAt })
               .HasDatabaseName("IX_QcResults_Target_Measured");

        // Rejected points awaiting review: the quality manager's daily list.
        builder.HasIndex(e => new { e.SiteId, e.Outcome, e.ReviewedAt })
               .HasDatabaseName("IX_QcResults_Review");

        // Multi-level rules (2-2s within run, R-4s) read every level of one run.
        builder.HasIndex(e => e.QcRunId)
               .HasFilter("[QcRunId] IS NOT NULL")
               .HasDatabaseName("IX_QcResults_Run");
    }
}

public sealed class NonConformityConfiguration : IEntityTypeConfiguration<NonConformity>
{
    public void Configure(EntityTypeBuilder<NonConformity> builder)
    {
        builder.ToTable("NonConformities", "qc");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Reference).AsCode(30);
        builder.HasIndex(e => e.Reference).IsUnique().HasDatabaseName("UX_NonConformities_Reference");

        builder.MapMultilingual(e => e.Description, "Description", maxLength: 2000);
        builder.MapMultilingual(e => e.ClinicalImpactAssessment, "ClinicalImpact",
                                frenchRequired: false, maxLength: 2000);
        builder.MapMultilingual(e => e.ImmediateAction, "ImmediateAction",
                                frenchRequired: false, maxLength: 2000);
        builder.MapMultilingual(e => e.RootCauseAnalysis, "RootCause",
                                frenchRequired: false, maxLength: 2000);
        builder.MapMultilingual(e => e.EffectivenessVerification, "Effectiveness",
                                frenchRequired: false, maxLength: 2000);

        builder.HasMany(e => e.Actions).WithOne()
               .HasForeignKey(a => a.NonConformityId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.SiteId, e.Status, e.DueDate })
               .HasDatabaseName("IX_NonConformities_Open");

        builder.HasIndex(e => new { e.Category, e.Severity, e.DetectedAt })
               .HasDatabaseName("IX_NonConformities_Analysis");
    }
}

public sealed class CorrectiveActionConfiguration : IEntityTypeConfiguration<CorrectiveAction>
{
    public void Configure(EntityTypeBuilder<CorrectiveAction> builder)
    {
        builder.ToTable("CorrectiveActions", "qc");
        builder.HasKey(e => e.Id);
        builder.MapMultilingual(e => e.Description, "Description", maxLength: 2000);
        builder.MapMultilingual(e => e.CompletionEvidence, "Evidence",
                                frenchRequired: false, maxLength: 2000);
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.HasIndex(e => new { e.ResponsibleUserId, e.Status, e.TargetDate })
               .HasDatabaseName("IX_CorrectiveActions_Owner");
    }
}

public sealed class ControlledDocumentConfiguration : IEntityTypeConfiguration<ControlledDocument>
{
    public void Configure(EntityTypeBuilder<ControlledDocument> builder)
    {
        builder.ToTable("ControlledDocuments", "qc");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Reference).AsCode(40);
        builder.HasIndex(e => e.Reference).IsUnique().HasDatabaseName("UX_ControlledDocuments_Reference");
        builder.MapMultilingual(e => e.Title, "Title", maxLength: 300);
        builder.Property(e => e.ProcessCode).HasMaxLength(30);

        builder.Ignore(e => e.EffectiveVersion);
        builder.Ignore(e => e.LatestVersion);
        builder.Ignore(e => e.NextReviewDue);

        builder.HasMany(e => e.Versions).WithOne()
               .HasForeignKey(v => v.ControlledDocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Acknowledgements).WithOne()
               .HasForeignKey(a => a.ControlledDocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public void Configure(EntityTypeBuilder<DocumentVersion> builder)
    {
        builder.ToTable("DocumentVersions", "qc");
        builder.HasKey(e => e.Id);
        builder.MapMultilingual(e => e.ChangeSummary, "ChangeSummary",
                                frenchRequired: false, maxLength: 2000);
        builder.Property(e => e.Content).HasColumnType("varbinary(max)");
        builder.Property(e => e.ContentType).HasMaxLength(100);
        builder.Property(e => e.FileName).HasMaxLength(260);
        builder.Property(e => e.ContentHash).HasMaxLength(64);

        builder.HasIndex(e => new { e.ControlledDocumentId, e.VersionNumber })
               .IsUnique()
               .HasDatabaseName("UX_DocumentVersions_Document_Version");

        // Exactly one version of a document may be in force at a time. Enforced in the database
        // because two effective procedures is precisely the finding document control exists to stop.
        builder.HasIndex(e => e.ControlledDocumentId)
               .IsUnique()
               .HasFilter("[Status] = 4")
               .HasDatabaseName("UX_DocumentVersions_OneEffective");
    }
}

public sealed class DocumentAcknowledgementConfiguration : IEntityTypeConfiguration<DocumentAcknowledgement>
{
    public void Configure(EntityTypeBuilder<DocumentAcknowledgement> builder)
    {
        builder.ToTable("DocumentAcknowledgements", "qc");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.ControlledDocumentId, e.UserId, e.VersionNumber })
               .IsUnique()
               .HasDatabaseName("UX_DocumentAcks_Document_User_Version");
    }
}

public sealed class EqaParticipationConfiguration : IEntityTypeConfiguration<EqaParticipation>
{
    public void Configure(EntityTypeBuilder<EqaParticipation> builder)
    {
        builder.ToTable("EqaParticipations", "qc");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SchemeName).HasMaxLength(150).IsRequired();
        builder.Property(e => e.RoundReference).HasMaxLength(60).IsRequired();
        builder.MapMultilingual(e => e.InvestigationNotes, "Investigation",
                                frenchRequired: false, maxLength: 2000);

        builder.Property(e => e.ReportedValue).HasColumnType("decimal(18,6)");
        builder.Property(e => e.AssignedValue).HasColumnType("decimal(18,6)");
        builder.Property(e => e.PeerGroupMean).HasColumnType("decimal(18,6)");
        builder.Property(e => e.PeerGroupSd).HasColumnType("decimal(18,6)");
        builder.Property(e => e.ZScore).HasColumnType("decimal(12,4)");
        builder.Property(e => e.BiasPercent).HasColumnType("decimal(12,4)");

        builder.Ignore(e => e.RequiresNonConformity);

        builder.HasIndex(e => new { e.SiteId, e.RoundDate, e.Outcome })
               .HasDatabaseName("IX_EqaParticipations_Site_Round");
    }
}

// ---- Inventory ---------------------------------------------------------------------------------

public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("Suppliers", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(30);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_Suppliers_Code");
        builder.Property(e => e.Name).AsSearchable(200);
        builder.Property(e => e.Ice).HasMaxLength(15);
        builder.Property(e => e.FiscalId).HasMaxLength(20);
        builder.Property(e => e.CommercialRegisterNumber).HasMaxLength(40);
        builder.Property(e => e.ContactName).HasMaxLength(200);
        builder.Property(e => e.Phone).HasMaxLength(30);
        builder.Property(e => e.Email).HasMaxLength(200);
        builder.Property(e => e.AddressLine).HasMaxLength(400);
        builder.Property(e => e.City).HasMaxLength(100);
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Property(e => e.AccountingAccount).HasMaxLength(20);
    }
}

public sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("StockItems", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(40);
        builder.HasIndex(e => new { e.SiteId, e.Code })
               .IsUnique()
               .HasDatabaseName("UX_StockItems_Site_Code");

        builder.MapMultilingual(e => e.Name, "Name");
        builder.Property(e => e.UnitOfMeasure).HasMaxLength(30).IsRequired();
        builder.Property(e => e.SupplierCatalogueReference).HasMaxLength(100);
        builder.Property(e => e.Manufacturer).HasMaxLength(150);
        builder.Property(e => e.UsedByAnalysisCodesCsv).HasMaxLength(2000);
        builder.Property(e => e.DefaultStorageLocation).HasMaxLength(100);
        builder.Property(e => e.HazardClass).HasMaxLength(60);

        builder.Property(e => e.ReorderPoint).HasColumnType("decimal(18,3)");
        builder.Property(e => e.ReorderQuantity).HasColumnType("decimal(18,3)");
        builder.Property(e => e.MinimumStock).HasColumnType("decimal(18,3)");
        builder.Property(e => e.MaximumStock).HasColumnType("decimal(18,3)");
        builder.Property(e => e.LastUnitPriceMad).HasColumnType("decimal(18,2)");

        builder.HasMany(e => e.Lots).WithOne()
               .HasForeignKey(l => l.StockItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ReagentLotConfiguration : IEntityTypeConfiguration<ReagentLot>
{
    public void Configure(EntityTypeBuilder<ReagentLot> builder)
    {
        builder.ToTable("ReagentLots", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.LotNumber).AsCode(60);
        builder.Property(e => e.DeliveryNoteReference).HasMaxLength(60);
        builder.Property(e => e.SupplierInvoiceReference).HasMaxLength(60);
        builder.Property(e => e.StorageLocation).HasMaxLength(100);
        builder.Property(e => e.AcceptanceEvidence).HasMaxLength(1000);
        builder.Property(e => e.RejectionReason).HasMaxLength(500);
        builder.Property(e => e.RecallReference).HasMaxLength(100);

        builder.Property(e => e.QuantityReceived).HasColumnType("decimal(18,3)");
        builder.Property(e => e.QuantityRemaining).HasColumnType("decimal(18,3)");
        builder.Property(e => e.UnitPriceMad).HasColumnType("decimal(18,2)");

        // On-board stability runs from the latest unit issued; the analyser a new lot is being
        // evaluated on receives its transmitted controls.
        builder.Property(e => e.LastOpenedOn);
        builder.Property(e => e.EvaluationInstrumentId);

        builder.HasIndex(e => new { e.StockItemId, e.LotNumber })
               .IsUnique()
               .HasDatabaseName("UX_ReagentLots_Item_Lot");

        // First-expiry-first-out consumption, and the expiry-alert list.
        builder.HasIndex(e => new { e.StockItemId, e.ExpiryDate })
               .HasDatabaseName("IX_ReagentLots_Item_Expiry");
    }
}

public sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Quantity).HasColumnType("decimal(18,3)");
        builder.Property(e => e.BalanceAfter).HasColumnType("decimal(18,3)");
        builder.Property(e => e.Reason).HasMaxLength(500);
        builder.Property(e => e.Reference).HasMaxLength(60);
        builder.Ignore(e => e.SignedQuantity);

        // A plain reference, like the item and the lot of this ledger row: dated receipts per order.
        builder.HasIndex(e => e.PurchaseOrderId)
               .HasFilter("[PurchaseOrderId] IS NOT NULL")
               .HasDatabaseName("IX_StockMovements_PurchaseOrder");

        builder.HasIndex(e => new { e.StockItemId, e.OccurredAt })
               .HasDatabaseName("IX_StockMovements_Item_Occurred");
        builder.HasIndex(e => e.ReagentLotId)
               .HasFilter("[ReagentLotId] IS NOT NULL")
               .HasDatabaseName("IX_StockMovements_Lot");
    }
}

public sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("PurchaseOrders", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Number).AsCode(40);
        builder.HasIndex(e => e.Number).IsUnique().HasDatabaseName("UX_PurchaseOrders_Number");
        builder.Property(e => e.SupplierReference).HasMaxLength(60);
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.Ignore(e => e.TotalExcludingVat);
        builder.Ignore(e => e.TotalVat);
        builder.Ignore(e => e.TotalIncludingVat);

        builder.HasMany(e => e.Lines).WithOne()
               .HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.SiteId, e.Status, e.ExpectedDeliveryDate })
               .HasDatabaseName("IX_PurchaseOrders_Outstanding");
    }
}

public sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("PurchaseOrderLines", "inv");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Description).HasMaxLength(300);
        builder.Property(e => e.QuantityOrdered).HasColumnType("decimal(18,3)");
        builder.Property(e => e.QuantityReceived).HasColumnType("decimal(18,3)");
        builder.Property(e => e.UnitPriceMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.DiscountRate).HasColumnType("decimal(6,4)");
        builder.Property(e => e.VatRate).HasColumnType("decimal(6,4)");

        builder.Ignore(e => e.NetUnitPriceMad);
        builder.Ignore(e => e.LineTotalExcludingVat);
        builder.Ignore(e => e.VatAmount);
        builder.Ignore(e => e.QuantityOutstanding);
        builder.Ignore(e => e.IsFullyReceived);

        builder.HasIndex(e => new { e.PurchaseOrderId, e.LineNumber })
               .IsUnique()
               .HasDatabaseName("UX_PurchaseOrderLines_Order_Line");
    }
}

// ---- Equipment and interfaces ------------------------------------------------------------------

public sealed class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
{
    public void Configure(EntityTypeBuilder<Instrument> builder)
    {
        builder.ToTable("Instruments", "eqp");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(30);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_Instruments_Code");

        builder.MapMultilingual(e => e.Name, "Name");
        builder.Property(e => e.Manufacturer).HasMaxLength(150);
        builder.Property(e => e.Model).HasMaxLength(150);
        builder.Property(e => e.SerialNumber).HasMaxLength(100);
        builder.Property(e => e.SoftwareVersion).HasMaxLength(60);
        builder.Property(e => e.MaintenanceContractReference).HasMaxLength(100);
        builder.Property(e => e.SupportPhone).HasMaxLength(30);
        builder.Property(e => e.Location).HasMaxLength(150);
        builder.Property(e => e.LastStatusReason).HasMaxLength(500);

        builder.Ignore(e => e.NextPreventiveMaintenanceDue);
        builder.Ignore(e => e.NextCalibrationDue);
        builder.Ignore(e => e.NextVerificationDue);

        builder.HasMany(e => e.MaintenanceRecords).WithOne()
               .HasForeignKey(m => m.InstrumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Calibrations).WithOne()
               .HasForeignKey(c => c.InstrumentId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.SiteId, e.Status }).HasDatabaseName("IX_Instruments_Site_Status");
    }
}

public sealed class MaintenanceRecordConfiguration : IEntityTypeConfiguration<MaintenanceRecord>
{
    public void Configure(EntityTypeBuilder<MaintenanceRecord> builder)
    {
        builder.ToTable("MaintenanceRecords", "eqp");
        builder.HasKey(e => e.Id);
        builder.MapMultilingual(e => e.Description, "Description", maxLength: 2000);
        builder.Property(e => e.ExternalTechnicianName).HasMaxLength(200);
        builder.Property(e => e.SupplierInterventionReference).HasMaxLength(60);
        builder.Property(e => e.PartsReplaced).HasMaxLength(1000);
        builder.Property(e => e.PostInterventionCheckNotes).HasMaxLength(1000);
        builder.Property(e => e.DowntimeHours).HasColumnType("decimal(10,2)");
        builder.Property(e => e.CostMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.AttachedReport).HasColumnType("varbinary(max)");
        builder.Property(e => e.AttachedReportContentType).HasMaxLength(100);
        builder.HasIndex(e => new { e.InstrumentId, e.PerformedOn })
               .HasDatabaseName("IX_MaintenanceRecords_Instrument_Date");
    }
}

public sealed class CalibrationRecordConfiguration : IEntityTypeConfiguration<CalibrationRecord>
{
    public void Configure(EntityTypeBuilder<CalibrationRecord> builder)
    {
        builder.ToTable("CalibrationRecords", "eqp");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.CalibratorLotNumber).HasMaxLength(60);
        builder.Property(e => e.CalibratorManufacturer).HasMaxLength(150);
        builder.Property(e => e.CurveData).HasMaxLength(4000);
        builder.Property(e => e.Comment).HasMaxLength(1000);
        builder.Property(e => e.Slope).HasColumnType("decimal(18,6)");
        builder.Property(e => e.Intercept).HasColumnType("decimal(18,6)");
        builder.Property(e => e.CorrelationCoefficient).HasColumnType("decimal(10,6)");

        // A result must be traceable to the calibration in force for its parameter when it ran.
        builder.HasIndex(e => new { e.InstrumentId, e.TestParameterId, e.PerformedOn })
               .HasDatabaseName("IX_CalibrationRecords_Instrument_Parameter");
    }
}

public sealed class InstrumentInterfaceConfiguration : IEntityTypeConfiguration<InstrumentInterface>
{
    public void Configure(EntityTypeBuilder<InstrumentInterface> builder)
    {
        builder.ToTable("InstrumentInterfaces", "eqp");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SerialPortName).HasMaxLength(20);
        builder.Property(e => e.Parity).HasMaxLength(10).IsRequired();
        builder.Property(e => e.StopBits).HasMaxLength(15).IsRequired();
        builder.Property(e => e.Handshake).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Host).HasMaxLength(150);
        builder.Property(e => e.InboundDirectory).HasMaxLength(300);
        builder.Property(e => e.OutboundDirectory).HasMaxLength(300);
        builder.Property(e => e.ProcessedDirectory).HasMaxLength(300);
        builder.Property(e => e.FilePattern).HasMaxLength(60);
        builder.Property(e => e.Encoding).HasMaxLength(30).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(1000);

        builder.Property(e => e.FormatTestCodeSuffix).HasMaxLength(10);
        builder.Property(e => e.FormatSpecimenIdField).HasMaxLength(10);
        builder.Property(e => e.FormatAcceptedResultTypes).HasMaxLength(60);
        builder.Ignore(e => e.MessageFormat);

        builder.HasMany(e => e.Mappings).WithOne()
               .HasForeignKey(m => m.InstrumentInterfaceId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.InstrumentId).IsUnique()
               .HasDatabaseName("UX_InstrumentInterfaces_Instrument");
    }
}

public sealed class InstrumentTestMappingConfiguration : IEntityTypeConfiguration<InstrumentTestMapping>
{
    public void Configure(EntityTypeBuilder<InstrumentTestMapping> builder)
    {
        builder.ToTable("InstrumentTestMappings", "eqp");
        builder.HasKey(e => e.Id);

        // Case-sensitive: some analysers distinguish "Na" from "NA".
        builder.Property(e => e.InstrumentTestCode).AsCode(40);
        builder.Property(e => e.InstrumentUnit).HasMaxLength(40);
        builder.Property(e => e.CodedValueMap).HasMaxLength(1000);
        builder.Property(e => e.ConversionFactor).HasColumnType("decimal(18,8)");
        builder.Property(e => e.ConversionOffset).HasColumnType("decimal(18,8)");

        // One meaning per analyser code. A duplicate would make the routing of a value ambiguous,
        // which is the failure mode this whole table exists to prevent.
        builder.HasIndex(e => new { e.InstrumentInterfaceId, e.InstrumentTestCode })
               .IsUnique()
               .HasDatabaseName("UX_InstrumentTestMappings_Interface_Code");
    }
}

public sealed class InstrumentMessageLogConfiguration : IEntityTypeConfiguration<InstrumentMessageLog>
{
    public void Configure(EntityTypeBuilder<InstrumentMessageLog> builder)
    {
        builder.ToTable("InstrumentMessageLog", "eqp");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.RawContent).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(e => e.SpecimenIds).HasMaxLength(1000);
        builder.Property(e => e.Issues).HasMaxLength(4000);

        builder.HasIndex(e => new { e.InstrumentInterfaceId, e.OccurredAt })
               .HasDatabaseName("IX_InstrumentMessageLog_Interface_Occurred");

        builder.HasIndex(e => new { e.Status, e.OccurredAt })
               .HasDatabaseName("IX_InstrumentMessageLog_Status");
    }
}

// ---- Traceability ------------------------------------------------------------------------------

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        // HasTrigger is declared, not decorative. From EF Core 7 the SQL Server provider saves
        // through an OUTPUT clause, and SQL Server refuses OUTPUT on a table carrying an enabled
        // trigger (error 334). This table has one — the append-only guard installed by the initial
        // migration — so without this declaration EF uses the OUTPUT path and every audit write
        // fails, which would take down the write it was recording with it. Declaring it makes EF
        // fall back to the pre-7 batching for this table only.
        builder.ToTable("AuditEntries", "aud", t => t.HasTrigger("TR_AuditEntries_NoUpdate"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.EntityName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.AggregateName).HasMaxLength(100);
        builder.Property(e => e.UserName).HasMaxLength(60);
        builder.Property(e => e.Workstation).HasMaxLength(100);
        builder.Property(e => e.OsUser).HasMaxLength(100);
        builder.Property(e => e.Changes).HasColumnType("nvarchar(max)");
        builder.Property(e => e.ChangedProperties).HasMaxLength(2000);
        builder.Property(e => e.Reason).HasMaxLength(1000);

        // Append-only: no row version, because nothing ever updates these rows.
        builder.Ignore(e => e.RowVersion);

        builder.HasIndex(e => new { e.EntityName, e.EntityId, e.OccurredAt })
               .HasDatabaseName("IX_AuditEntries_Entity");

        builder.HasIndex(e => new { e.UserId, e.OccurredAt }).HasDatabaseName("IX_AuditEntries_User");

        // "Show me everything that happened to this patient's file" — the question a data-subject
        // access request or a dispute actually asks.
        builder.HasIndex(e => new { e.PatientId, e.OccurredAt })
               .HasFilter("[PatientId] IS NOT NULL")
               .HasDatabaseName("IX_AuditEntries_Patient");

        builder.HasIndex(e => e.CorrelationId)
               .HasFilter("[CorrelationId] IS NOT NULL")
               .HasDatabaseName("IX_AuditEntries_Correlation");
    }
}

public sealed class DataAccessLogConfiguration : IEntityTypeConfiguration<DataAccessLog>
{
    public void Configure(EntityTypeBuilder<DataAccessLog> builder)
    {
        builder.ToTable("DataAccessLog", "aud", t => t.HasTrigger("TR_DataAccessLog_NoUpdate"));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.UserName).HasMaxLength(60);
        builder.Property(e => e.QueryDescription).HasMaxLength(1000);
        builder.Property(e => e.Context).HasMaxLength(150);
        builder.Property(e => e.Workstation).HasMaxLength(100);
        builder.Property(e => e.BreakGlassJustification).HasMaxLength(1000);
        builder.Ignore(e => e.RowVersion);

        builder.HasIndex(e => new { e.PatientId, e.OccurredAt })
               .HasFilter("[PatientId] IS NOT NULL")
               .HasDatabaseName("IX_DataAccessLog_Patient");

        builder.HasIndex(e => new { e.UserId, e.OccurredAt }).HasDatabaseName("IX_DataAccessLog_User");

        builder.HasIndex(e => e.IsBreakGlass)
               .HasFilter("[IsBreakGlass] = 1")
               .HasDatabaseName("IX_DataAccessLog_BreakGlass");
    }
}

public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("SecurityEvents", "aud", t => t.HasTrigger("TR_SecurityEvents_NoUpdate"));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AttemptedUserName).HasMaxLength(60);
        builder.Property(e => e.Workstation).HasMaxLength(100);
        builder.Property(e => e.OsUser).HasMaxLength(100);
        builder.Property(e => e.FailureReasonKey).HasMaxLength(100);
        builder.Property(e => e.Details).HasMaxLength(2000);
        builder.Ignore(e => e.RowVersion);

        builder.HasIndex(e => new { e.Kind, e.OccurredAt }).HasDatabaseName("IX_SecurityEvents_Kind");
        builder.HasIndex(e => new { e.AttemptedUserName, e.OccurredAt })
               .HasDatabaseName("IX_SecurityEvents_UserName");
    }
}
