using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

public sealed class RematerializationService(
    ITitleAggregateReader titleAggregates,
    ITitleMetadataStore titleMetadata,
    IPlatformFieldDefaultRepository platformFieldDefaultRepo,
    ILibraryMaterializationService materializationService,
    IUnitOfWork unitOfWork,
    IOptions<EnrichmentOptions> options,
    ILogger<RematerializationService> logger) : IRematerializationService
{
    private readonly EnrichmentOptions _options = options.Value;

    public async Task RematerializeTitleAsync(int titleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await unitOfWork.ExecuteInTransactionAsync<Success>(async token =>
        {
            var title = await titleAggregates.GetAggregateAsync(titleId, token);
            if (title is null)
            {
                logger.LogInformation("Title {TitleId} no longer exists; rematerialization is complete", titleId);
                return Result.Success;
            }

            var platformDefaults = await platformFieldDefaultRepo.GetByPlatformIdAsync(title.PlatformId, token);
            var before = TitleEligibilityFields.From(title);
            title.Rematerialize(_options.GlobalSourcePriority, platformDefaults);
            title.RecalculatePrimaryMedia(_options.GlobalSourcePriority);
            if (before != TitleEligibilityFields.From(title))
                await materializationService.FlagAffectedLibrariesAsync(title.PlatformId, token);

            await titleMetadata.UpdateMaterializedMetadataStagedAsync(title, token);
            return Result.Success;
        }, logger, ct);
    }
}
