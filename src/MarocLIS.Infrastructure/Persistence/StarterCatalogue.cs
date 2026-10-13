using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// A starter analysis catalogue covering the routine activity of a Moroccan private laboratory.
/// <para>
/// <b>The reference intervals and tariffs seeded here are a starting point, not a validated
/// configuration.</b> ISO 15189 §5.5.2 requires the laboratory to establish and periodically
/// review its own reference intervals for its own methods and population, and the reimbursement
/// base of each act comes from the Tarif National de Référence. Every seeded range carries
/// <c>Source = "À vérifier par le laboratoire"</c> so the catalogue screen can list what still
/// needs confirming, and the tariffs use a placeholder letter-key value.
/// </para>
/// <para>
/// What the seed does demonstrate structurally is worth keeping: multi-parameter panels, a
/// calculated parameter with a formula, sex- and age-specific intervals, coded results, critical
/// thresholds and delta checks.
/// </para>
/// </summary>
internal static class StarterCatalogue
{
    private const string PendingVerification = "À vérifier par le laboratoire";

    internal static async Task SeedAsync(LisDbContext context, IClock clock, CancellationToken ct)
    {
        var departments = await context.Departments.ToDictionaryAsync(d => d.Code, d => d.Id, ct);
        var specimens = await context.SpecimenTypes.ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        var containers = await context.ContainerTypes.ToDictionaryAsync(c => c.Code, c => c.Id, ct);
        var units = await context.Units.ToDictionaryAsync(u => u.Code, u => u.Id, ct);

        Guid Unit(string code) => units[code];

        var validFrom = new DateOnly(clock.Today.Year, 1, 1);

        var tests = new List<AnalysisTest>();

        // ---- Biochemistry ---------------------------------------------------------------------

        var glucose = New("GLY", "Glycémie à jeun", "سكر الدم على الصيام", "Fasting blood glucose",
                          departments[Department.Codes.Biochemistry],
                          specimens[SpecimenType.Codes.PlasmaFluoride],
                          containers["TUBE_FLUORURE"],
                          method: "Hexokinase", routineHours: 4, coefficient: 5m, tariffValidFrom: validFrom);
        glucose.FastingHoursOverride = 8;
        glucose.StabilityAtRoomTemperatureHours = 8;
        glucose.LoincCode = "1558-6";
        {
            var p = Param("GLY", "Glycémie", "سكر الدم", "Glucose", Unit("G_L"), decimals: 2);
            p.MeasuringRangeLow = 0.10m; p.MeasuringRangeHigh = 8.00m;
            p.TechnicalLimitLow = 0.05m; p.TechnicalLimitHigh = 12.00m;
            p.CriticalLow = 0.40m; p.CriticalHigh = 4.00m;
            p.DeltaCheckPercent = 50m; p.DeltaCheckWindowDays = 7;
            p.AddReferenceRange(Range(0.70m, 1.10m));
            glucose.AddParameter(p);
        }
        tests.Add(glucose);

        var creatinine = New("CREAT", "Créatininémie", "الكرياتينين في الدم", "Serum creatinine",
                             departments[Department.Codes.Biochemistry],
                             specimens[SpecimenType.Codes.SerumDryTube],
                             containers["TUBE_GEL"],
                             method: "Jaffé compensé / enzymatique", routineHours: 6, coefficient: 7m, tariffValidFrom: validFrom);
        creatinine.LoincCode = "2160-0";
        {
            var p = Param("CREAT", "Créatinine", "الكرياتينين", "Creatinine", Unit("MG_L"), decimals: 1);
            p.MeasuringRangeLow = 1.0m; p.MeasuringRangeHigh = 200.0m;
            p.CriticalHigh = 60.0m;
            p.DeltaCheckPercent = 40m; p.DeltaCheckWindowDays = 30;
            // Sex-specific intervals: muscle mass differs, so one interval would mis-flag both sexes.
            p.AddReferenceRange(Range(7.0m, 13.0m, Gender.Male));
            p.AddReferenceRange(Range(6.0m, 11.0m, Gender.Female));
            creatinine.AddParameter(p);

            // A calculated parameter. The formula is stored as data and evaluated by
            // FormulaEvaluator, so a biologist can switch to another equation without a release.
            //
            // CKD-EPI 2009 takes creatinine in mg/dL. CREAT above is reported in mg/L, so the one
            // conversion needed is /10 — and only that one. This formula read [CREAT]/10/88.4,
            // stacking the µmol/L-to-mg/dL factor on top of the mg/L-to-mg/dL one, which divided
            // the creatinine by 884 instead of 10 and multiplied every eGFR by about 6.3. A
            // 50-year-old man at 9 mg/L came out at 626 instead of 99 mL/min/1.73 m²: not a value
            // a biologist would misread, but one that is above every threshold, so established
            // renal failure was reported as normal filtration and the KDIGO decision threshold on
            // the range below could never fire.
            var egfr = Param("DFG", "Débit de filtration glomérulaire estimé (CKD-EPI)",
                             "معدل الترشيح الكبيبي المقدر", "Estimated GFR (CKD-EPI)",
                             Unit("ML_MIN_173"), decimals: 0);
            egfr.CalculationFormula =
                "round(141 * pow(min([CREAT]/10/if([SEXE_F]=1;0.7;0.9);1);if([SEXE_F]=1;-0.329;-0.411))" +
                " * pow(max([CREAT]/10/if([SEXE_F]=1;0.7;0.9);1);-1.209)" +
                " * pow(0.993;[AGE]) * if([SEXE_F]=1;1.018;1);0)";
            egfr.AddReferenceRange(Range(90m, null, source: "KDIGO — seuil de décision clinique"));
            egfr.ReferenceRanges.First().IsDecisionThreshold = true;
            creatinine.AddParameter(egfr);
        }
        tests.Add(creatinine);

        var urea = New("UREE", "Urée sanguine", "اليوريا في الدم", "Blood urea",
                       departments[Department.Codes.Biochemistry],
                       specimens[SpecimenType.Codes.SerumDryTube], containers["TUBE_GEL"],
                       method: "Uréase / GLDH", routineHours: 6, coefficient: 6m, tariffValidFrom: validFrom);
        {
            var p = Param("UREE", "Urée", "اليوريا", "Urea", Unit("G_L"), decimals: 2);
            p.CriticalHigh = 2.00m;
            p.AddReferenceRange(Range(0.15m, 0.45m));
            urea.AddParameter(p);
        }
        tests.Add(urea);

        // Lipid profile — a single orderable analysis with a calculated LDL.
        var lipids = New("BILAN_LIP", "Bilan lipidique", "حصيلة الدهون", "Lipid profile",
                         departments[Department.Codes.Biochemistry],
                         specimens[SpecimenType.Codes.SerumDryTube], containers["TUBE_GEL"],
                         method: "Enzymatique colorimétrique", routineHours: 8, coefficient: 25m, tariffValidFrom: validFrom);
        lipids.FastingHoursOverride = 12;
        {
            var total = Param("CHOL", "Cholestérol total", "الكوليسترول الكلي", "Total cholesterol",
                              Unit("G_L"), decimals: 2);
            total.AddReferenceRange(Range(null, 2.00m, source: "Seuil de décision clinique"));
            total.ReferenceRanges.First().IsDecisionThreshold = true;
            lipids.AddParameter(total);

            var hdl = Param("HDL", "HDL-cholestérol", "الكوليسترول الحميد", "HDL cholesterol",
                            Unit("G_L"), decimals: 2);
            hdl.AddReferenceRange(Range(0.40m, null, Gender.Male));
            hdl.AddReferenceRange(Range(0.50m, null, Gender.Female));
            lipids.AddParameter(hdl);

            var tg = Param("TG", "Triglycérides", "الدهون الثلاثية", "Triglycerides",
                           Unit("G_L"), decimals: 2);
            tg.AddReferenceRange(Range(null, 1.50m));
            lipids.AddParameter(tg);

            // LDL by Friedewald. Only valid below 4 g/L of triglycerides, which the formula
            // enforces itself: above that it yields 0 and the biologist is expected to order a
            // direct measurement rather than report a number the method cannot support.
            var ldl = Param("LDL", "LDL-cholestérol (Friedewald)", "الكوليسترول الضار (فريدوالد)",
                            "LDL cholesterol (Friedewald)", Unit("G_L"), decimals: 2);
            ldl.CalculationFormula = "if([TG]<4;round([CHOL]-[HDL]-[TG]/5;2);0)";
            ldl.AddReferenceRange(Range(null, 1.60m, source: "Seuil de décision clinique"));
            ldl.ReferenceRanges.First().IsDecisionThreshold = true;
            lipids.AddParameter(ldl);
        }
        tests.Add(lipids);

        var transaminases = New("TRANSAM", "Transaminases (ASAT / ALAT)", "ناقلات الأمين",
                                "Transaminases (AST / ALT)",
                                departments[Department.Codes.Biochemistry],
                                specimens[SpecimenType.Codes.SerumDryTube], containers["TUBE_GEL"],
                                method: "IFCC 37 °C", routineHours: 6, coefficient: 14m, tariffValidFrom: validFrom);
        {
            var asat = Param("ASAT", "ASAT (TGO)", "ناقلة أمين الأسبارتات", "AST (SGOT)",
                             Unit("UI_L"), decimals: 0);
            asat.CriticalHigh = 500m;
            asat.AddReferenceRange(Range(null, 40m, Gender.Male));
            asat.AddReferenceRange(Range(null, 32m, Gender.Female));
            transaminases.AddParameter(asat);

            var alat = Param("ALAT", "ALAT (TGP)", "ناقلة أمين الألانين", "ALT (SGPT)",
                             Unit("UI_L"), decimals: 0);
            alat.CriticalHigh = 500m;
            alat.AddReferenceRange(Range(null, 41m, Gender.Male));
            alat.AddReferenceRange(Range(null, 33m, Gender.Female));
            transaminases.AddParameter(alat);
        }
        tests.Add(transaminases);

        var ionogram = New("IONO", "Ionogramme sanguin", "شوارد الدم", "Serum electrolytes",
                           departments[Department.Codes.Biochemistry],
                           specimens[SpecimenType.Codes.SerumDryTube], containers["TUBE_GEL"],
                           method: "Potentiométrie indirecte", routineHours: 4, coefficient: 18m, tariffValidFrom: validFrom);
        {
            var sodium = Param("NA", "Sodium", "الصوديوم", "Sodium", Unit("MEQ_L"), decimals: 0);
            sodium.CriticalLow = 120m; sodium.CriticalHigh = 160m;
            sodium.TechnicalLimitLow = 90m; sodium.TechnicalLimitHigh = 190m;
            sodium.AddReferenceRange(Range(136m, 145m));
            ionogram.AddParameter(sodium);

            var potassium = Param("K", "Potassium", "البوتاسيوم", "Potassium", Unit("MEQ_L"), decimals: 1);
            // Potassium is the classic panic value: both tails are immediately actionable, and a
            // haemolysed tube falsely raises it — hence the tight delta check as well.
            potassium.CriticalLow = 2.8m; potassium.CriticalHigh = 6.0m;
            potassium.TechnicalLimitLow = 1.0m; potassium.TechnicalLimitHigh = 10.0m;
            potassium.DeltaCheckPercent = 25m; potassium.DeltaCheckWindowDays = 3;
            potassium.AddReferenceRange(Range(3.5m, 5.1m));
            ionogram.AddParameter(potassium);

            var chloride = Param("CL", "Chlore", "الكلور", "Chloride", Unit("MEQ_L"), decimals: 0);
            chloride.AddReferenceRange(Range(98m, 107m));
            ionogram.AddParameter(chloride);
        }
        tests.Add(ionogram);

        var hba1c = New("HBA1C", "Hémoglobine glyquée (HbA1c)", "الهيموغلوبين السكري",
                        "Glycated haemoglobin (HbA1c)",
                        departments[Department.Codes.Biochemistry],
                        specimens[SpecimenType.Codes.WholeBloodEdta], containers["TUBE_EDTA"],
                        method: "HPLC — certifiée IFCC/NGSP", routineHours: 24, coefficient: 40m, tariffValidFrom: validFrom);
        hba1c.IsAccredited = true;
        {
            var p = Param("HBA1C", "HbA1c", "الهيموغلوبين السكري", "HbA1c", Unit("PERCENT"), decimals: 1);
            p.MeasuringRangeLow = 4.0m; p.MeasuringRangeHigh = 15.0m;
            p.AnalyticalCvPercent = 2.0m;
            var range = Range(null, 5.7m, source: "ADA / seuils diagnostiques");
            range.IsDecisionThreshold = true;
            range.DisplayText = new MultilingualText(
                "< 5,7 % : normal ; 5,7–6,4 % : prédiabète ; ≥ 6,5 % : diabète",
                "أقل من 5,7 % : عادي ؛ 5,7–6,4 % : ما قبل السكري ؛ 6,5 % أو أكثر : سكري",
                "< 5.7 %: normal; 5.7–6.4 %: prediabetes; ≥ 6.5 %: diabetes");
            p.AddReferenceRange(range);
            hba1c.AddParameter(p);
        }
        tests.Add(hba1c);

        var crp = New("CRP", "Protéine C réactive", "البروتين التفاعلي C", "C-reactive protein",
                      departments[Department.Codes.Biochemistry],
                      specimens[SpecimenType.Codes.SerumDryTube], containers["TUBE_GEL"],
                      method: "Immunoturbidimétrie", routineHours: 4, coefficient: 20m, tariffValidFrom: validFrom);
        {
            var p = Param("CRP", "CRP", "CRP", "CRP", Unit("MG_L"), decimals: 1);
            p.MeasuringRangeLow = 1.0m; p.MeasuringRangeHigh = 300.0m;
            p.AddReferenceRange(Range(null, 5.0m));
            crp.AddParameter(p);
        }
        tests.Add(crp);

        // ---- Haematology ----------------------------------------------------------------------

        var cbc = New("NFS", "Numération formule sanguine", "تحليل الدم الكامل",
                      "Full blood count",
                      departments[Department.Codes.Haematology],
                      specimens[SpecimenType.Codes.WholeBloodEdta], containers["TUBE_EDTA"],
                      method: "Cytométrie de flux / impédance", routineHours: 4, coefficient: 40m, tariffValidFrom: validFrom);
        cbc.LoincCode = "58410-2";
        cbc.StabilityAtRoomTemperatureHours = 8;
        cbc.StabilityRefrigeratedHours = 24;
        cbc.IsAccredited = true;
        {
            void Add(TestParameter p) => cbc.AddParameter(p);

            var wbc = Param("GB", "Leucocytes", "الكريات البيضاء", "White blood cells",
                            Unit("GIGA_L"), decimals: 2);
            wbc.CriticalLow = 1.50m; wbc.CriticalHigh = 50.00m;
            wbc.AddReferenceRange(Range(4.00m, 10.00m));
            Add(wbc);

            var rbc = Param("GR", "Érythrocytes", "الكريات الحمراء", "Red blood cells",
                            Unit("TERA_L"), decimals: 2);
            rbc.AddReferenceRange(Range(4.50m, 5.90m, Gender.Male));
            rbc.AddReferenceRange(Range(4.00m, 5.20m, Gender.Female));
            Add(rbc);

            var hb = Param("HB", "Hémoglobine", "الهيموغلوبين", "Haemoglobin", Unit("G_DL"), decimals: 1);
            hb.CriticalLow = 7.0m; hb.CriticalHigh = 20.0m;
            hb.DeltaCheckPercent = 20m; hb.DeltaCheckWindowDays = 7;
            hb.AddReferenceRange(Range(13.0m, 17.0m, Gender.Male));
            hb.AddReferenceRange(Range(12.0m, 15.0m, Gender.Female));
            // Neonatal interval, in days: a newborn's haemoglobin is far above the adult range and
            // would otherwise be flagged critically high on every birth.
            hb.AddReferenceRange(Range(14.0m, 22.0m, ageFromDays: 0, ageToDays: 14));
            // Pregnancy lowers the threshold; WHO anaemia cut-off in pregnancy is 11 g/dL.
            var pregnancy = Range(11.0m, 14.0m, Gender.Female);
            pregnancy.AppliesToPregnancy = true;
            hb.AddReferenceRange(pregnancy);
            Add(hb);

            var hct = Param("HTE", "Hématocrite", "الهيماتوكريت", "Haematocrit",
                            Unit("PERCENT"), decimals: 1);
            hct.AddReferenceRange(Range(40.0m, 52.0m, Gender.Male));
            hct.AddReferenceRange(Range(36.0m, 47.0m, Gender.Female));
            Add(hct);

            var mcv = Param("VGM", "Volume globulaire moyen", "متوسط حجم الكرية",
                            "Mean corpuscular volume", Unit("FL"), decimals: 1);
            mcv.AddReferenceRange(Range(80.0m, 100.0m));
            Add(mcv);

            var mch = Param("TCMH", "Teneur corpusculaire moyenne en Hb", "متوسط هيموغلوبين الكرية",
                            "Mean corpuscular haemoglobin", Unit("PG"), decimals: 1);
            mch.AddReferenceRange(Range(27.0m, 32.0m));
            Add(mch);

            var mchc = Param("CCMH", "Concentration corpusculaire moyenne en Hb",
                             "متوسط تركيز هيموغلوبين الكرية",
                             "Mean corpuscular haemoglobin concentration", Unit("G_DL"), decimals: 1);
            mchc.AddReferenceRange(Range(32.0m, 36.0m));
            Add(mchc);

            var platelets = Param("PLQ", "Plaquettes", "الصفيحات الدموية", "Platelets",
                                  Unit("GIGA_L"), decimals: 0);
            platelets.CriticalLow = 50m; platelets.CriticalHigh = 1000m;
            platelets.AddReferenceRange(Range(150m, 400m));
            Add(platelets);

            // Differential count. Indented on the report under the leucocyte count.
            var heading = Param("FORMULE", "Formule leucocytaire", "الصيغة البيضاء",
                                "Differential count", null, decimals: 0);
            heading.ValueType = ResultValueType.Heading;
            Add(heading);

            (string Code, string Fr, string Ar, string En, decimal Low, decimal High)[] differential =
            [
                ("PNN",  "Polynucléaires neutrophiles", "العدلات",   "Neutrophils", 1.50m, 7.00m),
                ("PNE",  "Polynucléaires éosinophiles", "الحمضات",   "Eosinophils", 0.02m, 0.50m),
                ("PNB",  "Polynucléaires basophiles",   "القعدات",   "Basophils",   0.00m, 0.10m),
                ("LYMPH","Lymphocytes",                 "اللمفاويات","Lymphocytes", 1.00m, 4.00m),
                ("MONO", "Monocytes",                   "الوحيدات",  "Monocytes",   0.20m, 1.00m)
            ];

            foreach (var d in differential)
            {
                var p = Param(d.Code, d.Fr, d.Ar, d.En, Unit("GIGA_L"), decimals: 2);
                p.IndentLevel = 1;
                p.AddReferenceRange(Range(d.Low, d.High));
                Add(p);
            }
        }
        tests.Add(cbc);

        var esr = New("VS", "Vitesse de sédimentation", "سرعة ترسب الدم",
                      "Erythrocyte sedimentation rate",
                      departments[Department.Codes.Haematology],
                      specimens[SpecimenType.Codes.PlasmaCitrate], containers["TUBE_VS"],
                      method: "Westergren", routineHours: 4, coefficient: 10m, tariffValidFrom: validFrom);
        {
            var p = Param("VS1H", "VS à 1 heure", "سرعة الترسب في ساعة", "ESR at 1 hour",
                          Unit("MM_H"), decimals: 0);
            // The interval widens with age; the classic Miller rule is age/2 for men.
            p.AddReferenceRange(Range(null, 15m, Gender.Male, ageFromDays: 0, ageToDays: 50 * 365));
            p.AddReferenceRange(Range(null, 20m, Gender.Male, ageFromDays: 50 * 365));
            p.AddReferenceRange(Range(null, 20m, Gender.Female, ageFromDays: 0, ageToDays: 50 * 365));
            p.AddReferenceRange(Range(null, 30m, Gender.Female, ageFromDays: 50 * 365));
            esr.AddParameter(p);
        }
        tests.Add(esr);

        // ---- Haemostasis ----------------------------------------------------------------------

        var tp = New("TP_INR", "Taux de prothrombine / INR", "زمن البروثرومبين / INR",
                     "Prothrombin time / INR",
                     departments[Department.Codes.Haemostasis],
                     specimens[SpecimenType.Codes.PlasmaCitrate], containers["TUBE_CITRATE"],
                     method: "Chronométrique — thromboplastine calcique", routineHours: 4,
                     coefficient: 25m, tariffValidFrom: validFrom);
        tp.StabilityAtRoomTemperatureHours = 4;
        {
            var p = Param("TP", "Taux de prothrombine", "نسبة البروثرومبين", "Prothrombin activity",
                          Unit("PERCENT"), decimals: 0);
            p.CriticalLow = 20m;
            p.AddReferenceRange(Range(70m, 100m));
            tp.AddParameter(p);

            var inr = Param("INR", "INR", "INR", "INR", Unit("INR"), decimals: 2);
            inr.CriticalHigh = 5.00m;
            inr.DeltaCheckPercent = 50m; inr.DeltaCheckWindowDays = 7;
            var range = Range(0.80m, 1.20m);
            range.DisplayText = new MultilingualText(
                "0,80–1,20 (sujet non traité) ; cible thérapeutique selon indication",
                "0,80–1,20 (غير معالج) ؛ الهدف العلاجي حسب الاستطباب",
                "0.80–1.20 (untreated); therapeutic target depends on indication");
            inr.AddReferenceRange(range);
            tp.AddParameter(inr);
        }
        tests.Add(tp);

        // ---- Hormonology ----------------------------------------------------------------------

        var tsh = New("TSH", "TSH ultrasensible", "الهرمون المنبه للدرقية", "TSH (ultrasensitive)",
                      departments[Department.Codes.Hormonology],
                      specimens[SpecimenType.Codes.SerumDryTube], containers["TUBE_GEL"],
                      method: "Immunoessai par chimiluminescence", routineHours: 24, coefficient: 60m, tariffValidFrom: validFrom);
        tsh.IsAccredited = true;
        {
            var p = Param("TSH", "TSH", "TSH", "TSH", Unit("MUI_L"), decimals: 3);
            p.MeasuringRangeLow = 0.005m; p.MeasuringRangeHigh = 100.000m;
            p.AnalyticalCvPercent = 4.0m;
            p.AddReferenceRange(Range(0.270m, 4.200m));
            // Gestational intervals: pregnancy suppresses TSH in the first trimester, and applying
            // the non-pregnant interval would report a normal result as abnormally low.
            for (var trimester = 1; trimester <= 3; trimester++)
            {
                var (low, high) = trimester switch
                {
                    1 => (0.100m, 2.500m),
                    2 => (0.200m, 3.000m),
                    _ => (0.300m, 3.000m)
                };
                var range = Range(low, high, Gender.Female);
                range.AppliesToPregnancy = true;
                range.PregnancyTrimester = trimester;
                p.AddReferenceRange(range);
            }
            tsh.AddParameter(p);
        }
        tests.Add(tsh);

        // ---- Serology with a coded result -----------------------------------------------------

        var hbs = New("HBS_AG", "Antigène HBs", "المستضد السطحي للتهاب الكبد ب",
                      "Hepatitis B surface antigen",
                      departments[Department.Codes.Serology],
                      specimens[SpecimenType.Codes.SerumDryTube], containers["TUBE_GEL"],
                      method: "Immunoessai par chimiluminescence", routineHours: 24, coefficient: 45m, tariffValidFrom: validFrom);
        // Hepatitis B is notifiable to the health authorities in Morocco; a positive result raises
        // a declaration task rather than quietly closing the file.
        hbs.IsNotifiableDisease = true;
        {
            var p = Param("HBS_AG", "Ag HBs", "المستضد HBs", "HBs antigen", null, decimals: 0);
            p.ValueType = ResultValueType.Coded;
            p.AddCodedOption(Option("NEGATIF", "Négatif", "سلبي", "Negative", expectedNormal: true));
            p.AddCodedOption(Option("POSITIF", "Positif", "إيجابي", "Positive",
                                    abnormal: true, critical: true));
            p.AddCodedOption(Option("DOUTEUX", "Douteux — à contrôler", "مشكوك فيه — يعاد",
                                    "Equivocal — repeat", abnormal: true));
            hbs.AddParameter(p);
        }
        tests.Add(hbs);

        // ---- Urine analysis with a semi-quantitative scale ------------------------------------

        var urine = New("CHIM_URIN", "Chimie des urines (bandelette)", "الفحص الكيميائي للبول",
                        "Urine dipstick",
                        departments[Department.Codes.UrineAnalysis],
                        specimens[SpecimenType.Codes.Urine], containers["POT_URINE"],
                        method: "Bandelette réactive — lecture automatisée", routineHours: 2,
                        coefficient: 12m, tariffValidFrom: validFrom);
        {
            (string Code, string Fr, string Ar, string En)[] analytes =
            [
                ("PROT_U",  "Protéines",     "البروتينات", "Protein"),
                ("GLU_U",   "Glucose",       "السكر",      "Glucose"),
                ("CET_U",   "Corps cétoniques","الأجسام الكيتونية","Ketones"),
                ("SANG_U",  "Sang",          "الدم",       "Blood"),
                ("LEUCO_U", "Leucocytes",    "الكريات البيضاء","Leucocytes"),
                ("NIT_U",   "Nitrites",      "النتريت",    "Nitrites")
            ];

            foreach (var a in analytes)
            {
                var p = Param(a.Code, a.Fr, a.Ar, a.En, null, decimals: 0);
                p.ValueType = ResultValueType.SemiQuantitative;
                p.AddCodedOption(Option("NEG", "Négatif", "سلبي", "Negative", expectedNormal: true));
                p.AddCodedOption(Option("TRACES", "Traces", "آثار", "Trace"));
                p.AddCodedOption(Option("1PLUS", "+", "+", "+", abnormal: true));
                p.AddCodedOption(Option("2PLUS", "++", "++", "++", abnormal: true));
                p.AddCodedOption(Option("3PLUS", "+++", "+++", "+++", abnormal: true));
                urine.AddParameter(p);
            }

            var ph = Param("PH_U", "pH", "درجة الحموضة", "pH", Unit("NONE"), decimals: 1);
            ph.AddReferenceRange(Range(5.0m, 7.5m));
            urine.AddParameter(ph);
        }
        tests.Add(urine);

        // ---- Bacteriology with a free-text and a titre ----------------------------------------

        var ecbu = New("ECBU", "Examen cytobactériologique des urines", "الفحص الخلوي الجرثومي للبول",
                       "Urine culture",
                       departments[Department.Codes.Bacteriology],
                       specimens[SpecimenType.Codes.Urine], containers["POT_URINE"],
                       method: "Culture sur milieux chromogènes + antibiogramme", routineHours: 72,
                       coefficient: 55m, tariffValidFrom: validFrom);
        ecbu.RequiredClinicalInformation = new MultilingualText(
            "Antibiothérapie en cours ? Signes fonctionnels urinaires ? Sondage ?",
            "علاج بالمضادات الحيوية جارٍ؟ أعراض بولية؟ قثطرة؟",
            "Current antibiotic therapy? Urinary symptoms? Catheter?");
        {
            var leuco = Param("LEUCO_CYT", "Leucocytes", "الكريات البيضاء", "Leucocytes",
                              Unit("PER_MM3"), decimals: 0);
            leuco.AddReferenceRange(Range(null, 10m));
            ecbu.AddParameter(leuco);

            var rbcCount = Param("GR_CYT", "Érythrocytes", "الكريات الحمراء", "Erythrocytes",
                                 Unit("PER_MM3"), decimals: 0);
            rbcCount.AddReferenceRange(Range(null, 10m));
            ecbu.AddParameter(rbcCount);

            var culture = Param("CULTURE", "Culture", "المزرعة", "Culture", null, decimals: 0);
            culture.ValueType = ResultValueType.Text;
            ecbu.AddParameter(culture);

            var count = Param("NUM_GERMES", "Numération des germes", "تعداد الجراثيم",
                              "Colony count", Unit("CFU_ML"), decimals: 0);
            count.ValueType = ResultValueType.NumericWithOperator;
            var significant = Range(null, 1000m);
            significant.DisplayText = new MultilingualText(
                "< 10³ UFC/mL ; bactériurie significative ≥ 10⁵ UFC/mL",
                "أقل من 10³ وحدة/مل ؛ بيلة جرثومية معتبرة من 10⁵ وحدة/مل",
                "< 10³ CFU/mL; significant bacteriuria ≥ 10⁵ CFU/mL");
            count.AddReferenceRange(significant);
            ecbu.AddParameter(count);

            var antibiogram = Param("ANTIBIOGRAMME", "Antibiogramme", "اختبار الحساسية للمضادات",
                                    "Antibiogram", null, decimals: 0);
            antibiogram.ValueType = ResultValueType.Text;
            ecbu.AddParameter(antibiogram);
        }
        tests.Add(ecbu);

        // ---- Immunohaematology ----------------------------------------------------------------

        var grouping = New("GROUPAGE", "Groupage sanguin ABO-Rhésus", "تحديد فصيلة الدم",
                           "ABO-Rh blood grouping",
                           departments[Department.Codes.Immunology],
                           specimens[SpecimenType.Codes.WholeBloodEdta], containers["TUBE_EDTA"],
                           method: "Épreuve globulaire (Beth-Vincent) et sérique (Simonin)",
                           routineHours: 4, coefficient: 30m, tariffValidFrom: validFrom);
        {
            var abo = Param("ABO", "Groupe ABO", "فصيلة ABO", "ABO group", null, decimals: 0);
            abo.ValueType = ResultValueType.Coded;
            foreach (var g in (string[])["O", "A", "B", "AB"])
                abo.AddCodedOption(Option(g, g, g, g));
            grouping.AddParameter(abo);

            var rh = Param("RHESUS", "Rhésus D", "عامل ريزوس D", "Rh D", null, decimals: 0);
            rh.ValueType = ResultValueType.Coded;
            rh.AddCodedOption(Option("POS", "Positif", "إيجابي", "Positive"));
            rh.AddCodedOption(Option("NEG", "Négatif", "سلبي", "Negative"));
            grouping.AddParameter(rh);
        }
        tests.Add(grouping);

        context.AnalysisTests.AddRange(tests);
        await context.SaveChangesAsync(ct);

        await SeedPanelsAsync(context, ct);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Creates an analysis together with its default-price-list tariff.
    /// <para>
    /// The tariff is attached here rather than in a second pass so the coefficient never has to be
    /// carried in state between the two: an analysis and the price it is sold at are created in one
    /// place, and there is no way to add one and forget the other.
    /// </para>
    /// </summary>
    private static AnalysisTest New(string code, string fr, string ar, string en,
                                    Guid departmentId, Guid specimenTypeId, Guid containerTypeId,
                                    string method, int routineHours, decimal coefficient,
                                    DateOnly tariffValidFrom)
    {
        var test = new AnalysisTest(code, new MultilingualText(fr, ar, en), departmentId, specimenTypeId)
        {
            ContainerTypeId = containerTypeId,
            Method = new MultilingualText(method),
            RoutineTurnaroundHours = routineHours,
            UrgentTurnaroundMinutes = Math.Max(30, routineHours * 15),
            IsAvailableUrgently = routineHours <= 24,
            NationalActCode = code
        };

        // No payer: this is the laboratory's own price list, which AnalysisTest.TariffFor falls
        // back to for a self-paying patient. AMO tariffs are added per payer by an administrator
        // once the TNR figures for each act are confirmed.
        test.AddTariff(new AnalysisTestTariff(test.Id, payerId: null, tariffValidFrom)
        {
            LetterKey = "B",
            Coefficient = coefficient,
            LetterKeyValue = ReferenceDataSeeder.PlaceholderLetterKeyValueMad,
            Price = Money.Round(coefficient * ReferenceDataSeeder.PlaceholderLetterKeyValueMad),
            // Medical biology acts are VAT-exempt in Morocco.
            IsVatExempt = true,
            VatRate = 0m
        });

        return test;
    }

    private static TestParameter Param(string code, string fr, string ar, string en,
                                       Guid? unitId, int decimals)
    {
        var parameter = new TestParameter(code, new MultilingualText(fr, ar, en), ResultValueType.Numeric)
        {
            UnitId = unitId,
            Decimals = decimals
        };
        return parameter;
    }

    private static ReferenceRange Range(decimal? low, decimal? high,
                                        Gender gender = Gender.Unknown,
                                        int? ageFromDays = null, int? ageToDays = null,
                                        string? source = null) =>
        new(gender)
        {
            LowerLimit = low,
            UpperLimit = high,
            AgeFromDays = ageFromDays,
            AgeToDays = ageToDays,
            Source = source ?? PendingVerification
        };

    private static CodedResultOption Option(string code, string fr, string ar, string en,
                                            bool abnormal = false, bool critical = false,
                                            bool expectedNormal = false) =>
        new(code, new MultilingualText(fr, ar, en))
        {
            IsAbnormal = abnormal,
            IsCritical = critical,
            IsExpectedNormal = expectedNormal
        };

    /// <summary>The panels a reception desk orders as one line.</summary>
    private static async Task SeedPanelsAsync(LisDbContext context, CancellationToken ct)
    {
        if (await context.TestPanels.IgnoreQueryFilters().AnyAsync(ct)) return;

        var byCode = await context.AnalysisTests.ToDictionaryAsync(t => t.Code, t => t.Id, ct);

        (string Code, string Fr, string Ar, string En, string[] Tests)[] panels =
        [
            ("BILAN_STD", "Bilan standard", "الحصيلة العادية", "Standard profile",
             ["NFS", "GLY", "CREAT", "UREE", "TRANSAM", "VS"]),

            ("BILAN_PREOP", "Bilan préopératoire", "حصيلة ما قبل الجراحة", "Pre-operative profile",
             ["NFS", "TP_INR", "GROUPAGE", "GLY", "CREAT", "IONO"]),

            ("BILAN_PRENAT", "Bilan prénatal", "الحصيلة السابقة للولادة", "Antenatal profile",
             ["NFS", "GROUPAGE", "GLY", "CHIM_URIN", "HBS_AG", "TSH"]),

            ("BILAN_DIAB", "Surveillance du diabète", "متابعة السكري", "Diabetes monitoring",
             ["GLY", "HBA1C", "CREAT", "BILAN_LIP", "CHIM_URIN"]),

            ("BILAN_LIP_CV", "Bilan cardiovasculaire", "الحصيلة القلبية الوعائية",
             "Cardiovascular profile",
             ["BILAN_LIP", "GLY", "CRP", "CREAT"])
        ];

        foreach (var p in panels)
        {
            var panel = new TestPanel(p.Code, new MultilingualText(p.Fr, p.Ar, p.En))
            {
                ItemizeOnInvoice = true
            };

            foreach (var testCode in p.Tests)
                if (byCode.TryGetValue(testCode, out var id)) panel.AddAnalysis(id);

            context.TestPanels.Add(panel);
        }

        await context.SaveChangesAsync(ct);
    }
}
