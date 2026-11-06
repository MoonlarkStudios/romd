using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Infrastructure;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Qualification uses the production worker media composition with its explicit disposable database, CAS and supervised child observer.</summary>
internal sealed class ReconciliationProbeServices : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    public ReconciliationProbeServices(DbContextOptions<RomdDbContext> database, IRomdOptions application,
        MediaProcessingOptions limits, TempStorageOptions temporary, MediaProcessingRuntime runtime,
        LocalTempFileFactory staging, IMediaWorkerSupervisor supervisor, IContentAddressableStore cas, JobExecutionClaimOptions claims)
    {
        using var connection = new RomdDbContext(database);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(application);
        services.AddRomdTestingPersistence(connection.Database.GetConnectionString()!);
        services.AddRomdStorage();
        services.AddRomdWorkerMediaProcessing();
        services.AddScoped<MediaReconciliationJobExecutor>();
        services.AddRomdJobLifecyclePolicies();
        services.AddSingleton(claims);
        // These are the already owned, observed resources of this exact qualification run.
        services.AddSingleton(database);
        services.AddSingleton(limits);
        services.AddSingleton(temporary);
        services.AddSingleton(runtime);
        services.AddSingleton(staging);
        services.AddSingleton(supervisor);
        services.AddSingleton(cas);
        provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    public AsyncServiceScope CreateScope() => provider.CreateAsyncScope();
    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
