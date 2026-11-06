using System.Diagnostics;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Observes the production decoder without changing admission, ownership or retry policy.</summary>
internal sealed class InteractiveDecoderObservations : IRomRepresentationDecoder
{
    private readonly IRomRepresentationDecoder _inner;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _rejected = new(StringComparer.Ordinal);
    private long _opens;
    private double _maximumRejectedMilliseconds;

    internal InteractiveDecoderObservations(IRomRepresentationDecoder inner) => _inner = inner;

    public bool CanDecode(RomRepresentationEncoding format) => _inner.CanDecode(format);

    public Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, IRandomAccessContent source,
        CancellationToken ct, Sha256? artifact, TimeSpan? admissionTimeout) =>
        ObserveAsync(() => _inner.DecodeAsync(format, source, ct, artifact, admissionTimeout));

    public Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, RomArtifactSource source,
        RomMediaOpenContext context, CancellationToken ct) =>
        ObserveAsync(() => _inner.DecodeAsync(format, source, context, ct));

    private async Task<IRandomAccessContent> ObserveAsync(Func<Task<IRandomAccessContent>> open)
    {
        Interlocked.Increment(ref _opens);
        long started = Stopwatch.GetTimestamp();
        try
        { return await open(); }
        catch (ResourceCapacityException error)
        {
            string cause = error.Message switch
            {
                "Media read capacity is occupied; retry later." => "media-reader-slots",
                "CHD read capacity is occupied; retry later." => "chd-native-pool",
                _ => "other-resource-capacity"
            };
            lock (_gate)
            {
                _rejected[cause] = _rejected.GetValueOrDefault(cause) + 1;
                _maximumRejectedMilliseconds = Math.Max(_maximumRejectedMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            throw;
        }
    }

    internal object Snapshot()
    {
        lock (_gate)
            return new
            {
                Opens = Interlocked.Read(ref _opens),
                CapacityRejections = new Dictionary<string, long>(_rejected),
                MaximumRejectedOpenMilliseconds = _maximumRejectedMilliseconds,
                Scope = "Directly observed production ResourceCapacityException messages at decoder open. Media-reader-slots identifies RomRepresentationDecoder's shared read admission; chd-native-pool identifies the adapter's translated pool timeout. Other capacity failures remain separate. Observer forwards the exact source/context and never changes ownership or opens the source."
            };
    }
}
