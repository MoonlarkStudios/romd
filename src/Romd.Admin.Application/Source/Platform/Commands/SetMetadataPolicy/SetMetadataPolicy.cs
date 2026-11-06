using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy;

public sealed record SetMetadataPolicyCommand(int PlatformId, RevisionPrecondition IfMatch,
    IReadOnlyDictionary<string, string?> Defaults) : ICommand<PlatformMetadataPolicyDto>;

public sealed class SetMetadataPolicyCommandHandler(
    IPlatformRepository platforms,
    IPlatformFieldDefaultRepository defaults,
    IMetadataRematerializationJobRepository jobs,
    IAdminEventOutbox outbox,
    IEnrichmentJobEnqueuer enqueuer,
    ICurrentUser currentUser,
    IReferenceCatalogService referenceCatalog,
    IOptions<EnrichmentOptions> options,
    IUnitOfWork unitOfWork,
    ILogger<SetMetadataPolicyCommandHandler> logger,
    TimeProvider clock) : ICommandHandler<SetMetadataPolicyCommand, PlatformMetadataPolicyDto>
{
    public async Task<ErrorOr<PlatformMetadataPolicyDto>> HandleAsync(SetMetadataPolicyCommand command, CancellationToken ct = default)
    {
        if (command.Defaults.Any(pair => !MetadataPolicy.Fields.Contains(pair.Key) ||
            (!string.IsNullOrEmpty(pair.Value) && pair.Value != "user" &&
             !options.Value.GlobalSourcePriority.Contains(pair.Value))))
            return Error.Validation("MetadataPolicy.InvalidPreference", "Choose a supported metadata field and source.");

        Guid? accelerate = null;
        ErrorOr<PlatformMetadataPolicyDto> result;
        try
        {
            result = await unitOfWork.ExecuteInTransactionAsync<PlatformMetadataPolicyDto>(async token =>
            {
                var platform = await platforms.GetByIdAsync(command.PlatformId, token);
                if (platform is null)
                    return Error.NotFound("Platform.NotFound", "System not found.");
                await defaults.LockAsync(command.PlatformId, token);
                var saved = await defaults.GetByPlatformIdAsync(command.PlatformId, token);
                if (!command.IfMatch.IsSatisfiedBy(MetadataPolicy.Revision(saved, options.Value.GlobalSourcePriority)))
                    return Error.Custom(412, "MetadataPolicy.PreconditionFailed", "Metadata policy changed. Reload before saving again.");
                var updated = new Dictionary<string, string>(saved, StringComparer.Ordinal);
                foreach (var (field, source) in command.Defaults)
                {
                    if (string.IsNullOrEmpty(source))
                        updated.Remove(field);
                    else
                        updated[field] = source;
                }
                Guid? pending;
                if (MetadataPolicy.Revision(updated, options.Value.GlobalSourcePriority) == MetadataPolicy.Revision(saved, options.Value.GlobalSourcePriority))
                    pending = await jobs.GetPendingIdAsync(command.PlatformId, token);
                else
                {
                    // Flush all deletions before staging replacements: the unique field index and
                    // delete/add audit facts require that order, within this policy transaction.
                    foreach (string field in command.Defaults.Keys)
                        await defaults.ClearStagedAsync(command.PlatformId, field, token);
                    await unitOfWork.FlushAsync(token);
                    foreach (var (field, source) in command.Defaults)
                    {
                        if (!string.IsNullOrEmpty(source))
                            await defaults.SetStagedAsync(command.PlatformId, field, source, token);
                    }
                    var job = await jobs.GetReusablePendingAsync(command.PlatformId, token);
                    if (job is null)
                    {
                        job = MetadataRematerializationJob.Create(platform.Id, platform.ShortName, currentUser.UserId, createdAt: clock.GetUtcNow());
                        await jobs.AddAsync(job, token);
                        await outbox.EnqueueAsync(AdminRealtimeEventTypes.JobUpdated, job.ToContract(await referenceCatalog.GetSystemKeysAsync(token)), token);
                    }
                    pending = accelerate = job.Id;
                }
                return new PlatformMetadataPolicyDto(MetadataPolicy.Revision(updated, options.Value.GlobalSourcePriority), updated,
                    options.Value.GlobalSourcePriority, await defaults.CountTitlesAsync(command.PlatformId, token), pending);
            }, logger, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update metadata policy for {PlatformId}", command.PlatformId);
            return Error.Failure("MetadataPolicy.SaveFailed", "Metadata policy could not be saved.");
        }
        if (!result.IsError && accelerate is { } jobId)
        {
            try
            { await enqueuer.EnqueueAsync(jobId, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Metadata job {JobId} is committed and awaiting durable dispatch", jobId); }
        }
        return result;
    }
}
