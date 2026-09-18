using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Parsing.Formats.ClrMamePro;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Dat.Parsing.Models;
using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests.Formats.ClrMamePro;

public class ClrMameProDatFormatTests
{
    private readonly ClrMameProDatFormat _format = new(new NullLogger<ClrMameProDatFormat>());

    // The exact Redump PS2 BIOS sample (clrmamepro text format, dedicated BIOS set).
    private const string RedumpPs2Bios =
        """
        clrmamepro (
        	name "Sony - PlayStation 2 - BIOS Images"
        	description "Sony - PlayStation 2 - BIOS Images (115) (2022-12-12)"
        	category Console
        	version 2022-12-12
        	author "Jackal, AKuHAK | redump.org"
        )

        game (
        	name "ps2-0100jd-20000117"
        	description "DTL-H10000 (Version 5.0 01/17/00 T)"
        	rom ( name ps2-0100jd-20000117.bin size 4194304 crc 5a04500c md5 32f2e4d5ff5ee11072a6bc45530f5765 sha1 5b33170323ed6344e2363fed8115dc3918bb96a4 )
        )

        game (
        	name "ps2-0100j-20000117"
        	description "SCPH-10000 (Version 5.0 01/17/00 T)"
        	rom ( name ps2-0100j-20000117.bin size 4194304 crc b7ef81a9 md5 acf4730ceb38ac9d8c7d8e21f2614600 sha1 aea061e6e263fdcc1c4fdbd68553ef78dae74263 )
        )
        """;

    // A non-BIOS games DAT with a quoted name containing spaces, year, and manufacturer.
    private const string RedumpGames =
        """
        clrmamepro (
        	name "Sega - Mega Drive - Genesis"
        	version 2024-01-01
        	author "redump.org"
        )

        game (
        	name "Sonic the Hedgehog (USA, Europe)"
        	description "Sonic the Hedgehog (USA, Europe)"
        	year 1991
        	manufacturer "Sega"
        	rom ( name "Sonic the Hedgehog (USA, Europe).bin" size 524288 crc deadbeef sha1 da39a3ee5e6b4b0d3255bfef95601890afd80709 )
        )
        """;

    private static MemoryStream Stream(string content) =>
        new(Encoding.UTF8.GetBytes(content));

    [Fact]
    public void CanRead_ClrMameProText_True()
    {
        _format.CanRead(Encoding.UTF8.GetBytes(RedumpPs2Bios)).ShouldBeTrue();
    }

    [Fact]
    public void CanRead_Xml_False()
    {
        _format.CanRead(Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><datafile><header><name>X</name></header></datafile>"))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task ParseHeaderAsync_ReturnsRedumpMetadata()
    {
        await using var stream = Stream(RedumpPs2Bios);

        var result = await _format.ParseHeaderAsync(stream, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Value.Name.ShouldBe("Sony - PlayStation 2 - BIOS Images");
        result.Value.Provenance.ShouldBe(DatProvenance.Unknown);
        result.Value.Version.ShouldBe("2022-12-12");
        (result.Value.Author ?? string.Empty).ShouldContain("redump.org");
    }

    [Fact]
    public async Task ParseGamesAsync_BiosSet_ParsesEntriesWithoutWholeDatClassification()
    {
        await using var stream = Stream(RedumpPs2Bios);

        var games = (await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync())
            .Select(g => g.Value)
            .ToList();

        games.Count.ShouldBe(2);
        games.ShouldAllBe(g => !g.IsBios);

        var first = games[0];
        first.Name.ShouldBe("ps2-0100jd-20000117");
        first.Description.ShouldBe("DTL-H10000 (Version 5.0 01/17/00 T)");
        first.Roms.Count.ShouldBe(1);

        var rom = first.Roms.Single();
        rom.Name.ShouldBe("ps2-0100jd-20000117.bin");
        rom.Size.ShouldBe(4194304);
        rom.Crc.ShouldBe("5a04500c");
        rom.Md5.ShouldBe("32f2e4d5ff5ee11072a6bc45530f5765");
        rom.Sha1.ShouldBe("5b33170323ed6344e2363fed8115dc3918bb96a4");
    }

    [Fact]
    public async Task ParseGamesAsync_NegativeRomSize_FallsBackToZero()
    {
        const string corruptDat =
            """
            clrmamepro (
                name "Corrupt DAT"
            )

            game (
                name "Corrupt Game"
                rom ( name corrupt.bin size -1024 sha1 0000000000000000000000000000000000000001 )
            )
            """;
        await using var stream = Stream(corruptDat);

        var games = (await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync())
            .Select(g => g.Value)
            .ToList();

        games.Count.ShouldBe(1);
        games.Single().Roms.Single().Size.ShouldBe(0);
    }

    [Fact]
    public async Task ParseGamesAsync_GamesDat_ParsesQuotedFieldsAndDoesNotFlagBios()
    {
        await using var stream = Stream(RedumpGames);

        var games = (await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync())
            .Select(g => g.Value)
            .ToList();

        games.Count.ShouldBe(1);
        var game = games.Single();
        game.IsBios.ShouldBeFalse();
        game.Name.ShouldBe("Sonic the Hedgehog (USA, Europe)");
        game.Year.ShouldBe("1991");
        game.Manufacturer.ShouldBe("Sega");
        game.Roms.Single().Name.ShouldBe("Sonic the Hedgehog (USA, Europe).bin");
    }

    [Fact]
    public async Task DatParser_DetectsClrMameProAlongsideLogiqx()
    {
        var reader = new DatParser([
            new LogiqxDatFormat(new NullLogger<LogiqxDatFormat>()),
            _format
        ]);

        await using var stream = Stream(RedumpPs2Bios);
        var result = await reader.ReadHeaderAsync(stream);

        result.IsError.ShouldBeFalse();
        result.Value.Name.ShouldBe("Sony - PlayStation 2 - BIOS Images");
        result.Value.Provenance.ShouldBe(DatProvenance.Redump);
    }

    [Fact]
    public async Task DatParser_DedicatedBiosDat_FlagsAllEntriesAsBios()
    {
        var parser = new DatParser([_format]);
        await using var stream = Stream(RedumpPs2Bios);

        var games = await parser.StreamGamesAsync(stream).ToListAsync();

        games.Count.ShouldBe(2);
        games.ShouldAllBe(game => game.Value.IsBios);
    }
}
