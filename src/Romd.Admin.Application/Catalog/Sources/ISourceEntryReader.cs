using Romd.Application.Common.Pagination;
using ErrorOr;

namespace Romd.Admin.Application.Catalog.Sources;

public sealed record SourceEntryReference(int GameId, int EntryId, string Name, int? TitleId, string? TitleName,
    bool HasLocalPayload, int ActiveSources);
public sealed record SourceEntryPage(IReadOnlyList<SourceEntryReference> Items, int? NextCursor);
public interface ISourceEntryReader
{
    /// <summary>The largest page the operation serves; the endpoint publishes and enforces it.</summary>
    public const int MaxLimit = 100;

    Task<ErrorOr<PagedList<SourceEntryReference>>> ReadAsync(int datId, string? cursor, int limit, string? query, int? titleId, CancellationToken ct);
}
