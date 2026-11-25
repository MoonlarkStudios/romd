using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;

namespace Romd.Admin.Application.MetadataProviders;

/// <summary>Recomputes the title after its provider contributions have been withdrawn under the title lock.</summary>
public sealed class ProviderContributionRematerializer(
    ITitleAggregateReader titles,
    IMetadataRematerializer rematerializer,
    ITitleMetadataStore metadata,
    ILibraryRepository libraries)
{
    public async Task StageAsync(int titleId, CancellationToken ct)
    {
        var title = await titles.GetAggregateAsync(titleId, ct)
            ?? throw new InvalidOperationException($"Locked title {titleId} is missing.");
        var previousEligibility = TitleEligibilityFields.From(title);
        await rematerializer.RematerializeAsync(title, ct);
        if (previousEligibility != TitleEligibilityFields.From(title))
            await libraries.FlagForRematerializationByPlatformAsync(title.PlatformId, ct);
        await metadata.UpdateMaterializedMetadataStagedAsync(title, ct);
    }
}
