using FluentAssertions;
using MarocLIS.Domain.Common;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The plural token of the catalogues (<c>{0:plural:tube|tubes}</c>), read once in the domain so
/// the localizer, the audit trail's French reasons and the standard invoice agree with their
/// counts the same way.
/// </summary>
public sealed class PluralTextTests
{
    [Fact]
    public void French_takes_the_singular_below_two_english_and_arabic_only_at_one()
    {
        PluralText.French("Réception de {0} {0:plural:tube|tubes}", 0).Should().Be("Réception de 0 tube");
        PluralText.French("Réception de {0} {0:plural:tube|tubes}", 1).Should().Be("Réception de 1 tube");
        PluralText.French("Réception de {0} {0:plural:tube|tubes}", 3).Should().Be("Réception de 3 tubes");
        PluralText.French("{0} {0:plural:jour|jours}", 1.5m).Should().Be("1,5 jour");

        PluralText.Resolve("{0:plural:tube|tubes}", [0], AppLanguage.English).Should().Be("tubes");
        PluralText.Resolve("{0:plural:tube|tubes}", [1], AppLanguage.English).Should().Be("tube");
        PluralText.Resolve("{0:plural:الطلب|الطلبات}", [1], AppLanguage.Arabic).Should().Be("الطلب");
        PluralText.Resolve("{0:plural:الطلب|الطلبات}", [4], AppLanguage.Arabic).Should().Be("الطلبات");
    }

    [Fact]
    public void Each_count_agrees_on_its_own_and_a_text_that_is_not_a_number_takes_the_last_form()
    {
        PluralText.French("{0} {0:plural:ligne importée|lignes importées} ; {1} déjà {1:plural:présente|présentes}", 4, 1)
            .Should().Be("4 lignes importées ; 1 déjà présente");
        PluralText.French("{1:plural:Demande|Demandes} : {0}", "2610-0001", 1).Should().Be("Demande : 2610-0001");
        PluralText.Resolve("{0:plural:tube|tubes}", ["3"], AppLanguage.French).Should().Be("tubes");
        PluralText.Resolve("{0:plural:tube|tubes}", [], AppLanguage.French).Should().Be("tubes");
        PluralText.Resolve("Sans jeton {0}", [1], AppLanguage.French).Should().Be("Sans jeton {0}");
    }
}
