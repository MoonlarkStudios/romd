namespace Romd.Domain.Catalog;

/// <summary>Presentation roles, independent of historical media classifications.</summary>
public enum ArtworkRole
{
    Poster = 1,
    Hero = 2,
    Logo = 3,
    Backdrop = 4
}

public enum ArtworkFit
{
    Contain = 1,
    Cover = 2
}

public enum ArtworkSelectionMode
{
    Automatic = 1,
    Pinned = 2
}

public enum ArtworkFallbackReason
{
    None = 0,
    LegacyCover = 1,
    NoArtwork = 2
}
