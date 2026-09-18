namespace Romd.Admin.Application.Common.Persistence;

/// <summary>
///     Opens a caller-owned read transaction whose database snapshot remains stable across
///     every statement in the scope. Implementations must request repeatable snapshot semantics
///     explicitly for their provider and fail closed when those semantics are unavailable.
/// </summary>
public interface IReadSnapshotTransactionFactory
{
    Task<IReadSnapshotTransaction> BeginAsync(CancellationToken cancellationToken = default);
}

/// <summary>An active repeatable read snapshot owned by the application use case.</summary>
public interface IReadSnapshotTransaction : IAsyncDisposable
{
    /// <summary>Completes the read-only transaction after the use case has consumed its snapshot.</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}
