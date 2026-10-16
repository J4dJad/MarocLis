using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.Orders;

/// <summary>What the counter has typed so far, to be priced before it is registered.</summary>
public sealed record OrderQuoteRequest
{
    /// <summary>None yet: the analyses are priced on the laboratory's own list, paid by the patient.</summary>
    public Guid? PatientId { get; init; }

    public IReadOnlyList<Guid> AnalysisTestIds { get; init; } = [];
    public IReadOnlyList<Guid> TestPanelIds { get; init; } = [];
    public bool BillDirectlyToPatient { get; init; }
}

/// <summary>One tube to draw: its container, colour and nominal volume, and what goes in it.</summary>
public sealed record TubeToDraw(
    MultilingualText Name,
    string ColourHex,
    decimal? VolumeMl,
    IReadOnlyList<string> AnalysisCodes);

/// <summary>
/// The request as registration would price and tube it.
/// <para>
/// <see cref="Problems"/> holds what registration would refuse — a missing consent, an analysis
/// with no tariff — so the counter can say so before the clerk presses the button.
/// </para>
/// </summary>
public sealed record OrderQuote(
    OrderPricing Pricing,
    MultilingualText? PayerName,
    int AnalysisCount,
    IReadOnlyList<TubeToDraw> Tubes,
    IReadOnlyList<Error> Problems);

public interface IOrderQuotes
{
    Task<Result<OrderQuote>> QuoteAsync(OrderQuoteRequest request, CancellationToken ct = default);
}

/// <summary>
/// Prices a request before it is registered, for the counter's running total.
/// <para>
/// No rule of its own: the analyses are resolved, the coverage chosen, the acts priced and the
/// tubes grouped by the very calls <see cref="OrderRegistrationService"/> makes. The counter used
/// to add catalogue prices on its own and showed a gross figure only; the share the payer takes and
/// the amount the patient owes today came after the save, in a message box.
/// </para>
/// <para>
/// Read-only: nothing is added, numbered or saved.
/// </para>
/// </summary>
public sealed class OrderQuoteService(
    IRepository<Patient> patients,
    IRepository<Payer> payers,
    OrderComposer composer,
    IClock clock) : IOrderQuotes
{
    public async Task<Result<OrderQuote>> QuoteAsync(OrderQuoteRequest request, CancellationToken ct = default)
    {
        var resolved = await composer.ResolveAsync(request.AnalysisTestIds, request.TestPanelIds, ct);
        if (resolved.IsFailure) return Result.Failure<OrderQuote>(resolved.Errors);

        var selected = resolved.Value;
        var today = clock.Today;
        var problems = new List<Error>();

        var patient = request.PatientId is { } patientId
            ? await patients.GetByIdWithDetailsAsync(patientId, ct)
            : null;

        if (request.PatientId is not null && patient is null)
            return Result.Failure<OrderQuote>(Error.NotFound("Order_PatientNotFound"));

        var coverage = patient is null
            ? new OrderComposer.ResolvedCoverage(null, null, null, CoverageContext.SelfPay)
            : await composer.ResolveCoverageAsync(patient, null, request.BillDirectlyToPatient, today, ct);

        if (patient is not null) problems.AddRange(OrderComposer.CheckConsents(patient, selected, today));
        problems.AddRange(OrderComposer.CheckTariffs(selected, coverage.PayerId, today));

        var pricing = ActPricingCalculator.Price(
            OrderComposer.BuildPricingInputs(selected, coverage.PayerId, today), coverage.Context);

        MultilingualText? payerName = null;
        if (coverage.PayerId is { } payerId)
            payerName = (await payers.GetByIdAsync(payerId, ct))?.Name;

        var tubes = OrderComposer.GroupByTube(selected)
            .Select(group =>
            {
                var test = group.First().Test;
                var container = test.ContainerType;
                return new TubeToDraw(
                    container?.Name ?? test.SpecimenType?.Name ?? new MultilingualText("-"),
                    container?.CapColourHex ?? "#FFFFFF",
                    container?.NominalVolumeMl,
                    [.. group.Select(a => a.Test.Code)]);
            })
            .ToList();

        return Result.Success(new OrderQuote(pricing, payerName, selected.Count, tubes, problems));
    }
}
