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
            (result.Value.Author ?? string.Empty).ShouldContain("Synthetic Dumper");
            result.Value.Homepage.ShouldBe("No-Intro");
            (result.Value.Url ?? string.Empty).ShouldContain("No-Intro");
            result.Value.Version.ShouldNotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task ParseGamesAsync_ParsesAllGamesWithoutErrors()
        {
            await using var stream = TestResources.OpenNoIntroN64();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            games.Count.ShouldBe(21);
            games.ShouldAllBe(g => !g.IsError);
        }

        [Fact]
        public async Task ParseGamesAsync_SingleRomCartridge_HasExpectedData()
        {
            await using var stream = TestResources.OpenNoIntroN64();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            var cartridge = games
                .Where(g => !g.IsError)
                .Select(g => g.Value)
                .FirstOrDefault(g => g.Name == "Synthetic Star Quest 64 (USA)");

            cartridge.ShouldNotBeNull();
            cartridge.Roms.Count.ShouldBe(1);

            var rom = cartridge.Roms.First();
            rom.Name.ShouldEndWith(".z64");
            rom.Size.ShouldBeGreaterThan(0);
            rom.Sha1.ShouldNotBeNull();
            rom.Crc.ShouldNotBeNull();
            rom.Status.ShouldBe("verified");
            rom.Serial.ShouldBe("NSQE");
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

            biosEntries.Count.ShouldBe(3);
            biosEntries.ShouldAllBe(b => b.IsBios && b.Roms.Count >= 1);
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

            games.Count.ShouldBe(48);
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
        public async Task ParseGamesAsync_MultiDiscSet_HasExpectedStructure()
        {
            await using var stream = TestResources.OpenRedumpPs1();

            var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

            var firstDisc = games
                .Where(g => !g.IsError)
                .Select(g => g.Value)
                .FirstOrDefault(g => g.Name.Contains("Synthetic Saga VII")
                                     && g.Name.Contains("USA")
                                     && g.Name.Contains("Disc 1"));

            firstDisc.ShouldNotBeNull();
            firstDisc.Roms.ShouldNotBeEmpty();
            firstDisc.Roms.ShouldContain(r => r.Name.EndsWith(".cue"));
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
