using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>Process-local probe telemetry; contains no paths, user ids, or credentials.</summary>
internal sealed class VfsProbeMetrics : IDisposable
{
    private readonly ConcurrentQueue<Sample> _samples = new();
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<IDisposable> _databaseSubscriptions = new();
    private readonly IDisposable _diagnostics;
    private long _responseBytes;
    public long ResponseBytes => Interlocked.Read(ref _responseBytes);

    public VfsProbeMetrics()
    {
        _diagnostics = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore")
                _databaseSubscriptions.Add(listener.Subscribe(
                    new Observer<KeyValuePair<string, object?>>(item =>
                    {
                        if (item.Value is CommandExecutedEventData command)
                            _samples.Enqueue(new("ef.command.duration", "SQL", "success", command.Duration.TotalMilliseconds));
                    }), name => name == RelationalEventId.CommandExecuted.Name));
        }));
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name is "romd.vfs.response_bytes" or "romd.vfs.request_duration" or "romd.vfs.queue_wait" or "http.server.request.duration")
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string method = "unknown";
            string status = "unknown";
            foreach (var tag in tags)
            {
                if (tag.Key is "method" or "http.request.method")
                    method = tag.Value?.ToString() ?? "unknown";
                if (tag.Key is "status" or "http.response.status_code")
                    status = tag.Value?.ToString() ?? "unknown";
            }
            _samples.Enqueue(new(instrument.Name, method, status, value * 1000));
        });
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "romd.vfs.response_bytes")
                Interlocked.Add(ref _responseBytes, value);
        });
        _listener.Start();
    }

    public object Snapshot() => _samples.ToArray()
        .GroupBy(sample => (sample.Instrument, sample.Method, sample.Status))
        .OrderBy(group => group.Key.Instrument).ThenBy(group => group.Key.Method).ThenBy(group => group.Key.Status)
        .Select(group =>
        {
            double[] times = group.Select(sample => sample.Milliseconds).Order().ToArray();
            return new
            {
                group.Key.Instrument,
                group.Key.Method,
                group.Key.Status,
                Count = times.Length,
                TotalMilliseconds = times.Sum(),
                P50Milliseconds = times[(int)Math.Ceiling(times.Length * .50) - 1],
                P95Milliseconds = times[(int)Math.Ceiling(times.Length * .95) - 1],
                P99Milliseconds = times[(int)Math.Ceiling(times.Length * .99) - 1]
            };
        }).ToArray();

    public void Dispose()
    {
        _listener.Dispose();
        _diagnostics.Dispose();
        foreach (var subscription in _databaseSubscriptions)
            subscription.Dispose();
    }

    private sealed record Sample(string Instrument, string Method, string Status, double Milliseconds);

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
