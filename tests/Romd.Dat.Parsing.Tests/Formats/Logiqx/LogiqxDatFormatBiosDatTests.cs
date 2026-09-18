using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Parsing.Formats.Logiqx;
using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests.Formats.Logiqx;

public class LogiqxDatFormatBiosDatTests
{
    private readonly LogiqxDatFormat _format = new(new NullLogger<LogiqxDatFormat>());

    // A Redump-style BIOS DAT in Logiqx XML: the entries carry no per-game BIOS marker;
    // BIOS-ness is conveyed only by the header name "... - BIOS Images".
    private const string BiosDatXml =
        """
        <?xml version="1.0"?>
        <datafile>
          <header>
            <name>Sony - PlayStation 2 - BIOS Images</name>
            <description>Sony - PlayStation 2 - BIOS Images</description>
            <author>redump.org</author>
          </header>
          <game name="ps2-0100j-20000117">
            <description>SCPH-10000 (Version 5.0 01/17/00 T)</description>
            <rom name="ps2-0100j-20000117.bin" size="4194304" crc="b7ef81a9" sha1="aea061e6e263fdcc1c4fdbd68553ef78dae74263"/>
          </game>
          <game name="ps2-0101j-20000217">
            <description>SCPH-10000/SCPH-15000 (Version 5.0 02/17/00 T)</description>
            <rom name="ps2-0101j-20000217.bin" size="4194304" crc="211dfb6a" sha1="916e02431bcd73140504da3355c9598143b77e11"/>
          </game>
        </datafile>
        """;

    private const string GamesDatXml =
        """
        <?xml version="1.0"?>
        <datafile>
          <header>
            <name>Sony - PlayStation 2</name>
            <description>Sony - PlayStation 2</description>
            <author>redump.org</author>
          </header>
          <game name="Some Game (USA)">
            <description>Some Game (USA)</description>
            <rom name="Some Game (USA).bin" size="100" sha1="aea061e6e263fdcc1c4fdbd68553ef78dae74263"/>
          </game>
        </datafile>
        """;

    [Fact]
    public async Task ParseGamesAsync_DedicatedBiosDat_DoesNotApplyWholeDatClassification()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(BiosDatXml));

        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        games.Count.ShouldBe(2);
        games.ShouldAllBe(g => !g.IsError);
        games.ShouldAllBe(g => !g.Value.IsBios);
    }

    [Fact]
    public async Task ParseGamesAsync_RegularGamesDat_DoesNotFlagBios()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(GamesDatXml));

        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        games.Count.ShouldBe(1);
        games[0].Value.IsBios.ShouldBeFalse();
    }

    [Fact]
    public async Task DatParser_DedicatedBiosDat_FlagsAllEntriesAsBios()
    {
        var parser = new DatParser([_format]);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(BiosDatXml));

        var games = await parser.StreamGamesAsync(stream).ToListAsync();

        games.Count.ShouldBe(2);
        games.ShouldAllBe(game => game.Value.IsBios);
    }
}
