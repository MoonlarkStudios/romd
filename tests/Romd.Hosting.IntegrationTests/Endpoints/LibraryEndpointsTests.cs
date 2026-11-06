using Romd.Contracts.Common.Models;
using Romd.Admin.Application.Libraries.Queries.EvaluateLibrary;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
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
using Romd.Admin.Application.Libraries.Queries.GetLibraryDiscSetOptions;
using Romd.Admin.Application.Libraries.Queries.GetLibraryCollections;
using Romd.Admin.Application.Libraries.Queries.GetLibraryGenreFacets;
using Romd.Admin.Application.Libraries.Queries.GetLibraryPlatformFacets;
using Romd.Admin.Application.Libraries.Queries.GetLibraryTitleReleaseDiagnostics;
using Romd.Admin.Application.Libraries.Queries.ListLibraries;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Validation;
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
            .Returns(new LibraryEvaluationDto(DateTimeOffset.UtcNow, 2, 1, 0, 1, [], new Page<LibraryEvaluationTitleDto> { Items = [], HasNextPage = false }));
        var result = await InvokeAsync("Evaluate", new Sqid(7),
            new LibraryEvaluationRequest(new LibraryConfigurationDto { ShowMissingGames = true, IncludeUntrackedTitles = true }, "removed", "Mario", "opaque-draft-cursor", 37),
            handler, CancellationToken.None);
        result.ShouldBeOfType<Ok<LibraryEvaluationDto>>();
        await handler.Received(1).HandleAsync(Arg.Is<EvaluateLibraryQuery>(q =>
            q.LibraryId == 7 && q.Configuration.ShowMissingGames && q.Configuration.IncludeUntrackedTitles && q.View == "removed" && q.Search == "Mario" &&
            q.AfterId == 0 && q.Cursor == "opaque-draft-cursor" && q.Limit == 37),
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

    [Theory]
    [InlineData("invalid")]
    [InlineData("3:eED9Qbk")]
    public async Task Preview_MapsOpaqueCursorAndPreservesHandlerCursorRejection(string cursor)
    {
        var handler = Substitute.For<IQueryHandler<GetLibraryPreviewQuery, Page<LibraryPreviewTitleDto>>>();
        handler.HandleAsync(Arg.Any<GetLibraryPreviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<Page<LibraryPreviewTitleDto>>>(
                FieldErrors.Validation("Libraries.InvalidCursor", "The cursor is invalid for this query.", "cursor")));
        var result = await InvokeAsync("GetPreview", new Sqid(7), handler, new Sqid(9), cursor, 23, CancellationToken.None);
        AssertFieldProblem(result, "Libraries.InvalidCursor", "cursor");
        await handler.Received(1).HandleAsync(new GetLibraryPreviewQuery(7, 9, cursor, 23), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attachments_InvalidCollectionId_IsRejectedWithItsField()
    {
        var handler = Substitute.For<ICommandHandler<SetLibraryAttachmentsCommand, Success>>();
        var result = await InvokeAsync("SetAttachments", new Sqid(7),
            new SetLibraryAttachmentsRequest([new(IdCoder.Encode(12), false), new("not-a-sqid", true)]),
            handler, CancellationToken.None);
        AssertFieldProblem(result, "Libraries.InvalidId", "collections");
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task Evaluate_InvalidConfigurationId_IsRejectedWithItsField()
    {
        var handler = Substitute.For<IQueryHandler<EvaluateLibraryQuery, LibraryEvaluationDto>>();
        var result = await InvokeAsync("Evaluate", new Sqid(7),
            new LibraryEvaluationRequest(new LibraryConfigurationDto { ExcludeTitleIds = ["not-a-sqid"] }, "all", null),
            handler, CancellationToken.None);
        AssertFieldProblem(result, "Libraries.InvalidConfiguration", "configuration.excludeTitleIds");
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task Evaluate_InvalidConfiguration_PrecedesOpaqueCursorValidation()
    {
        var handler = Substitute.For<IQueryHandler<EvaluateLibraryQuery, LibraryEvaluationDto>>();
        var result = await InvokeAsync("Evaluate", new Sqid(7),
            new LibraryEvaluationRequest(new LibraryConfigurationDto { ExcludeTitleIds = ["not-a-sqid"] }, "all", null, "eED9Qbk"),
            handler, CancellationToken.None);
        AssertFieldProblem(result, "Libraries.InvalidConfiguration", "configuration.excludeTitleIds");
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task Evaluate_MapsOpaqueCursorAndPreservesHandlerCursorRejection()
    {
        var handler = Substitute.For<IQueryHandler<EvaluateLibraryQuery, LibraryEvaluationDto>>();
        handler.HandleAsync(Arg.Any<EvaluateLibraryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<LibraryEvaluationDto>>(
                FieldErrors.Validation("Libraries.InvalidCursor", "The cursor is invalid for this configuration preview.", "cursor")));
        var result = await InvokeAsync("Evaluate", new Sqid(7),
            new LibraryEvaluationRequest(new LibraryConfigurationDto(), "all", null, "not-a-sqid"),
            handler, CancellationToken.None);
        AssertFieldProblem(result, "Libraries.InvalidCursor", "cursor");
        await handler.Received(1).HandleAsync(Arg.Is<EvaluateLibraryQuery>(query =>
            query.LibraryId == 7 && query.Cursor == "not-a-sqid" && query.Limit == 48 && query.AfterId == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_MissingNameAndInvalidConfiguration_ReportsBothInOneEnvelope()
    {
        var handler = Substitute.For<ICommandHandler<CreateLibraryCommand, LibraryDto>>();
        var configuration = new LibraryConfigurationDto { AllowedSystemKeys = ["no-such-system"] };

        var result = await InvokeAsync("Create", new CreateLibraryRequest(" ", configuration), handler, CancellationToken.None);

        AssertFieldProblem(result, "Libraries.InvalidConfiguration", "name", "configuration.allowedSystemKeys");
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task Update_MissingNameAndInvalidConfiguration_ReportsBothInOneEnvelope()
    {
        var handler = Substitute.For<ICommandHandler<UpdateLibraryCommand, LibraryDto>>();
        var configuration = new LibraryConfigurationDto { AllowedSystemKeys = ["no-such-system"] };

        var result = await InvokeAsync("Update", new Sqid(7), new UpdateLibraryRequest(" ", configuration), handler,
            CancellationToken.None);

        AssertFieldProblem(result, "Libraries.InvalidConfiguration", "name", "configuration.allowedSystemKeys");
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task Create_SeveralInvalidConfigurationFields_ReportsEveryFieldInOneEnvelope()
    {
        var handler = Substitute.For<ICommandHandler<CreateLibraryCommand, LibraryDto>>();
        var configuration = new LibraryConfigurationDto
        {
            AllowedSystemKeys = ["no-such-system"],
            ExcludedDatIds = ["not-a-sqid"],
            IncludeTitleIds = [IdCoder.Encode(4), "eED9Qbk"]
        };

        var result = await InvokeAsync("Create", new CreateLibraryRequest("Kids", configuration), handler, CancellationToken.None);

        AssertFieldProblem(result, "Libraries.InvalidConfiguration",
            "configuration.allowedSystemKeys", "configuration.excludedDatIds", "configuration.includeTitleIds");
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public void InScopeMethods_UseOnlyFocusedApplicationHandlers()
    {
        AssertParameters("GetAll",
            typeof(IQueryHandler<ListLibrariesQuery, Page<LibraryDto>>),
            typeof(string),
            typeof(string),
            typeof(CancellationToken),
            typeof(int));
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
            typeof(IQueryHandler<GetLibraryGenreFacetsQuery, Page<LibraryFacetDto>>),
            typeof(string),
            typeof(CancellationToken),
            typeof(int),
            typeof(int));
        AssertParameters("GetCollections",
            typeof(Sqid),
            typeof(IQueryHandler<GetLibraryCollectionsQuery, Page<LibraryCollectionDto>>),
            typeof(string),
            typeof(CancellationToken),
            typeof(int),
            typeof(int));
        AssertParameters("GetTitleReleaseDiagnostics",
            typeof(Sqid),
            typeof(Sqid),
            typeof(IQueryHandler<
                GetLibraryTitleReleaseDiagnosticsQuery,
                Page<LibraryTitleReleaseDiagnosticsDto>>),
            typeof(string),
            typeof(CancellationToken),
            typeof(int));

        var forbidden = new[]
        {
            typeof(ILibraryRepository),
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
            GetLibraryTitleReleaseDiagnosticsQuery, Page<LibraryTitleReleaseDiagnosticsDto>>>();
        handler.HandleAsync(
                Arg.Any<GetLibraryTitleReleaseDiagnosticsQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(new Page<LibraryTitleReleaseDiagnosticsDto> { Items = [], HasNextPage = false });

        await InvokeAsync(
            "GetTitleReleaseDiagnostics",
            new Sqid(7),
            new Sqid(9),
            handler,
            "opaque-release-cursor",
            CancellationToken.None,
            37);

        await handler.Received(1).HandleAsync(
            new GetLibraryTitleReleaseDiagnosticsQuery(7, 9, "opaque-release-cursor", 37),
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
    public async Task MissingLibrary_ReturnsExactProblemEnvelope(string methodName)
    {
        var result = await InvokeMissingAsync(methodName);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Libraries.NotFound");
        problem.Detail.ShouldBe("Library not found.");
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var bodyStream = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            TraceIdentifier = "missing-library-test",
            Response = { Body = bodyStream }
        };
        await result.ExecuteAsync(context);
        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        bodyStream.Position = 0;
        using var body = await JsonDocument.ParseAsync(bodyStream);
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
        body.RootElement.GetProperty("errorCode").GetString().ShouldBe("Libraries.NotFound");
        body.RootElement.GetProperty("detail").GetString().ShouldBe("Library not found.");
        body.RootElement.GetProperty("traceId").GetString().ShouldBe(Activity.Current?.Id ?? context.TraceIdentifier);
    }

    [Fact]
    public async Task Create_MissingName_IsRejectedBeforeTheHandlerWithTheSharedFieldProblem()
    {
        var handler = Substitute.For<ICommandHandler<CreateLibraryCommand, LibraryDto>>();

        var result = await InvokeAsync(
            "Create",
            new CreateLibraryRequest(""),
            handler,
            CancellationToken.None);

        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);

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
            new("/libraries/{libraryId}/configuration/preview", "POST", "PreviewLibraryConfiguration", [200, 400, 404, 409], typeof(LibraryEvaluationDto)),
            new("/libraries/{libraryId}/attachments", "GET", "ListLibraryAttachments", [200, 404], typeof(IReadOnlyList<LibraryAttachmentDto>)),
            new("/libraries/{libraryId}/attachments", "PUT", "ReplaceLibraryAttachments", [204, 400, 404], typeof(void)),
            new("/libraries/{libraryId}/preview", "GET", "ListLibraryPreviewTitles", [200, 400, 404], typeof(Page<LibraryPreviewTitleDto>)),
            new("/collections/{collectionId}/library-placements", "GET", "ListCollectionLibraryPlacements", [200, 400, 404], typeof(Page<CollectionLibraryPlacementDto>)),
            new("/libraries", "GET", "ListLibraries", [200, 400], typeof(Page<LibraryDto>)),
            new("/libraries/{libraryId}", "GET", "GetLibrary", [200, 404], typeof(LibraryDto)),
            new("/libraries/{libraryId}/disc-set-options", "GET", "GetLibraryDiscSetOptions", [200, 400, 404], typeof(LibraryDiscSetOptionsDto)),
            new("/libraries", "POST", "CreateLibrary", [201, 400], typeof(LibraryDto)),
            new("/libraries/{libraryId}", "PATCH", "UpdateLibrary", [200, 400, 404], typeof(LibraryDto)),
            new("/libraries/{libraryId}", "DELETE", "DeleteLibrary", [204, 404, 409], typeof(void)),
            new("/libraries/{libraryId}/materialize", "POST", "MaterializeLibrary", [202, 404], typeof(JobReference)),
            new("/libraries/{libraryId}/facets/systems", "GET", "ListLibrarySystemFacets", [200, 404], typeof(IEnumerable<LibraryFacetDto>)),
            new("/libraries/{libraryId}/facets/genres", "GET", "ListLibraryGenreFacets", [200, 400, 404], typeof(Page<LibraryFacetDto>)),
            new("/libraries/{libraryId}/collections", "GET", "ListLibraryCollections", [200, 400, 404], typeof(Page<LibraryCollectionDto>)),
            new("/libraries/{libraryId}/titles/{titleId}/releases", "GET", "ListLibraryTitleReleaseDiagnostics", [200, 400, 404], typeof(Page<LibraryTitleReleaseDiagnosticsDto>))
        ];

        endpoints.Count.ShouldBe(expected.Length);
        foreach (var item in expected)
        {
            var endpoint = endpoints.Single(candidate =>
                NormalizeRoute(candidate.RoutePattern.RawText) == NormalizeRoute(item.Route) &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(item.HttpMethod) == true);
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName.ShouldBe(item.Name);
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(metadata => metadata.Policy)
                .ShouldContain(AuthorizationPolicies.RequireAdmin);
            endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Select(metadata => metadata.StatusCode)
                .Order()
                .ShouldBe(item.StatusCodes.Order());
            endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Single(metadata => metadata.StatusCode is >= 200 and < 300)
                .Type.ShouldBe(item.SuccessType);
        }

        var notFoundEndpoints = endpoints
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Any(metadata => metadata.StatusCode == StatusCodes.Status404NotFound))
            .ToList();
        foreach (var endpoint in notFoundEndpoints)
        {
            var metadata = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Single(item => item.StatusCode == StatusCodes.Status404NotFound);
            metadata.Type.ShouldBe(typeof(ProblemDetails));
            metadata.ContentTypes.ShouldBe(["application/problem+json"]);
        }

        var forceMaterialize = endpoints.Single(endpoint =>
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ==
            "MaterializeLibrary");
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
                IReadOnlyList<LibraryFacetDto>>(methodName, [new Sqid(7)], [1, CancellationToken.None]),
            "GetGenreFacets" => await InvokeMissingQuery<
                GetLibraryGenreFacetsQuery,
                Page<LibraryFacetDto>>(methodName, [new Sqid(7)], [null, CancellationToken.None, 1, 50]),
            "GetCollections" => await InvokeMissingQuery<
                GetLibraryCollectionsQuery,
                Page<LibraryCollectionDto>>(methodName, [new Sqid(7)], [null, CancellationToken.None, 1, 50]),
            "GetTitleReleaseDiagnostics" => await InvokeMissingQuery<
                GetLibraryTitleReleaseDiagnosticsQuery,
                Page<LibraryTitleReleaseDiagnosticsDto>>(
                    methodName,
                    [new Sqid(7), new Sqid(9)],
                    [null, CancellationToken.None, 50]),
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
        => await InvokeMissingQuery<TQuery, TResult>(methodName, prefix, [CancellationToken.None]);

    private static async Task<IResult> InvokeMissingQuery<TQuery, TResult>(
        string methodName,
        object[] prefix,
        object?[] suffix)
        where TQuery : IQuery<TResult>
    {
        var handler = Substitute.For<IQueryHandler<TQuery, TResult>>();
        handler.HandleAsync(Arg.Any<TQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<TResult>>(LibraryErrors.NotFound()));
        return await InvokeAsync(methodName, [.. prefix, handler, .. suffix]);
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
        services.AddSingleton(Substitute.For<IQueryHandler<GetLibraryPreviewQuery, Page<LibraryPreviewTitleDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<GetCollectionPlacementsQuery, Page<CollectionLibraryPlacementDto>>>());
        services.AddSingleton(Substitute.For<ICommandHandler<SetLibraryAttachmentsCommand, Success>>());
        services.AddSingleton(Substitute.For<IQueryHandler<ListLibrariesQuery, Page<LibraryDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<GetLibraryByIdQuery, LibraryDto>>());
        services.AddSingleton(Substitute.For<IQueryHandler<GetLibraryDiscSetOptionsQuery, LibraryDiscSetOptionsDto>>());
        services.AddSingleton(Substitute.For<ICommandHandler<CreateLibraryCommand, LibraryDto>>());
        services.AddSingleton(Substitute.For<ICommandHandler<UpdateLibraryCommand, LibraryDto>>());
        services.AddSingleton(Substitute.For<ICommandHandler<DeleteLibraryCommand, Deleted>>());
        services.AddSingleton(Substitute.For<ICommandHandler<ForceMaterializeLibraryCommand, Guid>>());
        services.AddSingleton(Substitute.For<IQueryHandler<
            GetLibraryPlatformFacetsQuery,
            IReadOnlyList<LibraryFacetDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<
            GetLibraryGenreFacetsQuery, Page<LibraryFacetDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<
            GetLibraryCollectionsQuery, Page<LibraryCollectionDto>>>());
        services.AddSingleton(Substitute.For<IQueryHandler<
            GetLibraryTitleReleaseDiagnosticsQuery, Page<LibraryTitleReleaseDiagnosticsDto>>>());
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

    private static void AssertFieldProblem(IResult result, string errorCode, params string[] fields)
    {
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>()!.Value.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe(errorCode);
        problem.Extensions[ProblemResults.ErrorsExtensionName]
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>()!.Keys.ShouldBe(fields, ignoreOrder: true);
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
            ExportFormats = [],
            CreatedAt = DateTimeOffset.UtcNow
        };

    private sealed record EndpointExpectation(
        string Route,
        string HttpMethod,
        string Name,
        int[] StatusCodes,
        Type SuccessType);
}
