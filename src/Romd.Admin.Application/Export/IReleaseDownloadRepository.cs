namespace Romd.Admin.Application.Export;

public sealed record ReleaseDownloadPlan(string Name, IReadOnlyList<ExportArchiveEntry> Entries, int MissingFiles);

public interface IReleaseDownloadRepository
{
    /// <summary>
    ///     The release's downloadable files when the user may download it: any release for an unrestricted user,
    ///     otherwise an owned, exposed release of the user's valid, materialized library. Otherwise null.
    /// </summary>
    Task<ReleaseDownloadPlan?> GetAsync(int releaseId, Guid userId, bool unrestricted, CancellationToken ct);

    /// <summary>
    ///     <see cref="GetAsync" /> for several releases in one read, keyed by release id. A release the user may not
    ///     download is absent.
    /// </summary>
    Task<IReadOnlyDictionary<int, ReleaseDownloadPlan>> GetManyAsync(IReadOnlyCollection<int> releaseIds, Guid userId,
        bool unrestricted, CancellationToken ct);
}
