namespace Romd.Contracts.Common.Artwork;

/// <summary>A bounded delivery image with an immutable, versioned URL.</summary>
public sealed record ArtworkVariant(
    string Name,
    string Url,
    string ContentVersion,
    string ContentType,
    int Width,
    int Height);
