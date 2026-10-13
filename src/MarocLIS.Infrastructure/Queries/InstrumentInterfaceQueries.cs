using System.Globalization;
using MarocLIS.Application.Instruments;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class InstrumentInterfaceQueries(LisDbContext db) : IInstrumentInterfaceQueries
{
    public async Task<IReadOnlyList<InterfaceRow>> GetInterfacesAsync(
        Guid siteId, DateOnly today, CancellationToken ct = default)
    {
        var since = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var rows = await db.InstrumentInterfaces.AsNoTracking()
            .Where(i => i.SiteId == siteId)
            .Select(i => new
            {
                i.Id,
                i.InstrumentId,
                i.Protocol,
                i.Transport,
                i.Direction,
                i.IsEnabled,
                i.Encoding,
                i.AcknowledgementTimeoutSeconds,
                i.MaxRetries,
                i.AutoAcceptResults,
                i.RaiseIssueOnUnmappedCode,
                i.RouteControlResultsToQc,
                i.LastConnectedAt,
                i.LastMessageAt,
                i.LastError,
                i.SerialPortName,
                i.BaudRate,
                i.DataBits,
                i.Parity,
                i.StopBits,
                i.Handshake,
                i.Host,
                i.TcpPort,
                i.ListenAsServer,
                i.InboundDirectory,
                i.OutboundDirectory,
                i.FilePattern,
                i.ProcessedDirectory,
                Instrument = db.Instruments.Where(a => a.Id == i.InstrumentId)
                    .Select(a => new { a.Code, a.Name }).FirstOrDefault(),
                MappingCount = db.InstrumentTestMappings.Count(m => m.InstrumentInterfaceId == i.Id),
                EnabledMappingCount = db.InstrumentTestMappings.Count(
                    m => m.InstrumentInterfaceId == i.Id && m.IsEnabled),
                MessagesToday = db.InstrumentMessageLog.Count(
                    l => l.InstrumentInterfaceId == i.Id && l.OccurredAt >= since),
                FailedToday = db.InstrumentMessageLog.Count(
                    l => l.InstrumentInterfaceId == i.Id && l.OccurredAt >= since
                         && (l.Status == InstrumentMessageProcessingStatus.Failed
                             || l.Status == InstrumentMessageProcessingStatus.PartiallyImported))
            })
            .ToListAsync(ct);

        return
        [
            .. rows
                // Enabled first, then the ones with a problem, so the screen opens on what needs
                // attention rather than in configuration order.
                .OrderByDescending(i => i.IsEnabled)
                .ThenByDescending(i => i.LastError != null)
                .ThenBy(i => i.Instrument == null ? string.Empty : i.Instrument.Code)
                .Select(i => new InterfaceRow(
                    i.Id,
                    i.InstrumentId,
                    i.Instrument?.Code ?? string.Empty,
                    i.Instrument?.Name ?? new MultilingualText(string.Empty),
                    i.Protocol,
                    i.Transport,
                    i.Direction,
                    i.IsEnabled,
                    DescribeEndpoint(i.Transport, i.SerialPortName, i.BaudRate, i.DataBits,
                                      i.Parity, i.StopBits, i.Host, i.TcpPort, i.ListenAsServer,
                                      i.InboundDirectory, i.FilePattern),
                    i.Encoding,
                    i.AcknowledgementTimeoutSeconds,
                    i.MaxRetries,
                    i.AutoAcceptResults,
                    i.RaiseIssueOnUnmappedCode,
                    i.RouteControlResultsToQc,
                    i.LastConnectedAt,
                    i.LastMessageAt,
                    i.LastError,
                    i.MappingCount,
                    i.EnabledMappingCount,
                    i.MessagesToday,
                    i.FailedToday,
                    i.SerialPortName,
                    i.BaudRate,
                    i.DataBits,
                    i.Parity,
                    i.StopBits,
                    i.Handshake,
                    i.Host,
                    i.TcpPort,
                    i.ListenAsServer,
                    i.InboundDirectory,
                    i.OutboundDirectory,
                    i.FilePattern,
                    i.ProcessedDirectory))
        ];
    }

    /// <summary>
    /// The endpoint in the form the technician who cabled it would recognise: a port and its frame
    /// settings, a host and port, or a watched directory.
    /// </summary>
    private static string DescribeEndpoint(InstrumentTransportKind transport, string? serialPort,
                                            int baud, int dataBits, string parity, string stopBits,
                                            string? host, int? tcpPort, bool listen,
                                            string? inbound, string? pattern) =>
        transport switch
        {
            InstrumentTransportKind.Serial =>
                $"{serialPort ?? "—"} @ {baud}-{dataBits}-{parity}-{stopBits}",

            // Invariant for the port: it is an endpoint an engineer copies into another tool, not a
            // quantity to read, so it must not pick up culture-specific digit grouping.
            InstrumentTransportKind.Tcp => listen
                ? $"écoute :{tcpPort?.ToString(CultureInfo.InvariantCulture) ?? "—"}"
                : $"{host ?? "—"}:{tcpPort?.ToString(CultureInfo.InvariantCulture) ?? "—"}",

            InstrumentTransportKind.FileDrop => $"{inbound ?? "—"}\\{pattern ?? "*.*"}",

            _ => "—"
        };

    public async Task<IReadOnlyList<MappingRow>> GetMappingsAsync(
        Guid interfaceId, CancellationToken ct = default)
    {
        var rows = await db.InstrumentTestMappings.AsNoTracking()
            .Where(m => m.InstrumentInterfaceId == interfaceId)
            .Select(m => new
            {
                m.Id,
                m.InstrumentInterfaceId,
                m.InstrumentTestCode,
                m.TestParameterId,
                m.InstrumentUnit,
                m.ConversionFactor,
                m.ConversionOffset,
                m.CodedValueMap,
                m.IsEnabled,
                Parameter = db.TestParameters.Where(p => p.Id == m.TestParameterId)
                    .Select(p => new
                    {
                        p.Code,
                        p.Name,
                        p.ValueType,
                        AnalysisCode = db.AnalysisTests.Where(a => a.Id == p.AnalysisTestId)
                                                        .Select(a => a.Code).FirstOrDefault(),
                        UnitSymbol = db.Units.Where(u => u.Id == p.UnitId)
                                              .Select(u => u.DisplaySymbol).FirstOrDefault()
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows
                .OrderBy(m => m.IsEnabled ? 0 : 1)
                .ThenBy(m => m.InstrumentTestCode, StringComparer.Ordinal)
                .Select(m => new MappingRow(
                    m.Id,
                    m.InstrumentInterfaceId,
                    m.InstrumentTestCode,
                    m.TestParameterId,
                    m.Parameter?.Code ?? string.Empty,
                    m.Parameter?.Name ?? new MultilingualText(string.Empty),
                    m.Parameter?.AnalysisCode ?? string.Empty,
                    m.Parameter?.ValueType ?? Domain.Catalog.ResultValueType.Numeric,
                    m.Parameter?.UnitSymbol,
                    m.InstrumentUnit,
                    m.ConversionFactor,
                    m.ConversionOffset,
                    m.CodedValueMap,
                    m.IsEnabled))
        ];
    }

    public async Task<IReadOnlyList<MessageLogRow>> GetMessagesAsync(
        Guid? interfaceId, int take, CancellationToken ct = default)
    {
        var query = db.InstrumentMessageLog.AsNoTracking();

        if (interfaceId is { } id) query = query.Where(l => l.InstrumentInterfaceId == id);

        return await query
            .OrderByDescending(l => l.OccurredAt)
            .Take(Math.Clamp(take, 1, 1000))
            // The raw frame is measured, not fetched. A log of two hundred ASTM transmissions is
            // megabytes of text, and the screen shows one at a time on request.
            .Select(l => new MessageLogRow(
                l.Id,
                l.InstrumentInterfaceId,
                l.Direction,
                l.OccurredAt,
                l.Status,
                l.SpecimenIds,
                l.ResultCount,
                l.ImportedCount,
                l.Issues,
                l.ProcessedAt,
                l.ReplayedAt,
                db.Users.Where(u => u.Id == l.ReplayedByUserId)
                        .Select(u => u.FullName).FirstOrDefault(),
                l.RawContent.Length))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MappableParameter>> GetMappableParametersAsync(
        string? search, CancellationToken ct = default)
    {
        var query = db.TestParameters.AsNoTracking()
            .Where(p => p.IsActive
                        && p.ValueType != Domain.Catalog.ResultValueType.Heading);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";

            query = query.Where(p => EF.Functions.Like(p.Code, pattern)
                                     || EF.Functions.Like(p.Name.Fr, pattern)
                                     || db.AnalysisTests.Any(a => a.Id == p.AnalysisTestId
                                                                  && EF.Functions.Like(a.Code, pattern)));
        }

        // Ordered before the projection: ordering on a projected record would have to be evaluated
        // after materialisation, which with Take(500) would take an arbitrary five hundred rows and
        // then sort those.
        var rows = await query
            .Select(p => new
            {
                p.Id,
                p.Code,
                p.Name,
                p.ValueType,
                AnalysisCode = db.AnalysisTests.Where(a => a.Id == p.AnalysisTestId)
                                                .Select(a => a.Code).FirstOrDefault(),
                UnitSymbol = db.Units.Where(u => u.Id == p.UnitId)
                                      .Select(u => u.DisplaySymbol).FirstOrDefault()
            })
            .OrderBy(p => p.AnalysisCode).ThenBy(p => p.Code)
            .Take(500)
            .ToListAsync(ct);

        return
        [
            .. rows.Select(p => new MappableParameter(p.Id, p.AnalysisCode ?? string.Empty, p.Code,
                                                       p.Name, p.ValueType, p.UnitSymbol))
        ];
    }
}
