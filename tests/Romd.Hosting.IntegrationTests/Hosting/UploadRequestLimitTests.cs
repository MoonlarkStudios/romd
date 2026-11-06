using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Only ROM-capable upload routes accept request bodies beyond Kestrel's default, and a
///     rejected body gets the shared 413 envelope (docs/decisions/api-contract-policy.md,
///     "One Error Envelope"). These tests run on real Kestrel because the in-memory test server
///     does not enforce body limits.
/// </summary>
public sealed class UploadRequestLimitTests(UploadRequestLimitTests.KestrelFixture fixture)
    : IClassFixture<UploadRequestLimitTests.KestrelFixture>
{
    private const long KestrelDefaultLimit = 30_000_000;
    // DAT and artwork routes: 32 MiB files plus multipart overhead.
    private const long FileRouteLimit = 34 * 1024 * 1024;
    private const int AboveFileRouteLimit = 40 * 1024 * 1024;

    [Fact]
    public void AdminRoutes_OnlyRomUploadsRaiseTheBodyLimitBeyondFileRoutes()
    {
        fixture.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize
            .ShouldBe(KestrelDefaultLimit);
        var raised = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Name: endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName!,
                Limit: endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()))
            .Where(route => route.Limit is not null && route.Limit.MaxRequestBodySize is null or > KestrelDefaultLimit)
            .ToDictionary(route => route.Name, route => route.Limit!.MaxRequestBodySize);

        raised.Where(route => route.Value != FileRouteLimit).Select(route => route.Key)
            .ShouldBe(["Upload", "UploadRom"], ignoreOrder: true);
        raised["Upload"].ShouldBe(64L * 1024 * 1024 * 1024 + 2 * 1024 * 1024);
        raised["UploadRom"].ShouldBe(64L * 1024 * 1024 * 1024 + 2 * 1024 * 1024);
        raised.Keys.ShouldContain("UploadDat");
        raised["CreateDatReplacement"].ShouldBe(FileRouteLimit);
        raised["PreviewDatReplacement"].ShouldBe(FileRouteLimit);
        raised["ListDatReplacementChanges"].ShouldBe(FileRouteLimit);
        raised["GetDatReplacementChange"].ShouldBe(FileRouteLimit);
        fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .ShouldNotContain("ReplaceDat");
        raised.Keys.ShouldContain("UploadTitleMedia");
    }

    [Fact]
    public async Task RomUpload_BodyAboveServerDefault_IsReadInFull()
    {
        using var client = fixture.CreateAuthenticatedClient();

        // The invalid name fails validation before job creation, which runs only after the form
        // body has been read in full; a body-limit rejection would return 413 instead.
        using var response = await client.PostAsync("/api/upload/rom", Multipart(AboveFileRouteLimit, ".."));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Upload.FileRequired");
    }

    [Fact]
    public async Task DatUpload_BodyAboveFileRouteLimit_IsRejected()
    {
        using var client = CreateExpectContinueClient();

        using var response = await client.PostAsync("/api/upload/dat", Multipart(AboveFileRouteLimit, "large.dat"));

        using var envelope = await ProblemEnvelope.ReadAsync(response, 413, "Request.PayloadTooLarge");
    }

    [Fact]
    public async Task JsonEndpoint_BodyAboveServerDefault_IsRejected()
    {
        using var client = CreateExpectContinueClient();
        string body = "{\"path\":\"" + new string('a', (int)KestrelDefaultLimit) + "\"}";

        using var response = await client.PostAsync("/api/upload/from-path",
            new StringContent(body, Encoding.UTF8, "application/json"));

        using var envelope = await ProblemEnvelope.ReadAsync(response, 413, "Request.PayloadTooLarge");
    }

    // Kestrel rejects an oversized Content-Length before reading; waiting for 100-continue lets
    // the client receive that 413 instead of failing mid-send on the closed connection.
    private HttpClient CreateExpectContinueClient()
    {
        var client = fixture.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.ExpectContinue = true;
        return client;
    }

    private static MultipartFormDataContent Multipart(int size, string fileName) =>
        new() { { new ByteArrayContent(new byte[size]), "file", fileName } };

    public sealed class KestrelFixture : IntegrationTestFixture
    {
        public KestrelFixture() => UseKestrel(0);
    }
}
