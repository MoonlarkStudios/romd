using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Security;

namespace Romd.Persistence.Identity;

/// <summary>Adapts the shared authentication boundary to the existing unit of work; no separate EF commit implementation.</summary>
public sealed class AccountSessionTransaction(IUnitOfWork unitOfWork, ILogger<AccountSessionTransaction> logger) : IAccountSessionTransaction
{
    public Task<ErrorOr<AccountSession>> ExecuteAsync(
        Func<CancellationToken, Task<ErrorOr<AccountSession>>> mutation,
        CancellationToken ct = default) => unitOfWork.ExecuteInTransactionAsync(mutation, logger, ct);
}
