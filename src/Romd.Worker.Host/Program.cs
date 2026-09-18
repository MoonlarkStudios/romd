using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Romd.Host.Configuration;
using Romd.Hosting;
using Romd.Infrastructure;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Serilog;
using EnrichmentOptions = Romd.Admin.Application.Configuration.EnrichmentOptions;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    if (args.Contains("--provision-hangfire", StringComparer.Ordinal))
    {
        Log.Information("Provisioning the ROMD Hangfire PostgreSQL schema without starting job servers");
        using IHost host = Romd.Worker.Host.Program.CreateHangfireProvisioningHostBuilder(args).Build();
        await host.StartAsync();
        await host.StopAsync();
        return;
    }

    if (args.Contains("--migrate-database", StringComparer.Ordinal))
    {
        Log.Information("Migrating the ROMD application PostgreSQL schema without starting job servers");
        using IHost host = Romd.Worker.Host.Program.CreateDatabaseProvisioningHostBuilder(args).Build();
        await host.StartAsync();
        await host.StopAsync();
        return;
    }

    Log.Information("Starting ROMD worker host");

    await Romd.Worker.Host.Program.CreateHostBuilder(args).Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

namespace Romd.Worker.Host
{
    public partial class Program
    {
        public static IHostBuilder CreateHangfireProvisioningHostBuilder(string[] args) =>
            Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
                .UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .WriteTo.Console())
                .ConfigureServices(services => services.AddHostedService<HangfireSchemaProvisioner>());

        public static IHostBuilder CreateDatabaseProvisioningHostBuilder(string[] args) =>
            Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
                .UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .WriteTo.Console())
                .ConfigureServices(services => services.AddHostedService<PostgreSqlSchemaProvisioner>());

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
                .UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .WriteTo.Console())
                .ConfigureServices((context, services) =>
                {
                    var romdOptions = services.AddRomdOptions(
                        context.Configuration,
                        context.HostingEnvironment,
                        ProductionSecretValidator.Validate);

                    services.AddRomdContentAddressableStorage(romdOptions);

                    services.Configure<EnrichmentOptions>(
                        context.Configuration.GetSection(EnrichmentOptions.SectionName));

                    services
                        // Registered first: hosted services start in registration order, and the
                        // seeders, recurring-job registrar, dispatchers, and Hangfire servers below
                        // all touch schemas that only the provisioners create.
                        .AddRomdSchemaProvisioning(context.HostingEnvironment)
                        .AddRomdWorkerInfrastructure(context.Configuration)
                        .AddRomdDataProtection(romdOptions)
                        .AddRomdIdentity()
                        .AddRomdHangfireClient(
                            context.Configuration,
                            context.HostingEnvironment,
                            useJobStateSyncFilter: true)
                        .AddRomdHangfireServer(context.HostingEnvironment)
                        // Registered last: the readiness marker is written only after migrations and
                        // seeders have run, so the API hosts can gate startup on a migrated schema.
                        .AddRomdReadinessSignal(context.Configuration);
                });
    }
}
