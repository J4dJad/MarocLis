using MarocLIS.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

public sealed class NationalBiologyActConfiguration : IEntityTypeConfiguration<NationalBiologyAct>
{
    public void Configure(EntityTypeBuilder<NationalBiologyAct> builder)
    {
        builder.ToTable("NationalBiologyActs", "cat");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Code).AsCode(NationalBiologyAct.CodeMaxLength);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_NationalBiologyActs_Code");

        // Searched by label from the analysis editor, accents and case ignored (« glycemie »).
        builder.Property(e => e.Label).AsSearchable(NationalBiologyAct.LabelMaxLength).IsRequired();
        builder.Property(e => e.LetterKey).HasMaxLength(5).IsRequired();
        builder.Property(e => e.Chapter).HasMaxLength(NationalBiologyAct.ChapterMaxLength).IsRequired();
        builder.Property(e => e.SubChapter).HasMaxLength(NationalBiologyAct.SubChapterMaxLength);
    }
}
