using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Libraries;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.MediaFacts;
using Romd.Infrastructure.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class ConsumerReleaseManifestRepositoryTests : IDisposable
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
    [InlineData("COM¹.bin", "_COM¹.bin")]
    [InlineData("CONIN$.bin", "_CONIN$.bin")]
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
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        var result = await GetManifestResultAsync();

        result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task GetManifestAsync_OwnedLengthDiffersFromCatalog_RefusesMismatchedRequirement()
    {
        SeedReleaseFiles(("disc.iso", true));
        await using (var db = CreateDb())
            await db.CatalogReleaseFiles.ExecuteUpdateAsync(update => update.SetProperty(file => file.Size, 999L));

        var manifest = await GetFoundManifestAsync();

        var item = manifest.Items.ShouldHaveSingleItem();
        item.Source.ShouldBeOfType<ConsumerReleaseManifestSource.Missing>();
        item.IsAvailable.ShouldBeFalse();
        item.Sha256.ShouldBeNull();
        item.Formats.ShouldBeEmpty();
        manifest.IsComplete.ShouldBeFalse();
        manifest.IsDeliveryComplete.ShouldBeFalse();
        // The unavailable requirement keeps its expected length; stored logical facts remain immutable.
        item.ExpectedSizeBytes.ShouldBe(999);
        await using var stored = CreateDb();
        (await MediaTestServices.CreateContentCatalog(stored).FindByRomIdAsync(1)).ShouldNotBeNull().ContentSize.ShouldBe(64);

    }

    [Fact]
    public async Task GetManifestAsync_OriginalPreferredWithVerifiedRvzAlternate_OffersBoundRawArtifact()
    {
        SeedReleaseFiles(("disc.iso", true));
        await SeedRvzAlternateAsync();

        var manifest = await GetFoundManifestAsync();

        var formats = manifest.Items.ShouldHaveSingleItem().Formats!;
        var original = formats.Single(format => format.Delivery.Format == RomDeliveryFormat.Original);
        original.Delivery.Target!.FileId.ShouldBe(1);
        original.Delivery.Target.Sha256.ShouldBe(NewSha256(1));
        var rvz = formats.Single(format => format.Delivery.Format == RomDeliveryFormat.Rvz);
        rvz.RelativePath.ShouldBe("disc.rvz");
        rvz.Delivery.State.ShouldBe(RomDeliveryState.Available);
        rvz.Delivery.Target!.FileId.ShouldBe(2);
        rvz.Delivery.Target.Sha256.ShouldBe(NewSha256(2));
        rvz.Delivery.Target.SizeBytes.ShouldBe(20);
        rvz.Delivery.Target.Md5.ShouldBe(NewMd5(2));
        rvz.Delivery.Target.Sha1.ShouldBe(NewSha1(2));
    }

    [Theory]
    [InlineData("gc")]
    [InlineData("wii")]
    public async Task GetManifestAsync_SingleDiscWithStoredDelivery_DescribesEachAvailableFormatsOwnRuntime(string systemKey)
    {
        SeedReleaseFiles(("disc.iso", true));
        await SetPlatformKeyAsync(systemKey);
        await SeedRvzAlternateAsync();

        var manifest = await GetFoundManifestAsync();

        manifest.Items.ShouldHaveSingleItem().Formats!.Select(format => (format.Delivery.Format, format.Delivery.State, format.Runtime))
            .ShouldBe([
                (RomDeliveryFormat.Original, RomDeliveryState.Available,
                    new ConsumerReleaseRuntime("single_rom", new("file", "disc.iso"), "direct_files", 64)),
                (RomDeliveryFormat.Rvz, RomDeliveryState.Available,
                    new ConsumerReleaseRuntime("single_rom", new("file", "disc.rvz"), "direct_files", 20))
            ]);
    }

    [Fact]
    public async Task GetManifestAsync_UnclassifiedOriginalWithUnmeasuredAlternate_ListsUnsupportedHoldingWithoutRuntimeClaim()
    {
        SeedReleaseFiles(("disc.iso", true));
        await SetPlatformKeyAsync("gc");
        await SeedRvzAlternateAsync(measured: false);

        var manifest = await GetFoundManifestAsync();

        var formats = manifest.Items.ShouldHaveSingleItem().Formats!;
        formats.Select(format => format.Delivery.Format).ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Rvz]);
        var original = formats[0];
        var unsupported = formats[1];
        unsupported.Delivery.State.ShouldBe(RomDeliveryState.Unsupported);
        unsupported.Delivery.Target.ShouldBeNull();
        unsupported.Delivery.RuntimeApplicability.ShouldBeEmpty();
        unsupported.Runtime.ShouldBeNull();
        original.Delivery.Format.ShouldBe(RomDeliveryFormat.Original);
        original.Delivery.State.ShouldBe(RomDeliveryState.Available);
        original.Delivery.Target!.ReadBinding.ShouldBeNull();
        original.Delivery.Target.Sha256.ShouldBe(NewSha256(1));
        original.Runtime.ShouldBeNull(); // No captured structural observation established a playable kind.
    }

    [Fact]
    public async Task GetManifestAsync_UninspectedOriginalOnRegisteredPlatform_ClaimsNoRuntime()
    {
        SeedReleaseFiles(("disc.iso", true));
        await SetPlatformKeyAsync("ps2");

        var manifest = await GetFoundManifestAsync();

        manifest.Items.ShouldHaveSingleItem().Formats!.ShouldHaveSingleItem().Runtime
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("snes", "Chrono Trigger (USA).sfc")]
    [InlineData("psx", "disc.iso")]
    public async Task GetManifestAsync_SingleFileWithoutStoredDelivery_DescribesNoFormatRuntime(string systemKey, string fileName)
    {
        SeedReleaseFiles((fileName, true));
        await SetPlatformKeyAsync(systemKey);

        var manifest = await GetFoundManifestAsync();

        var original = manifest.Items.ShouldHaveSingleItem().Formats!.ShouldHaveSingleItem();
        original.Delivery.State.ShouldBe(RomDeliveryState.Available);
        original.Runtime.ShouldBeNull();
    }

    [Fact]
    public async Task GetManifestAsync_MultiFileReleaseWithStoredDelivery_DescribesNoFormatRuntime()
    {
        SeedReleaseFiles(("disc.iso", true), ("notes.txt", true));
        await SetPlatformKeyAsync("gc");

        var manifest = await GetFoundManifestAsync();

        manifest.Items.Select(item => item.Formats!.ShouldHaveSingleItem())
            .Select(format => (format.RelativePath, format.Delivery.State, format.Runtime))
            .ShouldBe([("disc.iso", RomDeliveryState.Available, null), ("notes.txt", RomDeliveryState.Available, null)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetManifestAsync_AdmittedPs2Dvd_SeparatesLogicalOriginalAndBoundChdAndRuntime(bool preferred)
    {
        var evidence = await SeedPs2ChdAsync(preferred: preferred);
        var admission = new SyntheticChdAdmission(NativeDecoder);

        var manifest = await GetChdManifestAsync(admission);

        manifest.PlatformShortName.ShouldBe("ps2");
        var item = manifest.Items.ShouldHaveSingleItem();
        item.RelativePath.ShouldBe("folder/disc.iso");
        item.Sha256.ShouldBe(NewSha256(1));
        item.SizeBytes.ShouldBe(8192);
        ((ConsumerReleaseManifestSource.Original)item.Source).AvailableTarget!.ContentSizeBytes.ShouldBe(8192);
        item.IsAvailable.ShouldBeTrue();
        manifest.Runtime.Launch!.RelativePath.ShouldBe("folder/disc.iso");
        manifest.Runtime.MinimumInstallBytes.ShouldBe(8192);
        var formats = item.Formats!;
        formats.Select(format => format.Delivery.Format).ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Chd]);
        formats[0].Runtime.ShouldBe(new ConsumerReleaseRuntime("single_rom", new("file", "folder/disc.iso"), "direct_files", 8192));
        var original = formats[0].Delivery.Target!;
        original.FileId.ShouldBe(preferred ? 2 : 1);
        original.Sha256.ShouldBe(NewSha256(1));
        original.SizeBytes.ShouldBe(8192);
        original.Md5.ShouldBe(NewMd5(1));
        original.Sha1.ShouldBe(NewSha1(1));
        var chd = formats[1];
        chd.RelativePath.ShouldBe("folder/disc.chd");
        chd.Delivery.State.ShouldBe(RomDeliveryState.Available);
        chd.Delivery.Target!.FileId.ShouldBe(2);
        chd.Delivery.Target.Sha256.ShouldBe(NewSha256(2));
        chd.Delivery.Target.SizeBytes.ShouldBe(4096);
        chd.Delivery.Target.Md5.ShouldBe(NewMd5(2));
        chd.Delivery.Target.Sha1.ShouldBe(NewSha1(2));
        chd.Delivery.Target.ContentSha256.ShouldBe(NewSha256(1));
        chd.Delivery.Target.ContentSizeBytes.ShouldBe(8192);
        chd.Delivery.Target.ReadBinding.ShouldBe(evidence.ChdEvidence.Single().Bind());
        chd.Delivery.RuntimeApplicability.ShouldBe(["pcsx2"]);
        chd.Runtime.ShouldNotBeNull();
        chd.Runtime.Launch.ShouldNotBeNull();
        chd.Runtime.Launch.RelativePath.ShouldBe(chd.RelativePath);
        chd.Runtime.MinimumInstallBytes.ShouldBe(chd.Delivery.Target.SizeBytes);
        admission.LastEvidence!.Measurement.Id.ShouldBe(evidence.Facts.Verifications.Single().Id);
        admission.LastEvidence.Output.Id.ShouldBe(evidence.OutputId);
        admission.LastEvidence.Container.Fingerprint.ShouldBe(evidence.Container.Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetManifestAsync_Ps2ChdWithoutServingAssessment_DoesNotInventAvailability(bool preferred)
    {
        await SeedPs2ChdAsync(preferred: preferred);

        var manifest = await GetChdManifestAsync();

        var item = manifest.Items.ShouldHaveSingleItem();
        item.IsAvailable.ShouldBe(!preferred);
        item.Sha256.ShouldBe(preferred ? (Sha256?)null : NewSha256(1));
        var chd = item.Formats!.Single(format => format.Delivery.Format == RomDeliveryFormat.Chd);
        chd.Delivery.State.ShouldBe(RomDeliveryState.Unsupported);
        chd.Delivery.Target.ShouldBeNull();
        chd.Delivery.RuntimeApplicability.ShouldBeEmpty();
        chd.Runtime.ShouldBeNull();
    }

    [Fact]
    public async Task GetManifestAsync_ChdWithoutRetainedMeasurement_DoesNotAdmitOrAssess()
    {
        var evidence = await SeedPs2ChdAsync(retainEvidence: false);
        var admission = new SyntheticChdAdmission(NativeDecoder);
        await using (var db = CreateDb())
        {
            var representation = await db.RomRepresentations.SingleAsync(row => row.Id == evidence.RepresentationId);
            representation.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
            representation.FileId.ShouldBe(2);
            (await db.Verifications.AnyAsync(row => row.RepresentationId == evidence.RepresentationId)).ShouldBeFalse();
            (await db.Database.SqlQuery<int>($"""SELECT count(*)::integer AS "Value" FROM romd."ChdContainerParameters" WHERE "RepresentationId"={evidence.RepresentationId}""").SingleAsync()).ShouldBe(1);
        }

        var manifest = await GetChdManifestAsync(admission);

        var item = manifest.Items.ShouldHaveSingleItem();
        item.IsAvailable.ShouldBeFalse();
        item.Formats!.Select(format => format.Delivery.Format).ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Chd]);
        item.Formats.ShouldAllBe(format => format.Delivery.State == RomDeliveryState.Unsupported &&
            format.Delivery.Target == null && format.Delivery.RuntimeApplicability.Count == 0 && format.Runtime == null);
        var original = item.Formats[0];
        original.Delivery.Format.ShouldBe(RomDeliveryFormat.Original);
        original.Delivery.State.ShouldBe(RomDeliveryState.Unsupported);
        original.Delivery.Target.ShouldBeNull();
        original.Runtime.ShouldBeNull();
        admission.AssessCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetManifestAsync_ChdWrongDecoderAssessment_Refuses()
    {
        var evidence = await SeedPs2ChdAsync();
        var decoder = NativeDecoder;
        var other = new RomChdDecoderBuild(decoder.Rid, decoder.UpstreamCommit, decoder.RecipeVersion + 1);
        other.BuildLabel.ShouldNotBe(decoder.BuildLabel);
        var admission = new SyntheticChdAdmission(other);

        var manifest = await GetChdManifestAsync(admission);

        manifest.Items.ShouldHaveSingleItem().IsAvailable.ShouldBeFalse();
        var chd = manifest.Items[0].Formats!.Single(format => format.Delivery.Format == RomDeliveryFormat.Chd);
        chd.Delivery.State.ShouldBe(RomDeliveryState.Unsupported);
        chd.Runtime.ShouldBeNull();
        admission.AssessCount.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("md5")]
    [InlineData("sha1")]
    public async Task GetManifestAsync_ChdWholeArtifactHashMissing_KeepsLogicalOriginalButOmitsRawChd(string hash)
    {
        var evidence = await SeedPs2ChdAsync(missingArtifactHash: hash);
        await using (var db = CreateDb())
        {
            var artifact = await db.Files.SingleAsync(row => row.Id == 2);
            if (hash == "md5")
            {
                artifact.ArtifactMd5.ShouldBeNull();
                artifact.ArtifactSha1.ShouldBe(NewSha1(2));
            }
            else
            {
                artifact.ArtifactMd5.ShouldBe(NewMd5(2));
                artifact.ArtifactSha1.ShouldBeNull();
            }
            (await db.Verifications.AnyAsync(row => row.Id == evidence.Facts.Verifications.Single().Id)).ShouldBeTrue();
            (await db.Verifications.AnyAsync(row => row.Id == evidence.Facts.Verifications.Single().Id)).ShouldBeTrue();
        }

        var manifest = await GetChdManifestAsync(new SyntheticChdAdmission(NativeDecoder));

        var item = manifest.Items.ShouldHaveSingleItem();
        item.IsAvailable.ShouldBeTrue();
        item.Sha256.ShouldBe(NewSha256(1));
        item.Formats![0].Delivery.Target!.Md5.ShouldBe(NewMd5(1));
        item.Formats[0].Delivery.Target!.Sha1.ShouldBe(NewSha1(1));
        var chd = item.Formats.Single(format => format.Delivery.Format == RomDeliveryFormat.Chd);
        chd.Delivery.State.ShouldBe(RomDeliveryState.Unsupported);
        chd.Runtime.ShouldBeNull();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("file")]
    [InlineData("representation")]
    [InlineData("logical-sha256")]
    [InlineData("logical-size")]
    [InlineData("logical-md5")]
    [InlineData("logical-sha1")]
    [InlineData("logical-crc")]
    [InlineData("artifact-sha256")]
    [InlineData("artifact-size")]
    [InlineData("encoding")]
    public async Task GetManifestAsync_ChdCatalogAndOwnedProjectionDisagree_DoesNotBorrowAnotherBinding(string disagreement)
    {
        var evidence = await SeedPs2ChdAsync();
        await using var db = CreateDb();
        var exact = (await Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(1))!;
        var changed = ConsumerMediaFixture.ForeignSnapshot(exact, disagreement);
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RomContentDescriptor> { [1] = changed });
        var admission = new SyntheticChdAdmission(NativeDecoder);

        var manifest = await GetChdManifestAsync(admission, catalog);

        var item = manifest.Items.ShouldHaveSingleItem();
        item.IsAvailable.ShouldBeFalse();
        item.Sha256.ShouldBeNull();
        item.Formats!.ShouldBeEmpty();
        admission.AssessCount.ShouldBe(0);
        await catalog.Received(1).FindByRomIdsAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1 })), Arg.Any<CancellationToken>());
        await catalog.DidNotReceiveWithAnyArgs().FindByRomIdAsync(default, default);
    }

    /// <summary>
    ///     A release on a platform the ROM's DVD CHD evidence contradicts resolves no media kind. Its verified CHD is admitted
    ///     from evidence alone, so the release offers the decoded original and the CHD with no runtime claim and no format
    ///     runtime. Before kind resolution, CHD admission required the PS2 platform and refused both.
    /// </summary>
    [Fact]
    public async Task GetManifestAsync_VerifiedChdOnAPlatformItsEvidenceContradicts_OffersBytesWithoutRuntimeClaims()
    {
        var evidence = await SeedPs2ChdAsync();
        await SetPlatformKeyAsync("psx");

        var manifest = await GetChdManifestAsync(ResidentAdmission(NativeDecoder));

        var item = manifest.Items.ShouldHaveSingleItem();
        item.IsAvailable.ShouldBeTrue();
        item.Formats!.Select(format => (format.Delivery.Format, format.Delivery.State, format.Delivery.RuntimeApplicability.Count,
                format.Runtime))
            .ShouldBe([(RomDeliveryFormat.Original, RomDeliveryState.Available, 0, (ConsumerReleaseRuntime?)null),
                (RomDeliveryFormat.Chd, RomDeliveryState.Available, 0, null)]);
        item.Formats![1].Delivery.Target!.ReadBinding.ShouldBe(evidence.ChdEvidence.Single().Bind());
    }

    /// <summary>The production serving authority over a synthetic resident build of the evidence's decoder tuple.</summary>
    private static ChdDeliveryAdmission ResidentAdmission(RomChdDecoderBuild decoder)
    {
        var native = new ChdNativeIdentity(decoder.Rid, "libmoonlark_chdr.so", 100, new string('c', 64),
            decoder.UpstreamCommit.ToString(), "test-only-upstream", decoder.RecipeVersion, "test-only-family",
            new string('e', 64), true, true, true);
        var candidate = new ChdNativeCandidate("/synthetic/manifest-native", native);
        return new ChdDeliveryAdmission(() => true, () => candidate, () => { }, _ => native);
    }

    private async Task<ConsumerReleaseManifest> GetChdManifestAsync(IRomChdDeliveryAdmission? admission = null,
        IRomContentCatalog? catalog = null)
    {
        await using var db = CreateDb();
        var repository = new ConsumerReleaseManifestRepository(db, RomMediaPolicyComposition.CreateDeliveryPolicy(), catalog ?? MediaTestServices.CreateContentCatalog(db), new Romd.Persistence.MediaFacts.DerivedDescriptorReader(db), admission);
        var result = await repository.GetManifestAsync(new(new ConsumerLibraryScope(UserId), ReleaseId));
        var found = result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found>();
        found.LibraryId.ShouldBe(LibraryId);
        return found.Value;
    }

    private async Task SetPlatformKeyAsync(string systemKey)
    {
        await using var db = CreateDb();
        await db.Platforms.Where(platform => platform.Id == PlatformId).ExecuteUpdateAsync(update => update
            .SetProperty(platform => platform.CanonicalKey, systemKey)
            .SetProperty(platform => platform.ShortName, systemKey));
    }

    /// <summary>Retains a 20-byte RVZ artifact for the first seeded ROM; an unmeasured one is held but not deliverable.</summary>
    private async Task SeedRvzAlternateAsync(bool measured = true)
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var key = await db.Platforms.Where(row => row.Id == PlatformId).Select(row => row.CanonicalKey).SingleAsync();
        uint discType = key == "wii" ? 2u : 1u;
        db.Files.Add(new FileEntityPersistence { Id = 2, Sha256 = NewSha256(2), Size = 20, SizeOnDisk = 20, ArtifactMd5 = NewMd5(2), ArtifactSha1 = NewSha1(2), CreatedAt = now });
        await db.SaveChangesAsync();
        await ConsumerMediaFixture.PublishAsync(db, 1, 2, new RomContainerFacts.Rvz(0x01000000, discType, 5, 19, 131072, discType == 2, 64),
            measured ? new RomDecoderIdentity.Managed(ConsumerMediaFixture.ManagedBuild) : null, false, now);
    }

    private static RomChdDecoderBuild NativeDecoder => new("linux-x64", NewSha1(7), 1);
    private async Task<RomContentArtifact> SeedPs2ChdAsync(bool preferred = true, bool retainEvidence = true,
        string? missingArtifactHash = null)
    {
        SeedReleaseFilesWithSize(8192, ("folder/disc.iso", true));
        await using var db = CreateDb();
        await db.Platforms.Where(row => row.Id == PlatformId).ExecuteUpdateAsync(update => update.SetProperty(row => row.CanonicalKey, "ps2").SetProperty(row => row.ShortName, "ps2"));
        var observed = new DateTimeOffset(2026, 10, 11, 1, 2, 3, TimeSpan.Zero).AddTicks(42);
        uint hunk = 4096u;
        uint codec = RomChdCodecs.Zlib;
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
        public int AssessCount { get; private set; }
        public RomChdDeliveryEvidence? LastEvidence { get; private set; }
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) { AssessCount++; LastEvidence = evidence; return _inner.Assess(evidence); }
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => _inner.AssessManaged(artifact);
        public bool AdmitsCd(RomReconstruction.CdMember member) => _inner.AdmitsCd(member);
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => _inner.AssessCd(member);
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
        await using var db = CreateDb();
        var repository = new ConsumerReleaseManifestRepository(db, RomMediaPolicyComposition.CreateDeliveryPolicy(), MediaTestServices.CreateContentCatalog(db), new Romd.Persistence.MediaFacts.DerivedDescriptorReader(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));

        var result = await repository.GetManifestAsync(
            new ConsumerReleaseManifestRequest(new ConsumerLibraryScope(UserId), releaseId));

        return result;
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
}
