using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Enrichment;
using Romd.Admin.Application.Configuration;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
namespace Romd.Persistence.Enrichment;

/// <summary>
/// SQL reads each title's current identity, metadata and artwork facts once. The shared domain
/// expression classifies those facts in SQL before counts, filtering or keyset pagination.
/// Scope predicates restrict the title table before its lateral facts function executes.
/// </summary>
public sealed class EnrichmentAssessmentQuery(RomdDbContext db, IQueryable<TitleEntity>? scope = null, EnrichmentOptions? options = null, EnrichmentProviderAvailability? providers = null)
{
    public IQueryable<int> AutomaticTitleIds() => new EnrichmentAssessmentQuery(db,
        (scope ?? db.Titles).Where(x => db.TrackedTitles.Any(t => t.TitleId == x.Id)), options, providers)
        .Summaries().Where(x => x.CanEnrichAutomatically).Select(x => x.TitleId);

    public IQueryable<EnrichmentAssessmentSummary> Summaries() => Summaries(Requirements());

    // A facts plan cached for a page must not receive an entire catalog. Keep this in one
    // statement so all batches share its snapshot and the normal command deadline.
    public IQueryable<EnrichmentAssessmentSummary> SummariesForCounts() => Summaries(RequirementsForCounts());

    private static IQueryable<EnrichmentAssessmentSummary> Summaries(IQueryable<EnrichmentRequirement> requirements)
        => requirements.GroupBy(x => x.TitleId).Select(group => new EnrichmentAssessmentSummary
        {
            TitleId = group.Key,
            NeedsReview = group.Count(x => x.State == EnrichmentRequirementState.NeedsReview) > 0,
            CanEnrichAutomatically = group.Count(x => x.State == EnrichmentRequirementState.Automatic) > 0,
            HasActiveWork = group.Count(x => x.State == EnrichmentRequirementState.Processing) > 0,
            HasOperationalBlocker = group.Count(x => x.State == EnrichmentRequirementState.Blocked) > 0
        });

    public IQueryable<EnrichmentRequirement> Requirements() => Requirements(boundTitleArrays: false);

    internal IQueryable<EnrichmentRequirement> RequirementsForCounts() => Requirements(boundTitleArrays: true);

    private IQueryable<EnrichmentRequirement> Requirements(bool boundTitleArrays)
    {
        var settings = options ?? new EnrichmentOptions();
        var cover = settings.DownloadMedia && settings.MediaTypesToDownload.Contains(MediaType.Cover);
        var background = settings.DownloadMedia && settings.MediaTypesToDownload.Contains(MediaType.Background);
        var logo = settings.DownloadMedia && settings.MediaTypesToDownload.Contains(MediaType.Logo);
        var enabled = providers?.Enabled.ToArray() ?? [];
        var configured = providers?.Configured.ToArray() ?? [];
        var disabled = providers?.Disabled.ToArray() ?? [];
        var titles = scope ?? db.Titles;
        var ids = titles.GroupBy(_ => 1).Select(group => new { Values = EF.Functions.ArrayAgg(group.Select(title => title.Id)) });
        if (boundTitleArrays)
            ids = ids.SelectMany(group => db.EnrichmentBatchMembers(group.Values),
                    (group, member) => new { Batch = (member.Ordinal - 1) / 512, Id = member.Value })
                .GroupBy(member => member.Batch)
                .Select(batch => new { Values = EF.Functions.ArrayAgg(batch.Select(member => member.Id)) });
        var facts = ids.SelectMany(group => db.EnrichmentFacts(group.Values, ProviderIdentityPolicy.Version,
            cover, background, logo, enabled, configured, disabled));
        return facts.Select(EnrichmentAssessmentPolicy.Classify);
    }
}

public sealed class EnrichmentAssessmentSummary
{
    public int TitleId { get; init; }
    public bool NeedsReview { get; init; }
    public bool CanEnrichAutomatically { get; init; }
    public bool HasActiveWork { get; init; }
    public bool HasOperationalBlocker { get; init; }
}
