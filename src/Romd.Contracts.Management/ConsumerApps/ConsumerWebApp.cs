using System.ComponentModel.DataAnnotations;

namespace Romd.Contracts.Management.ConsumerApps;

public sealed record ConsumerWebApp(
    string ClientId,
    string DisplayName,
    string CallbackUri,
    string BrowserOrigin,
    ConsumerWebAppStatus Status);

public enum ConsumerWebAppStatus
{
    Pending,
    Active,
    Disabled,
    ProvisioningFailed
}

public sealed record RegisterConsumerWebAppRequest(
    [property: Required, StringLength(120)] string DisplayName,
    [property: Required, StringLength(2048)] string CallbackUri,
    [property: Required, StringLength(255)] string BrowserOrigin);
