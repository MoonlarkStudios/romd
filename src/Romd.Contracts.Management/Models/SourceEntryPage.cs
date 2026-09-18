namespace Romd.Contracts.Management.Models;

public sealed record SourceEntryReference(string GameId, string EntryId, string Name, string? TitleId, string? TitleName,
    bool HasLocalPayload, int ActiveSources);
public sealed record SourceEntryPage(IReadOnlyList<SourceEntryReference> Items, string? NextCursor);
