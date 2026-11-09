using System.Collections.Concurrent;
using System.Diagnostics;
using Romd.Hosting.Vfs;
using Romd.Media.Chd;

namespace Romd.Media.Probe;

/// <summary>Bounded exact latency samples; missed and failed arrivals never become successful samples.</summary>
internal sealed class InteractiveReadEvidence(IReadOnlyList<ChdCdInteractiveReadChecks.Case> cases)
{
    private const int _maximumSamples = 12_000;
    private readonly object _gate = new();
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly List<Phase> _phases = [];
    private int _samples;

    internal string Status { get; set; } = "in-progress";
    internal string? Error { get; set; }
    internal System.Text.Json.JsonElement? HttpLimits { get; set; }
    internal object? DecoderObservations { get; set; }
    internal ChdReadPoolSnapshot? ClosedPool { get; set; }

    internal void BeginRead(ChdCdInteractiveReadChecks.Case sample, string phase)
    {
        lock (_gate)
        {
            if (++_samples > _maximumSamples)
                throw new InvalidOperationException("Interactive latency sample bound exceeded.");
            var group = Get(sample, phase);
            group.Started++;
            group.InFlight++;
        }
    }

    internal void Fail(ChdCdInteractiveReadChecks.Case sample, string phase, string outcome)
    {
        lock (_gate)
        {
            var group = Get(sample, phase);
            group.Outcomes[outcome] = group.Outcomes.GetValueOrDefault(outcome) + 1;
            group.InFlight--;
        }
    }

    internal void Record(ChdCdInteractiveReadChecks.Case sample, string phase, InteractiveRangeRead.Measurement measurement, double dispatch)
    {
        lock (_gate)
        {
            var group = Get(sample, phase);
            group.InFlight--;
            group.Outcomes[measurement.Outcome] = group.Outcomes.GetValueOrDefault(measurement.Outcome) + 1;
            group.Attempts += measurement.Attempts;
            group.Rejections += measurement.Rejections;
            group.BackoffMilliseconds += measurement.BackoffMilliseconds;
            group.Boundaries[sample.Boundary] = group.Boundaries.GetValueOrDefault(sample.Boundary) + 1;
            group.Dispatch.Add(dispatch);
            if (measurement.Outcome == "completed")
            {
                group.FirstByte.Add(measurement.FirstByteMilliseconds!.Value);
                group.Completion.Add(measurement.ElapsedMilliseconds);
                group.ScheduledCompletion.Add(measurement.ElapsedMilliseconds + dispatch);
            }
            else
                group.FailureElapsed.Add(measurement.ElapsedMilliseconds);
            if (measurement.Outcome == "completed" && (group.WorstCompleted is null || measurement.ElapsedMilliseconds > group.WorstCompleted.ElapsedMilliseconds))
            {
                group.WorstCompleted = measurement;
                group.WorstCompletedOffset = sample.Range.Offset;
            }
            if (group.Worst is null || measurement.ElapsedMilliseconds > group.Worst.ElapsedMilliseconds)
            {
                group.Worst = measurement;
                group.WorstOffset = sample.Range.Offset;
                group.WorstBoundary = sample.Boundary;
            }
        }
    }

    internal void Missed(ChdCdInteractiveReadChecks.Case sample, string phase)
    {
        lock (_gate)
        {
            var group = Get(sample, phase);
            group.Outcomes["missed-arrival"] = group.Outcomes.GetValueOrDefault("missed-arrival") + 1;
        }
    }

    internal Phase BeginPhase(string name, int bulkReaders, TimeSpan duration)
    {
        var phase = new Phase(name, bulkReaders, duration);
        lock (_gate)
            _phases.Add(phase);
        return phase;
    }

    private Group Get(ChdCdInteractiveReadChecks.Case sample, string phase)
    {
        string key = $"{phase}/disc-{sample.Member.Disc}/{sample.Member.Kind}/{sample.RequestedBytes}";
        if (!_groups.TryGetValue(key, out var group))
        {
            if (_groups.Count >= 1024)
                throw new InvalidOperationException("Interactive group bound exceeded.");
            _groups.Add(key, group = new());
        }
        return group;
    }

    internal object Report()
    {
        lock (_gate)
            return new
            {
                Status,
                Error,
                ClosedPool,
                HttpLimits,
                DecoderObservations,
                Samples = _samples,
                MaximumSamples = _maximumSamples,
                IndependentOracleBufferBytes = cases.Sum(sample => sample.Range.Expected.LongLength),
                RangeCases = cases.Select(sample => new
                {
                    sample.Member.Disc,
                    sample.Member.Ordinal,
                    sample.Member.Kind,
                    sample.RequestedBytes,
                    sample.Boundary,
                    sample.Range.Offset,
                    ExpectedBytes = sample.Range.Expected.Length
                }).ToArray(),
                Groups = _groups.ToDictionary(pair => pair.Key, pair => pair.Value.Report()),
                PhaseAndSize = _groups.GroupBy(pair => pair.Key.Split('/')[0] + "/" + pair.Key.Split('/')[3]).ToDictionary(
                group => group.Key, group => new
                {
                    Outcomes = group.SelectMany(pair => pair.Value.Outcomes).GroupBy(pair => pair.Key)
                        .ToDictionary(outcome => outcome.Key, outcome => outcome.Sum(pair => pair.Value)),
                    FirstByteMilliseconds = Percentiles(group.SelectMany(pair => pair.Value.FirstByte).ToArray()),
                    CompletionMilliseconds = Percentiles(group.SelectMany(pair => pair.Value.Completion).ToArray()),
                    ScheduledCompletionMilliseconds = Percentiles(group.SelectMany(pair => pair.Value.ScheduledCompletion).ToArray()),
                    FailureElapsedMilliseconds = Percentiles(group.SelectMany(pair => pair.Value.FailureElapsed).ToArray())
                }),
                Phases = _phases.Select(phase => phase.Report()).ToArray(),
                Percentiles = "Exact nearest-rank percentiles over completed observations, with sample counts. First byte and completion start before the first HTTP attempt; retry/backoff included. Scheduled completion also includes dispatch delay. Missed arrivals, deadlines and transport failures are separate denominator outcomes, not successful percentile samples. Maximum is exact; fresh-decoder groups contain only one observation per selected disc/type/size, so their percentiles do not establish a distribution."
            };
    }

    private sealed class Group
    {
        internal Dictionary<string, int> Outcomes { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> Boundaries { get; } = new(StringComparer.Ordinal);
        internal List<double> FirstByte { get; } = [];
        internal List<double> Completion { get; } = [];
        internal List<double> ScheduledCompletion { get; } = [];
        internal List<double> FailureElapsed { get; } = [];
        internal List<double> Dispatch { get; } = [];
        internal long Attempts, Rejections, Started, InFlight;
        internal double BackoffMilliseconds;
        internal InteractiveRangeRead.Measurement? Worst, WorstCompleted;
        internal long WorstCompletedOffset;
        internal long WorstOffset;
        internal string? WorstBoundary;

        internal object Report() => new
        {
            Outcomes = new Dictionary<string, int>(Outcomes),
            Boundaries = new Dictionary<string, int>(Boundaries),
            Attempts,
            Rejections,
            BackoffMilliseconds,
            Started,
            InFlight,
            ObservedArrivals = Outcomes.Values.Sum() + InFlight,
            FirstByteMilliseconds = Percentiles(FirstByte),
            CompletionMilliseconds = Percentiles(Completion),
            ScheduledCompletionMilliseconds = Percentiles(ScheduledCompletion),
            DispatchMilliseconds = Percentiles(Dispatch),
            FailureElapsedMilliseconds = Percentiles(FailureElapsed),
            Worst,
            WorstOffset,
            WorstBoundary,
            WorstCompleted,
            WorstCompletedOffset
        };
    }

    internal static object Percentiles(IReadOnlyCollection<double> samples)
    {
        double[] sorted = samples.Order().ToArray();
        double? Quantile(double fraction) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
        return new
        {
            Count = sorted.Length,
            P50Milliseconds = Quantile(.5),
            P95Milliseconds = Quantile(.95),
            P99Milliseconds = Quantile(.99),
            MaximumMilliseconds = sorted.Length == 0 ? (double?)null : sorted[^1]
        };
    }

    internal sealed class Phase(string name, int bulkReaders, TimeSpan duration)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly TimeSpan _cpuStarted = Process.GetCurrentProcess().TotalProcessorTime;
        private readonly long _allocationStarted = GC.GetTotalAllocatedBytes();
        private readonly ConcurrentDictionary<string, long> _bulkByMember = new(StringComparer.Ordinal);
        private double? _cpuSeconds;
        private long? _allocatedBytes;
        private long _bulkBytes, _bulkFiles, _peakRss;
        private int _peakQueued;
        private ChdReadPoolSnapshot? _pool;
        internal long BulkRejections;
        internal InteractiveReadSchedule.Result? Schedule { get; set; }
        internal object? Resources { get; set; }
        internal object? DecoderObservations { get; set; }

        internal void BulkCompleted(int disc, int member, long bytes)
        {
            Interlocked.Add(ref _bulkBytes, bytes);
            Interlocked.Increment(ref _bulkFiles);
            _bulkByMember.AddOrUpdate($"{disc}/{member}", 1, (_, count) => count + 1);
        }

        internal void Observe(ChdReadPoolSnapshot? pool, ConsumerVfsAdmission admission)
        {
            _pool = pool;
            _peakQueued = Math.Max(_peakQueued, admission.QueuedRequestCount);
            using var process = Process.GetCurrentProcess();
            _peakRss = Math.Max(_peakRss, process.WorkingSet64);
        }

        internal void Complete()
        {
            _elapsed.Stop();
            using var process = Process.GetCurrentProcess();
            _cpuSeconds = (process.TotalProcessorTime - _cpuStarted).TotalSeconds;
            _allocatedBytes = GC.GetTotalAllocatedBytes() - _allocationStarted;
        }

        internal object Report() => new
        {
            Name = name,
            BulkReaders = bulkReaders,
            RequestedSeconds = duration.TotalSeconds,
            ElapsedSeconds = _elapsed.Elapsed.TotalSeconds,
            Schedule,
            BulkRejections = Interlocked.Read(ref BulkRejections),
            VerifiedBulkFiles = Interlocked.Read(ref _bulkFiles),
            VerifiedBulkBytes = Interlocked.Read(ref _bulkBytes),
            BulkCompletedByMember = _bulkByMember.ToDictionary(),
            VerifiedBulkMiBPerSecond = Interlocked.Read(ref _bulkBytes) / (1024d * 1024 * Math.Max(.001, _elapsed.Elapsed.TotalSeconds)),
            PeakProcessWorkingSetBytes = _peakRss,
            PeakHttpQueued = _peakQueued,
            LastPool = _pool,
            CpuSeconds = _cpuSeconds,
            AllocatedBytes = _allocatedBytes,
            Resources,
            DecoderObservations,
            Scope = "Whole-response bulk bytes count only after exact SHA256 verification. Intentionally canceled partial bulk responses are excluded. Elapsed includes draining outstanding interactive requests after arrivals stop; requested duration is the arrival window. Peaks are sampled, not instantaneous proofs."
        };
    }
}
