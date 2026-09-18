using System.Text.Json.Serialization;
using Romd.Contracts.Common.Serialization;

namespace Romd.Contracts.Management.Artwork;

public sealed record ArtworkProviderDto(string Id, string Name, bool IsAvailable,
    bool SupportsLanguageFilter, IReadOnlyList<ArtworkRoleCapabilityDto> Roles,
    string? GameId = null, string? GameName = null, string? GameUrl = null, IReadOnlyList<string>? MediaTypes = null);
public sealed record ArtworkRoleCapabilityDto(string Role, IReadOnlyList<string> Dimensions, IReadOnlyList<string> Styles);
public sealed record ArtworkProviderGameDto(string Id, string Name);
public sealed record ArtworkCandidateDto(string Reference, string ProviderAssetId, string Role,
    int Width, int Height, string? Style, string? Attribution, string SourcePageUrl, string? MediaType = null, bool IsSaved = false);
public sealed record ArtworkCandidatePageDto(IReadOnlyList<ArtworkCandidateDto> Items, string? NextCursor);
public sealed record PreviewArtworkRequest(string CandidateReference);
public sealed record ApplyArtworkRequest(Guid RequestId, string CandidateReference, int FocalX = 50, int FocalY = 50,
    [property: JsonConverter(typeof(CanonicalInt64StringJsonConverter))] long? ExpectedRevision = null);
public sealed record PinSavedArtworkRequest(string? AssetId, string? MediaId,
    [property: JsonConverter(typeof(CanonicalInt64StringJsonConverter))] long ExpectedRevision,
    int FocalX = 50, int FocalY = 50);
public sealed record SavedArtworkDto(string Id, string Role, string SourceId, string? Attribution,
    string? SourcePageUrl, IReadOnlyList<string> MediaIds, Romd.Contracts.Common.Artwork.ResolvedArtworkDto Artwork);
public sealed record ArtworkImportAcceptedDto(Guid JobId,
    [property: JsonConverter(typeof(CanonicalInt64StringJsonConverter))] long SelectionRevision);
public sealed record ArtworkAutomaticDto(
    [property: JsonConverter(typeof(CanonicalInt64StringJsonConverter))] long SelectionRevision);
public sealed record ArtworkSelectionStateDto(string Role, string Mode,
    [property: JsonConverter(typeof(CanonicalInt64StringJsonConverter))] long Revision,
    string? PinnedAssetId, Guid? PendingJobId);

public sealed record ImportGalleryArtworkRequest(string CandidateReference);
