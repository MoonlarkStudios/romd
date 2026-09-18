using ErrorOr;

namespace Romd.Admin.Application.Catalog.Sources;

public sealed record SourceEntryReference(int GameId, int EntryId, string Name, int? TitleId, string? TitleName,
    bool HasLocalPayload, int ActiveSources);
public sealed record SourceEntryPage(IReadOnlyList<SourceEntryReference> Items, int? NextCursor);
public interface ISourceEntryReader
{
    Task<ErrorOr<SourceEntryPage>> ReadAsync(int datId, int? after, int limit, string? search, int? titleId, CancellationToken ct);
}
