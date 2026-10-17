using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Quality;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Protocol;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Hosting;

/// <summary>
/// Where a driver's parsed messages become stored results.
/// <para>
/// This is the component the whole interfacing project was missing. Drivers, transports and parsers
/// were written and compiled, but nothing implemented this contract — so the project was unreachable
/// at runtime and every result still arrived in the laboratory by hand.
/// </para>
/// <para>
/// Five rules govern it, and each exists because of a specific way an analyser interface produces a
/// wrong patient result:
/// </para>
/// <list type="number">
/// <item>A specimen identifier that does not resolve to exactly one tube is never guessed at. One
/// barcode matching two tubes, or none, is logged and dropped — attaching a value to the wrong tube
/// is the single worst thing a LIS can do.</item>
/// <item>A control specimen never enters a patient file. Analysers transmit controls down the same
/// link as patients, and telling them apart at the boundary is what stops a control value being
/// reported as somebody's potassium.</item>
/// <item>An unmapped instrument code is never inferred from its name. "NA" is sodium on one
/// analyser and not-applicable on another; an unrecognised code is an issue on the transmission,
/// not a guess.</item>
/// <item>A validated result is never overwritten. The domain refuses it, and the refusal is
/// surfaced as an issue rather than swallowed — a corrected value after validation has to go
/// through the amendment path so the clinician holding the report learns of the change.</item>
/// <item>Results are attributed to a named service account with the from-instrument flag set, never
/// to whichever operator happened to be signed in. A record saying a technician entered a value
/// they never saw is a falsified record.</item>
/// </list>
/// </summary>
public sealed class InstrumentMessageSink(
    IRepository<InstrumentInterface> interfaces,
    IRepository<Sample> samples,
    IRepository<LabOrder> orders,
    IRepository<AnalysisTest> analyses,
    IRepository<TestResult> results,
    IRepository<Patient> patients,
    IRepository<QcTarget> qcTargets,
    IRepository<QcResult> qcResults,
    IRepository<QcMaterial> qcMaterials,
    IQualityQueries qualityQueries,
    IRepository<Instrument> instruments,
    ResultPostEntryPipeline pipeline,
    IInstrumentMessageLogStore messageLog,
    IUnitOfWork unitOfWork,
    IClock clock,
    IInstrumentServiceAccount serviceAccount,
    ILogger<InstrumentMessageSink>? logger = null)
    : IInstrumentMessageSink, IInstrumentResultReplayer
{
    public async Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message,
                                         CancellationToken ct = default)
    {
        var link = await interfaces.GetByIdWithDetailsAsync(interfaceId, ct);

        if (link is null)
        {
            logger?.LogError("Message for unknown interface {Interface} discarded.", interfaceId);
            return false;
        }

        var now = clock.Now;

        // The frame is recorded first and on its own. A transmission that cannot be imported must
        // still be on record: it is the evidence of what the analyser sent, and it is what a replay
        // re-imports once the missing mapping is added.
        // Recorded as failed until the outcome is known: an import that throws, or a workstation
        // that stops half-way, then leaves a row that can be replayed and that the retention purge
        // holds back — not a "received" one, which it deletes as history.
        var logId = await messageLog.RecordAsync(
            new InstrumentMessageLog(link.Id, link.SiteId, MessageDirection.Inbound,
                                      message.RawText ?? string.Empty, now)
            {
                Status = InstrumentMessageProcessingStatus.Failed,
                Issues = Describe("Interface_ImportInterrupted"),
                SpecimenIds = string.Join(", ", message.Specimens
                    .Select(s => s.SpecimenId)
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
                ResultCount = message.Specimens.Sum(s => s.Results.Count)
            }, ct);

        var issues = new List<string>();

        foreach (var issue in message.Issues)
            issues.Add(Describe(issue.ResourceKey, issue.Detail));

        if (message.HasBlockingIssues)
        {
            await CloseAsync(logId, InstrumentMessageProcessingStatus.Failed, message, 0, issues, ct);
            return false;
        }

        if (message.Kind != InstrumentMessageKind.Results)
        {
            await CloseAsync(logId, InstrumentMessageProcessingStatus.Ignored, message, 0, issues, ct);
            return true;
        }

        // An analyser declared out of service, retired or out of calibration may not produce
        // patient results. Held here, at reception, with the reason on the log line — not left to
        // be discovered at biological validation, after the technician has already moved on. The
        // frame stays replayable once the instrument is back in service.
        if (await EquipmentBlockAsync(link, ct) is { } blocked)
        {
            issues.Add(blocked);
            await CloseAsync(logId, InstrumentMessageProcessingStatus.Failed, message, 0, issues, ct);
            return false;
        }

        // Auto-acceptance off means a technician reviews the transmission before it reaches a
        // patient file. The frame is held in the log, not partially applied: half-imported results
        // are harder to reason about than none, and the replay path exists precisely for this.
        if (!link.AutoAcceptResults)
        {
            issues.Add("Interface_HeldForAcceptance");

            await CloseAsync(logId, InstrumentMessageProcessingStatus.AwaitingAcceptance,
                              message, 0, issues, ct);
            return true;
        }

        var imported = await ImportAsync(link, message, issues, ct);

        var status = imported == 0
            ? InstrumentMessageProcessingStatus.Failed
            : issues.Count == 0
                ? InstrumentMessageProcessingStatus.Imported
                : InstrumentMessageProcessingStatus.PartiallyImported;

        await CloseAsync(logId, status, message, imported, issues, ct);

        link.LastMessageAt = now;
        await unitOfWork.SaveChangesAsync(ct);

        return imported > 0;
    }

    /// <summary>
    /// Re-imports a frame already in the log, after a mapping was added or auto-acceptance was
    /// granted. The frame is not re-received; it is re-read, which is what makes a lost transmission
    /// a re-import rather than a redraw.
    /// <para>
    /// Every precondition is checked here rather than only in the screen that offers the button. A
    /// replay re-parses stored text and writes patient values: it is exactly the kind of operation
    /// that must refuse, not merely be hidden.
    /// </para>
    /// </summary>
    public async Task<Result<int>> ReplayAsync(Guid logId, Guid byUserId,
                                                CancellationToken ct = default)
    {
        var entry = await messageLog.GetByIdAsync(logId, ct);
        if (entry is null) return Result.Failure<int>(Error.NotFound("Interface_MessageNotFound"));

        // Outbound frames are what we sent. Replaying one would mean re-importing our own worklist
        // as though an analyser had reported it.
        if (entry.Direction != MessageDirection.Inbound)
            return Result.Failure<int>(Error.Validation("Interface_OnlyInboundReplayable"));

        if (entry.Status is not (InstrumentMessageProcessingStatus.Failed
                                 or InstrumentMessageProcessingStatus.PartiallyImported
                                 or InstrumentMessageProcessingStatus.AwaitingAcceptance))
            return Result.Failure<int>(
                Error.Conflict("Interface_StatusNotReplayable", entry.Status));

        if (string.IsNullOrWhiteSpace(entry.RawContent))
            return Result.Failure<int>(Error.Validation("Interface_RawFrameEmpty"));

        // Checked before the interface is loaded, not after. Without a resolved service account the
        // import would attribute every value to Guid.Empty, so this refusal is absolute and does not
        // depend on anything the link says — and a refusal that first reads the database has already
        // done work it had no business doing.
        if (!await serviceAccount.EnsureResolvedAsync(ct))
            return Result.Failure<int>(Error.Regulatory("Interface_ServiceAccountMissing"));

        var link = await interfaces.GetByIdWithDetailsAsync(entry.InstrumentInterfaceId, ct);
        if (link is null) return Result.Failure<int>(Error.NotFound("Interface_NotFound"));

        if (await EquipmentBlockAsync(link, ct) is { } blocked)
        {
            var parts = blocked.Split('\t', 2);
            return Result.Failure<int>(Error.Regulatory(parts[0], parts.Length > 1 ? parts[1] : null));
        }

        var reparsed = Reparse(link.Protocol, entry.RawContent, link.MessageFormat);

        if (reparsed is null)
            return Result.Failure<int>(
                Error.Validation("Interface_ProtocolNotReplayable", link.Protocol));

        if (reparsed.HasBlockingIssues)
            return Result.Failure<int>(Error.Validation(
                "Interface_RawFrameStillUnparsable",
                string.Join("; ", reparsed.Issues.Where(i => i.IsBlocking)
                                                  .Select(i => i.ResourceKey))));

        return await ReplayParsedAsync(logId, link, reparsed, byUserId, ct);
    }

    /// <summary>The issue that holds the link's results back, or null when its analyser may produce them.</summary>
    private async Task<string?> EquipmentBlockAsync(InstrumentInterface link, CancellationToken ct)
    {
        var instrument = await instruments.GetByIdAsync(link.InstrumentId, ct);
        if (instrument is null) return Describe("Instrument_NotFound");

        return instrument.ResultBlockKey(clock.Today) is { } key
            ? Describe(key, instrument.Code)
            : null;
    }

    /// <summary>
    /// Re-parses a stored frame. ASTM stores its records joined by CR and HL7 stores the message
    /// verbatim, so each protocol is undone the way <see cref="InstrumentMessage.RawText"/> was
    /// built — not by a shared guess at the line ending.
    /// </summary>
    internal static InstrumentMessage? Reparse(InstrumentProtocol protocol, string raw,
                                               InstrumentMessageFormat format) =>
        protocol switch
        {
            InstrumentProtocol.AstmE1394 => AstmMessageParser.Parse(
                raw.Split('\r', StringSplitOptions.RemoveEmptyEntries), format),
            InstrumentProtocol.Hl7V2 => Hl7ResultParser.Parse(raw, format),
            InstrumentProtocol.Poct1A => Poct1AResultParser.Parse(raw, format),
            InstrumentProtocol.RocheIntegra => IntegraMessageParser.Parse(raw, format),
            _ => null
        };

    private async Task<Result<int>> ReplayParsedAsync(Guid logId, InstrumentInterface link,
                                                       InstrumentMessage message, Guid byUserId,
                                                       CancellationToken ct)
    {
        var issues = new List<string>();
        var imported = await ImportAsync(link, message, issues, ct);

        await messageLog.MarkProcessedAsync(
            logId,
            imported == 0 ? InstrumentMessageProcessingStatus.Failed
                          : issues.Count == 0 ? InstrumentMessageProcessingStatus.Imported
                                              : InstrumentMessageProcessingStatus.PartiallyImported,
            message.Specimens.Sum(s => s.Results.Count), imported,
            issues.Count == 0 ? null : string.Join(Environment.NewLine, issues),
            clock.Now, ct);

        await messageLog.MarkReplayedAsync(logId, byUserId, clock.Now, ct);

        return Result.Success(imported);
    }

    // ---- Import --------------------------------------------------------------------------------

    private async Task<int> ImportAsync(InstrumentInterface link, InstrumentMessage message,
                                         List<string> issues, CancellationToken ct)
    {
        var imported = 0;

        foreach (var specimen in message.Specimens)
        {
            if (string.IsNullOrWhiteSpace(specimen.SpecimenId))
            {
                issues.Add("Interface_SpecimenIdMissing");
                continue;
            }

            if (specimen.IsControl)
            {
                imported += await ImportControlAsync(link, specimen, issues, ct);
                continue;
            }

            imported += await ImportPatientAsync(link, specimen, issues, ct);
        }

        if (imported > 0)
        {
            unitOfWork.AuditReason =
                $"Résultats reçus de l'automate ({link.Protocol}) — {imported} valeur(s)";

            await unitOfWork.SaveChangesAsync(ct);
        }

        return imported;
    }

    private async Task<int> ImportPatientAsync(InstrumentInterface link,
                                                InstrumentSpecimenResults specimen,
                                                List<string> issues, CancellationToken ct)
    {
        var barcode = specimen.SpecimenId!.Trim();

        // Exact match, and exactly one. A barcode that matches two tubes is a configuration fault
        // the laboratory has to see; silently taking the first would attach results at random.
        var matches = await samples.ListAsync(s => s.Barcode == barcode, ct);

        if (matches.Count == 0)
        {
            // Almost always a tube drawn without an order, or a tube from another site. Worth
            // logging loudly: the analyser has already consumed the specimen.
            issues.Add(Describe("Interface_SpecimenUnknown", barcode));
            return 0;
        }

        if (matches.Count > 1)
        {
            issues.Add(Describe("Interface_SpecimenAmbiguous", barcode));
            return 0;
        }

        var sample = matches[0];

        if (sample.Status is SampleStatus.Rejected or SampleStatus.Discarded)
        {
            // The tube was refused or thrown away, and the analyser ran it anyway. The value is not
            // reportable: whatever made the tube unacceptable has not gone away.
            issues.Add(Describe("Interface_SpecimenNotUsable", $"{barcode} / {sample.Status}"));
            return 0;
        }

        if (sample.Status is SampleStatus.AwaitingCollection or SampleStatus.Collected or SampleStatus.InTransit)
        {
            // Run before the laboratory took the tube in: the value waits in the log, to be
            // replayed once the tube is received.
            issues.Add(Describe("Interface_SpecimenNotReceived", barcode));
            return 0;
        }

        var order = await orders.GetByIdWithDetailsAsync(sample.LabOrderId, ct);
        if (order is null) { issues.Add(Describe("Interface_OrderNotFound", barcode)); return 0; }

        if (order.Status == LabOrderStatus.Cancelled)
        {
            issues.Add(Describe("Interface_OrderCancelled", order.AccessionNumber));
            return 0;
        }

        // The patient's demographics decide which reference interval applies, so they are loaded
        // once per specimen rather than per result — the surviving file's, when this order sits on
        // a duplicate that was merged into it.
        var patient = await pipeline.PatientOfAsync(order, ct);

        var stored = await results.ListAsync(r => r.LabOrderId == order.Id, ct);

        var imported = 0;
        var touched = new List<TestResult>();

        foreach (var incoming in specimen.Results)
        {
            // A result type the link does not keep (Abbott's interpretation beside its value).
            if (incoming.IsExcluded) continue;

            var mapping = FindMapping(link.Mappings, incoming.InstrumentTestCode);

            if (mapping is null)
            {
                if (IgnoredAsUnmappedNonNumeric(link, incoming)) continue;

                // Never inferred. The same three letters mean different measurands on different
                // analysers, and a guess puts a potassium value in a sodium field.
                if (link.RaiseIssueOnUnmappedCode)
                    issues.Add(Describe("Interface_UnmappedCode", incoming.InstrumentTestCode));

                continue;
            }

            var target = TargetFor(order, stored, sample.Id, mapping.TestParameterId);

            if (target is null)
            {
                // The analyser ran a test nobody ordered on this tube. Reported rather than
                // stored: an unordered result is unbilled, unvalidated and clinically unexplained.
                issues.Add(Describe("Interface_ResultNotOrdered",
                                     $"{incoming.InstrumentTestCode} / {order.AccessionNumber}"));
                continue;
            }

            var applied = await ApplyAsync(link, mapping, incoming, target, order, issues, ct);

            if (applied)
            {
                imported++;
                touched.Add(target);
            }
        }

        if (imported > 0)
        {
            // The reagent lot each value was measured with, from the stock of the analyser's site.
            var lots = await pipeline.ReagentLotsInUseAsync(
                [.. touched.Select(r => r.TestParameterId).Distinct()], link.SiteId, ct);

            foreach (var value in touched)
                value.ReagentLotId = lots.TryGetValue(value.TestParameterId, out var lot) ? lot : null;

            // The same post-analytical steps as a value typed at the bench: calculated parameters,
            // interpretation, previous value, delta check and the sample-defect flag. Run once per
            // tube, over the values it brought and the calculated ones they move; the order's other
            // values went through it when they were stored.
            if (patient is not null)
            {
                var catalogue = await pipeline.LoadCatalogueAsync(order, ct);

                await pipeline.RunAsync(order, patient, stored, catalogue, serviceAccount.UserId,
                                        order.ReportLanguage, touched, ct);
            }
            else
            {
                issues.Add(Describe("Order_PatientNotFound", order.AccessionNumber));
            }

            // The items the analyser produced values for move to result-entered, so the technical
            // validation worklist picks them up instead of them sitting in "received" forever. Only
            // those: another analysis on the same tube, run on another analyser or sent back to the
            // bench and not re-run yet, has nothing new to validate.
            var producedFor = touched.Select(r => r.LabOrderItemId).ToHashSet();

            foreach (var item in order.Items.Where(i => !i.IsCancelled && producedFor.Contains(i.Id)))
                item.MarkResultEntered(clock.Now);

            // And any other analysis a calculated value has just completed.
            ResultPostEntryPipeline.AdvanceCompletedItems(order, stored, clock.Now);

            order.RecomputeStatus();
        }

        return imported;
    }

    /// <summary>
    /// The row a value for this parameter goes into: one of a live analysis, on this tube.
    /// <para>
    /// A recollection cancels the analysis of the refused tube and keeps its rows beside the
    /// replacement's, for the same parameters. Taking the first row for the parameter wrote the
    /// new tube's value into the cancelled one about as often as not: hidden from the report,
    /// while the replacement stayed awaited and the file never completed. An analysis not yet
    /// bound to a tube is the fallback; one bound to another tube is not this tube's.
    /// </para>
    /// </summary>
    public static TestResult? TargetFor(LabOrder order, IReadOnlyList<TestResult> stored,
                                        Guid sampleId, Guid testParameterId)
    {
        var live = order.Items.Where(i => !i.IsCancelled).ToDictionary(i => i.Id);

        var candidates = stored
            .Where(r => r.TestParameterId == testParameterId && live.ContainsKey(r.LabOrderItemId))
            .ToList();

        return candidates.FirstOrDefault(r => live[r.LabOrderItemId].SampleId == sampleId)
               ?? candidates.FirstOrDefault(r => live[r.LabOrderItemId].SampleId is null);
    }

    private async Task<bool> ApplyAsync(InstrumentInterface link, InstrumentTestMapping mapping,
                                         InstrumentResult incoming, TestResult target,
                                         LabOrder order, List<string> issues,
                                         CancellationToken ct)
    {
        var parameter = await ResolveParameterAsync(order, target, mapping.TestParameterId, ct);

        if (parameter is null)
        {
            issues.Add(Describe("Interface_ParameterNotFound", mapping.InstrumentTestCode));
            return false;
        }

        if (target.IsValidated)
        {
            // The domain refuses this, and the refusal is the point: a value corrected after
            // biological validation has to be issued as an amended report, so the clinician who
            // already has the result learns that it changed.
            issues.Add(Describe(incoming.IsCorrection
                                    ? "Interface_CorrectionAfterValidation"
                                    : "Interface_ResultAlreadyValidated",
                                $"{parameter.Code} / {order.AccessionNumber}"));
            return false;
        }

        if (incoming.IsNotDone)
        {
            // The analyser says it could not produce this value. Recorded as not done rather than
            // left awaited: an awaited row keeps the order open forever, and the report prints
            // "non fait" against the parameter, which is the truth.
            var notDone = target.MarkNotDone(
                incoming.Comments.Count > 0 ? incoming.Comments[0] : "Interface_AnalyserReportedNotDone",
                clock.Now, serviceAccount.UserId);

            if (notDone.IsFailure)
            {
                issues.Add(Describe(notDone.FirstError.ResourceKey, parameter.Code));
                return false;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(incoming.Value))
        {
            issues.Add(Describe("Interface_EmptyValue", parameter.Code));
            return false;
        }

        // Stamped with the time the value reached the laboratory system, not the analyser's
        // measurement time. The entry time is what a critical call is compared with: a re-run
        // measured at 10:05 and imported at 10:30, after a link outage or from a replay, counted
        // as covered by a call made at 10:10 about the value it replaced — and an analyser clock
        // running ahead blocked calls that were valid. The measurement time is kept beside it,
        // and it is what the reference interval and the QC window are read on.
        var at = clock.Now;
        var raw = incoming.Value.Trim();

        var outcome = parameter.ValueType switch
        {
            ResultValueType.Numeric or ResultValueType.NumericWithOperator =>
                ApplyNumeric(parameter, mapping, raw, at, target, issues),

            ResultValueType.Coded or ResultValueType.SemiQuantitative =>
                target.EnterCoded(parameter, mapping.TranslateCode(raw), at,
                                   serviceAccount.UserId, fromInstrument: true),

            ResultValueType.Text =>
                target.EnterText(raw, at, serviceAccount.UserId, fromInstrument: true),

            _ => Result.Failure(Error.Validation("Interface_ValueTypeNotTransmittable",
                                                   parameter.ValueType))
        };

        if (outcome.IsFailure)
        {
            issues.Add(Describe(outcome.FirstError.ResourceKey, $"{parameter.Code} = {raw}"));
            return false;
        }

        target.MeasuredAt = incoming.MeasuredAt ?? at;
        target.InstrumentId = link.InstrumentId;
        target.RawInstrumentValue = TestResult.KeepRaw(raw);
        target.InstrumentFlags = link.KeepInstrumentFlags ? DescribeInstrumentFlags(incoming) : null;

        if (incoming.DilutionFactor is { } dilution and > 1m)
            target.DilutionFactor = dilution;

        // Interpreted with the rest of the tube, by the post-entry pipeline, once every value of
        // the transmission is in: against this patient's demographics, on the day it was measured
        // and with this analyser, since a method-dependent interval is the norm for hormones and
        // immunoassays.
        return true;
    }

    /// <summary>
    /// Applies a numeric value, honouring the mapping's unit conversion and the analyser's censoring
    /// operator.
    /// <para>
    /// The conversion is the most dangerous line in this file: a wrong factor is a hundredfold error
    /// on a report, which is why it is an explicit configured number rather than inferred from the
    /// unit strings the analyser happens to send.
    /// </para>
    /// </summary>
    private Result ApplyNumeric(TestParameter parameter, InstrumentTestMapping mapping, string raw,
                                 DateTimeOffset at, TestResult target, List<string> issues)
    {
        var text = raw;
        string? censoring = null;

        if (text.StartsWith('<') || text.StartsWith('>'))
        {
            censoring = text[..1];
            text = text[1..].Trim();
        }

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && !decimal.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("fr-FR"),
                                  out value))
        {
            // Not a number at all. Some analysers send "SEE COMMENT" or "ERROR" in a numeric field,
            // and storing that as text under a numeric parameter would make the column unreadable.
            issues.Add(Describe("Interface_NonNumericValue", $"{parameter.Code} = {raw}"));
            return Result.Failure(Error.Validation("Interface_NonNumericValue", raw));
        }

        var converted = mapping.Convert(value);

        // A censored value stays censored, through the domain method that exists for it: reporting
        // "< 0,01" as a bare 0,01 asserts a precision the method does not have.
        return censoring is null
            ? target.EnterNumeric(parameter, converted, at, serviceAccount.UserId,
                                   fromInstrument: true, rawValue: raw)
            : target.EnterCensoredNumeric(parameter, converted, censoring, at,
                                           serviceAccount.UserId, fromInstrument: true,
                                           rawValue: raw);
    }

    /// <summary>
    /// Records a control result against its target, keeping it out of every patient file.
    /// <para>
    /// The point is stored with the from-instrument flag and left pending review: the Westgard
    /// evaluation and the decision about whether patient results may flow belong to the quality
    /// control screen, where a biologist sees the chart. Evaluating it here and acting on the
    /// verdict silently would make a rejection invisible.
    /// </para>
    /// <para>
    /// A new lot's acceptance point is the exception: it is evaluated on the lot's own series, as
    /// the QC screen does, because the release gate reads its verdict and flags no patient result.
    /// Left pending, it blocked the lot's release for good.
    /// </para>
    /// </summary>
    private async Task<int> ImportControlAsync(InstrumentInterface link,
                                                InstrumentSpecimenResults specimen,
                                                List<string> issues, CancellationToken ct)
    {
        if (!link.RouteControlResultsToQc)
        {
            // Explicitly configured not to take them. Logged, never stored as a patient result.
            issues.Add(Describe("Interface_ControlNotRouted", specimen.ControlIdentifier
                                                              ?? specimen.SpecimenId));
            return 0;
        }

        var lot = specimen.ControlIdentifier?.Trim();

        if (string.IsNullOrWhiteSpace(lot))
        {
            issues.Add("Interface_ControlLotMissing");
            return 0;
        }

        var imported = 0;
        var points = new List<(QcResult Point, QcTarget Target)>();

        foreach (var incoming in specimen.Results)
        {
            // A result type the link does not keep (Abbott's interpretation beside its value).
            if (incoming.IsExcluded) continue;

            var mapping = FindMapping(link.Mappings, incoming.InstrumentTestCode);

            if (mapping is null)
            {
                if (IgnoredAsUnmappedNonNumeric(link, incoming)) continue;

                if (link.RaiseIssueOnUnmappedCode)
                    issues.Add(Describe("Interface_UnmappedCode", incoming.InstrumentTestCode));

                continue;
            }

            if (!decimal.TryParse(incoming.Value?.TrimStart('<', '>').Trim(), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out var value))
            {
                issues.Add(Describe("Interface_NonNumericValue", incoming.InstrumentTestCode));
                continue;
            }

            // The target is the laboratory's assigned mean and SD for this material, parameter and
            // analyser. Without one there is nothing to judge the point against, so it is not
            // stored: an unanchored control point on a chart is worse than a gap.
            var (target, candidates) = await TargetForControlAsync(
                mapping.TestParameterId, link.InstrumentId, lot, specimen.SpecimenId, ct);

            if (target is null)
            {
                issues.Add(Describe(candidates > 1 ? "Interface_QcTargetAmbiguous" : "Interface_QcTargetNotFound",
                                     $"{incoming.InstrumentTestCode} / {lot}"));
                continue;
            }

            var point = new QcResult(target.Id, mapping.TestParameterId, link.InstrumentId,
                                      link.SiteId, mapping.Convert(value),
                                      incoming.MeasuredAt ?? clock.Now, serviceAccount.UserId)
            {
                IsAutoTransmitted = true,
                TargetMean = target.AssignedMean,
                TargetSd = target.AssignedSd
            };

            await qcResults.AddAsync(point, ct);
            points.Add((point, target));
            imported++;
        }

        if (imported > 0)
        {
            // The reagent lot measured: a new lot whose evaluation run was started on this analyser
            // when it still expects this level, otherwise the lot in use. Crediting a new lot's
            // controls to the lot in use left the new lot without evidence and mixed its values into
            // the old one. Only the started run's own controls (see the query): the analyser's
            // routine QC is measured with the lot in use and stays credited to it.
            var parameterIds = points.Select(p => p.Point.TestParameterId).Distinct().ToList();
            var parameterOfTarget = points
                .GroupBy(p => p.Target.Id)
                .ToDictionary(g => g.Key, g => g.First().Point.TestParameterId);
            var evaluated = await pipeline.ReagentLotsUnderEvaluationAsync(
                parameterOfTarget, link.SiteId, link.InstrumentId, ct);
            var lots = await pipeline.ReagentLotsInUseAsync(parameterIds, link.SiteId, ct);

            foreach (var (point, _) in points)
            {
                if (evaluated.TryGetValue(point.QcTargetId, out var underEvaluation))
                {
                    point.ReagentLotId = underEvaluation;
                    point.IsLotEvaluation = true;
                }
                else
                {
                    point.ReagentLotId = lots.TryGetValue(point.TestParameterId, out var inUse) ? inUse : null;
                }
            }

            await EvaluateAcceptancePointsAsync([.. points.Where(p => p.Point.IsLotEvaluation)], ct);

            unitOfWork.AuditReason =
                $"Contrôle interne reçu de l'automate — lot {lot}, {imported} niveau(x)";

            await unitOfWork.SaveChangesAsync(ct);
        }

        return imported;
    }

    /// <summary>
    /// The target of the control the analyser sent. Several levels of one parameter on one analyser
    /// are several targets, told apart by the control lot: taking the first one stored a level-2
    /// value against the level-1 mean.
    /// <para>
    /// What the analyser sends is rarely the bare lot number: ASTM gives the specimen descriptor
    /// ("CONTROL-NORMAL") with the lot in the sample identifier ("QC-LOT-882"), HL7 the whole
    /// specimen source. So a material's lot matches when it stands in either one as a whole token;
    /// failing that, a level word (LOW, NORMAL, HIGH) narrows the targets; and a parameter with a
    /// single target on the analyser keeps it. Several targets left is no target: the point is not
    /// stored against a guessed level. The count of targets left tells "ambiguous" from "none".
    /// </para>
    /// </summary>
    private async Task<(QcTarget? Target, int Candidates)> TargetForControlAsync(
        Guid testParameterId, Guid instrumentId, string controlIdentifier, string? specimenId,
        CancellationToken ct)
    {
        var targets = await qcTargets.ListAsync(
            t => t.TestParameterId == testParameterId && t.InstrumentId == instrumentId && t.IsActive, ct);
        if (targets.Count == 0) return (null, 0);

        var materialIds = targets.Select(t => t.QcMaterialId).Distinct().ToList();
        var materials = (await qcMaterials.ListAsync(m => materialIds.Contains(m.Id), ct))
            .ToDictionary(m => m.Id);

        string[] sent = [.. new[] { controlIdentifier, specimenId }.Where(s => !string.IsNullOrWhiteSpace(s))
                                                                     .Select(s => s!.Trim())];

        var candidates = targets
            .Where(t => materials.TryGetValue(t.QcMaterialId, out var m)
                        && sent.Any(s => ControlLotMatcher.Names(s, m.LotNumber)))
            .ToList();

        if (candidates.Count == 0) candidates = [.. targets];

        if (candidates.Count > 1 && sent.Select(ControlLotMatcher.LevelNamed).FirstOrDefault(l => l is not null) is { } level)
        {
            var ofLevel = candidates
                .Where(t => materials.TryGetValue(t.QcMaterialId, out var m) && m.Level == level)
                .ToList();
            if (ofLevel.Count > 0) candidates = ofLevel;
        }

        return candidates.Count == 1 ? (candidates[0], 1) : (null, candidates.Count);
    }

    /// <summary>
    /// Evaluates the acceptance points of a lot under evaluation against that lot's own series,
    /// with the target statistics the QC screen uses. The transmission is one run.
    /// </summary>
    private async Task EvaluateAcceptancePointsAsync(List<(QcResult Point, QcTarget Target)> points,
                                                     CancellationToken ct)
    {
        if (points.Count == 0) return;

        var runId = Guid.CreateVersion7();

        foreach (var (point, target) in points)
        {
            point.QcRunId = runId;
            point.TargetMean = target.EffectiveMean;
            point.TargetSd = target.EffectiveSd;
            point.ZScore = target.ZScore(point.Value);
        }

        foreach (var (point, target) in points)
        {
            if (point.ZScore is not { } z)
            {
                point.ApplyEvaluation(WestgardEvaluation.NotEvaluable);
                continue;
            }

            var history = await qualityQueries.GetZScoreHistoryAsync(target.Id, 12, point.ReagentLotId, ct);
            var series = new List<QcPoint>(history)
            {
                new(z, point.MeasuredAt, runId, await qualityQueries.GetMaterialLevelAsync(target.QcMaterialId, ct))
            };

            // The other levels of the same analyte on the same lot, sent in the same transmission.
            var peers = points
                .Where(x => x.Point.Id != point.Id && x.Point.ZScore is not null
                            && x.Point.TestParameterId == point.TestParameterId
                            && x.Point.ReagentLotId == point.ReagentLotId)
                .Select(x => new QcPoint(x.Point.ZScore!.Value, x.Point.MeasuredAt, runId, QcLevel.Normal))
                .ToList();

            point.ApplyEvaluation(WestgardEvaluator.Evaluate(target.ActiveRules, series, peers));
        }
    }

    // ---- Query answering -----------------------------------------------------------------------

    /// <summary>
    /// The enabled mapping for a code as the analyser sent it.
    /// <para>
    /// The exact code first. Failing that, and only then, the part before a slash: Roche cobas
    /// analysers append the dilution to the code (<c>GLY/</c>, <c>767/5</c>), while another vendor's
    /// code may genuinely contain one (<c>A/G</c>), which the exact match still finds.
    /// </para>
    /// </summary>
    public static InstrumentTestMapping? FindMapping(IEnumerable<InstrumentTestMapping> mappings,
                                                     string code)
    {
        var enabled = mappings.Where(m => m.IsEnabled).ToList();

        var exact = enabled.FirstOrDefault(
            m => string.Equals(m.InstrumentTestCode, code, StringComparison.Ordinal));

        if (exact is not null) return exact;

        var slash = code.IndexOf('/');
        if (slash <= 0) return null;

        var stem = code[..slash];

        return enabled.FirstOrDefault(
            m => string.Equals(m.InstrumentTestCode, stem, StringComparison.Ordinal));
    }

    /// <summary>
    /// Answers an analyser that has scanned a tube and wants to know what to run on it.
    /// <para>
    /// Only analyses mapped on this interface are sent down: telling an analyser to run a test it
    /// has no channel for produces an error frame, and telling it to run one the laboratory does
    /// not map means the result comes back unmappable.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
        Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default)
    {
        var link = await interfaces.GetByIdWithDetailsAsync(interfaceId, ct);
        if (link is null) return [];

        var downloads = new List<InstrumentOrderDownload>(specimenIds.Count);

        foreach (var specimenId in specimenIds)
        {
            var barcode = specimenId.Trim();

            var matches = await samples.ListAsync(s => s.Barcode == barcode, ct);

            if (matches.Count != 1)
            {
                // An unknown barcode is itself worth recording: it usually means a tube was drawn
                // without an order, and the analyser is about to consume the specimen.
                await messageLog.RecordAsync(
                    new InstrumentMessageLog(link.Id, link.SiteId, MessageDirection.Inbound,
                                              $"[QUERY] {barcode}", clock.Now)
                    {
                        SpecimenIds = barcode,
                        Status = InstrumentMessageProcessingStatus.Failed,
                        Issues = Describe(matches.Count == 0 ? "Interface_SpecimenUnknown"
                                                             : "Interface_SpecimenAmbiguous",
                                           barcode)
                    }, ct);

                continue;
            }

            var sample = matches[0];

            var order = await orders.GetByIdWithDetailsAsync(sample.LabOrderId, ct);
            if (order is null || order.Status == LabOrderStatus.Cancelled) continue;

            var patient = await patients.GetByIdAsync(order.PatientId, ct);

            var wanted = new List<string>();

            foreach (var item in order.Items.Where(i => !i.IsCancelled && i.SampleId == sample.Id))
            {
                var analysis = await analyses.GetByIdWithDetailsAsync(item.AnalysisTestId, ct);
                if (analysis is null) continue;

                foreach (var parameter in analysis.Parameters.Where(p => p.IsActive))
                {
                    var mapping = link.Mappings.FirstOrDefault(
                        m => m.IsEnabled && m.TestParameterId == parameter.Id);

                    if (mapping is not null) wanted.Add(mapping.InstrumentTestCode);
                }
            }

            if (wanted.Count == 0) continue;

            downloads.Add(new InstrumentOrderDownload
            {
                SpecimenId = sample.Barcode,
                InstrumentTestCodes = [.. wanted.Distinct(StringComparer.Ordinal)],
                PatientId = patient?.MedicalRecordNumber,
                PatientName = patient?.FullName,
                Sex = patient?.Gender switch
                {
                    Gender.Male => "M",
                    Gender.Female => "F",
                    _ => "U"
                },
                DateOfBirth = patient?.DateOfBirth,
                Priority = order.IsUrgent ? "S" : "R",
                CollectedAt = sample.CollectedAt,
                ClinicalInformation = order.ClinicalInformation,
                RackPosition = null
            });
        }

        return downloads;
    }

    // ---- Logging and link state ----------------------------------------------------------------

    public Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content,
                             CancellationToken ct = default) =>
        RecordRawAsync(interfaceId, direction, content, ct);

    private async Task RecordRawAsync(Guid interfaceId, MessageDirection direction, string content,
                                       CancellationToken ct)
    {
        var link = await interfaces.GetByIdAsync(interfaceId, ct);
        if (link is null) return;

        await messageLog.RecordAsync(
            new InstrumentMessageLog(link.Id, link.SiteId, direction, content, clock.Now)
            {
                Status = InstrumentMessageProcessingStatus.Received
            }, ct);
    }

    public async Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error,
                                                  CancellationToken ct = default)
    {
        var link = await interfaces.GetByIdAsync(interfaceId, ct);
        if (link is null) return;

        if (connected)
        {
            link.LastConnectedAt = clock.Now;
            link.LastError = null;
        }
        else
        {
            link.LastError = error;
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>
    /// Loads the catalogue parameter behind a result row.
    /// <para>
    /// Reached through the row's own order item rather than looked up on its own, because a
    /// parameter is not an aggregate root and has no repository: going via its analysis is what
    /// keeps the reference intervals and coded options attached, and both are needed by the entry
    /// methods — the intervals to interpret the value and the options to validate a coded one.
    /// </para>
    /// <para>
    /// Cached per message: one transmission commonly carries twenty results from the same analysis,
    /// and loading the whole catalogue graph twenty times would turn one import into twenty.
    /// </para>
    /// </summary>
    private async Task<TestParameter?> ResolveParameterAsync(LabOrder order, TestResult target,
                                                              Guid parameterId,
                                                              CancellationToken ct)
    {
        if (_parameterCache.TryGetValue(parameterId, out var cached)) return cached;

        var item = order.Items.FirstOrDefault(i => i.Id == target.LabOrderItemId);
        if (item is null) return null;

        var analysis = await analyses.GetByIdWithDetailsAsync(item.AnalysisTestId, ct);

        var parameter = analysis?.Parameters.FirstOrDefault(p => p.Id == parameterId);

        if (parameter is not null) _parameterCache[parameterId] = parameter;

        return parameter;
    }

    private readonly Dictionary<Guid, TestParameter> _parameterCache = [];

    private async Task CloseAsync(Guid logId, InstrumentMessageProcessingStatus status,
                                   InstrumentMessage message, int imported, List<string> issues,
                                   CancellationToken ct) =>
        await messageLog.MarkProcessedAsync(
            logId, status,
            message.Specimens.Sum(s => s.Results.Count), imported,
            issues.Count == 0 ? null : string.Join(Environment.NewLine, issues),
            clock.Now, ct);

    /// <summary>
    /// Issues are stored as a resource key with its detail, not as a sentence. The log is read in
    /// whichever language the operator is using, and a French sentence written here would be
    /// untranslatable afterwards.
    /// </summary>
    private static string Describe(string resourceKey, object? detail) =>
        detail is null ? resourceKey : $"{resourceKey}\t{detail}";

    private static string Describe(string resourceKey) => resourceKey;

    /// <summary>
    /// Unmapped and not a number: a haematology alarm ("Blasts?"), a histogram file name. Dropped
    /// when the link says so, because such analysers send dozens per tube and burying the real
    /// unknown codes under them is worse than not listing them.
    /// </summary>
    public static bool IgnoredAsUnmappedNonNumeric(InstrumentInterface link, InstrumentResult incoming) =>
        IgnoredAsUnmappedNonNumeric(link.UnmappedNonNumeric, incoming);

    public static bool IgnoredAsUnmappedNonNumeric(UnmappedNonNumericHandling handling,
                                                   InstrumentResult incoming) =>
        handling == UnmappedNonNumericHandling.Ignore
        && !decimal.TryParse(incoming.Value?.TrimStart('<', '>').Trim(), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// The analyser's flag and comments on one value, as the biologist should see them. "N" (normal)
    /// and an alarm code of 0 say nothing the interpretation does not, so they are left out.
    /// </summary>
    public static string? DescribeInstrumentFlags(InstrumentResult incoming)
    {
        var parts = new List<string>();

        var flag = incoming.AbnormalFlag?.Trim();
        if (!string.IsNullOrEmpty(flag) && !string.Equals(flag, "N", StringComparison.OrdinalIgnoreCase))
            parts.Add(flag);

        // Roche sends "C|1|I|0|I" after every result: alarm code 0, no alarm.
        parts.AddRange(incoming.Comments.Select(c => c.Trim())
                                        .Where(c => c.Length > 0 && c != "0"));

        if (parts.Count == 0) return null;

        var text = string.Join(" · ", parts);
        return text.Length <= 500 ? text : text[..500];
    }
}

/// <summary>
/// The non-interactive account results arriving from an analyser are attributed to.
/// <para>
/// Resolved once at start-up and refused when absent. A machine write needs an accountable identity
/// for the same reason a manual one does, and borrowing whichever operator happened to be signed in
/// would put a technician's name against a measurement they never saw.
/// </para>
/// </summary>
public interface IInstrumentServiceAccount
{
    Guid UserId { get; }

    bool IsResolved { get; }

    /// <summary>
    /// Resolves the account if it has not been resolved yet, and reports whether it now is.
    /// <para>
    /// On the workstation that drives the hardware the host has already done this at start-up. On
    /// every other workstation it has not — the host returns immediately when
    /// <c>EnableInstrumentHost</c> is false — and a replay launched from a bench would otherwise
    /// store values against <see cref="Guid.Empty"/>. Resolving on demand is what lets a technician
    /// accept a held transmission from wherever they are standing.
    /// </para>
    /// </summary>
    Task<bool> EnsureResolvedAsync(CancellationToken ct = default);
}
