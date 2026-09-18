namespace Romd.Domain.Catalog;

/// <summary>
///     Categorizes metadata sources by type. Priority is configured separately via EnrichmentOptions.
/// </summary>
public enum MetadataSourceType
{
    /// <summary>
    ///     Unknown or unspecified source.
    /// </summary>
    Unknown = 0,

    /// <summary>
    ///     Derived from filename or DAT file parsing.
    /// </summary>
    Parsing = 1,

    /// <summary>
    ///     Automated scrapers (e.g., ScreenScraper).
    /// </summary>
    Scraper = 2,

    /// <summary>
    ///     API providers (e.g., IGDB, MobyGames).
    /// </summary>
    Provider = 3,

    /// <summary>
    ///     Explicit user input. Always highest priority regardless of configuration.
    /// </summary>
    User = 4
}
