using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Commands.AcceptUpload;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyReviewedDatReplacement;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Hosting;

public sealed class ImportPreparationCompositionTests
{
    private const string Connection = "Host=127.0.0.1;Database=unused;Username=unused";

    [Theory]
    [InlineData("admin")]
    [InlineData("worker")]
    [InlineData("testing")]
    public void CurationCompositions_RegisterNarrowPreparationAndCanonicalStagingWithoutOpeningDatabase(string composition)
    {
        var services = composition switch
        {
            "admin" => RomdBoundaryTestServices.CreateAdminApiStyleServices(),
            "worker" => RomdBoundaryTestServices.CreateWorkerStyleServices(),
            _ => new ServiceCollection().AddTestingInfrastructure(Connection)
        };
        services.AddLogging();
        services.AddHttpContextAccessor();
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(Path.GetTempPath());
        services.AddSingleton(options);
        services.AddSingleton<IFileSystem>(Substitute.For<IFileSystem>());
        services.AddSingleton<IFileStorageService>(Substitute.For<IFileStorageService>());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}"] = Connection }).Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var resolved = scope.ServiceProvider;

        resolved.GetRequiredService<ICommandHandler<ApplyReviewedDatReplacementCommand, Guid>>()
            .ShouldBeOfType<ApplyReviewedDatReplacementCommandHandler>();
        resolved.GetRequiredService<ICommandHandler<AcceptUploadCommand, Guid>>()
            .ShouldBeOfType<AcceptUploadCommandHandler>();
        resolved.GetRequiredService<IJobWorkspaceExclusion>().ShouldBeOfType<JobWorkspaceExclusion>();
        resolved.GetRequiredService<IUploadWorkspacePreparation>().ShouldBeOfType<UploadWorkspacePreparation>();
        resolved.GetRequiredService<IReplaceDatWorkspacePreparation>().ShouldBeOfType<ReplaceDatWorkspacePreparation>();
        resolved.GetRequiredService<IPathImportPreparation>().ShouldBeOfType<PathImportPreparation>();
        resolved.GetRequiredService<IReviewedDatReplacementPreparation>().ShouldBeOfType<DatReplacementReview>()
            .ShouldBeSameAs(resolved.GetRequiredService<IDatReplacementReview>());
        resolved.GetRequiredService<IUploadJobAcceptance>().ShouldBeSameAs(resolved.GetRequiredService<UploadJobRepository>());
        resolved.GetRequiredService<IUploadJobRepository>().ShouldBeSameAs(resolved.GetRequiredService<UploadJobRepository>());
        resolved.GetRequiredService<IReplaceDatJobAcceptance>().ShouldBeSameAs(resolved.GetRequiredService<ReplaceDatJobRepository>());
        resolved.GetRequiredService<IReplaceDatJobRepository>().ShouldBeSameAs(resolved.GetRequiredService<ReplaceDatJobRepository>());
        secondScope.ServiceProvider.GetRequiredService<IUploadJobAcceptance>()
            .ShouldNotBeSameAs(resolved.GetRequiredService<IUploadJobAcceptance>());
        resolved.GetRequiredService<RomdDbContext>().Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public void ConsumerComposition_DoesNotExposeImportPreparationOrAcceptance()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();
        Type[] ports = [typeof(IJobWorkspaceExclusion), typeof(IUploadWorkspacePreparation),
            typeof(IReplaceDatWorkspacePreparation), typeof(IPathImportPreparation), typeof(IReviewedDatReplacementPreparation),
            typeof(IUploadJobAcceptance), typeof(IReplaceDatJobAcceptance),
            typeof(ICommandHandler<ApplyReviewedDatReplacementCommand, Guid>),
            typeof(ICommandHandler<AcceptUploadCommand, Guid>)];

        foreach (var port in ports)
            services.ShouldNotContain(descriptor => descriptor.ServiceType == port);
    }

    [Fact]
    public void PreparationAndAcceptancePorts_SeparateImmutableInputsFromStagingAndLegacySaves()
    {
        typeof(IUploadJobAcceptance).GetMethods().Select(method => method.Name).ShouldBe(["AddStagedAsync"]);
        typeof(IReplaceDatJobAcceptance).GetMethods().Select(method => method.Name).ShouldBe(["AddStagedAsync"]);
        typeof(IUploadJobRepository).GetMethod("AddAsync").ShouldBeNull();
        typeof(IReplaceDatJobRepository).GetMethod("AddAsync").ShouldBeNull();
        typeof(UploadJobRepository).GetMethod("AddAsync").ShouldBeNull();
        typeof(ReplaceDatJobRepository).GetMethod("AddAsync").ShouldBeNull();
        typeof(IPreparedJobWorkspace).GetProperties().Select(property => property.Name).ShouldBe(["JobId"]);
        typeof(IJobWorkspaceLease).GetProperties().Select(property => property.Name).ShouldBe(["JobId"]);
        typeof(IUploadWorkspacePreparation).GetMethod("PrepareAsync").ShouldNotBeNull().GetParameters()[0].ParameterType
            .ShouldBe(typeof(JobWorkspaceInput));
        typeof(IPreparedDatReplacement).GetProperties().Select(property => property.PropertyType)
            .ShouldBe([typeof(DatReplacementFacts)]);
    }
}
