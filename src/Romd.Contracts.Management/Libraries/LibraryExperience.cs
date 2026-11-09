namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryAttachment(
    string CollectionId, string Name, string? Description, string? CoverUrl,
    int SortOrder, bool IsFeatured, int VisibleCount, int TotalCount, int LibraryCount);

public sealed record LibraryAttachmentRequest(string CollectionId, bool IsFeatured);
public sealed record SetLibraryAttachmentsRequest(IReadOnlyList<LibraryAttachmentRequest> Collections);

public sealed record LibraryPreviewTitle(string Id, string Name, string PlatformName, string? CoverUrl);
public sealed record LibraryPreview(IReadOnlyList<LibraryPreviewTitle> Items, string? NextCursor);

public sealed record CollectionLibraryPlacement(string LibraryId, string Name, bool IsFeatured);
