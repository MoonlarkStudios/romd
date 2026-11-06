namespace Romd.Infrastructure.Enrichment;

/// <summary>
///     Provider-specific credentials for Twitch/IGDB metadata enrichment.
/// </summary>
public sealed class IgdbProviderOptions
{
    public const string SectionName = "Providers:Igdb";

    /// <summary>
    ///     Twitch/IGDB Client ID for metadata enrichment.
    ///     Get credentials from: https://dev.twitch.tv/console.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    ///     Twitch/IGDB Client Secret for metadata enrichment.
    /// </summary>
    public string? ClientSecret { get; set; }
}
