using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Matching;
using Romd.Application.Common.Security;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;
using Romd.Consumer.Application.Browse;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Romd.Persistence.Search;
using Romd.Infrastructure.Catalog;
using Romd.Admin.Application.Catalog;
using Romd.Infrastructure.Source;

namespace Romd.ScaleHarness;

/// <summary>
///     Minimal DI container mirroring the production persistence registration in
///     <c>Romd.Infrastructure.DependencyInjection.AddRomdPersistence</c>: same Npgsql configuration
///     (schema, history table, MaxBatchSize), same audit and search-document interceptors, plus the
///     harness-only SQL capture interceptor. Only the repositories exercised by the scenarios are
///     registered.
/// </summary>
public static class HarnessServices
{
    public static ServiceProvider Build(string connectionString)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SqlCaptureInterceptor>();
        services.AddSingleton<SearchDocumentInterceptor>();
        services.AddScoped<IAuditContext, HarnessAuditContext>();
        services.AddScoped<AuditInterceptor>();

        services.AddDbContext<RomdDbContext>((sp, options) =>
        {
            PostgreSqlConfiguration.Configure(options, connectionString);
            options.UseOpenIddict();
            options.AddInterceptors(
                sp.GetRequiredService<AuditInterceptor>(),
                sp.GetRequiredService<SearchDocumentInterceptor>(),
                sp.GetRequiredService<SqlCaptureInterceptor>());
        });

        services.AddSingleton<IConsumerReleaseSelector, ConsumerReleaseSelector>();
        services.AddScoped<ITitleRepository, TitleRepository>();
        services.AddScoped<ITitleMatcher, TitleMatcher>();
        services.AddScoped<ISearchRepository, SearchRepository>();
        services.AddScoped<IExportRepository, ExportRepository>();
        services.AddScoped<IJobItemRepository, JobItemRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IReadSnapshotTransactionFactory, EfReadSnapshotTransactionFactory>();
        services.AddScoped<IQueryHandler<GetJobItemsQuery, JobItemPageResult>, GetJobItemsQueryHandler>();
        services.AddScoped<IQueryHandler<ExportJobItemsQuery, IReadOnlyList<JobItemView>>, ExportJobItemsQueryHandler>();
        services.AddScoped<ILibraryRepository, LibraryRepository>();
        services.AddScoped<IRomRepository, RomRepository>();
        services.AddScoped<ICatalogPayloadAssertionProvider, DatCatalogPayloadAssertionProvider>();
        services.AddScoped<ICatalogPayloadAssertionReader, CatalogPayloadAssertionReader>();
        services.AddScoped<ICatalogPayloadAssertionSynchronizer, CatalogPayloadAssertionSynchronizer>();
        services.AddScoped<ITitlePayloadAvailabilityProjection, TitlePayloadAvailabilityProjection>();

        return services.BuildServiceProvider();
    }
}

/// <summary>Fixed synthetic actor so the production audit interceptor runs unchanged.</summary>
public sealed class HarnessAuditContext : IAuditContext
{
    public Guid ActorId => DeterministicDataset.SystemUserId;
    public bool IsSystem => true;
}
