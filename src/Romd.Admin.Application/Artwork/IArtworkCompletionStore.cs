using Romd.Domain.Catalog;
using ErrorOr;
using Romd.Contracts.Management.Artwork;
using Romd.Admin.Application.Ingestion.Jobs;
namespace Romd.Admin.Application.Artwork;

/// <summary>Completion previews, run feedback and worker ledger operations; never calls a provider.</summary>
public interface IArtworkCompletionStore
{
    Task<ErrorOr<ArtworkCompletionPreviewDto>> PreviewScopeAsync(ArtworkCompletionRequest request, CancellationToken ct);
    Task<IReadOnlyList<ArtworkCompletionJobDto>> RecentAsync(CancellationToken ct);
    Task<ErrorOr<ArtworkCompletionItemsDto>> ItemsAsync(Guid jobId, int afterTitleId, string? status, CancellationToken ct, int limit = 50);
    Task<ErrorOr<ArtworkCompletionJobDto>> GetAsync(Guid jobId, CancellationToken ct);
    Task<ArtworkCompletionWork?> NextAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome>> OutcomesAsync(Guid jobId, int titleId, CancellationToken ct);
    Task<int> CompletedCountAsync(Guid jobId, CancellationToken ct);
    /// <summary>Completes an item once. The metadata fallback fills unrecorded history and never lowers a recorded outcome.</summary>
    Task CompleteAsync(Guid jobId, int titleId, IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome> outcomes,
        MetadataExecutionOutcome metadata, JobContext context, CancellationToken ct);
    /// <summary>
    ///     Records the metadata step inside the caller's owned mutation. Writes only to an unfinished item of this job;
    ///     jobs without completion items write nothing.
    /// </summary>
    Task RecordMetadataOutcomeAsync(Guid jobId, int titleId, MetadataExecutionOutcome outcome, CancellationToken ct);
    Task<bool> IsTrackedAsync(int titleId, CancellationToken ct);
    Task<bool> HasFailuresAsync(Guid jobId, CancellationToken ct);
    /// <summary>
    ///     Resumes the title's completion work in the caller's identity transaction and returns the completion job
    ///     that will enrich it, or null when the title does not exist.
    /// </summary>
    Task<Guid?> ResumeTitleStagedAsync(int titleId, CancellationToken ct);
}
public sealed record ArtworkCompletionWork(int TitleId, string TitleName, bool RequireTracked, string Mode = "Artwork");
