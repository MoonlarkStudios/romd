using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Storage.Files;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Persistence;
using Romd.Persistence.Repositories;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    private static ServiceProvider CreateBulkTitleServices(TestDatabase database,
        IOptions<EnrichmentOptions> options, Func<RomdDbContext, IFileStorageService>? storage = null) =>
        new ServiceCollection()
            .AddScoped(_ => database.CreateReadContext())
            .AddScoped<IUnitOfWork>(services => new EfUnitOfWork(services.GetRequiredService<RomdDbContext>()))
            .AddScoped<IJobExecutionMutationGuard>(services =>
            {
                var db = services.GetRequiredService<RomdDbContext>();
                return new JobExecutionMutationGuard(db, services.GetRequiredService<IUnitOfWork>(), TimeProvider.System,
                    new JobExecutionClaimOptions());
            })
            .AddScoped(services =>
            {
                var db = services.GetRequiredService<RomdDbContext>();
                var titles = new TitleRepository(db);
                return new BulkEnrichmentTitleProcessor(titles, titles, titles, new PlatformRepository(db),
                    storage?.Invoke(db) ?? Substitute.For<IFileStorageService>(), services.GetRequiredService<IUnitOfWork>(), Substitute.For<IJobNotifier>(),
                    Substitute.For<ILibraryMaterializationService>(), options,
                    NullLogger<BulkEnrichmentTitleProcessor>.Instance, Substitute.For<IAutomaticArtworkService>(), clock: TimeProvider.System);
            })
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
}
