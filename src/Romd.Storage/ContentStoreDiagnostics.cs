using System.Diagnostics.Metrics;

namespace Romd.Storage;

/// <summary>Opt-in System.Diagnostics.Metrics instruments. No keys, paths, or user identifiers are emitted.</summary>
public static class ContentStoreDiagnostics
{
    public const string MeterName = "Romd.Storage";
    private static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> RequestedBytes = Meter.CreateCounter<long>("cas.read.requested", "By");
    internal static readonly Counter<long> ReturnedBytes = Meter.CreateCounter<long>("cas.read.returned", "By");
    internal static readonly Counter<long> StoredBytesRead = Meter.CreateCounter<long>("cas.read.stored", "By");
    internal static readonly Counter<long> DecodedBytes = Meter.CreateCounter<long>("cas.read.decoded", "By");
    internal static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("cas.read.frame_cache_hits");
    internal static readonly Counter<long> IndexCacheHits = Meter.CreateCounter<long>("cas.index.cache.hits");
    internal static readonly Counter<long> IndexCacheMisses = Meter.CreateCounter<long>("cas.index.cache.misses");
    internal static readonly Counter<long> CorruptionFailures = Meter.CreateCounter<long>("cas.corruption.failures");
    internal static readonly Counter<long> SyncedBytes = Meter.CreateCounter<long>("cas.write.synced", "By");
    internal static readonly UpDownCounter<long> Readers = Meter.CreateUpDownCounter<long>("cas.readers.active");
    internal static readonly Histogram<double> ReadDuration = Meter.CreateHistogram<double>("cas.read.duration", "s");
    internal static readonly Histogram<double> OpenDuration = Meter.CreateHistogram<double>("cas.open.duration", "s");
    internal static readonly Histogram<double> MutationWait = Meter.CreateHistogram<double>("cas.mutation.wait", "s");
}
