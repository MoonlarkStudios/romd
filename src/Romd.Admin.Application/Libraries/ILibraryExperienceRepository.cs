using Romd.Contracts.Common.Models;
using ErrorOr;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries;

public interface ILibraryExperienceRepository
{
    Task<ErrorOr<Page<CollectionLibraryPlacementDto>>> GetCollectionPlacementsPageAsync(int collectionId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<Page<LibraryPreviewTitleDto>>> GetPreviewPageAsync(int libraryId, int? collectionId, string? cursor, int limit, CancellationToken ct);

    Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetCollectionIdsByLibraryAsync(CancellationToken ct, IReadOnlyList<int>? libraryIds = null);
    Task<IReadOnlyList<CollectionLibraryPlacementDto>> GetCollectionPlacementsAsync(int collectionId, CancellationToken ct);
    Task<IReadOnlyList<LibraryAttachmentDto>> GetAttachmentsAsync(int libraryId, CancellationToken ct);
    /// <summary>Stages a replacement inside the caller-owned transaction. The caller commits.</summary>
    Task<ErrorOr<Success>> SetAttachmentsAsync(int libraryId, IReadOnlyList<(int CollectionId, bool IsFeatured)> items, CancellationToken ct);
    Task<LibraryPreviewDto> GetPreviewAsync(int libraryId, int? collectionId, int afterId, int afterOrder, CancellationToken ct);
}
