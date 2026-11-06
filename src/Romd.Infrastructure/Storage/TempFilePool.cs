using Romd.Application.Common.Content;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Romd.Infrastructure.Storage;

/// <summary>
/// Process-shared staging reservations. Stable exclusive lease files protect owners;
/// the short-lived guard serializes accounting, reclamation and publication of reservations.
/// Requires a local/shared filesystem that honors exclusive FileShare.None opens.
/// </summary>
internal sealed class TempFilePool
{
    internal const string CdCuePoolName = "cd-cue-staging-v1";
    private const long ReservationStep = 8L << 20;
    // Retain OS leases until host exit when containment cannot be proved. Durable
    // markers additionally protect reservations after that exit, without auto-reclaim.
    private static readonly ConcurrentDictionary<string, Lease> Quarantined = new(StringComparer.Ordinal);
    private readonly IFileSystem _fs;
    private readonly string _root;
    private readonly TempStorageOptions _options;
    private readonly long _maximumFile;
    private readonly Func<long> _freeBytes;

    internal TempFilePool(IFileSystem fs, string root, TempStorageOptions options, long maximumFile,
        Func<long>? freeBytes = null)
    {
        _fs = fs;
        _root = root;
        _options = options;
        _maximumFile = maximumFile;
        _fs.Directory.CreateDirectory(_root);
        _freeBytes = freeBytes ?? AvailableFreeBytes;
    }

    internal async Task<Lease> AcquireAsync(long? knownLength, CancellationToken ct)
    {
        if (knownLength is < 0 || knownLength > _maximumFile)
            throw new InvalidDataException("The staged file exceeds Romd:MaxUploadBytes.");
        long started = Stopwatch.GetTimestamp();
        do
        {
            ct.ThrowIfCancellationRequested();
            using (var guard = TryLock("accounting.lease"))
            {
                if (guard is not null)
                {
                    var state = Read();
                    bool changed = Reclaim(state);
                    if (state.MaximumFiles != _options.MaximumFiles || state.MaximumBytes != _options.MaximumBytes ||
                        state.MaximumFileBytes != _maximumFile)
                    {
                        if (state.Reservations.Count != 0)
                            throw new IOException("Staging limits differ across live hosts; use the same configuration.");
                        state = Empty();
                        changed = true;
                    }
                    long reserve = knownLength ?? Math.Min(ReservationStep, _maximumFile);
                    bool hasSpace = CanReserve(state, reserve);
                    for (int slot = 0; hasSpace && slot < state.MaximumFiles; slot++)
                    {
                        if (state.Reservations.ContainsKey(slot))
                            continue;
                        if (HasChildHold(slot))
                            continue;
                        var owner = TryLock($"slot-{slot}.lease");
                        if (owner is null)
                            continue;
                        try
                        {
                            // An interrupted initial reservation can leave data without a record.
                            _fs.File.Delete(DataPath(slot));
                            state.Reservations.Add(slot, reserve);
                            Write(state); // Reserve before any payload bytes can be written.
                            var lease = new Lease(this, slot, owner, reserve);
                            owner = null;
                            return lease;
                        }
                        finally { owner?.Dispose(); }
                    }
                    // Contention polls need no metadata rewrite when nothing changed.
                    if (changed)
                        Write(state);
                }
            }
            await Task.Delay(25, ct);
        } while (Stopwatch.GetElapsedTime(started).TotalSeconds < _options.AdmissionTimeoutSeconds);
        throw new ResourceCapacityException("Temporary staging capacity or free disk space is exhausted; retry later.");
    }

    internal async Task<IReadOnlyList<Lease>> AcquireAggregateAsync(IReadOnlyList<long> lengths, CancellationToken ct, int maximumMembers = 3)
    {
        if (maximumMembers is < 1 or > 128 || lengths.Count < 1 || lengths.Count > maximumMembers || lengths.Any(length => length <= 0 || length > _maximumFile))
            throw new ArgumentOutOfRangeException(nameof(lengths));
        if (lengths.Count > _options.MaximumFiles)
            throw new Romd.Application.Common.Content.ResourceCapacityException("The complete staging set exceeds configured file capacity.");
        long total = lengths.Sum();
        long started = Stopwatch.GetTimestamp();
        do
        {
            ct.ThrowIfCancellationRequested();
            using (var guard = TryLock("accounting.lease"))
            {
                if (guard is not null)
                {
                    var state = Read();
                    bool changed = Reclaim(state);
                    if (state.MaximumFiles != _options.MaximumFiles || state.MaximumBytes != _options.MaximumBytes ||
                        state.MaximumFileBytes != _maximumFile)
                    {
                        if (state.Reservations.Count != 0)
                            throw new IOException("Staging limits differ across live hosts; use the same configuration.");
                        state = Empty();
                        changed = true;
                    }
                    var owners = new List<(int Slot, Stream Owner)>();
                    try
                    {
                        if (lengths.Count <= state.MaximumFiles - state.Reservations.Count && CanReserve(state, total))
                        {
                            for (int slot = 0; owners.Count < lengths.Count && slot < state.MaximumFiles; slot++)
                            {
                                if (state.Reservations.ContainsKey(slot) || HasChildHold(slot))
                                    continue;
                                if (TryLock($"slot-{slot}.lease") is { } owner)
                                    owners.Add((slot, owner));
                            }
                            if (owners.Count == lengths.Count)
                            {
                                for (int index = 0; index < owners.Count; index++)
                                {
                                    _fs.File.Delete(DataPath(owners[index].Slot));
                                    state.Reservations.Add(owners[index].Slot, lengths[index]);
                                }
                                Write(state); // One guarded publication, before any member writes bytes.
                                var result = owners.Select((owner, index) => new Lease(this, owner.Slot, owner.Owner, lengths[index])).ToArray();
                                owners.Clear();
                                return result;
                            }
                        }
                        if (changed)
                            Write(state);
                    }
                    finally { foreach (var owner in owners) owner.Owner.Dispose(); }
                }
            }
            await Task.Delay(25, ct);
        } while (Stopwatch.GetElapsedTime(started).TotalSeconds < _options.AdmissionTimeoutSeconds);
        throw new ResourceCapacityException("Aggregate staging capacity is occupied; retry without retaining partial reservations.");
    }

    private async Task GrowAsync(Lease lease, long required, CancellationToken ct)
    {
        if (required > _maximumFile)
            throw new InvalidDataException("The staged file exceeds Romd:MaxUploadBytes.");
        if (required <= lease.Reserved)
            return;
        using var guard = await GuardAsync(ct);
        var state = Read();
        if (!state.Reservations.TryGetValue(lease.Slot, out long previous) || previous != lease.Reserved)
            throw new IOException("Staging reservation is missing or inconsistent.");
        long desired = Math.Min(_maximumFile, checked(((required - 1) / ReservationStep + 1) * ReservationStep));
        if (!CanReserve(state, desired - previous))
            throw new ResourceCapacityException("Temporary staging byte budget or free disk space is exhausted.");
        state.Reservations[lease.Slot] = desired;
        Write(state);
        lease.Reserved = desired;
    }

    private bool CanReserve(State state, long increase)
    {
        long total = state.Reservations.Values.Sum();
        if (increase > state.MaximumBytes - total)
            return false;
        // Read lengths before free space: concurrent writes then make this conservative.
        long written = state.Reservations.Keys.Sum(slot => _fs.File.Exists(DataPath(slot))
            ? _fs.FileInfo.New(DataPath(slot)).Length : 0);
        long stillToWrite = Math.Max(0, total - written) + increase;
        return _freeBytes() >= checked(_options.MinimumFreeBytes + stillToWrite);
    }

    private async Task<bool> ReduceAsync(Lease lease, long measuredLength, CancellationToken ct)
    {
        if (measuredLength <= 0 || measuredLength > lease.Reserved)
            return false;
        using var guard = await GuardAsync(ct);
        var state = Read();
        if (HasChildHold(lease.Slot) || !state.Reservations.TryGetValue(lease.Slot, out long previous) ||
            previous != lease.Reserved || !_fs.File.Exists(lease.Path) || _fs.FileInfo.New(lease.Path).Length != measuredLength)
            return false;
        state.Reservations[lease.Slot] = measuredLength;
        Write(state);
        lease.Reserved = measuredLength;
        return true;
    }

    private bool Reclaim(State state)
    {
        int before = state.Reservations.Count;
        foreach (int slot in state.Reservations.Keys.ToArray())
        {
            if (HasChildHold(slot))
                continue;
            using var owner = TryLock($"slot-{slot}.lease");
            if (owner is null)
                continue;
            // Never release accounting unless payload deletion succeeds.
            _fs.File.Delete(DataPath(slot));
            state.Reservations.Remove(slot);
        }
        return state.Reservations.Count != before;
    }

    private async ValueTask ReleaseAsync(Lease lease)
    {
        if (HasChildHold(lease.Slot))
        {
            Quarantined.TryAdd(lease.Path, lease);
            return;
        }
        try
        {
            using var guard = await GuardAsync(CancellationToken.None);
            var state = Read();
            if (HasChildHold(lease.Slot))
            {
                Quarantined.TryAdd(lease.Path, lease);
                return;
            }
            _fs.File.Delete(DataPath(lease.Slot));
            state.Reservations.Remove(lease.Slot);
            Write(state);
        }
        finally { if (!Quarantined.ContainsKey(lease.Path)) lease.Owner.Dispose(); }
        // A failed delete/write leaves its reservation for the next guarded reclamation.
    }

    private static string PayloadExtension(string root) => Path.GetFileName(root) == CdCuePoolName ? ".cue" : ".data";
    private string DataPath(int slot) => Path.Combine(_root, $"slot-{slot}{PayloadExtension(_root)}");

    internal static async Task<bool> RecoverChildHoldAsync(MediaStagingBinding binding, string runId, string recordPath, CancellationToken ct)
    {
        if (binding.Slot < 0 || binding.Path != Path.Combine(binding.PoolRoot, $"slot-{binding.Slot}{PayloadExtension(binding.PoolRoot)}"))
            return false;
        // Recover only a versioned hold belonging to this exact run; legacy empty markers stay pinned.
        string markerPath = binding.Path + ".quarantine";
        if (!File.Exists(markerPath))
            return true;
        if (new FileInfo(markerPath).Length is <= 0 or > 4096)
            return false;
        using var marker = JsonDocument.Parse(BoundedRuntimeFile.Read(markerPath, 4096));
        var value = marker.RootElement;
        if (!MediaRunRecord.Unique(value) || value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3 ||
            value.GetProperty("Version").GetString() != "romd-media-hold/v1" || value.GetProperty("RunId").GetString() != runId ||
            value.GetProperty("RecordPath").GetString() != recordPath)
            return false;
        FileStream? guard = null;
        FileStream? owner = null;
        try
        {
            guard = new(Path.Combine(binding.PoolRoot, "accounting.lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            owner = new(Path.Combine(binding.PoolRoot, $"slot-{binding.Slot}.lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string accounting = Path.Combine(binding.PoolRoot, "reservations.json");
            if (new FileInfo(accounting).Length is <= 0 or > 32768)
                return false;
            var state = JsonSerializer.Deserialize<State>(BoundedRuntimeFile.Read(accounting, 32768));
            if (state is null || state.Version != 1 || !state.Reservations.TryGetValue(binding.Slot, out long charged) || charged != binding.ReservedBytes)
                return false;
            File.Delete(markerPath);
            return true;
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 ||
            OperatingSystem.IsLinux() && error.HResult == 11 || OperatingSystem.IsMacOS() && error.HResult == 35)
        { return false; }
        finally
        {
            if (owner is not null)
                await owner.DisposeAsync();
            if (guard is not null)
                await guard.DisposeAsync();
        }
    }
    private bool HasChildHold(int slot) => _fs.File.Exists(DataPath(slot) + ".quarantine");
    private Stream? TryLock(string name)
    {
        try
        { return _fs.File.Open(Path.Combine(_root, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        // .NET exposes native EWOULDBLOCK here on Unix (Linux 11, macOS 35),
        // while Windows uses sharing/lock violations wrapped in an HRESULT.
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 ||
            (OperatingSystem.IsLinux() && error.HResult == 11) ||
            (OperatingSystem.IsMacOS() && error.HResult == 35))
        { return null; }
    }

    private async Task<Stream> GuardAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        do
        {
            ct.ThrowIfCancellationRequested();
            var guard = TryLock("accounting.lease");
            if (guard is not null)
                return guard;
            await Task.Delay(25, ct);
        } while (Stopwatch.GetElapsedTime(started).TotalSeconds < _options.AdmissionTimeoutSeconds);
        throw new ResourceCapacityException("Timed out acquiring temporary staging accounting.");
    }

    private State Empty() => new()
    {
        MaximumFiles = _options.MaximumFiles,
        MaximumBytes = _options.MaximumBytes,
        MaximumFileBytes = _maximumFile
    };

    private State Read()
    {
        string path = Path.Combine(_root, "reservations.json");
        if (!_fs.File.Exists(path))
        {
            if (_fs.Directory.EnumerateFiles(_root, "slot-*" + PayloadExtension(_root)).Any())
                throw new InvalidDataException("Staging accounting is missing while payloads exist.");
            return Empty();
        }
        if (_fs.FileInfo.New(path).Length > 32768)
            throw new InvalidDataException("Staging accounting is oversized.");
        var state = JsonSerializer.Deserialize<State>(_fs.File.ReadAllText(path))
            ?? throw new InvalidDataException("Staging accounting is missing.");
        if (state.Version != 1 || state.MaximumFiles is < 1 or > 256 ||
            state.MaximumFileBytes is < 1 or > (16L << 40) ||
            state.MaximumBytes < state.MaximumFileBytes || state.MaximumBytes > (16L << 40) ||
            state.Reservations.Count > state.MaximumFiles ||
            state.Reservations.Any(x => x.Key < 0 || x.Key >= state.MaximumFiles || x.Value < 0 || x.Value > state.MaximumFileBytes) ||
            state.Reservations.Values.Sum() > state.MaximumBytes)
            throw new InvalidDataException("Staging accounting is invalid.");
        return state;
    }

    private void Write(State state)
    {
        string staging = Path.Combine(_root, "reservations.new");
        _fs.File.WriteAllText(staging, JsonSerializer.Serialize(state));
        _fs.File.Move(staging, Path.Combine(_root, "reservations.json"), overwrite: true);
    }

    private long AvailableFreeBytes()
    {
        string path = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        var drive = _fs.DriveInfo.GetDrives().Where(d => path.StartsWith(
                Path.EndsInDirectorySeparator(d.RootDirectory.FullName) ? d.RootDirectory.FullName
                    : d.RootDirectory.FullName + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .MaxBy(d => d.RootDirectory.FullName.Length)
            ?? throw new IOException("Cannot identify the staging volume.");
        return drive.AvailableFreeSpace;
    }

    private sealed class State
    {
        public int Version { get; set; } = 1;
        public int MaximumFiles { get; set; }
        public long MaximumBytes { get; set; }
        public long MaximumFileBytes { get; set; }
        public Dictionary<int, long> Reservations { get; set; } = new();
    }

    internal sealed class Lease(TempFilePool pool, int slot, Stream owner, long reserved) : IAsyncDisposable
    {
        private int _disposed;
        internal int Slot { get; } = slot;
        internal Stream Owner { get; } = owner;
        internal long Reserved { get; set; } = reserved;
        internal string Path => pool.DataPath(Slot);
        internal string PoolRoot => pool._root;
        internal Task EnsureCapacityAsync(long length, CancellationToken ct) => pool.GrowAsync(this, length, ct);
        internal Task<bool> ReduceToMeasuredAsync(long length, CancellationToken ct) => pool.ReduceAsync(this, length, ct);
        internal void Retain() => Quarantined.TryAdd(Path, this);
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
            ? pool.ReleaseAsync(this) : ValueTask.CompletedTask;
    }
}
