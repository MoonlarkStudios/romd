using ErrorOr;

namespace Romd.Consumer.Application.Common.Persistence;

/// <summary>
///     Runs one short consumer security mutation. Successful results are returned only after
///     commit; business errors, exceptions and cancellation roll back the entire operation.
///     The callback must not perform external I/O or start another transaction.
/// </summary>
public interface IConsumerSecurityTransaction
{
    Task<ErrorOr<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<ErrorOr<T>>> mutation,
        CancellationToken ct = default);
}
