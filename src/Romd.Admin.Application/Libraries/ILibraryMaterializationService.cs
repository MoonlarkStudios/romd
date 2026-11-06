namespace Romd.Admin.Application.Libraries;

/// <summary>
///     Orchestrates the materialization of a library's contents.
/// </summary>
public interface ILibraryMaterializationService
{
    /// <summary>
    ///     Materializes a single library: loads candidates, runs 1G1R, replaces materialized items.
    /// </summary>
    Task<MaterializationResult> MaterializeAsync(int libraryId, CancellationToken ct = default);

    /// <summary>
    ///     Flags all libraries that include the given platform for rematerialization.
    /// </summary>
    Task FlagAffectedLibrariesAsync(int? platformId, CancellationToken ct = default);
}

/// <summary>
///     How a materialization run ended: publication, or deferral because inputs are not stable.
/// </summary>
public enum MaterializationOutcome
{
    Materialized = 0,
    Deferred = 1
}

/// <summary>
///     Result of a materialization run.
/// </summary>
public sealed record MaterializationResult(
    int IncludedReleaseCount,
    int ExcludedTitleCount,
    int TotalTitleCount,
    MaterializationOutcome Outcome = MaterializationOutcome.Materialized)
{
    /// <summary>
    ///     A run that could not publish because the catalog is pending or inputs changed; zero published counts.
    /// </summary>
    public static MaterializationResult Deferred() =>
        new(0, 0, 0, MaterializationOutcome.Deferred);
}
