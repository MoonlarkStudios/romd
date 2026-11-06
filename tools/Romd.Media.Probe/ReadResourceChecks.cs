using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ErrorOr;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Read-only qualification observations; never participates in admission or reclamation.</summary>
internal sealed class ReadResourceChecks
{
    internal sealed record StagingScope(string PoolRoot, int MaximumFiles, long MaximumBytes,
        long MaximumFileBytes, long MinimumFreeBytes);
    internal sealed record StagingSnapshot(int Reservations, long ChargedBytes, long PayloadBytes, int HeldSlots,
        long AvailableFreeBytes, string Volume);
    internal sealed record ResourceSnapshot(int ActiveDecoderSlots, int QueuedDecoderReaders, int LifetimePeakQueuedDecoderReaders, int IdleImages, long CacheUsedBytes,
        long ProcessWorkingSetBytes, long ProcessLifetimePeakWorkingSetBytes, long ManagedHeapBytes,
        long ManagedAllocatedBytes, double ProcessCpuMilliseconds, StagingSnapshot? Staging,
        string StagingObservation, long? CgroupCurrentBytes, long? CgroupKernelPeakBytes, string? CgroupMaximum);

    private const int AccountingLimit = 32768;
    private const int SampleMilliseconds = 250;
    internal static TimeSpan SamplingInterval => TimeSpan.FromMilliseconds(SampleMilliseconds);
    private readonly object _gate = new();
    private readonly RomRepresentationDecoder _decoder;
    private readonly FieldInfo _decoderGate, _cache, _idle, _admission;
    private readonly StagingScope? _staging;
    private readonly ReadDecoderOptions _limits;
    private readonly Dictionary<string, Peaks> _phases = new(StringComparer.Ordinal);
    private ResourceSnapshot? _latest, _baseline;
    private Error? _failure;
    private long _samples, _stagingTransient, _loadStagingUnknownSamples;
    private bool _stagingChanged;

    private ReadResourceChecks(RomRepresentationDecoder decoder, FieldInfo decoderGate, FieldInfo cache,
        FieldInfo idle, FieldInfo admission, ReadDecoderOptions limits, StagingScope? staging)
    {
        _decoder = decoder;
        _decoderGate = decoderGate;
        _cache = cache;
        _idle = idle;
        _admission = admission;
        _limits = limits;
        _staging = staging;
    }

    internal static ErrorOr<ReadResourceChecks> Create(RomRepresentationDecoder decoder, StagingScope? staging = null)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(RomRepresentationDecoder);
        var gate = type.GetField("_gate", flags);
        var cache = type.GetField("_cache", flags);
        var idle = type.GetField("_idle", flags);
        var admission = type.GetField("_admission", flags);
        var options = type.GetField("_options", flags);
        if (decoder.GetType() != type || !type.IsSealed ||
            gate?.FieldType != typeof(object) || cache?.FieldType != typeof(MediaBlockCache) ||
            idle?.FieldType != typeof(List<(Sha256, IRandomAccessContent)>) ||
            admission?.FieldType != typeof(ReadDecoderAdmission) || options?.FieldType != typeof(ReadDecoderOptions) ||
            new[] { gate, cache, idle, admission, options }.Any(field => field?.DeclaringType != type) ||
            options!.GetValue(decoder) is not ReadDecoderOptions actual || gate!.GetValue(decoder) is null ||
            cache!.GetValue(decoder) is not MediaBlockCache || idle!.GetValue(decoder) is not List<(Sha256, IRandomAccessContent)> ||
            admission!.GetValue(decoder) is not ReadDecoderAdmission)
            return Failure("Shape", "The exact decoder assembly shape is not observable.");
        if (actual.MaxConcurrent != 4 || actual.MaxQueued != 8 || actual.AdmissionTimeoutMilliseconds != 5000 ||
            actual.MaxIdle != 2 || actual.CacheBytes != 64L << 20)
            return Failure("Defaults", "The observed decoder is not using the required production defaults.");
        if (staging is not null && (!Path.IsPathFullyQualified(staging.PoolRoot) ||
            Path.GetFullPath(staging.PoolRoot) != staging.PoolRoot || staging.MaximumFiles is < 1 or > 256 ||
            staging.MaximumFileBytes <= 0 || staging.MaximumBytes < staging.MaximumFileBytes ||
            staging.MaximumBytes > 16L << 40 || staging.MinimumFreeBytes < 0))
            return Failure("StagingScope", "The exact staging observation scope is invalid.");
        return new ReadResourceChecks(decoder, gate!, cache!, idle!, admission!,
            new()
            {
                MaxConcurrent = actual.MaxConcurrent,
                MaxQueued = actual.MaxQueued,
                AdmissionTimeoutMilliseconds = actual.AdmissionTimeoutMilliseconds,
                MaxIdle = actual.MaxIdle,
                CacheBytes = actual.CacheBytes
            }, staging);
    }

    internal ErrorOr<Success> BeginLoad()
    {
        var observed = Sample("sustained-load");
        if (observed.IsError)
            return observed.Errors;
        lock (_gate)
        {
            if (_staging is not null && observed.Value.Staging is null)
                return Failure("StagingBaseline", "A confirmed staging baseline is required.");
            _baseline = observed.Value;
            _stagingChanged = false;
            _loadStagingUnknownSamples = 0;
        }
        return Result.Success;
    }

    internal ErrorOr<ResourceSnapshot> Sample(string phase)
    {
        lock (_gate)
        {
            if (_failure is { } failed)
                return failed;
            if (phase is not ("iso-oracle-and-production-catalog" or "metadata-and-capacity" or
                "controlled-vfs-queue" or "sustained-load" or "complete"))
                return Fail(Failure("Phase", "The resource phase is outside the bounded qualification set."));
            try
            {
                int active, idle, queued, peakQueued;
                MediaBlockCache cache;
                lock (_decoderGate.GetValue(_decoder)!)
                {
                    cache = (MediaBlockCache)_cache.GetValue(_decoder)!;
                    idle = ((List<(Sha256, IRandomAccessContent)>)_idle.GetValue(_decoder)!).Count;
                    var admission = ((ReadDecoderAdmission)_admission.GetValue(_decoder)!).Observe();
                    active = admission.Active;
                    queued = admission.Queued;
                    peakQueued = admission.PeakQueued;
                }
                long cacheBytes = cache.UsedBytes;
                if (cache.BudgetBytes != _limits.CacheBytes || active < 0 || active > _limits.MaxConcurrent ||
                    queued < 0 || queued > _limits.MaxQueued || peakQueued < queued || peakQueued > _limits.MaxQueued ||
                    idle < 0 || idle > _limits.MaxIdle || cacheBytes < 0 || cacheBytes > cache.BudgetBytes)
                    return Fail(Failure("Bounds", "Observed cache or decoder admission exceeds its actual configured limit."));
                StagingSnapshot? staged = null;
                string stagingObservation = "unobserved-no-scope";
                if (_staging is not null)
                {
                    var observation = ReadStaging(_staging);
                    if (observation.IsError)
                    {
                        if (observation.FirstError.Code != "ReadProbe.StagingBusy")
                            return Fail(observation.FirstError);
                        _stagingTransient++;
                        if (phase == "sustained-load" && _baseline is not null)
                            _loadStagingUnknownSamples++;
                        stagingObservation = "transient-accounting-contention";
                    }
                    else
                    { staged = observation.Value; stagingObservation = "confirmed-under-accounting-lease"; }
                }
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var cgroup = ReadCgroup();
                _latest = new(active, queued, peakQueued, idle, cacheBytes, process.WorkingSet64, process.PeakWorkingSet64,
                    GC.GetGCMemoryInfo().HeapSizeBytes, GC.GetTotalAllocatedBytes(), process.TotalProcessorTime.TotalMilliseconds,
                    staged, stagingObservation, cgroup.Current, cgroup.Peak, cgroup.Maximum);
                _samples++;
                if (!_phases.TryGetValue(phase, out var peaks))
                    _phases.Add(phase, peaks = new());
                peaks.Add(_latest);
                if (phase == "sustained-load" && _baseline?.Staging is { } before && staged is { } after &&
                    (before.Reservations != after.Reservations || before.ChargedBytes != after.ChargedBytes ||
                     before.PayloadBytes != after.PayloadBytes || before.HeldSlots != after.HeldSlots))
                    _stagingChanged = true;
                return _latest;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or ArgumentException or InvalidOperationException or
                FormatException or OverflowException or System.ComponentModel.Win32Exception)
            {
                return Fail(Failure("Observation", "Resource observation failed; no zero-use fact is inferred."));
            }
        }
    }

    internal async Task SampleUntilCancelledAsync(Func<string> phase, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Sample(phase()).IsError)
                    return;
                await Task.Delay(SampleMilliseconds, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal ResourceSnapshot Snapshot()
    {
        lock (_gate)
            return _latest ?? throw new InvalidOperationException("No confirmed decoder observation exists.");
    }
    internal bool ReadPhaseStagingUnchanged
    {
        get
        {
            lock (_gate)
                return _failure is null && !_stagingChanged && _loadStagingUnknownSamples == 0 && _baseline?.Staging is { } before &&
                _latest?.Staging is { } after && before.Reservations == 0 && before.HeldSlots == 0 && before.PayloadBytes == 0 &&
                after.Reservations == 0 && after.HeldSlots == 0 && after.PayloadBytes == 0 && after.ChargedBytes == 0;
        }
    }
    internal object Report()
    {
        lock (_gate)
            return new
            {
                Samples = _samples,
                SamplingIntervalMilliseconds = SampleMilliseconds,
                FailureCode = _failure?.Code,
                StagingScopeProvided = _staging is not null,
                StagingTransientSamples = _stagingTransient,
                SustainedStagingUnknownSamples = _loadStagingUnknownSamples,
                ReadPhaseStagingUnchanged = _staging is null ? (bool?)null : ReadPhaseStagingUnchanged,
                DecoderAssemblyMvid = typeof(RomRepresentationDecoder).Module.ModuleVersionId,
                CacheAssemblyMvid = typeof(MediaBlockCache).Module.ModuleVersionId,
                ObservedConfiguredDecoderLimits = _limits,
                StagingScope = _staging,
                Baseline = _baseline,
                Latest = _latest,
                Phases = _phases.ToDictionary(pair => pair.Key, pair => pair.Value.Report()),
                SustainedManagedAllocatedBytes = _baseline is null || _latest is null ? (long?)null :
                _latest.ManagedAllocatedBytes - _baseline.ManagedAllocatedBytes,
                SustainedProcessCpuMilliseconds = _baseline is null || _latest is null ? (double?)null :
                _latest.ProcessCpuMilliseconds - _baseline.ProcessCpuMilliseconds,
                Scope = "Current invoking process includes any in-process host and clients; remote hosts, PostgreSQL and worker/child processes are excluded. Peaks are sampled, not hard RSS limits. Kernel cgroup values include the whole current cgroup and its cache; kernel peak is lifetime, not this load phase. Staging is read-only accounting/payload stat evidence; contention is not zero use. No source paths, payloads or custody files are read or reclaimed."
            };
    }

    private Error Fail(Error error) { _failure = error; return error; }
    private static Error Failure(string kind, string description) => Error.Failure("ReadProbe." + kind, description);

    private static ErrorOr<StagingSnapshot> ReadStaging(StagingScope scope)
    {
        string guardPath = Path.Combine(scope.PoolRoot, "accounting.lease");
        if ((File.GetAttributes(guardPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            return Failure("StagingFiles", "The accounting lease is not a supported ordinary file.");
        FileStream guard;
        try
        { guard = new(guardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 ||
            (OperatingSystem.IsLinux() && error.HResult == 11) ||
            (OperatingSystem.IsMacOS() && error.HResult == 35))
        { return Error.Conflict("ReadProbe.StagingBusy", "Accounting could not be observed exclusively; use is unknown."); }
        using (guard)
        {
            // Validate every entry, including the common accounting files, before any evidence read.
            var inventory = ReadInventory(scope);
            if (inventory.IsError)
                return inventory.Errors;
            var names = inventory.Value;
            using var json = JsonDocument.Parse(ReadBounded(Path.Combine(scope.PoolRoot, "reservations.json"), AccountingLimit));
            var root = json.RootElement;
            if (!Unique(root) || root.EnumerateObject().Count() != 5 ||
                root.EnumerateObject().Any(property => property.Name is not
                    ("Version" or "MaximumFiles" or "MaximumBytes" or "MaximumFileBytes" or "Reservations")) ||
                root.GetProperty("Version").GetInt32() != 1 ||
                root.GetProperty("MaximumFiles").GetInt32() != scope.MaximumFiles ||
                root.GetProperty("MaximumBytes").GetInt64() != scope.MaximumBytes ||
                root.GetProperty("MaximumFileBytes").GetInt64() != scope.MaximumFileBytes)
                return Failure("StagingAccounting", "Accounting is invalid or differs from the exact configured pool.");
            var reservations = root.GetProperty("Reservations");
            if (!Unique(reservations) || reservations.EnumerateObject().Count() > scope.MaximumFiles)
                return Failure("StagingAccounting", "Reservation accounting is not bounded and unique.");
            var charges = new Dictionary<int, long>();
            long charged = 0, payload = 0;
            int held = 0;
            foreach (var entry in reservations.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int slot) ||
                    slot < 0 || slot >= scope.MaximumFiles || entry.Value.ValueKind != JsonValueKind.Number ||
                    !entry.Value.TryGetInt64(out long bytes) || bytes < 0 || bytes > scope.MaximumFileBytes ||
                    !charges.TryAdd(slot, bytes) || charged > scope.MaximumBytes - bytes)
                    return Failure("StagingAccounting", "Reservation charges are invalid or exceed the pool.");
                charged += bytes;
            }
            for (int slot = 0; slot < scope.MaximumFiles; slot++)
            {
                string name = $"slot-{slot}.data";
                long bytes = names.Contains(name) ? new FileInfo(Path.Combine(scope.PoolRoot, name)).Length : 0;
                bool hold = names.Contains(name + ".quarantine");
                if (bytes < 0 || bytes > charges.GetValueOrDefault(slot) || (hold && !charges.ContainsKey(slot)))
                    return Failure("StagingPayload", "Payload or held custody cannot be reconciled with measured charges.");
                payload = checked(payload + bytes);
                if (hold)
                    held++;
            }
            var volume = DriveInfo.GetDrives().Where(drive => scope.PoolRoot.StartsWith(
                Path.EndsInDirectorySeparator(drive.RootDirectory.FullName) ? drive.RootDirectory.FullName :
                    drive.RootDirectory.FullName + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .MaxBy(drive => drive.RootDirectory.FullName.Length);
            if (volume is null || volume.AvailableFreeSpace < scope.MinimumFreeBytes)
                return Failure("StagingFloor", "The observed staging volume does not satisfy its configured free-space floor.");
            return new StagingSnapshot(charges.Count, charged, payload, held, volume.AvailableFreeSpace, volume.RootDirectory.FullName);
        }
    }

    private static ErrorOr<HashSet<string>> ReadInventory(StagingScope scope)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(scope.PoolRoot))
        {
            if (names.Count >= scope.MaximumFiles * 3 + 3)
                return Failure("StagingFiles", "Pool entry inventory exceeds its bounded shape.");
            string name = Path.GetFileName(path);
            if (!names.Add(name) || (File.GetAttributes(path) &
                (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                return Failure("StagingFiles", "Pool entries must be unique supported files; links and directories are unqualified.");
            if (name is "accounting.lease" or "reservations.json" or "reservations.new")
                continue;
            if (!name.StartsWith("slot-", StringComparison.Ordinal))
                return Failure("StagingFiles", "Unrecognized pool files remain unqualified.");
            int end = name.IndexOf('.', 5);
            if (end < 0 || !int.TryParse(name.AsSpan(5, end - 5), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int slot) || slot < 0 || slot >= scope.MaximumFiles ||
                name[..end] != "slot-" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                name[end..] is not (".lease" or ".data" or ".data.quarantine"))
                return Failure("StagingFiles", "Unrecognized or noncanonical pool slot remains unqualified.");
        }
        return names;
    }

    private static bool Unique(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();

    internal static byte[] ReadBounded(string path, int maximum)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > maximum)
            throw new InvalidDataException("Resource evidence exceeds its bounded read.");
        byte[] bytes = new byte[maximum + 1];
        int count = 0, read;
        while (count <= maximum && (read = input.Read(bytes, count, maximum + 1 - count)) != 0)
            count += read;
        if (count > maximum)
            throw new InvalidDataException("Resource evidence grew beyond its bounded read.");
        return bytes.AsSpan(0, count).ToArray();
    }
    private static (long? Current, long? Peak, string? Maximum) ReadCgroup()
    {
        const string root = "/sys/fs/cgroup";
        if (!OperatingSystem.IsLinux() || !File.Exists(Path.Combine(root, "memory.current")))
            return (null, null, null);
        string[] unified = System.Text.Encoding.ASCII.GetString(ReadBounded("/proc/self/cgroup", 4096))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("0::", StringComparison.Ordinal)).ToArray();
        // Only a namespace-root cgroup is directly attributable at this mount. Do not guess a host hierarchy mapping.
        if (unified.Length != 1 || unified[0] != "0::/")
            return (null, null, null);
        string Read(string name) => System.Text.Encoding.ASCII.GetString(ReadBounded(Path.Combine(root, name), 64)).Trim();
        long current = long.Parse(Read("memory.current"), System.Globalization.CultureInfo.InvariantCulture);
        long peak = long.Parse(Read("memory.peak"), System.Globalization.CultureInfo.InvariantCulture);
        string maximum = Read("memory.max");
        if (current < 0 || peak < 0 || (maximum != "max" &&
            (!long.TryParse(maximum, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long bound) || bound < 0)))
            throw new InvalidDataException("Cgroup evidence is not a bounded nonnegative memory fact.");
        return (current, peak, maximum);
    }

    private sealed class Peaks
    {
        private long _count, _rss, _heap, _cache, _slots, _queued, _lifetimeQueued, _idle, _charged, _payload, _cgroup, _stagingSamples, _cgroupSamples;
        public void Add(ResourceSnapshot sample)
        {
            _count++;
            _rss = Math.Max(_rss, sample.ProcessWorkingSetBytes);
            _heap = Math.Max(_heap, sample.ManagedHeapBytes);
            _cache = Math.Max(_cache, sample.CacheUsedBytes);
            _slots = Math.Max(_slots, sample.ActiveDecoderSlots);
            _queued = Math.Max(_queued, sample.QueuedDecoderReaders);
            _lifetimeQueued = Math.Max(_lifetimeQueued, sample.LifetimePeakQueuedDecoderReaders);
            _idle = Math.Max(_idle, sample.IdleImages);
            _charged = Math.Max(_charged, sample.Staging?.ChargedBytes ?? 0);
            _payload = Math.Max(_payload, sample.Staging?.PayloadBytes ?? 0);
            _cgroup = Math.Max(_cgroup, sample.CgroupCurrentBytes ?? 0);
            if (sample.Staging is not null)
                _stagingSamples++;
            if (sample.CgroupCurrentBytes is not null)
                _cgroupSamples++;
        }
        public object Report() => new
        {
            Samples = _count,
            SampledPeakWorkingSetBytes = _rss,
            SampledPeakManagedHeapBytes = _heap,
            SampledPeakCacheUsedBytes = _cache,
            SampledPeakActiveDecoderSlots = _slots,
            SampledPeakQueuedDecoderReaders = _queued,
            ObservedLifetimePeakQueuedDecoderReaders = _lifetimeQueued,
            SampledPeakIdleImages = _idle,
            ConfirmedStagingSamples = _stagingSamples,
            SampledPeakChargedStagingBytes = _stagingSamples == 0 ? (long?)null : _charged,
            SampledPeakStagingPayloadBytes = _stagingSamples == 0 ? (long?)null : _payload,
            ObservedCgroupSamples = _cgroupSamples,
            SampledPeakCgroupCurrentBytes = _cgroupSamples == 0 ? (long?)null : _cgroup
        };
    }
}
