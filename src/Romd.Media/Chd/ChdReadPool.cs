using System.Runtime.ExceptionServices;
using Moonlark.Libchdr;
using Romd.Storage;

namespace Romd.Media.Chd;

/// <summary>A shared CHD handle pool that owns the hunk decodes the shared block cache single-flights.</summary>
/// <remarks>
/// Supply one pool to all participating devices. Active/opening handles are bounded by
/// maxConcurrent, including globally retained idle handles. Idle handles from another artifact
/// are evicted before a new native handle would exceed this bound. These bounds do not qualify
/// native memory use or untrusted input. Cleanup faults are surfaced by pool disposal.
/// The caller owns the shared MediaBlockCache; the pool owns its raw sources and handles, and
/// keeps an artifact's source until its owned decodes drain.
/// </remarks>
public sealed class ChdReadPool : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Artifact> _artifacts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _maxIdle;
    private readonly int _maxConcurrent;
    private readonly List<Exception> _cleanupErrors = [];
    private int _live, _active, _idle, _peakLive, _peakActive, _opening, _peakOpening, _operations;
    private long _opened, _closed, _speculativeStarted, _speculativeFailed;
    private int _speculations, _peakSpeculations, _demandAdmissions;
    private bool _closing;
    private TaskCompletionSource? _drained;
    private Task? _disposal;

    internal MediaBlockCache Cache { get; }

    public ChdReadPool(MediaBlockCache cache, int maxConcurrent, int maxIdle)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (maxConcurrent is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        if (maxIdle < 0 || maxIdle > maxConcurrent)
            throw new ArgumentOutOfRangeException(nameof(maxIdle));
        Cache = cache;
        _maxIdle = maxIdle;
        _maxConcurrent = maxConcurrent;
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    internal ChdReadPoolSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(_live, _active, _idle, _peakLive, _peakActive, _opened, _closed,
                    _artifacts.Values.Sum(state => state.Decodes),
                    _artifacts.Keys.Sum(Cache.FlightWaiters),
                    _artifacts.Count, _speculations, _peakSpeculations, _speculativeStarted, _speculativeFailed,
                    _opening, _peakOpening);
        }
    }

    internal async ValueTask<Artifact> OpenAsync(ISynchronousRawContent incoming, ChdHeaderFacts facts,
        string? identity, TimeSpan timeout, CancellationToken ct)
    {
        Artifact? state = null;
        bool ownsIncoming = true;
        bool operation = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                string owner = identity is null ? $"chd:private:{Guid.NewGuid():N}" : $"chd:artifact:{identity}";
                if (_artifacts.TryGetValue(owner, out state))
                {
                    if (state.Facts != facts)
                        throw new InvalidDataException($"CHD artifact '{identity}' has inconsistent geometry.");
                    state.Devices++;
                }
                else
                {
                    state = new Artifact(owner, identity is null, incoming, facts);
                    _artifacts.Add(owner, state);
                    ownsIncoming = false;
                }
                _operations++;
                operation = true;
            }
            if (ownsIncoming)
            { ownsIncoming = false; incoming.Dispose(); }
            Handle handle = await BorrowAsync(state, timeout, ct);
            ReturnHandle(handle, healthy: true);
            return state;
        }
        catch
        {
            if (operation && state is not null)
                ReleaseArtifact(state);
            if (ownsIncoming)
                incoming.Dispose();
            throw;
        }
        finally { if (operation) EndOperation(); }
    }

    internal void BeginRead(Artifact state)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing || state.Retired, this);
            _operations++;
        }
    }

    internal void EndRead() => EndOperation();

    internal void ReleaseArtifact(Artifact state)
    {
        ISynchronousRawContent? released;
        lock (_gate)
        {
            if (state.Retired)
                return;
            state.Devices--;
            released = RetireLocked(state);
        }
        released?.Dispose();
    }

    internal ValueTask<Block> GetBlockAsync(Artifact state, uint index, TimeSpan timeout, CancellationToken ct,
        Func<uint, Task, Task> started)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            ObjectDisposedException.ThrowIf(_closing || state.Retired, this);
        return Cache.GetOrAddAsync(new BlockKey(state.Owner, 0, index),
            (Pool: this, State: state, Index: index, Timeout: timeout, Started: started),
            static work => work.Pool.StartDecode(work.State, work.Index, work.Timeout), ct,
            static (work, receipt) => work.Pool.TrackOriginReceipt(work.Index, receipt, work.Started));
    }

    // Speculation never queues for a handle: it reserves one immediately and leaves at least one global
    // permit for demand. A pending demand admission suppresses further speculation until it is served.
    internal Task? TryPrefetch(Artifact state, uint index, Func<uint, Task, Task> started)
    {
        lock (_gate)
        {
            if (_closing || state.Retired || _maxConcurrent == 1 || _speculations >= _maxConcurrent - 1 ||
                _demandAdmissions != 0 || !_slots.Wait(0))
                return null;
            _speculations++;
            _operations++; // Own the speculative waiter/buffer cleanup, including a deferred factory.
            _peakSpeculations = Math.Max(_peakSpeculations, _speculations);
        }
        bool flightStarted = false, nativeStarted = false;
        try
        {
            ValueTask<Block> pending = Cache.GetOrAddAsync(new BlockKey(state.Owner, 0, index), 0, _ =>
            {
                nativeStarted = true;
                lock (_gate)
                    _speculativeStarted++;
                return StartDecode(state, index, TimeSpan.Zero, reserved: true);
            }, CancellationToken.None, (_, receipt) =>
            {
                flightStarted = true;
                return TrackOriginReceipt(index, receipt, started);
            });
            if (!flightStarted)
                _slots.Release(); // Retained hit or joining another owner's flight.
            return FinishPrefetchAsync(pending, () => flightStarted && !nativeStarted);
        }
        catch
        {
            if (!nativeStarted)
                _slots.Release();
            lock (_gate)
                _speculations--;
            EndOperation();
            throw;
        }
    }

    private async Task FinishPrefetchAsync(ValueTask<Block> pending, Func<bool> unusedReservation)
    {
        try
        { Cache.Release(await pending); }
        catch { lock (_gate) _speculativeFailed++; } // Demand still retries and surfaces an uncached failure.
        finally
        {
            if (unusedReservation())
                _slots.Release();
            lock (_gate)
                _speculations--;
            EndOperation();
        }
    }

    private Task TrackOriginReceipt(uint index, Task receipt, Func<uint, Task, Task> started)
    {
        lock (_gate)
            _operations++;
        _ = FinishOriginReceiptAsync(receipt);
        return started(index, receipt);
    }

    private async Task FinishOriginReceiptAsync(Task receipt)
    {
        try
        { await receipt; }
        finally { EndOperation(); }
    }

    private Task<Block> StartDecode(Artifact state, uint index, TimeSpan timeout,
        bool reserved = false)
    {
        lock (_gate)
        {
            if (_closing || state.Retired)
            {
                if (reserved)
                    _slots.Release();
                throw new ObjectDisposedException(nameof(ChdReadPool));
            }
            state.Decodes++;
            _operations++; // Owned work survives cancellation of an individual waiter.
        }
        return Task.Run(() => DecodeAsync(state, index, timeout, reserved));
    }

    private async Task<Block> DecodeAsync(Artifact state, uint index, TimeSpan timeout, bool reserved)
    {
        Handle? handle = null;
        byte[]? buffer = null;
        Block? decoded = null;
        Exception? failure = null;
        bool healthy = false;
        try
        {
            handle = await BorrowAsync(state, timeout, _shutdown.Token, reserved);
            _shutdown.Token.ThrowIfCancellationRequested();
            buffer = Cache.Pool.Rent(checked((int)state.Facts.HunkBytes));
            // ReadHunk performs native CRC verification when a map entry supplies a CRC.
            handle.File.ReadHunk(index, buffer.AsSpan(0, checked((int)state.Facts.HunkBytes)));
            healthy = true;
            int length = (int)Math.Min(state.Facts.HunkBytes,
                state.Facts.LogicalBytes - (ulong)index * state.Facts.HunkBytes);
            decoded = new Block(buffer, length);
            buffer = null;
        }
        catch (ChdException error)
        { failure = new InvalidDataException($"CHD artifact '{state.Owner}', hunk {index} failed integrity/decode.", error); }
        catch (Exception error) { failure = error; }
        finally
        {
            if (buffer is not null)
                Cache.Pool.Return(buffer);
            if (handle is not null)
            {
                try
                { ReturnHandle(handle, healthy); }
                catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            }
        }
        if (failure is not null && decoded is not null)
        { Cache.Release(decoded); decoded = null; }

        ISynchronousRawContent? released;
        lock (_gate)
        {
            state.Decodes--;
            released = RetireLocked(state);
        }
        try
        { released?.Dispose(); }
        catch (Exception error) { lock (_gate) _cleanupErrors.Add(error); }
        finally { EndOperation(); }
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        return decoded!; // The cache admits it, and retains nothing for a private owner retirement already evicted.
    }

    private async ValueTask<Handle> BorrowAsync(Artifact state, TimeSpan timeout, CancellationToken ct, bool slotOwned = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        if (!slotOwned)
        {
            lock (_gate)
                _demandAdmissions++;
            try
            {
                if (!await _slots.WaitAsync(timeout, linked.Token))
                    throw new TimeoutException($"CHD read admission timed out for '{state.Owner}'.");
            }
            finally { lock (_gate) _demandAdmissions--; }
        }
        ChdFile? file = null;
        bool reserved = false;
        bool opening = false;
        bool counted = false;
        bool nativeOpening = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closing || state.Retired, this);
                _active++;
                state.Active++;
                _peakActive = Math.Max(_peakActive, _active);
                reserved = true;
                if (state.Idle.TryPop(out Handle? idle))
                {
                    _idle--;
                    return idle;
                }
                if (_live == _maxConcurrent)
                {
                    foreach (Artifact other in _artifacts.Values)
                        if (other.Idle.TryPop(out Handle? evicted))
                        {
                            _idle--;
                            CloseHandleLocked(evicted);
                            break;
                        }
                }
                if (_live >= _maxConcurrent)
                    throw new InvalidOperationException("CHD total handle capacity accounting is inconsistent.");
                _live++; // Includes this opening reservation until success or failure.
                opening = true;
                nativeOpening = true;
                _opening++;
                _peakOpening = Math.Max(_peakOpening, _opening);
                _peakLive = Math.Max(_peakLive, _live);
            }
            file = ChdFile.Open(new ChdRawContentSource(state.Source), leaveOpen: true,
                new ChdOpenOptions { ReadAheadBytes = 0 });
            lock (_gate)
            { _opened++; counted = true; _opening--; nativeOpening = false; }
            ValidateGeometry(file, state);
            return new Handle(state, file);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (nativeOpening)
                    _opening--;
                if (file is not null)
                {
                    try
                    { file.Dispose(); }
                    catch (Exception cleanup) { _cleanupErrors.Add(cleanup); }
                    finally { if (counted) _closed++; }
                }
                if (reserved)
                {
                    if (opening)
                        _live--;
                    _active--;
                    state.Active--;
                }
            }
            _slots.Release();
            if (error is ChdException native)
                throw new InvalidDataException($"CHD artifact '{state.Owner}' could not open.", native);
            throw;
        }
    }

    private static void ValidateGeometry(ChdFile file, Artifact state)
    {
        ChdHeader header = file.Header;
        ChdHeaderFacts facts = state.Facts;
        if (header.Version != 5 || header.HasParent || header.LogicalBytes != facts.LogicalBytes ||
            header.HunkBytes != facts.HunkBytes || header.UnitBytes != facts.UnitBytes ||
            header.HunkCount != facts.HunkCount || (uint)header.GetCodec(0) != facts.Codec0 ||
            (uint)header.GetCodec(1) != facts.Codec1 || (uint)header.GetCodec(2) != facts.Codec2 ||
            (uint)header.GetCodec(3) != facts.Codec3 || file.ReadAheadBytes != 0)
            throw new InvalidDataException($"CHD artifact '{state.Owner}' changed geometry or read-ahead after admission.");
    }

    private void ReturnHandle(Handle handle, bool healthy)
    {
        try
        {
            lock (_gate)
            {
                _active--;
                handle.State.Active--;
                if (healthy && !_closing && handle.State.Devices != 0 && _idle < _maxIdle)
                {
                    handle.State.Idle.Push(handle);
                    _idle++;
                }
                else
                    CloseHandleLocked(handle);
            }
        }
        finally { _slots.Release(); }
    }

    private void CloseHandleLocked(Handle handle)
    {
        // leaveOpen=true makes this native close independent of raw-source disposal.
        try
        { handle.File.Dispose(); }
        catch (Exception error) { _cleanupErrors.Add(error); }
        finally { _live--; _closed++; }
    }

    private ISynchronousRawContent? RetireLocked(Artifact state)
    {
        if (state.Retired || state.Devices != 0 || state.Active != 0 || state.Decodes != 0)
            return null;
        state.Retired = true;
        while (state.Idle.TryPop(out Handle? handle))
        { _idle--; CloseHandleLocked(handle); }
        _artifacts.Remove(state.Owner);
        if (state.Private)
            Cache.Evict(state.Owner);
        return state.Source;
    }

    private void EndOperation()
    {
        lock (_gate)
            if (--_operations == 0 && _closing)
                _drained?.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task drained;
        lock (_gate)
        {
            if (_disposal is not null)
                return new ValueTask(_disposal);
            _closing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
            if (_operations == 0)
                drained = Task.CompletedTask;
            else
            {
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _drained.Task;
            }
        }
        _shutdown.Cancel();
        _ = DisposeCoreAsync(drained, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(Task drained, TaskCompletionSource completion)
    {
        try
        {
            await drained;
            List<ISynchronousRawContent> sources = [];
            lock (_gate)
            {
                foreach (Artifact state in _artifacts.Values)
                {
                    state.Retired = true;
                    while (state.Idle.TryPop(out Handle? handle))
                    { _idle--; CloseHandleLocked(handle); }
                    if (state.Private)
                        Cache.Evict(state.Owner);
                    sources.Add(state.Source);
                }
                _artifacts.Clear();
            }
            List<Exception> errors;
            lock (_gate)
                errors = [.. _cleanupErrors];
            foreach (ISynchronousRawContent source in sources)
                try
                { source.Dispose(); }
                catch (Exception error) { errors.Add(error); }
            _slots.Dispose();
            _shutdown.Dispose();
            if (errors.Count != 0)
                throw new AggregateException(errors);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal sealed class Artifact(string owner, bool privateOwner, ISynchronousRawContent source, ChdHeaderFacts facts)
    {
        internal string Owner { get; } = owner;
        internal bool Private { get; } = privateOwner;
        internal ISynchronousRawContent Source { get; } = source;
        internal ChdHeaderFacts Facts { get; } = facts;
        internal int Devices = 1, Active, Decodes;
        internal bool Retired;
        private readonly Stack<Handle> _idle = new();
        internal Stack<Handle> Idle => _idle;
    }

    internal sealed record Handle(Artifact State, ChdFile File);
}

internal readonly record struct ChdReadPoolSnapshot(int LiveHandles, int ActiveHandles, int IdleHandles,
    int PeakLiveHandles, int PeakActiveHandles, long OpenedHandles, long ClosedHandles,
    int Flights, int FlightWaiters, int Artifacts, int Speculations, int PeakSpeculations,
    long SpeculativeStarted, long SpeculativeFailed, int OpeningHandles, int PeakOpeningHandles);
