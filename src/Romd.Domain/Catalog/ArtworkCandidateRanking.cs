namespace Romd.Domain.Catalog;

public static class ArtworkCandidateRanking
{
    // Covers may be portrait, square, or landscape packaging. Heroes need enough
    // landscape area to survive the wide display crop without upscaling. Logos are
    // never cropped, so their natural aspect ratio is intentionally unconstrained.
    public static bool IsSuitable(ArtworkRole role, int width, int height) => role switch
    {
        ArtworkRole.Poster => width >= 250 && height >= 250 && (double)width / height is >= 0.45 and <= 2.2,
        ArtworkRole.Hero => width >= 960 && height >= 400 && (double)width / height is >= 1.5 and <= 4,
        ArtworkRole.Backdrop => BackdropRetainedFraction(width, height) >= 0.75 &&
            Math.Min(width, height * (16d / 9)) >= 1280 && Math.Min(height, width * (9d / 16)) >= 720,
        ArtworkRole.Logo => width >= 128 && height >= 64,
        _ => false
    };

    // Suitability is a gate. Within a preference tier the provider's ordering is
    // authoritative; tiny aspect-ratio differences and asset IDs are not quality signals.
    public static int Preference(ArtworkRole role, string? style) =>
        role == ArtworkRole.Logo && style != "official" ? 1 : 0;

    // Fit tier first, then bounded cropping; preserve provider order within each tier.
    public static int BackdropPreference(int width, int height) =>
        BackdropRetainedFraction(width, height) >= 0.95 ? 0 : 1;

    private static double BackdropRetainedFraction(int width, int height) =>
        width <= 0 || height <= 0 ? 0 : Math.Min((double)width / height / (16d / 9), (16d / 9) / ((double)width / height));

    public static double CropCost(ArtworkRole role, int width, int height) => role switch
    {
        ArtworkRole.Poster => Math.Abs(Math.Log((double)width / height / (2d / 3))),
        ArtworkRole.Hero => Math.Abs(Math.Log((double)width / height / (1920d / 620))),
        ArtworkRole.Backdrop => Math.Abs(Math.Log((double)width / height / (16d / 9))),
        _ => 0
    };
}
