namespace Romd.Application.Common.Security;

public sealed record ConsumerWebAppRegistration(
    string ClientId,
    string DisplayName,
    string CallbackUri,
    string BrowserOrigin,
    DateTimeOffset? ProvisionedAt,
    DateTimeOffset? DisabledAt,
    DateTimeOffset? RetryProvisioningAt)
{
    public const string ClientIdPrefix = "romd-web-";

    public static bool IsClientId(string? clientId) =>
        clientId is not null && clientId.Length == ClientIdPrefix.Length + 32 &&
        clientId.StartsWith(ClientIdPrefix, StringComparison.Ordinal) &&
        clientId.AsSpan(ClientIdPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    public static bool IsValidDisplayName(string? name) =>
        name is { Length: > 0 and <= 120 } && !string.IsNullOrWhiteSpace(name) &&
        !name.Any(character => char.IsControl(character) ||
            char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format);

    public static bool TryReadCallback(string? value, out Uri uri) =>
        TryReadHttpsUri(value, 2048, out uri) && uri.AbsoluteUri == value;

    public static bool IsValidBrowserOrigin(string? value) =>
        TryReadHttpsUri(value, 255, out var uri) && uri.GetLeftPart(UriPartial.Authority) == value;

    private static bool TryReadHttpsUri(string? value, int maximumLength, out Uri uri)
    {
        if (value is { Length: > 0 } && value.Length <= maximumLength
            && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            && value.IndexOfAny(['\\', '*', '#']) < 0
            && Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.IsWellFormedOriginalString()
            && parsed.Scheme == Uri.UriSchemeHttps && parsed.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            && parsed.UserInfo.Length == 0 && parsed.Host.All(char.IsAscii))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    public bool IsActive => ProvisionedAt.HasValue && !DisabledAt.HasValue;
}

public interface IConsumerWebAppRegistrations
{
    Task<ConsumerWebAppRegistration?> FindAsync(string clientId, CancellationToken ct);
    Task<bool> IsActiveOriginAsync(string browserOrigin, CancellationToken ct);
}
