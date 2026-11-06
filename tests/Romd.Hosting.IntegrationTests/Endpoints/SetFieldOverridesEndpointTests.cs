using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
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
using Romd.Admin.Application.Export.Queries.GetArchiveFormats;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetEnrichmentOmission;
using Romd.Admin.Application.Titles.Commands.SetFieldOverrides;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.Queries.GetTitleEnrichmentState;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Export;
using Romd.Contracts.Management.Enrichment;
using Romd.Contracts.Management.Titles;
using Romd.Host.Authorization;
using Romd.Host.Endpoints;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class SetFieldOverridesEndpointTests
{
    [Fact]
    public async Task SetFieldOverrides_HandlerSucceeds_DispatchesIfMatchAndReturnsDetailWithItsETag()
    {
        using var cancellation = new CancellationTokenSource();
        var request = new SetFieldOverridesRequest
        {
            Overrides = new Dictionary<string, string?> { ["Rating"] = "igdb" }
        };
        var detail = CreateDetail();
        var handler = Substitute.For<ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>>();
        handler.HandleAsync(Arg.Any<SetFieldOverridesCommand>(), cancellation.Token).Returns(detail);
        var offered = new ArchiveFormats([
            new ArchiveFormatDto { Format = "original", Label = "Original" }, new ArchiveFormatDto { Format = "rvz", Label = "RVZ" }
        ], new Dictionary<int, IReadOnlyList<ArchiveFormatDto>>());
        var archiveFormats = Substitute.For<IQueryHandler<GetArchiveFormatsQuery, ArchiveFormats>>();
        archiveFormats.HandleAsync(Arg.Any<GetArchiveFormatsQuery>(), Arg.Any<CancellationToken>()).Returns(offered);
        var context = Context("\"read-revision\"", archiveFormats);

        var result = await InvokeAsync(new Sqid(47), request, context, handler, cancellation.Token);

        // The written title answers with the archive formats of the title the route addresses.
        var written = result.ShouldBeAssignableTo<Ok<Models.TitleDetail>>().Value.ShouldNotBeNull();
        written.ShouldBe(detail with { ArchiveFormats = offered.Title!, Releases = written.Releases });
        written.Releases.Items.ShouldBeEmpty();
        await archiveFormats.Received(1).HandleAsync(
            Arg.Is<GetArchiveFormatsQuery>(query => query.TitleId == 47 && query.ReleaseIds.Count == 0),
            Arg.Any<CancellationToken>());
        context.Response.Headers.ETag.ToString().ShouldBe("\"title-revision\"");
        await handler.Received(1).HandleAsync(
            Arg.Is<SetFieldOverridesCommand>(command =>
                command.TitleId == 47 &&
                ReferenceEquals(command.Overrides, request.Overrides) &&
                command.IfMatch.IsSatisfiedBy("read-revision") &&
                !command.IfMatch.IsSatisfiedBy("title-revision")),
            cancellation.Token);
    }

    [Fact]
    public async Task SetFieldOverrides_MissingIfMatch_Is428WithoutDispatch()
    {
        var handler = Substitute.For<ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>>();

        var result = await InvokeAsync(
            new Sqid(47),
            new SetFieldOverridesRequest { Overrides = new Dictionary<string, string?>() },
            Context(null),
            handler,
            CancellationToken.None);

        AssertProblem(result, StatusCodes.Status428PreconditionRequired, "Titles.PreconditionRequired");
        await handler.DidNotReceive().HandleAsync(Arg.Any<SetFieldOverridesCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound, "Catalog.TitleNotFound")]
    [InlineData(StatusCodes.Status412PreconditionFailed, "Titles.PreconditionFailed")]
    [InlineData(StatusCodes.Status500InternalServerError, "Catalog.TitleUpdateFailed")]
    public async Task SetFieldOverrides_HandlerError_UsesTheSharedEnvelope(int status, string errorCode)
    {
        var handler = Substitute.For<ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>>();
        handler.HandleAsync(Arg.Any<SetFieldOverridesCommand>(), Arg.Any<CancellationToken>())
            .Returns(status switch
            {
                StatusCodes.Status404NotFound => CatalogErrors.TitleNotFound(),
                StatusCodes.Status412PreconditionFailed => CatalogErrors.TitlePreconditionFailed(),
                _ => CatalogErrors.TitleUpdateFailed()
            });
        var context = Context("\"read-revision\"");

        var result = await InvokeAsync(
            new Sqid(47),
            new SetFieldOverridesRequest { Overrides = new Dictionary<string, string?>() },
            context,
            handler,
            CancellationToken.None);

        AssertProblem(result, status, errorCode);
        context.Response.Headers.ETag.ShouldBeEmpty();
    }

    [Fact]
    public void SetFieldOverrides_UseCaseBoundary_InjectsOnlyHandlerBesidesBindingContextAndCancellation()
    {
        var method = EndpointMethod();

        method.ReturnType.ShouldBe(typeof(Task<IResult>));
        method.GetParameters().Select(parameter => parameter.ParameterType).ShouldBe(
        [
            typeof(Sqid),
            typeof(SetFieldOverridesRequest),
            typeof(HttpContext),
            typeof(ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>),
            typeof(CancellationToken)
        ]);
    }

    [Fact]
    public async Task SetFieldOverrides_RouteMetadata_PreservesNameAuthorizationAndResponses()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Substitute.For<Romd.Admin.Application.Artwork.IArtworkAcquisitionStore>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<
            Romd.Admin.Application.Artwork.Commands.ReplaceArtworkEnrichmentSettings.ReplaceArtworkEnrichmentSettingsCommand,
            Romd.Contracts.Management.Artwork.ArtworkEnrichmentSettingsDto>>());
        builder.Services.AddSingleton(Substitute.For<Romd.Admin.Application.MetadataProviders.IIgdbProviderSettingsService>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment.TriggerTitleEnrichmentCommand, Guid>>());
        builder.Services.AddSingleton(Substitute.For<ITitleAggregateReader>());
        builder.Services.AddSingleton(Substitute.For<IQueryHandler<GetTitleEnrichmentStateQuery, TitleEnrichmentStateResponse>>());
        builder.Services.AddSingleton(Substitute.For<ITitleReader>());
        builder.Services.AddSingleton(Substitute.For<ITitleEnrichmentStatusReader>());
        builder.Services.AddSingleton(Substitute.For<IPlatformRepository>());
        builder.Services.AddSingleton(Substitute.For<IPlatformFieldDefaultRepository>());
        builder.Services.AddSingleton(Substitute.For<IQueryHandler<Romd.Admin.Application.Source.Platform.Queries.GetMetadataPolicy.GetMetadataPolicyQuery, Romd.Contracts.Management.Enrichment.PlatformMetadataPolicyDto>>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy.SetMetadataPolicyCommand, Romd.Contracts.Management.Enrichment.PlatformMetadataPolicyDto>>());
        builder.Services.AddSingleton(Substitute.For<IRematerializationScheduler>());
        builder.Services.AddSingleton(Substitute.For<Romd.Admin.Application.Common.Persistence.IUnitOfWork>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>>());
        builder.Services.AddSingleton(
            Substitute.For<ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail>>());
        builder.Services.AddSingleton(
            Substitute.For<ICommandHandler<SetEnrichmentOmissionCommand, Models.TitleDetail>>());
        await using var app = builder.Build();
        app.MapEnrichmentEndpoints();

        var policyEndpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate =>
                candidate.RoutePattern.RawText == "/systems/{systemKey}/enrichment/defaults" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("PATCH") == true);
        policyEndpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(x => x.StatusCode)
            .ShouldContain(StatusCodes.Status200OK);

        var readEndpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate =>
                candidate.RoutePattern.RawText == "/titles/{titleId}/enrichment" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        readEndpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName.ShouldBe("GetTitleEnrichmentState");
        readEndpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(metadata => metadata.StatusCode)
            .ShouldContain(StatusCodes.Status409Conflict);

        var bulkEndpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate =>
                candidate.RoutePattern.RawText == "/systems/{systemKey}/enrichment/bulk");
        bulkEndpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName.ShouldBe("TriggerSystemBulkEnrichment");
        bulkEndpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.ShouldBe(["POST"]);
        bulkEndpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(metadata => metadata.Policy)
            .ShouldContain(AuthorizationPolicies.RequireManager);
        var bulkResponses = bulkEndpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        // The route key and explicit problem declaration each retain their existing 404 metadata.
        bulkResponses.Select(metadata => metadata.StatusCode).Order().ShouldBe([202, 400, 404, 404]);
        bulkResponses.Single(metadata => metadata.StatusCode == 202).Type.ShouldBe(typeof(Models.JobReference));

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/titles/{titleId}/enrichment" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("PATCH") == true);

        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName.ShouldBe("UpdateTitleEnrichment");
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .ShouldContain(AuthorizationPolicies.RequireManager);
        var responseMetadata = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        responseMetadata.Select(metadata => metadata.StatusCode).Order().ShouldBe(
        [
            StatusCodes.Status200OK,
            StatusCodes.Status400BadRequest,
            StatusCodes.Status404NotFound,
            StatusCodes.Status412PreconditionFailed,
            StatusCodes.Status428PreconditionRequired
        ]);
        responseMetadata.Single(metadata => metadata.StatusCode == StatusCodes.Status200OK)
            .Type.ShouldBe(typeof(Models.TitleDetail));
        responseMetadata.Where(metadata => metadata.StatusCode >= 400)
            .ShouldAllBe(metadata => metadata.Type == typeof(ProblemDetails)
                && metadata.ContentTypes.SequenceEqual(new[] { "application/problem+json" }));
    }

    /// <summary>A request context; a successful write also resolves the title's archive formats from its services.</summary>
    private static DefaultHttpContext Context(string? ifMatch,
        IQueryHandler<GetArchiveFormatsQuery, ArchiveFormats>? archiveFormats = null)
    {
        var context = new DefaultHttpContext();
        if (ifMatch is not null)
            context.Request.Headers.IfMatch = ifMatch;
        if (archiveFormats is not null)
            context.RequestServices = new ServiceCollection().AddSingleton(archiveFormats)
                .AddSingleton(RomMediaPolicyComposition.CreateDeliveryPolicy()).BuildServiceProvider();
        return context;
    }

    private static void AssertProblem(IResult result, int status, string errorCode)
    {
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(status);
        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe(errorCode);
    }

    private static async Task<IResult> InvokeAsync(
        Sqid titleId,
        SetFieldOverridesRequest request,
        HttpContext context,
        ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        var task = EndpointMethod().Invoke(null, [titleId, request, context, handler, cancellationToken]);
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
            Revision = "title-revision",
            SystemKey = "snes",
            Name = "Super Mario World",
            EnrichmentStatus = "Completed",
            Rating = 94
        };
}
