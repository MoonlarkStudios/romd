namespace Romd.Consumer.Application.Activity;

/// <summary>
///     Application-owned transaction boundary for a play-session upsert.
///     Implementations serialize mutations for one user so duplicate client retries remain safe.
/// </summary>
public interface IPlayActivityUnitOfWork
{
    Task<IPlayActivityTransaction> BeginAsync(Guid userId, CancellationToken ct = default);
}

public interface IPlayActivityTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}
