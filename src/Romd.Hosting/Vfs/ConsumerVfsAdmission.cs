using System.Diagnostics;

namespace Romd.Hosting.Vfs;

/// <summary>Bounded admission with round-robin service across accounts and their connections.</summary>
public sealed class ConsumerVfsAdmission
{
    private readonly object _gate = new();
    private readonly ConsumerVfsOptions _options;
    private readonly Dictionary<Guid, Account> _accounts = [];
    private readonly List<Waiter> _waiting = [];
    private int _active;
    private long _turn;
    private readonly KeyValuePair<string, object?> _lane;

    public ConsumerVfsAdmission(ConsumerVfsOptions options, string lane = "content")
    {
        _options = options;
        _lane = new("lane", lane);
    }

    public int QueuedRequestCount { get { lock (_gate) return _waiting.Count; } }

    public async Task<IDisposable?> EnterAsync(Guid userId, Guid? connectionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            // Give existing eligible waiters priority, but do not leave spare capacity idle
            // merely because another connection has filled the queue at its own limit.
            Drain();
            var account = GetAccount(userId);
            var connection = GetConnection(account, connectionId);
            if (CanEnter(account, connection))
                return Admit(userId, connectionId, account, connection);
            if (_waiting.Count >= _options.MaximumQueuedRequests ||
                account.Queued >= _options.MaximumQueuedRequestsPerUser ||
                connection.Queued >= _options.MaximumQueuedRequestsPerConnection)
            {
                Cleanup(userId, connectionId, account, connection);
                ConsumerVfsDiagnostics.RejectedRequests.Add(1, _lane, new KeyValuePair<string, object?>("reason", "queue_full"));
                return null;
            }
            waiter = new(userId, connectionId, account, connection);
            _waiting.Add(waiter);
            account.Queued++;
            connection.Queued++;
            ConsumerVfsDiagnostics.QueuedRequests.Add(1, _lane);
            Drain();
        }

        long started = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(_options.MaximumQueueWaitMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await waiter.Completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (_waiting.Remove(waiter))
                {
                    RemoveQueued(waiter);
                    Cleanup(userId, connectionId, waiter.Account, waiter.Connection);
                }
                else if (waiter.Completion.Task.IsCompletedSuccessfully)
                {
                    // A grant raced cancellation. Return its slot rather than leaking it.
                    waiter.Completion.Task.Result.Dispose();
                }
                Drain();
            }
            ct.ThrowIfCancellationRequested();
            ConsumerVfsDiagnostics.RejectedRequests.Add(1, _lane, new KeyValuePair<string, object?>("reason", "timeout"));
            return null;
        }
        finally
        {
            ConsumerVfsDiagnostics.QueueWait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, _lane);
        }
    }

    // Nonwaiting acquisition is also useful for resource probes; it cannot jump queued work.
    public bool TryEnter(Guid userId)
    {
        lock (_gate)
        {
            var account = GetAccount(userId);
            var connection = GetConnection(account, null);
            if (_waiting.Count != 0 || !CanEnter(account, connection))
            {
                Cleanup(userId, null, account, connection);
                return false;
            }
            _ = Admit(userId, null, account, connection);
            return true;
        }
    }

    public void Exit(Guid userId) => Exit(userId, null);

    private void Exit(Guid userId, Guid? connectionId)
    {
        lock (_gate)
        {
            var account = _accounts[userId];
            var connection = account.Connections[connectionId ?? Guid.Empty];
            if (connection.Active == 0)
                throw new InvalidOperationException("No active VFS request.");
            connection.Active--;
            account.Active--;
            _active--;
            Cleanup(userId, connectionId, account, connection);
            Drain();
        }
    }

    private void Drain()
    {
        while (_active < _options.MaximumConcurrentRequests)
        {
            // Queue bounds keep this scan bounded; oldest waiter wins within each connection.
            var next = _waiting.Where(w => CanEnter(w.Account, w.Connection))
                .OrderBy(w => w.Account.LastTurn).ThenBy(w => w.Connection.LastTurn).FirstOrDefault();
            if (next is null)
                return;
            _waiting.Remove(next);
            RemoveQueued(next);
            next.Completion.SetResult(Admit(next.UserId, next.ConnectionId, next.Account, next.Connection));
        }
    }

    private void RemoveQueued(Waiter waiter)
    {
        waiter.Account.Queued--;
        waiter.Connection.Queued--;
        ConsumerVfsDiagnostics.QueuedRequests.Add(-1, _lane);
    }

    private bool CanEnter(Account account, Connection connection) =>
        _active < _options.MaximumConcurrentRequests && account.Active < _options.MaximumConcurrentRequestsPerUser &&
        connection.Active < _options.MaximumConcurrentRequestsPerConnection;

    private IDisposable Admit(Guid userId, Guid? connectionId, Account account, Connection connection)
    {
        _active++;
        account.Active++;
        connection.Active++;
        account.LastTurn = connection.LastTurn = ++_turn;
        return new Lease(this, userId, connectionId);
    }

    private Account GetAccount(Guid userId)
    {
        if (!_accounts.TryGetValue(userId, out var account))
            _accounts[userId] = account = new();
        return account;
    }

    private static Connection GetConnection(Account account, Guid? id)
    {
        if (!account.Connections.TryGetValue(id ?? Guid.Empty, out var connection))
            account.Connections[id ?? Guid.Empty] = connection = new();
        return connection;
    }

    private void Cleanup(Guid userId, Guid? id, Account account, Connection connection)
    {
        if (connection.Active == 0 && connection.Queued == 0)
            account.Connections.Remove(id ?? Guid.Empty);
        if (account.Active == 0 && account.Queued == 0)
            _accounts.Remove(userId);
    }

    private sealed class Account
    {
        public int Active, Queued;
        public long LastTurn;
        public Dictionary<Guid, Connection> Connections { get; } = [];
    }

    private sealed class Connection
    {
        public int Active, Queued;
        public long LastTurn;
    }

    private sealed record Waiter(Guid UserId, Guid? ConnectionId, Account Account, Connection Connection)
    {
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease : IDisposable
    {
        private ConsumerVfsAdmission? _owner;
        private readonly Guid _userId;
        private readonly Guid? _connectionId;

        public Lease(ConsumerVfsAdmission owner, Guid userId, Guid? connectionId)
        {
            _owner = owner;
            _userId = userId;
            _connectionId = connectionId;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit(_userId, _connectionId);
    }
}
