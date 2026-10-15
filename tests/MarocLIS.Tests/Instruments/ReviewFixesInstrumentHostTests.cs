using FluentAssertions;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// The sink a running analyser link is given: a fresh scope, so a fresh database context, for each
/// message, and a failed import that does not stop the link.
/// </summary>
public sealed class ReviewFixesInstrumentHostTests
{
    /// <summary>Stands for the real sink: one instance per scope, like its database context.</summary>
    private sealed class ScopedFake : IInstrumentMessageSink, IDisposable
    {
        public static readonly List<ScopedFake> Created = [];
        public static bool Throw;

        public ScopedFake() { lock (Created) Created.Add(this); }

        public bool Disposed { get; private set; }
        public int Calls { get; private set; }

        public Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message, CancellationToken ct = default)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("the database refused the import");
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
            Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("no database");
            return Task.FromResult<IReadOnlyList<InstrumentOrderDownload>>([new InstrumentOrderDownload { SpecimenId = "TUBE-1", InstrumentTestCodes = ["GLU"] }]);
        }

        public Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content, CancellationToken ct = default)
        {
            Calls++;
            return Throw ? throw new InvalidOperationException("no database") : Task.CompletedTask;
        }

        public Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private static ScopedInstrumentMessageSink NewSink(bool throwing)
    {
        lock (ScopedFake.Created) ScopedFake.Created.Clear();
        ScopedFake.Throw = throwing;

        var services = new ServiceCollection();
        services.AddScoped<IInstrumentMessageSink, ScopedFake>();
        return new ScopedInstrumentMessageSink(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task EachMessage_IsHandledInAScopeOfItsOwn_DisposedAfterwards()
    {
        var sink = NewSink(throwing: false);
        var message = new InstrumentMessage();

        (await sink.HandleAsync(Guid.NewGuid(), message)).Should().BeTrue();
        (await sink.HandleAsync(Guid.NewGuid(), message)).Should().BeTrue();
        (await sink.ResolveQueryAsync(Guid.NewGuid(), ["TUBE-1"])).Should().HaveCount(1);

        ScopedFake.Created.Should().HaveCount(3, "a context kept across messages reads mappings as they were days ago");
        ScopedFake.Created.Should().OnlyContain(s => s.Disposed && s.Calls == 1);
    }

    [Fact]
    public async Task FailedImport_IsReportedAsNotAccepted_WithoutStoppingTheLink()
    {
        var sink = NewSink(throwing: true);

        var handle = () => sink.HandleAsync(Guid.NewGuid(), new InstrumentMessage());
        (await handle.Should().NotThrowAsync()).Subject.Should().BeFalse();

        (await sink.ResolveQueryAsync(Guid.NewGuid(), ["TUBE-1"])).Should().BeEmpty();
        await sink.Invoking(s => s.LogRawAsync(Guid.NewGuid(), MessageDirection.Inbound, "H|"))
                  .Should().NotThrowAsync();
    }

    [Fact]
    public async Task Cancellation_StillStopsTheLink()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var services = new ServiceCollection();
        services.AddScoped<IInstrumentMessageSink>(_ => new CancellingSink());
        var real = new ScopedInstrumentMessageSink(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        await real.Invoking(s => s.HandleAsync(Guid.NewGuid(), new InstrumentMessage(), cancelled.Token))
                  .Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class CancellingSink : IInstrumentMessageSink
    {
        public Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
            Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstrumentOrderDownload>>([]);

        public Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
