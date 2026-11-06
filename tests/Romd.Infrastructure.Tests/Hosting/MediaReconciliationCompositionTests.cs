using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Source.Rom.Commands.RetryMediaReconciliation;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSelection;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSubject;
using Romd.Application.Common.Cqrs;
using Romd.Persistence.Catalog;
using Romd.Persistence.MediaFacts;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Romd.Persistence;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Hosting;

public sealed class MediaReconciliationCompositionTests
{
    [Fact]
    public void AdminApi_ReconciliationCommands_AreRegisteredAsScopedApplicationHandlers()
    {
        var admin = RomdBoundaryTestServices.CreateAdminApiStyleServices();
        var consumer = RomdBoundaryTestServices.CreateConsumerStyleServices();
        foreach (var (port, handler) in new[]
        {
            (typeof(ICommandHandler<StartMediaReconciliationSubjectCommand, Guid>), typeof(StartMediaReconciliationSubjectCommandHandler)),
            (typeof(ICommandHandler<StartMediaReconciliationSelectionCommand, Guid>), typeof(StartMediaReconciliationSelectionCommandHandler)),
            (typeof(ICommandHandler<RetryMediaReconciliationCommand, Guid>), typeof(RetryMediaReconciliationCommandHandler))
        })
        {
            var registration = admin.Where(descriptor => descriptor.ServiceType == port).ShouldHaveSingleItem();
            registration.ImplementationType.ShouldBe(handler);
            registration.Lifetime.ShouldBe(ServiceLifetime.Scoped);
            consumer.ShouldNotContain(descriptor => descriptor.ServiceType == port);
        }
    }

    [Fact]
    public async Task Worker_NoNativeAssets_ResolvesActualPipelineAndOneScopedPublicationAuthority()
    {
        var services = RomdBoundaryTestServices.CreateWorkerStyleServices();
        var options = new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql("Host=localhost;Database=composition;Username=composition").Options;
        services.AddSingleton(options);
        services.AddScoped(_ => new RomdDbContext(options));
        services.AddLogging();
        services.AddSingleton(Substitute.For<IContentAddressableStore>());
        var application = Substitute.For<IRomdOptions>();
        application.DataDirectory.Returns(Path.Combine(Path.GetTempPath(), "romd-reconciliation-composition-" + Guid.NewGuid().ToString("N")));
        application.MaxUploadBytes.Returns(1L << 30);
        services.AddSingleton(application);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var resolve = scope.ServiceProvider;
        var catalogReader = resolve.GetRequiredService<IRomCdImportCatalogReader>();
        catalogReader.ShouldBeOfType<RomCdImportCatalogReader>();
        resolve.GetRequiredService<IRomCdImportCatalogReader>().ShouldBeSameAs(catalogReader);
        await using (var otherScope = provider.CreateAsyncScope())
            otherScope.ServiceProvider.GetRequiredService<IRomCdImportCatalogReader>().ShouldNotBeSameAs(catalogReader);
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IRomCdImportCatalogReader>());
        resolve.GetRequiredService<ICatalogCdLayoutStore>().ShouldBeOfType<CatalogCdLayoutStore>();
        resolve.GetRequiredService<IDerivedDescriptorReader>().ShouldBeOfType<DerivedDescriptorReader>();
        var authority = resolve.GetRequiredService<IMediaReconciliationExecution>();
        resolve.GetRequiredService<IMediaReconciliationPublicationStore>().ShouldBeSameAs(authority);
        resolve.GetRequiredService<IMediaReconciliationEncoding>().ShouldBeOfType<MediaReconciliationEncoding>();
        resolve.GetRequiredService<IMediaReconciliationPublication>().ShouldBeOfType<MediaReconciliationPublication>();
        resolve.GetRequiredService<IJobExecutor<MediaReconciliationJob>>().ShouldBeOfType<MediaReconciliationJobExecutor>();
        resolve.GetRequiredService<IRomContentReader>().ShouldBeSameAs(resolve.GetRequiredService<RomContentReader>());
        var profile = resolve.GetRequiredService<IMediaReconciliationMediaProfile>();
        profile.Encoder("gamecube-disc").ShouldNotBeNull();
        profile.Encoder("ps2-dvd").ShouldBeNull();
        Should.Throw<NotSupportedException>(() => resolve.GetRequiredService<ChdArtifactEncoder>()).Message.ShouldContain("verified tool assets");
    }

    [Fact]
    public void ApiHosts_HaveNoReconciliationExecutionOrNativeProcessingServices()
    {
        var admin = RomdBoundaryTestServices.CreateAdminApiStyleServices();
        admin.ShouldContain(descriptor => descriptor.ServiceType == typeof(IMediaReconciliationRequests));
        var consumer = RomdBoundaryTestServices.CreateConsumerStyleServices();
        consumer.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IMediaReconciliationRequests));
        foreach (var services in new[] { admin, consumer })
            foreach (var type in new[] { typeof(IRomCdImportCatalogReader),typeof(ICatalogCdLayoutStore),typeof(IMediaReconciliationExecution),typeof(IMediaReconciliationPublicationStore),typeof(IMediaReconciliationSelections),
                     typeof(IMediaReconciliationEncoding),typeof(IMediaReconciliationPublication),typeof(MediaProcessingRuntime),typeof(ChdArtifactEncoder),typeof(IJobExecutor<MediaReconciliationJob>) })
                services.ShouldNotContain(descriptor => descriptor.ServiceType == type);
    }
}
