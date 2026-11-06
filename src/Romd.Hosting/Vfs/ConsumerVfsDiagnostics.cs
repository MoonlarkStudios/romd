using System.Diagnostics.Metrics;

namespace Romd.Hosting.Vfs;

internal static class ConsumerVfsDiagnostics
{
    private static readonly Meter Meter = new("Romd.Vfs");
    internal static readonly UpDownCounter<long> ActiveRequests = Meter.CreateUpDownCounter<long>("romd.vfs.active_requests");
    internal static readonly UpDownCounter<long> QueuedRequests = Meter.CreateUpDownCounter<long>("romd.vfs.queued_requests");
    internal static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>("romd.vfs.queue_wait", "s");
    internal static readonly Counter<long> RejectedRequests = Meter.CreateCounter<long>("romd.vfs.admission_rejected");
    internal static readonly Counter<long> ResponseBytes = Meter.CreateCounter<long>("romd.vfs.response_bytes", "By");
    internal static readonly Counter<long> Failures = Meter.CreateCounter<long>("romd.vfs.failures");
    internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("romd.vfs.request_duration", "s");
}
