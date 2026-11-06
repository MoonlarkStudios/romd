namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     Serializes a durable executor mutation with its execution fence. Implementations
///     must verify and lock the claim in the same transaction used by the mutation delegate.
/// </summary>
public interface IJobExecutionMutationGuard
{
    Task ExecuteAsync(
        Guid jobId,
        Guid fenceToken,
        Func<CancellationToken, Task> mutation,
        CancellationToken ct = default);
}
