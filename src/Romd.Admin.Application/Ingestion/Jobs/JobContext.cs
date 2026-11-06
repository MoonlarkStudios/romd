namespace Romd.Admin.Application.Ingestion.Jobs;

public sealed class JobContext
{
    public const int MaxUploadDeferrals = 8;
    private readonly Action<string, bool>? _onDeferral;
    private readonly Action? _onUploadProgress;
    private bool _madeUploadProgress;
    private readonly Func<CancellationToken, Task> _checkpointDelegate;
    private readonly Func<CancellationToken, Task<bool>> _hasExecutionOwnershipDelegate;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> _ownedMutationDelegate;

    public JobContext(
        string? workspacePath,
        Func<CancellationToken, Task> checkpointDelegate,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? hasExecutionOwnershipDelegate = null,
        Func<Func<CancellationToken, Task>, CancellationToken, Task>? ownedMutationDelegate = null, Guid? jobId = null,
        Action<string, bool>? onDeferral = null, Action? onUploadProgress = null)
    {
        _onDeferral = onDeferral;
        _onUploadProgress = onUploadProgress;
        WorkspacePath = workspacePath;
        JobId = jobId;
        _checkpointDelegate = checkpointDelegate;
        _hasExecutionOwnershipDelegate = hasExecutionOwnershipDelegate ?? (_ => Task.FromResult(true));
        _ownedMutationDelegate = ownedMutationDelegate ?? ((mutation, ct) => mutation(ct));
        CancellationToken = cancellationToken;
    }

    /// <summary>The earliest instant at which this unfinished job may execute again.</summary>
    public DateTimeOffset? DeferredUntil { get; private set; }

    public void RequestDeferral(DateTimeOffset until, string reason = "Temporary resource or outcome failure.")
    {
        if (DeferredUntil is null)
            _onDeferral?.Invoke(reason, _madeUploadProgress);
        if (DeferredUntil is null || until > DeferredUntil)
            DeferredUntil = until;
    }

    /// <summary>Call only after new outcomes are durable, including recovery after a lost checkpoint.</summary>
    public void ReportUploadProgress() => ReportProgress();

    public void ReportProgress()
    {
        _madeUploadProgress = true;
        _onUploadProgress?.Invoke();
    }

    /// <summary>Refresh an unpublished retry after the final outcome flush has established progress.</summary>
    public void RefreshUploadRetry(DateTimeOffset now, int noProgressDeferrals)
    {
        // The pending deferral is already counted. Back off using the preceding stalled attempts.
        if (DeferredUntil is not null)
            DeferredUntil = now.Add(UploadRetryDelay(Math.Max(0, noProgressDeferrals - 1)));
    }

    /// <summary>Exponential backoff with jitter, from 15–30 seconds up to 15–30 minutes.</summary>
    public static TimeSpan UploadRetryDelay(int noProgressDeferrals)
    {
        int seconds = Math.Min(900, 15 << Math.Clamp(noProgressDeferrals, 0, 6));
        return TimeSpan.FromSeconds(Random.Shared.Next(seconds, seconds * 2 + 1));
    }

    public Guid? JobId { get; }

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
