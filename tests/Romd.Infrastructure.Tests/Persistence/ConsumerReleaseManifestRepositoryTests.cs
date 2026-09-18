using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ConsumerReleaseManifestRepositoryTests : IDisposable
{
    private const int LibraryId = 1;
    private const int PlatformId = 1;
    private const int TitleId = 1;
    private const int ReleaseId = 1;
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public ConsumerReleaseManifestRepositoryTests()
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
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Sony PlayStation", BaseCompactLabel = "Sony PlayStation", CanonicalKey = "psx", ShortName = "psx",
            CreatedAt = now
        });
        seed.Platforms.Add(new PlatformEntity
        {
            Id = 2,
            Name = "Sega Saturn",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Sega Saturn", BaseCompactLabel = "Sega Saturn", CanonicalKey = "saturn", ShortName = "saturn",
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
        seed.SaveChanges();
    }

    [Fact]
    public async Task GetManifestAsync_CueWithMultipleBins_SelectsCueAsLaunchTarget()
    {
        SeedReleaseFiles(
            "Disc Quest (USA) (Track 1).bin",
            "Disc Quest (USA) (Track 2).bin",
            "Disc Quest (USA).cue");

        var manifest = await GetFoundManifestAsync();

        manifest.Runtime.Launch.ShouldNotBeNull();
        manifest.Runtime.Launch.Type.ShouldBe("file");
        manifest.Runtime.Launch.RelativePath.ShouldBe("Disc Quest (USA).cue");
    }

    [Fact]
    public async Task GetManifestAsync_M3uAlongsideCues_SelectsM3u()
    {
        SeedReleaseFiles(
            "Disc Quest (USA) (Disc 1).bin",
            "Disc Quest (USA) (Disc 1).cue",
            "Disc Quest (USA) (Disc 2).bin",
            "Disc Quest (USA) (Disc 2).cue",
            "Disc Quest (USA).m3u");

        var manifest = await GetFoundManifestAsync();

        manifest.Runtime.Launch.ShouldNotBeNull();
        manifest.Runtime.Launch.RelativePath.ShouldBe("Disc Quest (USA).m3u");
    }

    [Fact]
    public async Task GetManifestAsync_MultipleCuesWithoutM3u_SelectsLexicographicallyFirstCue()
    {
        SeedReleaseFiles(
            "Disc Quest (USA) (Disc 2).cue",
            "Disc Quest (USA) (Disc 2).bin",
            "Disc Quest (USA) (Disc 1).cue",
            "Disc Quest (USA) (Disc 1).bin");

        var manifest = await GetFoundManifestAsync();

        manifest.Runtime.Launch.ShouldNotBeNull();
        manifest.Runtime.Launch.RelativePath.ShouldBe("Disc Quest (USA) (Disc 1).cue");
    }

    [Fact]
    public async Task GetManifestAsync_ChdWithCompanionFile_SelectsChdCaseInsensitively()
    {
        SeedReleaseFiles(
            "Disc Quest (USA).CHD",
            "Disc Quest (USA) (Extra).bin");

        var manifest = await GetFoundManifestAsync();

        manifest.Runtime.Launch.ShouldNotBeNull();
        manifest.Runtime.Launch.RelativePath.ShouldBe("Disc Quest (USA).CHD");
    }

    [Fact]
    public async Task GetManifestAsync_IsoWithCompanionFile_SelectsIso()
    {
        SeedReleaseFiles(
            "Disc Quest (USA).iso",
            "Disc Quest (USA) (Extra).bin");

        var manifest = await GetFoundManifestAsync();

        manifest.Runtime.Launch.ShouldNotBeNull();
        manifest.Runtime.Launch.RelativePath.ShouldBe("Disc Quest (USA).iso");
    }

    [Fact]
    public async Task GetManifestAsync_SingleNonDiscFile_SelectsThatFile()
    {
        SeedReleaseFiles("Chrono Trigger (USA).sfc");

        var manifest = await GetFoundManifestAsync();

        manifest.Runtime.ContentType.ShouldBe("single_rom");
        manifest.Runtime.Launch.ShouldNotBeNull();
        manifest.Runtime.Launch.Type.ShouldBe("file");
        manifest.Runtime.Launch.RelativePath.ShouldBe("Chrono Trigger (USA).sfc");
    }

    [Fact]
    public async Task GetManifestAsync_MultipleItemsWithoutRecognizedExtension_ReturnsNullLaunch()
    {
        SeedReleaseFiles(
            "partial/part-1.bin",
            "partial/part-2.bin");

        var manifest = await GetFoundManifestAsync();

        manifest.Runtime.ContentType.ShouldBe("unknown");
        manifest.Runtime.Launch.ShouldBeNull();
    }

    [Fact]
    public async Task GetManifestAsync_UnavailableCue_StillSelectsCueAsLaunchTarget()
    {
        SeedReleaseFiles(
            ("Disc Quest (USA).cue", false),
            ("Disc Quest (USA) (Track 1).bin", true));

        var manifest = await GetFoundManifestAsync();

        manifest.Items.Single(item => item.RelativePath.EndsWith(".cue")).IsAvailable.ShouldBeFalse();
        manifest.Items.Single(item => item.RelativePath.EndsWith(".bin")).IsAvailable.ShouldBeTrue();
        manifest.Runtime.Launch.ShouldNotBeNull();
        manifest.Runtime.Launch.RelativePath.ShouldBe("Disc Quest (USA).cue");
    }

    [Fact]
    public async Task GetManifestAsync_UnsafeProducerFilename_EmitsSafeRelativePath()
    {
        SeedReleaseFiles("C:\\folder\\bad\u0001:name.rom");

        var manifest = await GetFoundManifestAsync();

        var item = manifest.Items.ShouldHaveSingleItem();
        item.RelativePath.ShouldBe("C_/folder/bad__name.rom");
        item.RelativePath.ShouldNotContain('\\');
        item.RelativePath.ShouldNotContain('\u0001');
        manifest.Runtime.Launch.ShouldNotBeNull();
        manifest.Runtime.Launch.RelativePath.ShouldBe(item.RelativePath);
    }

    [Theory]
    [InlineData("NUL.rom", "_NUL.rom")]
    [InlineData("folder/aux", "folder/_aux")]
    [InlineData("COM1.bin", "_COM1.bin")]
    public async Task GetManifestAsync_ReservedProducerFilename_EmitsSafeRelativePath(
        string original,
        string expected)
    {
        SeedReleaseFiles(original);

        var manifest = await GetFoundManifestAsync();

        manifest.Items.ShouldHaveSingleItem().RelativePath.ShouldBe(expected);
    }

    [Fact]
    public async Task GetManifestAsync_AbsentProjection_ReturnsAuthoritativeItemNotFound()
    {
        var result = await GetManifestResultAsync(releaseId: 8_888);

        var notFound = result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.ItemNotFound>();
        notFound.LibraryId.ShouldBe(LibraryId);
    }

    [Fact]
    public async Task GetManifestAsync_CorruptValidLibrary_ReturnsLibraryUnavailable()
    {
        await using (var db = CreateDb())
        {
            await db.Libraries
                .Where(library => library.Id == LibraryId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(library => library.ConfigurationState, LibraryConfigurationState.Valid.ToString())
                    .SetProperty(library => library.ConfigurationJson, "{"));
        }

        var result = await GetManifestResultAsync();

        result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.LibraryUnavailable>();
    }

    [Fact]
    public async Task GetManifestAsync_OrphanedCatalogRelease_ReturnsUnavailable()
    {
        const int orphanedReleaseId = 999;
        await using (var db = CreateDb())
        {
            await db.MaterializedLibraryReleases
                .Where(release => release.LibraryId == LibraryId && release.CatalogReleaseId == ReleaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    release => release.CatalogReleaseId,
                    orphanedReleaseId));
        }

        var result = await GetManifestResultAsync(orphanedReleaseId);

        result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task GetManifestAsync_TitleMismatchedProjection_ReturnsUnavailable()
    {
        await using (var db = CreateDb())
        {
            await db.MaterializedLibraryReleases
                .Where(release => release.LibraryId == LibraryId && release.CatalogReleaseId == ReleaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.TitleId, 2));
        }

        var result = await GetManifestResultAsync();

        result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task GetManifestAsync_PlatformMismatchedProjection_ReturnsUnavailable()
    {
        await using (var db = CreateDb())
        {
            await db.MaterializedLibraryReleases
                .Where(release => release.LibraryId == LibraryId && release.CatalogReleaseId == ReleaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.PlatformId, 2));
        }

        var result = await GetManifestResultAsync();

        result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task GetManifestAsync_DuplicateProjection_ReturnsUnavailableInsteadOfChoosingOne()
    {
        await using (var db = CreateDb())
        {
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = LibraryId,
                TitleId = TitleId,
                CatalogReleaseId = ReleaseId,
                DatGameId = 2,
                DatFileId = 1,
                PlatformId = PlatformId,
                IsEligible = true,
                IsComplete = true,
                IsOwned = true,
                IsPlayable = true,
                IsExposed = true,
                ExposureReason = "ExposedDefault"
            });
            await db.SaveChangesAsync();
        }

        var result = await GetManifestResultAsync();

        result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.ProjectionInconsistent>();
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private async Task<ConsumerReleaseManifest> GetFoundManifestAsync()
    {
        var result = await GetManifestResultAsync();
        var found = result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found>();
        found.LibraryId.ShouldBe(LibraryId);
        return found.Value;
    }

    private async Task<ConsumerLibraryReadResult<ConsumerReleaseManifest>> GetManifestResultAsync(
        int releaseId = ReleaseId)
    {
        var repository = new ConsumerReleaseManifestRepository(CreateDb());

        var result = await repository.GetManifestAsync(
            new ConsumerReleaseManifestRequest(new ConsumerLibraryScope(UserId), releaseId));

        return result;
    }

    private void SeedReleaseFiles(params string[] fileNames) =>
        SeedReleaseFiles(fileNames.Select(name => (name, false)).ToArray());

    private void SeedReleaseFiles(params (string Name, bool IsOwned)[] files)
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
                Size = 64,
                Sha1 = sha1
            });

            if (isOwned)
            {
                db.Files.Add(new FileEntityPersistence
                {
                    Id = marker,
                    Sha256 = NewSha256(marker),
                    Size = 64,
                    SizeOnDisk = 64,
                    IsCompressed = false,
                    CreatedAt = now
                });
                db.RomFiles.Add(new RomFileEntity
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
}
