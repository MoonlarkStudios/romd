using System.Reflection;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Romd.Hosting.Dashboard;
using Romd.Admin.Application.Catalog.Queries.GetCatalogFilters;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Dashboard.Queries.GetLibrarySummary;
using Romd.Admin.Application.Dashboard.Queries.GetStorageStats;
using Romd.Admin.Application.Dashboard.Queries.GetSystemStats;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Contracts.Management.Titles;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class EndpointErrorMappingTests
{
    [Fact]
    public async Task TitleCommand_Error_UsesSharedProblemMapping()
    {
        var handler = Substitute.For<ICommandHandler<UpdateUserMetadataCommand, Models.TitleDetail>>();
        handler.HandleAsync(Arg.Any<UpdateUserMetadataCommand>(), Arg.Any<CancellationToken>())
            .Returns(ToError<Models.TitleDetail>(Error.Validation("Title.Invalid", "Invalid metadata.")));

        var result = await InvokeAsync(
            typeof(TitleEndpoints),
            "UpdateMetadata",
            new Sqid(1),
            new UpdateTitleMetadataRequest(),
            handler,
            CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest, "Bad Request", "Invalid metadata.", "Title.Invalid");
    }

    [Fact]
    public async Task DashboardQuery_Error_UsesSharedProblemMapping()
    {
        var handler = Substitute.For<IQueryHandler<GetStorageStatsQuery, Models.StorageStatsDto>>();
        handler.HandleAsync(Arg.Any<GetStorageStatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(ToError<Models.StorageStatsDto>(Error.Conflict("Dashboard.Busy", "Dashboard is busy.")));

        var services = new ServiceCollection();
        services.AddScoped<IQueryHandler<GetStorageStatsQuery, Models.StorageStatsDto>>(_ => handler);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var cache = new DashboardStatsCache(provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, Substitute.For<IHostApplicationLifetime>());
        var result = await InvokeAsync(
            typeof(DashboardEndpoints),
            "GetStorage",
            cache,
            CancellationToken.None);

        AssertProblem(result, StatusCodes.Status409Conflict, "Conflict", "Dashboard is busy.", "Dashboard.Busy");
    }

    [Fact]
    public async Task SystemQuery_Error_UsesSharedProblemMapping()
    {
        var handler = Substitute.For<IQueryHandler<GetSystemStatsQuery, Models.SystemStats>>();
        handler.HandleAsync(Arg.Any<GetSystemStatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(ToError<Models.SystemStats>(Error.NotFound("System.NotFound", "System stats not found.")));

        var result = await InvokeAsync(
            typeof(SystemEndpoints),
            "GetStats",
            handler,
            CancellationToken.None);

        AssertProblem(result, StatusCodes.Status404NotFound, "Not Found", "System stats not found.", "System.NotFound");
    }

    [Fact]
    public async Task CatalogQuery_Error_UsesSharedProblemMapping()
    {
        var handler = Substitute.For<IQueryHandler<GetCatalogFiltersQuery, Models.CatalogFilters>>();
        handler.HandleAsync(Arg.Any<GetCatalogFiltersQuery>(), Arg.Any<CancellationToken>())
            .Returns(ToError<Models.CatalogFilters>(Error.Validation("Catalog.Invalid", "Invalid filters.")));

        var result = await InvokeAsync(
            typeof(CatalogEndpoints),
            "GetFilters",
            handler,
            CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest, "Bad Request", "Invalid filters.", "Catalog.Invalid");
    }

    [Fact]
    public async Task RomCommand_Error_UsesSharedProblemMapping()
    {
        var handler = Substitute.For<ICommandHandler<BatchDeleteRomsCommand, BatchDeleteResult>>();
        handler.HandleAsync(Arg.Any<BatchDeleteRomsCommand>(), Arg.Any<CancellationToken>())
            .Returns(ToError<BatchDeleteResult>(Error.NotFound("Rom.NotFound", "ROM not found.")));

        var result = await InvokeAsync(
            typeof(RomEndpoints),
            "BatchDelete",
            new Romd.Contracts.Management.Commands.BatchDeleteRomsRequest
            {
                RomIds = [Romd.Application.Common.Ids.IdCoder.Encode(1)]
            },
            handler,
            CancellationToken.None);

        AssertProblem(result, StatusCodes.Status404NotFound, "Not Found", "ROM not found.", "Rom.NotFound");
    }

    private static Task<ErrorOr<T>> ToError<T>(Error error) => Task.FromResult<ErrorOr<T>>(error);

    private static async Task<IResult> InvokeAsync(Type endpointType, string methodName, params object?[] args)
    {
        var method = endpointType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        if (method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Romd.Application.Common.ReferenceCatalog.IReferenceCatalogService))
            args = [Romd.Hosting.IntegrationTests.Infrastructure.TestReferenceCatalog.Create(), .. args];
        var task = method.Invoke(null, args);
        // Asynchronous completion may return a runtime-generated Task subclass.
        return await Assert.IsAssignableFrom<Task<IResult>>(task);
    }

    private static void AssertProblem(
        IResult result,
        int expectedStatus,
        string expectedTitle,
        string expectedDetail,
        string expectedErrorCode)
    {
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(expectedStatus);

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>()
            .Value;

        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(expectedStatus);
        problem.Title.ShouldBe(expectedTitle);
        problem.Detail.ShouldBe(expectedDetail);
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe(expectedErrorCode);
        problem.Type.ShouldNotBe(expectedErrorCode);
    }
}
