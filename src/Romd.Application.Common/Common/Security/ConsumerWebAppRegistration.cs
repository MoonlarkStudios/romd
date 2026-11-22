namespace Romd.Application.Common.Security;

public sealed record ConsumerWebAppRegistration(
    string ClientId,
    string DisplayName,
    string CallbackUri,
    string BrowserOrigin,
    DateTimeOffset? ProvisionedAt,
    DateTimeOffset? DisabledAt)
{
    public const string ClientIdPrefix = "romd-web-";

    public static bool IsClientId(string? clientId) =>
        clientId?.StartsWith(ClientIdPrefix, StringComparison.Ordinal) == true;

    public bool IsActive => ProvisionedAt.HasValue && !DisabledAt.HasValue;
}

public interface IConsumerWebAppRegistrations
{
    Task<ConsumerWebAppRegistration?> FindAsync(string clientId, CancellationToken ct);
    Task<bool> IsActiveOriginAsync(string browserOrigin, CancellationToken ct);
}
