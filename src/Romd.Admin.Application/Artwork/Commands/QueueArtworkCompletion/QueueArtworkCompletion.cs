using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Jobs;

using ArtworkCompletionJob = Romd.Domain.Jobs.ArtworkCompletionJob;

namespace Romd.Admin.Application.Artwork.Commands.QueueArtworkCompletion;

public sealed record QueueArtworkCompletionCommand(ArtworkCompletionRequest Request, Guid ActorId) : ICommand<Guid>;

/// <summary>Accepts one immutable run snapshot and its durable dispatch before returning the existing job reference.</summary>
public sealed class QueueArtworkCompletionCommandHandler(IArtworkCompletionAcceptance acceptance, IUnitOfWork unitOfWork,
    TimeProvider clock, ILogger<QueueArtworkCompletionCommandHandler> logger) : ICommandHandler<QueueArtworkCompletionCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(QueueArtworkCompletionCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (request.RequestId == Guid.Empty || !ArtworkCompletionIntent.IsValid(request))
            return ArtworkCurationErrors.InvalidRequest();
        if (request.Scope == "Retry" && request.RetryJobId is { } originalId)
        {
            var originalMode = await acceptance.ReadRetryModeAsync(originalId, ct);
            if (originalMode is null)
                return ArtworkCurationErrors.TitleNotFound();
            request = request with { Mode = originalMode };
        }
        request = ArtworkCompletionIntent.Normalize(request);
        var signature = ArtworkCompletionIntent.Signature(request);
        return await unitOfWork.ExecuteInTransactionAsync<Guid>(async token =>
        {
            await acceptance.LockRequestAsync(request.RequestId, token);
            var existing = await acceptance.FindRequestAsync(request.RequestId, token);
            if (existing is not null)
                return existing.CompletionSignature == signature ? existing.JobId : ArtworkCurationErrors.RequestConflict();
            await acceptance.LockOwnersAsync(token);
            if (request.Scope == "Selected" && await acceptance.FindActiveSelectedAsync(signature, token) is { } activeId)
                return activeId;
            var snapshot = await acceptance.SnapshotScopeAsync(request, token);
            if (snapshot.IsError)
                return snapshot.Errors;
            if (request.Scope == "Retry" && snapshot.Value.Titles.Count == 0)
                return ArtworkCurationErrors.InvalidRequest();
            var titleIds = snapshot.Value.Titles.Select(title => title.TitleId).ToArray();
            var owners = await acceptance.ReadOwnersAsync(titleIds, token);
            var items = ArtworkCompletionSharing.Plan(request, snapshot.Value, owners);
            var job = ArtworkCompletionJob.Create(request.RequestId, items.Count, command.ActorId, clock.GetUtcNow(), request.Mode);
            job.SetProgress(items.Count(item => item.FollowUpJobId is not null));
            var receipt = acceptance.AddJobStaged(job, signature, items);
            // The GUID is known already, but the job and dispatch must exist before item insertion's job FK.
            await unitOfWork.FlushAsync(token);
            var id = receipt.ReadAfterFlush();
            await acceptance.InsertItemsStagedAsync(receipt, clock.GetUtcNow(), token);
            if (request.Scope == "Retry" && request.RetryJobId is { } retryId)
                await acceptance.AttachRetryStagedAsync(receipt, retryId, token);
            return id;
        }, logger, ct);
    }
}
