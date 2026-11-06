namespace Romd.Contracts.Management.Enrichment;

public sealed record SetPlatformFieldDefaultsRequest
{
    public required IReadOnlyDictionary<string, string?> Defaults { get; init; }
}
