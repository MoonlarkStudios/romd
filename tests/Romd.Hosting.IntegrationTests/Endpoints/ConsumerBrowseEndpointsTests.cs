using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Consumer.Browse;
using Romd.Domain.Hashing;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence.Search;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;
using CommonModels = Romd.Contracts.Common.Models;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerBrowseEndpointsTests
{
    private const string TestJwtAudience = "RomdConsumerBrowseEndpointTests";
    private const string TestJwtIssuer = "RomdConsumerBrowseEndpointTests";
    private const string TestJwtSecret = "consumer-browse-test-secret-at-least-32-characters";

    [Theory]
    [InlineData("snes", 2)]
    [InlineData("nes", 0)]
    [InlineData("unfamiliar-key", 0)]
    public async Task SearchCatalog_SystemKeyFilter_PreservesLibraryScope(string key, int expectedCount)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);
        var response = await client.GetAsync($"/api/catalog?systemKey={key}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();
        body!.Items.Count.ShouldBe(expectedCount);
        body.Items.ShouldAllBe(x => x.System.Key == key);
    }

    [Fact]
    public async Task ListPlatforms_AuthenticatedUser_ReturnsAmbientLibraryPlatformsOnly()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.GetAsync("/api/me/library/systems");
        var body = await response.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerPlatformSummaryDto>>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Items.Select(item => item.Name).ShouldBe(["Super Nintendo"]);
        body.Items.ShouldNotContain(item => item.Name == "Nintendo Entertainment System");
        body.Items.ShouldNotContain(item => item.Name == "Sega Genesis");
        body.Items.Single(item => item.Name == "Super Nintendo").TitleCount.ShouldBe(2);
    }

    [Fact]
    public async Task CompanyRenameAndReset_ReachLibrarySystemResponsesThroughRelationships()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        await using var db = fixture.CreateSetupDbContext();
        var platform = await db.Platforms.AsTracking().SingleAsync(x => x.CanonicalKey == "snes");
        platform.Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd;
        platform.BuiltInVersion = 1;
        platform.BaseName = "Super Nintendo";
        platform.BaseCompactLabel = "SNES";
        platform.ReferenceOverrides = new();
        db.Companies.Add(new CompanyEntity
        {
            Key = "nintendo", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1,
            Name = "Nintendo", BaseName = "Nintendo", Overrides = new()
        });
        db.SystemCompanies.Add(new SystemCompanyEntity { PlatformId = platform.Id, CompanyKey = "nintendo" });
        await db.SaveChangesAsync();
        var reader = new Romd.Persistence.ReferenceData.CompanyReferenceReader(db);
        var repository = new Romd.Persistence.ReferenceData.CompanyReferenceRepository(db);
        var session = new Romd.Persistence.ReferenceData.ReferenceMutationSession(db);
        var company = (await reader.GetAsync("nintendo", default))!;
        var edit = await new Romd.Admin.Application.ReferenceData.Companies.Commands.UpdateCompanyCommandHandler(session, repository, reader)
            .HandleAsync(new("nintendo", new(Name: "Nintendo local"), company.ETag));
        edit.IsError.ShouldBeFalse();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);
        await AssertName("Nintendo local");
        (await new Romd.Admin.Application.ReferenceData.Companies.Commands.ResetCompanyOverridesCommandHandler(session, repository, reader)
            .HandleAsync(new("nintendo", null, edit.Value.ETag))).IsError.ShouldBeFalse();
        await AssertName("Nintendo");

        async Task AssertName(string name)
        {
            var list = (await client.GetFromJsonAsync<CommonModels.Page<ConsumerPlatformSummaryDto>>("/api/me/library/systems"))!;
            list.Items.Single(x => x.Key == "snes").Manufacturer.ShouldBe(name);
            var detail = (await client.GetFromJsonAsync<ConsumerPlatformDetailDto>("/api/me/library/systems/snes"))!;
            detail.Manufacturer.ShouldBe(name);
        }
    }

    [Fact]
    public async Task GetPlatform_AuthenticatedUser_ReturnsPlatformDetail()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.GetAsync($"/api/me/library/systems/snes");
        var body = await response.Content.ReadFromJsonAsync<ConsumerPlatformDetailDto>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("Super Nintendo");
        body.TitleCount.ShouldBe(2);
        body.Media.Single().Url.ShouldStartWith("/media/");
    }

    [Fact]
    public async Task SearchCatalog_AuthenticatedUser_ReturnsAmbientLibraryTitlesOnly()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.GetAsync("/api/catalog?limit=10");
        var body = await response.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Items.Select(item => item.Name).ShouldBe(["Chrono Trigger", "Partial Quest"]);
        body.Items.ShouldNotContain(item => item.Name == "Mega Man");
        body.Items.ShouldNotContain(item => item.Name == "Sonic the Hedgehog");

        var chrono = body.Items.Single(item => item.Id == IdCoder.Encode(seed.ChronoTitleId));
        chrono.ReleaseCount.ShouldBe(1);
        chrono.DefaultReleaseId.ShouldBe(IdCoder.Encode(seed.ChronoReleaseId));
    }

    [Fact]
    public async Task SearchCatalog_TextQuery_MatchesViaFullTextPrefixWithinOwnedScope()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var match = await client.GetAsync("/api/catalog?query=chrono&limit=10");
        var matchBody = await match.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();

        match.StatusCode.ShouldBe(HttpStatusCode.OK);
        matchBody.ShouldNotBeNull();
        matchBody.Items.Select(item => item.Name).ShouldBe(["Chrono Trigger"]);

        // FTS prefix matches "Mega Man", but it is not owned in this library, so the
        // owned-materialized-title scope still excludes it.
        var unowned = await client.GetAsync("/api/catalog?query=mega&limit=10");
        var unownedBody = await unowned.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();

        unowned.StatusCode.ShouldBe(HttpStatusCode.OK);
        unownedBody.ShouldNotBeNull();
        unownedBody.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchCatalog_ReleasePreference_ReturnsPreferredDefaultReleaseId()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        var alternateRelease = await fixture.AddAlternateChronoReleaseAsync(seed.LibraryId, seed.ChronoTitleId);
        var user = await fixture.CreateConsumerUserAsync("player", "player@localhost", seed.LibraryId);
        using var client = fixture.CreateAuthenticatedClient(user.Id, user.Email!, seed.LibraryId);

        var updateSettingsResponse = await client.PutAsJsonAsync(
            "/api/account/settings",
            new UpdateConsumerUserSettingsRequest(
                "system",
                new ConsumerReleasePreferenceDto(
                    [IdCoder.Encode(alternateRelease.RegionId)],
                    [],
                    "none")));
        var response = await client.GetAsync("/api/catalog?limit=10");
        var body = await response.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();

        updateSettingsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        var chrono = body.Items.Single(item => item.Id == IdCoder.Encode(seed.ChronoTitleId));
        chrono.DefaultReleaseId.ShouldBe(IdCoder.Encode(alternateRelease.ReleaseId));
    }

    [Fact]
    public async Task ConsumerBrowse_AccessibleReleases_ReturnsAllOwnedAccessibleReleases()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        var alternateRelease = await fixture.AddAlternateChronoReleaseAsync(seed.LibraryId, seed.ChronoTitleId);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var catalogResponse = await client.GetAsync("/api/catalog?limit=10");
        var catalogBody = await catalogResponse.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();
        var detailResponse = await client.GetAsync($"/api/titles/{IdCoder.Encode(seed.ChronoTitleId)}");
        var detailBody = await detailResponse.Content.ReadFromJsonAsync<ConsumerTitleDetailDto>();

        catalogResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        catalogBody.ShouldNotBeNull();
        var chrono = catalogBody.Items.Single(item => item.Id == IdCoder.Encode(seed.ChronoTitleId));
        chrono.ReleaseCount.ShouldBe(2);
        chrono.DefaultReleaseId.ShouldNotBeNull();
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        detailBody.ShouldNotBeNull();
        detailBody.Releases
            .Select(release => release.Id)
            .ShouldBe([IdCoder.Encode(alternateRelease.ReleaseId), IdCoder.Encode(seed.ChronoReleaseId)], ignoreOrder: true);
    }

    [Fact]
    public async Task SearchCatalog_CompletenessFilter_FiltersOwnedReleasesByCompleteness()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var completeResponse = await client.GetAsync("/api/catalog?completeness=complete&limit=10");
        var completeBody = await completeResponse.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();
        var partialResponse = await client.GetAsync("/api/catalog?completeness=partial&limit=10");
        var partialBody = await partialResponse.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();
        var localPayloadResponse = await client.GetAsync("/api/catalog?localPayload=none&limit=10");
        var localPayloadBody =
            await localPayloadResponse.Content.ReadFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>();

        completeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        completeBody.ShouldNotBeNull();
        completeBody.Items.Select(item => item.Name).ShouldBe(["Chrono Trigger"]);

        partialResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        partialBody.ShouldNotBeNull();
        partialBody.Items.Select(item => item.Name).ShouldBe(["Partial Quest"]);

        localPayloadResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        localPayloadBody.ShouldNotBeNull();
        localPayloadBody.Items.Select(item => item.Name).ShouldBe(["Chrono Trigger", "Partial Quest"]);
        localPayloadBody.Items.ShouldNotContain(item => item.Name == "Mega Man");
    }

    [Fact]
    public async Task TitleAndCatalog_EmbedEffectiveRatingPresentationWithoutSnapshotLookup()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        await using (var db = fixture.CreateSetupDbContext())
        {
            db.RatingBoards.Add(new RatingBoardEntity { Key = "Esrb", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "ESRB" });
            db.Ratings.Add(new RatingEntity { Key = "Esrb:E", BoardKey = "Esrb", Code = "E", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Everyone", BaseDescription = "Default explanation" });
            db.TitleContentRatings.Add(new TitleContentRatingEntity
            {
                TitleId = seed.ChronoTitleId, Board = (int)Romd.Domain.Catalog.Ratings.RatingBoard.Esrb,
                Code = "E", SourceId = "manual"
            });
            await db.SaveChangesAsync();
        }
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);
        var detail = (await client.GetFromJsonAsync<ConsumerTitleDetailDto>($"/api/titles/{IdCoder.Encode(seed.ChronoTitleId)}"))!;
        var page = (await client.GetFromJsonAsync<CommonModels.Page<ConsumerTitleCardDto>>("/api/catalog"))!;
        var rating = detail.ContentRatings.ShouldHaveSingleItem();
        rating.Board.ShouldBe("Esrb");
        rating.BoardName.ShouldBe("ESRB");
        rating.Code.ShouldBe("E");
        rating.Name.ShouldBe("Everyone");
        rating.Description.ShouldBe("Default explanation");
        rating.Icon.ShouldBeNull();
        page.Items.Single(x => x.Id == detail.Id).ContentRatings.ShouldHaveSingleItem().ShouldBe(rating);
    }

    [Fact]
    public async Task GetTitle_AuthenticatedUser_ReturnsConsumerSafeTitleDetail()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.GetAsync($"/api/titles/{IdCoder.Encode(seed.ChronoTitleId)}");
        string json = await response.Content.ReadAsStringAsync();
        var body = await response.Content.ReadFromJsonAsync<ConsumerTitleDetailDto>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("Chrono Trigger");
        body.System!.Name.ShouldBe("Super Nintendo");
        body.System.Key.ShouldBe("snes");
        body.ContentRatings.ShouldNotBeNull();
        body.Media.Single().Url.ShouldStartWith("/media/");
        var release = body.Releases.ShouldHaveSingleItem();
        release.Id.ShouldBe(IdCoder.Encode(seed.ChronoReleaseId));
        release.Name.ShouldBe("Chrono Trigger (USA)");
        release.SizeBytes.ShouldBe((ByteCount)1);
        release.IsComplete.ShouldBeTrue();
        body.DefaultReleaseId.ShouldBe(IdCoder.Encode(seed.ChronoReleaseId));
        body.Releases.ShouldNotContain(item => item.Id == IdCoder.Encode(seed.NonExposedReleaseId));

        json.ShouldNotContain("fileId", Case.Insensitive);
        json.ShouldNotContain("hash", Case.Insensitive);
        json.ShouldNotContain("datId", Case.Insensitive);
        json.ShouldNotContain("datFile", Case.Insensitive);
        json.ShouldNotContain("provenance", Case.Insensitive);
        json.ShouldNotContain("curation", Case.Insensitive);
        json.ShouldNotContain("materialization", Case.Insensitive);
    }

    [Fact]
    public async Task GetTitle_InvalidLibraryConfiguration_ReturnsNotFound()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.InvalidLibraryId);

        var response = await client.GetAsync($"/api/titles/{IdCoder.Encode(seed.ChronoTitleId)}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ServeMedia_ConsumerMediaRoute_ReturnsStoredMedia()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        await fixture.SeedBrowseDataAsync();
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync($"/media/{IdCoder.Encode(1)}");
        byte[] content = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/jpeg");
        content.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Artwork_MissingStoredBytes_ReturnsNotFoundWithoutImmutableCaching()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        // File 3 has a database record but no CAS bytes.
        await fixture.SeedArtworkAsync(seed.ChronoTitleId, variantFileId: 3);
        using var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync($"/artwork/{IdCoder.Encode(1)}/card/v1");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.CacheControl?.Extensions.ShouldNotContain(e => e.Name == "immutable");
    }

    [Fact]
    public async Task Artwork_SelectedVariant_IsResolvedAndDeliveredWithImmutableVersion()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        await fixture.SeedArtworkAsync(seed.ChronoTitleId);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "artwork@localhost", seed.LibraryId);

        var detail = await client.GetFromJsonAsync<ConsumerTitleDetailDto>($"/api/titles/{IdCoder.Encode(seed.ChronoTitleId)}");
        var poster = detail!.Artwork.Single(a => a.Role == "Poster");
        poster.Url.ShouldBe($"/artwork/{IdCoder.Encode(1)}/card/v1");
        poster.Width.ShouldBe(300);
        poster.OriginalWidth.ShouldBe(600);
        poster.Fit.ShouldBe("Contain");
        detail.Artwork.Single(a => a.Role == "Hero").Url.ShouldBeNull();

        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(poster.Url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe([1, 2, 3]);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        response.Headers.CacheControl!.Extensions.ShouldContain(e => e.Name == "immutable");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.ETag.ShouldNotBeNull();
        using var conditional = new HttpRequestMessage(HttpMethod.Get, poster.Url);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag);
        (await anonymous.SendAsync(conditional)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await anonymous.GetAsync(poster.Url!.Replace("/v1", "/stale"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(poster.Url.Replace("/card/", "/original/"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/api/me/library/systems")]
    [InlineData("/api/me/library/systems/abc")]
    [InlineData("/api/catalog")]
    [InlineData("/api/titles/abc")]
    public async Task ConsumerBrowseEndpoints_UnauthenticatedRequest_ReturnsUnauthorized(string path)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SearchCatalog_AuthenticatedUserWithoutLibrary_ReturnsNotFound()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost");

        var response = await client.GetAsync("/api/catalog");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/api/me/library/systems/{0}/titles")]
    [InlineData("/api/titles/{1}/details")]
    [InlineData("/api/libraries/{2}/catalog")]
    public async Task ConsumerBrowse_ForbiddenManagementShapes_AreNotMapped(string pathTemplate)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var seed = await fixture.SeedBrowseDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);
        string path = string.Format(
            pathTemplate,
            IdCoder.Encode(seed.SnesPlatformId),
            IdCoder.Encode(seed.ChronoTitleId),
            IdCoder.Encode(seed.LibraryId));

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed record BrowseSeed(
        int LibraryId,
        int SnesPlatformId,
        int ChronoTitleId,
        int ChronoReleaseId,
        int NonExposedReleaseId,
        int InvalidLibraryId);

    private sealed record AlternateReleaseSeed(int ReleaseId, int RegionId);

    private sealed class ConsumerHostFixture : IAsyncDisposable
    {
        private readonly string _tempDataDirectory;
        private readonly PostgreSqlTestDatabase _database;

        private ConsumerHostFixture(
            WebApplicationFactory<Romd.Consumer.Host.Program> factory,
            PostgreSqlTestDatabase database,
            string tempDataDirectory)
        {
            Factory = factory;
            _database = database;
            _tempDataDirectory = tempDataDirectory;
        }

        public WebApplicationFactory<Romd.Consumer.Host.Program> Factory { get; }

        public static async Task<ConsumerHostFixture> CreateAsync()
        {
            var database = PostgreSqlTestDatabase.Create();

            string tempDataDirectory = Path.Combine(Path.GetTempPath(), $"romd-consumer-browse-{Guid.NewGuid():N}");
            var webRoot = Path.Combine(tempDataDirectory, "wwwroot");
            Directory.CreateDirectory(webRoot);
            OpenIddictSigningKey.EnsureCreated(tempDataDirectory);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");

            var factory = new WebApplicationFactory<Romd.Consumer.Host.Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseContentRoot(AppContext.BaseDirectory);
                    builder.UseWebRoot(webRoot);
                    builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                    builder.UseSetting("Romd:DataDirectory", tempDataDirectory);
                    builder.UseSetting(
                        $"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}",
                        database.ConnectionString);
                    builder.UseSetting("Romd:JwtSecret", TestJwtSecret);
                    builder.UseSetting("Romd:JwtIssuer", TestJwtIssuer);
                    builder.UseSetting("Romd:ConsumerHost:JwtAudience", TestJwtAudience);

                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IHostedService>();
                        services.RemoveAll<DbContextOptions<RomdDbContext>>();

                        services.AddDbContext<RomdDbContext>((sp, options) =>
                        {
                            PostgreSqlConfiguration.Configure(options, database.ConnectionString);
                            options.UseOpenIddict();
                            options.EnableDetailedErrors();
                            options.AddInterceptors(
                                sp.GetRequiredService<AuditInterceptor>(),
                                new SearchDocumentInterceptor());
                        });

                        services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                    });
                });

            var fixture = new ConsumerHostFixture(factory, database, tempDataDirectory);
            await fixture.MigrateAsync();

            return fixture;
        }

        public HttpClient CreateAuthenticatedClient(Guid userId, string email, int? libraryId = null)
        {
            using var context = CreateSetupDbContext();
            if (!context.Users.Any(user => user.Id == userId))
            {
                string userName = $"consumer-{userId:N}";
                context.Users.Add(new RomdUser
                {
                    Id = userId,
                    UserName = userName,
                    NormalizedUserName = userName.ToUpperInvariant(),
                    Email = email,
                    NormalizedEmail = email.ToUpperInvariant(),
                    LibraryId = libraryId,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                context.SaveChanges();
            }

            var client = Factory.CreateClient();
            client.WithTestUser(userId, email, [RomdRoleType.User], libraryId);

            return client;
        }

        public async Task<RomdUser> CreateConsumerUserAsync(string userName, string email, int? libraryId = null)
        {
            await using var context = CreateSetupDbContext();
            var user = RomdUser.Create(userName, email);
            user.NormalizedUserName = userName.ToUpperInvariant();
            user.NormalizedEmail = email.ToUpperInvariant();
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            user.LibraryId = libraryId;

            context.Users.Add(user);
            await context.SaveChangesAsync();

            return user;
        }

        public async Task<BrowseSeed> SeedBrowseDataAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var contentStore = scope.ServiceProvider.GetRequiredService<IContentAddressableStore>();
            await using var context = CreateSetupDbContext();
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();

            var livingRoom = Library.CreateNew("Living Room", new LibraryConfiguration());
            var otherLibrary = Library.CreateNew("Other Room", new LibraryConfiguration());
            context.Libraries.AddRange(
                LibraryEntity.FromDomain(livingRoom),
                LibraryEntity.FromDomain(otherLibrary),
                new LibraryEntity
                {
                    Name = "Invalid Room",
                    ConfigurationJson = "{}",
                    ConfigurationState = LibraryConfigurationState.Invalid.ToString(),
                    ConfigurationError = "Invalid test configuration.",
                    NeedsMaterialization = false,
                    CreatedAt = now,
                    CreatedByUserId = userId
                });
            await context.SaveChangesAsync();

            await context.Libraries
                .Where(library => library.Name == "Living Room" || library.Name == "Other Room")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(library => library.NeedsMaterialization, false));

            int libraryId = await context.Libraries
                .Where(library => library.Name == "Living Room")
                .Select(library => library.Id)
                .SingleAsync();
            int otherLibraryId = await context.Libraries
                .Where(library => library.Name == "Other Room")
                .Select(library => library.Id)
                .SingleAsync();
            int invalidLibraryId = await context.Libraries
                .Where(library => library.Name == "Invalid Room")
                .Select(library => library.Id)
                .SingleAsync();

            context.Platforms.AddRange(
                new PlatformEntity
                {
                    Id = 1,
                    Name = "Super Nintendo",
                    ShortName = "snes",
                    Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes",
                    Manufacturer = "Nintendo",
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new PlatformEntity
                {
                    Id = 2,
                    Name = "Nintendo Entertainment System",
                    ShortName = "nes",
                    Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo Entertainment System", BaseCompactLabel = "Nintendo Entertainment System", CanonicalKey = "nes",
                    Manufacturer = "Nintendo",
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new PlatformEntity
                {
                    Id = 3,
                    Name = "Sega Genesis",
                    ShortName = "genesis",
                    Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Sega Genesis", BaseCompactLabel = "Sega Genesis", CanonicalKey = "genesis",
                    Manufacturer = "Sega",
                    CreatedAt = now,
                    CreatedByUserId = userId
                });

            await using var coverStream = new MemoryStream([1, 2, 3]);
            var coverStoreResult = await contentStore.StoreAsync(coverStream);

            context.Files.AddRange(
                NewFile(1, NewSha256(1), now, userId),
                NewFile(2, coverStoreResult.Key.Hash, now, userId),
                NewFile(3, NewSha256(3), now, userId));

            context.DatFiles.Add(new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
                },
                Id = 1,
                Name = "Consumer Browse DAT",
                Description = "Consumer browse test data",
                Type = "NoIntro",
                PlatformId = 1,
                OriginalFilename = "consumer-browse.dat",
                FileId = 1,
                GameCount = 5,
                CreatedAt = now,
                CreatedByUserId = userId
            });

            context.Titles.AddRange(
                new TitleEntity
                {
                    Id = 1,
                    PlatformId = 1,
                    Name = "Chrono Trigger",
                    NormalizedName = "chrono trigger",
                    Description = "Time travel RPG.",
                    Publisher = "Square",
                    Developer = "Square",
                    Genre = "RPG",
                    ReleaseDate = new DateOnly(1995, 3, 11),
                    Players = 1,
                    Rating = 9.8,
                    EnrichmentStatus = "Completed",
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new TitleEntity
                {
                    Id = 2,
                    PlatformId = 2,
                    Name = "Mega Man",
                    NormalizedName = "mega man",
                    Genre = "Action",
                    Rating = 8.7,
                    EnrichmentStatus = "Completed",
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new TitleEntity
                {
                    Id = 3,
                    PlatformId = 3,
                    Name = "Sonic the Hedgehog",
                    NormalizedName = "sonic the hedgehog",
                    Genre = "Platformer",
                    Rating = 8.9,
                    EnrichmentStatus = "Completed",
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new TitleEntity
                {
                    Id = 4,
                    PlatformId = 1,
                    Name = "Partial Quest",
                    NormalizedName = "partial quest",
                    Genre = "RPG",
                    Rating = 7.2,
                    EnrichmentStatus = "Completed",
                    CreatedAt = now,
                    CreatedByUserId = userId
                });

            context.TitleMedia.Add(new TitleMediaEntity
            {
                Id = 1,
                TitleId = 1,
                Type = "Cover",
                FileId = 2,
                SourceId = "test-cover",
                ContentType = "image/jpeg",
                IsPrimary = true,
                CreatedAt = now,
                CreatedByUserId = userId
            });

            context.SourceEntries.AddRange(
                NewSourceEntry(1, "Chrono Trigger (USA)", now, userId),
                NewSourceEntry(2, "Mega Man (USA)", now, userId),
                NewSourceEntry(3, "Sonic the Hedgehog (USA)", now, userId),
                NewSourceEntry(4, "Partial Quest (USA)", now, userId),
                NewSourceEntry(5, "Chrono Trigger (Blocked)", now, userId));

            context.DatGames.AddRange(
                NewDatGame(1, "Chrono Trigger (USA)", now, userId),
                NewDatGame(2, "Mega Man (USA)", now, userId),
                NewDatGame(3, "Sonic the Hedgehog (USA)", now, userId),
                NewDatGame(4, "Partial Quest (USA)", now, userId),
                NewDatGame(5, "Chrono Trigger (Blocked)", now, userId));

            context.RomFiles.Add(new RomFileEntity
            {
                Id = 1,
                OriginalFilename = "chrono-upload.sfc",
                FileId = 3,
                Sha1 = NewSha1(1),
                Md5 = NewMd5(1),
                Crc32 = Crc32.FromUInt32(0x01020304),
                CreatedAt = now,
                CreatedByUserId = userId
            });

            // DAT rom SHA-1s are the catalog's content identity; distinct hashes yield distinct
            // canonical releases (Chrono USA vs the blocked Chrono vs Partial Quest vs Sonic).
            context.DatRoms.AddRange(
                new DatRomEntity
                {
                    Id = 1,
                    DatGameId = 1,
                    Name = "chrono-trigger.sfc",
                    Size = 1,
                    Sha1 = NewSha1(1),
                    RomFileId = 1,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatRomEntity
                {
                    Id = 2,
                    DatGameId = 4,
                    Name = "partial-quest-a.sfc",
                    Size = 1,
                    Sha1 = NewSha1(40),
                    RomFileId = 1,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatRomEntity
                {
                    Id = 3,
                    DatGameId = 4,
                    Name = "partial-quest-b.sfc",
                    Size = 1,
                    Sha1 = NewSha1(41),
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatRomEntity
                {
                    Id = 4,
                    DatGameId = 5,
                    Name = "chrono-trigger-blocked.sfc",
                    Size = 1,
                    Sha1 = NewSha1(5),
                    RomFileId = 1,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatRomEntity
                {
                    Id = 6,
                    DatGameId = 3,
                    Name = "sonic.md",
                    Size = 1,
                    Sha1 = NewSha1(3),
                    CreatedAt = now,
                    CreatedByUserId = userId
                });

            context.TitleSourceLinks.AddRange(
                NewLink(sourceEntryId: 1, titleId: 1, now, userId),
                NewLink(sourceEntryId: 3, titleId: 3, now, userId),
                NewLink(sourceEntryId: 4, titleId: 4, now, userId),
                NewLink(sourceEntryId: 5, titleId: 1, now, userId));

            context.MaterializedLibraryTitles.AddRange(
                NewMaterializedTitle(libraryId, 1, 1, "RPG", isOwned: true, isPlayable: true),
                NewMaterializedTitle(libraryId, 4, 1, "RPG", isOwned: true, isPlayable: false),
                NewMaterializedTitle(otherLibraryId, 3, 3, "Platformer", isOwned: true, isPlayable: true),
                NewMaterializedTitle(invalidLibraryId, 1, 1, "RPG", isOwned: true, isPlayable: true));
            await context.SaveChangesAsync();

            // Build the canonical catalog from the seeded source, then stamp each hand-crafted
            // materialized release with its stable CatalogReleaseId (the public release identity).
            var releaseByGame = await TestHelpers.BuildCatalogAndMapReleasesAsync(context, 1, 3);

            context.MaterializedLibraryReleases.AddRange(
                NewMaterializedRelease(libraryId, 1, 1, 1, releaseByGame[1], isOwned: true, isComplete: true),
                NewMaterializedRelease(libraryId, 1, 5, 1, releaseByGame[5], isOwned: true, isComplete: true, isExposed: false),
                NewMaterializedRelease(libraryId, 4, 4, 1, releaseByGame[4], isOwned: true, isComplete: false),
                NewMaterializedRelease(otherLibraryId, 3, 3, 3, releaseByGame[3], isOwned: true, isComplete: true),
                NewMaterializedRelease(invalidLibraryId, 1, 1, 1, releaseByGame[1], isOwned: true, isComplete: true));

            await context.SaveChangesAsync();

            return new BrowseSeed(libraryId, 1, 1, releaseByGame[1], releaseByGame[5], invalidLibraryId);
        }

        public async Task<AlternateReleaseSeed> AddAlternateChronoReleaseAsync(int libraryId, int titleId)
        {
            await using var context = CreateSetupDbContext();
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();
            const int usaRegionId = 1;
            const int europeRegionId = 2;
            const int alternateReleaseId = 6;

            context.Regions.AddRange(
                new RegionEntity
                {
                    Id = usaRegionId,
                    Name = "USA",
                    SortOrder = 1,
                    IsAutoCreated = false,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new RegionEntity
                {
                    Id = europeRegionId,
                    Name = "Europe",
                    SortOrder = 2,
                    IsAutoCreated = false,
                    CreatedAt = now,
                    CreatedByUserId = userId
                });
            context.SourceEntries.Add(NewSourceEntry(alternateReleaseId, "Chrono Trigger (Europe)", now, userId));
            context.DatGames.Add(NewDatGame(alternateReleaseId, "Chrono Trigger (Europe)", now, userId));
            context.TitleSourceLinks.Add(new TitleSourceLinkEntity
            {
                SourceEntryId = alternateReleaseId,
                TitleId = titleId,
                CreatedAt = now,
                CreatedByUserId = userId
            });
            context.DatRoms.Add(new DatRomEntity
            {
                Id = 5,
                DatGameId = alternateReleaseId,
                Name = "chrono-trigger-europe.sfc",
                Size = 1,
                Sha1 = NewSha1(6),
                RomFileId = 1,
                CreatedAt = now,
                CreatedByUserId = userId
            });
            context.DatGameRegions.AddRange(
                new DatGameRegionEntity
                {
                    DatGameId = 1,
                    RegionId = usaRegionId,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatGameRegionEntity
                {
                    DatGameId = alternateReleaseId,
                    RegionId = europeRegionId,
                    CreatedAt = now,
                    CreatedByUserId = userId
                });
            await context.SaveChangesAsync();

            // Re-project platform 1 (now including the European Chrono and the USA region added
            // above), then materialize the alternate release against its stable CatalogReleaseId.
            var releaseByGame = await TestHelpers.BuildCatalogAndMapReleasesAsync(context, 1);

            context.MaterializedLibraryReleases.Add(
                NewMaterializedRelease(
                    libraryId,
                    titleId,
                    alternateReleaseId,
                    1,
                    releaseByGame[alternateReleaseId],
                    isOwned: true,
                    isComplete: true));

            await context.SaveChangesAsync();

            return new AlternateReleaseSeed(releaseByGame[alternateReleaseId], europeRegionId);
        }

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            await _database.DisposeAsync();

            if (Directory.Exists(_tempDataDirectory))
            {
                Directory.Delete(_tempDataDirectory, true);
            }
        }

        public async Task SeedArtworkAsync(int titleId, int variantFileId = 2)
        {
            await using var context = CreateSetupDbContext();
            context.ArtworkAssets.Add(new ArtworkAssetEntity
            {
                Id = 1, TitleId = titleId, Role = ArtworkRole.Poster, SourceId = "user",
                OriginalFileId = 2, ContentVersion = "original-v1", ContentType = "image/jpeg",
                Width = 600, Height = 900, CreatedAt = DateTimeOffset.UtcNow, IsEligible = true,
                Variants = [new ArtworkVariantEntity
                {
                    Name = "card", FileId = variantFileId, ContentVersion = "v1", ContentType = "image/jpeg",
                    Width = 300, Height = 450
                }]
            });
            await context.SaveChangesAsync();
        }

        private async Task MigrateAsync()
        {
            await using var context = CreateSetupDbContext();
            await context.Database.MigrateAsync();
        }

        public RomdDbContext CreateSetupDbContext()
        {
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .AddInterceptors(new SearchDocumentInterceptor())
                .UseOpenIddict()
                .EnableDetailedErrors()
                .Options;

            return new RomdDbContext(options);
        }
    }

    private static FileEntityPersistence NewFile(int id, Sha256 sha256, DateTimeOffset now, Guid userId) =>
        new()
        {
            Id = id,
            Sha256 = sha256,
            Size = 1,
            SizeOnDisk = 1,
            IsCompressed = false,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static DatGameEntity NewDatGame(int id, string name, DateTimeOffset now, Guid userId) =>
        new()
        {
            Id = id,
            DatFileId = 1,
            SourceEntryId = id,
            Name = name,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static SourceEntryEntity NewSourceEntry(int id, string name, DateTimeOffset now, Guid userId) =>
        new()
        {
            Id = id,
            CatalogSourceId = 1,
            EntryKey = name,
            Name = name,
            PlatformId = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static MaterializedLibraryTitleEntity NewMaterializedTitle(
        int libraryId,
        int titleId,
        int platformId,
        string genre,
        bool isOwned,
        bool isPlayable) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            PlatformId = platformId,
            Genre = genre,
            IsVisible = true,
            IsOwned = isOwned,
            IsPlayable = isPlayable,
            EligibleReleaseCount = 1,
            PlayableReleaseCount = isPlayable ? 1 : 0,
            ExposedReleaseCount = 1,
            Availability = isPlayable
                ? LibraryTitleAvailability.Playable.ToString()
                : LibraryTitleAvailability.Partial.ToString()
        };

    private static TitleSourceLinkEntity NewLink(int sourceEntryId, int titleId, DateTimeOffset now, Guid userId) =>
        new()
        {
            SourceEntryId = sourceEntryId,
            TitleId = titleId,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static MaterializedLibraryReleaseEntity NewMaterializedRelease(
        int libraryId,
        int titleId,
        int datGameId,
        int platformId,
        int catalogReleaseId,
        bool isOwned,
        bool isComplete,
        bool isExposed = true) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            CatalogReleaseId = catalogReleaseId,
            DatGameId = datGameId,
            DatFileId = 1,
            PlatformId = platformId,
            IsEligible = isExposed,
            IsComplete = isComplete,
            IsOwned = isOwned,
            IsPlayable = isExposed && isOwned && isComplete,
            IsBlocked = !isExposed,
            BlockReason = isExposed ? null : "ExcludedDat",
            IsExposed = isExposed,
            ExposureReason = isExposed ? "ExposedDefault" : "ExcludedDat"
        };

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromBytes(bytes);
    }

    private static Sha1 NewSha1(byte firstByte)
    {
        var bytes = new byte[Sha1.ByteLength];
        bytes[0] = firstByte;
        return Sha1.FromSpan(bytes);
    }

    private static Md5 NewMd5(byte firstByte)
    {
        var bytes = new byte[Md5.ByteLength];
        bytes[0] = firstByte;
        return Md5.FromSpan(bytes);
    }
}
