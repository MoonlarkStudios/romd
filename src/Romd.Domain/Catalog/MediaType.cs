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
    TitleScreen = 8,

    /// <summary>Front of a retail box, kept at its scanned proportions.</summary>
    BoxFront = 9,

    /// <summary>Back of a retail box.</summary>
    BoxBack = 10,

    /// <summary>Box spine.</summary>
    BoxSpine = 11,

    /// <summary>Full box wrap: back, spine and front in one scan.</summary>
    BoxFull = 12,

    /// <summary>Front label of a cartridge or card.</summary>
    CartridgeFront = 13,

    /// <summary>Back of a cartridge or card.</summary>
    CartridgeBack = 14,

    /// <summary>Printed label of a disc.</summary>
    DiscLabel = 15,

    /// <summary>Game manual, stored as one PDF document.</summary>
    Manual = 16
}

/// <summary>How each media type is collected, shown and delivered.</summary>
public static class MediaTypes
{
    /// <summary>Physical packaging scans: release-scoped, shown at their original proportions, one optional preference.</summary>
    public static bool IsPackaging(MediaType type) => type is MediaType.BoxFront or MediaType.BoxBack or
        MediaType.BoxSpine or MediaType.BoxFull or MediaType.CartridgeFront or MediaType.CartridgeBack or
        MediaType.DiscLabel;

    /// <summary>Documents are stored and delivered as PDFs, never through the image pipeline.</summary>
    public static bool IsDocument(MediaType type) => type == MediaType.Manual;

    /// <summary>
    ///     The types the consumer surface has always listed and served. Packaging and documents are curation
    ///     collections; adding them to consumer contracts is a separate, versioned change.
    /// </summary>
    public static bool IsConsumerVisible(MediaType type) => type is >= MediaType.Cover and <= MediaType.TitleScreen;

    /// <summary>The persisted names of <see cref="IsConsumerVisible" /> types, for query filters.</summary>
    public static IReadOnlyList<string> ConsumerVisibleNames { get; } = Enum.GetValues<MediaType>()
        .Where(IsConsumerVisible).Select(type => type.ToString()).ToArray();
}
