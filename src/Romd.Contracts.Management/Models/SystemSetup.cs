namespace Romd.Contracts.Management.Models;

public sealed record ManagedSystemDto(string Key, string Name, string ShortName, string? Manufacturer,
    IReadOnlyList<string> Aliases, bool Enabled, string State, string? Message, int CatalogCount, int OwnedTitles, int TrackedTitles = 0);
public sealed record SetSystemEnabled(bool Enabled);
public sealed record SystemDatPreviewDto(string Name, int Bytes, string? SuggestedSystemKey,
    IReadOnlyList<string> ExistingDatIds, DatReplacementPreview Preview);
