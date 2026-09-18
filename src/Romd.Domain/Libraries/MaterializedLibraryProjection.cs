namespace Romd.Domain.Libraries;

public enum LibraryTitleAvailability
{
    MetadataOnly = 0,
    Partial = 1,
    Playable = 2
}

public sealed record MaterializedLibraryProjection(
    IReadOnlyList<MaterializedLibraryTitle> Titles,
    IReadOnlyList<MaterializedLibraryRelease> Releases);

/// <summary>
/// Per-title projection derived from eligible release rows. Hidden titles are omitted,
/// so materialized title rows are always visible. Ownership, playability, counts, and
/// availability are aggregates over accessible releases.
/// </summary>
public sealed record MaterializedLibraryTitle(
    int LibraryId,
    int TitleId,
    int PlatformId,
    string? Genre,
    bool IsVisible,
    bool IsOwned,
    bool IsPlayable,
    int EligibleReleaseCount,
    int PlayableReleaseCount,
    int ExposedReleaseCount,
    LibraryTitleAvailability Availability);

/// <summary>
/// Per-release projection with near-orthogonal derived axes. Eligibility comes from
/// release-level library filters; blocked releases are ineligible and carry a block
/// reason. Playability requires eligibility, ownership, completeness, and no block.
/// Exposure is the consumer-facing accessibility gate and matches eligible, unblocked
/// releases; consumers must not recompute it.
/// </summary>
public sealed record MaterializedLibraryRelease(
    int LibraryId,
    int TitleId,
    int? CatalogReleaseId,
    int DatGameId,
    int DatFileId,
    int PlatformId,
    bool IsEligible,
    bool IsComplete,
    bool IsOwned,
    bool IsPlayable,
    bool IsBlocked,
    string? BlockReason,
    bool IsExposed,
    string? ExposureReason);

public static class MaterializedLibraryProjectionBuilder
{
    private const string ExposedDefaultReason = "ExposedDefault";
    private const string NotEligibleExposureReason = "NotEligible";

    public static MaterializedLibraryProjection Build(
        int libraryId,
        IEnumerable<TitleCandidates> titles,
        LibraryConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(config);

        var materializedTitles = new List<MaterializedLibraryTitle>();
        var materializedReleases = new List<MaterializedLibraryRelease>();

        foreach (var title in titles)
        {
            if (GetTitleBlockReason(title, config) is not null)
                continue;

            var releases = CollapseReleaseCandidates(title.Candidates, config)
                .Select(candidate => ToRelease(libraryId, title, candidate, config))
                .ToList();

            var eligibleReleases = releases
                .Where(release => release.IsEligible)
                .ToList();

            if (eligibleReleases.Count == 0)
                continue;

            int playableReleaseCount = eligibleReleases.Count(release => release.IsPlayable);
            int exposedReleaseCount = releases.Count(release => release.IsExposed);

            var materializedTitle = new MaterializedLibraryTitle(
                LibraryId: libraryId,
                TitleId: title.TitleId,
                PlatformId: title.PlatformId,
                Genre: title.Genre,
                IsVisible: true,
                IsOwned: eligibleReleases.Any(release => release.IsOwned),
                IsPlayable: playableReleaseCount > 0,
                EligibleReleaseCount: eligibleReleases.Count,
                PlayableReleaseCount: playableReleaseCount,
                ExposedReleaseCount: exposedReleaseCount,
                Availability: DetermineAvailability(eligibleReleases, playableReleaseCount));

            AssertProjectionInvariants(materializedTitle, releases);

            materializedTitles.Add(materializedTitle);

            materializedReleases.AddRange(releases);
        }

        return new MaterializedLibraryProjection(materializedTitles, materializedReleases);
    }

    public static string? GetExclusionReason(TitleCandidates title, LibraryConfiguration config)
    {
        var titleReason = GetTitleBlockReason(title, config);
        if (titleReason is not null) return titleReason;
        var releases = CollapseReleaseCandidates(title.Candidates, config);
        if (releases.Count == 0) return "NoLinkedRelease";
        if (releases.Any(r => GetReleaseBlockReason(r, config) is null)) return null;
        return releases.All(r => GetReleaseBlockReason(r, config) == "ExcludedDat")
            ? "ExcludedDat" : "MissingRom";
    }

    public static string? GetTitleBlockReason(TitleCandidates title, LibraryConfiguration config)
    {
        if (config.TitleSelectionMode == LibraryTitleSelectionMode.Rules &&
            config.AllowedPlatformIds.Count > 0 && !config.AllowedPlatformIds.Contains(title.PlatformId))
            return "SystemScope";

        if (config.ExcludeTitleIds.Contains(title.TitleId))
            return "ManualExclude";

        if (config.TitleSelectionMode == LibraryTitleSelectionMode.IncludeOnly &&
            !config.IncludeTitleIds.Contains(title.TitleId))
        {
            return "NotInIncludeList";
        }

        var ratingVerdict = ContentRatingPolicyEvaluator.Evaluate(
            config.ContentRatingPolicy,
            title.ContentRatings);
        if (ratingVerdict.Outcome != RatingOutcome.Allowed)
            return ratingVerdict.Reason;

        if (config.IncludeTitleIds.Contains(title.TitleId))
            return null;

        if (config.AllowedGenres.Count == 0)
            return null;

        if (title.Genre is null && config.UnknownGenrePolicy != UnknownMetadataPolicy.Allow)
            return config.UnknownGenrePolicy == UnknownMetadataPolicy.NeedsReview
                ? "UnknownGenreNeedsReview"
                : "UnknownGenreHidden";

        if (title.Genre is not null &&
            !config.AllowedGenres.Contains(title.Genre, StringComparer.OrdinalIgnoreCase))
            return "Genre";

        return null;
    }

    private static MaterializedLibraryRelease ToRelease(
        int libraryId,
        TitleCandidates title,
        GameCandidate candidate,
        LibraryConfiguration config)
    {
        string? blockReason = GetReleaseBlockReason(candidate, config);
        bool isEligible = blockReason is null;
        bool isBlocked = !isEligible;
        bool isPlayable = isEligible && candidate.HasOwnedRoms && candidate.IsComplete;
        bool isExposed = isEligible && !isBlocked;

        return new MaterializedLibraryRelease(
            LibraryId: libraryId,
            TitleId: title.TitleId,
            CatalogReleaseId: candidate.CatalogReleaseId,
            DatGameId: candidate.DatGameId,
            DatFileId: candidate.DatFileId,
            PlatformId: title.PlatformId,
            IsEligible: isEligible,
            IsComplete: candidate.IsComplete,
            IsOwned: candidate.HasOwnedRoms,
            IsPlayable: isPlayable,
            IsBlocked: isBlocked,
            BlockReason: blockReason,
            IsExposed: isExposed,
            ExposureReason: isExposed ? ExposedDefaultReason : blockReason ?? NotEligibleExposureReason);
    }

    private static string? GetReleaseBlockReason(GameCandidate candidate, LibraryConfiguration config)
    {
        if (candidate.SourceDatFileIds.Count > 0 &&
            candidate.SourceDatFileIds.All(config.ExcludedDatIds.Contains))
        {
            return "ExcludedDat";
        }

        if (!config.ShowMissingGames && !candidate.HasOwnedRoms)
            return "MissingRom";

        return null;
    }

    private static IReadOnlyList<GameCandidate> CollapseReleaseCandidates(
        IReadOnlyList<GameCandidate> candidates,
        LibraryConfiguration config) =>
        candidates
            .GroupBy(candidate => candidate.CatalogReleaseId ?? -candidate.DatGameId)
            .Select(group => CollapseReleaseCandidateGroup(group, config))
            .ToList();

    private static GameCandidate CollapseReleaseCandidateGroup(
        IEnumerable<GameCandidate> group,
        LibraryConfiguration config)
    {
        var candidates = group.ToList();
        var representative = candidates
            .OrderBy(candidate => config.ExcludedDatIds.Contains(candidate.DatFileId))
            .ThenByDescending(candidate => candidate.IsComplete)
            .ThenByDescending(candidate => candidate.HasOwnedRoms)
            .ThenBy(candidate => candidate.DatGameId)
            .First();

        return representative with
        {
            HasOwnedRoms = candidates.Any(candidate => candidate.HasOwnedRoms),
            IsComplete = candidates.Any(candidate => candidate.IsComplete),
            SourceDatFileIds = candidates
                .SelectMany(candidate => candidate.SourceDatFileIds)
                .Distinct()
                .ToList()
        };
    }

    private static LibraryTitleAvailability DetermineAvailability(
        IReadOnlyList<MaterializedLibraryRelease> eligibleReleases,
        int playableReleaseCount)
    {
        if (playableReleaseCount > 0)
            return LibraryTitleAvailability.Playable;

        return eligibleReleases.Any(release => release.IsOwned)
            ? LibraryTitleAvailability.Partial
            : LibraryTitleAvailability.MetadataOnly;
    }

    internal static void AssertProjectionInvariants(
        MaterializedLibraryTitle title,
        IReadOnlyCollection<MaterializedLibraryRelease> releases)
    {
        AssertReleaseInvariants(releases);

        if (!title.IsVisible)
            throw new InvalidOperationException("Materialized titles must be visible; hidden titles are omitted.");

        if (title.EligibleReleaseCount <= 0)
            throw new InvalidOperationException("Materialized titles require at least one eligible release.");

        if (releases.Any(release =>
                release.LibraryId != title.LibraryId ||
                release.TitleId != title.TitleId ||
                release.PlatformId != title.PlatformId))
        {
            throw new InvalidOperationException("Materialized title aggregates must only include matching release rows.");
        }

        var eligibleReleases = releases
            .Where(release => release.IsEligible)
            .ToList();
        int playableReleaseCount = releases.Count(release => release.IsPlayable);

        if (title.EligibleReleaseCount != eligibleReleases.Count)
            throw new InvalidOperationException("Materialized title eligible release count does not match releases.");

        if (title.PlayableReleaseCount != playableReleaseCount)
            throw new InvalidOperationException("Materialized title playable release count does not match releases.");

        if (title.ExposedReleaseCount != releases.Count(release => release.IsExposed))
            throw new InvalidOperationException("Materialized title exposed release count does not match releases.");

        if (title.IsOwned != eligibleReleases.Any(release => release.IsOwned))
            throw new InvalidOperationException("Materialized title ownership must be derived from eligible releases.");

        if (title.IsPlayable != (playableReleaseCount > 0))
            throw new InvalidOperationException("Materialized title playability must be derived from playable releases.");

        if (title.Availability != DetermineAvailability(eligibleReleases, playableReleaseCount))
            throw new InvalidOperationException("Materialized title availability must match eligible release state.");

    }

    internal static void AssertReleaseInvariants(IEnumerable<MaterializedLibraryRelease> releases)
    {
        foreach (var release in releases)
        {
            if (!release.IsEligible && !release.IsBlocked)
                throw new InvalidOperationException("Ineligible materialized releases must be blocked.");

            if (release.IsBlocked && release.IsEligible)
                throw new InvalidOperationException("Blocked materialized releases must not be eligible.");

            if (release.IsBlocked && release.BlockReason is null)
                throw new InvalidOperationException("Blocked materialized releases require a block reason.");

            if (!release.IsBlocked && release.BlockReason is not null)
                throw new InvalidOperationException("Unblocked materialized releases must not carry a block reason.");

            if (release.IsPlayable != (release.IsEligible && !release.IsBlocked && release.IsOwned && release.IsComplete))
                throw new InvalidOperationException("Materialized release playability requires eligibility, ownership, completeness, and no block.");

            if (release.IsExposed && (!release.IsEligible || release.IsBlocked))
                throw new InvalidOperationException("Materialized release exposure requires eligibility and no block.");

            if (release.IsExposed && release.ExposureReason != ExposedDefaultReason)
                throw new InvalidOperationException("Exposed materialized releases must carry the default exposure reason.");

            if (!release.IsExposed && release.ExposureReason is null)
                throw new InvalidOperationException("Unexposed materialized releases require an exposure reason.");
        }
    }
}
