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
        ArtworkRole.Backdrop => width >= 1200 && height >= 720 && (double)width / height is >= 1.3 and <= 2.1,
        ArtworkRole.Logo => width >= 128 && height >= 64,
        _ => false
    };

    public static double CropCost(ArtworkRole role, int width, int height) => role switch
    {
        ArtworkRole.Hero => Math.Abs(Math.Log((double)width / height / (1920d / 620))),
        ArtworkRole.Backdrop => Math.Abs(Math.Log((double)width / height / (16d / 9))),
        _ => 0
    };
}
