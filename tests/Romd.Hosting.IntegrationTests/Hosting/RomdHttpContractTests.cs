using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     The shared contract seams in isolation: the ProblemDetails customization's errorCode
///     fallbacks and the error-metadata extension (docs/decisions/api-contract-policy.md,
///     "One Error Envelope").
/// </summary>
public sealed class RomdHttpContractTests
{
    public static TheoryData<Exception?, string> ProblemsWithoutErrorCode => new()
    {
        { null, "General.Unexpected" },
        { new BadHttpRequestException("Malformed body."), "Request.InvalidBody" },
        { new BadHttpRequestException("Request body too large.", 413), "Request.PayloadTooLarge" },
        { new BadHttpRequestException("Unsupported media type.", 415), "Request.UnsupportedMediaType" },
        { new InvalidOperationException("Unhandled."), "General.Unhandled" }
    };

    [Theory]
    [MemberData(nameof(ProblemsWithoutErrorCode))]
    public async Task ProblemDetailsCustomization_ProblemWithoutErrorCode_StampsCodeAndTraceId(
        Exception? exception,
        string expectedErrorCode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRomdHttpContract();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        if (exception is not null)
            context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = exception });

        await provider.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = StatusCodes.Status404NotFound }
        });

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        body.RootElement.GetProperty("errorCode").GetString().ShouldBe(expectedErrorCode);
        body.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ProducesProblems_DeclaresEachStatusWithProblemDetailsAndProblemJson()
    {
        using var app = WebApplication.CreateSlimBuilder().Build();

        app.MapGet("/probe", () => "ok").ProducesProblems(400, 404, 409);

        var responses = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .Single()
            .Metadata
            .GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Where(metadata => metadata.StatusCode >= 400)
            .ToArray();
        responses.Select(metadata => metadata.StatusCode).ShouldBe([400, 404, 409]);
        responses.ShouldAllBe(metadata => metadata.Type == typeof(ProblemDetails)
            && metadata.ContentTypes.SequenceEqual(new[] { "application/problem+json" }));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(304)]
    [InlineData(600)]
    public void ProducesProblems_NonErrorStatus_IsRejected(int statusCode)
    {
        using var app = WebApplication.CreateSlimBuilder().Build();

        Should.Throw<ArgumentOutOfRangeException>(() => app.MapGet("/probe", () => "ok").ProducesProblems(statusCode));
    }
}
