using System.Text.Json.Serialization;
using Romd.Contracts.Common.Serialization;

namespace Romd.Contracts.Management.Artwork;

public sealed record ArtworkProvider(string Id, string Name, bool IsAvailable,
    bool SupportsLanguageFilter, IReadOnlyList<ArtworkRoleCapability> Roles,
    string? GameId = null, string? GameName = null, string? GameUrl = null, IReadOnlyList<string>? MediaTypes = null);
public sealed record ArtworkRoleCapability(string Role, IReadOnlyList<string> Dimensions, IReadOnlyList<string> Styles);
/// <summary>
///     A browsed candidate. <see cref="Reference" /> is the signed proof a write sends in its body;
///     <see cref="CandidateId" /> is the candidate's stable identity in routes.
/// </summary>
public sealed record ArtworkCandidate(string Reference, string CandidateId, string ProviderAssetId, string Role,
    int Width, int Height, string? Style, string? Attribution, string SourcePageUrl, string? MediaType = null, bool IsSaved = false);
public sealed record PreviewArtworkRequest([property: JsonRequired] string CandidateReference);

/// <summary>Imports a candidate into the role named by the route; the role's validator travels in <c>If-Match</c>.</summary>
public sealed record ApplyArtworkRequest([property: JsonRequired] Guid RequestId,
    [property: JsonRequired] string CandidateReference, int FocalX = 50, int FocalY = 50);

public sealed record SavedArtwork(string Id, string Role, string SourceId, string? Attribution,
    string? SourcePageUrl, IReadOnlyList<string> MediaIds, Romd.Contracts.Common.Artwork.ResolvedArtwork Artwork);

/// <summary>
///     One artwork role's selection. <see cref="Revision" /> is the role's validator, also sent as its ETag.
/// </summary>
public sealed record ArtworkSelectionState(string Role, ArtworkMode Mode,
    [property: JsonConverter(typeof(CanonicalInt64StringJsonConverter))] long Revision,
    string? PinnedAssetId, Guid? PendingJobId);

/// <summary>
///     A title's artwork family: every role's selection, and <see cref="Revision" />, the family validator built
///     from all the role revisions and sent as the ETag of the family's collections.
/// </summary>
public sealed record TitleArtwork(string Revision, IReadOnlyList<ArtworkSelectionState> Roles);

/// <summary>How a role chooses its artwork.</summary>
public enum ArtworkMode
{
    Automatic,
    Empty,
    Pinned
}

/// <summary>
///     The whole state of one role. <see cref="ArtworkMode.Pinned" /> names exactly one saved asset or title media
///     item and may place its focal point; the other modes take neither.
/// </summary>
public sealed record ReplaceArtworkSelectionRequest
{
    public required ArtworkMode Mode { get; init; }
    public string? AssetId { get; init; }
    public string? MediaId { get; init; }
    public int? FocalX { get; init; }
    public int? FocalY { get; init; }
}

public sealed record ImportGalleryArtworkRequest([property: JsonRequired] string CandidateReference);

public sealed record HideArtworkCandidateRequest([property: JsonRequired] string CandidateReference);

public sealed record RejectedArtwork(string Id, string Role, string SourceId, string? ProviderAssetId);
