using Romd.Domain.Source.Rom;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenIddict.Validation.AspNetCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Access.Queries.GetConsumerReleaseAccess;
using Romd.Consumer.Application.Account.Commands.UpdateConsumerUserSettings;
using Romd.Consumer.Application.Activity.Commands.UpsertPlaySession;
using Romd.Consumer.Application.Auth.Queries.GetCurrentConsumerUser;
using Romd.Consumer.Application.Browse.Queries.GetConsumerTitle;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Consumer.Application.Vfs.Commands.CreateVfsConnection;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Consumer.Activity;
using Romd.Contracts.Consumer.Auth;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Identity;
using Romd.Domain.Hashing;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     End-to-end coverage for the shared error envelope on the consumer host
///     (docs/decisions/api-contract-policy.md, "One Error Envelope";
///     docs/decisions/api-contract-policy.md, "Strict Validation"):
///     binding failures, endpoint-produced problems and unhandled exceptions all return RFC 9457
///     ProblemDetails with errorCode and traceId. The host runs without a database, so each test
///     replaces the persistence-backed service its route resolves.
/// </summary>
public sealed class ConsumerErrorEnvelopeTests : IDisposable
{
    private const string TestJwtSecret = "consumer-error-envelope-test-secret-at-least-32-characters";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"romd-consumer-error-envelope-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UnhandledException_Returns500EnvelopeWithoutExceptionDetails()
    {
        const string secretExceptionMessage = "secret-consumer-identity-failure-detail";
        var handler = Substitute.For<IQueryHandler<GetCurrentConsumerUserQuery, CurrentUser>>();
        handler.HandleAsync(Arg.Any<GetCurrentConsumerUserQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(secretExceptionMessage));
        await using var factory = CreateFactory(services => services.ReplaceWith(handler));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync("/api/me");

        await ProblemEnvelope.AssertSanitizedServerErrorAsync(
            response,
            secretExceptionMessage,
            typeof(InvalidOperationException));
    }

    [Fact]
    public async Task UpdateSettings_MalformedJsonBody_Returns400Envelope()
    {
        await using var factory = CreateFactory(services => services.ReplaceWith(
            Substitute.For<ICommandHandler<UpdateConsumerUserSettingsCommand, ConsumerUserSettings>>()));
        using var client = CreateUserClient(factory);
        using var content = new StringContent("{\"theme\":", Encoding.UTF8, "application/json");

        using var response = await client.PatchAsync("/api/me/settings", content);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody");
    }

    [Fact]
    public async Task UpsertPlaySession_QuotedNumericValue_Returns400Envelope()
    {
        await using var factory = CreateFactory(services => services.ReplaceWith(
            Substitute.For<ICommandHandler<UpsertPlaySessionCommand, PlaySession>>()));
        using var client = CreateUserClient(factory);
        using var content = new StringContent(
            "{\"clientId\":\"console\",\"titleId\":\"abc\",\"releaseId\":\"abc\"," +
            "\"startedAt\":\"2026-10-20T00:00:00Z\",\"activeDurationSeconds\":\"5\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PutAsync($"/api/me/activity/play-sessions/{Guid.NewGuid()}", content);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody");
    }

    [Fact]
    public async Task GetTitle_MalformedRouteIdentity_Returns400Envelope()
    {
        await using var factory = CreateFactory(services => services.ReplaceWith(
            Substitute.For<IQueryHandler<GetConsumerTitleQuery, ConsumerTitleDetail>>()));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync("/api/titles/not-a-sqid!");

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").TryGetProperty("titleId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetReleaseAccess_MalformedReleaseId_Returns400EnvelopeWithFieldError()
    {
        var handler = Substitute.For<IQueryHandler<GetConsumerReleaseAccessQuery, ConsumerReleaseAccess>>();
        await using var factory = CreateFactory(services => services.ReplaceWith(handler));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync("/api/releases/not-valid!/access");

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").TryGetProperty("releaseId", out _).ShouldBeTrue();
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task CreateVfsConnection_VfsUnavailable_Returns503Envelope()
    {
        await using var factory = CreateFactory(services => services.ReplaceWith(
            Substitute.For<ICommandHandler<CreateVfsConnectionCommand, IssuedVfsCredential>>()));
        using var client = CreateUserClient(factory);

        using var response = await client.PostAsJsonAsync("/api/me/connections", new { name = "Living room" });

        using var body = await ProblemEnvelope.ReadAsync(response, 503, "Vfs.ConnectionsUnavailable");
    }

    [Fact]
    public async Task GetDiscSetStatus_LibraryUnavailable_Returns409Envelope()
    {
        var repository = Substitute.For<IConsumerDiscSetRepository>();
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.LibraryUnavailable());
        await using var factory = CreateFactory(services => services.ReplaceWith(repository));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync($"/api/disc-sets/{IdCoder.Encode(41)}/status");

        using var body = await ProblemEnvelope.ReadAsync(response, 409, "Consumer.CurrentLibraryUnavailable");
    }

    [Fact]
    public async Task GetDiscSetStatus_UnknownDiscSet_Returns404Envelope()
    {
        var repository = Substitute.For<IConsumerDiscSetRepository>();
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound(7));
        await using var factory = CreateFactory(services => services.ReplaceWith(repository));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync($"/api/disc-sets/{IdCoder.Encode(41)}/status");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Consumer.ContentNotFound");
    }

    [Fact]
    public async Task GetSystem_UnknownKey_Returns404SystemsNotFoundEnvelope()
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.ResolveSystemIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        await using var factory = CreateFactory(services => services.ReplaceWith(catalog));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync("/api/systems/unknown-system");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Systems.NotFound");
    }

    [Fact]
    public async Task GetCompany_UnknownKey_Returns404Envelope()
    {
        var reader = Substitute.For<ICompanyReferenceReader>();
        reader.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReferenceResourceResult<Romd.Contracts.Common.ReferenceCatalog.CompanyResource,
                Romd.Contracts.Common.ReferenceCatalog.CompanyOverrides>?)null);
        await using var factory = CreateFactory(services => services.ReplaceWith(reader));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync("/api/companies/unknown-company");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "ReferenceResource.NotFound");
    }

    [Theory]
    [InlineData("/api/catalog-snapshot/systems?revision=wrong", "revision")]
    [InlineData("/api/catalog-snapshot/systems?revision=PIN&cursor=", "cursor")]
    [InlineData("/api/catalog-snapshot/systems?revision=PIN&limit=101", "limit")]
    [InlineData("/api/catalog-snapshot/systems?revision=PIN&limit=0", "limit")]
    [InlineData("/api/catalog-snapshot/systems?revision=PIN&limit=1&limit=2", "limit")]
    [InlineData("/api/catalog-snapshot/systems?revision=PIN&revision=PIN", "revision")]
    [InlineData("/api/catalog-snapshot/systems?revision=PIN&unknown=1", "unknown")]
    [InlineData("/api/catalog-snapshot?unknown=1", "unknown")]
    [InlineData("/api/systems?cursor=", "cursor")]
    [InlineData("/api/systems?query=!!!", "query")]
    [InlineData("/api/systems?query=a&query=b", "query")]
    [InlineData("/api/systems?sortBy=name:desc", "sortBy")]
    [InlineData("/api/companies?limit=101", "limit")]
    [InlineData("/api/companies?queryy=a", "queryy")]
    public async Task ReferenceReads_InvalidQuery_ReturnsField400BeforeAnyCatalogOrResourceRead(string path, string field)
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        var systems = Substitute.For<ISystemReferenceReader>();
        var companies = Substitute.For<ICompanyReferenceReader>();
        await using var factory = CreateFactory(services => { services.ReplaceWith(catalog); services.ReplaceWith(systems); services.ReplaceWith(companies); });
        using var client = CreateUserClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, path.Replace("PIN", new string('a', 64)));
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await client.SendAsync(request);
        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        await catalog.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
        await systems.DidNotReceive().ListAsync(Arg.Any<ReferencePageRequest>(), Arg.Any<CancellationToken>());
        await companies.DidNotReceive().ListAsync(Arg.Any<ReferencePageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SnapshotChildren_ReadOnePublishedDocument_KeepSequenceAndRejectOldPinEvenWithConditionalHeader()
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        var published = new Romd.Contracts.Common.ReferenceCatalog.ReferenceCatalog
        {
            Revision = new string('a', 64),
            BuiltInVersion = 7,
            Systems = [new("local-z", "Z", "Z", null, null), new("local-a", "A", "A", null, null)],
            RatingBoards = [],
            Ratings = []
        };
        catalog.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(published);
        await using var factory = CreateFactory(services => services.ReplaceWith(catalog));
        using var client = CreateUserClient(factory);
        var descriptor = (await client.GetFromJsonAsync<Romd.Contracts.Common.ReferenceCatalog.ReferenceCatalogDescriptor>("/api/catalog-snapshot"))!;
        descriptor.CatalogRevision.ShouldBe(published.Revision);
        descriptor.Counts.Systems.ShouldBe(2);
        descriptor.Revision.ShouldNotBe(descriptor.CatalogRevision);
        catalog.ClearReceivedCalls();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog-snapshot/systems?revision=" + published.Revision + "&limit=1");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Headers.ETag.ShouldBeNull();
        var first = (await response.Content.ReadFromJsonAsync<Romd.Contracts.Common.Models.Page<Romd.Contracts.Common.ReferenceCatalog.ReferenceSystem>>())!;
        first.Items.ShouldHaveSingleItem().Key.ShouldBe("local-z");
        await catalog.Received(1).GetCurrentAsync(Arg.Any<CancellationToken>());
        var next = (await client.GetFromJsonAsync<Romd.Contracts.Common.Models.Page<Romd.Contracts.Common.ReferenceCatalog.ReferenceSystem>>(
            "/api/catalog-snapshot/systems?revision=" + published.Revision + "&cursor=" + Uri.EscapeDataString(first.NextCursor!)))!;
        next.Items.ShouldHaveSingleItem().Key.ShouldBe("local-a");
        next.HasNextPage.ShouldBeFalse();
        next.NextCursor.ShouldBeNull();
        catalog.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(published with { Revision = new string('b', 64) });
        using var stale = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
        stale.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var rejected = await client.SendAsync(stale);
        using var problem = await ProblemEnvelope.ReadAsync(rejected, 409, "ReferenceCatalog.SnapshotChanged");
    }

    [Theory]
    [InlineData("/api/catalog-snapshot")]
    [InlineData("/api/catalog-snapshot/systems?revision=")]
    [InlineData("/api/catalog-snapshot/companies?revision=")]
    [InlineData("/api/catalog-snapshot/regions?revision=")]
    [InlineData("/api/catalog-snapshot/languages?revision=")]
    [InlineData("/api/catalog-snapshot/rating-boards?revision=")]
    [InlineData("/api/catalog-snapshot/ratings?revision=")]
    public async Task GetCatalogSnapshot_NoPublishedCatalog_Returns503Envelope(string path)
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns((Romd.Contracts.Common.ReferenceCatalog.ReferenceCatalog?)null);
        await using var factory = CreateFactory(services => services.ReplaceWith(catalog));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync(path.EndsWith('=') ? path + new string('a', 64) : path);

        using var body = await ProblemEnvelope.ReadAsync(response, 503, "ReferenceCatalog.Unavailable");
    }

    [Fact]
    public async Task PersistenceConflictException_IsAdminOnly_Returns500Envelope()
    {
        const string privateDetail = "secret-consumer-persistence-conflict-detail";
        var handler = Substitute.For<IQueryHandler<GetCurrentConsumerUserQuery, CurrentUser>>();
        handler.HandleAsync(Arg.Any<GetCurrentConsumerUserQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PersistenceConflictException(new InvalidOperationException(privateDetail)));
        await using var factory = CreateFactory(services => services.ReplaceWith(handler));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync("/api/me");

        await ProblemEnvelope.AssertSanitizedServerErrorAsync(
            response,
            privateDetail,
            typeof(PersistenceConflictException));
    }

    [Theory]
    [InlineData("/media/not-a-sqid!")]
    [InlineData("/artwork/not-a-sqid!/thumbnail/1")]
    public async Task ProtocolMediaRoute_MalformedIdentity_Returns400Envelope(string path)
    {
        await using var factory = CreateFactory(services => services
            .ReplaceWith(Substitute.For<IConsumerMediaArtifactResolver>())
            .ReplaceWith(Substitute.For<IArtworkDelivery>()));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
    }

    [Fact]
    public async Task ProtocolMediaRoute_UnknownArtifact_Returns404Envelope()
    {
        var resolver = Substitute.For<IConsumerMediaArtifactResolver>();
        resolver.ResolveMediaAsync(Arg.Any<ConsumerMediaArtifactRequest>(), Arg.Any<CancellationToken>())
            .Returns((ConsumerMediaArtifact?)null);
        await using var factory = CreateFactory(services => services.ReplaceWith(resolver));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/media/{IdCoder.Encode(41)}");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Consumer.ContentNotFound");
    }

    [Fact]
    public async Task ProtocolArtworkRoute_UnknownVariant_Returns404Envelope()
    {
        var delivery = Substitute.For<IArtworkDelivery>();
        delivery.OpenAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ArtworkDelivery?)null);
        await using var factory = CreateFactory(services => services.ReplaceWith(delivery));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/artwork/{IdCoder.Encode(41)}/thumbnail/1");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Delivery.ContentNotFound");
    }

    [Theory]
    [InlineData("invalid-token", 401, "Consumer.InvalidContentGrant")]
    [InlineData("missing-artifact", 404, "Consumer.ContentNotFound")]
    [InlineData("oversized-artifact", 404, "Consumer.ContentNotFound")]
    [InlineData("mismatched-artifact", 404, "Consumer.ContentNotFound")]
    public async Task OriginalLayoutRedemption_FailurePath_ReturnsEnvelope(string failure, int status, string errorCode)
    {
        byte[] pinned = Encoding.UTF8.GetBytes("FILE \"track.bin\" BINARY\n");
        var grant = new ConsumerOriginalLayoutGrant(Guid.NewGuid(), 1, 2, 3, 4, Guid.NewGuid(), 5,
            Sha256.FromBytes(System.Security.Cryptography.SHA256.HashData(pinned)), pinned.Length, "game.cue");
        var issuer = Substitute.For<IConsumerOriginalLayoutGrantIssuer>();
        issuer.Validate(Arg.Any<string>()).Returns(failure == "invalid-token"
            ? ConsumerErrors.InvalidContentGrant()
            : new ConsumerValidatedOriginalLayoutGrant(grant, DateTimeOffset.UtcNow.AddMinutes(5)));
        var artifacts = Substitute.For<IConsumerContentArtifactResolver>();
        artifacts.ResolveAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>()).Returns(_ => failure switch
        {
            "oversized-artifact" => new MemoryStream([.. pinned, .. pinned]),
            "mismatched-artifact" => new MemoryStream(Encoding.UTF8.GetBytes(new string('x', pinned.Length))),
            _ => (Stream?)null
        });
        await using var factory = CreateFactory(services => services.ReplaceWith(issuer).ReplaceWith(artifacts));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/delivery/original-layout/token");

        using var body = await ProblemEnvelope.ReadAsync(response, status, errorCode);
    }

    private static RomCdArchiveDownload UnavailableArchive()
    {
        var sha = Sha1.Parse(new string('b', 40));
        var identity = new RomLogicalIdentity(32 * 2352, Sha256.Parse(new string('a', 64)), sha,
            Md5.Parse(new string('c', 32)), Crc32.FromUInt32(1));
        var bin = new RomCdFileIdentity(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
        var cue = new RomCdFileIdentity(98, Sha256.Parse(new string('d', 64)), sha, identity.Md5, Crc32.FromUInt32(2));
        var container = new RomContainerFacts.Chd(5, 32 * 2448, 4 * 2448, 2448, 8, [0x63647a73, 0, 0, 0], false, sha, sha,
            [new(0x43485432, 1, "TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:32 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"u8)]);
        var manifest = new RomReconstructionManifest.CdTracks(32, [new(1, 0, 32, RomCdTrackKind.Mode2Raw)],
            [new RomCdBinMember("disc.bin", bin, bin, [1]), new RomCdStoredMember("disc.cue", cue, RomCdStoredRole.Cue)]);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), Sha256.Parse(new string('e', 64)), 1024,
            new RomDecoderIdentity.Native(new("linux-x64", sha, 1)), "complete-cd/v1", "test-verifier", DateTimeOffset.UnixEpoch,
            [new(new RomOutputSelector.Member(0), identity)], container, sha, sha);
        return new(9, Guid.NewGuid(), new(19, 29, proof.ArtifactSha256, proof.ArtifactSize, container, manifest,
            [new(39, 7, new RomOutputSelector.Member(0), identity)], [proof], [new(1, 49, cue.Sha256, cue.Size)]), "game.zip");
    }

    [Fact]
    public async Task CdArchiveDownload_UnavailableStream_Returns404Envelope()
    {
        var repository = Substitute.For<IConsumerCdArchiveRepository>();
        // The complete retained archive exists; only opening its stream fails.
        repository.FindAsync(Arg.Any<ConsumerLibraryScope>(), 41, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<RomCdArchiveDownload>.Found(7, UnavailableArchive()));
        var reader = Substitute.For<IRomCdArchiveDownloadReader>();
        reader.OpenAsync(Arg.Any<RomCdArchiveDownload>(), Arg.Any<CancellationToken>()).Returns((Stream?)null);
        await using var factory = CreateFactory(services => services.ReplaceWith(repository).ReplaceWith(reader));
        using var client = CreateUserClient(factory);

        using var response = await client.GetAsync($"/api/releases/{IdCoder.Encode(41)}/cd-archive");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Delivery.ContentNotFound");
    }

    [Fact]
    public async Task ReferenceAsset_UnknownHash_Returns404Envelope()
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.GetAssetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ReferenceAssetContent?)null);
        await using var factory = CreateFactory(services => services.ReplaceWith(catalog));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/assets/{new string('a', 64)}");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "ReferenceAsset.NotFound");
    }

    private WebApplicationFactory<Romd.Consumer.Host.Program> CreateFactory(Action<IServiceCollection> configureServices) =>
        RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(
            _dataDirectory,
            "Testing",
            TestJwtSecret,
            services =>
            {
                services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                configureServices(services);
            });

    private static HttpClient CreateUserClient(WebApplicationFactory<Romd.Consumer.Host.Program> factory) =>
        factory.CreateClient().WithTestUser(Guid.NewGuid(), "player@localhost", [RomdRoleType.User]);
}
