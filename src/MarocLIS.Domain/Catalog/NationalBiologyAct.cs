using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Catalog;

/// <summary>
/// One act of the official Moroccan nomenclature of medical biology (NABM): its national code
/// (B100…B591), its label and its coefficient of the letter key B.
/// <para>
/// Reference data, not catalogue data. The list is fixed by decree — arrêté 1796-03 of
/// 21 July 2005, completed by the 134 assimilated acts of arrêté 827-12 (1 March 2012) and the
/// 16 of arrêté 1968-16 (24 June 2016) — and shipped with the application, so the laboratory
/// reads it but does not type it. What the laboratory owns is its catalogue: an analysis keeps its
/// own internal code and points at an act through <see cref="AnalysisTest.NationalActCode"/>.
/// Several analyses may share one act, and an analysis outside the nomenclature has none.
/// </para>
/// <para>
/// The value of the letter key in dirhams is not part of the nomenclature; it stays a tariff
/// setting (<see cref="AnalysisTestTariff.LetterKeyValue"/>).
/// </para>
/// </summary>
public sealed class NationalBiologyAct : AuditableAggregateRoot
{
    /// <summary>The letter key of every act of the biology nomenclature.</summary>
    public const string BiologyLetterKey = "B";

    public const int CodeMaxLength = 10;
    public const int LabelMaxLength = 400;
    public const int ChapterMaxLength = 200;
    public const int SubChapterMaxLength = 300;

    private NationalBiologyAct() { }

    public NationalBiologyAct(string code, string label, decimal coefficient, int decreeYear)
    {
        Code = Normalize(code) ?? throw new DomainException("A national act needs a code.");
        Label = label.Trim();
        Coefficient = coefficient;
        DecreeYear = decreeYear;
    }

    /// <summary>National code, e.g. <c>B118</c>. Unique, never renamed.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>The label as the decree words it, in French.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Coefficient of the letter key: the reimbursement base is coefficient × key value.</summary>
    public decimal Coefficient { get; set; }

    public string LetterKey { get; set; } = BiologyLetterKey;

    /// <summary>Chapter of the decree, e.g. « I - Analyses biochimiques ».</summary>
    public string Chapter { get; set; } = string.Empty;

    /// <summary>Sub-chapter(s), e.g. « 1 - Examens sanguins / a - Chimie courante »; empty when none.</summary>
    public string? SubChapter { get; set; }

    /// <summary>Year of the decree the act comes from: 2005, 2012 or 2016.</summary>
    public int DecreeYear { get; set; }

    /// <summary>An act withdrawn from the nomenclature stays readable for the analyses that cite it.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// A code as the nomenclature writes it: trimmed, upper case, no inner space (« b 118 » → « B118 »).
    /// Null for an empty entry.
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return string.Concat(code.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    }

    /// <summary>The decree an act year refers to, as the document names it.</summary>
    public static string DecreeReference(int year) => year switch
    {
        2005 => "Arrêté 1796-03 du 21 juillet 2005",
        2012 => "Arrêté 827-12 du 1er mars 2012",
        2016 => "Arrêté 1968-16 du 24 juin 2016",
        _ => year.ToString()
    };
}
