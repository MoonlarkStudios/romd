using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Storage.FileSystem;

namespace Romd.Storage;

/// <summary>
///     Extension methods for configuring content-addressable storage.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds content-addressable storage services with explicit configuration.
    /// </summary>
    public static IServiceCollection AddContentAddressableStorage(
        this IServiceCollection services,
        Action<ContentStoreOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<IContentAddressableStore>(CreateStore);
        return services;
    }

    /// <summary>
    ///     Adds content-addressable storage with configuration binding.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationSection">Configuration section name. Default is "ContentStore".</param>
    public static IServiceCollection AddContentAddressableStorage(
        this IServiceCollection services,
        string configurationSection = "ContentStore")
    {
        services.AddOptions<ContentStoreOptions>()
            .BindConfiguration(configurationSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IContentAddressableStore>(CreateStore);
        return services;
    }

    private static FileSystemContentStore CreateStore(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<ContentStoreOptions>>();
        var logger = sp.GetService<ILogger<FileSystemContentStore>>();
        return new FileSystemContentStore(options, logger);
    }
}
