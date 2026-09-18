namespace Romd.Contracts.Management.Collections;

public sealed record AddCollectionItemRequest(
    string TitleId,
    string? Note = null);
