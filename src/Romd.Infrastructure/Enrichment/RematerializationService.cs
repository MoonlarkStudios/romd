using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Enrichment;

public sealed class RematerializationService(
    ITitleRepository titleRepo,
    IPlatformRepository platformRepo,
    IPlatformFieldDefaultRepository platformFieldDefaultRepo,
    ILibraryMaterializationService materializationService,
    IUnitOfWork unitOfWork,
    IOptions<EnrichmentOptions> options,
    ILogger<RematerializationService> logger) : IRematerializationService
{
    private readonly EnrichmentOptions _options = options.Value;

    public Task RematerializeTitleAsync(int titleId, CancellationToken ct = default) =>
        RematerializeTitleCoreAsync(titleId, null, ct);

    private async Task RematerializeTitleCoreAsync(
        int titleId, IReadOnlyDictionary<string, string>? defaults, CancellationToken ct)
    {
        await unitOfWork.ExecuteInTransactionAsync<Success>(async token =>
        {
            var title = await titleRepo.GetWithMetadataLayersAsync(titleId, token);
            if (title is null)
            {
                logger.LogInformation("Title {TitleId} no longer exists; rematerialization is complete", titleId);
                return Result.Success;
            }

            var platformDefaults = defaults ?? await platformFieldDefaultRepo.GetByPlatformIdAsync(title.PlatformId, token);
            var before = TitleEligibilityFields.From(title);
            title.Rematerialize(_options.GlobalSourcePriority, platformDefaults);
            title.RecalculatePrimaryMedia(_options.GlobalSourcePriority);
            if (before != TitleEligibilityFields.From(title))
                await materializationService.FlagAffectedLibrariesAsync(title.PlatformId, token);

            await titleRepo.UpdateMaterializedMetadataStagedAsync(title, token);
            return Result.Success;
        }, logger, ct);
    }

    public async Task RematerializePlatformAsync(int platformId, CancellationToken ct = default)
    {
        var titleIds = await titleRepo.GetIdsByPlatformAsync(platformId, ct);
        var defaults = await platformFieldDefaultRepo.GetByPlatformIdAsync(platformId, ct);
        logger.LogInformation("Rematerializing {Count} titles for platform {PlatformId}", titleIds.Count, platformId);
        foreach (int titleId in titleIds)
        {
            ct.ThrowIfCancellationRequested();
            // Each title's effective state and invalidation are committed together. An earlier
            // library rebuild must not consume the only flag while later titles are still changing.
            await RematerializeTitleCoreAsync(titleId, defaults, ct);
        }
    }

    public async Task RematerializeAllAsync(CancellationToken ct = default)
    {
        var platforms = await platformRepo.GetAllAsync(ct);
        foreach (var platform in platforms)
        {
            ct.ThrowIfCancellationRequested();
            await RematerializePlatformAsync(platform.Id, ct);
        }
    }
}
