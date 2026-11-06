namespace Romd.Hosting;

public static class RomdBrowserPlaybackRegistrationExtensions
{
    public static IServiceCollection AddRomdBrowserPlayback(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(BrowserPlaybackOptions.SectionName)
            .Get<BrowserPlaybackOptions>() ?? new BrowserPlaybackOptions();

        options.Validate();
        services.AddSingleton(options);

        return services;
    }
}
