using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Romd.Infrastructure.Tests.Hosting;

internal static class RomdServiceCollectionAssertions
{
    public static void ShouldContainService<TService>(this IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(TService)).ShouldBeTrue();

    public static void ShouldNotContainService<TService>(this IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(TService)).ShouldBeFalse();

    public static void ShouldContainOpenGenericService(this IServiceCollection services, Type serviceType) =>
        services.Any(descriptor => descriptor.ServiceType == serviceType).ShouldBeTrue();

    public static void ShouldNotContainOpenGenericService(this IServiceCollection services, Type serviceType) =>
        services.Any(descriptor => descriptor.ServiceType == serviceType).ShouldBeFalse();

    public static void ShouldNotResolveService<TService>(this IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();

        provider.GetService(typeof(TService)).ShouldBeNull();
    }

    public static void ShouldContainConfiguredOptions<TOptions>(this IServiceCollection services)
        where TOptions : class =>
        services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<TOptions>)).ShouldBeTrue();

    public static void ShouldNotContainConfiguredOptions<TOptions>(this IServiceCollection services)
        where TOptions : class =>
        services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<TOptions>)).ShouldBeFalse();

    public static void ShouldContainHostedService<THostedService>(this IServiceCollection services)
        where THostedService : IHostedService =>
        services.ContainsHostedService<THostedService>().ShouldBeTrue();

    public static void ShouldNotContainHostedService<THostedService>(this IServiceCollection services)
        where THostedService : IHostedService =>
        services.ContainsHostedService<THostedService>().ShouldBeFalse();

    private static bool ContainsHostedService<THostedService>(this IServiceCollection services)
        where THostedService : IHostedService =>
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(THostedService));
}
