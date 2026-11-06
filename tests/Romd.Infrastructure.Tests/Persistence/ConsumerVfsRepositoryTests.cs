using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Romd.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Vfs;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;
using Romd.Domain.Libraries;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class ConsumerVfsRepositoryTests : IDisposable
{
    private static readonly RomDeliveryPolicy DeliveryPolicy = RomMediaPolicyComposition.CreateDeliveryPolicy();

    [Theory]
    [InlineData("gc")]
    [InlineData("wii")]
    public async Task GetReleaseAsync_StoredVerifiedGcWiiRvz_ProjectsDeliveredArtifactPathAndMetadata(string systemKey)
    {
        SeedReleaseFiles(("Disc/game.iso", true));
        await using var db = CreateDb();
        await db.Platforms.Where(platform => platform.Id == PlatformId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(platform => platform.ShortName, systemKey)
                .SetProperty(platform => platform.CanonicalKey, systemKey));
        await AddRvzAsync(db, 1, 2, systemKey == "wii" ? 2u : 1u);

        var release = (await new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value;

        var entry = release.Tree.List("Disc").ShouldNotBeNull().ShouldHaveSingleItem();
        entry.RelativePath.ShouldBe("Disc/game.rvz");
        entry.File.ShouldNotBeNull().Sha256.ShouldBe(NewSha256(2));
        entry.File.Length.ShouldBe(20);
        entry.File.Md5.ShouldBe(NewMd5(2));
        entry.File.Sha1.ShouldBe(NewSha1(2));
        release.Tree.Find("Disc/game.iso").ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredVfs_AdmittedRvzWithContradictoryInspection_KeepsOriginalListingAndExactPath(bool preferRvz)
    {
        SeedReleaseFilesWithSize(8192, ("Disc/game.iso", true));
        await using var db = CreateDb();
        await SetSystemKeyAsync(db, "gc");
        await AddRvzAsync(db, 1, 20, 1u, preferred: preferRvz);
        var catalog = MediaTestServices.CreateContentCatalog(db);
        var content = (await catalog.FindByRomIdAsync(1)).ShouldNotBeNull();
        byte[] prefix = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(24), 0x5d1c9ea3);
        var observation = new RomLogicalInspection(Guid.NewGuid(), content.Identity, "vfs-conflict/v1", "fixture-inspector",
            DateTimeOffset.UtcNow, prefix, new RomNintendoLayoutFacts(1, 1, 0));
        var source = content.Preferred;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await MediaTestServices.CreateObservationStore(db, TimeProvider.System).StageAsync(content.RomId,
                new(source.RepresentationId, source.OutputId, source.FileId, source.Facts.Verifications.Single().Id), observation)).IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        content = (await catalog.FindByRomIdAsync(1)).ShouldNotBeNull();
        var resolution = MediaKindResolver.Resolve(DeliveryPolicy.Kinds, content, ["gc"]);
        resolution.Outcome.ShouldBe(MediaKindOutcome.Fallback);
        var admission = new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null);
        var options = RomDeliveryPlanner.Plan(content, "Disc/game.iso", DeliveryPolicy, resolution, admission);
        options.ShouldContain(option => option.Format == RomDeliveryFormat.Rvz && option.State == RomDeliveryState.Available);
        options.ShouldAllBe(option => option.RuntimeApplicability.Count == 0);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, catalog, admission);

        var page = (await repository.ListReleasesAsync(new(UserId), PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        page.ReleasePaths[ReleaseId].ShouldBe(["Disc/game.iso"]);
        var release = (await repository.GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value;
        release.Tree.List("Disc").ShouldNotBeNull().ShouldHaveSingleItem().RelativePath.ShouldBe("Disc/game.iso");
        var original = (await repository.GetFileAsync(new(UserId), ReleaseId, "Disc/game.iso"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value;
        original.File.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
        (await repository.GetFileAsync(new(UserId), ReleaseId, "Disc/game.rvz"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
    }

    [Fact]
    public async Task GetFileAsync_StoredAndOriginal_BindsSelectedBytesAndRejectsOldPathAlias()
    {
        SeedReleaseFiles(("Disc/game.iso", true));
        await using var db = CreateDb();
        await SetSystemKeyAsync(db, "gc");
        await AddRvzAsync(db, 1, 20, 1u);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var stored = (await repository.GetFileAsync(new(UserId), ReleaseId, "Disc/game.rvz"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value;
        var original = (await repository.GetFileAsync(new(UserId), ReleaseId, "Disc/game.iso", default, VfsFormat.Original))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value;
        stored.File.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Rvz);
        stored.File.Target.ArtifactSha256.ShouldBe(NewSha256(20));
        stored.File.Target.ContentSha256.ShouldBe(NewSha256(1));
        stored.File.Sha256.ShouldBe(NewSha256(20));
        stored.File.Length.ShouldBe(20);
        stored.ReleasePaths.ShouldBe(["Disc/game.rvz"]);
        original.File.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
        original.File.Sha256.ShouldBe(NewSha256(1));
        original.File.Length.ShouldBe(64);
        original.File.Md5.ShouldBe(NewMd5(1));
        original.File.Sha1.ShouldBe(NewSha1(1));
        original.ReleasePaths.ShouldBe(["Disc/game.iso"]);
        (await repository.GetFileAsync(new(UserId), ReleaseId, "Disc/game.iso"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
        (await repository.GetFileAsync(new(UserId), ReleaseId, "Disc/game.rvz", default, VfsFormat.Original))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
    }

    [Theory]
    [InlineData("psx", true)]
    [InlineData("gc", false)]
    public async Task GetReleaseAsync_UnsupportedSystemOrUnmeasuredRvz_KeepsOriginal(string systemKey, bool measured)
    {
        SeedReleaseFiles(("game.iso", true));
        await using var db = CreateDb();
        // A user-visible short label cannot establish native-format capability.
        await db.Platforms.Where(platform => platform.Id == PlatformId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(platform => platform.ShortName, "gc")
                .SetProperty(platform => platform.CanonicalKey, systemKey));
        await AddRvzAsync(db, 1, 20, 1u, measured: measured);
        var release = (await new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value;
        var file = release.Tree.List("")!.ShouldHaveSingleItem().File!;
        file.RelativePath.ShouldBe("game.iso");
        file.Sha256.ShouldBe(NewSha256(1));
        file.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
    }

    [Fact]
    public async Task GetReleaseAsync_MeasuredRvzWithoutPhysicalChecksums_OmitsUnknownAndKeepsLogicalOriginalFacts()
    {
        SeedReleaseFiles(("game.rvz", true));
        await using var db = CreateDb();
        await SetSystemKeyAsync(db, "gc");
        await AddRvzAsync(db, 1, 20, 1u, checksums: false, preferred: true);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var stored = (await repository.GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value.Tree.List("")!.Single().File!;
        stored.RelativePath.ShouldBe("game.rvz");
        stored.Md5.IsEmpty.ShouldBeTrue();
        stored.Sha1.IsEmpty.ShouldBeTrue();
        stored.Target.ShouldNotBeNull().Md5.ShouldBeNull();
        stored.Target.Sha1.ShouldBeNull();
        var original = (await repository.GetReleaseAsync(new(UserId), ReleaseId, default, VfsFormat.Original))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value.Tree.List("")!.Single().File!;
        original.RelativePath.ShouldBe("game.iso");
        original.Sha256.ShouldBe(NewSha256(1));
        original.Md5.ShouldBe(NewMd5(1));
        original.Sha1.ShouldBe(NewSha1(1));
    }

    [Fact]
    public async Task GetReleaseAsync_RetainedRvzSelection_IsDeterministicAndPrefersCurrentPreferredArtifact()
    {
        SeedReleaseFiles(("game.iso", true));
        await using var db = CreateDb();
        await SetSystemKeyAsync(db, "wii");
        await AddRvzAsync(db, 1, 21, 2u);
        await AddRvzAsync(db, 1, 20, 2u);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var first = (await repository.GetFileAsync(new(UserId), ReleaseId, "game.rvz"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value.File;
        first.Target.ShouldNotBeNull().ArtifactSha256.ShouldBe(NewSha256(20));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            int outputId = await (from output in db.RepresentationOutputs
                                  join rep in db.RomRepresentations on output.RepresentationId equals rep.Id
                                  where output.RomId == 1 && rep.FileId == 21
                                  select output.Id).SingleAsync();
            await MediaTestServices.CreateRomRepository(db).SetPreferredRepresentationStagedAsync(1, outputId);
            await transaction.CommitAsync();
        }
        var preferred = (await repository.GetFileAsync(new(UserId), ReleaseId, "game.rvz"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value.File;
        preferred.Target.ShouldNotBeNull().ArtifactSha256.ShouldBe(NewSha256(21));
    }

    [Theory]
    [InlineData("Disc/game.iso", "Lists/game.cue", "Other/safe.iso")]
    [InlineData("Disc/game.iso", "Disc/game.RVZ", "Other/safe.rvz")]
    [InlineData("Disc/game.iso", "Disc/game.rvz/readme.txt", "Other/safe.rvz")]
    [InlineData("Disc/é.iso", "Disc/e\u0301.bin", "Other/safe.rvz")]
    public async Task GetReleaseAndFileAsync_CompletePathPolicy_AgreesForEveryMember(string source, string companion, string expectedSibling)
    {
        SeedReleaseFiles((source, true), (companion, true), ("Other/safe.iso", true));
        await using var db = CreateDb();
        await SetSystemKeyAsync(db, "gc");
        await AddRvzAsync(db, 1, 20, 1u);
        await AddRvzAsync(db, 3, 21, 1u);
        if (companion == "Disc/e\u0301.bin")
            await AddRvzAsync(db, 2, 22, 1u);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var release = (await repository.GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value;
        release.Tree.Find(source).ShouldNotBeNull();
        release.Tree.Find(companion).ShouldNotBeNull();
        release.Tree.Find(expectedSibling).ShouldNotBeNull();
        var page = (await repository.ListReleasesAsync(new(UserId), PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        page.ReleasePaths[ReleaseId].Order(StringComparer.Ordinal).ShouldBe(new[] { source, companion, expectedSibling }.Order(StringComparer.Ordinal));
        foreach (string path in new[] { source, companion, expectedSibling })
        {
            var file = (await repository.GetFileAsync(new(UserId), ReleaseId, path))
                .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value.File;
            file.ShouldBe(release.Tree.Find(path)!.File);
        }
    }

    private static Task SetSystemKeyAsync(RomdDbContext db, string key, CancellationToken ct = default) => db.Platforms.Where(platform => platform.Id == PlatformId)
        .ExecuteUpdateAsync(setters => setters.SetProperty(platform => platform.CanonicalKey, key), ct);

    private static async Task AddRvzAsync(RomdDbContext db, int romId, byte marker, uint discType, bool measured = true, bool checksums = true, bool preferred = false, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rom = await db.RomFiles.SingleAsync(row => row.Id == romId, ct);
        db.Files.Add(new FileEntityPersistence
        {
            Id = marker,
            Sha256 = NewSha256(marker),
            Size = 20,
            SizeOnDisk = 20,
            ArtifactMd5 = checksums ? NewMd5(marker) : null,
            ArtifactSha1 = checksums ? NewSha1(marker) : null,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await ConsumerMediaFixture.PublishAsync(db, romId, marker, new RomContainerFacts.Rvz(0x01000000, discType, 5, 19, 131072, discType == 2, rom.ContentSize),
            measured ? new RomDecoderIdentity.Managed(ConsumerMediaFixture.ManagedBuild) : null, preferred, now);
    }

    private const int LibraryId = 1;
    private const int PlatformId = 1;
    private const int TitleId = 1;
    private const int ReleaseId = 1;
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public ConsumerVfsRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();

        using var seed = CreateDb();
        var now = DateTimeOffset.UtcNow;
        seed.Libraries.Add(new LibraryEntity
        {
            Id = LibraryId,
            Name = "Living Room",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = now
        });
        seed.Users.Add(new RomdUser
        {
            Id = UserId,
            UserName = "manifest-user",
            NormalizedUserName = "MANIFEST-USER",
            Email = "manifest@example.test",
            NormalizedEmail = "MANIFEST@EXAMPLE.TEST",
            LibraryId = LibraryId,
            CreatedAt = now
        });
        seed.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Sony PlayStation",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Sony PlayStation",
            BaseCompactLabel = "Sony PlayStation",
            CanonicalKey = "psx",
            ShortName = "psx",
            CreatedAt = now
        });
        seed.Platforms.Add(new PlatformEntity
        {
            Id = 2,
            Name = "Sega Saturn",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Sega Saturn",
            BaseCompactLabel = "Sega Saturn",
            CanonicalKey = "saturn",
            ShortName = "saturn",
            CreatedAt = now
        });
        seed.Titles.AddRange(
            new TitleEntity
            {
                Id = TitleId,
                PlatformId = PlatformId,
                Name = "Disc Quest",
                NormalizedName = "disc quest",
                EnrichmentStatus = "Completed",
                CreatedAt = now
            },
            new TitleEntity
            {
                Id = 2,
                PlatformId = PlatformId,
                Name = "Different Quest",
                NormalizedName = "different quest",
                EnrichmentStatus = "Completed",
                CreatedAt = now
            });
        seed.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = ReleaseId,
            PlatformId = PlatformId,
            CatalogTitleId = TitleId,
            Fingerprint = "release-fingerprint",
            Name = "Disc Quest (USA)",
            CreatedAt = now,
            UpdatedAt = now
        });
        seed.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
        {
            LibraryId = LibraryId,
            TitleId = TitleId,
            CatalogReleaseId = ReleaseId,
            DatGameId = 1,
            DatFileId = 1,
            PlatformId = PlatformId,
            IsEligible = true,
            IsComplete = true,
            IsOwned = true,
            IsPlayable = true,
            IsExposed = true,
            ExposureReason = "ExposedDefault"
        });
        seed.PublishOriginalRomsAsync().GetAwaiter().GetResult();
        seed.SaveChanges();
    }

    [Fact]
    public async Task Platforms_KeysetPagesAndReleaseQueries_AreScopedToEachPlatform()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        db.Titles.Add(new TitleEntity
        {
            Id = 3,
            PlatformId = 2,
            Name = "Saturn Game",
            NormalizedName = "saturn game",
            EnrichmentStatus = "Completed",
            CreatedAt = now
        });
        db.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = 2,
            PlatformId = 2,
            CatalogTitleId = 3,
            Fingerprint = "saturn-release",
            Name = "Saturn Game",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
        {
            LibraryId = LibraryId,
            TitleId = 3,
            CatalogReleaseId = 2,
            DatGameId = 2,
            DatFileId = 1,
            PlatformId = 2,
            IsOwned = true,
            IsExposed = true
        });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var scope = new ConsumerLibraryScope(UserId);

        var first = (await repository.ListPlatformsAsync(scope, limit: 1))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found>().Value;
        first.Items.Select(platform => platform.Id).ShouldBe([1]);
        first.NextPlatformId.ShouldBe(1);
        var second = (await repository.ListPlatformsAsync(scope, first.NextPlatformId.GetValueOrDefault(), limit: 1))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found>().Value;
        second.Items.Select(platform => platform.Id).ShouldBe([2]);
        second.NextPlatformId.ShouldBeNull();
        (await repository.ListReleasesAsync(scope, 1))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value.Items
            .Select(release => release.Id).ShouldBe([1]);
        (await repository.ListReleasesAsync(scope, 2))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value.Items
            .Select(release => release.Id).ShouldBe([2]);
    }

    [Fact]
    public async Task Platforms_OnlyAuthorizedPublishedContent_IsListedWithoutStoredBytes()
    {
        await using var db = CreateDb();
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var scope = new ConsumerLibraryScope(UserId);

        var platforms = (await repository.ListPlatformsAsync(scope, limit: 1))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found>().Value;
        platforms.Items.ShouldBe([new ConsumerVfsPlatform(PlatformId, "Sony PlayStation")]);
        platforms.NextPlatformId.ShouldBeNull();
        (await repository.GetPlatformAsync(scope, 2))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatform>.ItemNotFound>();
        (await repository.ListReleasesAsync(scope, 2))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value.Items.ShouldBeEmpty();

        await db.MaterializedLibraryReleases.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsExposed, false));
        (await repository.ListPlatformsAsync(scope))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found>().Value.Items.ShouldBeEmpty();
        (await repository.GetPlatformAsync(scope, PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatform>.ItemNotFound>();

        await db.Users.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LibraryId, (int?)null));
        (await repository.ListPlatformsAsync(scope))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.LibraryUnavailable>();
        (await repository.GetPlatformAsync(scope, PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatform>.LibraryUnavailable>();
    }

    [Fact]
    public async Task DefaultLayout_CatalogNamesAndCompanionsChange_ReflectsNewPathsWithoutAliases()
    {
        SeedReleaseFiles(("Disc/game.cue", true), ("Disc/track.bin", true));
        await using var db = CreateDb();
        var layout = new DefaultLibraryLayout(new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)));
        var scope = new ConsumerLibraryScope(UserId);
        string platform = IdCoder.Encode(PlatformId) + " Sony PlayStation";
        string release = IdCoder.Encode(ReleaseId) + " Disc Quest (USA)";
        string oldPath = platform + "/" + release + "/Disc/game.cue";

        var before = await layout.ReadAsync(scope, oldPath, false, 10);
        before.IsError.ShouldBeFalse();
        var originalHash = before.Value.Entry.File!.Sha256;
        (await layout.ReadAsync(scope, "", true, 10)).Value.Children.Select(entry => entry.Path).ShouldBe([platform]);
        (await layout.ReadAsync(scope, platform, true, 10)).Value.Children.Select(entry => entry.Path)
            .ShouldBe([platform + "/" + release]);

        await db.Platforms.Where(row => row.Id == PlatformId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "PlayStation"));
        await db.CatalogReleases.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "Disc Quest (World)"));
        await db.CatalogReleaseFiles.Where(row => row.Name == "Disc/game.cue")
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "Disc/new.cue"));

        (await layout.ReadAsync(scope, oldPath, false, 10)).FirstError.ShouldBe(ConsumerVfsErrors.NotFound());
        (await layout.ReadAsync(scope, platform, true, 10)).FirstError.ShouldBe(ConsumerVfsErrors.NotFound());
        string newPlatform = IdCoder.Encode(PlatformId) + " PlayStation";
        string newRelease = IdCoder.Encode(ReleaseId) + " Disc Quest (World)";
        var after = await layout.ReadAsync(scope, newPlatform + "/" + newRelease + "/Disc/new.cue", false, 10);
        after.IsError.ShouldBeFalse();
        after.Value.Entry.File!.Sha256.ShouldBe(originalHash);
        (await layout.ReadAsync(scope, newPlatform + "/" + newRelease + "/Disc/track.bin", false, 10)).IsError.ShouldBeFalse();

        // Valid content cannot be reached under an unrelated platform prefix.
        (await layout.ReadAsync(scope, IdCoder.Encode(2) + " Sega Saturn/" + newRelease + "/Disc/new.cue", false, 10))
            .FirstError.ShouldBe(ConsumerVfsErrors.NotFound());
    }

    [Fact]
    public async Task DefaultLayout_ReleaseMovesBetweenPlatforms_ChangesHierarchy()
    {
        SeedReleaseFiles(("game.bin", true));
        await using var db = CreateDb();
        var layout = new DefaultLibraryLayout(new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)));
        var scope = new ConsumerLibraryScope(UserId);
        string release = IdCoder.Encode(ReleaseId) + " Disc Quest (USA)/game.bin";
        string oldPlatform = IdCoder.Encode(PlatformId) + " Sony PlayStation";
        (await layout.ReadAsync(scope, oldPlatform + "/" + release, false, 10)).IsError.ShouldBeFalse();

        await db.CatalogReleases.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PlatformId, 2));
        await db.MaterializedLibraryReleases.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PlatformId, 2));
        await db.Titles.Where(row => row.Id == TitleId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PlatformId, 2));

        string newPlatform = IdCoder.Encode(2) + " Sega Saturn";
        (await layout.ReadAsync(scope, "", true, 10)).Value.Children.Select(entry => entry.Path).ShouldBe([newPlatform]);
        (await layout.ReadAsync(scope, oldPlatform + "/" + release, false, 10)).FirstError.ShouldBe(ConsumerVfsErrors.NotFound());
        (await layout.ReadAsync(scope, newPlatform + "/" + release, false, 10)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task GetReleaseAsync_OwnedCompanions_PreservesOriginalPaths()
    {
        SeedReleaseFiles(("Disc/game.cue", true), ("Disc/track.bin", true));
        var found = (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>();
        found.Value.Tree.List("Disc")!.Select(entry => entry.Name).ShouldBe(["game.cue", "track.bin"]);
        found.Value.Tree.Find("Disc/game.cue")!.File!.Length.ShouldBe(64);
        found.Value.Summary.Id.ShouldBe(ReleaseId);
    }

    [Theory]
    [InlineData("../game.bin")]
    [InlineData("folder\\game.bin")]
    public async Task GetReleaseAsync_UnsafeOriginalPath_RejectsRatherThanSanitizes(string path)
    {
        SeedReleaseFiles((path, true));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Rejected>();
    }

    [Fact]
    public async Task GetReleaseAsync_CaseCollision_RejectsLayout()
    {
        SeedReleaseFiles(("game.bin", true), ("GAME.BIN", true));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Rejected>();
    }

    [Fact]
    public async Task GetReleaseAsync_MissingCompanion_DoesNotExposePartialRelease()
    {
        SeedReleaseFiles(("game.cue", true), ("track.bin", false));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ItemNotFound>();
    }

    [Fact]
    public async Task GetReleaseAsync_UnidentifiedCompanion_DoesNotExposePartialRelease()
    {
        SeedReleaseFiles(("game.cue", true), ("track.bin", true));
        await using var db = CreateDb();
        await db.CatalogReleaseFiles.Where(row => row.Name == "track.bin")
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Sha1, (Sha1?)null));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ItemNotFound>();
    }

    [Fact]
    public async Task GetReleaseAsync_TwoPathsForSameContent_PreservesBothPaths()
    {
        SeedReleaseFiles(("Disc1/track.bin", true), ("Disc2/track.bin", false));
        await using var db = CreateDb();
        var sharedHash = NewSha1(1);
        await db.CatalogReleaseFiles.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Sha1, sharedHash));
        var found = (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>();
        found.Value.Tree.Find("Disc1/track.bin")!.File!.Sha256
            .ShouldBe(found.Value.Tree.Find("Disc2/track.bin")!.File!.Sha256);
    }

    [Fact]
    public async Task GetReleaseAsync_ForeignCatalogLogicalSize_FailsClosedWithoutChangingOwnedIdentity()
    {
        SeedReleaseFiles(("game.bin", true));
        await using var db = CreateDb();
        var exact = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(1)).ShouldNotBeNull();
        var foreign = ConsumerMediaFixture.ForeignSnapshot(exact, "logical-size");
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, RomContentDescriptor> { [1] = foreign });
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, catalog, new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        (await repository.GetReleaseAsync(new(UserId), ReleaseId)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ProjectionInconsistent>();
        (await db.RomFiles.SingleAsync()).ContentSize.ShouldBe(64);
        await catalog.Received(1).FindByRomIdsAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1 })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadAndList_LibraryAssignmentRemoved_DenySubsequentCalls()
    {
        SeedReleaseFiles(("game.bin", true));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>();
        await using var db = CreateDb();
        await db.Users.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LibraryId, (int?)null));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.LibraryUnavailable>();
        (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.LibraryUnavailable>();
    }

    [Fact]
    public async Task ReadAndList_DifferentUser_CannotUseOwnersLibrary()
    {
        SeedReleaseFiles(("game.bin", true));
        await using var db = CreateDb();
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var other = new ConsumerLibraryScope(Guid.NewGuid());
        (await repository.GetReleaseAsync(other, ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.LibraryUnavailable>();
        (await repository.ListReleasesAsync(other, PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.LibraryUnavailable>();
    }

    [Fact]
    public async Task ReadAndList_ReassignedLibrary_ImmediatelyChangesVisibleScope()
    {
        SeedReleaseFiles(("game.bin", true));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>();
        await using var db = CreateDb();
        db.Libraries.Add(new LibraryEntity
        {
            Id = 2,
            Name = "Other",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        await db.Users.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LibraryId, (int?)2));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ItemNotFound>();
        var page = (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>();
        page.LibraryId.ShouldBe(2);
        page.Value.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ReadAndList_NotOwnedOrExposed_HidesRelease(bool owned, bool exposed)
    {
        SeedReleaseFiles(("game.bin", true));
        await using var db = CreateDb();
        await db.MaterializedLibraryReleases.ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.IsOwned, owned).SetProperty(row => row.IsExposed, exposed));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ItemNotFound>();
        (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAndList_HiddenDuplicateProjection_FailsClosed()
    {
        SeedReleaseFiles(("game.bin", true));
        await using var db = CreateDb();
        db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
        {
            LibraryId = LibraryId,
            TitleId = TitleId,
            CatalogReleaseId = ReleaseId,
            DatGameId = 2,
            DatFileId = 1,
            PlatformId = PlatformId,
            IsOwned = false,
            IsExposed = false
        });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ProjectionInconsistent>();
        (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task ReadAndList_ProjectionTitleMismatch_FailsClosed()
    {
        await using var db = CreateDb();
        await db.MaterializedLibraryReleases.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TitleId, 2));
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ProjectionInconsistent>();
        (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task ReadAndList_OrphanedProjection_FailsClosed()
    {
        await using var db = CreateDb();
        await db.CatalogReleases.ExecuteDeleteAsync();
        (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ProjectionInconsistent>();
        (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task ListReleasesAsync_KeysetPaging_IsBoundedAndDoesNotNeedStoredContent()
    {
        await using var db = CreateDb();
        for (int id = 2; id <= 4; id++)
        {
            db.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = id,
                PlatformId = PlatformId,
                CatalogTitleId = TitleId,
                Fingerprint = $"release-{id}",
                Name = $"Release {id}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = LibraryId,
                TitleId = TitleId,
                CatalogReleaseId = id,
                DatGameId = id,
                DatFileId = 1,
                PlatformId = PlatformId,
                IsOwned = true,
                IsExposed = true
            });
        }
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var first = (await repository.ListReleasesAsync(new(UserId), PlatformId, limit: 2))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        first.Items.Select(row => row.Id).ShouldBe([1, 2]);
        first.NextReleaseId.ShouldBe(2);
        var second = (await repository.ListReleasesAsync(new(UserId), PlatformId, afterReleaseId: first.NextReleaseId.GetValueOrDefault(), limit: 2))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        second.Items.Select(row => row.Id).ShouldBe([3, 4]);
        second.NextReleaseId.ShouldBeNull();
    }

    [Fact]
    public async Task ListReleasesAsync_ReturnsOriginalPathsWithoutJudgingPortability()
    {
        SeedReleaseFiles(("game.bin", false), ("GAME.BIN", false));
        (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value
            .ReleasePaths[ReleaseId].Order(StringComparer.Ordinal).ShouldBe(["GAME.BIN", "game.bin"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadAndList_PublicationState_UsesPublishedSnapshot(bool published)
    {
        SeedReleaseFiles(("game.bin", true));
        await using var db = CreateDb();
        await db.Libraries.ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.NeedsMaterialization, true)
            .SetProperty(row => row.LastMaterializedAt, published ? DateTimeOffset.UtcNow : (DateTimeOffset?)null));
        if (published)
        {
            (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>();
            (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>();
        }
        else
        {
            (await Read()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.LibraryUnavailable>();
            (await List()).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.LibraryUnavailable>();
        }
    }

    public void Dispose() => _connection.Dispose();
    private RomdDbContext CreateDb() => new(_contextOptions);
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    public async Task GetFileAsync_MultiFileRelease_ResolvesOneFileAndRechecksAccess(int fileCount)
    {
        SeedReleaseFiles(("game.bin", true));
        await using var db = CreateDb();
        db.CatalogReleaseFiles.AddRange(Enumerable.Range(1, fileCount - 1).Select(index => new CatalogReleaseFileEntity
        {
            CatalogReleaseId = ReleaseId,
            FileFingerprint = "track-" + index,
            Name = $"Disc/track-{index:D5}.bin",
            Size = 64,
            Sha1 = NewSha1(1)
        }));
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        string path = fileCount == 1 ? "game.bin" : $"Disc/track-{fileCount - 1:D5}.bin";
        var durations = new List<double>();
        for (int attempt = 0; attempt < 12; attempt++)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var read = (await repository.GetFileAsync(new(UserId), ReleaseId, path))
                .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>();
            if (attempt >= 2)
                durations.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            read.Value.File.RelativePath.ShouldBe(path);
            read.Value.Summary.Id.ShouldBe(ReleaseId);
            read.Value.File.Sha256.ShouldBe(NewSha256(1));
        }
        Console.WriteLine($"VFS indexed lookup: files={fileCount}, samples={durations.Count}, meanMs={durations.Average():F3}, maxMs={durations.Max():F3}");
        await db.Users.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LibraryId, (int?)null));
        (await repository.GetFileAsync(new(UserId), ReleaseId, path))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.LibraryUnavailable>();
    }

    [Fact]
    public async Task GetFileAsync_MissingCompanion_HidesEntireRelease()
    {
        SeedReleaseFiles(("game.cue", true), ("track.bin", false));
        await using var db = CreateDb();
        (await new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).GetFileAsync(new(UserId), ReleaseId, "game.cue"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
    }

    [Fact]
    public async Task GetFileAsync_ReturnsReleasePathsWithoutJudgingPortability()
    {
        SeedReleaseFiles(("game.bin", true), ("GAME.BIN", true));
        await using var db = CreateDb();
        var found = (await new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).GetFileAsync(new(UserId), ReleaseId, "game.bin"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>();
        found.Value.ReleasePaths.Order(StringComparer.Ordinal).ShouldBe(["GAME.BIN", "game.bin"]);
    }

    private async Task<ConsumerLibraryReadResult<ConsumerVfsRelease>> Read()
    {
        await using var db = CreateDb();
        return await new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).GetReleaseAsync(new(UserId), ReleaseId);
    }
    private async Task<ConsumerLibraryReadResult<ConsumerVfsReleasePage>> List()
    {
        await using var db = CreateDb();
        return await new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).ListReleasesAsync(new(UserId), PlatformId);
    }

    private void SeedReleaseFiles(params string[] fileNames) =>
        SeedReleaseFiles(fileNames.Select(name => (name, false)).ToArray());

    private void SeedReleaseFiles(params (string Name, bool IsOwned)[] files) => SeedReleaseFilesWithSize(64, files);

    private void SeedReleaseFilesWithSize(long logicalBytes, params (string Name, bool IsOwned)[] files)
    {
        using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;

        for (int index = 0; index < files.Length; index++)
        {
            var (name, isOwned) = files[index];
            byte marker = (byte)(index + 1);
            var sha1 = NewSha1(marker);

            db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
            {
                CatalogReleaseId = ReleaseId,
                FileFingerprint = $"file-{marker}",
                Name = name,
                Size = logicalBytes,
                Sha1 = sha1
            });

            if (isOwned)
            {
                db.Files.Add(new FileEntityPersistence
                {
                    Id = marker,
                    Sha256 = NewSha256(marker),
                    Size = logicalBytes,
                    SizeOnDisk = logicalBytes,
                    IsCompressed = false,
                    CreatedAt = now
                });
                db.StageOriginalRom(new OriginalRomFixture
                {
                    Id = marker,
                    OriginalFilename = name,
                    FileId = marker,
                    Sha1 = sha1,
                    Md5 = NewMd5(marker),
                    Crc32 = Crc32.FromUInt32(marker),
                    CreatedAt = now
                });
            }
        }

        db.PublishOriginalRomsAsync().GetAwaiter().GetResult();

        db.SaveChanges();
    }

    private static Sha256 NewSha256(byte firstByte) => HashWithFirstByte<Sha256>(Sha256.ByteLength, firstByte);

    private static Sha1 NewSha1(byte firstByte) => HashWithFirstByte<Sha1>(Sha1.ByteLength, firstByte);

    private static Md5 NewMd5(byte firstByte) => HashWithFirstByte<Md5>(Md5.ByteLength, firstByte);

    private static T HashWithFirstByte<T>(int byteLength, byte firstByte)
        where T : struct, IHashValue<T>
    {
        var bytes = new byte[byteLength];
        bytes[0] = firstByte;
        return T.FromSpan(bytes);
    }
    [Fact]
    public async Task Ps2Chd_StoredAndOriginal_ListReleaseAndIndexedFileAgreeOnExactDeliveredFacts()
    {
        var evidence = await SeedPs2ChdAsync();
        await using var db = CreateDb();
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), chdAdmission: new SyntheticChdAdmission(NativeDecoder));
        var listed = (await repository.ListReleasesAsync(new(UserId), PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        listed.ReleasePaths[ReleaseId].ShouldBe(["folder/disc.chd"]);
        var release = (await repository.GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value;
        var physical = release.Tree.Find("folder/disc.chd").ShouldNotBeNull().File.ShouldNotBeNull();
        physical.Sha256.ShouldBe(NewSha256(2));
        physical.Length.ShouldBe(4096);
        physical.Md5.ShouldBe(NewMd5(2));
        physical.Sha1.ShouldBe(NewSha1(2));
        physical.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Chd);
        physical.Target.ReadBinding.ShouldBe(evidence.ChdEvidence.Single().Bind());
        var stored = (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.chd"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value;
        stored.File.Target.ShouldBe(physical.Target);
        var original = (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.iso", format: VfsFormat.Original))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value;
        original.File.Sha256.ShouldBe(NewSha256(1));
        original.File.Length.ShouldBe(8192);
        original.File.Md5.ShouldBe(NewMd5(1));
        original.File.Sha1.ShouldBe(NewSha1(1));
        original.File.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
        (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.iso"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
        (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.chd", format: VfsFormat.Original))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
    }

    [Fact]
    public async Task Ps2Chd_NoncanonicalCapturedParameters_ProjectTheAdmittedOriginal()
    {
        var artifact = await SeedPs2ChdAsync(canonical: false);
        await using var db = CreateDb();
        var content = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(1)).ShouldNotBeNull();
        content.EncodingProfile.ShouldContain("hunk=4096");
        DeliveryPolicy.IsCanonicalChd("ps2-dvd", (RomContainerFacts.Chd)artifact.Container).ShouldBeFalse();
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new SyntheticChdAdmission(NativeDecoder));
        var page = (await repository.ListReleasesAsync(new(UserId), PlatformId)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        page.ReleasePaths[ReleaseId].ShouldBe(["folder/disc.iso"]);
        (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.chd")).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
    }

    [Fact]
    public async Task Ps2Chd_RegisteredRecipeRejectsProfile_ProjectsOriginalOnEveryVfsRead()
    {
        var evidence = await SeedPs2ChdAsync();
        var registration = RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "ps2-dvd");
        var original = registration.WholeFilePolicy!(registration.PlatformKeys.Single());
        var adapter = Substitute.For<IRomMediaTypePolicy>();
        adapter.PlatformKey.Returns(original.PlatformKey);
        adapter.DiscKind.Returns(original.DiscKind);
        adapter.EncodingProfile.StoredEncoding.Returns(original.EncodingProfile.StoredEncoding);
        adapter.EncodingProfile.RecipeVersion.Returns(original.EncodingProfile.RecipeVersion);
        adapter.MatchesFacts(Arg.Any<RomContentDescriptor>()).Returns(call => original.MatchesFacts(call.Arg<RomContentDescriptor>()));
        adapter.EncodingProfile.Classify(Arg.Any<RomContainerFacts>()).Returns(RomRepresentationProfileClass.NonCanonical);
        var policy = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(kind =>
            kind == registration ? kind with { WholeFilePolicy = _ => adapter } : kind).ToArray()).DeliveryPolicy;
        await using var db = CreateDb();
        var repository = new ConsumerVfsRepository(db, policy, MediaTestServices.CreateContentCatalog(db),
            chdAdmission: new SyntheticChdAdmission(NativeDecoder));

        var page = (await repository.ListReleasesAsync(new(UserId), PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        page.ReleasePaths[ReleaseId].ShouldBe(["folder/disc.iso"]);
        var release = (await repository.GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value;
        release.Tree.Find("folder/disc.iso").ShouldNotBeNull().File.ShouldNotBeNull()
            .Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
        (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.iso"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value.File
            .Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
        (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.chd"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
    }

    [Theory]
    [InlineData("missing-admission")]
    [InlineData("missing-proof")]
    [InlineData("kind-without-runtimes")]
    [InlineData("noncanonical-profile")]
    [InlineData("historical-v1-recipe")]
    public async Task Ps2Chd_UnqualifiedStoredProjection_DoesNotAdvertiseChd(string refusal)
    {
        // A chd-dvd-canonical/v1 (zlib) artifact stays deliverable but projects as its original until re-converged.
        var evidence = await SeedPs2ChdAsync(retainEvidence: refusal != "missing-proof", canonical: refusal != "noncanonical-profile",
            historicalV1: refusal == "historical-v1-recipe");
        await using var db = CreateDb();
        var admission = refusal == "missing-admission" ? null : new SyntheticChdAdmission(NativeDecoder);
        // A kind whose registration names no runtime that reads its CHD natively never lists the stored format.
        var policy = refusal == "kind-without-runtimes"
            ? RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(kind =>
                kind.Kind == "ps2-dvd" ? kind with { NativeRuntimes = [] } : kind).ToArray()).DeliveryPolicy
            : DeliveryPolicy;
        var repository = new ConsumerVfsRepository(db, policy, MediaTestServices.CreateContentCatalog(db), chdAdmission: admission);
        var page = (await repository.ListReleasesAsync(new(UserId), PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        page.ReleasePaths[ReleaseId].ShouldBe(["folder/disc.iso"]);
        (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.chd"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
    }

    [Theory]
    [InlineData("md5")]
    [InlineData("sha1")]
    public async Task Ps2Chd_MissingPhysicalHash_PreservesLogicalOriginalWithoutStoredProjection(string missing)
    {
        var evidence = await SeedPs2ChdAsync(missingArtifactHash: missing);
        await using var db = CreateDb();
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), chdAdmission: new SyntheticChdAdmission(NativeDecoder));
        var result = (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.iso"))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>().Value;
        result.File.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
        result.File.Sha256.ShouldBe(NewSha256(1));
        result.File.Length.ShouldBe(8192);
        result.ReleasePaths.ShouldBe(["folder/disc.iso"]);
    }

    /// <summary>
    ///     A release on a platform the ROM's DVD CHD evidence contradicts resolves no media kind. Its verified CHD is admitted
    ///     from evidence alone, so the release lists the decoded original; no stored projection applies to a platform whose
    ///     kind does not store CHD. Before kind resolution, CHD admission required the PS2 platform, so the release read was
    ///     inconsistent.
    /// </summary>
    [Fact]
    public async Task Ps2Chd_ReleaseOnAPlatformItsEvidenceContradicts_ListsTheDecodedOriginal()
    {
        var evidence = await SeedPs2ChdAsync();
        await using var db = CreateDb();
        await SetSystemKeyAsync(db, "psx");
        var native = new ChdNativeIdentity(NativeDecoder.Rid, "libmoonlark_chdr.so", 100,
            new string('c', 64), NativeDecoder.UpstreamCommit.ToString(), "test-only-upstream",
            NativeDecoder.RecipeVersion, "test-only-family", new string('e', 64), true, true, true);
        var candidate = new ChdNativeCandidate("/synthetic/vfs-native", native);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db),
            chdAdmission: new ChdDeliveryAdmission(() => true, () => candidate, () => { }, _ => native));

        var release = (await repository.GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.Found>().Value;

        var original = release.Tree.Find("folder/disc.iso").ShouldNotBeNull().File.ShouldNotBeNull();
        original.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
        original.Target.ReadBinding.ShouldBe(evidence.ChdEvidence.Single().Bind());
        original.Sha256.ShouldBe(NewSha256(1));
        release.Tree.Find("folder/disc.chd").ShouldBeNull();
    }

    [Fact]
    public async Task Ps2Chd_ContradictoryCatalogProjection_RefusesEvenOtherwiseValidAdmission()
    {
        var evidence = await SeedPs2ChdAsync();
        await using var db = CreateDb();
        var content = (await Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(1)).ShouldNotBeNull();
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RomContentDescriptor> { [1] = ConsumerMediaFixture.ForeignSnapshot(content, "file") });
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, catalog, new SyntheticChdAdmission(NativeDecoder));
        (await repository.GetReleaseAsync(new(UserId), ReleaseId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsRelease>.ProjectionInconsistent>();
        (await repository.GetFileAsync(new(UserId), ReleaseId, "folder/disc.iso", format: VfsFormat.Original))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ProjectionInconsistent>();
    }


    private static RomChdDecoderBuild NativeDecoder => new("linux-x64", NewSha1(7), 1);
    private async Task<RomContentArtifact> SeedPs2ChdAsync(bool preferred = true, bool retainEvidence = true,
        string? missingArtifactHash = null, bool canonical = true, bool historicalV1 = false)
    {
        SeedReleaseFilesWithSize(8192, ("folder/disc.iso", true));
        await using var db = CreateDb();
        await db.Platforms.Where(row => row.Id == PlatformId).ExecuteUpdateAsync(update => update.SetProperty(row => row.CanonicalKey, "ps2").SetProperty(row => row.ShortName, "ps2"));
        var observed = new DateTimeOffset(2026, 10, 11, 1, 2, 3, TimeSpan.Zero).AddTicks(42);
        uint hunk = canonical ? 16384u : 4096u;
        uint codec = historicalV1 ? RomChdCodecs.Zlib : RomChdCodecs.Zstd;
        var container = new RomContainerFacts.Chd(5, 8192, hunk, 2048, (8192 + hunk - 1) / hunk, [codec, 0, 0, 0], false, NewSha1(1), NewSha1(3), [new(0x44564420, 1, [0])]);
        db.Files.Add(new FileEntityPersistence
        {
            Id = 2,
            Sha256 = NewSha256(2),
            Size = 4096,
            SizeOnDisk = 4096,
            ArtifactMd5 = missingArtifactHash == "md5" ? null : NewMd5(2),
            ArtifactSha1 = missingArtifactHash == "sha1" ? null : NewSha1(2),
            CreatedAt = observed
        });
        await db.SaveChangesAsync();
        return await ConsumerMediaFixture.PublishAsync(db, 1, 2, container, retainEvidence ? new RomDecoderIdentity.Native(NativeDecoder) : null, preferred, observed);
    }

    private sealed class SyntheticChdAdmission(RomChdDecoderBuild decoder) : IRomChdDeliveryAdmission
    {
        private readonly ConsumerMediaAdmission _inner = new(ConsumerMediaFixture.ManagedBuild, decoder);
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => _inner.Assess(evidence);
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => _inner.AssessManaged(artifact);
        public bool AdmitsCd(RomReconstruction.CdMember member) => _inner.AdmitsCd(member);
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => _inner.AssessCd(member);
    }

}
