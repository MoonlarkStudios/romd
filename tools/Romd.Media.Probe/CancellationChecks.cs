using Romd.Media.Rvz;
using Romd.Storage;

namespace Romd.Media.Probe;

internal static class CancellationChecks
{
    internal static async Task Run(IContentAddressableStore cas, StorageKey key)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (var cancelOpen = new CancellationTokenSource())
        {
            // Let the header read pass, then block the next artifact read while opening.
            var source = new GatedSource((await cas.OpenRandomAccessAsync(key))!, skip: 1) { Armed = true };
            var opening = Task.Run(() => RvzImage.OpenAsync(source, new RvzReadOptions(), new MediaBlockCache(1 << 20), ct: cancelOpen.Token));
            try
            {
                await source.Entered.Task.WaitAsync(deadline.Token);
                cancelOpen.Cancel();
                await Check.Throws<OperationCanceledException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
                Check.That(source.Disposed, "cancelled open disposes the artifact");
                Check.That(source.ObservedToken == cancelOpen.Token, "open passes cancellation to artifact reads");
            }
            finally
            {
                cancelOpen.Cancel();
                source.Release.TrySetResult();
                try
                { await (await opening).DisposeAsync(); }
                catch (OperationCanceledException) { }
                await source.DisposeAsync();
            }
        }

        var gated = new GatedSource((await cas.OpenRandomAccessAsync(key))!);
        await using var reader = await RvzImage.OpenAsync(gated, new RvzReadOptions(), new MediaBlockCache(1 << 20));
        gated.Armed = true;
        using var cancelRead = new CancellationTokenSource();
        var reading = Task.Run(async () => await reader.ReadAtAsync(reader.Length - 4096, new byte[4096], cancelRead.Token));
        try
        {
            await gated.Entered.Task.WaitAsync(deadline.Token);
            // A read blocked on its artifact must not serialize other reads of the same image.
            byte[] head = new byte[4096];
            Check.That(await reader.ReadAtAsync(0x100000, head).AsTask().WaitAsync(TimeSpan.FromSeconds(10)) == head.Length,
                "a blocked read does not stall concurrent reads");
            Check.That(!reading.IsCompleted, "the blocked read is still waiting on its artifact");

            cancelRead.Cancel();
            await Check.Throws<OperationCanceledException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(5)));
            Check.That(gated.ObservedToken == cancelRead.Token, "reads pass cancellation to artifact reads");
        }
        finally
        {
            cancelRead.Cancel();
            gated.Release.TrySetResult();
            try
            { await reading; }
            catch (OperationCanceledException) { }
        }
    }

    // Blocks only the first read after arming (and after skipping), so other reads proceed.
    private sealed class GatedSource(IRandomAccessContent inner, int skip = 0) : IRandomAccessContent
    {
        public bool Armed;
        private int _reads, _gated;
        public bool Disposed { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Length => inner.Length;
        public ContentReadIntegrity Integrity => inner.Integrity;

        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _reads) > skip && Armed && Interlocked.Exchange(ref _gated, 1) == 0)
            {
                ObservedToken = ct;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await inner.ReadAtAsync(offset, destination, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Disposed)
                return;
            Disposed = true;
            await inner.DisposeAsync();
        }
    }
}
