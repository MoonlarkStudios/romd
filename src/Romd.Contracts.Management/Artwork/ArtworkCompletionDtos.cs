using System.Text.Json.Serialization;
using Romd.Contracts.Management.Enrichment;

namespace Romd.Contracts.Management.Artwork;

public sealed record ArtworkCompletionRequest(Guid RequestId, IReadOnlyList<string>? TitleIds = null,
    string Scope = "Selected", ArtworkCompletionFilterDto? Filters = null, Guid? RetryJobId = null, string? RetryStatus = null, string Mode = "Artwork");
// Enrichment is omitted from JSON when null so accepted-run signatures that predate it stay byte-identical.
public sealed record ArtworkCompletionFilterDto(string? Query = null, string? SystemKey = null, string? Genre = null,
    string? EnrichmentStatus = null, string Tracked = "all", string ReleaseCompleteness = "all", string? ArtworkStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Enrichment = null);
public sealed record ArtworkRoleCountDto(string Role, string Status, int Count);
public sealed record ArtworkCompletionPreviewDto(int Titles, IReadOnlyList<ArtworkRoleCountDto> Roles);
/// <summary>
///     Phase, Total and Processed are the job's own execution history. Review is current; Failed, Unfinished and
///     Working are recorded at the continuation leaf. Review and Failed may overlap, so the counts are not a partition.
/// </summary>
public sealed record ArtworkCompletionJobDto(Guid JobId, string Phase, int Total, int Processed,
    IReadOnlyList<ArtworkRoleCountDto> Roles, int Review, int Failed, int Unfinished, int Working, DateTimeOffset? NextAttemptAt,
    string Mode = "Artwork", DateTimeOffset? CreatedAt = null);
/// <summary>Whether the continuation leaf finished the item, or stopped without finishing it.</summary>
public enum ArtworkCompletionState { Working, Unfinished, Done }
/// <summary>The recorded metadata execution outcome. Success means the step ran, not that metadata is complete.</summary>
public enum ArtworkCompletionMetadataOutcome { Succeeded, Failed, NotAttempted }
/// <summary>
///     Roles and MetadataOutcome are recorded at the continuation leaf; a null MetadataOutcome is unknown history.
///     Now is the title's current assessment, or null when the title no longer exists.
/// </summary>
public sealed record ArtworkCompletionItemDto(string TitleId, string TitleName, ArtworkCompletionState Completion,
    IReadOnlyList<ArtworkRoleCountDto> Roles, ArtworkCompletionMetadataOutcome? MetadataOutcome, EnrichmentSummaryDto? Now, Guid? FollowUpJobId);
public sealed record ArtworkCompletionItemsDto(IReadOnlyList<ArtworkCompletionItemDto> Items, string? NextCursor);
