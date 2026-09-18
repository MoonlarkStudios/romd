using Romd.Persistence.ReferenceData;
using ErrorOr;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Romd.Admin.Application.Collections.Commands.CreateCollection;
using Romd.Admin.Application.Collections.Queries.ListCollections;
using Romd.Admin.Application.Collections;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Readiness;
using Romd.Consumer.Application.Auth.Queries.GetCurrentConsumerUser;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Browse.Queries.SearchConsumerCatalog;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Diagnostics;
using Romd.Admin.Application.Dashboard.Queries.GetStorageStats;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.Commands.AddRegionAlias;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Commands.CreateLibrary;
using Romd.Admin.Application.Libraries.Commands.ForceMaterializeLibrary;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetFieldOverrides;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.Matching;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Commands.AssignDefaultLibraryToUsers;
using Romd.Admin.Application.Users.Commands.AssignUserLibrary;
using Romd.Admin.Application.Users.Commands.AssignUserRole;
using Romd.Admin.Application.Users.Commands.CreateUser;
using Romd.Admin.Application.Users.Commands.DeleteUser;
using Romd.Admin.Application.Users.Commands.UpdateUser;
using Romd.Admin.Application.Users.Queries.GetUserById;
using Romd.Admin.Application.Users.Queries.ListUsers;
using Romd.Contracts.Consumer.Auth;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Management.Collections;
using Romd.Dat.Parsing;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Dashboard;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Infrastructure.Libraries;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Source;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using CommonModels = Romd.Contracts.Common.Models;
using ManagementModels = Romd.Contracts.Management.Models;

namespace Romd.Infrastructure.Tests.Hosting;

public sealed class RomdHostingBoundaryTests
{
    [Fact]
    public void UserAdministrationPort_ExposesOnlyApplicationSafeModelsAndResults()
    {
        var exposedTypes = typeof(IUserAdministration).GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .SelectMany(FlattenType)
            .Distinct()
            .ToList();

        exposedTypes.ShouldNotContain(typeof(RomdUser));
        exposedTypes.ShouldNotContain(typeof(IdentityResult));
        exposedTypes.ShouldAllBe(type => type.Namespace == null || !type.Namespace.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal));
        exposedTypes.ShouldAllBe(type => type.Namespace == null || !type.Namespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        exposedTypes.ShouldAllBe(type => type.Namespace == null || !type.Namespace.StartsWith("Romd.Infrastructure", StringComparison.Ordinal));
        exposedTypes.ShouldAllBe(type => type.Namespace == null || !type.Namespace.StartsWith("Romd.Contracts", StringComparison.Ordinal));
        exposedTypes.ShouldAllBe(type => !type.IsGenericType || type.GetGenericTypeDefinition() != typeof(IQueryable<>));
    }

    [Fact]
    public void AdminRealtimeOutboxPorts_SeparateEnqueueFromDispatchOperations()
    {
        var enqueueMethods = typeof(IAdminEventOutbox).GetMethods();
        enqueueMethods.Length.ShouldBe(2);
        enqueueMethods.ShouldAllBe(method => method.Name == nameof(IAdminEventOutbox.EnqueueAsync));
        typeof(IAdminRealtimeOutbox).GetMethods()
            .Select(method => method.Name)
            .ShouldNotContain(nameof(IAdminEventOutbox.EnqueueAsync));
    }

    [Fact]
    public void TitleEnrichmentEvidencePort_ExposesOneTitleKeyedReadAndNoSourceIdentity()
    {
        var method = typeof(ITitleEnrichmentEvidenceReader).GetMethods().ShouldHaveSingleItem();
        method.Name.ShouldBe(nameof(ITitleEnrichmentEvidenceReader.ReadAsync));
        method.GetParameters().Select(parameter => (parameter.Name, parameter.ParameterType)).ShouldBe(
        [
            ("titleId", typeof(int)),
            ("cancellationToken", typeof(CancellationToken))
        ]);

        Type[] contractTypes = [typeof(TitleEnrichmentEvidence), typeof(OwnedRomHashEvidence)];
        var contractProperties = contractTypes.SelectMany(type => type.GetProperties()).ToList();

        contractProperties.ShouldAllBe(property => !property.Name.EndsWith("Id", StringComparison.Ordinal));
        contractProperties
            .SelectMany(property => FlattenType(property.PropertyType))
            .ShouldAllBe(type => type.Namespace == null ||
                !type.Namespace.StartsWith("Romd.Domain.Source.Dat", StringComparison.Ordinal));
    }

    [Fact]
    public void RomCatalogOwnershipPorts_ExposeSegregatedReadsAndNoDatSourceIdentity()
    {
        var catalogMethod = typeof(IRomCatalogMatchReader).GetMethods().ShouldHaveSingleItem();
        catalogMethod.Name.ShouldBe(nameof(IRomCatalogMatchReader.ReadAsync));
        catalogMethod.GetParameters().Select(parameter => (parameter.Name, parameter.ParameterType)).ShouldBe(
        [
            ("sha1", typeof(Sha1)),
            ("cancellationToken", typeof(CancellationToken))
        ]);

        var ownershipMethods = typeof(IRomOwnershipImpactReader).GetMethods();
        ownershipMethods.ShouldHaveSingleItem().Name.ShouldBe(
            nameof(IRomOwnershipImpactReader.ReadTitlePlatformIdsAsync));
        foreach (var ownershipMethod in ownershipMethods)
        {
            ownershipMethod.GetParameters()
                .Select(parameter => (parameter.Name, parameter.ParameterType))
                .ShouldBe(
            [
                ("romFileId", typeof(int)),
                ("cancellationToken", typeof(CancellationToken))
            ]);
        }

        var payloadImpactMethods = typeof(IRomPayloadAssertionImpactReader).GetMethods();
        payloadImpactMethods.Select(method => method.Name).ToHashSet().SetEquals(
        [
            nameof(IRomPayloadAssertionImpactReader.ReadSourceEntryIdsBySha1Async),
            nameof(IRomPayloadAssertionImpactReader.ReadSourceEntryIdsByRomFileIdAsync)
        ]).ShouldBeTrue();
        payloadImpactMethods.Single(method => method.Name.EndsWith("Sha1Async", StringComparison.Ordinal))
            .GetParameters()[0].ParameterType.ShouldBe(typeof(Sha1));
        payloadImpactMethods.Single(method => method.Name.EndsWith("RomFileIdAsync", StringComparison.Ordinal))
            .GetParameters()[0].ParameterType.ShouldBe(typeof(int));

        var contractProperties = typeof(RomCatalogMatch).GetProperties().ToList();
        contractProperties.Select(property => property.Name).ToHashSet().SetEquals(
        [
            nameof(RomCatalogMatch.TitleIds),
            nameof(RomCatalogMatch.TitlePlatformIds),
            nameof(RomCatalogMatch.BiosPlatformIds),
            nameof(RomCatalogMatch.HasCatalogMatch),
            nameof(RomCatalogMatch.PrimaryPlatformId)
        ]).ShouldBeTrue();
        contractProperties
            .SelectMany(property => FlattenType(property.PropertyType))
            .ShouldAllBe(type => type.Namespace == null ||
                !type.Namespace.StartsWith("Romd.Domain.Source.Dat", StringComparison.Ordinal));
        typeof(RomCatalogMatch).GetProperty(nameof(RomCatalogMatch.PrimaryPlatformId))!
            .PropertyType.ShouldBe(typeof(int?));
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nested in FlattenType(elementType))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GenericTypeArguments)
        {
            foreach (var nested in FlattenType(argument))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void AdminPersistenceRegistration_RegistersFullRepositorySurface()
    {
        var services = new ServiceCollection();

        services.AddRomdAdminPersistence();

        services.ShouldContainService<IDatRepository>();
        services.ShouldContainService<ITitleSourceAssignmentStore>();
        services.ShouldContainService<IRomCatalogMatchReader>();
        services.ShouldContainService<IRomOwnershipImpactReader>();
        services.ShouldContainService<IRomPayloadAssertionImpactReader>();
        services.ShouldContainService<IRomRepository>();
        services.ShouldContainService<IPlatformRepository>();
        services.ShouldContainService<ITitleRepository>();
        services.ShouldContainService<ICollectionRepository>();
        services.ShouldContainService<ISearchRepository>();
        services.ShouldContainService<IUploadJobRepository>();
        services.ShouldContainService<IJobRepository>();
        services.ShouldContainService<IJobRepository<ExportJob>>();
        services.ShouldContainService<IAdminEventOutbox>();
        services.ShouldContainService<IAdminRealtimeOutbox>();
        services.ShouldContainService<IExportRepository>();
        services.ShouldContainService<ILibraryRepository>();
        services.ShouldContainService<IConsumerBrowseRepository>();
        services.ShouldContainService<IConsumerReleaseAccessRepository>();
        services.ShouldContainService<IConsumerReleaseManifestRepository>();
        services.ShouldContainService<IConsumerCollectionReadRepository>();
        services.ShouldContainService<IConsumerLibraryContextRepository>();
        services.ShouldContainService<IPlatformFieldDefaultRepository>();
        services.ShouldContainService<IUnitOfWork>();
        services.ShouldContainService<IUserAdministration>();
        services.ShouldContainService<IFileRepository>();
        services.ShouldContainService<IRegionRepository>();
        services.ShouldContainService<ITaxonomyRepository<Region>>();
        services.ShouldContainService<ILanguageRepository>();
        services.ShouldContainService<ITaxonomyRepository<GameLanguage>>();
    }

    [Fact]
    public void TitleDerivationPort_RegisteredInCurationCompositionsButNotConsumer()
    {
        using var connection = PostgreSqlTestDatabase.Create();

        RomdBoundaryTestServices.CreateAdminApiStyleServices()
            .ShouldContainService<ITitleDerivationService>();
        RomdBoundaryTestServices.CreateWorkerStyleServices()
            .ShouldContainService<ITitleDerivationService>();
        new ServiceCollection().AddTestingInfrastructure(connection.ConnectionString)
            .ShouldContainService<ITitleDerivationService>();

        var consumerServices = RomdBoundaryTestServices.CreateConsumerStyleServices();
        consumerServices.ShouldNotContainService<ITitleDerivationService>();
        consumerServices.ShouldNotResolveService<ITitleDerivationService>();
    }

    [Fact]
    public void OperationalDiagnosticsPorts_RegisteredForAdminHandlerCompositionsButNotConsumer()
    {
        using var connection = PostgreSqlTestDatabase.Create();

        var adminServices = RomdBoundaryTestServices.CreateAdminApiStyleServices();
        adminServices.ShouldContainService<IOperationalDiagnosticsReader>();
        adminServices.Single(service => service.ServiceType == typeof(OperationalDiagnosticsAdmission))
            .Lifetime.ShouldBe(ServiceLifetime.Singleton);
        var testServices = new ServiceCollection().AddTestingInfrastructure(connection.ConnectionString);
        testServices.ShouldContainService<IOperationalDiagnosticsReader>();
        testServices.Single(service => service.ServiceType == typeof(OperationalDiagnosticsAdmission))
            .Lifetime.ShouldBe(ServiceLifetime.Singleton);

        var workerServices = RomdBoundaryTestServices.CreateWorkerStyleServices();
        workerServices.ShouldContainService<IOperationalDiagnosticsReader>();
        workerServices.ShouldContainService<IHangfireDiagnosticsReader>();
        workerServices.ShouldContainService<IStorageDiagnosticsReader>();
        workerServices.Single(service => service.ServiceType == typeof(OperationalDiagnosticsAdmission))
            .Lifetime.ShouldBe(ServiceLifetime.Singleton);

        var consumerServices = RomdBoundaryTestServices.CreateConsumerStyleServices();
        consumerServices.ShouldNotContainService<IOperationalDiagnosticsReader>();
        consumerServices.ShouldNotContainService<IHangfireDiagnosticsReader>();
        consumerServices.ShouldNotContainService<IStorageDiagnosticsReader>();
        consumerServices.ShouldNotContainService<OperationalDiagnosticsAdmission>();
    }

    [Fact]
    public void TitleSourceReferencePort_RegisteredInCurationCompositionsButNotConsumer()
    {
        using var connection = PostgreSqlTestDatabase.Create();

        RomdBoundaryTestServices.CreateAdminApiStyleServices()
            .ShouldContainService<ITitleSourceReferenceReader>();
        RomdBoundaryTestServices.CreateWorkerStyleServices()
            .ShouldContainService<ITitleSourceReferenceReader>();
        new ServiceCollection().AddTestingInfrastructure(connection.ConnectionString)
            .ShouldContainService<ITitleSourceReferenceReader>();

        var consumerServices = RomdBoundaryTestServices.CreateConsumerStyleServices();
        consumerServices.ShouldNotContainService<ITitleSourceReferenceReader>();
        consumerServices.ShouldNotResolveService<ITitleSourceReferenceReader>();
    }

    [Fact]
    public void SourceLifecyclePort_RegisteredInCurationCompositionsButNotConsumer()
    {
        using var connection = PostgreSqlTestDatabase.Create();

        RomdBoundaryTestServices.CreateAdminApiStyleServices()
            .ShouldContainService<ISourceLifecycle>();
        RomdBoundaryTestServices.CreateWorkerStyleServices()
            .ShouldContainService<ISourceLifecycle>();
        new ServiceCollection().AddTestingInfrastructure(connection.ConnectionString)
            .ShouldContainService<ISourceLifecycle>();

        var consumerServices = RomdBoundaryTestServices.CreateConsumerStyleServices();
        consumerServices.ShouldNotContainService<ISourceLifecycle>();
        consumerServices.ShouldNotResolveService<ISourceLifecycle>();
    }

    [Fact]
    public void TestingPersistenceRegistration_RegistersUserAdministrationPort()
    {
        var services = new ServiceCollection();
        using var connection = PostgreSqlTestDatabase.Create();

        services.AddRomdTestingPersistence(connection.ConnectionString);

        services.ShouldContainService<IUserAdministration>();
        services.ShouldContainService<IUnitOfWork>();
        services.ShouldContainService<ILibraryRepository>();
    }

    [Fact]
    public void TestingPersistenceRegistration_AssignmentAndDatPortsShareScopedRepositoryInstance()
    {
        var services = new ServiceCollection();
        using var connection = PostgreSqlTestDatabase.Create();
        services.AddRomdTestingPersistence(connection.ConnectionString);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var datRepository = scope.ServiceProvider.GetRequiredService<IDatRepository>();
        var assignmentStore = scope.ServiceProvider.GetRequiredService<ITitleSourceAssignmentStore>();

        assignmentStore.ShouldBeSameAs(datRepository);
    }

    [Fact]
    public void TestingPersistenceRegistration_RomCatalogReadRolesShareScopedAdapterInstance()
    {
        var services = new ServiceCollection();
        using var connection = PostgreSqlTestDatabase.Create();
        services.AddRomdTestingPersistence(connection.ConnectionString);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var catalogMatches = scope.ServiceProvider.GetRequiredService<IRomCatalogMatchReader>();
        var ownershipImpact = scope.ServiceProvider.GetRequiredService<IRomOwnershipImpactReader>();
        var payloadImpact = scope.ServiceProvider.GetRequiredService<IRomPayloadAssertionImpactReader>();

        ownershipImpact.ShouldBeSameAs(catalogMatches);
        payloadImpact.ShouldBeSameAs(catalogMatches);
    }

    [Fact]
    public void ConsumerStyleRegistration_RegistersOnlyConsumerReadPersistenceSurface()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();

        services.ShouldContainService<IConsumerBrowseRepository>();
        services.ShouldContainService<IConsumerReleaseAccessRepository>();
        services.ShouldContainService<IConsumerCollectionReadRepository>();
        services.ShouldContainService<IConsumerLibraryContextRepository>();
        services.ShouldContainService<IConsumerContentArtifactResolver>();
        services.ShouldContainService<IConsumerMediaArtifactResolver>();
        services.ShouldContainService<IConsumerReleaseManifestRepository>();
        services.ShouldContainService<IConsumerContentGrantIssuer>();
        services.ShouldContainService<IConsumerPasswordChanger>();
        services.ShouldContainService<IConsumerUserSettingsStore>();
        services.ShouldContainService<IServerInstanceIdentity>();

        services.ShouldNotContainService<IDatRepository>();
        services.ShouldNotContainService<ITitleSourceAssignmentStore>();
        services.ShouldNotContainService<IRomCatalogMatchReader>();
        services.ShouldNotContainService<IRomOwnershipImpactReader>();
        services.ShouldNotContainService<IRomPayloadAssertionImpactReader>();
        services.ShouldNotContainService<ITitleEnrichmentEvidenceReader>();
        services.ShouldNotContainService<IRomRepository>();
        services.ShouldNotContainService<IPlatformRepository>();
        services.ShouldNotContainService<ITitleRepository>();
        services.ShouldNotContainService<ICollectionRepository>();
        services.ShouldNotContainService<ISearchRepository>();
        services.ShouldNotContainService<IUploadJobRepository>();
        services.ShouldNotContainService<IJobRepository>();
        services.ShouldNotContainService<IJobRepository<UploadJob>>();
        services.ShouldNotContainService<IJobRepository<ReplaceDatJob>>();
        services.ShouldNotContainService<IJobRepository<EnrichmentJob>>();
        services.ShouldNotContainService<IJobRepository<BulkEnrichmentJob>>();
        services.ShouldNotContainService<IJobRepository<ExportJob>>();
        services.ShouldNotContainService<IJobRepository<MaterializationJob>>();
        services.ShouldNotContainService<IMaterializationJobRepository>();
        services.ShouldNotContainService<IEnrichmentJobRepository>();
        services.ShouldNotContainService<IAdminEventOutbox>();
        services.ShouldNotContainService<IAdminRealtimeOutbox>();
        services.ShouldNotContainService<IExportRepository>();
        services.ShouldNotContainService<ILibraryRepository>();
        services.ShouldNotContainService<IPlatformFieldDefaultRepository>();
        services.ShouldNotContainService<IUnitOfWork>();
        services.ShouldNotContainService<IMaterializationJobEnqueuer>();
        services.ShouldNotContainService<IEnrichmentJobEnqueuer>();
        services.ShouldNotContainService<IUserAdministration>();
        services.ShouldNotContainService<IQueryHandler<ListUsersQuery, IReadOnlyList<ManagedUser>>>();
        services.ShouldNotContainService<IQueryHandler<GetUserByIdQuery, ManagedUser>>();
        services.ShouldNotContainService<ICommandHandler<CreateUserCommand, ManagedUser>>();
        services.ShouldNotContainService<ICommandHandler<UpdateUserCommand, ManagedUser>>();
        services.ShouldNotContainService<ICommandHandler<DeleteUserCommand, Deleted>>();
        services.ShouldNotContainService<ICommandHandler<AssignUserRoleCommand, ManagedUser>>();
        services.ShouldNotContainService<ICommandHandler<AssignUserLibraryCommand, ManagedUser>>();
        services.ShouldNotContainService<ICommandHandler<AssignDefaultLibraryToUsersCommand, int>>();
        services.ShouldNotContainService<IFileRepository>();
        services.ShouldNotContainService<IRegionRepository>();
        services.ShouldNotContainService<ITaxonomyRepository<Region>>();
        services.ShouldNotContainService<ILanguageRepository>();
        services.ShouldNotContainService<ITaxonomyRepository<GameLanguage>>();
        services.ShouldNotContainService<UserManager<RomdUser>>();
        services.ShouldNotContainService<SignInManager<RomdUser>>();
        services.ShouldNotContainService<IUserStore<RomdUser>>();
        services.ShouldNotContainService<RoleManager<RomdIdentityRole>>();
        services.ShouldNotContainService<IRoleStore<RomdIdentityRole>>();
    }

    [Fact]
    public void ConsumerStyleRegistration_CannotResolveRepresentativeWriteRepositories()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();

        services.ShouldNotResolveService<IDatRepository>();
        services.ShouldNotResolveService<ITitleSourceAssignmentStore>();
        services.ShouldNotResolveService<IRomCatalogMatchReader>();
        services.ShouldNotResolveService<IRomOwnershipImpactReader>();
        services.ShouldNotResolveService<IRomPayloadAssertionImpactReader>();
        services.ShouldNotResolveService<IRomRepository>();
        services.ShouldNotResolveService<ITitleRepository>();
        services.ShouldNotResolveService<ICollectionRepository>();
        services.ShouldNotResolveService<ILibraryRepository>();
        services.ShouldNotResolveService<IJobRepository>();
        services.ShouldNotResolveService<IJobRepository<ExportJob>>();
        services.ShouldNotResolveService<IAdminEventOutbox>();
        services.ShouldNotResolveService<IAdminRealtimeOutbox>();
        services.ShouldNotResolveService<IExportRepository>();
        services.ShouldNotResolveService<IPlatformFieldDefaultRepository>();
        services.ShouldNotResolveService<IUnitOfWork>();
        services.ShouldNotResolveService<IUserAdministration>();
        services.ShouldNotResolveService<IQueryHandler<ListUsersQuery, IReadOnlyList<ManagedUser>>>();
        services.ShouldNotResolveService<IQueryHandler<GetUserByIdQuery, ManagedUser>>();
        services.ShouldNotResolveService<ICommandHandler<CreateUserCommand, ManagedUser>>();
        services.ShouldNotResolveService<ICommandHandler<UpdateUserCommand, ManagedUser>>();
        services.ShouldNotResolveService<ICommandHandler<DeleteUserCommand, Deleted>>();
        services.ShouldNotResolveService<ICommandHandler<AssignUserRoleCommand, ManagedUser>>();
        services.ShouldNotResolveService<ICommandHandler<AssignUserLibraryCommand, ManagedUser>>();
        services.ShouldNotResolveService<ICommandHandler<AssignDefaultLibraryToUsersCommand, int>>();
        services.ShouldNotResolveService<IFileRepository>();
        services.ShouldNotResolveService<ITaxonomyRepository<Region>>();
        services.ShouldNotResolveService<UserManager<RomdUser>>();
        services.ShouldNotResolveService<SignInManager<RomdUser>>();
        services.ShouldNotResolveService<IUserStore<RomdUser>>();
        services.ShouldNotResolveService<RoleManager<RomdIdentityRole>>();
        services.ShouldNotResolveService<IRoleStore<RomdIdentityRole>>();
    }

    [Fact]
    public void ReadOnlyStorageRegistration_RegistersConsumerArtifactResolversOnly()
    {
        var services = new ServiceCollection();

        services.AddRomdReadOnlyStorage();

        services.ShouldContainService<IConsumerContentArtifactResolver>();
        services.ShouldContainService<IConsumerMediaArtifactResolver>();
        services.ShouldNotContainService<IFileStorageService>();
        services.ShouldNotContainService<IFileRepository>();
        services.ShouldNotContainService<IUnitOfWork>();
    }

    [Fact]
    public void BroadStorageRegistration_DoesNotRegisterConsumerArtifactResolvers()
    {
        var services = new ServiceCollection();

        services.AddRomdStorage();

        services.ShouldNotContainService<IConsumerContentArtifactResolver>();
        services.ShouldNotContainService<IConsumerMediaArtifactResolver>();
    }

    [Fact]
    public void ConsumerStyleRegistration_RegistersOnlyConsumerApplicationHandlers()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();
        var handlerServiceTypes = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(IsCqrsHandlerServiceType)
            .ToArray();

        handlerServiceTypes.ShouldNotBeEmpty();
        handlerServiceTypes.ShouldAllBe(type => IsConsumerHandlerServiceType(type));
        services.ShouldContainService<IQueryHandler<GetCurrentConsumerUserQuery, CurrentUserDto>>();
        services.ShouldContainService<IQueryHandler<
            SearchConsumerCatalogQuery,
            CommonModels.Page<ConsumerTitleCardDto>>>();
    }

    [Fact]
    public void ConsumerStyleRegistration_DoesNotResolveManagementApplicationHandlers()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();

        services.ShouldNotResolveService<ICommandHandler<SetFieldOverridesCommand, ManagementModels.TitleDetail>>();
        services.ShouldNotResolveService<ICommandHandler<UpdateUserMetadataCommand, ManagementModels.TitleDetail>>();
        services.ShouldNotResolveService<ICommandHandler<BatchDeleteRomsCommand, BatchDeleteResult>>();
        services.ShouldNotResolveService<ICommandHandler<IngestDatCommand, DatIngestResult>>();
        services.ShouldNotResolveService<IQueryHandler<GetStorageStatsQuery, ManagementModels.StorageStatsDto>>();
        services.ShouldNotResolveService<ICommandHandler<CreateCollectionCommand, CollectionSummary>>();
        services.ShouldNotResolveService<IQueryHandler<ListCollectionsQuery, IReadOnlyList<CollectionSummary>>>();
        services.ShouldNotResolveService<ICommandHandler<AddRegionAliasCommand, Created>>();
    }

    [Fact]
    public void ConsumerStyleRegistration_ExcludesEveryAdminLibraryHandler()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();
        var libraryHandlerServiceTypes = typeof(CreateLibraryCommand).Assembly
            .GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                "Romd.Admin.Application.Libraries.",
                StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetInterfaces())
            .Where(IsCqrsHandlerServiceType)
            .Distinct()
            .ToArray();

        libraryHandlerServiceTypes.Length.ShouldBe(15);
        services.ShouldAllBe(descriptor => !libraryHandlerServiceTypes.Contains(descriptor.ServiceType));
        services.ShouldNotResolveService<
            ICommandHandler<ForceMaterializeLibraryCommand, Guid>>();
    }

    [Fact]
    public void ConsumerStyleRegistration_DoesNotRegisterProviderCredentialBindingOrCurationServices()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();

        services.ShouldNotContainConfiguredOptions<IgdbProviderOptions>();
        services.ShouldNotContainService<IgdbMetadataProvider>();
        services.ShouldNotContainService<IMetadataProvider>();
        services.ShouldNotContainService<IEnrichmentOrchestrator>();
        services.ShouldNotContainService<IEnrichmentScheduler>();
        services.ShouldNotContainService<IProviderRateLimiter>();
        services.ShouldNotContainService<ITitleMatcher>();
        services.ShouldNotContainService<IDatReader>();
        services.ShouldNotContainService<IDatFormat>();
        services.ShouldNotContainService<IDatHeaderReader>();
        services.ShouldNotContainService<IDatGameStreamReader>();
        services.ShouldNotContainService<DatParser>();
        services.ShouldNotContainService<IFileClassifier>();
        services.ShouldNotContainService<IRematerializationService>();
        services.ShouldNotContainService<IUploadJobCreator>();
        services.ShouldNotContainService<IReplaceDatJobCreator>();
    }

    [Fact]
    public void ConsumerStyleRegistration_DoesNotResolveCurationExportOrWorkerServices()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();

        services.ShouldNotResolveService<IgdbMetadataProvider>();
        services.ShouldNotResolveService<IMetadataProvider>();
        services.ShouldNotResolveService<IEnrichmentOrchestrator>();
        services.ShouldNotResolveService<IEnrichmentScheduler>();
        services.ShouldNotResolveService<IProviderRateLimiter>();
        services.ShouldNotResolveService<ITitleMatcher>();
        services.ShouldNotResolveService<IDatReader>();
        services.ShouldNotResolveService<IDatFormat>();
        services.ShouldNotResolveService<IDatHeaderReader>();
        services.ShouldNotResolveService<IDatGameStreamReader>();
        services.ShouldNotResolveService<DatParser>();
        services.ShouldNotResolveService<IFileClassifier>();
        services.ShouldNotResolveService<IRematerializationService>();
        services.ShouldNotResolveService<IUploadJobCreator>();
        services.ShouldNotResolveService<IReplaceDatJobCreator>();
        services.ShouldNotResolveService<IExportScheduler>();
        services.ShouldNotResolveService<IJobExecutor<ExportJob>>();
        services.ShouldNotResolveService<IJobExecutor<UploadJob>>();
        services.ShouldNotResolveService<IJobExecutor<ReplaceDatJob>>();
        services.ShouldNotResolveService<IJobExecutor<EnrichmentJob>>();
        services.ShouldNotResolveService<IJobExecutor<BulkEnrichmentJob>>();
        services.ShouldNotResolveService<IJobExecutor<MaterializationJob>>();
        services.ShouldNotResolveService<IMaterializationDataProvider>();
        services.ShouldNotResolveService<ILibraryMaterializationService>();
        services.ShouldNotResolveService<ILibraryMaterializationScheduler>();
        services.ShouldNotResolveService<IMaterializationJobEnqueuer>();
        services.ShouldNotResolveService<IEnrichmentJobEnqueuer>();
    }

    [Fact]
    public void ConsumerStyleRegistration_DoesNotRegisterExportExecutionOrWorkerServices()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();

        services.ShouldNotContainService<IExportScheduler>();
        services.ShouldNotContainService<IJobExecutor<ExportJob>>();
        services.ShouldNotContainService<IJobExecutor<UploadJob>>();
        services.ShouldNotContainService<IJobExecutor<ReplaceDatJob>>();
        services.ShouldNotContainService<IJobExecutor<EnrichmentJob>>();
        services.ShouldNotContainService<IJobExecutor<BulkEnrichmentJob>>();
        services.ShouldNotContainService<IJobExecutor<MaterializationJob>>();
        services.ShouldNotContainService<ExportJobExecutor>();
        services.ShouldNotContainService<ExportArtifactCleanupJob>();
        services.ShouldNotContainService<UploadJobExecutor>();
        services.ShouldNotContainService<ReplaceDatJobExecutor>();
        services.ShouldNotContainService<EnrichmentJobExecutor>();
        services.ShouldNotContainService<BulkEnrichmentJobExecutor>();
        services.ShouldNotContainService<MaterializationJobExecutor>();
        services.ShouldNotContainService<IMaterializationDataProvider>();
        services.ShouldNotContainService<ILibraryMaterializationService>();
        services.ShouldNotContainService<ILibraryMaterializationScheduler>();
        services.ShouldNotContainService<IMaterializationJobEnqueuer>();
        services.ShouldNotContainService<LibraryMaterializationReconciler>();
        services.ShouldNotContainService<IEnrichmentJobEnqueuer>();
        services.ShouldNotContainService<JobDispatchWorker>();
        services.ShouldNotContainService<CatalogProjectionRecoveryDispatcher>();
    }

    [Fact]
    public void ConsumerStyleRegistration_DoesNotRegisterSeedersRecurringJobsOrHangfireServer()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();

        services.ShouldContainHostedService<ServerInstanceIdentityInitializer>();
        services.ShouldNotContainHostedService<PostgreSqlSchemaProvisioner>();
        services.ShouldNotContainHostedService<AdminSeeder>();
        services.ShouldNotContainHostedService<SharedReferenceDataSeeder>();
        services.ShouldNotContainHostedService<RecurringJobRegistrar>();
        services.ShouldNotContainHostedService<LibraryMaterializationReconciler>();
        services.ShouldNotContainHostedService<JobDispatchWorker>();
        services.ShouldNotContainHostedService<CatalogProjectionRecoveryDispatcher>();
        services.ShouldNotContainService<HangfireJobStateSyncFilter>();
        services.ShouldNotContainService<ExportJobHangfireHandler>();
        services.ShouldNotContainService<OutboxJobNotifier>();
        services.ShouldNotContainService<OutboxStatsNotifier>();
        services.ShouldNotContainService<AdminRealtimeOutboxCleanupJob>();
        services.ShouldNotContainService<DatReplacementConvergenceSweepJob>();
        services.ShouldNotContainService<IAdminRealtimeEventSink>();
        services.ShouldNotContainHostedService<AdminRealtimeOutboxDispatcher>();
        services.ShouldNotContainService<BackgroundJobServer>();
    }

    [Fact]
    public void AdminApiStyleRegistration_RegistersEnqueueOnlySchedulingSurface()
    {
        var services = RomdBoundaryTestServices.CreateAdminApiStyleServices();

        services.ShouldContainService<IUploadJobCreator>();
        services.ShouldContainService<IReplaceDatJobCreator>();
        services.ShouldContainService<IEnrichmentScheduler>();
        services.ShouldContainService<IRematerializationScheduler>();
        services.ShouldContainService<ILibraryMaterializationScheduler>();
        services.ShouldContainService<IMaterializationJobEnqueuer>();
        services.ShouldContainService<IEnrichmentJobEnqueuer>();
        services.ShouldNotContainService<ITitleEnrichmentEvidenceReader>();
        services.ShouldNotContainService<IEnrichmentContextFactory>();
        services.ShouldContainService<IMetadataRematerializer>();
        services.ShouldContainService<IExportScheduler>();
        services.ShouldContainService<IAdminEventOutbox>();
        services.ShouldContainService<IAdminRealtimeOutbox>();
        services.ShouldContainService<AdminRealtimeOutboxNotifierCommitGate>();
        services.ShouldContainService<OutboxJobNotifier>();
        services.ShouldContainService<OutboxStatsNotifier>();
        services.ShouldContainService<IServerInstanceIdentity>();
        services.ShouldContainHostedService<ServerInstanceIdentityInitializer>();
        services.ShouldContainService<IUserAdministration>();
        services.ShouldContainService<IQueryHandler<ListUsersQuery, IReadOnlyList<ManagedUser>>>();
        services.ShouldContainService<IQueryHandler<GetUserByIdQuery, ManagedUser>>();
        services.ShouldContainService<ICommandHandler<CreateUserCommand, ManagedUser>>();
        services.ShouldContainService<ICommandHandler<UpdateUserCommand, ManagedUser>>();
        services.ShouldContainService<ICommandHandler<DeleteUserCommand, Deleted>>();
        services.ShouldContainService<ICommandHandler<AssignUserRoleCommand, ManagedUser>>();
        services.ShouldContainService<ICommandHandler<AssignUserLibraryCommand, ManagedUser>>();
        services.ShouldContainService<ICommandHandler<AssignDefaultLibraryToUsersCommand, int>>();

        services.ShouldNotContainService<HangfireJobStateSyncFilter>();
        services.ShouldNotContainOpenGenericService(typeof(JobRunner<>));
        services.ShouldNotContainService<UploadJobHangfireHandler>();
        services.ShouldNotContainService<ReplaceDatJobHangfireHandler>();
        services.ShouldNotContainService<EnrichmentJobHangfireHandler>();
        services.ShouldNotContainService<BulkEnrichmentJobHangfireHandler>();
        services.ShouldNotContainService<ExportJobHangfireHandler>();
        services.ShouldNotContainService<MaterializationJobHangfireHandler>();
        services.ShouldNotContainService<IJobExecutor<UploadJob>>();
        services.ShouldNotContainService<IJobExecutor<ReplaceDatJob>>();
        services.ShouldNotContainService<IJobExecutor<EnrichmentJob>>();
        services.ShouldNotContainService<IJobExecutor<BulkEnrichmentJob>>();
        services.ShouldNotContainService<IJobExecutor<ExportJob>>();
        services.ShouldNotContainService<IJobExecutor<MaterializationJob>>();
        services.ShouldNotContainService<UploadJobExecutor>();
        services.ShouldNotContainService<ReplaceDatJobExecutor>();
        services.ShouldNotContainService<EnrichmentJobExecutor>();
        services.ShouldNotContainService<BulkEnrichmentJobExecutor>();
        services.ShouldNotContainService<ExportJobExecutor>();
        services.ShouldNotContainService<MaterializationJobExecutor>();
        services.ShouldNotContainService<ExportArtifactCleanupJob>();
        services.ShouldNotContainService<IRematerializationService>();
        services.ShouldNotContainService<IMaterializationDataProvider>();
        services.ShouldNotContainService<ILibraryMaterializationService>();
        services.ShouldNotContainService<DatProcessor>();
        services.ShouldNotContainService<RomProcessor>();
        services.ShouldNotContainService<AdminRealtimeOutboxCleanupJob>();
        services.ShouldNotContainService<DatReplacementConvergenceSweepJob>();
        services.ShouldNotContainService<IAdminRealtimeEventSink>();
        services.ShouldNotContainHostedService<PostgreSqlSchemaProvisioner>();
        services.ShouldNotContainHostedService<AdminSeeder>();
        services.ShouldNotContainHostedService<SharedReferenceDataSeeder>();
        services.ShouldNotContainHostedService<RecurringJobRegistrar>();
        services.ShouldNotContainHostedService<LibraryMaterializationReconciler>();
        services.ShouldNotContainHostedService<JobDispatchWorker>();
        services.ShouldNotContainHostedService<CatalogProjectionRecoveryDispatcher>();
    }

    [Fact]
    public void WorkerStyleRegistration_RegistersExecutionRecurringMaterializationAndStateSync()
    {
        var services = RomdBoundaryTestServices.CreateWorkerStyleServices();

        services.ShouldContainService<HangfireJobStateSyncFilter>();
        services.ShouldContainOpenGenericService(typeof(JobRunner<>));
        services.ShouldContainService<UploadJobHangfireHandler>();
        services.ShouldContainService<ReplaceDatJobHangfireHandler>();
        services.ShouldContainService<EnrichmentJobHangfireHandler>();
        services.ShouldContainService<BulkEnrichmentJobHangfireHandler>();
        services.ShouldContainService<ExportJobHangfireHandler>();
        services.ShouldContainService<MaterializationJobHangfireHandler>();
        services.ShouldContainService<IJobExecutor<UploadJob>>();
        services.ShouldContainService<IJobExecutor<ReplaceDatJob>>();
        services.ShouldContainService<IJobExecutor<EnrichmentJob>>();
        services.ShouldContainService<IJobExecutor<BulkEnrichmentJob>>();
        services.ShouldContainService<IJobExecutor<ExportJob>>();
        services.ShouldContainService<IJobExecutor<MaterializationJob>>();
        services.ShouldContainService<ExportArtifactCleanupJob>();
        services.ShouldContainService<IAdminEventOutbox>();
        services.ShouldContainService<IAdminRealtimeOutbox>();
        services.ShouldContainService<AdminRealtimeOutboxNotifierCommitGate>();
        services.ShouldContainService<OutboxJobNotifier>();
        services.ShouldContainService<OutboxStatsNotifier>();
        services.ShouldContainService<AdminRealtimeOutboxCleanupJob>();
        services.ShouldContainService<DatReplacementConvergenceSweepJob>();
        services.ShouldContainService<IRematerializationService>();
        services.ShouldContainService<IRematerializationScheduler>();
        services.ShouldContainService<IMaterializationDataProvider>();
        services.ShouldContainService<ILibraryMaterializationService>();
        services.ShouldContainService<ILibraryMaterializationScheduler>();
        services.ShouldContainService<IMaterializationJobEnqueuer>();
        services.ShouldContainService<IEnrichmentJobEnqueuer>();
        services.ShouldContainService<ITitleEnrichmentEvidenceReader>();
        services.ShouldContainService<IRomCatalogMatchReader>();
        services.ShouldContainService<IRomOwnershipImpactReader>();
        services.ShouldContainService<IRomPayloadAssertionImpactReader>();
        services.ShouldContainService<IEnrichmentContextFactory>();
        services.ShouldContainService<IServerInstanceIdentity>();
        services.ShouldContainHostedService<ServerInstanceIdentityInitializer>();
        services.ShouldContainHostedService<OpenIddictSigningKeyInitializer>();
        services.ShouldContainHostedService<AdminSeeder>();
        services.ShouldContainHostedService<SharedReferenceDataSeeder>();
        services.ShouldContainHostedService<RecurringJobRegistrar>();
        services.ShouldContainHostedService<LibraryMaterializationReconciler>();
        services.ShouldContainHostedService<JobDispatchWorker>();
        services.ShouldContainHostedService<CatalogProjectionRecoveryDispatcher>();
        services.ShouldNotContainService<IAdminRealtimeEventSink>();
        services.ShouldNotContainHostedService<AdminRealtimeOutboxDispatcher>();
        services.ShouldNotContainService<IConsumerMediaArtifactResolver>();
        services.ShouldNotContainService<IConsumerContentArtifactResolver>();
        services.ShouldNotContainService<IConsumerContentGrantIssuer>();
    }

    [Fact]
    public void AdminApiStyleRegistration_RegistersFullReadinessCheckSet()
    {
        var services = RomdBoundaryTestServices.CreateAdminApiStyleServices();

        services.ShouldContainService<IReadinessEvaluator>();
        services.ShouldContainService<IHangfireStorageProbe>();
        services.ShouldContainService<IHangfireSchemaProbe>();
        GetReadinessCheckImplementations(services).ShouldBe(
            [
                typeof(DatabaseReadinessCheck),
                typeof(ApplicationSchemaReadinessCheck),
                typeof(DiskSpaceReadinessCheck),
                typeof(ContentStorageReadinessCheck),
                typeof(HangfireStorageReadinessCheck),
                typeof(AdminRealtimeOutboxReadinessCheck),
                typeof(WorkerHeartbeatReadinessCheck)
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void ConsumerStyleRegistration_RegistersOnlyDatabaseDiskAndStorageReadinessChecks()
    {
        var services = RomdBoundaryTestServices.CreateConsumerStyleServices();

        services.ShouldContainService<IReadinessEvaluator>();
        services.ShouldNotContainService<IHangfireStorageProbe>();
        services.ShouldContainService<IHangfireSchemaProbe>();
        GetReadinessCheckImplementations(services).ShouldBe(
            [
                typeof(DatabaseReadinessCheck),
                typeof(ApplicationSchemaReadinessCheck),
                typeof(DiskSpaceReadinessCheck),
                typeof(ContentStorageReadinessCheck),
                typeof(HangfireStorageReadinessCheck)
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void WorkerStyleRegistration_RegistersNoReadinessServices()
    {
        var services = RomdBoundaryTestServices.CreateWorkerStyleServices();

        services.ShouldNotContainService<IReadinessEvaluator>();
        services.ShouldNotContainService<IReadinessCheck>();
        services.ShouldNotContainService<IHangfireStorageProbe>();
        services.ShouldNotContainService<IHangfireSchemaProbe>();
    }

    private static Type[] GetReadinessCheckImplementations(IServiceCollection services) =>
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IReadinessCheck))
            .Select(descriptor => descriptor.ImplementationType)
            .Where(type => type is not null)
            .Cast<Type>()
            .ToArray();

    [Fact]
    public void RealtimeOutboxPublisherRegistration_UsesScopedConcreteIdentityAndOneSharedGate()
    {
        var services = new ServiceCollection();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql("Host=localhost;Database=romd;Username=romd")
            .Options;

        services.AddLogging();
        services.AddScoped(_ => new RomdDbContext(options));
        services.AddScoped(_ => Substitute.For<IAdminEventOutbox>());
        services.AddRomdRealtimeOutboxPublishers();

        var gateDescriptor = services
            .Where(candidate => candidate.ServiceType == typeof(AdminRealtimeOutboxNotifierCommitGate))
            .ShouldHaveSingleItem();
        gateDescriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
        AssertSingleScopedNotifier<IJobNotifier, OutboxJobNotifier>(services);
        AssertSingleScopedNotifier<IStatsNotifier, OutboxStatsNotifier>(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        ReferenceEquals(
                scope.ServiceProvider.GetRequiredService<IJobNotifier>(),
                scope.ServiceProvider.GetRequiredService<OutboxJobNotifier>())
            .ShouldBeTrue();
        ReferenceEquals(
                scope.ServiceProvider.GetRequiredService<IStatsNotifier>(),
                scope.ServiceProvider.GetRequiredService<OutboxStatsNotifier>())
            .ShouldBeTrue();
        ReferenceEquals(
                scope.ServiceProvider.GetRequiredService<AdminRealtimeOutboxNotifierCommitGate>(),
                scope.ServiceProvider.GetRequiredService<AdminRealtimeOutboxNotifierCommitGate>())
            .ShouldBeTrue();
        typeof(OutboxJobNotifier).GetConstructors().Single().GetParameters()
            .ShouldContain(parameter => parameter.ParameterType == typeof(AdminRealtimeOutboxNotifierCommitGate));
        typeof(OutboxStatsNotifier).GetConstructors().Single().GetParameters()
            .ShouldContain(parameter => parameter.ParameterType == typeof(AdminRealtimeOutboxNotifierCommitGate));
    }

    [Fact]
    public void ProductionCompositions_RegisterExactlyOneScopedOutboxNotifierPair()
    {
        IServiceCollection[] productionCompositions =
        [
            RomdBoundaryTestServices.CreateAdminApiStyleServices(),
            RomdBoundaryTestServices.CreateWorkerStyleServices(),
            new ServiceCollection().AddInfrastructure()
        ];

        foreach (IServiceCollection services in productionCompositions)
        {
            AssertSingleScopedNotifier<IJobNotifier, OutboxJobNotifier>(services);
            AssertSingleScopedNotifier<IStatsNotifier, OutboxStatsNotifier>(services);
            services
                .Where(candidate => candidate.ServiceType == typeof(AdminRealtimeOutboxNotifierCommitGate))
                .ShouldHaveSingleItem()
                .Lifetime.ShouldBe(ServiceLifetime.Scoped);
        }
    }

    [Fact]
    public void ConsumerAndTestingCompositions_RegisterExactlyOneScopedNoOpNotifierPair()
    {
        using var connection = PostgreSqlTestDatabase.Create();
        IServiceCollection[] noOpCompositions =
        [
            RomdBoundaryTestServices.CreateConsumerStyleServices(),
            new ServiceCollection().AddTestingInfrastructure(connection.ConnectionString)
        ];

        foreach (IServiceCollection services in noOpCompositions)
        {
            AssertSingleScopedNotifier<IJobNotifier, NoOpJobNotifier>(services);
            AssertSingleScopedNotifier<IStatsNotifier, NoOpStatsNotifier>(services);
            services.ShouldNotContainService<OutboxJobNotifier>();
            services.ShouldNotContainService<OutboxStatsNotifier>();
        }
    }

    [Fact]
    public void FullInfrastructureRegistration_RegistersCurrentSingleHostInfrastructure()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure();

        services.ShouldContainConfiguredOptions<IgdbProviderOptions>();
        services.ShouldContainService<IMetadataProvider>();
        services.ShouldContainService<IExportScheduler>();
        services.ShouldContainService<IJobExecutor<ExportJob>>();
        services.ShouldContainService<HangfireJobStateSyncFilter>();
        services.ShouldContainService<ExportJobHangfireHandler>();
        services.ShouldContainService<IAdminEventOutbox>();
        services.ShouldContainService<IAdminRealtimeOutbox>();
        services.ShouldContainService<AdminRealtimeOutboxNotifierCommitGate>();
        services.ShouldContainService<OutboxJobNotifier>();
        services.ShouldContainService<OutboxStatsNotifier>();
        services.ShouldContainService<AdminRealtimeOutboxCleanupJob>();
        services.ShouldContainService<DatReplacementConvergenceSweepJob>();
        services.ShouldContainService<IServerInstanceIdentity>();
        services.ShouldContainHostedService<ServerInstanceIdentityInitializer>();
        services.ShouldContainHostedService<OpenIddictSigningKeyInitializer>();
        services.ShouldContainHostedService<AdminSeeder>();
        services.ShouldContainHostedService<RecurringJobRegistrar>();
        services.ShouldContainService<IUserAdministration>();
        services.ShouldNotContainService<IConsumerMediaArtifactResolver>();
        services.ShouldNotContainService<IConsumerContentArtifactResolver>();
        services.ShouldNotContainService<IConsumerContentGrantIssuer>();
    }

    [Fact]
    public void HostStyleRegistrations_RegisterServerIdentityInitializerBeforeOtherHostedServices()
    {
        IServiceCollection[] hostStyles =
        [
            RomdBoundaryTestServices.CreateConsumerStyleServices(),
            RomdBoundaryTestServices.CreateAdminApiStyleServices(),
            RomdBoundaryTestServices.CreateWorkerStyleServices(),
            new ServiceCollection().AddInfrastructure()
        ];

        foreach (IServiceCollection services in hostStyles)
        {
            Type[] hostedServiceTypes = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(descriptor => descriptor.ImplementationType)
                .Where(type => type is not null)
                .Cast<Type>()
                .ToArray();

            hostedServiceTypes.ShouldNotBeEmpty();
            hostedServiceTypes[0].ShouldBe(typeof(ServerInstanceIdentityInitializer));
        }
    }

    private static bool IsCqrsHandlerServiceType(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)
            || type.GetGenericTypeDefinition() == typeof(ICommandHandler<,>));

    private static bool IsConsumerHandlerServiceType(Type type) =>
        type.GenericTypeArguments[0].Namespace?.StartsWith("Romd.Consumer.Application.", StringComparison.Ordinal) == true;

    private static void AssertSingleScopedNotifier<TPort, TImplementation>(IServiceCollection services)
        where TPort : class
        where TImplementation : class, TPort
    {
        var portDescriptor = services
            .Where(candidate => candidate.ServiceType == typeof(TPort))
            .ShouldHaveSingleItem();
        portDescriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);

        if (portDescriptor.ImplementationType is not null)
        {
            portDescriptor.ImplementationType.ShouldBe(typeof(TImplementation));
            return;
        }

        portDescriptor.ImplementationFactory.ShouldNotBeNull();
        services
            .Where(candidate => candidate.ServiceType == typeof(TImplementation))
            .ShouldHaveSingleItem()
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }
}
