using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Parsing.Formats.Logiqx;
using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests.Formats.Logiqx;

public class LogiqxDatFormatIntegrationTests
{
    private readonly LogiqxDatFormat _format = new(new NullLogger<LogiqxDatFormat>());

    public class NoIntroN64 : LogiqxDatFormatIntegrationTests
    {
        [Fact]
        public async Task ParseHeaderAsync_ReturnsExpectedMetadata()
        {
            await using var stream = TestResources.OpenNoIntroN64();

            var result =
                await _format.ParseHeaderAsync(stream, CancellationToken.None);

            result.IsError.ShouldBeFalse();
            result.Value.Name.ShouldContain("Nintendo 64");
            (result.Value.Author ?? string.Empty).ShouldContain("NESBrew12");
            (result.Value.Url ?? string.Empty).ShouldContain("No-Intro"); // Homepage mapped to Url
            result.Value.Version.ShouldNotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task ParseGamesAsync_ParsesAllGamesWithoutErrors()
        {
            await using var stream = TestResources.OpenNoIntroN64();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            games.ShouldNotBeEmpty();
            games.Count.ShouldBeGreaterThan(500);
            games.ShouldAllBe(g => !g.IsError);
        }

        [Fact]
        public async Task ParseGamesAsync_SuperMario64_HasExpectedData()
        {
            await using var stream = TestResources.OpenNoIntroN64();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            var mario64Usa = games
                .Where(g => !g.IsError)
                .Select(g => g.Value)
                .FirstOrDefault(g => g.Name == "Super Mario 64 (USA)");

            mario64Usa.ShouldNotBeNull();
            mario64Usa.Roms.Count.ShouldBe(1);

            var rom = mario64Usa.Roms.First();
            rom.Name.ShouldEndWith(".z64");
            rom.Size.ShouldBeGreaterThan(0);
            rom.Sha1.ShouldNotBeNull();
            rom.Crc.ShouldNotBeNull();
        }

        [Fact]
        public async Task ParseGamesAsync_BiosEntries_AreParsedCorrectly()
        {
            await using var stream = TestResources.OpenNoIntroN64();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            var biosEntries = games
                .Where(g => !g.IsError)
                .Select(g => g.Value)
                .Where(g => g.Name.StartsWith("[BIOS]"))
                .ToList();

            biosEntries.ShouldNotBeEmpty();
            biosEntries.ShouldAllBe(b => b.Roms.Count >= 1);
        }
    }

    public class RedumpPs1 : LogiqxDatFormatIntegrationTests
    {
        [Fact]
        public async Task ParseHeaderAsync_ReturnsExpectedMetadata()
        {
            await using var stream = TestResources.OpenRedumpPs1();

            var result =
                await _format.ParseHeaderAsync(stream, CancellationToken.None);

            result.IsError.ShouldBeFalse();
            result.Value.Name.ShouldContain("PlayStation");
            (result.Value.Url ?? string.Empty).ShouldContain("redump.org");
            result.Value.Version.ShouldNotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task ParseGamesAsync_ParsesAllGamesWithoutErrors()
        {
            await using var stream = TestResources.OpenRedumpPs1();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            games.ShouldNotBeEmpty();
            games.Count.ShouldBeGreaterThan(500);
            games.ShouldAllBe(g => !g.IsError);
        }

        [Fact]
        public async Task ParseGamesAsync_MultiDiscGame_HasMultipleRoms()
        {
            await using var stream = TestResources.OpenRedumpPs1();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            var multiFileGame = games
                .Where(g => !g.IsError)
                .Select(g => g.Value)
                .FirstOrDefault(g => g.Roms.Count > 1);

            multiFileGame.ShouldNotBeNull();
            multiFileGame.Roms.ShouldContain(r => r.Name.EndsWith(".cue"));
            multiFileGame.Roms.ShouldContain(r => r.Name.EndsWith(".bin"));
        }

        [Fact]
        public async Task ParseGamesAsync_FinalFantasyVII_HasExpectedStructure()
        {
            await using var stream = TestResources.OpenRedumpPs1();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            var ff7Disc1 = games
                .Where(g => !g.IsError)
                .Select(g => g.Value)
                .FirstOrDefault(g => g.Name.Contains("Final Fantasy VII")
                                     && g.Name.Contains("USA")
                                     && g.Name.Contains("Disc 1"));

            ff7Disc1.ShouldNotBeNull();
            ff7Disc1.Roms.ShouldNotBeEmpty();
            ff7Disc1.Roms.ShouldContain(r => r.Name.EndsWith(".cue"));
        }
    }

    public class CrossFormat : LogiqxDatFormatIntegrationTests
    {
        [Fact]
        public async Task BothFormats_HaveConsistentHashLengths()
        {
            var allSha1Hashes = new List<string>();

            await using (var n64Stream = TestResources.OpenNoIntroN64())
            {
                var games = await _format.ParseGamesAsync(n64Stream, CancellationToken.None).ToListAsync();
                allSha1Hashes.AddRange(games
                    .Where(g => !g.IsError)
                    .SelectMany(g => g.Value.Roms)
                    .Where(r => r.Sha1 is not null)
                    .Select(r => r.Sha1!));
            }

            await using (var ps1Stream = TestResources.OpenRedumpPs1())
            {
                var games = await _format.ParseGamesAsync(ps1Stream, CancellationToken.None).ToListAsync();
                allSha1Hashes.AddRange(games
                    .Where(g => !g.IsError)
                    .SelectMany(g => g.Value.Roms)
                    .Where(r => r.Sha1 is not null)
                    .Select(r => r.Sha1!));
            }

            // All SHA1 hashes should be 40 characters (lowercase hex)
            allSha1Hashes.ShouldAllBe(h => h.Length == 40);
            allSha1Hashes.ShouldAllBe(h => h == h.ToLowerInvariant());
        }
    }
}
