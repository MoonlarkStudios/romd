using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment;

public sealed record TriggerTitleEnrichmentCommand(int TitleId, bool ArtworkOnly = false) : ICommand<Guid>;

public sealed class TriggerTitleEnrichmentCommandHandler(
    ITitleRepository titleRepository,
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
        bool commitSucceeded = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

            // Full aggregate load so the staged write preserves provenance/override JSON,
            // primary-media selection, and authoritative rating rows exactly as persisted.
            Title? title = await titleRepository.GetWithCollectionsAsync(command.TitleId, ct);
            if (title is null)
            {
                return CatalogErrors.TitleNotFound();
            }

            acceptedJob = await enrichmentJobs.GetActiveForTitleAsync(title.Id, ct);
            accelerateExistingJob = acceptedJob is not null
                && acceptedJob.PhaseEnum == EnrichmentJobPhase.Pending
                && string.IsNullOrWhiteSpace(acceptedJob.HangfireJobId);

            if (acceptedJob is not null && acceptedJob.ArtworkOnly && !command.ArtworkOnly)
                return Error.Conflict("Enrichment.ArtworkInProgress", "Artwork acquisition is already running. Retry the metadata refresh when it finishes.");
            if (!command.ArtworkOnly)
            {
                title.RequeueForEnrichment();
                await titleRepository.UpdateMaterializedMetadataStagedAsync(title, ct);
            }
            if (acceptedJob is null)
            {
                acceptedJob = EnrichmentJob.Create(title.Name, title.Id, title.PlatformId, timeProvider, command.ArtworkOnly);
                await enrichmentJobs.AddStagedAsync(acceptedJob, ct);
                createdJob = true;
            }

            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            logger.LogWarning(
                ex,
                "Title enrichment request committed but transaction cleanup failed " +
                "(Title ID: {TitleId}, Job ID: {JobId})",
                command.TitleId,
                acceptedJob?.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
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
