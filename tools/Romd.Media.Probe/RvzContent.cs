using Romd.Media.Rvz;
using Romd.Storage;

namespace Romd.Media.Probe;

// Instrument the production decoder: count artifact bytes each operation reads.
internal sealed class RvzContent : IRandomAccessContent
{
    private readonly CountingSource _source;
    private readonly RvzImage _decoded;

    private RvzContent(CountingSource source, RvzImage decoded)
    {
        _source = source;
        _decoded = decoded;
    }

    public static async Task<RvzContent> OpenAsync(IRandomAccessContent source, CancellationToken ct = default)
    {
        var counting = new CountingSource(source);
        return new RvzContent(counting, await RvzImage.OpenAsync(counting, new RvzReadOptions(), new MediaBlockCache(16L << 20), ct: ct));
    }

    public long SourceBytes => _source.Bytes;
    public long Length => _decoded.Length;
    public ContentReadIntegrity Integrity => _decoded.Integrity;
    public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default) => _decoded.ReadAtAsync(offset, destination, ct);
    public ValueTask DisposeAsync() => _decoded.DisposeAsync();

    private sealed class CountingSource(IRandomAccessContent inner) : IRandomAccessContent
    {
        private long _bytes;
        public long Bytes => Interlocked.Read(ref _bytes);
        public long Length => inner.Length;
        public ContentReadIntegrity Integrity => inner.Integrity;
        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            int count = await inner.ReadAtAsync(offset, destination, ct);
            Interlocked.Add(ref _bytes, count);
            return count;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
