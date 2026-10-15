using System.Linq.Expressions;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Instruments.Protocol;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// The stored frame must survive a round trip through the log.
/// <para>
/// Replay re-parses <see cref="MarocLIS.Instruments.InstrumentMessage.RawText"/> rather than keeping
/// the parsed object, which means the whole feature rests on one assumption: that the text written
/// to <c>eqp.InstrumentMessageLog</c> parses back to the same message. If it does not, a replay
/// reports success and stores nothing, which is worse than refusing — so the assumption is asserted
/// here rather than trusted.
/// </para>
/// <para>
/// The two protocols store their raw text differently on purpose: ASTM joins its records with CR,
/// HL7 keeps the message verbatim. A single shared split would quietly break one of them.
/// </para>
/// </summary>
public sealed class StoredFrameRoundTripTests
{
    private static readonly string[] AstmRecords =
    [
        "H|\\^&|||ANALYSER^1.0|||||||P|1|20261008120000",
        "P|1||PAT-1||BENANI^AHMED||19800101|M",
        "O|1|TUBE-0001||^^^GLU|R||20261008113000",
        "R|1|^^^GLU|5.4|mmol/L||N||F||OP1|20261008115500",
        "R|2|^^^NA|139|mmol/L||N||F||OP1|20261008115500",
        "L|1|N"
    ];

    [Fact]
    public void AstmRawText_ReparsesToTheSameResults()
    {
        var original = AstmMessageParser.Parse(AstmRecords);

        original.RawText.Should().NotBeNullOrWhiteSpace(
            "the frame log stores RawText, so a parser that does not populate it makes replay a no-op");

        // Exactly how InstrumentMessageSink.Reparse undoes it.
        var replayed = AstmMessageParser.Parse(
            original.RawText!.Split('\r', StringSplitOptions.RemoveEmptyEntries));

        replayed.HasBlockingIssues.Should().BeFalse();
        replayed.Specimens.Should().HaveCount(original.Specimens.Count);

        var before = original.Specimens.Single();
        var after = replayed.Specimens.Single();

        after.SpecimenId.Should().Be(before.SpecimenId);
        after.Results.Select(r => r.InstrumentTestCode)
             .Should().Equal(before.Results.Select(r => r.InstrumentTestCode));
        after.Results.Select(r => r.Value)
             .Should().Equal(before.Results.Select(r => r.Value));
    }

    [Fact]
    public void Hl7RawText_ReparsesToTheSameResults()
    {
        var text = string.Join('\r',
            "MSH|^~\\&|ANALYSER|LAB|LIS|LAB|20261008120000||ORU^R01|1|P|2.5",
            "PID|1||PAT-1||BENANI^AHMED||19800101|M",
            "OBR|1|TUBE-0002||^^^GLU|||20261008113000",
            "OBX|1|NM|GLU||5.4|mmol/L|||||F",
            "OBX|2|NM|NA||139|mmol/L|||||F");

        var original = Hl7ResultParser.Parse(text);

        original.RawText.Should().NotBeNullOrWhiteSpace();

        var replayed = Hl7ResultParser.Parse(original.RawText!);

        replayed.HasBlockingIssues.Should().BeFalse();
        replayed.Specimens.SelectMany(s => s.Results).Select(r => r.Value)
                .Should().Equal(original.Specimens.SelectMany(s => s.Results).Select(r => r.Value));
    }
}

/// <summary>
/// The replay refusals, asserted against the sink itself rather than against the screen.
/// <para>
/// Every repository below throws if it is touched. That is the assertion: a refusal must be decided
/// before the sink goes anywhere near a patient, a sample or a result. A refusal that first loads an
/// order has already done work it should not have, and would be a refusal only by accident of
/// ordering.
/// </para>
/// </summary>
public sealed class ReplayRefusalTests
{
    [Fact]
    public async Task UnknownFrame_IsRefused()
    {
        var sink = Build(entry: null, out _);

        var result = await sink.ReplayAsync(Guid.NewGuid(), Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Interface_MessageNotFound");
    }

    [Fact]
    public async Task OutboundFrame_IsRefused()
    {
        // Replaying what we sent would re-import our own worklist as though it were a result.
        var entry = Entry(MessageDirection.Outbound, InstrumentMessageProcessingStatus.Imported);

        var result = await Build(entry, out _).ReplayAsync(entry.Id, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Interface_OnlyInboundReplayable");
    }

    [Theory]
    [InlineData(InstrumentMessageProcessingStatus.Imported)]
    [InlineData(InstrumentMessageProcessingStatus.Ignored)]
    [InlineData(InstrumentMessageProcessingStatus.Received)]
    public async Task StatusThatIsNotReplayable_IsRefused(InstrumentMessageProcessingStatus status)
    {
        var entry = Entry(MessageDirection.Inbound, status);

        var result = await Build(entry, out _).ReplayAsync(entry.Id, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Interface_StatusNotReplayable");
    }

    [Theory]
    [InlineData(InstrumentMessageProcessingStatus.Failed)]
    [InlineData(InstrumentMessageProcessingStatus.PartiallyImported)]
    [InlineData(InstrumentMessageProcessingStatus.AwaitingAcceptance)]
    public async Task ReplayableStatus_GetsPastTheStatusGate(
        InstrumentMessageProcessingStatus status)
    {
        // Held transmissions are the reason this path exists: with auto-acceptance off, every frame
        // lands on AwaitingAcceptance and nothing else can release it.
        var entry = Entry(MessageDirection.Inbound, status, raw: string.Empty);

        var result = await Build(entry, out _).ReplayAsync(entry.Id, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Interface_RawFrameEmpty",
            "an empty frame is the next gate, which proves the status gate let this one through");
    }

    [Fact]
    public async Task UnresolvedServiceAccount_IsRefusedRatherThanAttributedToNobody()
    {
        // Without this the import would store every value against Guid.Empty.
        var entry = Entry(MessageDirection.Inbound,
                          InstrumentMessageProcessingStatus.AwaitingAcceptance,
                          raw: "H|\\^&\rL|1|N");

        var sink = Build(entry, out var log, serviceAccountResolves: false);

        var result = await sink.ReplayAsync(entry.Id, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Interface_ServiceAccountMissing");

        log.Replays.Should().BeEmpty("a refused replay must not stamp the log entry");
    }

    // ---- Fixture ----------------------------------------------------------------------------

    private static InstrumentMessageLog Entry(MessageDirection direction,
                                               InstrumentMessageProcessingStatus status,
                                               string raw = "H|\\^&\rL|1|N")
    {
        var entry = new InstrumentMessageLog(Guid.NewGuid(), Guid.NewGuid(), direction, raw,
                                              DateTimeOffset.Now);

        entry.Status = status;
        return entry;
    }

    private static InstrumentMessageSink Build(InstrumentMessageLog? entry,
                                                out FakeMessageLogStore log,
                                                bool serviceAccountResolves = true)
    {
        log = new FakeMessageLogStore(entry);

        return new InstrumentMessageSink(
            new ThrowingRepository<InstrumentInterface>(),
            new ThrowingRepository<Sample>(),
            new ThrowingRepository<LabOrder>(),
            new ThrowingRepository<AnalysisTest>(),
            new ThrowingRepository<TestResult>(),
            new ThrowingRepository<Patient>(),
            new ThrowingRepository<QcTarget>(),
            new ThrowingRepository<QcResult>(),
            new ThrowingRepository<QcMaterial>(),
            null!,
            new ThrowingRepository<Instrument>(),
            new ResultPostEntryPipeline(new ThrowingRepository<AnalysisTest>(),
                                        new ThrowingRepository<Patient>(),
                                        new ThrowingRepository<Sample>(),
                                        null!, new FixedClock()),
            log,
            new ThrowingUnitOfWork(),
            new FixedClock(),
            new FakeServiceAccount(serviceAccountResolves));
    }

    private sealed class FakeMessageLogStore(InstrumentMessageLog? entry) : IInstrumentMessageLogStore
    {
        public List<Guid> Replays { get; } = [];

        public Task<InstrumentMessageLog?> GetByIdAsync(Guid logId, CancellationToken ct = default) =>
            Task.FromResult(entry is not null && entry.Id == logId ? entry : null);

        public Task MarkReplayedAsync(Guid logId, Guid byUserId, DateTimeOffset at,
                                       CancellationToken ct = default)
        {
            Replays.Add(logId);
            return Task.CompletedTask;
        }

        public Task<Guid> RecordAsync(InstrumentMessageLog e, CancellationToken ct = default) =>
            throw new InvalidOperationException("a replay must not record a new frame");

        public Task MarkProcessedAsync(Guid logId, InstrumentMessageProcessingStatus status,
                                        int? resultCount, int? importedCount, string? issues,
                                        DateTimeOffset processedAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<InstrumentMessageLog>> GetRecentAsync(
            Guid? interfaceId, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstrumentMessageLog>>([]);

        public Task<MessageLogCensus> CensusAsync(DateTimeOffset cutoff,
                                                   CancellationToken ct = default) =>
            Task.FromResult(new MessageLogCensus(0, 0, null, entry is null ? 0 : 1));

        public Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            throw new InvalidOperationException("a replay must not purge the log");
    }

    private sealed class FakeServiceAccount(bool resolves) : IInstrumentServiceAccount
    {
        public Guid UserId => resolves ? Guid.Parse("11111111-1111-1111-1111-111111111111")
                                       : Guid.Empty;

        public bool IsResolved => resolves;

        public Task<bool> EnsureResolvedAsync(CancellationToken ct = default) =>
            Task.FromResult(resolves);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(1));
        public DateOnly Today => DateOnly.FromDateTime(Now.Date);
    }

    /// <summary>Touching any of these in a refusal path is itself the failure.</summary>
    private sealed class ThrowingRepository<T> : IRepository<T> where T : AggregateRoot
    {
        private static InvalidOperationException Unexpected() =>
            new($"the refusal path must not reach IRepository<{typeof(T).Name}>");

        public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw Unexpected();

        public Task<T?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default) =>
            throw Unexpected();

        public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) => throw Unexpected();

        public Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> predicate,
                                                 CancellationToken ct = default) => throw Unexpected();

        public Task AddAsync(T entity, CancellationToken ct = default) => throw Unexpected();

        public void Update(T entity) => throw Unexpected();

        public void SoftDelete(T entity, Guid byUserId, string reason) => throw Unexpected();
    }

    private sealed class ThrowingUnitOfWork : IUnitOfWork
    {
        public string? AuditReason { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("the refusal path must not save");

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("the refusal path must not open a transaction");

        public Task CommitAsync(CancellationToken ct = default) => throw new InvalidOperationException();

        public Task RollbackAsync(CancellationToken ct = default) => throw new InvalidOperationException();
    }
}
