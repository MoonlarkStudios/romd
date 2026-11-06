using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Consumer.Application.Common.Persistence;

namespace Romd.Persistence.Identity;

/// <summary>
///     Bridges the consumer-owned boundary to the existing unit-of-work implementation.
///     Consumer application code does not depend on the administrative application port.
/// </summary>
public sealed class ConsumerSecurityTransaction(
    IUnitOfWork unitOfWork,
    ILogger<ConsumerSecurityTransaction> logger) : IConsumerSecurityTransaction
{
    public Task<ErrorOr<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<ErrorOr<T>>> mutation,
        CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync(mutation, logger, ct);
}
