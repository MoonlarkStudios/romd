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
    ///     Stages one title match per input position, in input order. The derivation batch owner
    ///     flushes required identities before reading matches. Matching uses
    ///     normalized-name equality only. When more than one input normalizes to a missing title,
    ///     the first raw name supplies its display name and is the only position reported created.
    /// </summary>
    Task<IStagedTitleMatchBatch> MatchOrStageBatchAsync(
        int platformId,
        IReadOnlyList<string> gameNames,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Owned ordered match facts. Existing-only matches are immediately readable; new identities
///     require the owner's flush and retain their creation receipt's transaction/staging lifetime.
/// </summary>
public interface IStagedTitleMatchBatch
{
    bool RequiresFlush { get; }
    IReadOnlyList<TitleMatch> ReadAfterFlush();
}

/// <summary>A persisted title id and whether this input position created it.</summary>
public sealed record TitleMatch(int TitleId, bool TitleWasCreated);
