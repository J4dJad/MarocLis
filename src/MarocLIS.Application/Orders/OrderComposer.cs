using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Orders;

/// <summary>
/// Everything involved in putting analyses onto an order: resolution, the consent and fasting
/// gates, coverage, pricing, tubes and the awaited result rows.
/// <para>
/// It lives in one place because two paths add analyses to an order — registration, and an addition
/// to an order that already exists — and the second one is the dangerous one. An added analysis
/// that skipped the consent gate would be a genetic or HIV test performed without the written
/// consent that loi 08-09 and professional practice both require to pre-date the act. An added
/// analysis priced by a second implementation would be priced differently from the same analysis
/// ordered five minutes earlier. Neither is a defect that shows up in testing; both are defects
/// that show up in an inspection.
/// </para>
/// </summary>
public sealed class OrderComposer(
    IRepository<AnalysisTest> analyses,
    IRepository<TestPanel> panels,
    IRepository<Sample> samples,
    IRepository<TestResult> results,
    IRepository<Payer> payers,
    ISequenceAllocator sequences,
    ICurrentUser currentUser,
    IClock clock,
    OrderRegistrationOptions options)
{
    /// <param name="PanelPackagePrice">
    /// The package price of the panel this analysis came in with, when the panel has one: the
    /// members are then billed that price between them rather than their own tariffs.
    /// </param>
    public sealed record SelectedAnalysis(AnalysisTest Test, Guid? PanelId, decimal? PanelPackagePrice = null);

    public sealed record ResolvedCoverage(
        Guid? PayerId, Guid? CoverageId, string? PriorAgreementNumber, CoverageContext Context);

    // ---- Analysis resolution -------------------------------------------------------------------

    /// <summary>
    /// Resolves explicit analyses plus every panel's mandatory members, de-duplicated.
    /// </summary>
    public async Task<Result<List<SelectedAnalysis>>> ResolveAsync(
        IReadOnlyList<Guid> analysisTestIds, IReadOnlyList<Guid> testPanelIds,
        CancellationToken ct = default)
    {
        var wanted = new Dictionary<Guid, Guid?>();
        var packages = new Dictionary<Guid, decimal>();

        foreach (var id in analysisTestIds)
            wanted.TryAdd(id, null);

        foreach (var panelId in testPanelIds)
        {
            var panel = await panels.GetByIdWithDetailsAsync(panelId, ct);
            if (panel is null)
                return Result.Failure<List<SelectedAnalysis>>(Error.NotFound("Order_PanelNotFound", panelId));

            // An analysis already chosen individually keeps its individual pricing; a panel does
            // not silently add a second copy of it. A panel sold at a package price is the
            // exception: it claims its members, since the package already pays for them and
            // billing one of them again on its own would charge it twice.
            if (panel.PackagePrice is { } package)
            {
                packages[panel.Id] = package;
                foreach (var analysisId in panel.MandatoryAnalysisIds)
                    wanted[analysisId] = panel.Id;
            }
            else
            {
                foreach (var analysisId in panel.MandatoryAnalysisIds)
                    wanted.TryAdd(analysisId, panel.Id);
            }
        }

        var selected = new List<SelectedAnalysis>(wanted.Count);

        foreach (var (analysisId, panelId) in wanted)
        {
            var test = await analyses.GetByIdWithDetailsAsync(analysisId, ct);
            if (test is null)
                return Result.Failure<List<SelectedAnalysis>>(Error.NotFound("Order_AnalysisNotFound", analysisId));
            if (!test.IsActive)
                return Result.Failure<List<SelectedAnalysis>>(Error.Validation("Order_AnalysisInactive", test.Code));

            selected.Add(new SelectedAnalysis(
                test, panelId,
                panelId is { } p && packages.TryGetValue(p, out var price) ? price : null));
        }

        return Result.Success(selected);
    }

    // ---- Gates ---------------------------------------------------------------------------------

    /// <summary>
    /// Consent gates. An analysis requiring specific written consent cannot be added without it:
    /// loi 08-09 and professional practice both require the consent to pre-date the act, so the
    /// moment the analysis is put on the order is the only point where refusing is still honest.
    /// </summary>
    public static List<Error> CheckConsents(Patient patient,
                                            IReadOnlyList<SelectedAnalysis> selected,
                                            DateOnly today)
    {
        var errors = new List<Error>();

        foreach (var analysis in selected)
        {
            var purpose = analysis.Test.ConsentRequirement switch
            {
                ConsentRequirement.Genetic => ConsentPurpose.GeneticTesting,
                ConsentRequirement.Hiv => ConsentPurpose.HivSerology,
                ConsentRequirement.Prenatal => ConsentPurpose.PrenatalScreening,
                _ => (ConsentPurpose?)null
            };

            if (purpose is not { } required) continue;

            var consent = patient.ActiveConsent(required);

            if (consent is null || !consent.IsEffectiveOn(today))
            {
                errors.Add(Error.Regulatory("Order_ConsentMissing", analysis.Test.Name.Fr));
                continue;
            }

            // For these purposes a verbal note is not enough — the consent must be in writing.
            if (PatientConsent.RequiresWrittenEvidence(required) &&
                consent.Channel == ConsentChannel.Verbal)
                errors.Add(Error.Regulatory("Order_ConsentMustBeWritten", analysis.Test.Name.Fr));
        }

        return errors;
    }

    /// <summary>
    /// Fasting is advisory, not blocking: a clinician may knowingly accept a non-fasting sample.
    /// </summary>
    public static IEnumerable<Error> CheckFasting(bool? patientFasted, int? declaredFastingHours,
                                                   IReadOnlyList<SelectedAnalysis> selected)
    {
        if (patientFasted is not false && declaredFastingHours is null) yield break;

        foreach (var analysis in selected)
        {
            var required = analysis.Test.EffectiveFastingHours(
                analysis.Test.SpecimenType?.FastingHours ?? 0);

            if (required <= 0) continue;

            if (patientFasted is false ||
                (declaredFastingHours is { } declared && declared < required))
                yield return Error.Validation("Order_FastingNotRespected",
                                              analysis.Test.Name.Fr, required);
        }
    }

    // ---- Coverage and pricing ------------------------------------------------------------------

    public async Task<ResolvedCoverage> ResolveCoverageAsync(Patient patient,
                                                              Guid? explicitCoverageId,
                                                              bool billDirectlyToPatient,
                                                              DateOnly today,
                                                              CancellationToken ct = default)
    {
        if (billDirectlyToPatient)
            return new ResolvedCoverage(null, null, null, CoverageContext.SelfPay);

        var coverage = explicitCoverageId is { } explicitId
            ? patient.Coverages.FirstOrDefault(c => c.Id == explicitId)
            : patient.PrimaryCoverage(today);

        if (coverage is null || !coverage.IsValidOn(today))
            return new ResolvedCoverage(null, null, null, CoverageContext.SelfPay);

        var payer = coverage.Payer ?? await payers.GetByIdAsync(coverage.PayerId, ct);

        if (payer is null || !payer.IsActive || payer.Kind == PayerKind.SelfPay)
            return new ResolvedCoverage(null, null, null, CoverageContext.SelfPay);

        // An expired third-party agreement means the laboratory cannot bill the payer directly;
        // the entitlement still exists, so the patient advances the money and claims it back.
        var agreementValid = payer.IsAgreementValid(today);

        return new ResolvedCoverage(
            payer.Id,
            coverage.Id,
            coverage.PriorAgreementNumber,
            new CoverageContext
            {
                PayerId = payer.Id,
                ReimbursementRate = coverage.EffectiveRate(today, payer.DefaultReimbursementRate),
                UsesReferenceTariff = payer.UsesNationalReferenceTariff,
                DirectBilling = coverage.DirectBillingEnabled && payer.ThirdPartyPaymentAgreement
                                && agreementValid
            });
    }

    /// <summary>
    /// The coverage an order was registered under, rebuilt from its snapshot for an analysis added
    /// later: the frozen rate and direct-billing decision, and the payer's own tariff basis — a
    /// payer reimbursing a share of the laboratory's price is not split on the reference tariff.
    /// </summary>
    public async Task<CoverageContext> RegisteredCoverageAsync(LabOrder order, CancellationToken ct = default)
    {
        if (order.PayerId is not { } payerId) return CoverageContext.SelfPay;

        var payer = await payers.GetByIdAsync(payerId, ct);

        return new CoverageContext
        {
            PayerId = payerId,
            ReimbursementRate = order.ReimbursementRateAtRegistration ?? 0m,
            UsesReferenceTariff = payer?.UsesNationalReferenceTariff ?? true,
            DirectBilling = order.DirectBillingAtRegistration
        };
    }

    /// <summary>
    /// Refuses an analysis with no tariff in force for the payer or on the laboratory's own list.
    /// It used to be priced at nothing without a word, and the first anyone heard of it was an
    /// invoice short by the price of the analysis. A member of a panel sold at a package price
    /// needs no tariff of its own: the package pays for it.
    /// </summary>
    public static List<Error> CheckTariffs(IReadOnlyList<SelectedAnalysis> selected,
                                           Guid? payerId, DateOnly today) =>
    [
        .. selected
            .Where(a => a.PanelPackagePrice is null && a.Test.TariffFor(payerId, today) is null)
            .Select(a => Error.Validation("Order_AnalysisWithoutTariff", a.Test.Code))
    ];

    public static List<ActPricingInput> BuildPricingInputs(IReadOnlyList<SelectedAnalysis> selected,
                                                     Guid? payerId, DateOnly today)
    {
        var inputs = new List<ActPricingInput>(selected.Count);

        foreach (var analysis in selected)
        {
            var tariff = analysis.Test.TariffFor(payerId, today);

            inputs.Add(new ActPricingInput
            {
                AnalysisTestId = analysis.Test.Id,
                AnalysisCode = analysis.Test.Code,
                Description = analysis.Test.Name.Fr,
                DescriptionArabic = analysis.Test.Name.Ar,
                Quantity = 1,
                ServiceDate = today,
                NationalActCode = analysis.Test.NationalActCode,
                LetterKey = tariff?.LetterKey ?? "B",
                Coefficient = tariff?.Coefficient ?? 0m,
                LetterKeyValue = tariff?.LetterKeyValue ?? 0m,
                ChargedUnitPrice = tariff?.Price ?? 0m,
                IsVatExempt = tariff?.IsVatExempt ?? true,
                VatRate = tariff?.VatRate ?? 0m,
                TestPanelId = analysis.PanelId
            });
        }

        ApplyPackagePrices(inputs, selected);

        return inputs;
    }

    /// <summary>
    /// Bills a panel sold at a package price at that price, spread over its members in proportion
    /// to their own prices so each invoice line stays meaningful; members with no price of their
    /// own share it equally. The rounding remainder goes to the last member, so the lines add up
    /// to the package exactly. The reference base is left alone: the payer reimburses each act on
    /// the nomenclature, whatever the laboratory charges for the package.
    /// </summary>
    private static void ApplyPackagePrices(List<ActPricingInput> inputs,
                                           IReadOnlyList<SelectedAnalysis> selected)
    {
        var groups = selected
            .Select((analysis, index) => (analysis, index))
            .Where(x => x.analysis.PanelId is not null && x.analysis.PanelPackagePrice is not null)
            .GroupBy(x => x.analysis.PanelId);

        foreach (var group in groups)
        {
            var members = group.ToList();
            var package = Math.Round(members[0].analysis.PanelPackagePrice!.Value, 2);
            var own = members.Sum(m => inputs[m.index].ChargedUnitPrice);
            var allocated = 0m;

            for (var i = 0; i < members.Count; i++)
            {
                var index = members[i].index;

                var share = i == members.Count - 1
                    ? package - allocated
                    : Math.Round(own > 0m
                                     ? package * inputs[index].ChargedUnitPrice / own
                                     : package / members.Count, 2);

                allocated += share;
                inputs[index] = inputs[index] with { ChargedUnitPrice = share };
            }
        }
    }

    /// <summary>
    /// Builds an order item from a priced act. One implementation, so an analysis added later
    /// carries the same frozen price, reference base and coverage split as one ordered at the
    /// counter.
    /// </summary>
    public static LabOrderItem BuildItem(PricedAct priced, SelectedAnalysis analysis,
                                          OrderPriority priority) =>
        new(analysis.Test.Id, priority)
        {
            TestPanelId = priced.Input.TestPanelId,
            DepartmentId = analysis.Test.DepartmentId,
            ChargedPrice = priced.Net.Amount,
            ReferenceTariffBase = priced.ReferenceBase.Amount,
            BilledLetterKey = priced.Input.LetterKey,
            BilledCoefficient = priced.Input.Coefficient,
            BilledActCode = priced.Input.NationalActCode,
            PayerShare = priced.PayerShare.Amount,
            PatientShare = priced.PatientShare.Amount,
            IsSubcontracted = analysis.Test.IsSubcontracted,
            SubcontractorSiteId = analysis.Test.SubcontractorSiteId
        };

    // ---- Samples and awaited results -----------------------------------------------------------

    /// <summary>
    /// The tubes a set of analyses needs: one per specimen and container, in the order they are
    /// created. Shared by registration and by the counter's preview, so the tubes announced before
    /// saving are the tubes that get labelled.
    /// </summary>
    public static List<IGrouping<(Guid SpecimenTypeId, Guid? ContainerTypeId), SelectedAnalysis>> GroupByTube(
        IEnumerable<SelectedAnalysis> selected) =>
        // Subcontracted analyses still need a tube; analyses with no container defined (a
        // patient-brought urine pot, say) group under a null key and get one container each.
        [.. selected
            .GroupBy(a => (a.Test.SpecimenTypeId, a.Test.ContainerTypeId))
            .OrderBy(g => g.Key.ContainerTypeId is null ? 1 : 0)];

    /// <summary>
    /// Creates one sample per distinct container the order needs and assigns each analysis to it.
    /// <para>
    /// Grouping by container rather than by analysis is what keeps the draw to the minimum number
    /// of tubes — the difference between three tubes and eleven for a standard profile, which the
    /// patient in the chair notices.
    /// </para>
    /// <para>
    /// <paramref name="reusable"/> carries the tubes the order already has. An analysis whose
    /// container is already drawn is attached to that tube instead of provoking a second needle,
    /// which is the whole point of adding an analysis to an existing order rather than registering
    /// a new one. It is the caller's job to warn when the reused tube has already been received:
    /// whether the residual volume is enough is a question only the person holding it can answer.
    /// </para>
    /// </summary>
    public async Task<List<SampleLabel>> CreateSamplesAsync(
        LabOrder order, Patient patient, IReadOnlyList<SelectedAnalysis> selected,
        IReadOnlyList<Sample>? reusable = null, CancellationToken ct = default)
    {
        var groups = GroupByTube(selected);

        var labels = new List<SampleLabel>(groups.Count);
        var sequence = (reusable?.Count ?? 0) + 1;

        foreach (var group in groups)
        {
            var existing = reusable?.FirstOrDefault(
                s => s.SpecimenTypeId == group.Key.SpecimenTypeId
                     && s.ContainerTypeId == group.Key.ContainerTypeId);

            if (existing is not null)
            {
                Bind(group, existing.Id);
                continue;
            }

            var barcode = await sequences.NextAsync(SequencePurpose.SampleBarcode, null, null, ct);
            if (barcode.IsFailure) continue;

            var sample = new Sample(barcode.Value, order.Id, group.Key.SpecimenTypeId,
                                    currentUser.SiteId)
            {
                ContainerTypeId = group.Key.ContainerTypeId,
                SequenceInOrder = sequence,
                CollectionLocation = order.Origin switch
                {
                    OrderOrigin.HomeCollection => CollectionLocation.PatientHome,
                    OrderOrigin.CollectionCentre => CollectionLocation.CollectionCentre,
                    OrderOrigin.Institution => CollectionLocation.HospitalWard,
                    OrderOrigin.OccupationalHealth => CollectionLocation.Workplace,
                    _ => CollectionLocation.Laboratory
                },
                KeepUntil = clock.Today.AddDays(options.SampleRetentionDays)
            };

            sample.AddEvent(SampleEventKind.LabelPrinted, clock.Now, currentUser.UserId,
                            $"Dossier {order.AccessionNumber}", workstation: currentUser.Workstation);

            await samples.AddAsync(sample, ct);

            // Bind each analysis of the group to this tube, so result entry knows which sample a
            // value came from and sample rejection knows which analyses it invalidates.
            Bind(group, sample.Id);

            var container = group.First().Test.ContainerType;

            labels.Add(new SampleLabel(
                sample.Id,
                sample.Barcode,
                patient.FullName,
                patient.FullNameArabic,
                patient.DateOfBirth,
                order.AccessionNumber,
                container?.Name.Fr ?? group.First().Test.SpecimenType?.Name.Fr ?? "-",
                container?.CapColourHex ?? "#FFFFFF",
                sequence,
                groups.Count,
                [.. group.Select(a => a.Test.Code)],
                order.IsUrgent,
                clock.Now));

            sequence++;
        }

        return labels;

        void Bind(IEnumerable<SelectedAnalysis> group, Guid sampleId)
        {
            foreach (var analysis in group)
            {
                var item = order.Items.FirstOrDefault(
                    i => i.AnalysisTestId == analysis.Test.Id && !i.IsCancelled);

                if (item is not null) item.SampleId = sampleId;
            }
        }
    }

    /// <summary>
    /// Creates the awaited result rows for every parameter of the given items.
    /// <para>
    /// Created up front rather than on first entry so the department worklist, the turnaround
    /// report and the report skeleton all exist from the moment the analysis is ordered. A
    /// technician then types into a row that is already there, which also means a result can never
    /// exist without the order item it belongs to.
    /// </para>
    /// </summary>
    public async Task CreateAwaitedResultsAsync(LabOrder order,
                                                 IReadOnlyList<SelectedAnalysis> selected,
                                                 IEnumerable<LabOrderItem> items,
                                                 CancellationToken ct = default)
    {
        foreach (var item in items.Where(i => !i.IsCancelled))
        {
            var analysis = selected.FirstOrDefault(a => a.Test.Id == item.AnalysisTestId);
            if (analysis is null) continue;

            foreach (var parameter in analysis.Test.Parameters.Where(p => p.IsActive))
            {
                // A section heading carries no result and gets no row.
                if (parameter.ValueType == ResultValueType.Heading) continue;

                await results.AddAsync(
                    new TestResult(order.Id, item.Id, parameter.Id, currentUser.SiteId)
                    {
                        DepartmentId = analysis.Test.DepartmentId,
                        AppliedDecimals = parameter.Decimals,
                        AppliedUnitSymbol = parameter.Unit?.DisplaySymbol
                    }, ct);
            }
        }
    }

    /// <summary>Promised completion: the latest of the per-analysis promises.</summary>
    public DateTimeOffset PromisedCompletion(IReadOnlyList<SelectedAnalysis> selected, bool urgent) =>
        selected.Select(s => s.Test.ExpectedCompletion(clock.Now, urgent))
                .DefaultIfEmpty(clock.Now)
                .Max();
}
