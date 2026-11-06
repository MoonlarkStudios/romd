using Romd.Application.Common.Content;

namespace Romd.Infrastructure.Storage;

internal readonly record struct ReadAdmissionSnapshot(int Active, int Queued, int PeakQueued);

/// <summary>One bounded FIFO for every decoded reader; a returned lease transfers its slot to the oldest waiter.</summary>
internal sealed class ReadDecoderAdmission : IDisposable
{
    private readonly object _gate = new();
    private readonly int _maximumConcurrent;
    private readonly int _maximumQueued;
    private readonly LinkedList<Waiter> _waiting = [];
    private int _active;
    private int _peakQueued;
    private bool _disposed;

    internal ReadDecoderAdmission(int maximumConcurrent, int maximumQueued)
    {
        _maximumConcurrent = maximumConcurrent;
        _maximumQueued = maximumQueued;
    }

    internal ValueTask<Lease> EnterAsync(TimeSpan timeout, CancellationToken ct)
    {
        Waiter waiter;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (_active < _maximumConcurrent && _waiting.Count == 0)
            {
                _active++;
                return ValueTask.FromResult(new Lease(this));
            }
            if (timeout == TimeSpan.Zero || _waiting.Count >= _maximumQueued)
                throw CapacityUnavailable();
            waiter = new Waiter();
            waiter.Node = _waiting.AddLast(waiter);
            _peakQueued = Math.Max(_peakQueued, _waiting.Count);
        }
        return WaitAsync(waiter, timeout, ct);
    }

    private async ValueTask<Lease> WaitAsync(Waiter waiter, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await waiter.Completion.Task.WaitAsync(timeout, ct);
        }
        catch (Exception error)
        {
            Lease? granted = null;
            lock (_gate)
            {
                if (waiter.Node!.List is not null)
                {
                    _waiting.Remove(waiter.Node);
                    waiter.Completion.TrySetCanceled();
                }
                else if (waiter.Completion.Task.IsCompletedSuccessfully)
                {
                    // Grant can win just after WaitAsync observes cancellation/timeout.
                    // This caller never received the lease, so it must return that slot itself.
                    granted = waiter.Completion.Task.Result;
                }
                else if (waiter.Completion.Task.IsFaulted)
                {
                    _ = waiter.Completion.Task.Exception;
                }
            }
            granted?.Dispose();
            if (error is TimeoutException)
                throw CapacityUnavailable();
            throw;
        }
    }

    internal ReadAdmissionSnapshot Observe()
    {
        lock (_gate)
            return new(_active, _waiting.Count, _peakQueued);
    }

    private void Release()
    {
        lock (_gate)
        {
            _active--;
            if (_active < 0)
                throw new InvalidOperationException("Decoded reader capacity was returned twice.");
            if (!_disposed && _waiting.First is { } next)
            {
                _waiting.RemoveFirst();
                _active++;
                next.Value.Completion.SetResult(new Lease(this));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_waiting.First is { } waiter)
            {
                _waiting.RemoveFirst();
                waiter.Value.Completion.SetException(new ObjectDisposedException(nameof(RomRepresentationDecoder)));
            }
        }
    }

    private static ResourceCapacityException CapacityUnavailable() =>
        new("Media read capacity is occupied; retry later.");

    private sealed class Waiter
    {
        internal LinkedListNode<Waiter>? Node;
        internal TaskCompletionSource<Lease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class Lease : IDisposable
    {
        private ReadDecoderAdmission? _owner;

        internal Lease(ReadDecoderAdmission owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
