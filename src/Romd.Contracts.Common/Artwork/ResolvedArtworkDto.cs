namespace Romd.Contracts.Common.Artwork;

/// <summary>
/// Server-resolved artwork for a presentation role. Width and Height describe
/// the default URL; original dimensions describe retained source content.
/// Missing dimensions remain null for historical media without measured sizes.
/// AssetId is an encoded public identifier. Variants exclude the original file.
/// </summary>
public sealed record ResolvedArtworkDto(
    string Role,
    string? AssetId,
    string? ContentVersion,
    string? Url,
    int? Width,
    int? Height,
    int? OriginalWidth,
    int? OriginalHeight,
    string Fit,
    string FallbackReason,
    IReadOnlyList<ArtworkVariantDto> Variants,
    int FocalX = 50,
    int FocalY = 50);
