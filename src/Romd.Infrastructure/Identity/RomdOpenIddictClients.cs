using Romd.Application.Common.Security;

namespace Romd.Infrastructure.Identity;

/// <summary>
///     Well-known OpenIddict client identifiers. The admin SPA is bound to the admin host, the
///     consumer SPA, console and mount helper to the consumer host (see per-host surface binding).
/// </summary>
public static class RomdOpenIddictClients
{
    public const string AdminSpa = "romd-admin-spa";
    public const string ConsumerSpa = "romd-consumer-spa";
    public const string ConsumerSpaConfiguration = "Romd:Auth:ConsumerSpa";
    public const string ConsumerSpaDevelopmentOrigin = "http://localhost:5174";
    public const string Console = "romd-console";
    public const string CommunityDevice = "romd-community-device";
    public const string Mount = "romd-mount";

    public static bool IsDevice(string? clientId) =>
        clientId is CommunityDevice or Console or Mount;

    public static bool UsesConsumerPermissions(string? clientId) =>
        IsDevice(clientId) || ConsumerWebAppRegistration.IsClientId(clientId);

}
