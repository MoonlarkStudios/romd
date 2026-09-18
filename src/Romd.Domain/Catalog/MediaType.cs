namespace Romd.Domain.Catalog;

/// <summary>
///     Type of media asset for a title.
/// </summary>
public enum MediaType
{
    /// <summary>
    ///     Box art / cover image (2D front cover).
    /// </summary>
    Cover = 1,

    /// <summary>
    ///     In-game screenshot.
    /// </summary>
    Screenshot = 2,

    /// <summary>
    ///     Banner image (wide format).
    /// </summary>
    Banner = 3,

    /// <summary>
    ///     Game logo (transparent wheel art).
    /// </summary>
    Logo = 4,

    /// <summary>
    ///     Background / fanart (1080p+ art for UI backdrop).
    /// </summary>
    Background = 5,

    /// <summary>
    ///     Video snap preview (typically MP4, under 30 seconds).
    /// </summary>
    Video = 6,

    /// <summary>
    ///     3D box art (angled box rendering).
    /// </summary>
    Box3d = 7,

    /// <summary>
    ///     Title screen / start screen screenshot.
    /// </summary>
    TitleScreen = 8
}
