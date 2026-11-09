using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.Chd;

namespace Romd.Media.Probe;

/// <summary>Owns the probe's production-default decoder, shared cache and optional native pool.</summary>
internal sealed class ConvergedReadDecoder : IAsyncDisposable
{
    private readonly ChdMediaAdapter? _chd;
    private int _disposed;
    private ConvergedReadDecoder(ChdNativeCandidate? native)
    {
        Options = new();
        var cache = new MediaBlockCache(Options.CacheBytes);
        _chd = native is null ? null : new ChdMediaAdapter(native, cache, Options);
        var adapters = _chd is null ? new RomMediaAdapterRegistry() : new RomMediaAdapterRegistry(_chd);
        Decoder = new(adapters, Options, cache);
    }
    internal static ConvergedReadDecoder Managed() => new(null);
    internal static ConvergedReadDecoder NativeChd(ChdNativeCandidate native) => new(native ?? throw new ArgumentNullException(nameof(native)));
    internal ReadDecoderOptions Options { get; }
    internal RomRepresentationDecoder Decoder { get; }
    internal ChdReadPoolSnapshot? NativeSnapshot => _chd?.Snapshot;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        { await Decoder.DisposeAsync(); }
        finally { if (_chd is not null) await _chd.DisposeAsync(); }
    }
}
