using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Source.Dat.Commands.StopDatSubscription;

public sealed record StopDatSubscriptionCommand(int SubscriptionId) : ICommand<Success>;

public sealed class StopDatSubscriptionCommandHandler(
    IDatSubscriptionStopStore store,
    IUnitOfWork unitOfWork,
    ILogger<StopDatSubscriptionCommandHandler> logger) : ICommandHandler<StopDatSubscriptionCommand, Success>
{
    public Task<ErrorOr<Success>> HandleAsync(StopDatSubscriptionCommand command, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync(token => store.StopStagedAsync(command.SubscriptionId, token), logger, ct);
}
