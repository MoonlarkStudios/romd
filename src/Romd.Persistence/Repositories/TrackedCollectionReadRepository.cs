using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.TrackedCollection;
using Romd.Admin.Application.TrackedCollection.ReadModels;
using Romd.Domain.Catalog;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Computes tracked collection state from the catalog and owned SHA-1 hashes. The database
///     read is deliberately one set-based query that returns at most one row per catalog release;
///     title grouping and preference ranking never issue per-title database calls.
/// </summary>
public sealed class TrackedCollectionReadRepository(
    RomdDbContext context,
    IOptions<EnrichmentOptions> options) : ITrackedCollectionReadRepository
{
    private readonly CatalogReleasePreferencePolicy _preferencePolicy =
        new(options.Value.RegionPriority);

    public async Task<IReadOnlyList<TrackedCollectionTitleData>> ListAsync(
        TrackedCollectionView view,
        CancellationToken cancellationToken = default)
    {
        var titles = await BuildAsync(cancellationToken);
        var filtered = titles
            .Where(title => view switch
            {
                TrackedCollectionView.Satisfied => title.IsSatisfied,
                TrackedCollectionView.Missing => !title.IsSatisfied,
                TrackedCollectionView.Upgrades => title.HasUpgrade,
                _ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
            });

        return view == TrackedCollectionView.Satisfied
            ? filtered
                .OrderByDescending(title => title.SatisfiedAt)
                .ThenBy(title => title.PlatformName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(title => title.TitleName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(title => title.TitleId)
                .ToList()
            : filtered.ToList();
    }

    public async Task<TrackedCollectionStatsData> GetStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var titles = await BuildAsync(cancellationToken);
        return new TrackedCollectionStatsData
        {
            TrackedTitleCount = titles.Count,
            SatisfiedTitleCount = titles.Count(title => title.IsSatisfied),
            MissingTitleCount = titles.Count(title => !title.IsSatisfied),
            UpgradeTitleCount = titles.Count(title => title.HasUpgrade),
            CompletionPercent = CompletionPercent(titles),
            Platforms = titles
                .GroupBy(title => new { title.PlatformId, title.PlatformName })
                .Select(group => new TrackedCollectionPlatformStatsData
                {
                    PlatformId = group.Key.PlatformId,
                    PlatformName = group.Key.PlatformName,
                    TrackedTitleCount = group.Count(),
                    SatisfiedTitleCount = group.Count(title => title.IsSatisfied),
                    MissingTitleCount = group.Count(title => !title.IsSatisfied),
                    UpgradeTitleCount = group.Count(title => title.HasUpgrade),
                    CompletionPercent = CompletionPercent(group)
                })
                .OrderBy(platform => platform.PlatformName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(platform => platform.PlatformId)
                .ToList()
        };
    }

    private async Task<List<TrackedCollectionTitleData>> BuildAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from tracked in context.TrackedTitles.AsNoTracking()
                join title in context.Titles.AsNoTracking() on tracked.TitleId equals title.Id
                join platform in context.Platforms.AsNoTracking() on title.PlatformId equals platform.Id
                join releaseRow in context.CatalogReleases.AsNoTracking()
                    on title.Id equals releaseRow.CatalogTitleId into releaseRows
                from release in releaseRows.DefaultIfEmpty()
                select new CollectionRow(
                    title.Id,
                    title.Name,
                    platform.Id,
                    platform.Name,
                    tracked.PinnedCatalogReleaseId,
                    tracked.UpdatedAt,
                    release == null ? null : release.Id,
                    release == null ? null : release.Name,
                    release == null ? null : release.Region,
                    release == null ? null : release.Revision,
                    release == null ? null : release.CreatedAt,
                    release == null
                        ? 0
                        : context.CatalogReleaseFiles.Count(file =>
                            file.CatalogReleaseId == release.Id
                            && (file.Status == null || file.Status.ToLower() != "nodump")),
                    release == null
                        ? 0
                        : context.CatalogReleaseFiles.Count(file =>
                            file.CatalogReleaseId == release.Id
                            && (file.Status == null || file.Status.ToLower() != "nodump")
                            && file.Sha1.HasValue
                            && context.RomFiles.Any(owned => owned.Sha1 == file.Sha1.Value)),
                    release != null
                    && context.CatalogReleaseFiles.Any(file =>
                        file.CatalogReleaseId == release.Id
                        && file.Status != null
                        && (file.Status.ToLower() == "baddump" || file.Status.ToLower() == "nodump")),
                    release == null
                        ? null
                        : context.RomFiles
                            .Where(owned => context.CatalogReleaseFiles.Any(file =>
                                file.CatalogReleaseId == release.Id
                                && (file.Status == null || file.Status.ToLower() != "nodump")
                                && file.Sha1.HasValue
                                && owned.Sha1 == file.Sha1.Value))
                            .OrderByDescending(owned => owned.CreatedAt)
                            .Select(owned => (DateTimeOffset?)owned.CreatedAt)
                            .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => new
            {
                row.TitleId,
                row.TitleName,
                row.PlatformId,
                row.PlatformName,
                row.PinnedCatalogReleaseId
            })
            .Select(group => BuildTitle(group))
            .OrderBy(title => title.PlatformName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(title => title.TitleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(title => title.TitleId)
            .ToList();
    }

    private TrackedCollectionTitleData BuildTitle<TKey>(IGrouping<TKey, CollectionRow> rows)
    {
        var first = rows.First();
        var releases = rows
            .Where(row => row.CatalogReleaseId.HasValue)
            .Select(row => new ReleaseCandidate(
                row.CatalogReleaseId!.Value,
                row.ReleaseName!,
                row.Region,
                row.Revision,
                row.RequiredFileCount > 0 && row.RequiredFileCount == row.OwnedRequiredFileCount,
                row.HasInferiorDump,
                row.RequiredFileCount > 0 && row.RequiredFileCount == row.OwnedRequiredFileCount
                    ? Latest(first.TrackedUpdatedAt, row.ReleaseCreatedAt, row.LatestOwnedRequiredFileAt)
                    : null))
            .ToList();

        var desiredPreference = _preferencePolicy.SelectDesired(
            releases.Select(ToPreferenceCandidate),
            first.PinnedCatalogReleaseId);
        ReleaseCandidate? desired = desiredPreference is null
            ? null
            : releases.Single(release => release.Id == desiredPreference.CatalogReleaseId);
        ReleaseCandidate? owned = desired?.IsComplete == true
            ? desired
            : SelectPreferredOwned(releases);
        bool isSatisfied = owned is not null;
        bool hasUpgrade = isSatisfied && desired is not null && !desired.IsComplete;

        return new TrackedCollectionTitleData
        {
            TitleId = first.TitleId,
            TitleName = first.TitleName,
            PlatformId = first.PlatformId,
            PlatformName = first.PlatformName,
            IsSatisfied = isSatisfied,
            HasUpgrade = hasUpgrade,
            IsPinned = first.PinnedCatalogReleaseId.HasValue,
            SatisfiedAt = owned?.SatisfiedAt,
            DesiredRelease = ToData(desired),
            OwnedRelease = ToData(owned)
        };
    }

    private static TrackedCollectionReleaseData? ToData(ReleaseCandidate? release) =>
        release is null
            ? null
            : new TrackedCollectionReleaseData(release.Id, release.Name, release.Region, release.Revision);

    private static CatalogReleasePreferenceCandidate ToPreferenceCandidate(ReleaseCandidate release) =>
        new(release.Id, release.Name, release.Region, release.Revision, release.HasInferiorDump);

    private ReleaseCandidate? SelectPreferredOwned(IReadOnlyCollection<ReleaseCandidate> releases)
    {
        var preference = _preferencePolicy.SelectDesired(
            releases.Where(release => release.IsComplete).Select(ToPreferenceCandidate));
        return preference is null
            ? null
            : releases.Single(release => release.Id == preference.CatalogReleaseId);
    }

    private static decimal CompletionPercent(IEnumerable<TrackedCollectionTitleData> titles)
    {
        var materialized = titles as IReadOnlyCollection<TrackedCollectionTitleData> ?? titles.ToList();
        return materialized.Count == 0
            ? 0m
            : Math.Round((decimal)materialized.Count(title => title.IsSatisfied) / materialized.Count * 100m, 1);
    }

    private static DateTimeOffset Latest(
        DateTimeOffset trackedUpdatedAt,
        DateTimeOffset? releaseCreatedAt,
        DateTimeOffset? latestOwnedRequiredFileAt)
    {
        var latest = trackedUpdatedAt;
        if (releaseCreatedAt > latest)
        {
            latest = releaseCreatedAt.Value;
        }

        if (latestOwnedRequiredFileAt > latest)
        {
            latest = latestOwnedRequiredFileAt.Value;
        }

        return latest;
    }

    private sealed record CollectionRow(
        int TitleId,
        string TitleName,
        int PlatformId,
        string PlatformName,
        int? PinnedCatalogReleaseId,
        DateTimeOffset TrackedUpdatedAt,
        int? CatalogReleaseId,
        string? ReleaseName,
        string? Region,
        string? Revision,
        DateTimeOffset? ReleaseCreatedAt,
        int RequiredFileCount,
        int OwnedRequiredFileCount,
        bool HasInferiorDump,
        DateTimeOffset? LatestOwnedRequiredFileAt);

    private sealed record ReleaseCandidate(
        int Id,
        string Name,
        string? Region,
        string? Revision,
        bool IsComplete,
        bool HasInferiorDump,
        DateTimeOffset? SatisfiedAt);

}
