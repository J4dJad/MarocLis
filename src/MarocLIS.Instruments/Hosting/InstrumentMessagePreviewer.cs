using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Instruments.Protocol;

namespace MarocLIS.Instruments.Hosting;

/// <summary>
/// Reads a frame the way the link would, and says what the import would do with each result,
/// without writing anything.
/// <para>
/// It follows the sink's rules in the same order — excluded type, mapping, tube, order, ordered
/// parameter, validation, value — so that what the window promises is what the link then does. It
/// does not check the plausibility limits or the QC targets: those depend on the value being
/// stored, and the window says "would store", not "stored".
/// </para>
/// </summary>
public sealed partial class InstrumentMessagePreviewer(
    IRepository<InstrumentInterface> interfaces,
    IRepository<Sample> samples,
    IRepository<LabOrder> orders,
    IRepository<AnalysisTest> analyses,
    IRepository<TestResult> results,
    IRepository<Patient> patients,
    IRepository<Instrument> instruments,
    IClock clock)
    : IInstrumentMessagePreviewer
{
    public async Task<Result<MessagePreview>> PreviewAsync(
        Guid interfaceId, InstrumentProtocol protocol, InstrumentMessageFormat format,
        UnmappedNonNumericHandling unmappedNonNumeric, string raw, CancellationToken ct = default)
    {
        var link = await interfaces.GetByIdWithDetailsAsync(interfaceId, ct);
        if (link is null) return Result.Failure<MessagePreview>(Error.NotFound("Interface_NotFound"));

        if (string.IsNullOrWhiteSpace(raw))
            return Result.Failure<MessagePreview>(Error.Validation("Interface_RawFrameEmpty"));

        var message = InstrumentMessageSink.Reparse(protocol, Normalise(protocol, raw), format);

        if (message is null)
            return Result.Failure<MessagePreview>(
                Error.Validation("Interface_ProtocolNotReplayable", protocol));

        var issues = message.Issues
            .Select(i => i.Detail is null ? i.ResourceKey : $"{i.ResourceKey}\t{i.Detail}")
            .ToList();

        // Said first: while the analyser itself may not produce results, nothing below is stored.
        var instrument = await instruments.GetByIdAsync(link.InstrumentId, ct);
        var blocked = instrument?.ResultBlockKey(clock.Today);
        if (blocked is not null) issues.Insert(0, $"{blocked}\t{instrument!.Code}");

        if (message.Kind == InstrumentMessageKind.Query && message.QueriedSpecimenIds.Count > 0)
            issues.Add($"Interface_PreviewQuery\t{string.Join(", ", message.QueriedSpecimenIds)}");

        var lines = new List<PreviewLine>();

        foreach (var specimen in message.Specimens)
            lines.AddRange(await PreviewSpecimenAsync(link, unmappedNonNumeric, specimen, ct));

        if (blocked is not null)
            lines = [.. lines.Select(l => l.Outcome is PreviewOutcome.WouldStore
                                                  or PreviewOutcome.WouldStoreControl
                                                  or PreviewOutcome.WouldMarkNotDone
                ? l with { Outcome = PreviewOutcome.InstrumentNotUsable }
                : l)];

        return Result.Success(new MessagePreview(issues, lines));
    }

    private async Task<IReadOnlyList<PreviewLine>> PreviewSpecimenAsync(
        InstrumentInterface link, UnmappedNonNumericHandling unmappedNonNumeric,
        InstrumentSpecimenResults specimen, CancellationToken ct)
    {
        var id = specimen.SpecimenId?.Trim();
        var lines = new List<PreviewLine>();

        if (specimen.Results.Count == 0)
        {
            lines.Add(Line(specimen, null, null, null, PreviewOutcome.NoResults));
            return lines;
        }

        if (string.IsNullOrWhiteSpace(id) && !specimen.IsControl)
        {
            foreach (var incoming in specimen.Results)
                lines.Add(Line(specimen, incoming, Map(link, incoming), null,
                               PreviewOutcome.SpecimenIdMissing));
            return lines;
        }

        if (specimen.IsControl)
        {
            foreach (var incoming in specimen.Results)
            {
                var mapping = Map(link, incoming);

                var early = Screen(incoming, mapping, unmappedNonNumeric);
                if (early is not null) { lines.Add(Line(specimen, incoming, mapping, null, early.Value)); continue; }

                lines.Add(TryNumber(incoming.Value, out var value)
                    ? Line(specimen, incoming, mapping, null, PreviewOutcome.WouldStoreControl,
                           stored: Format(mapping!.Convert(value)),
                           detail: specimen.ControlIdentifier)
                    : Line(specimen, incoming, mapping, null, PreviewOutcome.NotNumeric));
            }

            return lines;
        }

        var matches = await samples.ListAsync(s => s.Barcode == id, ct);

        PreviewOutcome? tubeProblem = matches.Count switch
        {
            0 => PreviewOutcome.SpecimenUnknown,
            > 1 => PreviewOutcome.SpecimenAmbiguous,
            _ => matches[0].Status is SampleStatus.Rejected or SampleStatus.Discarded
                ? PreviewOutcome.SpecimenNotUsable
                : null
        };

        var order = tubeProblem is null
            ? await orders.GetByIdWithDetailsAsync(matches[0].LabOrderId, ct)
            : null;

        if (tubeProblem is null && (order is null || order.Status == LabOrderStatus.Cancelled))
            tubeProblem = PreviewOutcome.OrderNotUsable;

        var patient = order is null ? null : await patients.GetByIdAsync(order.PatientId, ct);

        var stored = order is null
            ? []
            : await results.ListAsync(r => r.LabOrderId == order.Id, ct);

        foreach (var incoming in specimen.Results)
        {
            var mapping = Map(link, incoming);
            var context = (order, patient);

            var early = Screen(incoming, mapping, unmappedNonNumeric);
            if (early is not null) { lines.Add(Line(specimen, incoming, mapping, context, early.Value)); continue; }

            if (tubeProblem is { } problem)
            {
                lines.Add(Line(specimen, incoming, mapping, context, problem,
                               detail: matches.Count == 1 ? matches[0].Status.ToString() : null));
                continue;
            }

            var target = stored.FirstOrDefault(r => r.TestParameterId == mapping!.TestParameterId);

            if (target is null)
            {
                lines.Add(Line(specimen, incoming, mapping, context, PreviewOutcome.NotOrdered));
                continue;
            }

            var parameter = await ParameterAsync(order!, target, mapping!.TestParameterId, ct);

            if (target.IsValidated)
            {
                lines.Add(Line(specimen, incoming, mapping, context, PreviewOutcome.AlreadyValidated,
                               parameter: parameter));
                continue;
            }

            if (incoming.IsNotDone)
            {
                lines.Add(Line(specimen, incoming, mapping, context, PreviewOutcome.WouldMarkNotDone,
                               parameter: parameter));
                continue;
            }

            if (string.IsNullOrWhiteSpace(incoming.Value))
            {
                lines.Add(Line(specimen, incoming, mapping, context, PreviewOutcome.EmptyValue,
                               parameter: parameter));
                continue;
            }

            var raw = incoming.Value.Trim();

            if (parameter?.ValueType is ResultValueType.Numeric or ResultValueType.NumericWithOperator)
            {
                var censoring = raw.StartsWith('<') || raw.StartsWith('>') ? raw[..1] : string.Empty;

                lines.Add(TryNumber(raw, out var number)
                    ? Line(specimen, incoming, mapping, context, PreviewOutcome.WouldStore,
                           stored: censoring + Format(mapping.Convert(number)), parameter: parameter)
                    : Line(specimen, incoming, mapping, context, PreviewOutcome.NotNumeric,
                           parameter: parameter));
                continue;
            }

            var shown = parameter?.ValueType is ResultValueType.Coded or ResultValueType.SemiQuantitative
                ? mapping.TranslateCode(raw)
                : raw;

            lines.Add(Line(specimen, incoming, mapping, context, PreviewOutcome.WouldStore,
                           stored: shown, parameter: parameter));
        }

        return lines;
    }

    /// <summary>The checks that do not need the tube: excluded type and mapping.</summary>
    private static PreviewOutcome? Screen(InstrumentResult incoming, InstrumentTestMapping? mapping,
                                          UnmappedNonNumericHandling unmappedNonNumeric)
    {
        if (incoming.IsExcluded) return PreviewOutcome.ExcludedType;
        if (mapping is not null) return null;

        return InstrumentMessageSink.IgnoredAsUnmappedNonNumeric(unmappedNonNumeric, incoming)
            ? PreviewOutcome.IgnoredNonNumeric
            : PreviewOutcome.UnmappedCode;
    }

    private static InstrumentTestMapping? Map(InstrumentInterface link, InstrumentResult incoming) =>
        InstrumentMessageSink.FindMapping(link.Mappings, incoming.InstrumentTestCode);

    private static PreviewLine Line(InstrumentSpecimenResults specimen, InstrumentResult? incoming,
                                    InstrumentTestMapping? mapping,
                                    (LabOrder? Order, Patient? Patient)? context,
                                    PreviewOutcome outcome, string? stored = null,
                                    TestParameter? parameter = null, string? detail = null) =>
        new(specimen.SpecimenId?.Trim() is { Length: > 0 } id ? id : specimen.ControlIdentifier,
            specimen.IsControl,
            context?.Order?.AccessionNumber,
            context?.Patient?.FullName,
            incoming?.RawTestId,
            incoming?.InstrumentTestCode,
            mapping?.TestParameterId,
            parameter?.Code,
            incoming?.Value,
            stored,
            incoming?.Unit,
            incoming is null ? null : InstrumentMessageSink.DescribeInstrumentFlags(incoming),
            incoming?.ResultType,
            outcome,
            detail);

    private async Task<TestParameter?> ParameterAsync(LabOrder order, TestResult target,
                                                      Guid parameterId, CancellationToken ct)
    {
        var item = order.Items.FirstOrDefault(i => i.Id == target.LabOrderItemId);
        if (item is null) return null;

        var analysis = await analyses.GetByIdWithDetailsAsync(item.AnalysisTestId, ct);
        return analysis?.Parameters.FirstOrDefault(p => p.Id == parameterId);
    }

    private static bool TryNumber(string? raw, out decimal value)
    {
        var text = raw?.TrimStart('<', '>').Trim();

        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               || decimal.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("fr-FR"),
                                   out value);
    }

    private static string Format(decimal value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    // ---- Pasted text ----------------------------------------------------------------------------

    /// <summary>
    /// Turns what somebody pasted into what the parser expects.
    /// <para>
    /// A frame copied from an analyser's manual or a serial-port capture arrives in every possible
    /// shape: real line breaks, the literal "&lt;CR&gt;" a trace tool prints, ASTM framing still on
    /// it ("&lt;STX&gt;1H|…&lt;ETX&gt;A4"). Each is undone here so the test reads the content, not
    /// the transcription.
    /// </para>
    /// </summary>
    public static string Normalise(InstrumentProtocol protocol, string raw)
    {
        if (protocol == InstrumentProtocol.Poct1A) return raw.Trim();

        // The INTEGRA parser reads its own framing, written out or real; its sums must see every byte.
        if (protocol == InstrumentProtocol.RocheIntegra) return RocheIntegra.FromTranscript(raw);

        var text = raw.Replace("<CR><LF>", "\r", StringComparison.OrdinalIgnoreCase)
                      .Replace("<CR>", "\r", StringComparison.OrdinalIgnoreCase)
                      .Replace("<LF>", "\r", StringComparison.OrdinalIgnoreCase)
                      .Replace("\r\n", "\r")
                      .Replace('\n', '\r');

        // Framing written out as text, or the real control characters left in a capture.
        text = ControlToken().Replace(text, "\r");
        text = text.Replace('\x02', '\r').Replace('\x05', '\r').Replace('\x04', '\r')
                   .Replace('\x06', '\r').Replace('\x15', '\r').Replace('\x0b', '\r')
                   .Replace("\x1c", "\r");
        text = RealFrameEnd().Replace(text, "\r");

        var lines = text.Split('\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var record = line;

            // "1H|\^&" — the ASTM frame number left in front of the record type.
            if (protocol == InstrumentProtocol.AstmE1394 && FrameNumber().IsMatch(record))
                record = record[1..];

            // A checksum on a line of its own, left behind by "<ETX>A4" on the next line.
            if (protocol == InstrumentProtocol.AstmE1394 && Checksum().IsMatch(record)) continue;

            kept.Add(record);
        }

        return string.Join('\r', kept);
    }

    [GeneratedRegex(@"<(STX|ETX|ETB|ENQ|EOT|ACK|NAK|VT|FS)>[0-9A-Fa-f]{0,2}", RegexOptions.IgnoreCase)]
    private static partial Regex ControlToken();

    [GeneratedRegex("[\x03\x17][0-9A-Fa-f]{0,2}")]
    private static partial Regex RealFrameEnd();

    [GeneratedRegex(@"^[0-7][A-Za-z]\|")]
    private static partial Regex FrameNumber();

    [GeneratedRegex("^[0-9A-Fa-f]{2}$")]
    private static partial Regex Checksum();
}
