namespace Romd.Contracts.Management.Models;

public sealed record ManagedSystem(string Key, string Name, string ShortName, string? Manufacturer,
    bool Enabled, string State, string? Message, int CatalogCount, int OwnedTitles, int TrackedTitles = 0);
public sealed record SetSystemEnabled(bool Enabled);
public sealed record SystemDatPreview(string Name, int Bytes, string? SuggestedSystemKey,
    IReadOnlyList<string> ExistingDatIds, DatReplacementPreview Preview);
