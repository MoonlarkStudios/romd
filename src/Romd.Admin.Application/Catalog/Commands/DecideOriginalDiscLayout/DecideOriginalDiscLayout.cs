using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;

public sealed record DecideOriginalDiscLayoutCommand(int Id, OriginalDiscLayoutDecisionRequest Request, RevisionPrecondition Precondition, Guid Actor) : ICommand<OriginalDiscLayoutDto>;

public sealed class DecideOriginalDiscLayoutCommandHandler(IOriginalDiscLayoutService service, IUnitOfWork unitOfWork, IMediaReconciliationRequests requests,
    ILogger<DecideOriginalDiscLayoutCommandHandler> logger)
    : ICommandHandler<DecideOriginalDiscLayoutCommand, OriginalDiscLayoutDto>
{
    public async Task<ErrorOr<OriginalDiscLayoutDto>> HandleAsync(DecideOriginalDiscLayoutCommand command, CancellationToken ct = default)
    {
        return await unitOfWork.ExecuteInTransactionAsync<OriginalDiscLayoutDto>(async token =>
        {
            var staged = await service.DecideStagedAsync(command.Id, command.Request, command.Precondition, command.Actor, token);
            if (staged.IsError)
                return staged.Errors;
            // Hydrate the accepted representation, including generated audit identities, while our locks still hold.
            await unitOfWork.FlushAsync(token);
            foreach (int layoutId in staged.Value.Order())
            {
                var demand = await requests.StageSubjectAsync(new(Guid.NewGuid(), new MediaReconciliationSubject.OriginalLayout(layoutId),
                    false, MediaReconciliationCause.Facts, null, null), token);
                if (demand.IsError)
                    return demand.Errors;
            }
            var result = await service.GetAsync(command.Id, token);
            if (result.IsError)
                return result.Errors;
            return result.Value;
        }, logger, ct);
    }
}
