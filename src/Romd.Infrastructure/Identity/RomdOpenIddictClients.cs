namespace Romd.Infrastructure.Identity;

/// <summary>
///     Well-known OpenIddict client identifiers. The admin SPA is bound to the admin host, the
///     consumer SPA, console and mount helper to the consumer host (see per-host surface binding).
/// </summary>
public static class RomdOpenIddictClients
{
    public const string AdminSpa = "romd-admin-spa";
    public const string ConsumerSpa = "romd-consumer-spa";
    public const string Console = "romd-console";
    public const string Mount = "romd-mount";
}
