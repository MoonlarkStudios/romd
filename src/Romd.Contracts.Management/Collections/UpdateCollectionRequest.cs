namespace Romd.Contracts.Management.Collections;

public sealed record UpdateCollectionRequest(
    string Name,
    string? Description = null,
    string? SystemKey = null,
    string? CoverMediaId = null);
