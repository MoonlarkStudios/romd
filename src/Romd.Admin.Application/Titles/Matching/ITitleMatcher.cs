namespace Romd.Admin.Application.Titles.Matching;

/// <summary>Shared limits for the catalog's bounded title-matching contract.</summary>
public static class TitleMatchingContract
{
    public const int MaxBatchSize = 500;
}

/// <summary>Matches one bounded batch of ordered game names to platform-specific canonical titles.</summary>
public interface ITitleMatcher
{
    /// <summary>
    ///     Returns one flushed title match per input position, in input order. Matching uses
    ///     normalized-name equality only. When more than one input normalizes to a missing title,
    ///     the first raw name supplies its display name and is the only position reported created.
    /// </summary>
    Task<IReadOnlyList<TitleMatch>> MatchOrCreateBatchAsync(
        int platformId,
        IReadOnlyList<string> gameNames,
        CancellationToken cancellationToken = default);
}

/// <summary>A persisted title id and whether this input position created it.</summary>
public sealed record TitleMatch(int TitleId, bool TitleWasCreated);
