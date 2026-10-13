using System.Linq.Expressions;
using MarocLIS.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Maps a <see cref="MultilingualText"/> to three columns on the owner's table —
    /// <c>{prefix}Fr</c>, <c>{prefix}Ar</c>, <c>{prefix}En</c>.
    /// <para>
    /// Three real columns rather than a JSON blob: the reception desk searches analysis names, the
    /// report generator sorts by them, and "missing Arabic translations" has to be a query. None of
    /// that works against serialised JSON on SQL Server Express.
    /// </para>
    /// </summary>
    public static EntityTypeBuilder<TEntity> MapMultilingual<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, MultilingualText?>> navigation,
        string prefix,
        bool frenchRequired = true,
        int maxLength = 300)
        where TEntity : class
    {
        builder.OwnsOne(navigation, owned =>
        {
            owned.Property(p => p.Fr)
                 .HasColumnName($"{prefix}Fr")
                 .HasMaxLength(maxLength)
                 .IsRequired(frenchRequired);

            owned.Property(p => p.Ar)
                 .HasColumnName($"{prefix}Ar")
                 .HasMaxLength(maxLength);

            owned.Property(p => p.En)
                 .HasColumnName($"{prefix}En")
                 .HasMaxLength(maxLength);
        });

        if (!frenchRequired)
            builder.Navigation(navigation).IsRequired(false);

        return builder;
    }

    /// <summary>
    /// Applies the accent- and case-insensitive collation used for every column an operator types
    /// into a search box. Without it, "Bénani" and "benani" are different patients as far as SQL
    /// Server is concerned, and a reception clerk creates a duplicate file.
    /// <para>
    /// One generic method, not an overload pair for <c>string</c> and <c>string?</c>: nullable
    /// annotations are erased, so those two overloads had the same signature. Generic over the
    /// property type with a <c>class?</c> constraint rather than fixed to <c>string</c>, because a
    /// fixed <c>PropertyBuilder&lt;string&gt;</c> parameter makes every nullable column's call site
    /// a CS8620 — and whether a column is required is the mapping's business, not this collation
    /// helper's. The constraint still keeps a non-string property out.
    /// </para>
    /// </summary>
    public static PropertyBuilder<TProperty> AsSearchable<TProperty>(
        this PropertyBuilder<TProperty> builder, int maxLength = 200)
        where TProperty : class? =>
        builder.HasMaxLength(maxLength).UseCollation(Collations.AccentInsensitive);

    /// <summary>
    /// A short code that must compare exactly: analysis codes, barcodes, invoice numbers. Binary
    /// collation, because "GLU" and "glu" must not be allowed to become two catalogue entries and
    /// a barcode lookup must never match loosely.
    /// </summary>
    public static PropertyBuilder<string> AsCode(this PropertyBuilder<string> builder,
                                                 int maxLength = 40) =>
        builder.HasMaxLength(maxLength).IsRequired().UseCollation(Collations.CaseSensitive);
}

public static class Collations
{
    /// <summary>
    /// Accent- and case-insensitive. Applied to names so French diacritics and Arabic
    /// orthographic variants do not fragment a patient search.
    /// </summary>
    public const string AccentInsensitive = "Latin1_General_100_CI_AI_SC_UTF8";

    /// <summary>Case- and accent-sensitive, for identifiers that must match exactly.</summary>
    public const string CaseSensitive = "Latin1_General_100_CS_AS_SC_UTF8";
}
