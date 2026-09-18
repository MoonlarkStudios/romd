using System.Reflection;
using ErrorOr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Dashboard.Queries.GetLibrarySummary;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Source.Rom.Commands.PurgeUnidentified;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Host.Authorization;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class RomEndpointsTests
{
    [Fact]
    public async Task List_OversizedSearch_ReturnsBadRequestWithoutQueryingFiles()
    {
        var repository = Substitute.For<IRomRepository>();
        var method = typeof(RomEndpoints).GetMethod("List", BindingFlags.NonPublic | BindingFlags.Static).ShouldNotBeNull();
        var task = method.Invoke(null, [repository, null, null, 50, new string('x', 201), CancellationToken.None]);
        var result = await task.ShouldBeOfType<Task<IResult>>();
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_HandlerSucceeds_DispatchesDecodedIdAndReturnsNoContent()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = Substitute.For<ICommandHandler<DeleteRomCommand, Deleted>>();
        handler.HandleAsync(Arg.Any<DeleteRomCommand>(), cancellation.Token)
            .Returns(Result.Deleted);

        var result = await InvokeDeleteAsync(new Sqid(47), handler, cancellation.Token);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        await handler.Received(1).HandleAsync(
            Arg.Is<DeleteRomCommand>(command => command.RomId == 47),
            cancellation.Token);
    }

    [Fact]
    public async Task Delete_HandlerReturnsMissingRom_PreservesBodylessNotFound()
    {
        var handler = Substitute.For<ICommandHandler<DeleteRomCommand, Deleted>>();
        handler.HandleAsync(Arg.Any<DeleteRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(CatalogErrors.RomNotFound());

        var result = await InvokeDeleteAsync(new Sqid(47), handler, CancellationToken.None);

        result.ShouldBeOfType<NotFound>();
    }

    [Fact]
    public async Task Delete_HandlerReturnsNonNotFoundError_UsesSharedProblemDetails()
    {
        var handler = Substitute.For<ICommandHandler<DeleteRomCommand, Deleted>>();
        handler.HandleAsync(Arg.Any<DeleteRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(Error.Failure("Library.DatabaseFailed", "Database operation failed"));

        var result = await InvokeDeleteAsync(new Sqid(47), handler, CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Detail.ShouldBe("Database operation failed");
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Library.DatabaseFailed");
    }

    [Fact]
    public void Delete_UseCaseBoundary_InjectsOnlyHandlerBesidesBindingAndCancellation()
    {
        var method = DeleteMethod();

        method.ReturnType.ShouldBe(typeof(Task<IResult>));
        method.GetParameters().Select(parameter => parameter.ParameterType).ShouldBe(
        [
            typeof(Sqid),
            typeof(ICommandHandler<DeleteRomCommand, Deleted>),
            typeof(CancellationToken)
        ]);
    }

    [Fact]
    public async Task Delete_RouteMetadata_PreservesNameAuthorizationAndResponses()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Substitute.For<IRomRepository>());
        builder.Services.AddSingleton(Substitute.For<IFileStorageService>());
        builder.Services.AddSingleton(Substitute.For<ICurrentUser>());
        builder.Services.AddSingleton(
            Substitute.For<IQueryHandler<GetLibrarySummaryQuery, Models.LibrarySummary>>());
        builder.Services.AddSingleton(
            Substitute.For<ICommandHandler<DeleteRomCommand, Deleted>>());
        builder.Services.AddSingleton(
            Substitute.For<ICommandHandler<BatchDeleteRomsCommand, BatchDeleteResult>>());
        builder.Services.AddSingleton(
            Substitute.For<ICommandHandler<PurgeUnidentifiedRomsCommand, PurgeUnidentifiedRomsResult>>());
        await using var app = builder.Build();
        app.MapRomEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/roms/{romId}" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);

        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName.ShouldBe("DeleteRom");
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .ShouldContain(AuthorizationPolicies.RequireManager);
        var responseMetadata = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        responseMetadata
            .Select(metadata => metadata.StatusCode)
            .Order()
            .ShouldBe([StatusCodes.Status204NoContent, StatusCodes.Status404NotFound]);
        var notFoundMetadata = responseMetadata
            .Where(metadata => metadata.StatusCode == StatusCodes.Status404NotFound)
            .ShouldHaveSingleItem();
        notFoundMetadata.Type.ShouldBe(typeof(void));
        notFoundMetadata.ContentTypes.ShouldBeEmpty();
    }

    private static async Task<IResult> InvokeDeleteAsync(
        Sqid romId,
        ICommandHandler<DeleteRomCommand, Deleted> handler,
        CancellationToken cancellationToken)
    {
        var task = DeleteMethod().Invoke(null, [romId, handler, cancellationToken]);
        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static MethodInfo DeleteMethod()
    {
        var method = typeof(RomEndpoints).GetMethod(
            "Delete",
            BindingFlags.NonPublic | BindingFlags.Static);
        return method.ShouldNotBeNull();
    }
}
