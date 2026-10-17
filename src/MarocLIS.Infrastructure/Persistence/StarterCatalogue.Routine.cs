using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// The rest of the starter catalogue: the analyses a Moroccan routine laboratory is asked for every
/// week beyond the core of <see cref="StarterCatalogue.SeedAsync"/> — biochemistry, haematology,
/// haemostasis, immunology and tumour markers, hormonology, serology, bacteriology, urine.
/// <para>
/// Seeded on a new installation only, with the rest of the catalogue (no analysis in the database
/// yet): a laboratory already in use never sees one of these appear at start-up.
/// </para>
/// <para>
/// <b>Valeurs indicatives de départ, à valider par le biologiste.</b> The reference intervals are
/// conventional adult values of the usual sources — Tietz Fundamentals of Clinical Chemistry
/// (reference intervals appendix), the inserts of the routine immunoassay and chemistry reagents in
/// wide use in Morocco (Roche cobas / Elecsys, IFCC methods at 37 °C), WHO, ADA and KDIGO
/// thresholds — in the conventional units Moroccan laboratories report in. Each one carries
/// <c>Source = "À vérifier par le laboratoire"</c>, so the catalogue's set-up check lists it until the
/// biologist confirms it against the laboratory's own methods and population (ISO 15189 §7.3.6
/// / §5.5.2). Where an interval depends on the menstrual phase, the assay or the clinical context,
/// it is seeded as a display text (no flag) rather than as limits that would mis-flag.
/// </para>
/// <para>
/// The act of the national nomenclature (NABM, <c>nomenclature-biologie.csv</c>) is set where one
/// act clearly covers the analysis; its coefficient is then the official one, read from the shipped
/// nomenclature. Where none clearly does, the act is left empty for the laboratory to decide and
/// the coefficient stated is the nearest act's, as a starting price. These codes are deliberately
/// kept out of <see cref="StarterCatalogue.ActCodes"/>: that table drives an upgrade step that
/// rewrites the act of analyses with these codes in databases already in use, and a laboratory's
/// own « FER » or « CA125 » must not be touched.
/// </para>
/// </summary>
internal static partial class StarterCatalogue
{
    /// <summary>The routine analyses of a new installation, codes and acts as the CSV review lists them.</summary>
    private static List<AnalysisTest> RoutineAnalyses(Dictionary<string, Guid> departments,
                                                      Dictionary<string, Guid> specimens,
                                                      Dictionary<string, Guid> containers,
                                                      Dictionary<string, Guid> units,
                                                      DateOnly validFrom)
    {
        var acts = NationalBiologyActSeed.Rows.ToDictionary(r => r.Code, r => r.Coefficient, StringComparer.Ordinal);
        var tests = new List<AnalysisTest>();

        Guid Unit(string code) => units[code];

        // An analysis billed under one national act takes that act's coefficient; one left without
        // an act states its own.
        AnalysisTest Test(string code, string fr, string ar, string en, string department, string specimen,
                          string container, string method, int hours, string? act, decimal? coefficient = null)
        {
            var test = New(code, fr, ar, en, departments[department], specimens[specimen], containers[container],
                           method, hours, coefficient ?? acts[act!], validFrom, nationalActCode: act);
            tests.Add(test);
            return test;
        }

        // The common case: one analysis, one numeric parameter.
        TestParameter Single(string code, string fr, string ar, string en, string department, string specimen,
                             string container, string method, int hours, string? act,
                             string parameterCode, string unit, int decimals, params ReferenceRange[] ranges)
        {
            var test = Test(code, fr, ar, en, department, specimen, container, method, hours, act);
            var p = Param(parameterCode, fr, ar, en, Unit(unit), decimals);
            foreach (var range in ranges) p.AddReferenceRange(range);
            test.AddParameter(p);
            return p;
        }

        // A serology or immunohaematology answer: négatif / positif / douteux.
        TestParameter Qualitative(AnalysisTest test, string code, string fr, string ar, string en,
                                  bool positiveIsAbnormal = true)
        {
            var p = Param(code, fr, ar, en, null, decimals: 0);
            p.ValueType = ResultValueType.Coded;
            p.AddCodedOption(Option("NEGATIF", "Négatif", "سلبي", "Negative", expectedNormal: true));
            p.AddCodedOption(Option("POSITIF", "Positif", "إيجابي", "Positive", abnormal: positiveIsAbnormal));
            p.AddCodedOption(Option("DOUTEUX", "Douteux — à contrôler", "مشكوك فيه — يعاد",
                                    "Equivocal — repeat", abnormal: true));
            test.AddParameter(p);
            return p;
        }

        TestParameter Text(AnalysisTest test, string code, string fr, string ar, string en)
        {
            var p = Param(code, fr, ar, en, null, decimals: 0);
            p.ValueType = ResultValueType.Text;
            test.AddParameter(p);
            return p;
        }

        const string bioch = Department.Codes.Biochemistry;
        const string hema = Department.Codes.Haematology;
        const string hemost = Department.Codes.Haemostasis;
        const string immuno = Department.Codes.Immunology;
        const string hormo = Department.Codes.Hormonology;
        const string sero = Department.Codes.Serology;
        const string bacte = Department.Codes.Bacteriology;
        const string parasito = Department.Codes.Parasitology;
        const string urine = Department.Codes.UrineAnalysis;

        const string serum = SpecimenType.Codes.SerumDryTube;
        const string edta = SpecimenType.Codes.WholeBloodEdta;
        const string citrate = SpecimenType.Codes.PlasmaCitrate;
        const string fluoride = SpecimenType.Codes.PlasmaFluoride;
        const string urineSpot = SpecimenType.Codes.Urine;
        const string urine24 = SpecimenType.Codes.Urine24H;
        const string stool = SpecimenType.Codes.Stool;

        const string immunoassay = "Immunoessai par chimiluminescence";

        // ---- Biochemistry ---------------------------------------------------------------------
        // Tietz; Roche cobas inserts (UA2, CA2, PHOS2, MG, TP2, ALB2, BILT3/BILD2, ALP2, GGT-2,
        // LDHI2, CKL, AMYL2, LIPC, IRON2), IFCC methods at 37 °C.
        // The parameter codes are the ones the COBAS INTEGRA 400 plus test table of the
        // demonstration looks for (AU, CA, PHOS, MG, PROT, ALB, BILT, BILD, PAL, GGT, LDH, CK, AMY,
        // LIP, FER), so an INTEGRA link maps them without retyping.

        Single("AC_URIQUE", "Acide urique (uricémie)", "حمض البوليك في الدم", "Serum uric acid",
               bioch, serum, "TUBE_GEL", "Uricase / peroxydase", 6, "B100", "AU", "MG_L", 0,
               Adult(34m, 70m, Gender.Male), Adult(24m, 57m, Gender.Female));

        {
            var ca = Single("CALCEMIE", "Calcémie", "الكالسيوم في الدم", "Serum calcium",
                            bioch, serum, "TUBE_GEL", "Colorimétrie (NM-BAPTA / o-crésolphtaléine)", 6, "B104",
                            "CA", "MG_L", 0, Adult(86m, 102m));
            // 65 and 130 mg/L ≈ 1.6 and 3.25 mmol/L, the usual alert thresholds.
            ca.CriticalLow = 65m; ca.CriticalHigh = 130m;
        }

        // Children run higher phosphate (Tietz paediatric intervals).
        Single("PHOSPHORE", "Phosphorémie", "الفوسفور في الدم", "Serum phosphate",
               bioch, serum, "TUBE_GEL", "Phosphomolybdate UV", 6, "B129", "PHOS", "MG_L", 0,
               Adult(25m, 45m), Years(38m, 65m, 1, 4), Years(37m, 56m, 4, 12));

        Single("MAGNESIUM", "Magnésémie", "المغنيزيوم في الدم", "Serum magnesium",
               bioch, serum, "TUBE_GEL", "Colorimétrie (bleu de xylidyle)", 6, "B126", "MG", "MG_L", 0,
               Adult(16m, 26m));

        Single("PROTIDES", "Protidémie", "البروتينات الكلية في الدم", "Total serum protein",
               bioch, serum, "TUBE_GEL", "Biuret", 6, "B130", "PROT", "G_L", 0,
               Adult(64m, 83m));

        // B101 names the immunological method; a routine albumin is measured with bromocresol green,
        // so the act is left for the laboratory to decide. Coefficient of B101.
        {
            var test = Test("ALBUMINE", "Albuminémie", "الألبومين في الدم", "Serum albumin",
                            bioch, serum, "TUBE_GEL", "Vert de bromocrésol", 6, act: null, coefficient: 60m);
            var p = Param("ALB", "Albumine", "الألبومين", "Albumin", Unit("G_L"), decimals: 0);
            p.AddReferenceRange(Adult(35m, 52m));
            test.AddParameter(p);
        }

        // Bilirubin: from one month of age only. A newborn's jaundice is read on the hour-specific
        // nomogram (Bhutani), not against an adult upper limit.
        {
            var test = Test("BILIRUBINE", "Bilirubine totale et directe", "البيليروبين الكلي والمباشر",
                            "Total and direct bilirubin", bioch, serum, "TUBE_GEL", "Diazoréaction", 6, "B103");
            test.StabilityAtRoomTemperatureHours = 8;

            var total = Param("BILT", "Bilirubine totale", "البيليروبين الكلي", "Total bilirubin",
                              Unit("MG_L"), decimals: 1);
            total.AddReferenceRange(Months(null, 12m, 1));
            test.AddParameter(total);

            var direct = Param("BILD", "Bilirubine directe (conjuguée)", "البيليروبين المباشر",
                               "Direct (conjugated) bilirubin", Unit("MG_L"), decimals: 1);
            direct.AddReferenceRange(Months(null, 3m, 1));
            test.AddParameter(direct);

            var indirect = Param("BILI", "Bilirubine indirecte (libre)", "البيليروبين غير المباشر",
                                 "Indirect (unconjugated) bilirubin", Unit("MG_L"), decimals: 1);
            indirect.CalculationFormula = "round([BILT]-[BILD];1)";
            test.AddParameter(indirect);
        }

        // Alkaline phosphatase is two to four times higher in a growing child: adult limits only.
        Single("PAL", "Phosphatases alcalines", "الفوسفاتاز القلوية", "Alkaline phosphatase",
               bioch, serum, "TUBE_GEL", "IFCC 37 °C", 6, "B143", "PAL", "UI_L", 0,
               Adult(40m, 129m, Gender.Male), Adult(35m, 104m, Gender.Female));

        Single("GGT", "Gamma-glutamyl transférase (gamma GT)", "غاما غلوتاميل ترانسفيراز",
               "Gamma-glutamyl transferase", bioch, serum, "TUBE_GEL", "IFCC 37 °C", 6, "B141", "GGT", "UI_L", 0,
               Adult(10m, 71m, Gender.Male), Adult(6m, 42m, Gender.Female));

        Single("LDH", "Lactate déshydrogénase (LDH)", "نازعة هيدروجين اللاكتات", "Lactate dehydrogenase",
               bioch, serum, "TUBE_GEL", "IFCC 37 °C", 6, "B142", "LDH", "UI_L", 0,
               Adult(135m, 225m, Gender.Male), Adult(135m, 214m, Gender.Female));

        Single("CPK", "Créatine kinase (CPK)", "كيناز الكرياتين", "Creatine kinase",
               bioch, serum, "TUBE_GEL", "IFCC 37 °C (NAC)", 6, "B138", "CK", "UI_L", 0,
               Adult(null, 190m, Gender.Male), Adult(null, 170m, Gender.Female));

        Single("AMYLASE", "Amylasémie", "الأميلاز في الدم", "Serum amylase",
               bioch, serum, "TUBE_GEL", "IFCC 37 °C (CNPG3)", 4, "B137", "AMY", "UI_L", 0,
               Adult(28m, 100m));

        Single("LIPASE", "Lipasémie", "الليباز في الدم", "Serum lipase",
               bioch, serum, "TUBE_GEL", "Colorimétrie enzymatique", 4, "B148", "LIP", "UI_L", 0,
               Adult(13m, 60m));

        Single("FER", "Fer sérique", "الحديد في المصل", "Serum iron",
               bioch, serum, "TUBE_GEL", "Ferrozine", 6, "B115", "FER", "UMOL_L", 1,
               Adult(11.6m, 31.3m, Gender.Male), Adult(9.0m, 30.4m, Gender.Female));

        Single("FERRITINE", "Ferritine", "الفيريتين", "Ferritin",
               bioch, serum, "TUBE_GEL", immunoassay, 24, "B154", "FERRIT", "UG_L", 0,
               Adult(30m, 400m, Gender.Male), Adult(13m, 150m, Gender.Female));

        Single("RA", "Réserve alcaline (bicarbonates)", "الاحتياطي القلوي (البيكربونات)",
               "Total CO2 (bicarbonate)", bioch, serum, "TUBE_GEL", "Enzymatique (PEP carboxylase)", 4,
               "B132", "HCO3", "MMOL_L", 0, Adult(22m, 29m));

        {
            var p = Single("GLY_PP", "Glycémie postprandiale (2 h)", "سكر الدم بعد الأكل (ساعتان)",
                           "Postprandial glucose (2 h)", bioch, fluoride, "TUBE_FLUORURE", "Hexokinase", 4,
                           "B118", "GLY_PP", "G_L", 2, Threshold(Range(null, 1.40m)));
            p.CriticalLow = 0.40m; p.CriticalHigh = 4.00m;
        }

        // Oral glucose tolerance test, 75 g. WHO / ADA thresholds outside pregnancy; IADPSG
        // thresholds in pregnancy (gestational diabetes: one value at or above is enough).
        {
            var test = Test("HGPO", "Hyperglycémie provoquée par voie orale (75 g)",
                            "اختبار تحمل الجلوكوز الفموي (75 غ)", "Oral glucose tolerance test (75 g)",
                            bioch, fluoride, "TUBE_FLUORURE", "Hexokinase", 6, "B206");
            test.FastingHoursOverride = 8;
            test.RequiredClinicalInformation = new MultilingualText(
                "Grossesse (terme) ? Charge de 75 g ingérée à quelle heure ?",
                "حمل (المدة)؟ في أي ساعة تم تناول 75 غ؟",
                "Pregnancy (gestational age)? Time the 75 g load was taken?");

            (string Code, string Fr, string Ar, string En, decimal? Usual, decimal Pregnancy)[] times =
            [
                ("HGPO_T0",   "Glycémie à jeun (T0)", "سكر الدم على الصيام (0 د)",  "Fasting glucose (T0)", 1.00m, 0.92m),
                ("HGPO_T60",  "Glycémie à 60 min",     "سكر الدم بعد 60 دقيقة",     "Glucose at 60 min",    null,  1.80m),
                ("HGPO_T120", "Glycémie à 120 min",    "سكر الدم بعد 120 دقيقة",    "Glucose at 120 min",   1.40m, 1.53m)
            ];

            foreach (var t in times)
            {
                var p = Param(t.Code, t.Fr, t.Ar, t.En, Unit("G_L"), decimals: 2);
                if (t.Usual is { } usual)
                {
                    var outside = Threshold(Range(null, usual, Gender.Unknown));
                    outside.AppliesToPregnancy = false;
                    p.AddReferenceRange(outside);
                }
                var pregnant = Threshold(Range(null, t.Pregnancy, Gender.Female));
                pregnant.AppliesToPregnancy = true;
                p.AddReferenceRange(pregnant);
                test.AddParameter(p);
            }
        }

        Single("FRUCTOSAMINE", "Fructosamine", "الفركتوزامين", "Fructosamine",
               bioch, serum, "TUBE_GEL", "Nitrobleu de tétrazolium", 24, "B117", "FRUCTO", "UMOL_L", 0,
               Adult(205m, 285m));

        // Serum protein electrophoresis, as fractions in per cent (agarose gel / capillary).
        {
            var test = Test("EPP", "Électrophorèse des protéines sériques", "الرحلان الكهربائي لبروتينات المصل",
                            "Serum protein electrophoresis", bioch, serum, "TUBE_GEL",
                            "Électrophorèse capillaire / gel d'agarose", 48, "B114");

            (string Code, string Fr, string Ar, string En, decimal Low, decimal High)[] fractions =
            [
                ("EP_ALB",  "Albumine",          "الألبومين",          "Albumin",          55.8m, 66.1m),
                ("EP_A1",   "Alpha-1 globulines", "غلوبولينات ألفا 1", "Alpha-1 globulins", 2.9m,  4.9m),
                ("EP_A2",   "Alpha-2 globulines", "غلوبولينات ألفا 2", "Alpha-2 globulins", 7.1m, 11.8m),
                ("EP_BETA", "Bêta globulines",    "غلوبولينات بيتا",   "Beta globulins",    8.4m, 13.1m),
                ("EP_GAMMA","Gamma globulines",   "غلوبولينات غاما",   "Gamma globulins",  11.1m, 18.8m)
            ];

            foreach (var f in fractions)
            {
                var p = Param(f.Code, f.Fr, f.Ar, f.En, Unit("PERCENT"), decimals: 1);
                p.AddReferenceRange(Adult(f.Low, f.High));
                test.AddParameter(p);
            }

            Text(test, "EP_COMMENT", "Interprétation du profil", "تفسير المنحنى", "Pattern interpretation");
        }

        // Cardiac markers: decision thresholds, assay-dependent. hs-cTnT 99th percentile (ESC);
        // NT-proBNP rule-out of heart failure (ESC 2021, by age).
        Single("TROPONINE", "Troponine ultrasensible", "التروبونين عالي الحساسية", "High-sensitivity troponin",
               bioch, serum, "TUBE_GEL", "Troponine T hs — immunoessai", 2, "B149", "TROPO", "PG_ML", 1,
               Note(Threshold(Range(null, 14m)),
                    "< 14 pg/mL (ng/L) : 99e percentile hs-TnT ; seuil propre à la trousse",
                    "أقل من 14 بيكوغرام/مل : المئين 99 ؛ حسب الكاشف",
                    "< 14 pg/mL (ng/L): hs-TnT 99th percentile; assay-specific"));

        Single("NTPROBNP", "NT-proBNP", "NT-proBNP", "NT-proBNP",
               bioch, serum, "TUBE_GEL", immunoassay, 4, "B455", "NTPROBNP", "PG_ML", 0,
               Threshold(Years(null, 125m, 0, 75)), Threshold(Years(null, 450m, 75, null)));

        Single("PCT", "Procalcitonine", "البروكالسيتونين", "Procalcitonin",
               bioch, serum, "TUBE_GEL", immunoassay, 4, "B456", "PCT", "NG_ML", 2,
               Note(Threshold(Range(null, 0.50m)),
                    "< 0,5 ng/mL : infection bactérienne systémique peu probable ; ≥ 2 ng/mL : probable",
                    "أقل من 0,5 : عدوى جرثومية جهازية غير مرجحة ؛ 2 أو أكثر : مرجحة",
                    "< 0.5 ng/mL: systemic bacterial infection unlikely; ≥ 2 ng/mL: likely"));

        // KDIGO albuminuria categories.
        Single("MICROALB", "Microalbuminurie des 24 heures", "الألبومين الدقيق في بول 24 ساعة",
               "24-hour urine albumin", bioch, urine24, "POT_URINE", "Immunoturbidimétrie", 24, "B482",
               "MALB", "MG_24H", 0,
               Note(Threshold(Range(null, 30m)),
                    "< 30 mg/24 h : normale (A1) ; 30–300 : microalbuminurie (A2) ; > 300 : (A3)",
                    "أقل من 30 : عادي (A1) ؛ 30–300 : بيلة ألبومينية دقيقة (A2) ؛ أكثر من 300 : (A3)",
                    "< 30 mg/24 h: normal (A1); 30–300: moderately increased (A2); > 300: (A3)"));

        Single("PROT_24H", "Protéinurie des 24 heures", "البروتين في بول 24 ساعة", "24-hour urine protein",
               bioch, urine24, "POT_URINE", "Turbidimétrie (chlorure de benzéthonium)", 24, "B177",
               "PROT24", "G_24H", 2, Adult(null, 0.15m));

        Single("CALCIURIE", "Calciurie des 24 heures", "الكالسيوم في بول 24 ساعة", "24-hour urine calcium",
               bioch, urine24, "POT_URINE", "Colorimétrie", 24, "B168", "CA_U24", "MG_24H", 0,
               Adult(null, 300m, Gender.Male), Adult(null, 250m, Gender.Female));

        // Vitamins. 25-OH vitamin D: Endocrine Society categories. Two acts of the nomenclature name
        // it (B384 « 25 Hydroxy cholecalciférol », B439 « Vitamine D », both B 450): the laboratory
        // chooses; coefficient 450.
        {
            var test = Test("VIT_D", "Vitamine D (25-OH)", "فيتامين د (25-OH)", "25-hydroxyvitamin D",
                            bioch, serum, "TUBE_GEL", immunoassay, 48, act: null, coefficient: 450m);
            var p = Param("VITD", "25-OH vitamine D", "25-هيدروكسي فيتامين د", "25-OH vitamin D",
                          Unit("NG_ML"), decimals: 1);
            p.AddReferenceRange(Note(Threshold(Range(30m, 100m)),
                "< 20 ng/mL : carence ; 20–29 : insuffisance ; 30–100 : valeurs souhaitables",
                "أقل من 20 : نقص ؛ 20–29 : عدم كفاية ؛ 30–100 : قيم مرغوبة",
                "< 20 ng/mL: deficiency; 20–29: insufficiency; 30–100: desirable"));
            test.AddParameter(p);
        }

        Single("VIT_B12", "Vitamine B12", "فيتامين ب12", "Vitamin B12",
               bioch, serum, "TUBE_GEL", immunoassay, 48, "B438", "B12", "PG_ML", 0,
               Adult(197m, 771m));

        Single("FOLATES", "Folates sériques (vitamine B9)", "حمض الفوليك في المصل (فيتامين ب9)",
               "Serum folate (vitamin B9)", bioch, serum, "TUBE_GEL", immunoassay, 48, "B485", "FOL", "NG_ML", 1,
               Adult(3.9m, 26.8m));

        // ---- Haematology ----------------------------------------------------------------------

        Single("RETIC", "Réticulocytes", "الخلايا الشبكية", "Reticulocytes",
               hema, edta, "TUBE_EDTA", "Cytométrie de flux", 6, "B222", "RETIC", "GIGA_L", 0,
               Adult(25m, 100m));

        // HbF reaches the adult level during the second year: limits from 2 years of age.
        {
            var test = Test("EHB", "Électrophorèse de l'hémoglobine", "الرحلان الكهربائي للهيموغلوبين",
                            "Haemoglobin electrophoresis", hema, edta, "TUBE_EDTA",
                            "Électrophorèse capillaire", 72, "B112");

            var a = Param("HBA_EP", "Hémoglobine A", "الهيموغلوبين A", "Haemoglobin A", Unit("PERCENT"), decimals: 1);
            a.AddReferenceRange(Years(96.8m, 97.8m, 2, null));
            test.AddParameter(a);

            var a2 = Param("HBA2", "Hémoglobine A2", "الهيموغلوبين A2", "Haemoglobin A2", Unit("PERCENT"), decimals: 1);
            a2.AddReferenceRange(Years(2.2m, 3.2m, 2, null));
            test.AddParameter(a2);

            var f = Param("HBF", "Hémoglobine F", "الهيموغلوبين F", "Haemoglobin F", Unit("PERCENT"), decimals: 1);
            f.AddReferenceRange(Years(null, 1.0m, 2, null));
            test.AddParameter(f);

            Text(test, "HB_VARIANT", "Fraction anormale / conclusion", "جزء غير طبيعي / خلاصة",
                 "Abnormal fraction / conclusion");
        }

        // ---- Haemostasis ----------------------------------------------------------------------

        {
            var p = Single("FIBRINOGENE", "Fibrinogène", "الفيبرينوجين", "Fibrinogen",
                           hemost, citrate, "TUBE_CITRATE", "Von Clauss (chronométrique)", 4, "B233",
                           "FIB", "G_L", 2, Range(2.00m, 4.00m));
            p.CriticalLow = 1.00m;
        }

        // Activated partial thromboplastin time: the seconds depend on the reagent, so only the
        // patient / control ratio carries a limit.
        {
            var test = Test("TCA", "Temps de céphaline activée (TCA)", "زمن الثرومبوبلاستين الجزئي المنشط",
                            "Activated partial thromboplastin time", hemost, citrate, "TUBE_CITRATE",
                            "Chronométrique — céphaline + activateur", 4, "B239");
            test.StabilityAtRoomTemperatureHours = 4;

            test.AddParameter(Param("TCA_P", "TCA du patient", "زمن المريض", "Patient APTT", Unit("SECONDS"), decimals: 1));
            test.AddParameter(Param("TCA_T", "TCA du témoin", "زمن الشاهد", "Control APTT", Unit("SECONDS"), decimals: 1));

            var ratio = Param("TCA_R", "Rapport patient / témoin", "نسبة المريض / الشاهد", "Patient / control ratio",
                              Unit("RATIO"), decimals: 2);
            ratio.CalculationFormula = "round([TCA_P]/[TCA_T];2)";
            ratio.AddReferenceRange(Range(null, 1.20m));
            test.AddParameter(ratio);
        }

        // D-dimers: exclusion threshold, age-adjusted after 50 (age × 10 µg/L).
        Single("DDIMERES", "D-dimères", "دي-دايمر", "D-dimer",
               hemost, citrate, "TUBE_CITRATE", "Immunoturbidimétrie", 2, "B494", "DDIM", "UG_L", 0,
               Note(Threshold(Range(null, 500m)),
                    "< 500 µg/L FEU ; après 50 ans, seuil d'exclusion = âge × 10 µg/L",
                    "أقل من 500 ميكروغرام/ل ؛ بعد 50 سنة : العتبة = السن × 10",
                    "< 500 µg/L FEU; over 50, exclusion threshold = age × 10 µg/L"));

        // ---- Immunohaematology ----------------------------------------------------------------

        Qualitative(Test("RAI", "Recherche d'agglutinines irrégulières (RAI)", "البحث عن الأجسام المضادة غير المنتظمة",
                         "Irregular antibody screen", immuno, edta, "TUBE_EDTA",
                         "Test indirect à l'antiglobuline — panel d'hématies tests", 24, "B225"),
                    "RAI", "RAI", "الأجسام المضادة غير المنتظمة", "Antibody screen");

        Qualitative(Test("COOMBS_D", "Test de Coombs direct", "اختبار كومبس المباشر", "Direct antiglobulin test",
                         immuno, edta, "TUBE_EDTA", "Antiglobuline polyspécifique — carte gel", 24, "B224"),
                    "COOMBS_D", "Coombs direct", "كومبس المباشر", "Direct Coombs");

        // ---- Immunology, auto-immunity and tumour markers ------------------------------------

        {
            var test = Test("AAN", "Anticorps antinucléaires", "الأجسام المضادة للنواة", "Antinuclear antibodies",
                            immuno, serum, "TUBE_GEL", "Immunofluorescence indirecte sur cellules HEp-2", 72, "B347");
            Qualitative(test, "AAN", "Anticorps antinucléaires", "الأجسام المضادة للنواة", "Antinuclear antibodies");
            Text(test, "AAN_ASPECT", "Titre et aspect de fluorescence", "العيار ونمط التألق", "Titre and pattern");
        }

        Qualitative(Test("FR_LATEX", "Facteurs rhumatoïdes (test au latex)", "العوامل الروماتويدية (اختبار اللاتكس)",
                         "Rheumatoid factors (latex)", immuno, serum, "TUBE_GEL", "Agglutination sur lame", 24, "B368"),
                    "FR_LATEX", "Facteurs rhumatoïdes", "العوامل الروماتويدية", "Rheumatoid factors");

        // Total IgE rise through childhood: adult limit only.
        Single("IGE_TOT", "IgE totales", "الغلوبولين المناعي E الكلي", "Total IgE",
               immuno, serum, "TUBE_GEL", immunoassay, 48, "B354", "IGE", "UI_ML", 0, Adult(null, 100m));

        // B348 « AC anti microsomaux » — the microsomal antigen is thyroperoxidase.
        Single("AC_TPO", "Anticorps anti-thyroperoxydase (anti-TPO)", "الأجسام المضادة لبيروكسيداز الدرق",
               "Anti-thyroid peroxidase antibodies", immuno, serum, "TUBE_GEL", immunoassay, 48, "B348",
               "TPO", "UI_ML", 0, Adult(null, 34m));

        Single("PSA", "PSA total", "مستضد البروستات النوعي الكلي", "Total PSA",
               immuno, serum, "TUBE_GEL", immunoassay, 24, "B361", "PSA", "NG_ML", 2,
               Threshold(Adult(null, 4.00m, Gender.Male)));

        Single("ACE", "Antigène carcino-embryonnaire (ACE)", "المستضد السرطاني المضغي", "Carcinoembryonic antigen (CEA)",
               immuno, serum, "TUBE_GEL", immunoassay, 24, "B359", "ACE", "NG_ML", 1, Adult(null, 5.0m));

        Single("AFP", "Alpha-fœtoprotéine (AFP)", "ألفا فيتوبروتين", "Alpha-fetoprotein",
               immuno, serum, "TUBE_GEL", immunoassay, 24, "B360", "AFP", "NG_ML", 1, NotPregnant(Adult(null, 7.0m)));

        Single("CA125", "CA 125", "CA 125", "CA 125",
               immuno, serum, "TUBE_GEL", immunoassay, 48, "B362", "CA125", "U_ML", 1,
               Adult(null, 35m, Gender.Female));

        Single("CA153", "CA 15-3", "CA 15-3", "CA 15-3",
               immuno, serum, "TUBE_GEL", immunoassay, 48, "B363", "CA153", "U_ML", 1, Adult(null, 30m));

        Single("CA199", "CA 19-9", "CA 19-9", "CA 19-9",
               immuno, serum, "TUBE_GEL", immunoassay, 48, "B364", "CA199", "U_ML", 1, Adult(null, 37m));

        // ---- Hormonology ----------------------------------------------------------------------
        // Roche Elecsys inserts (FT4 III, FT3 III, FSH, LH, Prolactin II, Testosterone II,
        // Cortisol II, Insulin, HCG+β, Estradiol, Progesterone). Phase-dependent female values are
        // seeded as text: the cycle day is not known to the system, and limits would mis-flag.

        Single("T4L", "T4 libre (FT4)", "الثيروكسين الحر", "Free thyroxine (FT4)",
               hormo, serum, "TUBE_GEL", immunoassay, 24, "B161", "FT4", "PMOL_L", 1, Adult(12.0m, 22.0m));

        // B165 « Triiodothyronine libre » rather than B159 « T3 ou FT3 », which also covers total T3.
        Single("T3L", "T3 libre (FT3)", "ثلاثي يودوثيرونين الحر", "Free triiodothyronine (FT3)",
               hormo, serum, "TUBE_GEL", immunoassay, 24, "B165", "FT3", "PMOL_L", 2, Adult(3.10m, 6.80m));

        Single("FSH", "FSH", "الهرمون المنبه للجريب", "Follicle-stimulating hormone",
               hormo, serum, "TUBE_GEL", immunoassay, 24, "B458", "FSH", "UI_L", 1,
               Adult(1.5m, 12.4m, Gender.Male),
               Display(Gender.Female,
                       "Folliculaire 3,5–12,5 ; ovulatoire 4,7–21,5 ; lutéale 1,7–7,7 ; ménopause 25,8–134,8 UI/L",
                       "الطور الجريبي 3,5–12,5 ؛ الإباضة 4,7–21,5 ؛ الطور الأصفري 1,7–7,7 ؛ سن اليأس 25,8–134,8",
                       "Follicular 3.5–12.5; ovulatory 4.7–21.5; luteal 1.7–7.7; postmenopausal 25.8–134.8 IU/L"));

        Single("LH", "LH", "الهرمون الملوتن", "Luteinising hormone",
               hormo, serum, "TUBE_GEL", immunoassay, 24, "B156", "LH", "UI_L", 1,
               Adult(1.7m, 8.6m, Gender.Male),
               Display(Gender.Female,
                       "Folliculaire 2,4–12,6 ; ovulatoire 14,0–95,6 ; lutéale 1,0–11,4 ; ménopause 7,7–58,5 UI/L",
                       "الطور الجريبي 2,4–12,6 ؛ الإباضة 14,0–95,6 ؛ الطور الأصفري 1,0–11,4 ؛ سن اليأس 7,7–58,5",
                       "Follicular 2.4–12.6; ovulatory 14.0–95.6; luteal 1.0–11.4; postmenopausal 7.7–58.5 IU/L"));

        {
            // Pregnancy raises prolactin up to tenfold: the interval is the non-pregnant woman's.
            var female = NotPregnant(Adult(4.8m, 23.3m, Gender.Female));
            Single("PROLACTINE", "Prolactine", "البرولاكتين", "Prolactin",
                   hormo, serum, "TUBE_GEL", immunoassay, 24, "B158", "PRL", "NG_ML", 1,
                   Adult(4.0m, 15.2m, Gender.Male), female);
        }

        Single("OESTRADIOL", "17-bêta œstradiol", "إستراديول 17 بيتا", "17-beta oestradiol",
               hormo, serum, "TUBE_GEL", immunoassay, 24, "B152", "E2", "PG_ML", 0,
               Display(Gender.Female,
                       "Folliculaire 12,5–166 ; ovulatoire 85,8–498 ; lutéale 43,8–211 ; ménopause < 54,7 pg/mL",
                       "الطور الجريبي 12,5–166 ؛ الإباضة 85,8–498 ؛ الطور الأصفري 43,8–211 ؛ سن اليأس أقل من 54,7",
                       "Follicular 12.5–166; ovulatory 85.8–498; luteal 43.8–211; postmenopausal < 54.7 pg/mL"));

        Single("PROGESTERONE", "Progestérone", "البروجسترون", "Progesterone",
               hormo, serum, "TUBE_GEL", immunoassay, 24, "B157", "PROG", "NG_ML", 2,
               Display(Gender.Female,
                       "Folliculaire 0,2–1,5 ; lutéale 1,7–27 ; ménopause 0,1–0,8 ng/mL",
                       "الطور الجريبي 0,2–1,5 ؛ الطور الأصفري 1,7–27 ؛ سن اليأس 0,1–0,8",
                       "Follicular 0.2–1.5; luteal 1.7–27; postmenopausal 0.1–0.8 ng/mL"),
               Adult(0.2m, 1.4m, Gender.Male));

        Single("TESTOSTERONE", "Testostérone totale", "التستوستيرون الكلي", "Total testosterone",
               hormo, serum, "TUBE_GEL", immunoassay, 24, "B162", "TESTO", "NG_ML", 2,
               Adult(2.50m, 8.40m, Gender.Male), Adult(0.08m, 0.48m, Gender.Female));

        {
            var test = Test("CORTISOL", "Cortisol (8 h)", "الكورتيزول (الساعة 8)", "Cortisol (8 a.m.)",
                            hormo, serum, "TUBE_GEL", immunoassay, 24, "B153");
            test.RequiredClinicalInformation = new MultilingualText(
                "Heure du prélèvement ? Corticothérapie en cours ?",
                "ساعة أخذ العينة؟ علاج بالكورتيزون جارٍ؟",
                "Time of collection? Current corticosteroid therapy?");
            var p = Param("CORTISOL", "Cortisol", "الكورتيزول", "Cortisol", Unit("NMOL_L"), decimals: 0);
            p.AddReferenceRange(Note(Adult(171m, 536m),
                "Matin (7 h–10 h) : 171–536 nmol/L ; soir (16 h–20 h) : 64–327 nmol/L",
                "الصباح (7–10) : 171–536 ؛ المساء (16–20) : 64–327 نانومول/ل",
                "Morning (7–10 a.m.): 171–536 nmol/L; evening (4–8 p.m.): 64–327 nmol/L"));
            test.AddParameter(p);
        }

        {
            var test = Test("INSULINE", "Insulinémie à jeun", "الأنسولين على الصيام", "Fasting insulin",
                            hormo, serum, "TUBE_GEL", immunoassay, 24, "B155");
            test.FastingHoursOverride = 8;
            var p = Param("INSULINE", "Insuline", "الأنسولين", "Insulin", Unit("MUI_L"), decimals: 1);
            p.AddReferenceRange(Adult(2.6m, 24.9m));
            test.AddParameter(p);
        }

        Single("BHCG", "Bêta-HCG plasmatique (quantitatif)", "هرمون الحمل بيتا (كمي)", "Quantitative beta-hCG",
               hormo, serum, "TUBE_GEL", immunoassay, 4, "B150", "BHCG", "UI_L", 1,
               Note(NotPregnant(Range(null, 5.0m)),
                    "< 5 UI/L : négatif ; en cours de grossesse, selon le terme",
                    "أقل من 5 : سلبي ؛ أثناء الحمل حسب المدة",
                    "< 5 IU/L: negative; in pregnancy, by gestational age"));

        // ---- Serology -------------------------------------------------------------------------
        // Kit-dependent cut-offs: answered négatif / positif / douteux, the index typed in the
        // comment where the laboratory reports it.

        {
            var test = Test("VHC", "Sérologie hépatite C (anticorps anti-VHC)", "الأجسام المضادة لالتهاب الكبد C",
                            "Hepatitis C antibodies", sero, serum, "TUBE_GEL", immunoassay, 24, "B324");
            test.IsNotifiableDisease = true;
            Qualitative(test, "VHC", "Ac anti-VHC", "أضداد VHC", "Anti-HCV");
        }

        {
            var test = Test("VIH", "Sérologie VIH 1 et 2 (dépistage combiné)", "الكشف عن فيروس نقص المناعة 1 و 2",
                            "HIV 1/2 screening (combined)", sero, serum, "TUBE_GEL", immunoassay, 24, "B329");
            // Specific consent and counselling (ConsentRequirement.Hiv), and a declaration task on a
            // positive result.
            test.ConsentRequirement = ConsentRequirement.Hiv;
            test.IsNotifiableDisease = true;
            Qualitative(test, "VIH", "Ac anti-VIH 1/2 et Ag p24", "أضداد VIH وp24", "Anti-HIV 1/2 and p24 Ag");
        }

        Single("AC_HBS", "Anticorps anti-HBs", "الأجسام المضادة لـ HBs", "Anti-HBs antibodies",
               sero, serum, "TUBE_GEL", immunoassay, 24, "B318", "AC_HBS", "UI_L", 0,
               Note(Threshold(Range(10m, null)),
                    "< 10 UI/L : non protégé ; ≥ 10 UI/L : protégé",
                    "أقل من 10 : غير محمي ؛ 10 أو أكثر : محمي",
                    "< 10 IU/L: not protected; ≥ 10 IU/L: protected"));

        Qualitative(Test("AC_HBC", "Anticorps anti-HBc totaux", "الأجسام المضادة الكلية لـ HBc", "Total anti-HBc",
                         sero, serum, "TUBE_GEL", immunoassay, 24, "B321"),
                    "AC_HBC", "Ac anti-HBc", "أضداد HBc", "Anti-HBc");

        Qualitative(Test("VHA_IGM", "Hépatite A : IgM anti-VHA", "التهاب الكبد A : IgM", "Hepatitis A IgM",
                         sero, serum, "TUBE_GEL", immunoassay, 24, "B316"),
                    "VHA_IGM", "IgM anti-VHA", "IgM ضد VHA", "Anti-HAV IgM");

        Qualitative(Test("TPHA", "Syphilis : TPHA", "الزهري : TPHA", "Syphilis: TPHA",
                         sero, serum, "TUBE_GEL", "Hémagglutination passive", 24, "B285"),
                    "TPHA", "TPHA", "TPHA", "TPHA");

        Qualitative(Test("VDRL", "Syphilis : VDRL", "الزهري : VDRL", "Syphilis: VDRL",
                         sero, serum, "TUBE_GEL", "Floculation (RPR / VDRL)", 24, "B283"),
                    "VDRL", "VDRL", "VDRL", "VDRL");

        Qualitative(Test("TOXO_IGG", "Toxoplasmose IgG", "داء المقوسات IgG", "Toxoplasma IgG",
                         sero, serum, "TUBE_GEL", immunoassay, 24, "B307"),
                    "TOXO_IGG", "IgG anti-toxoplasme", "IgG ضد المقوسة", "Anti-toxoplasma IgG",
                    positiveIsAbnormal: false);

        Qualitative(Test("TOXO_IGM", "Toxoplasmose IgM", "داء المقوسات IgM", "Toxoplasma IgM",
                         sero, serum, "TUBE_GEL", immunoassay, 24, "B308"),
                    "TOXO_IGM", "IgM anti-toxoplasme", "IgM ضد المقوسة", "Anti-toxoplasma IgM");

        {
            var test = Test("RUBEOLE_IGG", "Rubéole IgG", "الحصبة الألمانية IgG", "Rubella IgG",
                            sero, serum, "TUBE_GEL", immunoassay, 24, "B343");
            var p = Param("RUB_IGG", "IgG anti-rubéole", "IgG ضد الحصبة الألمانية", "Anti-rubella IgG", null, decimals: 0);
            p.ValueType = ResultValueType.Coded;
            p.AddCodedOption(Option("IMMUNISE", "Immunisé(e) (≥ 10 UI/mL)", "محصن", "Immune", expectedNormal: true));
            p.AddCodedOption(Option("NON_IMMUNISE", "Non immunisé(e)", "غير محصن", "Not immune", abnormal: true));
            p.AddCodedOption(Option("DOUTEUX", "Douteux — à contrôler", "مشكوك فيه — يعاد", "Equivocal — repeat",
                                    abnormal: true));
            test.AddParameter(p);
        }

        Single("ASLO", "Antistreptolysines O (ASLO)", "مضادات الستربتوليزين O", "Antistreptolysin O",
               sero, serum, "TUBE_GEL", "Immunoturbidimétrie", 24, "B266", "ASLO", "UI_ML", 0, Adult(null, 200m));

        Qualitative(Test("WRIGHT", "Sérodiagnostic de Wright (brucellose)", "اختبار رايت (داء البروسيلات)",
                         "Brucella serology (Wright)", sero, serum, "TUBE_GEL",
                         "Agglutination (Rose Bengale / Wright)", 24, "B268"),
                    "WRIGHT", "Wright", "رايت", "Wright");

        Qualitative(Test("HP_SERO", "Sérologie Helicobacter pylori", "الأجسام المضادة للملوية البوابية",
                         "Helicobacter pylori serology", sero, serum, "TUBE_GEL", immunoassay, 48, "B270"),
                    "HP_SERO", "Ac anti-H. pylori", "أضداد الملوية البوابية", "Anti-H. pylori");

        // ---- Bacteriology and parasitology ----------------------------------------------------
        // The antibiogram is a separate act (B242), added when a culture is positive.

        {
            var test = Test("HEMOC", "Hémoculture (aérobie + anaérobie)", "زرع الدم (هوائي ولاهوائي)",
                            "Blood culture (aerobic + anaerobic)", bacte, SpecimenType.Codes.BloodCulture, "HEMOC_AERO",
                            "Automate à détection fluorimétrique — 5 jours", 120, "B503");
            test.IsAvailableUrgently = false;
            Text(test, "HEMOC_CULT", "Culture et identification", "الزرع والتعرف", "Culture and identification");
        }

        {
            var test = Test("COPRO", "Coproculture", "زرع البراز", "Stool culture",
                            bacte, stool, "POT_SELLES", "Culture sur milieux sélectifs (Salmonella, Shigella…)", 72, "B255");
            test.IsAvailableUrgently = false;
            Text(test, "COPRO_EXAM", "Examen macroscopique et microscopique", "الفحص العياني والمجهري",
                 "Macroscopic and microscopic examination");
            Text(test, "COPRO_CULT", "Culture et identification", "الزرع والتعرف", "Culture and identification");
        }

        // Vaginal swab: direct examination, culture and, if positive, an antibiogram — several acts,
        // left without one like the urine culture. Coefficient of B241.
        {
            var test = Test("PV", "Prélèvement vaginal (examen cytobactériologique)", "المسحة المهبلية (فحص خلوي جرثومي)",
                            "High vaginal swab culture", bacte, SpecimenType.Codes.VaginalSwab, "ECOUVILLON",
                            "Examen direct, Gram, culture", 72, act: null, coefficient: 90m);
            test.IsAvailableUrgently = false;
            Text(test, "PV_DIRECT", "Examen direct et coloration de Gram", "الفحص المباشر وصبغة غرام",
                 "Direct examination and Gram stain");
            Text(test, "PV_CULT", "Culture et identification", "الزرع والتعرف", "Culture and identification");
        }

        {
            var test = Test("EPS", "Examen parasitologique des selles", "الفحص الطفيلي للبراز",
                            "Stool ova and parasites", parasito, stool, "POT_SELLES",
                            "Examen direct et après concentration", 24, "B254");
            Text(test, "EPS_RESULT", "Résultat", "النتيجة", "Result");
        }

        Qualitative(Test("SANG_SELLES", "Recherche de sang occulte dans les selles (immunologique)",
                         "البحث عن الدم الخفي في البراز (مناعي)", "Faecal immunochemical test",
                         bacte, stool, "POT_SELLES", "Immunochromatographie", 24, "B257"),
                    "SANG_SELLES", "Sang occulte", "الدم الخفي", "Occult blood");

        // ---- Urine ----------------------------------------------------------------------------

        Qualitative(Test("TEST_GROSS", "Test de grossesse urinaire", "اختبار الحمل في البول", "Urine pregnancy test",
                         urine, urineSpot, "POT_URINE", "Immunochromatographie (hCG)", 2, "B194"),
                    "TEST_GROSS", "hCG urinaire", "hCG في البول", "Urine hCG", positiveIsAbnormal: false);

        return tests;
    }

    /// <summary>The panels a new installation adds to the five of <see cref="SeedPanelsAsync"/>.</summary>
    private static readonly (string Code, string Fr, string Ar, string En, string[] Tests)[] RoutinePanels =
    [
        ("BILAN_HEPAT", "Bilan hépatique", "الحصيلة الكبدية", "Liver profile",
         ["TRANSAM", "GGT", "PAL", "BILIRUBINE"]),

        ("BILAN_RENAL", "Bilan rénal", "الحصيلة الكلوية", "Renal profile",
         ["UREE", "CREAT", "IONO", "AC_URIQUE"]),

        ("BILAN_THYR", "Bilan thyroïdien", "الحصيلة الدرقية", "Thyroid profile",
         ["TSH", "T4L"]),

        ("BILAN_MARTIAL", "Bilan martial", "حصيلة الحديد", "Iron studies",
         ["NFS", "FER", "FERRITINE"]),

        ("BILAN_HEMOST", "Bilan d'hémostase", "حصيلة التخثر", "Coagulation screen",
         ["TP_INR", "TCA", "FIBRINOGENE"]),

        ("BILAN_PHOSPHOCALC", "Bilan phosphocalcique", "حصيلة الكالسيوم والفوسفور", "Bone profile",
         ["CALCEMIE", "PHOSPHORE", "PAL", "VIT_D"])
    ];

    // ---- Range helpers ---------------------------------------------------------------------------

    /// <summary>An adult interval: from 18 years, so a child is not flagged against it.</summary>
    private static ReferenceRange Adult(decimal? low, decimal? high, Gender gender = Gender.Unknown) =>
        Years(low, high, 18, null, gender);

    /// <summary>An interval over an age band in years (upper bound exclusive), stored in years.</summary>
    private static ReferenceRange Years(decimal? low, decimal? high, int fromYears, int? toYears,
                                        Gender gender = Gender.Unknown)
    {
        var range = Range(low, high, gender,
                          ageFromDays: fromYears * ReferenceRange.DaysPerYear,
                          ageToDays: toYears * ReferenceRange.DaysPerYear);
        range.AgeUnitDays = ReferenceRange.DaysPerYear;
        return range;
    }

    /// <summary>An interval from an age in months onwards, stored in months.</summary>
    private static ReferenceRange Months(decimal? low, decimal? high, int fromMonths)
    {
        var range = Range(low, high, ageFromDays: fromMonths * ReferenceRange.DaysPerMonth);
        range.AgeUnitDays = ReferenceRange.DaysPerMonth;
        return range;
    }

    /// <summary>Outside pregnancy only, for an analyte pregnancy raises (AFP, hCG).</summary>
    private static ReferenceRange NotPregnant(ReferenceRange range)
    {
        range.AppliesToPregnancy = false;
        return range;
    }

    private static ReferenceRange Threshold(ReferenceRange range)
    {
        range.IsDecisionThreshold = true;
        return range;
    }

    private static ReferenceRange Note(ReferenceRange range, string fr, string ar, string en)
    {
        range.DisplayText = new MultilingualText(fr, ar, en);
        return range;
    }

    /// <summary>A text-only interval: printed on the report, never used to flag.</summary>
    private static ReferenceRange Display(Gender gender, string fr, string ar, string en) =>
        new(gender)
        {
            DisplayText = new MultilingualText(fr, ar, en),
            Source = PendingVerification
        };
}
