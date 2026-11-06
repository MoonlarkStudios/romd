using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Models;
namespace Romd.Admin.Application.Ingestion.Jobs.Commands.SetJobArchived;

public sealed record SetJobArchivedCommand(Guid JobId, bool IsArchived) : ICommand<JobDto>;
public sealed class SetJobArchivedCommandHandler(IJobRepository jobs, IUnitOfWork unitOfWork,
    ICurrentUser user, IReferenceCatalogService referenceCatalog, ILogger<SetJobArchivedCommandHandler> logger) : ICommandHandler<SetJobArchivedCommand, JobDto>
{
    public async Task<ErrorOr<JobDto>> HandleAsync(SetJobArchivedCommand command, CancellationToken ct = default)
    {
        var job = await jobs.GetVisibleAsync(command.JobId, user, ct);
        if (job is null)
            return JobErrors.NotFound(command.JobId);
        if (!job.IsTerminal)
            return JobErrors.NotTerminal();
        return await unitOfWork.ExecuteInTransactionAsync<JobDto>(async token =>
        {
            var updated = await jobs.SetArchivedAsync(command.JobId, command.IsArchived, JobVisibility.ScopeFor(user), token);
            if (updated is null)
            {
                var current = await jobs.GetVisibleAsync(command.JobId, user, token);
                return current is { IsTerminal: false } ? JobErrors.NotTerminal() : JobErrors.NotFound(command.JobId);
            }
            var representation = updated.ToContract(await referenceCatalog.GetSystemKeysAsync(token));
            return representation;
        }, logger, ct);
    }
}
