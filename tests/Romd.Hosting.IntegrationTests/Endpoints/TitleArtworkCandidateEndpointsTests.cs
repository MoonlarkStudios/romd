using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Candidate writes under <c>/api/titles/{titleId}</c> over HTTP with the provider seams faked: the gallery
///     import's create and repeat under the title ETag, the apply's 202, and upstream failures
///     (docs/decisions/api-contract-policy.md, "Method And Status Semantics" and "Idempotency And Concurrency").
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TitleArtworkCandidateEndpointsTests(IntegrationTestFixture fixture)
{
    private const string Reference = "signed-candidate";
    private static readonly TrustedArtworkCandidate Candidate = new("steamgriddb", "game", "asset", ArtworkRole.Poster,
        $"https://cdn2.steamgriddb.com/grid/{Guid.NewGuid():N}.png", "https://cdn2.steamgriddb.com/thumb/asset.png",
        "Artist", "https://www.steamgriddb.com/grid/asset", MediaType.Cover);

    [Fact]
    public async Task GalleryImport_CreatesMediaAtItsUrl_ARepeatIs200WithTheSameETag_AndAStaleETagIs412()
    {
        await using var host = WithProvider();
        var titleId = await SeedTitleAsync();
        var shown = await TitleETagAsync(host.Client, titleId);
        var path = $"/api/titles/{titleId}/artwork-candidates/{ArtworkCandidateIds.Of(Candidate.TrustedAssetUrl)}/import";

        using var created = await host.Client.SendAsync(Request(HttpMethod.Post, path, shown, new { candidateReference = Reference }));

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var media = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        created.Headers.Location!.OriginalString.ShouldBe($"/api/titles/{titleId}/media/{media}");
        var etag = created.Headers.ETag!.Tag;
        etag.ShouldNotBe(shown);
        etag.ShouldBe(await TitleETagAsync(host.Client, titleId));

        using var repeat = await host.Client.SendAsync(Request(HttpMethod.Post, path, etag, new { candidateReference = Reference }));
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK, await repeat.Content.ReadAsStringAsync());
        repeat.Headers.ETag!.Tag.ShouldBe(etag);
        JsonDocument.Parse(await repeat.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString().ShouldBe(media);

        using var stale = await host.Client.SendAsync(Request(HttpMethod.Post, path, shown, new { candidateReference = Reference }));
        using (await ProblemEnvelope.ReadAsync(stale, 412, "Titles.PreconditionFailed"))
        { }
        (await TitleETagAsync(host.Client, titleId)).ShouldBe(etag);
        (await MediaCountAsync(titleId)).ShouldBe(1);
    }

    [Fact]
    public async Task Apply_Is202WithTheJobLocationAndNoETag_AndAReplayAfterTheRotationAnswersTheSameJob()
    {
        await using var host = WithProvider();
        var titleId = await SeedTitleAsync();
        var path = $"/api/titles/{titleId}/artwork/Poster/apply";
        var body = new { requestId = Guid.NewGuid(), candidateReference = Reference };

        using var accepted = await host.Client.SendAsync(Request(HttpMethod.Post, path, "\"0\"", body));

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        var jobId = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetGuid();
        accepted.Headers.Location!.OriginalString.ShouldBe($"/api/jobs/{jobId}");
        accepted.Headers.ETag.ShouldBeNull();
        // The request rotated the role, so "0" is stale; the same request is recognized before its precondition.
        using (var role = await host.Client.GetAsync($"/api/titles/{titleId}/artwork/Poster"))
            role.Headers.ETag!.Tag.ShouldNotBe("\"0\"");

        using var replay = await host.Client.SendAsync(Request(HttpMethod.Post, path, "\"0\"", body));

        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted, await replay.Content.ReadAsStringAsync());
        JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetGuid().ShouldBe(jobId);
        replay.Headers.ETag.ShouldBeNull();
        using var other = await host.Client.SendAsync(Request(HttpMethod.Post, path, "\"0\"",
            new { requestId = Guid.NewGuid(), candidateReference = Reference }));
        using (await ProblemEnvelope.ReadAsync(other, 412, "Artwork.PreconditionFailed"))
        { }
    }

    [Fact]
    public async Task Hide_AReferenceForAnotherCandidate_Is400AndHidesNothing()
    {
        await using var host = WithProvider();
        var titleId = await SeedTitleAsync();
        using var family = await host.Client.GetAsync($"/api/titles/{titleId}/artwork");
        var etag = family.Headers.ETag!.Tag;

        using var hide = await host.Client.SendAsync(Request(HttpMethod.Put,
            $"/api/titles/{titleId}/hidden-artwork-candidates/{ArtworkCandidateIds.Of("https://cdn2.steamgriddb.com/grid/other.png")}",
            etag, new { candidateReference = Reference }));

        using (await ProblemEnvelope.ReadAsync(hide, 400, "Artwork.CandidateMismatch"))
        { }
        using var after = await host.Client.GetAsync($"/api/titles/{titleId}/artwork");
        after.Headers.ETag!.Tag.ShouldBe(etag);
    }

    [Fact]
    public async Task UnavailableProvider_Is503_AndAnInvalidProviderResponseIs502()
    {
        await using var host = WithProvider(browse: ArtworkProviderErrors.Unavailable(), download: ArtworkProviderErrors.InvalidResponse());
        var titleId = await SeedTitleAsync();

        using var browse = await host.Client.GetAsync(
            $"/api/titles/{titleId}/artwork-candidates?providerId=steamgriddb&gameId=game&role=Poster");
        using (await ProblemEnvelope.ReadAsync(browse, 503, "Artwork.ProviderUnavailable"))
        { }

        using var import = await host.Client.SendAsync(Request(HttpMethod.Post,
            $"/api/titles/{titleId}/artwork-candidates/{ArtworkCandidateIds.Of(Candidate.TrustedAssetUrl)}/import",
            await TitleETagAsync(host.Client, titleId), new { candidateReference = Reference }));
        using (await ProblemEnvelope.ReadAsync(import, 502, "Artwork.ProviderInvalidResponse"))
        { }
        (await MediaCountAsync(titleId)).ShouldBe(0);
    }

    /// <summary>
    ///     A host whose browsing service trusts <see cref="Reference" /> as <see cref="Candidate" /> and whose asset
    ///     source downloads it, unless <paramref name="browse" /> or <paramref name="download" /> fail instead.
    /// </summary>
    private CandidateHost WithProvider(Error? browse = null, Error? download = null)
    {
        var browsing = Substitute.For<IArtworkBrowsingService>();
        browsing.ValidateCandidateAsync(Arg.Any<int>(), Arg.Any<Guid>(), Reference, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<TrustedArtworkCandidate>>(Candidate));
        browsing.BrowseAsync(Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<ProviderArtworkQuery>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<ArtworkCandidatePage>>(browse is { } failure
                ? failure
                : new ArtworkCandidatePage([], null)));
        var source = Substitute.For<IArtworkAssetSource>();
        // Each host downloads distinct bytes, so no other test's import of the same content is a repeat.
        var bytes = Guid.NewGuid().ToByteArray();
        source.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ArtworkRole>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<DownloadedArtworkAsset>>(download is { } failed
                ? failed
                : new DownloadedArtworkAsset(bytes, "image/png", Candidate.Attribution, Candidate.SourcePageUrl)));
        var processor = Substitute.For<IArtworkImageProcessor>();
        processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ArtworkRole>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<ProcessedArtworkImage>>(new ProcessedArtworkImage("image/png", 600, 900, [])));
        var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IArtworkBrowsingService>();
            services.RemoveAll<IArtworkAssetSource>();
            services.RemoveAll<IArtworkImageProcessor>();
            services.AddSingleton(browsing);
            services.AddSingleton(source);
            services.AddSingleton(processor);
        }));
        var client = factory.CreateClient();
        using var authenticated = fixture.CreateAuthenticatedClient();
        foreach (var header in authenticated.DefaultRequestHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        return new CandidateHost(factory, client);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? ifMatch, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    private static async Task<string> TitleETagAsync(HttpClient client, string titleId)
    {
        using var response = await client.GetAsync($"/api/titles/{titleId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag!.Tag;
    }

    private async Task<int> MediaCountAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        return await scope.ServiceProvider.GetRequiredService<RomdDbContext>().TitleMedia.CountAsync(media => media.TitleId == id);
    }

    private async Task<string> SeedTitleAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var name = $"Artwork Candidate Test Title {Guid.NewGuid():N}";
        var title = new TitleEntity
        {
            PlatformId = await db.Platforms.OrderBy(platform => platform.Id).Select(platform => platform.Id).FirstAsync(),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return IdCoder.Encode(title.Id);
    }

    private sealed record CandidateHost(WebApplicationFactory<Romd.Admin.Host.Program> Factory, HttpClient Client)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
