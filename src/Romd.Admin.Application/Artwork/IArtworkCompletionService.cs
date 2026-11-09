using ErrorOr;
using Romd.Contracts.Management.Artwork;

namespace Romd.Admin.Application.Artwork;

public interface IArtworkCompletionService
{
    Task<ErrorOr<ArtworkCompletionPreview>> PreviewScopeAsync(ArtworkCompletionRequest request, CancellationToken ct);
    Task<IReadOnlyList<ArtworkCompletionJob>> RecentAsync(CancellationToken ct);
    Task<ErrorOr<ArtworkCompletionItems>> ItemsAsync(Guid jobId, int afterTitleId, string? status, CancellationToken ct, int limit = 50);
    Task<ErrorOr<ArtworkCompletionPreview>> PreviewAsync(IReadOnlyList<int> titleIds, CancellationToken ct);
    Task<ErrorOr<ArtworkCompletionJob>> GetAsync(Guid jobId, CancellationToken ct);
}
