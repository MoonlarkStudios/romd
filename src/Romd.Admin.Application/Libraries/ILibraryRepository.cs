using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries;

public interface ILibraryRepository
{
    Task<ErrorOr<PagedList<Library>>> GetPageAsync(string? query, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<PagedList<GenreFacet>>> GetGenrePageAsync(int libraryId, int minimumItems, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<PagedList<CollectionFacet>>> GetCollectionPageAsync(int libraryId, int minimumItems, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<PagedList<LibraryTitleReleaseDiagnostics>>> GetReleaseDiagnosticsPageAsync(int libraryId, int titleId, string? cursor, int limit, CancellationToken ct);

    Task<Library?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Library?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<Library?> GetDefaultAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Library>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    ///     Stages a library row without saving. The caller owns the flush and commit.
    /// </summary>
    Task AddStagedAsync(Library library, CancellationToken ct = default);

    /// <summary>
    ///     Stages a library update without saving. The caller owns the flush and commit.
    /// </summary>
    Task UpdateStagedAsync(Library library, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
    Task ClearDefaultAsync(CancellationToken ct = default);
    Task<string?> ValidateConfigurationReferencesAsync(LibraryConfiguration configuration, CancellationToken ct = default, int? existingLibraryId = null);
    Task MarkConfigurationInvalidAsync(int libraryId, string? error, CancellationToken ct = default);
    Task<IReadOnlyList<Library>> GetNeedingMaterializationAsync(CancellationToken ct = default);
    /// <summary>
    ///     Requests a refresh without revoking published access and returns the current state.
    ///     Executes immediately within the required caller-owned transaction.
    /// </summary>
    Task<Library?> FlagForRematerializationAsync(int libraryId, CancellationToken ct = default);
    /// <summary>Marks additive ROM availability changes without revoking published access.</summary>
    Task MarkUpdatesAvailableByPlatformAsync(int platformId, CancellationToken ct = default);
    Task FlagAllForRematerializationAsync(CancellationToken ct = default);
    Task FlagForRematerializationByPlatformAsync(int platformId, CancellationToken ct = default);
    /// <summary>
    ///     Stages projection publication inside the caller's active transaction. Keep the returned
    ///     receipt alive through the caller's flush and commit or rollback, then dispose it.
    /// </summary>
    Task<IStagedLibraryPublication> StageMaterializedProjectionsAndActivateAsync(
        int libraryId,
        MaterializedLibraryProjection projection,
        int itemCount,
        Guid materializationToken,
        CancellationToken ct = default);
    /// <summary>
    ///     Withdraws a projection immediately inside the required caller-owned transaction.
    ///     The caller owns the flush and commit, including any event intents.
    /// </summary>
    Task<bool> StageMaterializedProjectionsAndMarkConfigurationInvalidAsync(
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

/// <summary>
///     A publication's staged state. Disposal releases only publication-owned tracking; it never
///     flushes or commits. Dispose after the owning transaction has committed or rolled back.
/// </summary>
public interface IStagedLibraryPublication : IDisposable
{
    bool Applied { get; }
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
    string? ExposureReason,
    string? VfsPathError = null);
