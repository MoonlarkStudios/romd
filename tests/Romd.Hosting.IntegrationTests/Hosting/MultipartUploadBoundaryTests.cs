using System.Net;
using ErrorOr;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Commands.AcceptUpload;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Host.Endpoints;
using Romd.Host.Authorization;
using Romd.Host.Configuration;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>Exercises real endpoint binding on Kestrel with small file limits and no database.</summary>
public sealed class MultipartUploadBoundaryTests
{
    private const int FileLimit = 1024 * 1024;
    private const int MultipartAllowance = 2 * 1024 * 1024;

    [Theory]
    [InlineData("/api/upload")]
    [InlineData("/api/upload/rom")]
    public async Task Upload_FileExactlyAtLimit_IsAccepted(string route)
    {
        await using var app = await StartAppAsync();
        using var client = CreateClient(app);
        using var form = Multipart(FileLimit);

        using var response = await client.PostAsync(route, form);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["jobId"]);
        response.Headers.Location?.OriginalString.ShouldBe($"/api/jobs/{body.RootElement.GetProperty("jobId").GetGuid()}");
        response.Headers.ETag.ShouldBeNull();
        await app.Services.GetRequiredService<IUploadWorkspacePreparation>().Received(1).PrepareAsync(
            Arg.Is<JobWorkspaceInput>(input => input.FileName == "game.rom"), Arg.Any<Stream>(),
            Arg.Any<IJobWorkspaceLease>(), Arg.Any<CancellationToken>());
        await app.Services.GetRequiredService<IUploadJobAcceptance>().Received(1).AddStagedAsync(
            Arg.Any<Romd.Domain.Jobs.UploadJob>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/api/upload")]
    [InlineData("/api/upload/rom")]
    public async Task Upload_FileAboveLimitWithinBodyAllowance_IsRejectedBeforeProcessing(string route)
    {
        await using var app = await StartAppAsync();
        using var client = CreateClient(app);
        using var form = Multipart(FileLimit + 1);

        using var response = await client.PostAsync(route, form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Upload.FileTooLarge");
        await app.Services.GetRequiredService<IUploadWorkspacePreparation>().DidNotReceiveWithAnyArgs()
            .PrepareAsync(default!, default!, default!, default);
        await app.Services.GetRequiredService<IUnitOfWork>().DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        await app.Services.GetRequiredService<IFileClassifier>().DidNotReceiveWithAnyArgs()
            .ClassifyAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("/api/upload?maxParallelRoms=0&trackedOnly=true", false, "Upload.FileRequired", "file")]
    [InlineData("/api/upload?maxParallelRoms=0&trackedOnly=true", true, "Upload.InvalidConcurrency", "maxParallelRoms")]
    [InlineData("/api/upload?requestId=00000000-0000-0000-0000-000000000000&trackedOnly=true", true, "Upload.InvalidIdentity", "requestId")]
    [InlineData("/api/upload?trackedOnly=true", true, "Upload.NoTrackedTitles", "trackedOnly")]
    [InlineData("/api/upload/rom?trackedOnly=true", true, "Upload.NoTrackedTitles", "trackedOnly")]
    public async Task Upload_ValidationPrecedencePreservesExactProblemFields(string route, bool file, string code, string field)
    {
        await using var app = await StartAppAsync(classification: FileType.Dat);
        using var client = CreateClient(app);
        using var form = file ? Multipart(FileLimit) : new MultipartFormDataContent();
        if (!file)
            form.Add(new StringContent("present"), "ordinaryField");

        using var response = await client.PostAsync(route, form);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, code);
        body.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe([field]);
        response.Headers.Location.ShouldBeNull();
        response.Headers.ETag.ShouldBeNull();
        await app.Services.GetRequiredService<IFileClassifier>().DidNotReceiveWithAnyArgs().ClassifyAsync(default!, default!, default);
        await app.Services.GetRequiredService<IUploadWorkspacePreparation>().DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("/api/upload/dat", FileType.Rom, false, "Upload.InvalidDatFile")]
    [InlineData("/api/upload/dat", FileType.Archive, false, "Upload.InvalidDatFile")]
    [InlineData("/api/upload/dat", FileType.Unknown, false, "Upload.InvalidDatFile")]
    [InlineData("/api/upload/dat", FileType.Dat, true, null)]
    [InlineData("/api/upload/rom", FileType.Dat, false, "Upload.InvalidRomFile")]
    [InlineData("/api/upload/rom", FileType.Unknown, true, null)]
    [InlineData("/api/upload/rom", FileType.Archive, true, null)]
    public async Task Upload_ClassificationKeepsDatOnlyAndRomNonDatRules(string route, FileType classification, bool accepted, string? code)
    {
        await using var app = await StartAppAsync(classification: classification);
        using var client = CreateClient(app);
        using var form = Multipart(FileLimit);

        using var response = await client.PostAsync(route, form);

        response.StatusCode.ShouldBe(accepted ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest);
        await app.Services.GetRequiredService<IFileClassifier>().Received(1).ClassifyAsync(Arg.Any<Stream>(), "game.rom", Arg.Any<CancellationToken>());
        if (!accepted)
        {
            using var body = await ProblemEnvelope.ReadAsync(response, 400, code!);
            body.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe(["file"]);
            await app.Services.GetRequiredService<IUploadWorkspacePreparation>().DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default!, default);
        }
    }

    [Theory]
    [InlineData("/api/upload")]
    [InlineData("/api/upload/rom")]
    public async Task Upload_RequestAboveBodyAllowance_IsRejectedByKestrel(string route)
    {
        await using var app = await StartAppAsync();
        using var client = CreateClient(app);
        client.DefaultRequestHeaders.ExpectContinue = true;
        using var form = Multipart(FileLimit + MultipartAllowance);

        using var response = await client.PostAsync(route, form);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Upload_MaximumLongFileLimit_RequestAllowanceDoesNotOverflow()
    {
        await using var app = await StartAppAsync(long.MaxValue);
        var limits = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is "Upload" or "UploadRom")
            .Select(endpoint => endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize)
            .ToArray();

        limits.ShouldBe([long.MaxValue, long.MaxValue]);
    }

    private static MultipartFormDataContent Multipart(int size) =>
        new() { { new ByteArrayContent(new byte[size]), "file", "game.rom" } };

    private static HttpClient CreateClient(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    private static async Task<WebApplication> StartAppAsync(long maxFileBytes = FileLimit, FileType classification = FileType.Rom)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.RequireContributor, policy => policy.RequireAssertion(_ => true));
            options.AddPolicy(AuthorizationPolicies.RequireManager, policy => policy.RequireAssertion(_ => true));
        });
        builder.Services.AddSingleton<IRomdOptions>(new RomdOptions { MaxUploadBytes = maxFileBytes });
        // Match production's global section limit; route metadata must add the allowance.
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxFileBytes);
        builder.Services.AddSingleton(Substitute.For<IReferenceCatalogService>());
        builder.Services.AddSingleton(Substitute.For<ITrackedTitleReader>());
        builder.Services.AddSingleton(Substitute.For<ICurrentUser>());
        builder.Services.AddSingleton(Substitute.For<IUploadJobRepository>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<ImportFromPathCommand, Guid>>());
        var classifier = Substitute.For<IFileClassifier>();
        classifier.ClassifyAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FileClassification(classification, null, 1));
        builder.Services.AddSingleton(classifier);
        builder.Services.AddSingleton(Substitute.For<IJobRepository>());
        builder.Services.AddSingleton(Substitute.For<IUploadJobAcceptance>());
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        var exclusion = Substitute.For<IJobWorkspaceExclusion>();
        exclusion.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IJobWorkspaceLease>()));
        builder.Services.AddSingleton(exclusion);
        var preparation = Substitute.For<IUploadWorkspacePreparation>();
        preparation.PrepareAsync(Arg.Any<JobWorkspaceInput>(), Arg.Any<Stream>(),
            Arg.Any<IJobWorkspaceLease>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                // The real owner must pass the exact bound input length to byte preparation.
                call.Arg<Stream>().Length.ShouldBe(FileLimit);
                return Task.FromResult(ErrorOrFactory.From(Substitute.For<IPreparedJobWorkspace>()));
            });
        builder.Services.AddSingleton(preparation);
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));
        builder.Services.AddSingleton(work);
        builder.Services.AddSingleton<ICommandHandler<AcceptUploadCommand, Guid>, AcceptUploadCommandHandler>();
        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseAuthorization();
        app.MapGroup("/api").AllowAnonymous().MapUploadEndpoints();
        await app.StartAsync();
        return app;
    }
}
