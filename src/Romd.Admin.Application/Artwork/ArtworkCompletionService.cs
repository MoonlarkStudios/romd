using ErrorOr;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
namespace Romd.Admin.Application.Artwork;

/// <summary>Validates read intent and exposes completion previews and run feedback.</summary>
public sealed class ArtworkCompletionService(IArtworkCompletionStore store) : IArtworkCompletionService
{
    // Run filters share one predicate each; retry takes only the recorded ones that execution can repeat.
    private static bool ValidItemStatus(string? status) => status is null or "Review" or "Failed" or "Unfinished" or "Working";
    public Task<ErrorOr<ArtworkCompletionPreviewDto>> PreviewScopeAsync(ArtworkCompletionRequest request, CancellationToken ct) => ArtworkCompletionIntent.IsValid(request) ? store.PreviewScopeAsync(request, ct) : Task.FromResult<ErrorOr<ArtworkCompletionPreviewDto>>(ArtworkCurationErrors.InvalidRequest());
    public Task<IReadOnlyList<ArtworkCompletionJobDto>> RecentAsync(CancellationToken ct) => store.RecentAsync(ct);
    public Task<ErrorOr<ArtworkCompletionItemsDto>> ItemsAsync(Guid jobId, int afterTitleId, string? status, CancellationToken ct, int limit = 50) => ValidItemStatus(status) && limit is >= 1 and <= 100 ? store.ItemsAsync(jobId, afterTitleId, status, ct, limit) : Task.FromResult<ErrorOr<ArtworkCompletionItemsDto>>(ArtworkCurationErrors.InvalidRequest());
    public Task<ErrorOr<ArtworkCompletionPreviewDto>> PreviewAsync(IReadOnlyList<int> titleIds, CancellationToken ct) => PreviewScopeAsync(new(Guid.NewGuid(), titleIds.Select(IdCoder.Encode).ToArray()), ct);
    public Task<ErrorOr<ArtworkCompletionJobDto>> GetAsync(Guid jobId, CancellationToken ct) => store.GetAsync(jobId, ct);
}
