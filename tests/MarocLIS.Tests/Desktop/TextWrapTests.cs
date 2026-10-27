using FluentAssertions;
using MarocLIS.Desktop.Infrastructure;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// Captions of the workstation broken at their spaces only: Windows broke « Prélèvement
/// pédiatrique » into « Prélèvement pé | diatrique » and « N° Ordre des médecins » into « N° Ordre
/// des mé | decins ».
/// </summary>
public sealed class TextWrapTests
{
    // Seven pixels a character, whatever it is.
    private static int Measure(string text) => text.Length * 7;

    [Fact]
    public void AWord_IsNeverSplit()
    {
        var lines = TextWrap.Lines("Prélèvement pédiatrique", Measure, 7 * 15);

        lines.Should().Equal("Prélèvement", "pédiatrique");
        TextWrap.Lines("N° Ordre des médecins / pharmaciens", Measure, 7 * 15)
            .Should().Equal("N° Ordre des", "médecins /", "pharmaciens");
        TextWrap.Lines("Date limite de réponse", Measure, 7 * 17).Should().Equal("Date limite de", "réponse");
    }

    [Fact]
    public void AWordWiderThanTheLine_StaysWhole_OnItsOwnLine()
    {
        TextWrap.Lines("Secteurs autorisés", Measure, 7 * 6).Should().Equal("Secteurs", "autorisés");
    }

    [Fact]
    public void ACaptionThatFits_IsOneLine_AndLineBreaksAreKept()
    {
        TextWrap.Lines("Matricule", Measure, 200).Should().Equal("Matricule");
        TextWrap.Lines("Valeur\nassignée", Measure, 500).Should().Equal("Valeur", "assignée");
        TextWrap.Wrap("Rôles et droits", Measure, 7 * 9).Should().Be("Rôles et\ndroits");
        TextWrap.Lines(null, Measure, 100).Should().BeEmpty();
    }
}
