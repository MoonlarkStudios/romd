namespace Romd.Hosting;

public sealed class BrowserPlaybackOptions
{
    public const string SectionName = "Romd:BrowserPlayback";

    public BrowserPlaybackPlayerOriginOptions[] PlayerOrigins { get; init; } = [];

    public string? DefaultPlayerOrigin { get; init; }

    public string? ResolvePlayerOrigin(string requestOrigin)
    {
        BrowserPlaybackPlayerOriginOptions? match = PlayerOrigins.FirstOrDefault(
            configured => string.Equals(configured.Parent, requestOrigin, StringComparison.Ordinal));

        return match?.Player ?? NormalizeOptionalOrigin(DefaultPlayerOrigin);
    }

    public void Validate()
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < PlayerOrigins.Length; index++)
        {
            BrowserPlaybackPlayerOriginOptions origin = PlayerOrigins[index]
                ?? throw new InvalidOperationException(
                    $"{SectionName}:PlayerOrigins:{index} must define a parent/player origin mapping.");

            ValidateCanonicalOrigin(origin.Parent, $"{SectionName}:PlayerOrigins:{index}:Parent");
            ValidateCanonicalOrigin(origin.Player, $"{SectionName}:PlayerOrigins:{index}:Player");

            if (!parents.Add(origin.Parent))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:PlayerOrigins contains duplicate parent origin '{origin.Parent}'.");
            }
        }

        string? defaultPlayerOrigin = NormalizeOptionalOrigin(DefaultPlayerOrigin);
        if (defaultPlayerOrigin is not null)
        {
            ValidateCanonicalOrigin(defaultPlayerOrigin, $"{SectionName}:DefaultPlayerOrigin");
        }
    }

    private static string? NormalizeOptionalOrigin(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static void ValidateCanonicalOrigin(string value, string configurationKey)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(value, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{configurationKey} must be a canonical HTTP(S) origin without credentials, path, query, " +
                $"fragment, trailing slash, or an explicit default port. Value: '{value}'.");
        }
    }
}

public sealed class BrowserPlaybackPlayerOriginOptions
{
    public string Parent { get; init; } = string.Empty;

    public string Player { get; init; } = string.Empty;
}
