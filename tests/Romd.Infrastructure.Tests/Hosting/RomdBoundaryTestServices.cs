using Microsoft.Extensions.DependencyInjection;
using Romd.Infrastructure;

namespace Romd.Infrastructure.Tests.Hosting;

internal static class RomdBoundaryTestServices
{
    public static ServiceCollection CreateConsumerStyleServices()
    {
        var services = new ServiceCollection();

        services.AddRomdConsumerInfrastructure();

        return services;
    }

    public static ServiceCollection CreateAdminApiStyleServices()
    {
        var services = new ServiceCollection();

        services.AddRomdAdminEnqueueInfrastructure();

        return services;
    }

    public static ServiceCollection CreateWorkerStyleServices()
    {
        var services = new ServiceCollection();

        services.AddRomdWorkerInfrastructure();

        return services;
    }
}
