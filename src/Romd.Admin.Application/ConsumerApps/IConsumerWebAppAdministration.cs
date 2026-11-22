using Romd.Application.Common.Security;
using Romd.Contracts.Management.ConsumerApps;

namespace Romd.Admin.Application.ConsumerApps;

public interface IConsumerWebAppAdministration
{
    Task<IReadOnlyList<ConsumerWebAppRegistration>> ListAsync(CancellationToken ct);
    void StageRegistration(ConsumerWebAppRegistration registration);
    Task<bool> DisableAsync(string clientId, DateTimeOffset disabledAt, CancellationToken ct);
}

internal static class ConsumerWebAppMapping
{
    public static ConsumerWebApp ToContract(ConsumerWebAppRegistration registration) => new(
        registration.ClientId,
        registration.DisplayName,
        registration.CallbackUri,
        registration.BrowserOrigin,
        registration.DisabledAt.HasValue ? ConsumerWebAppStatus.Disabled :
            registration.IsActive ? ConsumerWebAppStatus.Active : ConsumerWebAppStatus.Pending);
}
