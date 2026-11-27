using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Consumer.Application.PlayLater;

namespace Romd.Persistence;

/// <summary>Bridges the consumer's transaction boundary to the shared persistence implementation.</summary>
public sealed class PlayLaterTransaction(
    IUnitOfWork unitOfWork, ILogger<PlayLaterTransaction> logger) : IPlayLaterTransaction
{
    public Task<ErrorOr<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<ErrorOr<T>>> mutation, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync(mutation, logger, ct);
}
