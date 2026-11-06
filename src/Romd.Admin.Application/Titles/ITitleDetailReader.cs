using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Titles.ReadModels;

namespace Romd.Admin.Application.Titles;

/// <summary>
///     Read port for the title detail projection: title, releases, files, media, and release
///     preference. Use cases read it inside their transaction after flushing to build responses.
/// </summary>
public interface ITitleDetailReader
{
    Task<TitleDetailData?> GetPublicDetailAsync(int titleId, CancellationToken ct = default);
    Task<ErrorOr<TitleReadPage<TitleMediaData>>> GetMediaPageAsync(int titleId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<TitleReadPage<TitleReleaseData>>> GetReleasePageAsync(int titleId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<TitleReadPage<TitleReleaseSourceData>>> GetReleaseSourcesPageAsync(int titleId, string? releaseId, string? cursor, int limit, CancellationToken ct);

    /// <summary>Gets the title detail, or <c>null</c> when the title does not exist.</summary>
    Task<TitleDetailData?> GetDetailAsync(int titleId, CancellationToken cancellationToken = default);
}

public sealed record TitleReadPage<T>(Guid Revision, PagedList<T> Page);
