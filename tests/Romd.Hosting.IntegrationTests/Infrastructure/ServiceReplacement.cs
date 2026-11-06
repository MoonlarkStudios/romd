using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

internal static class ServiceReplacement
{
    /// <summary>Replaces every registration of <typeparamref name="TService" /> with one instance.</summary>
    public static IServiceCollection ReplaceWith<TService>(this IServiceCollection services, TService instance)
        where TService : class
    {
        services.RemoveAll<TService>();
        return services.AddSingleton(instance);
    }
}
