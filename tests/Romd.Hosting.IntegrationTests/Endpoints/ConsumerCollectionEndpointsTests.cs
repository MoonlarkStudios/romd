using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Romd.Contracts.Consumer.Collections;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence.Search;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerCollectionEndpointsTests
{
    private const string TestJwtAudience = "RomdConsumerCollectionEndpointTests";
    private const string TestJwtIssuer = "RomdConsumerCollectionEndpointTests";
    private const string TestJwtSecret = "consumer-collection-endpoint-test-secret-at-least-32-characters";

    [Fact]
    public async Task ListCollections_AuthenticatedUserWithLibrary_ReturnsVisibleConsumerCollections()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var scenario = await fixture.CreateCollectionScenarioAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", scenario.LibraryId);

        var response = await client.GetAsync("/api/collections");
        var body = await response.Content.ReadFromJsonAsync<IReadOnlyList<ConsumerCollectionDto>>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Count.ShouldBe(1);
        body[0].Id.ShouldBe(IdCoder.Encode(scenario.CollectionId));
        body[0].Name.ShouldBe("Saturday Picks");
        body[0].System!.Key.ShouldBe(scenario.SystemKey);
        body[0].System!.Name.ShouldBe("Nintendo Entertainment System");
        body[0].ItemCount.ShouldBe(2);
    }

    [Fact]
    public async Task ListCollections_UnresolvedSystem_DoesNotReturnUnscopedCollections()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var scenario = await fixture.CreateCollectionScenarioAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", scenario.LibraryId);

        var response = await client.GetAsync("/api/collections?systemKey=local-unregistered");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCollection_AuthenticatedUserWithLibrary_ReturnsConsumerCollection()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var scenario = await fixture.CreateCollectionScenarioAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", scenario.LibraryId);

        var response = await client.GetAsync($"/api/collections/{IdCoder.Encode(scenario.CollectionId)}");
        var body = await response.Content.ReadFromJsonAsync<ConsumerCollectionDto>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("Saturday Picks");
        body.Description.ShouldBe("Consumer-safe collection description");
        body.ItemCount.ShouldBe(2);
    }

    [Fact]
    public async Task ListCollectionTitles_AuthenticatedUserWithLibrary_ReturnsPagedOwnedTitlesOnly()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var scenario = await fixture.CreateCollectionScenarioAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", scenario.LibraryId);

        var firstResponse = await client.GetAsync($"/api/collections/{IdCoder.Encode(scenario.CollectionId)}/titles?limit=1");
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<Page<ConsumerCollectionTitleDto>>();

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstPage.ShouldNotBeNull();
        firstPage.Items.Count.ShouldBe(1);
        firstPage.Items[0].Name.ShouldBe("Alpha Quest");
        firstPage.Items[0].ReleaseCount.ShouldBe(1);
        firstPage.Items[0].DefaultReleaseId.ShouldNotBeNull();
        firstPage.HasNextPage.ShouldBeTrue();
        firstPage.NextCursor.ShouldNotBeNull();
        string nextCursor = firstPage.NextCursor!;

        var secondResponse = await client.GetAsync(
            $"/api/collections/{IdCoder.Encode(scenario.CollectionId)}/titles?limit=1&cursor={Uri.EscapeDataString(nextCursor)}");
        var secondPage = await secondResponse.Content.ReadFromJsonAsync<Page<ConsumerCollectionTitleDto>>();

        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondPage.ShouldNotBeNull();
        secondPage.Items.Count.ShouldBe(1);
        secondPage.Items[0].Name.ShouldBe("Beta Quest");
        secondPage.Items.ShouldNotContain(title => title.Name == "Hidden Quest");
        secondPage.HasNextPage.ShouldBeFalse();
        secondPage.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData("/api/collections")]
    [InlineData("/api/collections/abc")]
    [InlineData("/api/collections/abc/titles")]
    public async Task ConsumerCollectionEndpoints_UnauthenticatedRequest_ReturnsUnauthorized(string path)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListCollections_AuthenticatedUserWithoutLibrary_ReturnsNotFound()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost");

        var response = await client.GetAsync("/api/collections");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCollection_CollectionOutsideAmbientLibrary_ReturnsNotFound()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var scenario = await fixture.CreateCollectionScenarioAsync();
        int otherLibraryId = await fixture.CreateLibraryAsync("Bedroom");
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", otherLibraryId);

        var response = await client.GetAsync($"/api/collections/{IdCoder.Encode(scenario.CollectionId)}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("POST", "/api/collections")]
    [InlineData("PUT", "/api/collections/{0}")]
    [InlineData("DELETE", "/api/collections/{0}")]
    public async Task ConsumerCollectionWriteRoutes_AuthenticatedUser_ReturnMethodNotAllowed(
        string method,
        string routeTemplate)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var scenario = await fixture.CreateCollectionScenarioAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", scenario.LibraryId);
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            string.Format(routeTemplate, IdCoder.Encode(scenario.CollectionId)))
        {
            Content = JsonContent.Create(new { name = "Changed" })
        };

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task ConsumerCollectionResponses_DoNotExposeForbiddenManagementFields()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var scenario = await fixture.CreateCollectionScenarioAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", scenario.LibraryId);
        string collectionId = IdCoder.Encode(scenario.CollectionId);

        var responses = new[]
        {
            await client.GetStringAsync("/api/collections"),
            await client.GetStringAsync($"/api/collections/{collectionId}"),
            await client.GetStringAsync($"/api/collections/{collectionId}/titles")
        };

        var forbiddenTerms = new[]
        {
            "configuration",
            "curation",
            "datId",
            "datIds",
            "dump",
            "fileId",
            "hash",
            "materialization",
            "md5",
            "note",
            "provenance",
            "savedFilter",
            "sha",
            "state"
        };

        responses
            .SelectMany(ReadJsonPropertyNames)
            .Where(propertyName => forbiddenTerms.Any(term =>
                propertyName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ShouldBeEmpty();
    }

    private static IEnumerable<string> ReadJsonPropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);

        return ReadJsonPropertyNames(document.RootElement).ToList();
    }

    private static IEnumerable<string> ReadJsonPropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;

                foreach (string childName in ReadJsonPropertyNames(property.Value))
                {
                    yield return childName;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (string childName in ReadJsonPropertyNames(item))
                {
                    yield return childName;
                }
            }
        }
    }

    private sealed record CollectionScenario(int LibraryId, int PlatformId, int CollectionId, string SystemKey);

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

            string tempDataDirectory = Path.Combine(
                Path.GetTempPath(),
                $"romd-consumer-collection-endpoints-{Guid.NewGuid():N}");
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

        public async Task<int> CreateLibraryAsync(string name)
        {
            await using var context = CreateSetupDbContext();
            var library = Library.CreateNew(name, new LibraryConfiguration());
            var entity = LibraryEntity.FromDomain(library);
            entity.NeedsMaterialization = false;
            context.Libraries.Add(entity);
            await context.SaveChangesAsync();

            return entity.Id;
        }

        public async Task<CollectionScenario> CreateCollectionScenarioAsync()
        {
            await using var context = CreateSetupDbContext();
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();

            var library = LibraryEntity.FromDomain(Library.CreateNew("Living Room", new LibraryConfiguration()));
            library.NeedsMaterialization = false;
            context.Libraries.Add(library);

            var file = new FileEntityPersistence
            {
                Sha256 = NewSha256(1),
                Size = 1,
                SizeOnDisk = 1,
                IsCompressed = false,
                CreatedAt = now,
                CreatedByUserId = userId
            };
            context.Files.Add(file);

            var platform = new PlatformEntity
            {
                Name = "Nintendo Entertainment System",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation, BaseName = "Nintendo Entertainment System", BaseCompactLabel = "Nintendo Entertainment System", CanonicalKey = "local-collection-nes",
                ShortName = $"nes-{Guid.NewGuid():N}",
                Manufacturer = "Nintendo",
                CreatedAt = now,
                CreatedByUserId = userId
            };
            context.Platforms.Add(platform);

            await context.SaveChangesAsync();

            var datFile = new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    CatalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Active" }
                },
                Name = "No-Intro NES",
                Description = "NES DAT",
                Type = "NoIntro",
                PlatformId = platform.Id,
                OriginalFilename = "nes.dat",
                FileId = file.Id,
                GameCount = 3,
                RomCount = 3,
                DiskCount = 0,
                CreatedAt = now,
                CreatedByUserId = userId
            };
            context.DatFiles.Add(datFile);

            var titles = new[]
            {
                NewTitle(platform.Id, "Alpha Quest", "Action", new DateOnly(1990, 1, 1), 87, now, userId),
                NewTitle(platform.Id, "Beta Quest", "Adventure", new DateOnly(1991, 1, 1), 81, now, userId),
                NewTitle(platform.Id, "Hidden Quest", "Puzzle", new DateOnly(1992, 1, 1), 76, now, userId)
            };
            context.Titles.AddRange(titles);

            await context.SaveChangesAsync();

            var sourceEntries = titles
                .Select(title => new SourceEntryEntity
                {
                    CatalogSourceId = datFile.Source!.CatalogSourceId,
                    EntryKey = title.Name,
                    Name = title.Name,
                    PlatformId = platform.Id,
                    CreatedAt = now,
                    CreatedByUserId = userId
                })
                .ToList();
            context.SourceEntries.AddRange(sourceEntries);
            await context.SaveChangesAsync();

            var datGames = titles
                .Select((title, index) => new DatGameEntity
                {
                    DatFileId = datFile.Id,
                    SourceEntryId = sourceEntries[index].Id,
                    Name = title.Name,
                    CreatedAt = now,
                    CreatedByUserId = userId
                })
                .ToList();
            context.DatGames.AddRange(datGames);

            var collection = new CollectionEntity
            {
                Name = "Saturday Picks",
                Description = "Consumer-safe collection description",
                PlatformId = platform.Id,
                IsSystem = false,
                SortOrder = 10,
                CreatedAt = now,
                CreatedByUserId = userId
            };
            context.Collections.Add(collection);

            await context.SaveChangesAsync();

            context.LibraryCollections.Add(new LibraryCollectionEntity { LibraryId = library.Id, CollectionId = collection.Id, IsFeatured = true });
            context.CollectionItems.AddRange(
                NewCollectionItem(collection.Id, titles[0].Id, 10, "private note", now, userId),
                NewCollectionItem(collection.Id, titles[1].Id, 20, null, now, userId),
                NewCollectionItem(collection.Id, titles[2].Id, 30, "should stay hidden", now, userId));

            var romFiles = new[]
            {
                NewRomFile("alpha-upload.nes", file.Id, NewSha1(2), NewMd5(2), now, userId),
                NewRomFile("beta-upload.nes", file.Id, NewSha1(3), NewMd5(3), now, userId)
            };
            context.RomFiles.AddRange(romFiles);
            await context.SaveChangesAsync();

            // DAT rom hashes match their owned RomFiles so the catalog projection produces a
            // resolvable release per collected, owned title.
            context.DatRoms.AddRange(
                NewDatRom(datGames[0].Id, "alpha.nes", romFiles[0].Id, NewSha1(2), now, userId),
                NewDatRom(datGames[1].Id, "beta.nes", romFiles[1].Id, NewSha1(3), now, userId));
            context.TitleSourceLinks.AddRange(
                NewLink(sourceEntries[0].Id, titles[0].Id, now, userId),
                NewLink(sourceEntries[1].Id, titles[1].Id, now, userId));

            context.MaterializedLibraryTitles.AddRange(
                NewMaterializedLibraryTitle(library.Id, titles[0].Id, platform.Id, "Action"),
                NewMaterializedLibraryTitle(library.Id, titles[1].Id, platform.Id, "Adventure"));

            await context.SaveChangesAsync();

            var releaseByGame = await TestHelpers.BuildCatalogAndMapReleasesAsync(context, platform.Id);

            context.MaterializedLibraryReleases.AddRange(
                NewMaterializedLibraryRelease(
                    library.Id,
                    titles[0].Id,
                    datGames[0].Id,
                    datFile.Id,
                    platform.Id,
                    releaseByGame[datGames[0].Id]),
                NewMaterializedLibraryRelease(
                    library.Id,
                    titles[1].Id,
                    datGames[1].Id,
                    datFile.Id,
                    platform.Id,
                    releaseByGame[datGames[1].Id]));

            await context.SaveChangesAsync();

            return new CollectionScenario(library.Id, platform.Id, collection.Id, platform.CanonicalKey!);
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

        private async Task MigrateAsync()
        {
            await using var context = CreateSetupDbContext();
            await context.Database.MigrateAsync();
        }

        private RomdDbContext CreateSetupDbContext()
        {
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .AddInterceptors(new SearchDocumentInterceptor())
                .UseOpenIddict()
                .EnableDetailedErrors()
                .Options;

            return new RomdDbContext(options);
        }

        private static TitleEntity NewTitle(
            int platformId,
            string name,
            string genre,
            DateOnly releaseDate,
            double rating,
            DateTimeOffset now,
            Guid userId) =>
            new()
            {
                PlatformId = platformId,
                Name = name,
                NormalizedName = name.ToLowerInvariant(),
                Genre = genre,
                ReleaseDate = releaseDate,
                Rating = rating,
                EnrichmentStatus = "None",
                CreatedAt = now,
                CreatedByUserId = userId
            };

        private static CollectionItemEntity NewCollectionItem(
            int collectionId,
            int titleId,
            int sortOrder,
            string? note,
            DateTimeOffset now,
            Guid userId) =>
            new()
            {
                CollectionId = collectionId,
                TitleId = titleId,
                SortOrder = sortOrder,
                Note = note,
                AddedAt = now,
                CreatedAt = now,
                CreatedByUserId = userId
            };

        private static MaterializedLibraryTitleEntity NewMaterializedLibraryTitle(
            int libraryId,
            int titleId,
            int platformId,
            string genre) =>
            new()
            {
                LibraryId = libraryId,
                TitleId = titleId,
                PlatformId = platformId,
                Genre = genre,
                IsVisible = true,
                IsOwned = true,
                IsPlayable = true,
                EligibleReleaseCount = 1,
                PlayableReleaseCount = 1,
                ExposedReleaseCount = 1,
                Availability = LibraryTitleAvailability.Playable.ToString()
            };

        private static TitleSourceLinkEntity NewLink(int sourceEntryId, int titleId, DateTimeOffset now, Guid userId) =>
            new()
            {
                SourceEntryId = sourceEntryId,
                TitleId = titleId,
                CreatedAt = now,
                CreatedByUserId = userId
            };

        private static MaterializedLibraryReleaseEntity NewMaterializedLibraryRelease(
            int libraryId,
            int titleId,
            int datGameId,
            int datFileId,
            int platformId,
            int catalogReleaseId) =>
            new()
            {
                LibraryId = libraryId,
                TitleId = titleId,
                CatalogReleaseId = catalogReleaseId,
                DatGameId = datGameId,
                DatFileId = datFileId,
                PlatformId = platformId,
                IsEligible = true,
                IsComplete = true,
                IsOwned = true,
                IsPlayable = true,
                IsBlocked = false,
                BlockReason = null,
                IsExposed = true,
                ExposureReason = "ExposedDefault"
            };

        private static RomFileEntity NewRomFile(
            string originalFilename,
            int fileId,
            Sha1 sha1,
            Md5 md5,
            DateTimeOffset now,
            Guid userId) =>
            new()
            {
                OriginalFilename = originalFilename,
                FileId = fileId,
                Sha1 = sha1,
                Md5 = md5,
                Crc32 = Crc32.FromUInt32((uint)fileId),
                CreatedAt = now,
                CreatedByUserId = userId
            };

        private static DatRomEntity NewDatRom(
            int datGameId,
            string name,
            int romFileId,
            Sha1 sha1,
            DateTimeOffset now,
            Guid userId) =>
            new()
            {
                DatGameId = datGameId,
                Name = name,
                Size = 1,
                Sha1 = sha1,
                RomFileId = romFileId,
                CreatedAt = now,
                CreatedByUserId = userId
            };

        private static Sha256 NewSha256(byte firstByte)
        {
            var bytes = new byte[32];
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
}
