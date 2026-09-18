using Romd.Admin.Application.Libraries.Queries.EvaluateLibrary;
using System.Reflection;
using ErrorOr;
using Romd.Admin.Application.Libraries.Commands.SetLibraryAttachments;
using Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Commands.CreateLibrary;
using Romd.Admin.Application.Libraries.Commands.DeleteLibrary;
using Romd.Admin.Application.Libraries.Commands.ForceMaterializeLibrary;
using Romd.Admin.Application.Libraries.Commands.UpdateLibrary;
using Romd.Admin.Application.Libraries.Queries.GetLibraryById;
using Romd.Admin.Application.Libraries.Queries.GetLibraryCollections;
using Romd.Admin.Application.Libraries.Queries.GetLibraryGenreFacets;
using Romd.Admin.Application.Libraries.Queries.GetLibraryPlatformFacets;
using Romd.Admin.Application.Libraries.Queries.GetLibraryTitleReleaseDiagnostics;
using Romd.Admin.Application.Libraries.Queries.ListLibraries;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Management.Models;
using Romd.Domain.Libraries;
using Romd.Host.Authorization;
using Romd.Host.Endpoints;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class LibraryEndpointsTests
{
    [Fact]
    public async Task Evaluate_MapsDraftWithoutCallingSave()
    {
        var handler = Substitute.For<IQueryHandler<EvaluateLibraryQuery, LibraryEvaluationDto>>();
        handler.HandleAsync(Arg.Any<EvaluateLibraryQuery>(), Arg.Any<CancellationToken>())
            .Returns(new LibraryEvaluationDto(DateTimeOffset.UtcNow, 2, 1, 0, 1, [], [], null));
        var result = await InvokeAsync("Evaluate", new Sqid(7),
            new LibraryEvaluationRequest(new LibraryConfigurationDto { ShowMissingGames = true }, "removed", "Mario"),
            handler, CancellationToken.None);
        result.ShouldBeOfType<Ok<LibraryEvaluationDto>>();
        await handler.Received(1).HandleAsync(Arg.Is<EvaluateLibraryQuery>(q =>
            q.LibraryId == 7 && q.Configuration.ShowMissingGames && q.View == "removed" && q.Search == "Mario"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attachments_DecodeCollectionIdsAndPreservePlacementOrder()
    {
        var handler = Substitute.For<ICommandHandler<SetLibraryAttachmentsCommand, Success>>();
        handler.HandleAsync(Arg.Any<SetLibraryAttachmentsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        var response = await InvokeAsync("SetAttachments", new Sqid(7),
            new SetLibraryAttachmentsRequest([new(IdCoder.Encode(12), false), new(IdCoder.Encode(9), true)]),
            handler, CancellationToken.None);
        response.ShouldBeOfType<NoContent>();
        await handler.Received(1).HandleAsync(Arg.Is<SetLibraryAttachmentsCommand>(c =>
            c.LibraryId == 7 && c.Collections[0].CollectionId == 12 && !c.Collections[0].IsFeatured &&
            c.Collections[1].CollectionId == 9 && c.Collections[1].IsFeatured), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_InvalidCursor_IsRejectedBeforeQuerying()
    {
        var handler = Substitute.For<IQueryHandler<GetLibraryPreviewQuery, LibraryPreviewDto>>();
        (await InvokeAsync("GetPreview", new Sqid(7), handler, null, "invalid", CancellationToken.None))
            .ShouldBeOfType<BadRequest>();
        await handler.DidNotReceive().HandleAsync(Arg.Any<GetLibraryPreviewQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void InScopeMethods_UseOnlyFocusedApplicationHandlers()
    {
        AssertParameters("GetAll",
            typeof(IQueryHandler<ListLibrariesQuery, IReadOnlyList<LibraryDto>>),
            typeof(CancellationToken));
        AssertParameters("GetById",
            typeof(Sqid),
            typeof(IQueryHandler<GetLibraryByIdQuery, LibraryDto>),
            typeof(CancellationToken));
        AssertParameters("Create",
            typeof(CreateLibraryRequest),
            typeof(ICommandHandler<CreateLibraryCommand, LibraryDto>),
            typeof(CancellationToken));
        AssertParameters("Update",
            typeof(Sqid),
            typeof(UpdateLibraryRequest),
            typeof(ICommandHandler<UpdateLibraryCommand, LibraryDto>),
            typeof(CancellationToken));
        AssertParameters("Delete",
            typeof(Sqid),
            typeof(ICommandHandler<DeleteLibraryCommand, Deleted>),
            typeof(CancellationToken));
        AssertParameters("ForceMaterialize",
            typeof(Sqid),
            typeof(ICommandHandler<ForceMaterializeLibraryCommand, Guid>),
            typeof(CancellationToken));
        AssertParameters("GetPlatformFacets",
            typeof(Sqid),
            typeof(IQueryHandler<GetLibraryPlatformFacetsQuery, IReadOnlyList<LibraryFacetDto>>),
            typeof(int),
            typeof(CancellationToken));
        AssertParameters("GetGenreFacets",
            typeof(Sqid),
            typeof(IQueryHandler<GetLibraryGenreFacetsQuery, IReadOnlyList<LibraryFacetDto>>),
            typeof(int),
            typeof(CancellationToken));
        AssertParameters("GetCollections",
            typeof(Sqid),
            typeof(IQueryHandler<GetLibraryCollectionsQuery, IReadOnlyList<LibraryCollectionDto>>),
            typeof(int),
            typeof(CancellationToken));
        AssertParameters("GetTitleReleaseDiagnostics",
            typeof(Sqid),
            typeof(Sqid),
            typeof(IQueryHandler<
                GetLibraryTitleReleaseDiagnosticsQuery,
                IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>>),
            typeof(CancellationToken));

        var forbidden = new[]
        {
            typeof(ILibraryRepository),
            typeof(ILibraryMaterializationScheduler),
            typeof(IAdminEventOutbox),
            typeof(RomdDbContext)
        };
        InScopeMethodNames
            .SelectMany(name => EndpointMethod(name).GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ShouldAllBe(type => !forbidden.Contains(type));
    }

    [Fact]
    public async Task Create_MapsRequestToCommandAndUsesGeneratedIdForLocation()
    {
        var handler = Substitute.For<ICommandHandler<CreateLibraryCommand, LibraryDto>>();
        string id = IdCoder.Encode(17);
        var created = NewDto(id, "Arcade", isDefault: true);
        handler.HandleAsync(Arg.Any<CreateLibraryCommand>(), Arg.Any<CancellationToken>())
            .Returns(created);
        var request = new CreateLibraryRequest(
            "Arcade",
            new LibraryConfigurationDto
            {
                TitleSelectionMode = Romd.Contracts.Management.Libraries.LibraryTitleSelectionMode.IncludeOnly,
                IncludeTitleIds = [IdCoder.Encode(42)],
                ShowMissingGames = true
            },
            IsDefault: true);

        var result = await InvokeAsync("Create", request, handler, CancellationToken.None);

        var response = result.ShouldBeOfType<Created<LibraryDto>>();
        response.Location.ShouldBe($"/api/libraries/{id}");
        response.Value.ShouldBe(created);
        await handler.Received(1).HandleAsync(
            Arg.Is<CreateLibraryCommand>(command =>
                command.Name == "Arcade" &&
                command.IsDefault &&
                command.Configuration.TitleSelectionMode ==
                Romd.Domain.Libraries.LibraryTitleSelectionMode.IncludeOnly &&
                command.Configuration.IncludeTitleIds.SequenceEqual(new[] { 42 }) &&
                command.Configuration.ShowMissingGames),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_MapsRouteAndOptionalDefaultToCommand()
    {
        var handler = Substitute.For<ICommandHandler<UpdateLibraryCommand, LibraryDto>>();
        handler.HandleAsync(Arg.Any<UpdateLibraryCommand>(), Arg.Any<CancellationToken>())
            .Returns(NewDto(IdCoder.Encode(7), "Updated", isDefault: false));

        var result = await InvokeAsync(
            "Update",
            new Sqid(7),
            new UpdateLibraryRequest("Updated", IsDefault: false),
            handler,
            CancellationToken.None);

        result.ShouldBeOfType<Ok<LibraryDto>>();
        await handler.Received(1).HandleAsync(
            Arg.Is<UpdateLibraryCommand>(command =>
                command.LibraryId == 7 &&
                command.Name == "Updated" &&
                command.Configuration == null &&
                command.IsDefault == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPlatformFacets_PassesMinimumItemsToQuery()
    {
        var handler = Substitute.For<IQueryHandler<
            GetLibraryPlatformFacetsQuery,
            IReadOnlyList<LibraryFacetDto>>>();
        handler.HandleAsync(Arg.Any<GetLibraryPlatformFacetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<LibraryFacetDto>());

        var result = await InvokeAsync(
            "GetPlatformFacets",
            new Sqid(7),
            handler,
            3,
            CancellationToken.None);

        result.ShouldBeOfType<Ok<IReadOnlyList<LibraryFacetDto>>>();
        await handler.Received(1).HandleAsync(
            new GetLibraryPlatformFacetsQuery(7, 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTitleReleaseDiagnostics_PassesBothOpaqueIdsToQuery()
    {
        var handler = Substitute.For<IQueryHandler<
            GetLibraryTitleReleaseDiagnosticsQuery,
            IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>>>();
        handler.HandleAsync(
                Arg.Any<GetLibraryTitleReleaseDiagnosticsQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<LibraryTitleReleaseDiagnosticsDto>());

        await InvokeAsync(
            "GetTitleReleaseDiagnostics",
            new Sqid(7),
            new Sqid(9),
            handler,
            CancellationToken.None);

        await handler.Received(1).HandleAsync(
            new GetLibraryTitleReleaseDiagnosticsQuery(7, 9),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("GetById")]
    [InlineData("GetPlatformFacets")]
    [InlineData("GetGenreFacets")]
    [InlineData("GetCollections")]
    [InlineData("GetTitleReleaseDiagnostics")]
    [InlineData("Update")]
    [InlineData("Delete")]
    [InlineData("ForceMaterialize")]
    public async Task MissingLibrary_ReturnsBodylessNotFound(string methodName)
    {
        var result = await InvokeMissingAsync(methodName);

        result.ShouldBeOfType<NotFound>();
    }

    [Fact]
    public async Task Create_NameValidationError_ReturnsSharedFieldProblem()
    {
        var handler = Substitute.For<ICommandHandler<CreateLibraryCommand, LibraryDto>>();
        handler.HandleAsync(Arg.Any<CreateLibraryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<LibraryDto>>(LibraryErrors.NameRequired()));

        var result = await InvokeAsync(
            "Create",
            new CreateLibraryRequest(""),
            handler,
            CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode
            .ShouldBe(StatusCodes.Status400BadRequest);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem!.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Libraries.NameRequired");
        var errors = problem.Extensions[ProblemResults.ErrorsExtensionName]
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>();
        errors!.Keys.ShouldContain("name");
    }

    [Fact]
    public async Task ForceMaterialize_ReturnsTypedAcceptedJobReferenceAndLocation()
    {
        var handler = Substitute.For<ICommandHandler<ForceMaterializeLibraryCommand, Guid>>();
        var jobId = Guid.NewGuid();
        handler.HandleAsync(Arg.Any<ForceMaterializeLibraryCommand>(), Arg.Any<CancellationToken>())
            .Returns(jobId);

        var result = await InvokeAsync(
            "ForceMaterialize",
            new Sqid(7),
            handler,
            CancellationToken.None);

        var accepted = result.ShouldBeOfType<Accepted<JobReference>>();
        accepted.Location.ShouldBe($"/api/jobs/{jobId}");
        accepted.Value.ShouldBe(new JobReference(jobId));
        await handler.Received(1).HandleAsync(
            new ForceMaterializeLibraryCommand(7),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteMetadata_PreservesExactNamesAuthorizationAndResponseStatuses()
    {
        var builder = WebApplication.CreateBuilder();
        RegisterRouteDependencies(builder.Services);
        await using var app = builder.Build();
        app.MapLibraryEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        EndpointExpectation[] expected =
        [
            new("{libraryId}/evaluate", "POST", "EvaluateLibrary", [200, 400, 404, 409]),
            new("{libraryId}/attachments", "GET", "GetLibraryAttachments", [200]),
            new("{libraryId}/attachments", "PUT", "SetLibraryAttachments", [204, 400]),
            new("{libraryId}/preview", "GET", "GetLibraryPreview", [200]),
            new("collection-placements/{collectionId}", "GET", "GetCollectionLibraryPlacements", [200]),
            new("", "GET", "ListLibraries", [200]),
            new("{libraryId}", "GET", "GetLibraryById", [200, 404]),
            new("", "POST", "CreateLibrary", [201, 400]),
            new("{libraryId}", "PUT", "UpdateLibrary", [200, 400, 404]),
            new("{libraryId}", "DELETE", "DeleteLibrary", [204, 404, 409]),
            new("{libraryId}/materialize", "POST", "ForceMaterializeLibrary", [202, 404]),
            new("{libraryId}/facets/systems", "GET", "GetLibraryPlatformFacets", [200]),
            new("{libraryId}/facets/genres", "GET", "GetLibraryGenreFacets", [200]),
            new("{libraryId}/collections", "GET", "GetLibraryCollections", [200]),
            new("{libraryId}/titles/{titleId}/releases", "GET", "GetLibraryTitleReleaseDiagnostics", [200, 404])
        ];

        endpoints.Count.ShouldBe(expected.Length);
        foreach (var item in expected)
        {
            var endpoint = endpoints.Single(candidate =>
                NormalizeRoute(candidate.RoutePattern.RawText) == NormalizeRoute($"/libraries/{item.Route}") &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(item.HttpMethod) == true);
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName.ShouldBe(item.Name);
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(metadata => metadata.Policy)
                .ShouldContain(AuthorizationPolicies.RequireAdmin);
            endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Select(metadata => metadata.StatusCode)
                .Order()
                .ShouldBe(item.StatusCodes.Order());
        }

        var bodylessNotFoundEndpoints = endpoints
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Any(metadata => metadata.StatusCode == StatusCodes.Status404NotFound))
            .ToList();
        foreach (var endpoint in bodylessNotFoundEndpoints)
        {
            var metadata = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Single(item => item.StatusCode == StatusCodes.Status404NotFound);
            metadata.Type.ShouldBe(typeof(void));
            metadata.ContentTypes.ShouldBeEmpty();
        }

        var forceMaterialize = endpoints.Single(endpoint =>
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ==
            "ForceMaterializeLibrary");
        forceMaterialize.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Single(metadata => metadata.StatusCode == StatusCodes.Status202Accepted)
            .Type.ShouldBe(typeof(JobReference));
    }

    private static readonly string[] InScopeMethodNames =
    [
        "GetAll",
        "GetById",
        "Create",
        "Update",
        "Delete",
        "ForceMaterialize",
        "GetPlatformFacets",
        "GetGenreFacets",
        "GetCollections",
        "GetTitleReleaseDiagnostics"
    ];

    private static async Task<IResult> InvokeMissingAsync(string methodName)
    {
        return methodName switch
        {
            "GetById" => await InvokeMissingQuery<GetLibraryByIdQuery, LibraryDto>(methodName, new Sqid(7)),
            "GetPlatformFacets" => await InvokeMissingQuery<
                GetLibraryPlatformFacetsQuery,
                IReadOnlyList<LibraryFacetDto>>(methodName, [new Sqid(7)], [1]),
            "GetGenreFacets" => await InvokeMissingQuery<
                GetLibraryGenreFacetsQuery,
                IReadOnlyList<LibraryFacetDto>>(methodName, [new Sqid(7)], [1]),
            "GetCollections" => await InvokeMissingQuery<
                GetLibraryCollectionsQuery,
                IReadOnlyList<LibraryCollectionDto>>(methodName, [new Sqid(7)], [1]),
            "GetTitleReleaseDiagnostics" => await InvokeMissingQuery<
                GetLibraryTitleReleaseDiagnosticsQuery,
                IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>>(
                    methodName,
                    [new Sqid(7), new Sqid(9)],
                    []),
            "Update" => await InvokeMissingCommand<UpdateLibraryCommand, LibraryDto>(
                methodName,
                new Sqid(7),
                new UpdateLibraryRequest("Missing")),
            "Delete" => await InvokeMissingCommand<DeleteLibraryCommand, Deleted>(methodName, new Sqid(7)),
            "ForceMaterialize" => await InvokeMissingCommand<ForceMaterializeLibraryCommand, Guid>(
                methodName,
                new Sqid(7)),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName))
        };
    }

    private static async Task<IResult> InvokeMissingQuery<TQuery, TResult>(
        string methodName,
        params object[] prefix)
        where TQuery : IQuery<TResult>
        => await InvokeMissingQuery<TQuery, TResult>(methodName, prefix, []);

    private static async Task<IResult> InvokeMissingQuery<TQuery, TResult>(
        string methodName,
        object[] prefix,
        object[] suffix)
        where TQuery : IQuery<TResult>
    {
        var handler = Substitute.For<IQueryHandler<TQuery, TResult>>();
        handler.HandleAsync(Arg.Any<TQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<TResult>>(LibraryErrors.NotFound()));
        return await InvokeAsync(methodName, [.. prefix, handler, .. suffix, CancellationToken.None]);
    }

    private static async Task<IResult> InvokeMissingCommand<TCommand, TResult>(
        string methodName,
        params object[] prefix)
        where TCommand : ICommand<TResult>
    {
        var handler = Substitute.For<ICommandHandler<TCommand, TResult>>();
        handler.HandleAsync(Arg.Any<TCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<TResult>>(LibraryErrors.NotFound()));
        return await InvokeAsync(methodName, [.. prefix, handler, CancellationToken.None]);
    }

    private static void RegisterRouteDependencies(IServiceCollection services)
    {
        services.AddSingleton(Substitute.For<IQueryHandler<EvaluateLibraryQuery, LibraryEvaluationDto>>());
        services.AddSingleton(Substitute.For<IQueryHandler<GetLibraryAttachmentsQuery, IReadOnlyList<LibraryAttachmentDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<GetLibraryPreviewQuery, LibraryPreviewDto>>());
        services.AddSingleton(Substitute.For<IQueryHandler<GetCollectionPlacementsQuery, IReadOnlyList<CollectionLibraryPlacementDto>>>());
        services.AddSingleton(Substitute.For<ICommandHandler<SetLibraryAttachmentsCommand, Success>>());
        services.AddSingleton(Substitute.For<IQueryHandler<ListLibrariesQuery, IReadOnlyList<LibraryDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<GetLibraryByIdQuery, LibraryDto>>());
        services.AddSingleton(Substitute.For<ICommandHandler<CreateLibraryCommand, LibraryDto>>());
        services.AddSingleton(Substitute.For<ICommandHandler<UpdateLibraryCommand, LibraryDto>>());
        services.AddSingleton(Substitute.For<ICommandHandler<DeleteLibraryCommand, Deleted>>());
        services.AddSingleton(Substitute.For<ICommandHandler<ForceMaterializeLibraryCommand, Guid>>());
        services.AddSingleton(Substitute.For<IQueryHandler<
            GetLibraryPlatformFacetsQuery,
            IReadOnlyList<LibraryFacetDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<
            GetLibraryGenreFacetsQuery,
            IReadOnlyList<LibraryFacetDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<
            GetLibraryCollectionsQuery,
            IReadOnlyList<LibraryCollectionDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<
            GetLibraryTitleReleaseDiagnosticsQuery,
            IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>>>());
    }

    private static async Task<IResult> InvokeAsync(string methodName, params object?[] args)
    {
        var method = EndpointMethod(methodName);
        if (method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Romd.Application.Common.ReferenceCatalog.IReferenceCatalogService))
            args = [Romd.Hosting.IntegrationTests.Infrastructure.TestReferenceCatalog.Create(), .. args];
        var task = method.Invoke(null, args);
        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static MethodInfo EndpointMethod(string methodName)
    {
        var method = typeof(LibraryEndpoints).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        return method;
    }

    private static void AssertParameters(string methodName, params Type[] expected) =>
        EndpointMethod(methodName).GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Where(type => type != typeof(Romd.Application.Common.ReferenceCatalog.IReferenceCatalogService))
            .ShouldBe(expected);

    private static string NormalizeRoute(string? route) =>
        (route ?? string.Empty).TrimEnd('/');

    private static LibraryDto NewDto(string id, string name, bool isDefault) =>
        new()
        {
            Id = id,
            Name = name,
            Configuration = new LibraryConfigurationDto(),
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            IsDefault = isDefault,
            NeedsMaterialization = true,
            ItemCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private sealed record EndpointExpectation(
        string Route,
        string HttpMethod,
        string Name,
        int[] StatusCodes);
}
