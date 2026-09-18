namespace Romd.Hosting;

public sealed class RomdHostSurfaceOptions
{
    public const string AdminSectionName = "Romd:AdminHost";
    public const string ConsumerSectionName = "Romd:ConsumerHost";

    // Distinct per-surface audiences are the security boundary between admin and consumer tokens;
    // these are non-optional defaults so a missing config value cannot collapse both to one audience.
    public const string DefaultAdminAudience = "romd-admin";
    public const string DefaultConsumerAudience = "romd-consumer";

    public string? JwtAudience { get; init; }

    /// <summary>
    ///     Absolute public origin for this surface (e.g. <c>https://console.example.com</c>), used as the
    ///     OpenIddict issuer so discovery metadata, authorize/token/device endpoints, and the device-flow
    ///     verification URI are advertised at the public origin rather than the internal container origin.
    ///     Behind a reverse proxy this should match the SPA redirect URIs and CORS origins for this surface.
    /// </summary>
    public string? PublicUrl { get; init; }

    public string[] CorsOrigins { get; init; } = [];

    public bool AllowCorsCredentials { get; init; } = true;

    public static RomdHostSurfaceOptions FromConfiguration(
        IConfiguration? configuration,
        string sectionName,
        IHostEnvironment environment,
        IReadOnlyCollection<string> developmentCorsOrigins)
    {
        var options = configuration
            ?.GetSection(sectionName)
            .Get<RomdHostSurfaceOptions>() ?? new RomdHostSurfaceOptions();

        if (options.CorsOrigins.Length > 0 || !environment.IsDevelopment())
        {
            return options;
        }

        return new RomdHostSurfaceOptions
        {
            JwtAudience = options.JwtAudience,
            PublicUrl = options.PublicUrl,
            CorsOrigins = developmentCorsOrigins.ToArray(),
            AllowCorsCredentials = options.AllowCorsCredentials
        };
    }
}
