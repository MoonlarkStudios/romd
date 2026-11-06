using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomDecoderQueueTests
{
    [Fact]
    public async Task DecodeAsync_DefaultAdmission_WaitsWithoutOpeningSourceThenResumes()
    {
        var adapter = new Adapter();
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter),
            new() { MaxConcurrent = 1, MaxIdle = 0 });
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        using var cancellation = new CancellationTokenSource();
        var source = new Source();
        var waiting = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, cancellation.Token);
        try
        {
            waiting.IsCompleted.ShouldBeFalse("default admission must queue instead of forcing a 15-second HTTP retry");
            adapter.Opened.ShouldBe(1);
            source.Disposals.ShouldBe(0);
            await occupied.DisposeAsync();
            await using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            adapter.Opened.ShouldBe(2);
            resumed.Length.ShouldBe(4);
            await resumed.DisposeAsync();
            source.Disposals.ShouldBe(1);
        }
        finally
        {
            await cancellation.CancelAsync();
            await JoinAsync(waiting);
        }
    }

    [Fact]
    public async Task DecodeAsync_DefaultAdmission_OlderWaitersPrecedeNewBulkOpen()
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()),
            new() { MaxConcurrent = 1, MaxIdle = 0 });
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        using var cancellation = new CancellationTokenSource();
        var first = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), cancellation.Token);
        var second = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), cancellation.Token);
        Task<IRandomAccessContent>? later = null;
        try
        {
            first.IsCompleted.ShouldBeFalse();
            second.IsCompleted.ShouldBeFalse();
            await occupied.DisposeAsync();
            later = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), cancellation.Token);
            await using var firstLease = await first.WaitAsync(TimeSpan.FromSeconds(5));
            second.IsCompleted.ShouldBeFalse();
            later.IsCompleted.ShouldBeFalse();
            await firstLease.DisposeAsync();
            await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
            later.IsCompleted.ShouldBeFalse();
            await secondLease.DisposeAsync();
            await using var lastLease = await later.WaitAsync(TimeSpan.FromSeconds(5));
            lastLease.Length.ShouldBe(4);
        }
        finally
        {
            await cancellation.CancelAsync();
            await JoinAsync(first);
            await JoinAsync(second);
            if (later is not null)
                await JoinAsync(later);
        }
    }

    [Fact]
    public async Task DisposeAsync_QueuedRead_RefusesAndReleasesItsSource()
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()),
            new() { MaxConcurrent = 1, MaxIdle = 0 });
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        using var cancellation = new CancellationTokenSource();
        var source = new Source();
        var waiting = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, cancellation.Token);
        try
        {
            waiting.IsCompleted.ShouldBeFalse();
            await decoder.DisposeAsync();
            await Should.ThrowAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            source.Disposals.ShouldBe(1);
            await occupied.DisposeAsync();
        }
        finally
        {
            await cancellation.CancelAsync();
            await JoinAsync(waiting);
        }
    }

    [Fact]
    public async Task DecodeAsync_QueueFull_RefusesAndOwnsSourceAcrossFormats()
    {
        var rvz = new Adapter(RomRepresentationEncoding.Rvz);
        var chd = new Adapter(RomRepresentationEncoding.Chd);
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(rvz, chd),
            new() { MaxConcurrent = 1, MaxQueued = 2, MaxIdle = 0 });
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        using var cancellation = new CancellationTokenSource();
        var first = decoder.DecodeAsync(RomRepresentationEncoding.Chd, new Source(), cancellation.Token);
        var second = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), cancellation.Token);
        var refused = new Source();
        try
        {
            await Should.ThrowAsync<ResourceCapacityException>(() => decoder.DecodeAsync(RomRepresentationEncoding.Chd, refused));
            refused.Disposals.ShouldBe(1);
            rvz.Opened.ShouldBe(1);
            chd.Opened.ShouldBe(0);
            first.IsCompleted.ShouldBeFalse();
            second.IsCompleted.ShouldBeFalse();
            await occupied.DisposeAsync();
            await using var firstLease = await first.WaitAsync(TimeSpan.FromSeconds(5));
            chd.Opened.ShouldBe(1);
            second.IsCompleted.ShouldBeFalse();
            await firstLease.DisposeAsync();
            await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
            rvz.Opened.ShouldBe(2);
        }
        finally
        {
            await cancellation.CancelAsync();
            await JoinAsync(first);
            await JoinAsync(second);
        }
    }

    [Fact]
    public async Task DecodeAsync_CancelledMiddleWaiter_RemovesOnlyItsSourceAndPreservesFifo()
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()),
            new() { MaxConcurrent = 1, MaxIdle = 0 });
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        using var run = new CancellationTokenSource();
        using var middleCancellation = new CancellationTokenSource();
        var first = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), run.Token);
        var removedSource = new Source();
        var middle = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, removedSource, middleCancellation.Token);
        var last = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), run.Token);
        try
        {
            await middleCancellation.CancelAsync();
            var error = await Should.ThrowAsync<OperationCanceledException>(() => middle);
            error.CancellationToken.ShouldBe(middleCancellation.Token);
            removedSource.Disposals.ShouldBe(1);
            first.IsCompleted.ShouldBeFalse();
            last.IsCompleted.ShouldBeFalse();
            await occupied.DisposeAsync();
            await using var firstLease = await first.WaitAsync(TimeSpan.FromSeconds(5));
            last.IsCompleted.ShouldBeFalse();
            await firstLease.DisposeAsync();
            await using var lastLease = await last.WaitAsync(TimeSpan.FromSeconds(5));
            lastLease.Length.ShouldBe(4);
        }
        finally
        {
            await run.CancelAsync();
            await JoinAsync(first);
            await JoinAsync(last);
        }
    }

    [Fact]
    public async Task DecodeAsync_Timeout_RemovesQueueEntryAndRetainsTheOccupiedSlot()
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()),
            new() { MaxConcurrent = 1, MaxQueued = 1, MaxIdle = 0 });
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        var timedOut = new Source();
        await Should.ThrowAsync<ResourceCapacityException>(() => decoder.DecodeAsync(RomRepresentationEncoding.Rvz, timedOut,
            admissionTimeout: TimeSpan.FromMilliseconds(25)));
        timedOut.Disposals.ShouldBe(1);
        var stillFull = new Source();
        await Should.ThrowAsync<ResourceCapacityException>(() => decoder.DecodeAsync(RomRepresentationEncoding.Rvz, stillFull,
            admissionTimeout: TimeSpan.Zero));
        stillFull.Disposals.ShouldBe(1);
        await occupied.DisposeAsync();
        await using var next = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), admissionTimeout: TimeSpan.Zero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeAsync_QueuedOpenOrActiveDisposalFails_TransfersCapacityToNextWaiter(bool disposal)
    {
        var adapter = new Adapter();
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter),
            new() { MaxConcurrent = 1, MaxIdle = 0 });
        var failedSource = new Source { FailDispose = disposal };
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, disposal ? failedSource : new Source());
        using var cancellation = new CancellationTokenSource();
        Task<IRandomAccessContent>? failedOpen = null;
        if (!disposal)
        {
            adapter.FailNextOpen = 1;
            failedOpen = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, failedSource, cancellation.Token);
        }
        var next = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), cancellation.Token);
        try
        {
            if (disposal)
                await Should.ThrowAsync<IOException>(() => occupied.DisposeAsync().AsTask());
            else
            {
                await occupied.DisposeAsync();
                await Should.ThrowAsync<InvalidDataException>(() => failedOpen!);
            }
            failedSource.Disposals.ShouldBe(1);
            await using var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5));
            nextLease.Length.ShouldBe(4);
        }
        finally
        {
            await cancellation.CancelAsync();
            await JoinAsync(next);
        }
    }

    [Fact]
    public async Task DecodeAsync_CancellationRacesGrant_ReturnsUnclaimedPermitAndDisposesSourceOnce()
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()),
            new() { MaxConcurrent = 1, MaxIdle = 0 });
        for (int iteration = 0; iteration < 100; iteration++)
        {
            await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
            using var cancellation = new CancellationTokenSource();
            var source = new Source();
            var waiting = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, cancellation.Token);
            await cancellation.CancelAsync();
            await occupied.DisposeAsync();
            try
            { await (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync(); }
            catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(cancellation.Token); }
            source.Disposals.ShouldBe(1);
            await using var probe = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), admissionTimeout: TimeSpan.Zero);
            await Should.ThrowAsync<ResourceCapacityException>(() => decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(),
                admissionTimeout: TimeSpan.Zero));
        }
    }

    [Theory]
    [InlineData(-1, 5000)]
    [InlineData(129, 5000)]
    [InlineData(8, -1)]
    [InlineData(8, 300001)]
    public void Constructor_InvalidQueuePolicy_Refuses(int queued, int timeout)
    {
        Should.Throw<InvalidOperationException>(() => new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()),
            new() { MaxQueued = queued, AdmissionTimeoutMilliseconds = timeout }));
    }

    [Fact]
    public async Task Constructor_CallerMutatesQueueOptions_UsesCapturedPolicy()
    {
        var options = new ReadDecoderOptions { MaxConcurrent = 1, MaxQueued = 1, MaxIdle = 0 };
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()), options);
        options.MaxQueued = 0;
        options.AdmissionTimeoutMilliseconds = 0;
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        using var cancellation = new CancellationTokenSource();
        var queued = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), cancellation.Token);
        try
        {
            queued.IsCompleted.ShouldBeFalse();
            await occupied.DisposeAsync();
            await using var lease = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            lease.Length.ShouldBe(4);
        }
        finally
        {
            await cancellation.CancelAsync();
            await JoinAsync(queued);
        }
    }

    [Fact]
    public async Task DecodeAsync_ConfiguredDefaultWaitExpires_ClosesSourceWithoutCallerOverride()
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()),
            new() { MaxConcurrent = 1, MaxIdle = 0, AdmissionTimeoutMilliseconds = 25 });
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        var timedOut = new Source();
        await Should.ThrowAsync<ResourceCapacityException>(() => decoder.DecodeAsync(RomRepresentationEncoding.Rvz, timedOut)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        timedOut.Disposals.ShouldBe(1);
        await occupied.DisposeAsync();
        await using var next = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source(), admissionTimeout: TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0, 5000)]
    [InlineData(8, 0)]
    public async Task DecodeAsync_ConfiguredFailFast_DoesNotEnqueueAndOwnsSource(int queued, int timeout)
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new Adapter()),
            new() { MaxConcurrent = 1, MaxIdle = 0, MaxQueued = queued, AdmissionTimeoutMilliseconds = timeout });
        await using var occupied = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Source());
        var refused = new Source();
        var result = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, refused);
        result.IsCompleted.ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(() => result);
        refused.Disposals.ShouldBe(1);
    }

    private static async Task JoinAsync(Task<IRandomAccessContent> task)
    {
        try
        { await (await task).DisposeAsync(); }
        catch (Exception error) when (error is ResourceCapacityException or OperationCanceledException or ObjectDisposedException) { }
    }

    private sealed class Adapter(RomRepresentationEncoding encoding = RomRepresentationEncoding.Rvz) : IRomMediaAdapter
    {
        internal int Opened;
        internal int FailNextOpen;
        public RomRepresentationEncoding Encoding => encoding;
        public bool ReusesIdleImages => false;

        public async ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
            MediaBlockCache cache, CancellationToken ct)
        {
            Interlocked.Increment(ref Opened);
            var opened = await source.OpenRandomAccessAsync(ct);
            if (Interlocked.Exchange(ref FailNextOpen, 0) != 0)
            {
                await opened.DisposeAsync();
                throw new InvalidDataException("Injected adapter open failure.");
            }
            return opened;
        }
    }

    private sealed class Source : IRandomAccessContent
    {
        internal int Disposals;
        internal bool FailDispose;
        public long Length => 4;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default) =>
            ValueTask.FromResult(0);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            if (FailDispose)
                throw new IOException("Injected source disposal failure.");
            return ValueTask.CompletedTask;
        }
    }
}
