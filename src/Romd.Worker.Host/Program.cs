using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Romd.Admin.Application.Source.Rom;
using Romd.Host.Configuration;
using Romd.Hosting;
using Romd.Infrastructure;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Serilog;
using Serilog.Events;
using EnrichmentOptions = Romd.Admin.Application.Configuration.EnrichmentOptions;

if (Romd.Worker.Host.RedumpDiscImportCommand.IsRequested(args) ||
    Romd.Worker.Host.AdminRecoveryCommand.IsRequested(args))
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
        .CreateBootstrapLogger();
}
else
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .CreateBootstrapLogger();
}

try
{
    if (Romd.Worker.Host.AdminRecoveryCommand.IsRequested(args))
    {
        Environment.ExitCode = await Romd.Worker.Host.AdminRecoveryCommand.RunAsync(args);
        return;
    }

    if (Romd.Worker.Host.RedumpDiscImportCommand.IsRequested(args))
    {
        Environment.ExitCode = await Romd.Worker.Host.RedumpDiscImportCommand.RunAsync(args);
        return;
    }

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

    await Romd.Worker.Host.Program.CreateHostBuilder(args).Build().RunRomdServerAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
    // An unhandled CLR exception can stall while raising its fatal signal as
    // Linux namespace PID 1. Return a failure status through normal teardown so
    // startup failures terminate the container and extinguish media descendants.
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

namespace Romd.Worker.Host
{
    public partial class Program
    {
        public static IHostBuilder CreateAdminRecoveryHostBuilder() =>
            Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder([])
                .UseSerilog((_, _, configuration) => configuration
                    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose))
                .ConfigureServices(services => services.AddRomdLocalAdminRecovery());

        public static IHostBuilder CreateDiscReferenceImportHostBuilder() =>
            Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder([])
                // This command reserves stdout for its JSON result. Configuration and service
                // sinks can target stdout, so only this command uses a fixed stderr logger.
                .UseSerilog((_, _, configuration) => configuration
                    .Enrich.FromLogContext()
                    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose))
                .ConfigureServices(services =>
                {
                    // Runtime persistence only: this command never starts the host or provisions
                    // schemas, native assets, media children, job servers or readiness markers.
                    services.AddRomdPersistence();
                    services.AddScoped<IRomdSchemaProbe, RomdSchemaProbe>();
                    services.AddScoped<IRomDiscMediaReferenceImportStore, RomDiscMediaReferenceImportStore>();
                });

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

                    services.AddRomdContentAddressableStorage(romdOptions, context.Configuration);

                    services.Configure<EnrichmentOptions>(
                        context.Configuration.GetSection(EnrichmentOptions.SectionName));

                    services
                        .AddRomdChdAssets(requireTool: true)
                        // First database services: hosted services start in registration order, and the
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
