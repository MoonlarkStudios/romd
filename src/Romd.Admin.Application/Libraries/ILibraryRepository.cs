using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries;

public interface ILibraryRepository
{
    Task<Library?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Library?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<Library?> GetDefaultAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Library>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    ///     Stages a library row without saving. The caller owns the flush and commit.
    /// </summary>
    Task AddStagedAsync(Library library, CancellationToken ct = default);

    Task UpdateAsync(Library library, CancellationToken ct = default);

    /// <summary>
    ///     Stages a library update without saving. The caller owns the flush and commit.
    /// </summary>
    Task UpdateStagedAsync(Library library, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
    Task SetDefaultAsync(int libraryId, CancellationToken ct = default);
    Task ClearDefaultAsync(CancellationToken ct = default);
    Task<string?> ValidateConfigurationReferencesAsync(LibraryConfiguration configuration, CancellationToken ct = default);
    Task MarkConfigurationInvalidAsync(int libraryId, string? error, CancellationToken ct = default);
    Task<IReadOnlyList<Library>> GetNeedingMaterializationAsync(CancellationToken ct = default);
    /// <summary>
    ///     Flags one library without rewriting configuration and returns its current state.
    ///     Executes immediately within the required caller-owned transaction.
    /// </summary>
    Task<Library?> FlagForRematerializationAsync(int libraryId, CancellationToken ct = default);
    Task FlagAllForRematerializationAsync(CancellationToken ct = default);
    Task FlagForRematerializationByPlatformAsync(int platformId, CancellationToken ct = default);
    Task<bool> TryReplaceMaterializedProjectionsAndActivateAsync(
        int libraryId,
        MaterializedLibraryProjection projection,
        int itemCount,
        Guid materializationToken,
        CancellationToken ct = default);
    Task<bool> TryReplaceMaterializedProjectionsAndMarkConfigurationInvalidAsync(
        int libraryId,
        string? error,
        Guid materializationToken,
        CancellationToken ct = default);

    Task<LibraryContentCounts> GetContentCountsAsync(int libraryId, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformFacet>> GetPlatformFacetsAsync(int libraryId, int minimumItems = 1, CancellationToken ct = default);
    Task<IReadOnlyList<GenreFacet>> GetGenreFacetsAsync(int libraryId, int minimumItems = 1, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionFacet>> GetCollectionFacetsAsync(int libraryId, int minimumItems = 1, CancellationToken ct = default);
    Task<IReadOnlyList<LibraryTitleReleaseDiagnostics>> GetTitleReleaseDiagnosticsAsync(
        int libraryId,
        int titleId,
        CancellationToken ct = default);
}

public sealed record LibraryContentCounts(int OwnedTitleCount, int AvailableTitleCount, int CollectionCount);
public sealed record PlatformFacet(int PlatformId, string PlatformName, int Count);
public sealed record GenreFacet(string Genre, int Count);
public sealed record CollectionFacet(int CollectionId, string CollectionName, int MatchingCount, int TotalCount);
public sealed record LibraryTitleReleaseDiagnostics(
    int ReleaseId,
    int DatFileId,
    string Name,
    bool IsEligible,
    bool IsBlocked,
    string? BlockReason,
    bool IsExposed,
    string? ExposureReason);
