using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;
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

    private static readonly JsonLocalizer Localizer = new(new LocalizationOptions { MarkMissingKeys = true });

    private static string Fr(string key, params object?[] args) => Localizer.Format(AppLanguage.French, key, args);

    private static string En(string key, params object?[] args) => Localizer.Format(AppLanguage.English, key, args);

    private static string Ar(string key, params object?[] args) => Localizer.Format(AppLanguage.Arabic, key, args);

    /// <summary>The counts of the files, the tubes, the results and the reports read « 1 dossier », « 3 dossiers », not « dossier(s) ».</summary>
    [Fact]
    public void The_counts_of_the_files_tubes_results_and_reports_agree()
    {
        Fr("OrderEntry.TubeCount", 1, 2).Should().Be("1 tube · 2 étiquettes");
        Fr("Collection.PatientsAndTubes", 0, 1).Should().Be("0 patient · 1 tube");
        Fr("Order.ListCount", 12).Should().Be("12 dossiers");
        En("Order.ListCount", 1).Should().Be("1 order");
        Ar("Order.ListCount", 12).Should().Contain("عدد الملفات: ").And.Contain("12").And.NotContain("(");
        Fr("OrderEntry.PreviousOrders", 1, "12/10/2026").Should().Be("1 demande antérieure · dernière le 12/10/2026");
        Fr("Validation.PendingSummary", 1, 6).Should().Be("1 dossier · 6 résultats en attente");
        En("Validation.PendingSummary", 2, 1).Should().Be("2 files · 1 result pending");
        Fr("Validation.ContextAbnormal", 3).Should().Be("3 résultats anormaux à examiner");
        Fr("Validation.ContextAbnormal", 1).Should().Be("1 résultat anormal à examiner");
        Fr("Validation.Unvalidated", 2).Should().Be("2 résultats dévalidés");
        En("Validation.Unvalidated", 1).Should().Be("1 result unvalidated");
        Fr("Validation.CommentsNotSaved", 1).Should().StartWith("1 commentaire biologique saisi n'est pas encore enregistré : il l'est")
                                                 .And.EndWith("Quitter l'écran et l'abandonner ?");
        En("Validation.CommentsNotSaved", 2).Should().EndWith("Leave the screen and discard them?");
        Fr("Report.VersionCount", 1).Should().Be("1 version émise");
        Fr("Subcontract.SendSummary", 3, 1).Should().Be("3 analyses de 1 dossier à envoyer :");
        Fr("Subcontract.Sent", "ST/1, ST/2", 2).Should().Be("Envoyé — bordereaux ST/1, ST/2");
        En("Subcontract.Sent", "ST/1", 1).Should().Be("Sent — slip ST/1");
        Fr("Dashboard.OrdersRegisteredToday", "1", 1).Should().Be("1 dossier enregistré aujourd'hui");
        Fr("Dashboard.RangesPending", 2).Should().Be("2 paramètres attendent la validation de leurs valeurs usuelles");
        Fr("Patient.PossibleDuplicateNotice", 1, "X").Should().StartWith("Dossier possiblement en double : 1 trouvé — X.");
        Fr("Report_LatinFontMissingWarning", "Arial", 1).Should().StartWith("Police de compte rendu absente : Arial.");
        Fr("Print.RowCount", 0).Should().Be("0 ligne");
        En("Print.RowCount", 0).Should().Be("0 rows");
    }
}
