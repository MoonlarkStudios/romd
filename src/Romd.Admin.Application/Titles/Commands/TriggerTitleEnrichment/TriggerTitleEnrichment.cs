using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment;

/// <summary>
///     Requests enrichment of one title. A metadata refresh requeues the title and rotates its revision, so it is a
///     title family write checked against <paramref name="IfMatch" />, the revision the curator read. Artwork
///     acquisition (<paramref name="ArtworkOnly" />) writes no title row and takes no precondition.
/// </summary>
public sealed record TriggerTitleEnrichmentCommand(
    int TitleId,
    bool ArtworkOnly = false,
    RevisionPrecondition? IfMatch = null) : ICommand<Guid>;

public sealed class TriggerTitleEnrichmentCommandHandler(
    ITitleAggregateReader titleAggregates,
    ITitleMetadataStore titleMetadata,
    IEnrichmentJobRepository enrichmentJobs,
    IEnrichmentJobEnqueuer enrichmentJobEnqueuer,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<TriggerTitleEnrichmentCommandHandler> logger)
    : ICommandHandler<TriggerTitleEnrichmentCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(
        TriggerTitleEnrichmentCommand command,
        CancellationToken ct = default)
    {
        EnrichmentJob? acceptedJob = null;
        bool createdJob = false;
        bool accelerateExistingJob = false;
        try
        {
            var result = await unitOfWork.ExecuteInTransactionAsync<Guid>(async token =>
            {
                // Full aggregate load so the staged write preserves provenance/override JSON,
                // primary-media selection, and authoritative rating rows exactly as persisted.
                Title? title = await titleAggregates.GetAggregateAsync(command.TitleId, token);
                if (title is null)
                {
                    return CatalogErrors.TitleNotFound();
                }

                if (command.IfMatch is { } ifMatch && !ifMatch.IsSatisfiedBy(title.Revision))
                {
                    return CatalogErrors.TitlePreconditionFailed();
                }

                acceptedJob = await enrichmentJobs.GetActiveForTitleAsync(title.Id, token);
                accelerateExistingJob = acceptedJob is not null
                    && acceptedJob.PhaseEnum == EnrichmentJobPhase.Pending
                    && string.IsNullOrWhiteSpace(acceptedJob.HangfireJobId);

                if (acceptedJob is not null && acceptedJob.ArtworkOnly && !command.ArtworkOnly)
                    return Error.Conflict("Enrichment.ArtworkInProgress", "Artwork acquisition is already running. Retry the metadata refresh when it finishes.");
                if (!command.ArtworkOnly)
                {
                    title.RequeueForEnrichment();
                    await titleMetadata.UpdateMaterializedMetadataStagedAsync(title, token);
                }
                if (acceptedJob is null)
                {
                    acceptedJob = EnrichmentJob.Create(title.Name, title.Id, title.PlatformId, timeProvider.GetUtcNow(), command.ArtworkOnly);
                    await enrichmentJobs.AddStagedAsync(acceptedJob, token);
                    createdJob = true;
                }

                return acceptedJob.Id;
            }, logger, ct);
            if (result.IsError)
                return result.Errors;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PersistenceConflictException)
        {
            // A concurrent title write committed after the precondition held: the staged write's recheck is the
            // same stale validator.
            return CatalogErrors.TitlePreconditionFailed();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to accept title enrichment request (Title ID: {TitleId})",
                command.TitleId);
            return CatalogErrors.EnrichmentRequestFailed();
        }

        if (createdJob || accelerateExistingJob)
        {
            await TryEnqueueAsync(acceptedJob!.Id, ct);
        }

        return acceptedJob!.Id;
    }

    private async Task TryEnqueueAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            await enrichmentJobEnqueuer.EnqueueAsync(jobId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Title enrichment request {JobId} committed but enqueue acceleration failed; " +
                "the worker dispatcher will recover",
                jobId);
        }
    }
}
