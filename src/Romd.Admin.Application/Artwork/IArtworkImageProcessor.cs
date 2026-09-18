using ErrorOr;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork;

public interface IArtworkImageProcessor
{
    Task<ErrorOr<ProcessedArtworkImage>> ProcessAsync(ReadOnlyMemory<byte> original, ArtworkRole role,
        CancellationToken ct = default);
}

/// <summary>Measured display dimensions honor EXIF orientation; original bytes remain unchanged with the caller.</summary>
public sealed record ProcessedArtworkImage(string ContentType, int Width, int Height,
    IReadOnlyList<ProcessedArtworkVariant> Variants);

public sealed record ProcessedArtworkVariant(string Name, string ContentType, int Width, int Height,
    byte[] EncodedBytes);

public static class ArtworkImageErrors
{
    public static Error Invalid() => Error.Validation("Artwork.InvalidImage", "Artwork must be a complete static JPEG, PNG, or WebP image.");
    public static Error TooLarge() => Error.Validation("Artwork.ImageTooLarge", "Artwork exceeds the encoded size or decoded dimension limits.");
    public static Error UnsupportedRole() => Error.Validation("Artwork.UnsupportedRole", "This artwork role is not supported.");
    public static Error ProcessingFailed() => Error.Failure("Artwork.ImageProcessingFailed", "Artwork could not be processed.");
}
