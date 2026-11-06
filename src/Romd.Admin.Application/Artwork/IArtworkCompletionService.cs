using ErrorOr;
using Romd.Contracts.Management.Artwork;

namespace Romd.Admin.Application.Artwork;

public interface IArtworkCompletionService
{
    Task<ErrorOr<ArtworkCompletionPreviewDto>> PreviewScopeAsync(ArtworkCompletionRequest request, CancellationToken ct);
    Task<IReadOnlyList<ArtworkCompletionJobDto>> RecentAsync(CancellationToken ct);
    Task<ErrorOr<ArtworkCompletionItemsDto>> ItemsAsync(Guid jobId, int afterTitleId, string? status, CancellationToken ct, int limit = 50);
    Task<ErrorOr<ArtworkCompletionPreviewDto>> PreviewAsync(IReadOnlyList<int> titleIds, CancellationToken ct);
    Task<ErrorOr<ArtworkCompletionJobDto>> GetAsync(Guid jobId, CancellationToken ct);
}
