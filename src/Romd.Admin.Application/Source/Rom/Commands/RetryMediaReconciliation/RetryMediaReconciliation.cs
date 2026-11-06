using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;

namespace Romd.Admin.Application.Source.Rom.Commands.RetryMediaReconciliation;

public sealed record RetryMediaReconciliationCommand(Guid RequestId, Guid PreviousJobId) : ICommand<Guid>;

public sealed class RetryMediaReconciliationCommandHandler(IMediaReconciliationRequests requests, IUnitOfWork work,
    ICurrentUser user, ILogger<RetryMediaReconciliationCommandHandler> logger) : ICommandHandler<RetryMediaReconciliationCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(RetryMediaReconciliationCommand command, CancellationToken ct = default)
    {
        if (user.UserId is not { } actor || actor == Guid.Empty)
            return MediaReconciliationErrors.ActorRequired();
        if (command.RequestId == Guid.Empty || command.PreviousJobId == Guid.Empty)
            return MediaReconciliationErrors.InvalidRequest("Provide exact request and previous job identities.");

        return await work.ExecuteInTransactionAsync<Guid>(async token =>
        {
            var accepted = await requests.StageRetryAsync(command.RequestId, command.PreviousJobId, actor, token);
            return accepted.IsError ? accepted.Errors : accepted.Value.Job.Id;
        }, logger, ct);
    }
}
