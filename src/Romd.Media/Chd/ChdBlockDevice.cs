using Romd.Storage;

namespace Romd.Media.Chd;

/// <summary>Logical CHD bytes with shared hunk caching and exclusive native-handle leases.</summary>
/// <remarks>
/// Read path: native CRCs are checked when supplied by the CHD map; no universal
/// per-read or whole-delivery integrity guarantee is claimed. Native read-ahead is disabled.
/// Each serialized production reader owns at most one native decode: it joins surviving cancelled
/// demand or speculative work before distinct new work, and disposal joins its receipts before returning
/// decoder admission. Thus native active work cannot exceed admitted production CHD leases. Direct
/// callers may still issue concurrent demand reads, bounded by the shared native pool.
/// Production eligibility, containment, authorization and delivered hashes belong to the caller.
/// </remarks>
public sealed class ChdBlockDevice : IRandomAccessContent
{
    private readonly object _gate = new();
    private readonly ChdReadPool _pool;
    private readonly ChdReadPool.Artifact _artifact;
    private readonly TimeSpan _timeout;
    private int _reads;
    private bool _closing;
    private TaskCompletionSource? _drained;
    private Task? _disposal;
    private readonly List<(uint Index, Task Completion, ReadOperation? Read)> _owned = [];
    private Task? _speculation;
    private uint _speculationIndex;
    private long? _expectedOffset;

    private ChdBlockDevice(ChdReadPool pool, ChdReadPool.Artifact artifact, TimeSpan timeout)
    { _pool = pool; _artifact = artifact; _timeout = timeout; Length = checked((long)artifact.Facts.LogicalBytes); }

    // Completion receipts are bounded by outstanding origin work, rather than logical request length.
    internal int OwnedWorkCount { get { lock (_gate) return _owned.Count; } }

    public long Length { get; }
    public ContentReadIntegrity Integrity => ContentReadIntegrity.None;

    /// <summary>Takes ownership of the raw source, including on failed admission/open.</summary>
    /// <param name="source">An immutable raw snapshot with concurrent synchronous positional reads.</param>
    /// <param name="pool">The one shared pool for participating devices.</param>
    /// <param name="limits">Fixed-header admission limits, applied before native opening.</param>
    /// <param name="cacheIdentity">Caller-verified immutable CAS identity, normally artifact SHA-256.
    /// Supplying the same identity asserts byte equality; this method does not hash or authenticate it.
    /// Null isolates the source and its cached blocks. An active shared identity must have identical geometry.</param>
    /// <param name="admissionTimeout">Bounded wait for a native handle on open and a cache miss; default zero,
    /// at most five minutes. A cancelled waiter cannot interrupt a native call or cancel another waiter.</param>
    /// <param name="ct">Cancellation checked before admission and between logical hunks.</param>
    public static async ValueTask<ChdBlockDevice> OpenAsync(ISynchronousRawContent source, ChdReadPool pool,
        ChdHeaderPreflightLimits limits, string? cacheIdentity = null,
        TimeSpan? admissionTimeout = null, CancellationToken ct = default)
    {
        bool transferred = false;
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(pool);
            ArgumentNullException.ThrowIfNull(limits);
            if (cacheIdentity is not null)
                ArgumentException.ThrowIfNullOrWhiteSpace(cacheIdentity);
            TimeSpan timeout = admissionTimeout ?? TimeSpan.Zero;
            if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException(nameof(admissionTimeout));
            ct.ThrowIfCancellationRequested();
            byte[] header = new byte[124];
            int received = 0;
            while (received < header.Length)
            {
                ct.ThrowIfCancellationRequested();
                int count = source.ReadAt(received, header.AsSpan(received));
                if (count == 0)
                    break;
                if (count < 0 || count > header.Length - received)
                    throw new IOException("CHD source returned an invalid header byte count.");
                received += count;
            }
            ChdHeaderPreflightResult result = ChdHeaderPreflight.Inspect(header.AsSpan(0, received), source.Length, limits);
            if (result is ChdHeaderPreflightResult.Rejected rejected)
                throw new InvalidDataException($"ROMD CHD header rejected: {rejected.Cause}; slot={rejected.CodecSlot}; codec={rejected.Codec:X8}");
            var accepted = (ChdHeaderPreflightResult.Accepted)result;
            if (accepted.Facts.LogicalBytes > long.MaxValue || accepted.Facts.HunkBytes > int.MaxValue)
                throw new InvalidDataException("CHD geometry exceeds managed logical/buffer bounds.");
            transferred = true;
            ChdReadPool.Artifact artifact = await pool.OpenAsync(source, accepted.Facts, cacheIdentity, timeout, ct);
            return new ChdBlockDevice(pool, artifact, timeout);
        }
        catch { if (!transferred) source?.Dispose(); throw; }
    }

    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        bool adjacent;
        Task[] previous;
        var read = new ReadOperation(this);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (ct.IsCancellationRequested)
            {
                _expectedOffset = null;
                ct.ThrowIfCancellationRequested();
            }
            _pool.BeginRead(_artifact);
            _reads++;
            adjacent = _reads == 1 && _expectedOffset == offset && !destination.IsEmpty && offset < Length;
            if (!adjacent)
                _expectedOffset = null;
            _owned.RemoveAll(work => work.Completion.IsCompleted);
            uint first = offset < Length ? checked((uint)((ulong)offset / _artifact.Facts.HunkBytes)) : uint.MaxValue;
            previous = _reads == 1
                ? _owned.Where(work => work.Index != first).Select(work => work.Completion).ToArray()
                : [];
            if (_speculation is { IsCompleted: false } speculation && _speculationIndex != first)
                previous = [.. previous, speculation];
        }
        bool succeeded = false;
        int count = 0;
        try
        {
            // A serial caller may stop waiting promptly, but it retains ownership of the decode it started.
            // Before distinct work, join those receipts so one production lease never borrows another handle.
            foreach (Task completion in previous)
                await ObserveAsync(completion).WaitAsync(ct);
            if (offset >= Length || destination.IsEmpty)
                return 0;
            count = (int)Math.Min(destination.Length, Length - offset);
            int copied = 0;
            uint hunkBytes = _artifact.Facts.HunkBytes;
            while (copied < count)
            {
                ct.ThrowIfCancellationRequested();
                long position = offset + copied;
                uint index = checked((uint)((ulong)position / hunkBytes));
                int within = (int)((ulong)position % hunkBytes);
                Block block = await _pool.GetBlockAsync(_artifact, index, _timeout, ct, read.Started);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    int take = Math.Min(count - copied, block.Length - within);
                    if (take <= 0)
                        throw new InvalidDataException("A CHD decoded hunk is shorter than its logical range.");
                    block.Buffer.AsMemory(within, take).CopyTo(destination[copied..]);
                    copied += take;
                }
                finally { _pool.Cache.Release(block); }
            }
            succeeded = true;
            return copied;
        }
        finally
        {
            _pool.EndRead();
            lock (_gate)
            {
                read.Active = false;
                if (succeeded && _reads == 1 && !_closing && !ct.IsCancellationRequested)
                {
                    _expectedOffset = offset + count;
                    // Two adjacent nonempty ranges establish a sequence; one hunk beyond the last
                    // demanded hunk is the entire look-ahead window, never past logical EOF.
                    uint next = checked((uint)((ulong)(offset + count - 1) / _artifact.Facts.HunkBytes + 1));
                    if (adjacent && next < _artifact.Facts.HunkCount &&
                        (_speculation is null || _speculation.IsCompleted))
                    {
                        _speculationIndex = next;
                        _speculation = _pool.TryPrefetch(_artifact, next, (index, receipt) => RegisterDecode(index, receipt, null));
                    }
                }
                else
                    _expectedOffset = null;
                if (--_reads == 0 && _closing)
                    _drained?.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null)
                return new ValueTask(_disposal);
            _closing = true;
            Task drained;
            if (_reads == 0)
                drained = Task.CompletedTask;
            else
            {
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _drained.Task;
            }
            _disposal = DisposeCoreAsync(drained);
            return new ValueTask(_disposal);
        }
    }

    private Task RegisterDecode(uint index, Task completion, ReadOperation? read)
    {
        Task[] previous;
        lock (_gate)
        {
            _owned.RemoveAll(work => work.Completion.IsCompleted);
            // This callback is atomic with NEW-flight ownership, so a removed same-key flight is
            // no longer mistaken for a joinable decode. Existing-flight joiners never run it.
            // Preserve explicitly concurrent direct demand calls: they do not join each other's
            // still-active reads. Production leases serialize, and cancelled/finished origins drain.
            previous = _owned.Where(work => work.Read is null || !work.Read.Active || work.Read == read)
                .Select(work => work.Completion).ToArray();
            if (_speculation is { IsCompleted: false } speculation)
                previous = [.. previous, speculation];
            _owned.Add((index, completion, read));
        }
        return previous.Length == 0 ? Task.CompletedTask : DrainPredecessorsAsync(previous);
    }

    private async Task DrainPredecessorsAsync(Task[] previous)
    {
        foreach (Task completion in previous)
            await ObserveAsync(completion);
        lock (_gate)
            _owned.RemoveAll(work => work.Completion.IsCompleted);
    }

    private sealed class ReadOperation(ChdBlockDevice owner)
    {
        internal bool Active = true; // Guarded by the device gate.
        internal Task Started(uint index, Task completion) => owner.RegisterDecode(index, completion, this);
    }

    private static async Task ObserveAsync(Task completion)
    {
        try
        { await completion; }
        catch { /* Demand already received its error; speculation must remain an uncached retry. */ }
    }

    private async Task DisposeCoreAsync(Task drained)
    {
        await drained;
        Task[] owned;
        lock (_gate)
        {
            owned = _owned.Select(work => work.Completion).ToArray();
            if (_speculation is not null)
                owned = [.. owned, _speculation];
        }
        foreach (Task completion in owned)
            await ObserveAsync(completion);
        _pool.ReleaseArtifact(_artifact);
    }
}
