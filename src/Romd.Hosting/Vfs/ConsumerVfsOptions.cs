using Romd.Consumer.Application.Vfs;

namespace Romd.Hosting.Vfs;

public sealed class ConsumerVfsOptions
{
    public bool Enabled { get; set; }
    public bool AllowDevelopmentBearer { get; set; }
    public int MaximumConcurrentMetadataRequests { get; set; } = 2;
    public int MaximumConcurrentRequests { get; set; } = 8;
    public int MaximumConcurrentRequestsPerUser { get; set; } = 4;
    public int MaximumConcurrentRequestsPerConnection { get; set; } = 4;
    public int MaximumQueuedRequests { get; set; } = 128;
    public int MaximumQueuedRequestsPerUser { get; set; } = 32;
    public int MaximumQueuedRequestsPerConnection { get; set; } = 16;
    public int MaximumQueueWaitMilliseconds { get; set; } = 10_000;
    public int MaximumDirectoryEntries { get; set; } = 20_000;
}

public static class ConsumerVfsRegistration
{
    public static IServiceCollection AddRomdConsumerVfs(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ConsumerVfsOptions();
        configuration.GetSection("Romd:Vfs").Bind(options);
        if (options.MaximumConcurrentMetadataRequests is < 1 or > 32 ||
            options.MaximumConcurrentRequests is < 1 or > 128 ||
            options.MaximumConcurrentRequestsPerUser is < 1 or > 128 ||
            options.MaximumConcurrentRequestsPerConnection is < 1 or > 128 ||
            options.MaximumQueuedRequests is < 1 or > 4096 ||
            options.MaximumQueuedRequestsPerUser is < 1 or > 4096 ||
            options.MaximumQueuedRequestsPerConnection is < 1 or > 4096 ||
            options.MaximumQueueWaitMilliseconds is < 1 or > 60_000 ||
            options.MaximumDirectoryEntries is < 1 or > 100_000)
        {
            throw new InvalidOperationException("Invalid ROMD VFS resource limits.");
        }

        services.AddAuthentication()
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, VfsBasicAuthenticationHandler>(
                VfsBasicAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(authorization => authorization.AddPolicy(VfsBasicAuthenticationHandler.PolicyName,
            policy => policy.AddAuthenticationSchemes(VfsBasicAuthenticationHandler.SchemeName,
                OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()));
        services.AddSingleton<VfsConnectionExport>();
        services.AddSingleton(options);
        services.AddSingleton<ConsumerVfsAdmission>();
        services.AddSingleton<ConsumerVfsMetadataAdmission>();
        services.AddScoped<IVfsLayout, DefaultLibraryLayout>();
        services.AddScoped<VfsLayoutRegistry>();
        services.AddScoped<ConsumerVfsService>();
        return services;
    }
}
