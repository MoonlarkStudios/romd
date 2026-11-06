namespace Romd.Contracts.Management.Collections;

public sealed record ReorderCollectionItemsRequest(IReadOnlyList<string> TitleIds);
