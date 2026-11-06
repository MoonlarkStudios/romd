namespace Romd.Admin.Application.Ingestion.Preparation;

/// <summary>Shares the cross-process exclusion used by job execution and orphan cleanup.</summary>
public interface IJobWorkspaceExclusion
{
    Task<IJobWorkspaceLease> AcquireAsync(Guid jobId, CancellationToken ct = default);
}

/// <summary>
///     Caller-owned, single-flight workspace exclusion. Keep it alive through preparation,
///     acceptance and transaction disposal. Releasing it never deletes prepared input.
/// </summary>
public interface IJobWorkspaceLease : IAsyncDisposable
{
    Guid JobId { get; }
}

/// <summary>
///     Completed input owned by the matching live lease. It exposes no filesystem paths.
///     Retain it when acceptance is durable or uncertain; discard only after proving no publication.
/// </summary>
public interface IPreparedJobWorkspace
{
    Guid JobId { get; }
    Task DiscardAsync(CancellationToken ct = default);
}
