using System.Buffers;
using System.Runtime.ExceptionServices;

namespace Romd.Media;

/// <summary>
/// Byte-budgeted cache of decoded media blocks, plus the bounded buffer pool every reader rents
/// from. One instance serves a whole host. Blocks are reference counted, so an evicted buffer
/// returns to the pool only after concurrent readers have finished copying from it. Concurrent
/// misses for one key share a single decode.
/// </summary>
public sealed class MediaBlockCache
{
    /// <summary>Largest buffer the shared pool retains.</summary>
    public const int MaxPooledBufferBytes = 32 << 20;

    private readonly object _gate = new();
    private readonly Dictionary<BlockKey, Block> _blocks = [];
    private readonly Dictionary<BlockKey, Flight> _flights = [];
    private readonly LinkedList<Block> _recent = new();
    private long _usedBytes, _retainedHits, _flightJoins, _newFactories;

    public MediaBlockCache(long budgetBytes)
        : this(budgetBytes, ArrayPool<byte>.Create(MaxPooledBufferBytes, maxArraysPerBucket: 16)) { }

    internal MediaBlockCache(long budgetBytes, ArrayPool<byte> buffers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentNullException.ThrowIfNull(buffers);
        BudgetBytes = budgetBytes;
        Pool = new ObservedBufferPool(buffers);
    }

    public long BudgetBytes { get; }

    public long UsedBytes
    {
        get { lock (_gate) return _usedBytes; }
    }

    internal ArrayPool<byte> Pool { get; }

    internal MediaBlockCacheSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var buffers = ((ObservedBufferPool)Pool).Snapshot;
                return new(_retainedHits, _flightJoins, _newFactories, _flights.Count, _usedBytes,
                    buffers.Live, buffers.Peak);
            }
        }
    }

    /// <summary>
    /// Returns an acquired block for <paramref name="key"/>, sharing one <paramref name="factory"/> call among
    /// concurrent misses. The factory runs inline on the caller that starts the decode, with no cache lock held,
    /// until its first incomplete await, so a synchronous factory does all of its work on that caller; callers that
    /// join wait asynchronously. The factory never sees a waiter's token: it runs under its owner's lifetime, and
    /// a cancelled waiter stops waiting without cancelling the shared decode. The factory returns a fresh block
    /// holding one reference, which then takes the normal admission path. A failure reaches every waiter of that
    /// key, including readers through other images of the same identity, and is not cached: every outcome leaves
    /// the in-flight map, so a later call decodes again. The optional origin callback receives a cleanup
    /// receipt before its factory starts; joining callers do not acquire unrelated work ownership. This receipt
    /// settles after admission and unclaimed-buffer release, even on factory or admission failure,
    /// before publishing the result to waiters. The callback returns a barrier joined before the new
    /// factory starts; it may drain the origin's previous work without affecting existing-flight joins.
    /// </summary>
    internal ValueTask<Block> GetOrAddAsync<TState>(BlockKey key, TState state, Func<TState, Task<Block>> factory,
        CancellationToken ct, Func<TState, Task, Task>? started = null)
    {
        ct.ThrowIfCancellationRequested();
        Flight? flight;
        bool start = false;
        lock (_gate)
        {
            if (_blocks.TryGetValue(key, out var cached))
            {
                _retainedHits++;
                Interlocked.Increment(ref cached.References);
                _recent.Remove(cached.Node!);
                _recent.AddFirst(cached.Node!);
                return new ValueTask<Block>(cached);
            }
            if (!_flights.TryGetValue(key, out flight))
            {
                flight = new Flight(key);
                if (started is not null)
                    flight.Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _flights.Add(key, flight);
                start = true;
                _newFactories++;
            }
            else
                _flightJoins++;
            flight.Waiters++;
        }
        if (start)
            _ = RunAsync(flight, state, factory, started);
        return WaitAsync(flight, ct);
    }

    private async Task RunAsync<TState>(Flight flight, TState state, Func<TState, Task<Block>> factory,
        Func<TState, Task, Task>? started)
    {
        Block? fresh = null;
        Exception? failure = null;
        try
        {
            // The factory origin may join its own predecessor cleanup without delaying healthy
            // callers that join an existing flight. No cache lock is held while the barrier waits.
            if (started is not null)
                await started(state, flight.Receipt!.Task);
            fresh = await factory(state) ?? throw new InvalidOperationException("The block factory completed without a block.");
        }
        catch (Exception error) { failure = error; }
        try
        { Complete(flight, fresh, failure); }
        finally { flight.Receipt?.TrySetResult(); } // Includes admission and unclaimed-buffer cleanup.

    }

    /// <summary>Completes every waiter, faulting them all if admission throws, so no waiter can hang.</summary>
    private void Complete(Flight flight, Block? fresh, Exception? failure)
    {
        Block? admitted = null;
        if (fresh is not null)
        {
            try
            { admitted = Admit(flight, fresh); }
            catch (Exception error) { failure = error; }
        }
        bool unclaimed;
        lock (_gate)
        {
            _flights.Remove(flight.Key);
            unclaimed = flight.Waiters == 0;
            // The flight's reference goes to one waiter; every other registered waiter gets its own.
            if (admitted is not null && !unclaimed)
                Interlocked.Add(ref admitted.References, flight.Waiters - 1);
            flight.Result = admitted;
            flight.Completed = true;
        }
        if (admitted is null)
        {
            flight.Receipt?.TrySetResult();
            flight.Completion.SetException(failure!);
            _ = flight.Completion.Task.Exception; // Observe failures even when every waiter cancelled.
            return;
        }
        if (unclaimed)
            Release(admitted);
        // A successful waiter must not resume before factory cleanup is settled. This ordering
        // also prevents a multi-hunk reader retaining the predecessor's pending receipt.
        flight.Receipt?.TrySetResult();
        flight.Completion.SetResult(admitted);
    }

    /// <summary>Returns the admitted block holding the flight's reference; on failure the flight holds none.</summary>
    private Block Admit(Flight flight, Block fresh)
    {
        List<Block>? released = null;
        Block admitted;
        try
        {
            lock (_gate)
                admitted = AddOrGetLocked(flight.Key, fresh, flight.Retain, ref released);
        }
        catch
        {
            Release(fresh);
            throw;
        }
        try
        { ReleaseAll(released); }
        catch
        {
            Release(admitted);
            throw;
        }
        return admitted;
    }

    private async ValueTask<Block> WaitAsync(Flight flight, CancellationToken ct)
    {
        bool returned = false;
        try
        {
            Block block = await flight.Completion.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            returned = true;
            return block;
        }
        finally
        {
            if (!returned)
                Leave(flight);
        }
    }

    private void Leave(Flight flight)
    {
        Block? reserved = null;
        lock (_gate)
        {
            if (flight.Completed)
                reserved = flight.Result;
            else
                flight.Waiters--;
        }
        if (reserved is not null)
            Release(reserved);
    }

    private Block AddOrGetLocked(in BlockKey key, Block fresh, bool retain, ref List<Block>? released)
    {
        if (_blocks.TryGetValue(key, out var existing))
        {
            Interlocked.Increment(ref existing.References);
            (released ??= []).Add(fresh);
            return existing;
        }
        if (!retain || fresh.Buffer.Length > BudgetBytes)
            return fresh; // Not retained; the caller's reference is the only one.
        Interlocked.Increment(ref fresh.References); // The cache's own reference.
        fresh.Key = key;
        fresh.Node = _recent.AddFirst(fresh);
        _blocks[key] = fresh;
        _usedBytes += fresh.Buffer.Length;
        while (_usedBytes > BudgetBytes && _recent.Last is { } oldest && oldest.Value != fresh)
            (released ??= []).Add(Unlink(oldest.Value));
        return fresh;
    }

    internal void Release(Block block)
    {
        if (Interlocked.Decrement(ref block.References) == 0)
            Pool.Return(block.Buffer);
    }

    /// <summary>Releases every block, even after one release throws, then rethrows the first failure.</summary>
    private void ReleaseAll(List<Block>? released)
    {
        if (released is null)
            return;
        Exception? failure = null;
        foreach (var block in released)
        {
            try
            { Release(block); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary>Drops every retained block owned by <paramref name="owner"/> and retains none it is still decoding.</summary>
    internal void Evict(string owner)
    {
        List<Block> released = [];
        lock (_gate)
        {
            for (var node = _recent.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Key.Owner == owner)
                    released.Add(Unlink(node.Value));
                node = next;
            }
            foreach (var flight in _flights.Values)
                if (flight.Key.Owner == owner)
                    flight.Retain = false;
        }
        foreach (var block in released)
            Release(block);
    }

    /// <summary>Callers currently waiting on decodes of blocks owned by <paramref name="owner"/>.</summary>
    internal int FlightWaiters(string owner)
    {
        lock (_gate)
            return _flights.Values.Where(flight => flight.Key.Owner == owner).Sum(flight => flight.Waiters);
    }

    private Block Unlink(Block block)
    {
        _recent.Remove(block.Node!);
        _blocks.Remove(block.Key);
        _usedBytes -= block.Buffer.Length;
        block.Node = null;
        return block;
    }

    // Counts actual ArrayPool bucket lengths, including decoded blocks still held after eviction.
    private sealed class ObservedBufferPool(ArrayPool<byte> inner) : ArrayPool<byte>
    {
        private readonly object _gate = new();
        private readonly HashSet<byte[]> _rented = new(ReferenceEqualityComparer.Instance);
        private long _live, _peak;
        internal (long Live, long Peak) Snapshot { get { lock (_gate) return (_live, _peak); } }
        public override byte[] Rent(int minimumLength)
        {
            lock (_gate)
            {
                byte[] buffer = inner.Rent(minimumLength);
                _rented.Add(buffer);
                _live += buffer.Length;
                _peak = Math.Max(_peak, _live);
                return buffer;
            }
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_gate)
            {
                // Invalid returns must retain the original pool's exception behavior.
                inner.Return(array, clearArray);
                if (_rented.Remove(array))
                    _live -= array.Length;
            }
        }
    }

    /// <summary>One shared decode. Mutable state is guarded by the cache lock.</summary>
    private sealed class Flight(BlockKey key)
    {
        public BlockKey Key { get; } = key;
        public TaskCompletionSource<Block> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Receipt;
        public int Waiters;
        public bool Completed;
        public bool Retain = true;
        public Block? Result;
    }
}

internal readonly record struct BlockKey(string Owner, int Area, long Index);

internal sealed class Block(byte[] buffer, int length)
{
    public byte[] Buffer { get; } = buffer;
    public int Length { get; } = length;
    public int References = 1;
    public BlockKey Key;
    public LinkedListNode<Block>? Node;
}

internal readonly record struct MediaBlockCacheSnapshot(long RetainedHits, long FlightJoins, long NewFactories,
    int Flights, long RetainedBytes, long LiveRentedBytes, long PeakRentedBytes);
