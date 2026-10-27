using System.Text.Json;
using System.Text.RegularExpressions;
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
        PluralText.Resolve("{0:plural:jour|jours}", [1.5m], AppLanguage.French).Should().Be("jour");
        PluralText.Resolve("{0:plural:day|days}", [1.5m], AppLanguage.English).Should().Be("days");

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

    /// <summary>Invoices, batches, statement lines and entries: « 1 facture sélectionnée », « 2 lignes importées ; 1 déjà présente ».</summary>
    [Fact]
    public void The_counts_of_billing_treasury_and_accounting_agree()
    {
        Fr("Claim.SelectionSummary", 1, "120,00 DH").Should().Be("1 facture sélectionnée — 120,00 DH");
        En("Claim.SelectionSummary", 3, "x").Should().Be("3 invoices selected — x");
        Fr("Claim.PartiallySubmitted", "B-1", 3, 1).Should().Be("Bordereau B-1 transmis avec 3 factures. 1 facture n'a pas été transmise :");
        En("Claim.PartiallySubmitted", "B-1", 1, 2).Should().Be("Batch B-1 sent with 1 invoice. 2 invoices were not sent:");
        En("Claim.BatchSummary", 2, "x").Should().StartWith("2 batches");
        Fr("Treasury.Imported", 2, 1, 0).Should().Be("2 lignes importées ; 1 déjà présente, 0 illisible.");
        Fr("Treasury.ImportWithInvalid", 1).Should().Be("1 ligne illisible ne sera pas importée. Importer les autres ?");
        Ar("Treasury.ImportWithInvalid", 4).Should().NotContain("(أسطر)").And.Contain("4");
        Fr("Treasury.CardBatchLabel", 1).Should().Be("1 paiement par carte");
        Fr("Accounting.Balanced", 1).Should().StartWith("1 écriture, équilibrée et affectée à un compte");
        En("Accounting.Balanced", 5).Should().StartWith("5 entries, all balanced");
        Fr("Accounting_ExportRefused", 1).Should().Contain("1 anomalie à corriger").And.EndWith("Elle est listée ci-dessous.");
        Fr("Deposit.RefundedPartly", "2610-1", 1, 2).Should().StartWith("Dossier 2610-1 : 1 acompte remboursé sur 2.");
        Fr("Sequence_FormatFrozenAfterFirstUse", 1L).Should().Contain("1 numéro a déjà été attribué");
        Fr("InvoicePrint.Orders", "2610-1, 2610-2", 2).Should().Be("Demandes : 2610-1, 2610-2");
        En("InvoicePrint.Orders", "2610-1", 1).Should().Be("Order: 2610-1");
    }

    /// <summary>Stock, purchasing, instruments, catalogue and quality: « 1 lot à compter », « 2 trames supprimées ».</summary>
    [Fact]
    public void The_counts_of_stock_instruments_catalogue_and_quality_agree()
    {
        Fr("InventoryCount.Started", 1).Should().Be("1 lot à compter.");
        Fr("InventoryCount_LinesNotCounted", 1).Should().Be("1 lot n'est pas encore compté : comptez-le (0 s'il est absent) avant de clôturer.");
        Fr("InventoryCount_LinesNotCounted", 3).Should().Be("3 lots ne sont pas encore comptés : comptez-les (0 s'ils sont absents) avant de clôturer.");
        En("InventoryCount_LinesNotCounted", 1).Should().Be("1 lot is not counted yet: count it (0 if absent) before closing.");
        Fr("Consumption.DeadVolume", "1", 1m).Should().Be("Volume mort (1 ouverture)");
        En("Consumption.DeadVolume", "4", 4m).Should().Be("Dead volume (4 openings)");
        Fr("SupplierInvoices.StillBlocked", 1).Should().Be("La facture reste bloquée : 1 écart.");
        Fr("Quotes.AwardMissingItems", 2).Should().Contain("2 articles ; ils ne figureront pas");
        Fr("Interface.PurgeLogScope", 1, "01/04/2026", 3).Should()
            .Be("1 trame antérieure au 01/04/2026 sera supprimée. 3 trames sont conservées parce qu'elles restent réimportables.");
        En("Interface.PurgeLogRetained", 1).Should().Be("1 frame kept despite its age: it is still replayable. Deal with it from the log.");
        Fr("Interface.MappingSummary", 7, 1).Should().Be("7 correspondances. 1 code retiré, conservé pour relire les anciens messages.");
        Fr("Catalog.NomenclatureInUseNotice", 1).Should().EndWith("tant qu'il s'y réfère.");
        Fr("Test_HasOpenItemsCannotDeactivate", 2).Should().StartWith("2 analyses en cours attendent encore un résultat");
        Fr("Qc_PatientResultsFlagged", 1).Should().StartWith("1 résultat patient non diffusé a été signalé : sa validation");
        Fr("Quality.RuleCount", 1).Should().Be("1 règle active.");
        Fr("StorageLocations.DeactivateConfirm", "Frigo", 1).Should().EndWith("1 tube rangé ici garde ce lieu.");
        En("StorageLocations.DeactivateConfirm", "Fridge", 5).Should().EndWith("5 tubes stored there keep this location.");
    }

    /// <summary>Staff, relations, administration and the data take-over: « 1 bulletin », « 0,5 jour ouvrable décompté ».</summary>
    [Fact]
    public void The_counts_of_staff_relations_administration_and_take_over_agree()
    {
        Fr("Leave.WorkingDays", "0,5", 0.5m).Should().Be("0,5 jour ouvrable décompté");
        Fr("Leave.WorkingDays", "3", 3m).Should().Be("3 jours ouvrables décomptés");
        En("Leave.CalendarDays", "1", 1m).Should().Be("1 calendar day");
        Fr("Leave.AwayWarning", 1, "BIO").Should().Be("1 collègue du service BIO absent sur une partie de ces jours");
        Fr("Payroll.Updated", 1).Should().StartWith("1 nouveau bulletin ;");
        Fr("Payroll.Updated", 4).Should().StartWith("4 nouveaux bulletins ;");
        Fr("Planning.CopyWeekDone", 3, 1).Should().Be("3 postes recopiés, 1 ignoré.");
        En("QualityHr.Planning.Count", 1, 2, 0).Should().Be("1 person · 2 shifts · 0 points to check");
        Fr("Accident.Heading", 1, 0, 2).Should().Be("1 accident cette année · 0 dossier en cours · 2 déclarations en retard");
        En("Settlement.NoticeMonths", 1).Should().Be("1 month");
        Fr("Messaging.CampaignOutcome", 2, 1, 0).Should().Be("2 envoyés, 1 en échec, 0 ignoré.");
        Fr("Admin.ConfirmPermissionChange", "BIO", 1, 2, 1).Should()
            .Be("Rôle BIO : 1 droit ajouté, 2 droits retirés. La modification s'applique immédiatement à 1 utilisateur actif. Confirmer ?");
        Fr("Migration.Undone", 1, 2).Should().Be("1 enregistrement retiré ; 2 conservés car utilisés depuis.");
        Fr("Migration.TablesListed", 1).Should().Be("1 table ou vue trouvée : choisissez celle à lire.");
        En("Migration.RowsRead", 1, 9).Should().Be("1 row, 9 columns — the first 200 shown");
    }

    /// <summary>
    /// The texts that may keep a bracket the guard below takes for a pseudo-plural, each with its
    /// reason. Anything else written « tube(s) », « entr(y/ies) » or « ملف(ات) » is a count to agree
    /// with the plural token, or a heading to write in the plural.
    /// </summary>
    private static readonly Dictionary<string, string> PseudoPluralAllowed = new(StringComparer.Ordinal)
    {
        ["Interface.AckTimeout"] = "« (s) » is the unit, seconds, not a plural",
        ["FamilyStatus.Widowed"] = "« (ة) » is the feminine form of the status, not a plural",
        ["FamilyStatus.Divorced"] = "« (ة) » is the feminine form of the status, not a plural",
        ["FamilyStatus.Married"] = "« (ة) » is the feminine form of the status, not a plural"
    };

    [Theory]
    [InlineData("fr", @"\((s|es|x|ux|ies|y/ies)\)")]
    [InlineData("en", @"\((s|es|ies|y/ies)\)")]
    [InlineData("ar", @"[\u0600-\u06FF]\((ات|ين|ون|أسطر|ة|اختلالات|قيود|أصناف|ًا)\)|\{\d+\}[^.،:{]{0,25}\((نتائج|ملفات|قواعد|صلاحيات|أرقام|أيام|فواتير|فوارق|تعريفات|معايير|مواضيع|تقارير)\)")]
    public void No_catalogue_text_writes_a_pseudo_plural_outside_the_allowed_list(string language, string pattern)
    {
        var path = Path.Combine(MarocLIS.Tests.Web.WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Localization", "Resources", language + ".json");
        using var catalogue = JsonDocument.Parse(File.ReadAllText(path));
        var found = new List<string>();

        void Walk(JsonElement element, string prefix)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                if (property.Value.ValueKind == JsonValueKind.Object) Walk(property.Value, key);
                else if (property.Value.ValueKind == JsonValueKind.String && key != "$comment"
                         && Regex.IsMatch(property.Value.GetString()!, pattern) && !PseudoPluralAllowed.ContainsKey(key))
                    found.Add($"{key}: {property.Value.GetString()}");
            }
        }

        Walk(catalogue.RootElement, "");
        found.Should().BeEmpty($"a count in {language}.json agrees with {{0:plural:…|…}} instead of « (s) »");
        PseudoPluralAllowed.Keys.Should().OnlyContain(k => Lookup(catalogue.RootElement, k) != null,
                                                      "an exception whose text is gone leaves the list");
    }

    private static string? Lookup(JsonElement root, string key)
    {
        var element = root;
        foreach (var part in key.Split('.'))
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element)) return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
