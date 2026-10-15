using MarocLIS.Domain.Relations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// ---- Commercial relations and patient follow-up messages (schema "rel") -------------------------

public sealed class DelegateVisitConfiguration : IEntityTypeConfiguration<DelegateVisit>
{
    public void Configure(EntityTypeBuilder<DelegateVisit> builder)
    {
        builder.ToTable("DelegateVisits", "rel");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Notes).HasMaxLength(DelegateVisit.NotesMaxLength);
        builder.Property(e => e.DeletionReason).HasMaxLength(500);

        builder.HasIndex(e => new { e.SiteId, e.VisitDate }).HasDatabaseName("IX_DelegateVisits_Site_Date");
        builder.HasIndex(e => new { e.PrescriberId, e.VisitDate }).HasDatabaseName("IX_DelegateVisits_Prescriber");
    }
}

public sealed class PartnershipConfiguration : IEntityTypeConfiguration<Partnership>
{
    public void Configure(EntityTypeBuilder<Partnership> builder)
    {
        builder.ToTable("Partnerships", "rel");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.PartnerName).AsSearchable(200).IsRequired();
        builder.Property(e => e.PartnerIce).HasMaxLength(20);
        builder.Property(e => e.ContactName).HasMaxLength(150);
        builder.Property(e => e.ContactPhone).HasMaxLength(30);
        builder.Property(e => e.ContactEmail).HasMaxLength(200);
        builder.Property(e => e.Reference).HasMaxLength(60);
        builder.Property(e => e.Terms).HasMaxLength(Partnership.TermsMaxLength);
        builder.Property(e => e.DiscountPercent).HasColumnType("decimal(5,2)");
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Property(e => e.DeletionReason).HasMaxLength(500);

        builder.HasIndex(e => new { e.SiteId, e.IsActive }).HasDatabaseName("IX_Partnerships_Site");
    }
}

public sealed class PartnershipDocumentConfiguration : IEntityTypeConfiguration<PartnershipDocument>
{
    public void Configure(EntityTypeBuilder<PartnershipDocument> builder)
    {
        builder.ToTable("PartnershipDocuments", "rel");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.FileName).HasMaxLength(260).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Content).HasColumnType("varbinary(max)").IsRequired();
        builder.HasIndex(e => new { e.PartnershipId, e.AttachedAt }).HasDatabaseName("IX_PartnershipDocuments_Partnership");
    }
}

public sealed class ReminderRuleConfiguration : IEntityTypeConfiguration<ReminderRule>
{
    public void Configure(EntityTypeBuilder<ReminderRule> builder)
    {
        builder.ToTable("ReminderRules", "rel");
        builder.HasKey(e => e.Id);
        builder.MapMultilingual(e => e.Template, "Template", maxLength: MessageWording.MaximumLength);
        builder.HasIndex(e => e.AnalysisTestId).HasDatabaseName("IX_ReminderRules_Analysis");
        builder.HasIndex(e => e.TestPanelId).HasDatabaseName("IX_ReminderRules_Panel");
    }
}

public sealed class PatientMessageConfiguration : IEntityTypeConfiguration<PatientMessage>
{
    public void Configure(EntityTypeBuilder<PatientMessage> builder)
    {
        builder.ToTable("PatientMessages", "rel");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.PhoneNumber).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Text).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Failure).HasMaxLength(500);

        builder.HasIndex(e => new { e.SiteId, e.SentAt }).HasDatabaseName("IX_PatientMessages_Site_SentAt");
        builder.HasIndex(e => new { e.ReminderRuleId, e.SourceOrderId }).HasDatabaseName("IX_PatientMessages_Rule_Order");
        builder.HasIndex(e => e.PatientId).HasDatabaseName("IX_PatientMessages_Patient");
    }
}

public sealed class SurveyInvitationConfiguration : IEntityTypeConfiguration<SurveyInvitation>
{
    public void Configure(EntityTypeBuilder<SurveyInvitation> builder)
    {
        builder.ToTable("SurveyInvitations", "rel");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(SurveyCode.Length).IsRequired();
        builder.Property(e => e.Comment).HasMaxLength(SurveyInvitation.CommentMaxLength);

        // The code is how a reply finds its survey: one survey per code, ever.
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_SurveyInvitations_Code");
        builder.HasIndex(e => new { e.SiteId, e.InvitedAt }).HasDatabaseName("IX_SurveyInvitations_Site_InvitedAt");
        builder.HasIndex(e => e.LabOrderId).HasDatabaseName("IX_SurveyInvitations_Order");
        builder.HasIndex(e => new { e.PatientId, e.InvitedAt }).HasDatabaseName("IX_SurveyInvitations_Patient");
    }
}
