namespace Romd.Admin.Application.Ingestion.Jobs;

public sealed class JobContext
{
    private readonly Func<CancellationToken, Task> _checkpointDelegate;
    private readonly Func<CancellationToken, Task<bool>> _hasExecutionOwnershipDelegate;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> _ownedMutationDelegate;

    public JobContext(
        string? workspacePath,
        Func<CancellationToken, Task> checkpointDelegate,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? hasExecutionOwnershipDelegate = null,
        Func<Func<CancellationToken, Task>, CancellationToken, Task>? ownedMutationDelegate = null)
    {
        WorkspacePath = workspacePath;
        _checkpointDelegate = checkpointDelegate;
        _hasExecutionOwnershipDelegate = hasExecutionOwnershipDelegate ?? (_ => Task.FromResult(true));
        _ownedMutationDelegate = ownedMutationDelegate ?? ((mutation, ct) => mutation(ct));
        CancellationToken = cancellationToken;
    }

    public string? WorkspacePath { get; }

    public CancellationToken CancellationToken { get; }

    public Task CheckpointAsync(CancellationToken ct = default)
        => _checkpointDelegate(ct);

    public async Task EnsureExecutionOwnershipAsync(CancellationToken ct = default)
    {
        if (!await _hasExecutionOwnershipDelegate(ct))
        {
            throw new JobExecutionOwnershipLostException();
        }
    }

    public Task ExecuteOwnedMutationAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken ct = default) =>
        _ownedMutationDelegate(mutation, ct);
}
