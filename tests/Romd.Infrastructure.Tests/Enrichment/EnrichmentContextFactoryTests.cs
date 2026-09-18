using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public class EnrichmentContextFactoryTests
{
    private readonly ITitleEnrichmentEvidenceReader _evidenceReader =
        Substitute.For<ITitleEnrichmentEvidenceReader>();

    private readonly EnrichmentOptions _options = new() { RegionPriority = ["USA", "WORLD", "EUROPE", "JAPAN"] };

    private EnrichmentContextFactory CreateFactory() => new(_evidenceReader, Options.Create(_options));

    private static Title CreateTitle(int id = 1, string name = "Super Mario World")
    {
        return Title.Rehydrate(
            id, 10, name, name.ToLowerInvariant(),
            null, null, null, null,
            null, null, null,
            EnrichmentStatus.None, null,
            DateTimeOffset.UtcNow);
    }

    private static Platform CreatePlatform(string shortName = "snes") =>
        Platform.Rehydrate(10, "Super Nintendo", shortName, "Nintendo", DateTimeOffset.UtcNow);

    private static TitleEnrichmentEvidence CreateEvidence(
        string name,
        string? region = null,
        string? revision = null,
        string? developmentStatus = null,
        string? year = null,
        string? manufacturer = null,
        bool hasDefinedRoms = false,
        IReadOnlyList<OwnedRomHashEvidence>? ownedRomHashes = null) =>
        new(
            name,
            year,
            manufacturer,
            region,
            revision,
            developmentStatus,
            hasDefinedRoms,
            ownedRomHashes ?? []);

    private static OwnedRomHashEvidence CreateOwnedRomEvidence(
        string? sha1 = null, string? md5 = null, string? crc = null, long size = 1024)
        => new(
            sha1 != null ? Sha1.Parse(sha1) : null,
            md5 != null ? Md5.Parse(md5) : null,
            crc != null ? Crc32.Parse(crc) : null,
            size);

    [Fact]
    public async Task CreateAsync_NoGames_ReturnsEmptyContext()
    {
        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        result.TitleName.ShouldBe("Super Mario World");
        result.PlatformShortName.ShouldBe("snes");
        result.KnownHashes.ShouldBeEmpty();
        result.Year.ShouldBeNull();
        result.Region.ShouldBeNull();
    }

    [Fact]
    public async Task CreateAsync_SingleGame_ReturnsThatGame()
    {
        var evidence = CreateEvidence("Super Mario World (USA)", "USA", year: "1990");
        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([evidence]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        result.Year.ShouldBe("1990");
        result.Region.ShouldBe("USA");
    }

    [Fact]
    public async Task CreateAsync_RegionPriority_USAPreferredOverJapan()
    {
        var japanGame = CreateEvidence("Super Mario World (Japan)", "Japan", year: "1990");
        var usaGame = CreateEvidence("Super Mario World (USA)", "USA", year: "1991");

        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([japanGame, usaGame]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        // USA game should be selected (higher region score)
        result.Region.ShouldBe("USA");
        result.Year.ShouldBe("1991");
    }

    [Fact]
    public async Task CreateAsync_VerifiedEntry_PreferredOverOwned()
    {
        var ownedGame = CreateEvidence("Super Mario World (USA)", "USA",
            year: "1991",
            hasDefinedRoms: true,
            ownedRomHashes: [CreateOwnedRomEvidence("da39a3ee5e6b4b0d3255bfef95601890afd80709")]);
        var verifiedGame = CreateEvidence("Super Mario World (USA) [!]", "USA", year: "1990");

        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([ownedGame, verifiedGame]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        // Verified [!] evidence scores +1000, owned scores +500, so the verified year wins.
        result.Year.ShouldBe("1990");
    }

    [Fact]
    public async Task CreateAsync_HashCollection_OnlyOwnedRoms()
    {
        string sha1Hex = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
        var evidence = CreateEvidence(
            "Game (USA)",
            "USA",
            hasDefinedRoms: true,
            ownedRomHashes: [CreateOwnedRomEvidence(sha1Hex, size: 2048)]);
        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([evidence]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        // Only owned ROM's hashes should be collected
        result.KnownHashes.Count.ShouldBe(1);
        result.KnownHashes[0].Sha1.ShouldBe(Sha1.Parse(sha1Hex));
        result.KnownHashes[0].Size.ShouldBe(2048);
    }

    [Fact]
    public async Task CreateAsync_HashCorrelation_PreservesPerRomAssociation()
    {
        string sha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
        string md5 = "d41d8cd98f00b204e9800998ecf8427e";
        string crc = "00000000";
        var rom = CreateOwnedRomEvidence(sha1, md5, crc, 4096);

        var evidence = CreateEvidence("Game", hasDefinedRoms: true, ownedRomHashes: [rom]);
        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([evidence]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        result.KnownHashes.Count.ShouldBe(1);
        var hashSet = result.KnownHashes[0];
        hashSet.Sha1.ShouldBe(Sha1.Parse(sha1));
        hashSet.Md5.ShouldBe(Md5.Parse(md5));
        hashSet.Crc32.ShouldBe(Crc32.Parse(crc));
        hashSet.Size.ShouldBe(4096);
    }

    [Fact]
    public async Task CreateAsync_NoRevision_PreferredOverRevision()
    {
        var revGame = CreateEvidence("Game (USA) (Rev 1)", "USA", "Rev 1", year: "1991");
        var baseGame = CreateEvidence("Game (USA)", "USA", year: "1990");

        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([revGame, baseGame]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        // Base release (no revision) scores +50 more
        result.Region.ShouldBe("USA");
        result.Year.ShouldBe("1990");
    }

    [Fact]
    public async Task CreateAsync_ConfigurableRegionPriority()
    {
        _options.RegionPriority = ["JAPAN", "USA"];

        var usaGame = CreateEvidence("Game (USA)", "USA", year: "1991");
        var japanGame = CreateEvidence("Game (Japan)", "Japan", year: "1990");

        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([usaGame, japanGame]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        // Japan is first in priority now
        result.Region.ShouldBe("Japan");
        result.Year.ShouldBe("1990");
    }

    [Fact]
    public async Task CreateAsync_OwnedRom_PreferredOverUnowned()
    {
        var unownedGame = CreateEvidence("Game (USA)", "USA", year: "1990", hasDefinedRoms: true);
        var ownedGame = CreateEvidence(
            "Game (USA)",
            "USA",
            year: "1991",
            hasDefinedRoms: true,
            ownedRomHashes: [CreateOwnedRomEvidence("da39a3ee5e6b4b0d3255bfef95601890afd80709")]);

        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([unownedGame, ownedGame]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        // Owned game scores +500, wins over unowned
        result.Year.ShouldBe("1991");
    }

    [Fact]
    public async Task CreateAsync_EqualScores_UsesAlphabeticalThenReaderOrder()
    {
        var laterAlphabetically = CreateEvidence("Game B", year: "1991");
        var firstSameName = CreateEvidence("Game A", year: "1990");
        var secondSameName = CreateEvidence("Game A", year: "1989");

        _evidenceReader.ReadAsync(1, Arg.Any<CancellationToken>())
            .Returns([laterAlphabetically, firstSameName, secondSameName]);

        var factory = CreateFactory();
        var result = await factory.CreateAsync(CreateTitle(), CreatePlatform());

        result.Year.ShouldBe("1990");
    }
}
