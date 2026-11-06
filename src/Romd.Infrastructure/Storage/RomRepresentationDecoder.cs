using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

/// <summary>
/// Bounded read-time decoding. One admission caps concurrently open decoded readers of every format; images of
/// recently read artifacts stay idle for reuse where their adapter allows it; decoded blocks share one
/// byte-budgeted cache per host.
/// Cached images and blocks never carry an authorization decision.
/// </summary>
public sealed class RomRepresentationDecoder : IRomRepresentationDecoder, IAsyncDisposable
{
    private readonly RomMediaAdapterRegistry _adapters;
    private readonly ReadDecoderOptions _options;
    private readonly MediaBlockCache _cache;
    private readonly ReadDecoderAdmission _admission;
    private readonly object _gate = new();
    private readonly List<(Sha256 Hash, IRandomAccessContent Content)> _idle = [];
    private bool _disposed;

    public RomRepresentationDecoder() : this(new RomMediaAdapterRegistry(), new()) { }
    public RomRepresentationDecoder(RomMediaAdapterRegistry adapters, ReadDecoderOptions options, MediaBlockCache? cache = null)
    {
        options.Validate();
        if (cache is not null && cache.BudgetBytes != options.CacheBytes)
            throw new InvalidOperationException("Shared media cache budget disagrees with configured decoder limits.");
        _adapters = adapters;
        _options = new()
        {
            MaxConcurrent = options.MaxConcurrent,
            MaxQueued = options.MaxQueued,
            AdmissionTimeoutMilliseconds = options.AdmissionTimeoutMilliseconds,
            MaxIdle = options.MaxIdle,
            CacheBytes = options.CacheBytes
        };
        _cache = cache ?? new MediaBlockCache(_options.CacheBytes);
        _admission = new(_options.MaxConcurrent, _options.MaxQueued);
    }

    public bool CanDecode(RomRepresentationEncoding format) => _adapters.Supports(format);

    public Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, IRandomAccessContent source,
        CancellationToken ct = default, Sha256? artifact = null, TimeSpan? admissionTimeout = null) =>
        DecodeAsync(format, RomArtifactSource.FromRandomAccess(source), new RomMediaOpenContext(artifact,
            admissionTimeout: admissionTimeout), ct);

    public async Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, RomArtifactSource source,
        RomMediaOpenContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ReadDecoderAdmission.Lease? admission = null;
        IRandomAccessContent? decoded = null;
        try
        {
            ArgumentNullException.ThrowIfNull(context);
            ct.ThrowIfCancellationRequested();
            if (context.AdmissionTimeout is { } wait && (wait < TimeSpan.Zero || wait > TimeSpan.FromMinutes(5)))
                throw new ArgumentOutOfRangeException(nameof(RomMediaOpenContext.AdmissionTimeout));
            lock (_gate)
                ObjectDisposedException.ThrowIf(_disposed, this);
            var adapter = _adapters.For(format);
            if (format == RomRepresentationEncoding.Identity)
                return await source.OpenRandomAccessAsync(ct);
            admission = await _admission.EnterAsync(context.AdmissionTimeout ??
                TimeSpan.FromMilliseconds(_options.AdmissionTimeoutMilliseconds), ct);
            ct.ThrowIfCancellationRequested();
            // An adapter that rechecks admitted facts on every open never parks, so reuse cannot skip those checks.
            Sha256? reusable = adapter.ReusesIdleImages ? context.Artifact : null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int index = reusable is { } key ? _idle.FindIndex(item => item.Hash == key) : -1;
                if (index >= 0)
                { decoded = _idle[index].Content; _idle.RemoveAt(index); }
            }
            decoded ??= await adapter.OpenAsync(source, context, _cache, ct);
            await source.DisposeAsync(); // Releases a snapshot the adapter did not claim, as on an idle hit.
            ct.ThrowIfCancellationRequested();
            return new Lease(this, decoded, reusable, admission);
        }
        catch
        {
            try
            {
                try
                { if (decoded is not null) await decoded.DisposeAsync(); }
                finally { await source.DisposeAsync(); }
            }
            finally { admission?.Dispose(); }
            throw;
        }
    }

    private async ValueTask ReleaseAsync(IRandomAccessContent content, Sha256? artifact, bool failed,
        ReadDecoderAdmission.Lease admission)
    {
        bool keep;
        lock (_gate)
        {
            keep = !_disposed && !failed && artifact.HasValue && _idle.Count < _options.MaxIdle;
            if (keep)
                _idle.Add((artifact!.Value, content));
        }
        try
        { if (!keep) await content.DisposeAsync(); }
        finally { admission.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        IRandomAccessContent[] idle;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            idle = _idle.Select(item => item.Content).ToArray();
            _idle.Clear();
        }
        _admission.Dispose();
        foreach (var content in idle)
            await content.DisposeAsync();
        // Active leases dispose their content instead of returning it to the pool after shutdown.
    }

    // Reads through one lease are serialized, so disposal never races an in-flight read.
    private sealed class Lease(RomRepresentationDecoder pool, IRandomAccessContent content, Sha256? artifact,
        ReadDecoderAdmission.Lease admission) : IRandomAccessContent
    {
        private readonly SemaphoreSlim _gate = new(1);
        private int _disposed;
        private bool _failed;
        public long Length => content.Length;
        public ContentReadIntegrity Integrity => content.Integrity;
        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return await content.ReadAtAsync(offset, destination, ct);
            }
            catch { _failed = true; throw; }
            finally { _gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    await pool.ReleaseAsync(content, artifact, _failed, admission);
            }
            finally { _gate.Release(); }
        }
    }
}
