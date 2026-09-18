using ErrorOr;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries;

public interface ILibraryExperienceRepository
{
    Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetCollectionIdsByLibraryAsync(CancellationToken ct);
    Task<IReadOnlyList<CollectionLibraryPlacementDto>> GetCollectionPlacementsAsync(int collectionId, CancellationToken ct);
    Task<IReadOnlyList<LibraryAttachmentDto>> GetAttachmentsAsync(int libraryId, CancellationToken ct);
    /// <summary>Stages a replacement inside the caller-owned transaction. The caller commits.</summary>
    Task<ErrorOr<Success>> SetAttachmentsAsync(int libraryId, IReadOnlyList<(int CollectionId, bool IsFeatured)> items, CancellationToken ct);
    Task<LibraryPreviewDto> GetPreviewAsync(int libraryId, int? collectionId, int afterId, int afterOrder, CancellationToken ct);
}
