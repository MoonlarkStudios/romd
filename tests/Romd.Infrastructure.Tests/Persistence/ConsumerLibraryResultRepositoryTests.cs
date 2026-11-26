using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Search;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ConsumerLibraryResultRepositoryTests : IAsyncLifetime
{
    private static readonly Guid UserId = Guid.Parse("d1321926-8770-4238-b7e4-33fa453a3709");
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _options;
    private readonly int _libraryId;

    public ConsumerLibraryResultRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .UseOpenIddict()
            .Options;

        using var context = CreateContext();
        var library = LibraryEntity.FromDomain(Library.CreateNew("Empty Library", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch));
        library.NeedsMaterialization = false;
        context.Libraries.Add(library);
        context.SaveChanges();
        _libraryId = library.Id;
        context.Users.Add(new RomdUser
        {
            Id = UserId,
            UserName = "empty-library-user",
            NormalizedUserName = "EMPTY-LIBRARY-USER",
            Email = "empty-library@example.test",
            NormalizedEmail = "EMPTY-LIBRARY@EXAMPLE.TEST",
            LibraryId = library.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task ListAndSearch_ValidEmptyLibrary_ReturnFoundWithEmptyValues()
    {
        await using var context = CreateContext();
        var repository = new ConsumerBrowseRepository(context, new ConsumerReleaseSelector());

        var platforms = await repository.ListPlatformsAsync(new ConsumerLibraryScope(UserId), null, 20);
        var search = await repository.SearchCatalogAsync(
            new ConsumerLibraryScope(UserId),
            new ConsumerCatalogFilters(null, null, null, ConsumerCompletenessFilter.All, ConsumerTitleSortField.Name),
            ConsumerReleasePreference.Default,
            null,
            20);

        var platformFound = platforms
            .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>.Found>();
        platformFound.LibraryId.ShouldBe(_libraryId);
        platformFound.Value.Items.ShouldBeEmpty();
        var searchFound = search
            .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Found>();
        searchFound.LibraryId.ShouldBe(_libraryId);
        searchFound.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Platforms_ResolveSystemPresentationIndependentlyOfGameMedia()
    {
        await SeedCompletenessScenariosAsync();
        await using var context = CreateContext();
        var defaultHash = new string('a', 64);
        var customHash = new string('b', 64);
        context.Files.AddRange(
            new FileEntityPersistence { Id = 1, Sha256 = Sha256.Parse(defaultHash), Size = 1, SizeOnDisk = 1 },
            new FileEntityPersistence { Id = 2, Sha256 = Sha256.Parse(customHash), Size = 1, SizeOnDisk = 1 });
        context.ReferenceAssets.AddRange(
            new ReferenceAssetEntity { Hash = defaultHash, ContentType = "image/png", FileId = 1 },
            new ReferenceAssetEntity { Hash = customHash, ContentType = "image/webp", FileId = 2 });
        var platform = await context.Platforms.AsTracking().SingleAsync();
        platform.BaseConsoleArtworkHash = defaultHash;
        platform.BaseControllerArtworkHash = customHash;
        platform.BaseAssetHash = defaultHash;
        platform.BaseDescription = "A cartridge console.";
        platform.CanonicalKey = "snes";
        await context.SaveChangesAsync();
        var repository = new ConsumerBrowseRepository(context, new ConsumerReleaseSelector());
        var scope = new ConsumerLibraryScope(UserId);

        var listed = (await repository.ListPlatformsAsync(scope, null, 20))
            .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>.Found>();
        var summary = listed.Value.Items.ShouldHaveSingleItem();
        summary.ConsoleArtwork!.Sha256.ShouldBe(defaultHash);
        summary.Icon!.Sha256.ShouldBe(defaultHash);
        summary.CoverMediaId.ShouldBeNull();

        platform.ConsoleArtworkOverrideHash = customHash;
        platform.ControllerArtworkOverrideHash = defaultHash;
        platform.HasArtworkOverride = true;
        await context.SaveChangesAsync();
        var selected = (await repository.GetPlatformAsync(scope, platform.Id))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerPlatformDetailData>.Found>();
        selected.Value.ConsoleArtwork!.Sha256.ShouldBe(customHash);
        selected.Value.ConsoleArtwork.Url.ShouldBe($"/api/assets/{customHash}");
        selected.Value.ConsoleArtwork.ContentType.ShouldBe("image/webp");
        selected.Value.ControllerArtwork!.Sha256.ShouldBe(defaultHash);
        selected.Value.Icon.ShouldBeNull();
        selected.Value.Description.ShouldBe("A cartridge console.");
        selected.Value.Hardware!.Category.ShouldBe("console");
        selected.Value.Hardware.Generation.ShouldBe(4);
        selected.Value.Hardware.ReleaseYear.ShouldBe(1990);
        selected.Value.Media.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchCatalogAsync_CompletenessFilters_RequireExposedReleasesAndClassifyMixedReleasesAsPartial()
    {
        await SeedCompletenessScenariosAsync();
        await using var context = CreateContext();
        var repository = new ConsumerBrowseRepository(context, new ConsumerReleaseSelector());

        var all = await SearchByCompletenessAsync(repository, ConsumerCompletenessFilter.All);
        var complete = await SearchByCompletenessAsync(repository, ConsumerCompletenessFilter.Complete);
        var partial = await SearchByCompletenessAsync(repository, ConsumerCompletenessFilter.Partial);

        all.Items.Select(title => title.Name).ShouldBe([
            "All Complete",
            "Mixed Releases",
            "No Exposed Releases"
        ]);
        complete.Items.Select(title => title.Name).ShouldBe(["All Complete"]);
        partial.Items.Select(title => title.Name).ShouldBe(["Mixed Releases"]);
    }

    [Fact]
    public async Task SearchCatalogAsync_ReturnsPlayersAndEffectiveEsrbRating()
    {
        await SeedCompletenessScenariosAsync();
        await using var context = CreateContext();
        var title = await context.Titles.AsTracking().SingleAsync(item => item.Name == "All Complete");
        title.Players = 2;
        context.TitleContentRatings.Add(new TitleContentRatingEntity
        {
            TitleId = title.Id,
            Board = (int)Romd.Domain.Catalog.Ratings.RatingBoard.Esrb,
            Code = "E",
            SourceId = "user"
        });
        await context.SaveChangesAsync();
        var repository = new ConsumerBrowseRepository(context, new ConsumerReleaseSelector());
        var result = await SearchByCompletenessAsync(repository, ConsumerCompletenessFilter.All);
        var rated = result.Items.Single(item => item.Name == "All Complete");
        rated.Players.ShouldBe(2);
        rated.EsrbRating.ShouldBe("E");
        rated.System.Key.ShouldBe((await context.Platforms.SingleAsync(item => item.Id == rated.PlatformId)).ShortName);
        rated.ContentRatings.ShouldHaveSingleItem().Code.ShouldBe("E");
        rated.ContentRatings[0].Board.ShouldBe(Romd.Domain.Catalog.Ratings.RatingBoard.Esrb);
        var detail = (await repository.GetTitleAsync(new ConsumerLibraryScope(UserId), title.Id, ConsumerReleasePreference.Default))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerTitleDetailData>.Found>().Value;
        detail.System.Key.ShouldBe(rated.System.Key);
        detail.ContentRatings.ShouldBe(rated.ContentRatings);
        result.Items.Single(item => item.Name == "Mixed Releases").EsrbRating.ShouldBeNull();
    }

    [Fact]
    public async Task SearchCatalogAsync_DatAliasUsesItsOwnCatalogSourceStatus()
    {
        await SeedCompletenessScenariosAsync();
        await using var context = CreateContext();
        const int datSourceId = 1;
        const int catalogSourceId = 2;
        const string alias = "Crimson Road (USA)";
        context.CatalogSources.AddRange(
            new CatalogSourceEntity { Id = datSourceId, Kind = nameof(CatalogSourceKind.Manual), Status = nameof(CatalogSourceStatus.Disabled) },
            new CatalogSourceEntity { Id = catalogSourceId, Kind = nameof(CatalogSourceKind.Dat), Status = nameof(CatalogSourceStatus.Active) });
        context.DatSources.Add(new DatSourceEntity { Id = datSourceId, CatalogSourceId = catalogSourceId });
        context.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = Sha256.Parse(new string('a', 64)), Size = 1, SizeOnDisk = 1 });
        context.DatFiles.Add(new DatFileEntity
        {
            Id = 1,
            DatSourceId = datSourceId,
            FileId = 1,
            PlatformId = 1,
            Name = "Regional releases",
            Description = "Regional releases",
            OriginalFilename = "regional.dat",
            Type = nameof(DatType.Custom)
        });
        context.SourceEntries.Add(new SourceEntryEntity { Id = 1, CatalogSourceId = catalogSourceId, PlatformId = 1, EntryKey = "regional", Name = alias });
        context.DatGames.Add(new DatGameEntity { Id = 1, DatFileId = 1, SourceEntryId = 1, Name = alias, SearchDocument = SearchDocuments.For(alias, null) });
        context.CatalogReleases.Add(new CatalogReleaseEntity { Id = 1, PlatformId = 1, CatalogTitleId = 1, Name = alias, Fingerprint = "regional" });
        context.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity { CatalogReleaseId = 1, SourceEntryId = 1, ProviderClaimKey = "regional" });
        await context.SaveChangesAsync();
        await context.MaterializedLibraryReleases.Where(release => release.LibraryId == _libraryId && release.DatGameId == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(release => release.CatalogReleaseId, 1));

        var repository = new ConsumerBrowseRepository(context, new ConsumerReleaseSelector());
        var filters = new ConsumerCatalogFilters("Crimson Road", null, null, ConsumerCompletenessFilter.All, ConsumerTitleSortField.Name);
        (await SearchAsync()).Items.ShouldHaveSingleItem().Name.ShouldBe("All Complete");

        await context.CatalogSources.ExecuteUpdateAsync(update => update.SetProperty(source => source.Status,
            source => source.Id == catalogSourceId ? nameof(CatalogSourceStatus.Disabled) : nameof(CatalogSourceStatus.Active)));
        (await SearchAsync()).Items.ShouldBeEmpty();

        async Task<PagedList<ConsumerTitleCardData>> SearchAsync() =>
            (await repository.SearchCatalogAsync(new ConsumerLibraryScope(UserId), filters, ConsumerReleasePreference.Default, null, 20))
            .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Found>().Value;
    }

    [Fact]
    public async Task BrowseDetails_AbsentItems_ReturnAuthoritativeItemNotFound()
    {
        await using var context = CreateContext();
        var repository = new ConsumerBrowseRepository(context, new ConsumerReleaseSelector());

        var platform = await repository.GetPlatformAsync(new ConsumerLibraryScope(UserId), 999);
        var title = await repository.GetTitleAsync(
            new ConsumerLibraryScope(UserId),
            999,
            ConsumerReleasePreference.Default);

        platform.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerPlatformDetailData>.ItemNotFound>()
            .LibraryId.ShouldBe(_libraryId);
        title.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerTitleDetailData>.ItemNotFound>()
            .LibraryId.ShouldBe(_libraryId);
    }

    [Fact]
    public async Task Collections_EmptyListIsFoundAndAbsentDetailsAreItemNotFound()
    {
        await using var context = CreateContext();
        var repository = new CollectionRepository(context, new ConsumerReleaseSelector());

        var list = await repository.GetCollectionsPageAsync(new ConsumerLibraryScope(UserId), null, null, 100);
        var detail = await repository.GetCollectionByIdAsync(new ConsumerLibraryScope(UserId), 999);
        var titles = await repository.GetCollectionTitlesPageAsync(
            new ConsumerLibraryScope(UserId),
            999,
            null,
            ConsumerReleasePreference.Default,
            20);

        var found = list
            .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerCollectionReadModel>>.Found>();
        found.LibraryId.ShouldBe(_libraryId);
        found.Value.Items.ShouldBeEmpty();
        detail.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerCollectionReadModel>.ItemNotFound>()
            .LibraryId.ShouldBe(_libraryId);
        titles.ShouldBeOfType<
                ConsumerLibraryReadResult<PagedList<ConsumerCollectionTitleReadModel>>.ItemNotFound>()
            .LibraryId.ShouldBe(_libraryId);
    }

    [Fact]
    public async Task Bios_AbsentPlatform_ReturnsAuthoritativeItemNotFound()
    {
        await using var context = CreateContext();
        var repository = new ConsumerBiosRepository(context);

        var result = await repository.GetPlatformBiosAsync(
            new ConsumerPlatformBiosRequest(new ConsumerLibraryScope(UserId), int.MaxValue));

        result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerPlatformBios>.ItemNotFound>()
            .LibraryId.ShouldBe(_libraryId);
    }

    [Fact]
    public async Task AnyConsumerRead_CorruptLiveLibrary_ReturnsLibraryUnavailable()
    {
        await using var context = CreateContext();
        await context.Libraries
            .Where(library => library.Id == _libraryId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.ConfigurationState, LibraryConfigurationState.Valid.ToString())
                .SetProperty(library => library.ConfigurationJson, "{"));
        var repository = new ConsumerBrowseRepository(context, new ConsumerReleaseSelector());

        var result = await repository.ListPlatformsAsync(new ConsumerLibraryScope(UserId), null, 20);

        result.ShouldBeOfType<
            ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>.LibraryUnavailable>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private RomdDbContext CreateContext() => new(_options);

    private async Task<PagedList<ConsumerTitleCardData>> SearchByCompletenessAsync(
        ConsumerBrowseRepository repository,
        ConsumerCompletenessFilter completeness)
    {
        var result = await repository.SearchCatalogAsync(
            new ConsumerLibraryScope(UserId),
            new ConsumerCatalogFilters(null, null, null, completeness, ConsumerTitleSortField.Name),
            ConsumerReleasePreference.Default,
            null,
            20);

        return result
            .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Found>()
            .Value;
    }

    private async Task SeedCompletenessScenariosAsync()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        const int platformId = 1;
        const int allCompleteTitleId = 1;
        const int mixedTitleId = 2;
        const int noExposedTitleId = 3;

        context.Platforms.Add(new PlatformEntity
        {
            Id = platformId,
            Name = "Completeness Test Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Completeness Test Platform",
            BaseCompactLabel = "Completeness Test Platform",
            CanonicalKey = "completeness-test",
            ShortName = "completeness-test",
            CreatedAt = now
        });
        context.Titles.AddRange(
            NewTitle(allCompleteTitleId, platformId, "All Complete", now),
            NewTitle(mixedTitleId, platformId, "Mixed Releases", now),
            NewTitle(noExposedTitleId, platformId, "No Exposed Releases", now));
        context.MaterializedLibraryTitles.AddRange(
            NewOwnedTitle(allCompleteTitleId, platformId, exposedReleaseCount: 2),
            NewOwnedTitle(mixedTitleId, platformId, exposedReleaseCount: 2),
            NewOwnedTitle(noExposedTitleId, platformId, exposedReleaseCount: 0));
        context.MaterializedLibraryReleases.AddRange(
            NewRelease(allCompleteTitleId, platformId, datGameId: 1, isComplete: true),
            NewRelease(allCompleteTitleId, platformId, datGameId: 2, isComplete: true),
            NewRelease(mixedTitleId, platformId, datGameId: 3, isComplete: true),
            NewRelease(mixedTitleId, platformId, datGameId: 4, isComplete: false),
            NewRelease(
                noExposedTitleId,
                platformId,
                datGameId: 5,
                isComplete: true,
                isExposed: false));

        await context.SaveChangesAsync();
    }

    private static TitleEntity NewTitle(int id, int platformId, string name, DateTimeOffset now) =>
        new()
        {
            Id = id,
            PlatformId = platformId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = now
        };

    private MaterializedLibraryTitleEntity NewOwnedTitle(
        int titleId,
        int platformId,
        int exposedReleaseCount) =>
        new()
        {
            LibraryId = _libraryId,
            TitleId = titleId,
            PlatformId = platformId,
            IsVisible = true,
            IsOwned = true,
            IsPlayable = exposedReleaseCount > 0,
            EligibleReleaseCount = exposedReleaseCount,
            PlayableReleaseCount = exposedReleaseCount,
            ExposedReleaseCount = exposedReleaseCount,
            Availability = LibraryTitleAvailability.Playable.ToString()
        };

    private MaterializedLibraryReleaseEntity NewRelease(
        int titleId,
        int platformId,
        int datGameId,
        bool isComplete,
        bool isExposed = true) =>
        new()
        {
            LibraryId = _libraryId,
            TitleId = titleId,
            DatGameId = datGameId,
            DatFileId = datGameId,
            PlatformId = platformId,
            IsEligible = isExposed,
            IsComplete = isComplete,
            IsOwned = true,
            IsPlayable = isExposed && isComplete,
            IsExposed = isExposed,
            ExposureReason = isExposed ? "CompletenessTest" : "NotEligible"
        };
}
