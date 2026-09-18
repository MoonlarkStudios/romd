namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryAttachmentDto(
    string CollectionId, string Name, string? Description, string? CoverUrl,
    int SortOrder, bool IsFeatured, int VisibleCount, int TotalCount, int LibraryCount);

public sealed record LibraryAttachmentRequest(string CollectionId, bool IsFeatured);
public sealed record SetLibraryAttachmentsRequest(IReadOnlyList<LibraryAttachmentRequest> Collections);

public sealed record LibraryPreviewTitleDto(string Id, string Name, string PlatformName, string? CoverUrl);
public sealed record LibraryPreviewDto(IReadOnlyList<LibraryPreviewTitleDto> Items, string? NextCursor);

public sealed record CollectionLibraryPlacementDto(string LibraryId, string Name, bool IsFeatured);
