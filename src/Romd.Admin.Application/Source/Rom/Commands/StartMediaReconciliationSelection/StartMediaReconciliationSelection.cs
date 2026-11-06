using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Content;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSelection;

public sealed record StartMediaReconciliationSelectionCommand(Guid RequestId, string Filter, string? KindId, string? SystemKey,
    long Limit, bool ConversionRequested) : ICommand<Guid>;

public sealed class StartMediaReconciliationSelectionCommandHandler(IMediaReconciliationRequests requests, IUnitOfWork work,
    ICurrentUser user, MediaKindCatalog kinds, IReferenceCatalogService referenceCatalog,
    ILogger<StartMediaReconciliationSelectionCommandHandler> logger) : ICommandHandler<StartMediaReconciliationSelectionCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(StartMediaReconciliationSelectionCommand command, CancellationToken ct = default)
    {
        if (user.UserId is not { } actor || actor == Guid.Empty)
            return MediaReconciliationErrors.ActorRequired();
        if (command.RequestId == Guid.Empty || command.Limit is < 1 or > (4L * int.MaxValue))
            return MediaReconciliationErrors.InvalidRequest("Provide an exact request identity and a positive bounded selection limit.");

        int? platformId = null;
        if (command.SystemKey is { } system)
        {
            platformId = await referenceCatalog.ResolveSystemIdAsync(system, ct);
            if (platformId is null)
                return MediaReconciliationErrors.InvalidRequest("Choose a known system key.");
        }
        MediaReconciliationFilter? filter = command.Filter switch
        {
            "AllSubjects" when command.KindId is null && platformId is null => new MediaReconciliationFilter.AllSubjects(),
            "RetainedRepresentations" when command.KindId is null && platformId is null && !command.ConversionRequested => new MediaReconciliationFilter.RetainedRepresentations(),
            "KindSubjects" when command.KindId is { } kind && kinds.Find(kind) is not null => new MediaReconciliationFilter.KindSubjects(kind, platformId),
            _ => null
        };
        if (filter is null)
            return MediaReconciliationErrors.InvalidRequest("Choose a supported filter and its exact kind/system scope. Retained representations only request verification.");

        return await work.ExecuteInTransactionAsync<Guid>(async token =>
        {
            var accepted = await requests.StageSelectionAsync(command.RequestId, filter, command.Limit, command.ConversionRequested,
                MediaReconciliationCause.Admin, actor, token);
            return accepted.IsError ? accepted.Errors : accepted.Value.Job.Id;
        }, logger, ct);
    }
}
