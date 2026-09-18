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
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetFieldOverrides;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Host.Authorization;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class SetFieldOverridesEndpointTests
{
    [Fact]
    public async Task SetFieldOverrides_HandlerSucceeds_DispatchesDecodedIdAndReturnsDetail()
    {
        using var cancellation = new CancellationTokenSource();
        var request = new SetFieldOverridesRequest
        {
            Overrides = new Dictionary<string, string?> { ["Rating"] = "igdb" }
        };
        var detail = CreateDetail();
        var handler = Substitute.For<ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>>();
        handler.HandleAsync(Arg.Any<SetFieldOverridesCommand>(), cancellation.Token).Returns(detail);

        var result = await InvokeAsync(new Sqid(47), request, handler, cancellation.Token);

        result.ShouldBeAssignableTo<Ok<Models.TitleDetail>>().Value.ShouldBeSameAs(detail);
        await handler.Received(1).HandleAsync(
            Arg.Is<SetFieldOverridesCommand>(command =>
                command.TitleId == 47 &&
                ReferenceEquals(command.Overrides, request.Overrides)),
            cancellation.Token);
    }

    [Fact]
    public async Task SetFieldOverrides_HandlerReturnsMissingTitle_PreservesBodylessNotFound()
    {
        var handler = Substitute.For<ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>>();
        handler.HandleAsync(Arg.Any<SetFieldOverridesCommand>(), Arg.Any<CancellationToken>())
            .Returns(Error.NotFound("Catalog.TitleNotFound", "Title not found"));

        var result = await InvokeAsync(
            new Sqid(47),
            new SetFieldOverridesRequest { Overrides = new Dictionary<string, string?>() },
            handler,
            CancellationToken.None);

        result.ShouldBeOfType<NotFound>();
    }

    [Fact]
    public async Task SetFieldOverrides_HandlerReturnsNonNotFoundError_UsesSharedProblemDetails()
    {
        var handler = Substitute.For<ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>>();
        handler.HandleAsync(Arg.Any<SetFieldOverridesCommand>(), Arg.Any<CancellationToken>())
            .Returns(Error.Failure("Catalog.TitleUpdateFailed", "Title update failed"));

        var result = await InvokeAsync(
            new Sqid(47),
            new SetFieldOverridesRequest { Overrides = new Dictionary<string, string?>() },
            handler,
            CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Detail.ShouldBe("Title update failed");
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Catalog.TitleUpdateFailed");
    }

    [Fact]
    public void SetFieldOverrides_UseCaseBoundary_InjectsOnlyHandlerBesidesBindingAndCancellation()
    {
        var method = EndpointMethod();

        method.ReturnType.ShouldBe(typeof(Task<IResult>));
        method.GetParameters().Select(parameter => parameter.ParameterType).ShouldBe(
        [
            typeof(Sqid),
            typeof(SetFieldOverridesRequest),
            typeof(ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>),
            typeof(CancellationToken)
        ]);
    }

    [Fact]
    public async Task SetFieldOverrides_RouteMetadata_PreservesNameAuthorizationAndResponses()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Substitute.For<Romd.Admin.Application.Artwork.IArtworkAcquisitionStore>());
        builder.Services.AddSingleton(Substitute.For<Romd.Admin.Application.MetadataProviders.IIgdbProviderSettingsService>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment.TriggerTitleEnrichmentCommand, Guid>>());
        builder.Services.AddSingleton(Substitute.For<ITitleRepository>());
        builder.Services.AddSingleton(Substitute.For<IPlatformRepository>());
        builder.Services.AddSingleton(Substitute.For<IPlatformFieldDefaultRepository>());
        builder.Services.AddSingleton(Substitute.For<IQueryHandler<Romd.Admin.Application.Source.Platform.Queries.GetMetadataPolicy.GetMetadataPolicyQuery, Romd.Contracts.Management.Enrichment.PlatformMetadataPolicyDto>>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy.SetMetadataPolicyCommand, Success>>());
        builder.Services.AddSingleton(Substitute.For<IRematerializationScheduler>());
        builder.Services.AddSingleton(Substitute.For<Romd.Admin.Application.Common.Persistence.IUnitOfWork>());
        builder.Services.AddSingleton(Substitute.For<IEnrichmentScheduler>());
        builder.Services.AddSingleton(
            Substitute.For<ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>>());
        await using var app = builder.Build();
        app.MapEnrichmentEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/titles/{titleId}/enrichment/fields" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("PATCH") == true);

        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName.ShouldBe("SetFieldOverrides");
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .ShouldContain(AuthorizationPolicies.RequireManager);
        var responseMetadata = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        responseMetadata.Select(metadata => metadata.StatusCode).Order().ShouldBe(
        [
            StatusCodes.Status200OK,
            StatusCodes.Status404NotFound
        ]);
        responseMetadata.Single(metadata => metadata.StatusCode == StatusCodes.Status200OK)
            .Type.ShouldBe(typeof(Models.TitleDetail));
        var notFoundMetadata = responseMetadata
            .Where(metadata => metadata.StatusCode == StatusCodes.Status404NotFound)
            .ShouldHaveSingleItem();
        notFoundMetadata.Type.ShouldBe(typeof(void));
        notFoundMetadata.ContentTypes.ShouldBeEmpty();
    }

    private static async Task<IResult> InvokeAsync(
        Sqid titleId,
        SetFieldOverridesRequest request,
        ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        var task = EndpointMethod().Invoke(null, [titleId, request, handler, cancellationToken]);
        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static MethodInfo EndpointMethod()
    {
        var method = typeof(EnrichmentEndpoints).GetMethod(
            "SetFieldOverrides",
            BindingFlags.NonPublic | BindingFlags.Static);
        return method.ShouldNotBeNull();
    }

    private static Models.TitleDetail CreateDetail() =>
        new()
        {
            Id = "title-public-id",
            SystemKey = "snes",
            Name = "Super Mario World",
            EnrichmentStatus = "Completed",
            Rating = 94
        };
}
