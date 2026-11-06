using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Parsing.Formats.Logiqx;
using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests.Formats.Logiqx;

public class LogiqxDatFormatTests
{
    private readonly LogiqxDatFormat _format = new(new NullLogger<LogiqxDatFormat>());

    [Fact]
    public async Task ParseHeaderAsync_ValidNoIntroDat_ReturnsDatFileEntity()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <!DOCTYPE datafile PUBLIC "-//Logiqx//DTD ROM Management Datafile//EN" "http://www.logiqx.com/Dats/datafile.dtd">
                     <datafile>
                       <header>
                         <name>Nintendo - Super Nintendo Entertainment System</name>
                         <description>Nintendo - Super Nintendo Entertainment System</description>
                         <version>20250101-000000</version>
                         <author>No-Intro</author>
                         <homepage>https://no-intro.org</homepage>
                       </header>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var result = await _format.ParseHeaderAsync(stream, CancellationToken.None);

        // Assert
        result.IsError.ShouldBeFalse();
        var datFile = result.Value;

        datFile.Name.ShouldBe("Nintendo - Super Nintendo Entertainment System");
        datFile.Version.ShouldBe("20250101-000000");
        datFile.Author.ShouldBe("No-Intro");
        datFile.Url.ShouldBe("https://no-intro.org");
    }

    [Fact]
    public async Task ParseGamesAsync_NegativeRomSize_FallsBackToZero()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>Test DAT</name>
                       </header>
                       <game name="Corrupt Game">
                         <rom name="corrupt.sfc" size="-524288" sha1="0000000000000000000000000000000000000001"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        games.Count.ShouldBe(1);
        games[0].IsError.ShouldBeFalse();
        games[0].Value.Roms.Single().Size.ShouldBe(0);
    }

    [Fact]
    public async Task ParseGamesAsync_SingleGameWithSingleRom_ReturnsDatGameEntity()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>Test DAT</name>
                         <description>Test DAT</description>
                       </header>
                       <game name="Super Mario World (USA)">
                         <description>Super Mario World (USA)</description>
                         <rom name="Super Mario World (USA).sfc" size="524288" crc="B19ED489" md5="CDD3C8C37322978CA8669B34BC89C804" sha1="6B47BB75D16514B6A476AA0C73A683A2A4C18765"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        games.Count.ShouldBe(1);
        games[0].IsError.ShouldBeFalse();

        var game = games[0].Value;
        game.Name.ShouldBe("Super Mario World (USA)");
        game.Description.ShouldBe("Super Mario World (USA)");
        game.NameMetadata.Region.ShouldBe("USA");
        game.NameMetadata.Category.ShouldBe("Game");
        game.Roms.Count.ShouldBe(1);

        var rom = game.Roms.First();
        rom.Name.ShouldBe("Super Mario World (USA).sfc");
        rom.Size.ShouldBe(524288);
        rom.Crc.ShouldBe("b19ed489");
        rom.Md5.ShouldBe("cdd3c8c37322978ca8669b34bc89c804");
        rom.Sha1.ShouldBe("6b47bb75d16514b6a476aa0c73a683a2a4c18765");
    }

    [Fact]
    public async Task ParseGamesAsync_MultipleGames_StreamsAll()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>Test DAT</name>
                         <description>Test DAT</description>
                       </header>
                       <game name="Game One">
                         <rom name="game1.sfc" size="1024" sha1="0000000000000000000000000000000000000001"/>
                       </game>
                       <game name="Game Two">
                         <rom name="game2.sfc" size="2048" sha1="0000000000000000000000000000000000000002"/>
                       </game>
                       <game name="Game Three">
                         <rom name="game3.sfc" size="4096" sha1="0000000000000000000000000000000000000003"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        games.Count.ShouldBe(3);
        games.All(g => !g.IsError).ShouldBeTrue();
        games[0].Value.Name.ShouldBe("Game One");
        games[1].Value.Name.ShouldBe("Game Two");
        games[2].Value.Name.ShouldBe("Game Three");
    }

    [Fact]
    public async Task ParseGamesAsync_GameWithMultipleRoms_ParsesAllRoms()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>Test DAT</name>
                         <description>Test DAT</description>
                       </header>
                       <game name="Final Fantasy VII (USA) (Disc 1)">
                         <description>Final Fantasy VII (USA) (Disc 1)</description>
                         <rom name="Final Fantasy VII (USA) (Disc 1).bin" size="734701968" sha1="0000000000000000000000000000000000000123"/>
                         <rom name="Final Fantasy VII (USA) (Disc 1).cue" size="93" sha1="0000000000000000000000000000000000000456"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        games.Count.ShouldBe(1);
        games[0].Value.Roms.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ParseHeaderAsync_MissingHeader_ReturnsError()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <game name="Some Game">
                         <rom name="game.rom" size="1024" sha1="abc123abc123abc123abc123abc123abc123abc1"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var result = await _format.ParseHeaderAsync(stream, CancellationToken.None);

        // Assert
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Dat.MissingRequiredElement");
    }

    [Fact]
    public async Task ParseHeaderAsync_InvalidXml_ReturnsError()
    {
        // Arrange
        string xml = "not valid xml at all <>";
        using var stream = CreateStream(xml);

        // Act
        var result = await _format.ParseHeaderAsync(stream, CancellationToken.None);

        // Assert
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Dat.ParseFailed");
    }

    [Fact]
    public async Task ParseGamesAsync_MachineElement_ParsesLikeMame()
    {
        // Arrange - MAME uses <machine> instead of <game>
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>MAME DAT</name>
                         <description>MAME DAT</description>
                       </header>
                       <machine name="pacman">
                         <description>Pac-Man</description>
                         <year>1980</year>
                         <manufacturer>Namco</manufacturer>
                         <rom name="pacman.6e" size="4096" crc="c1e6ab10" sha1="0000000000000000000000000000000000000123"/>
                       </machine>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        games.Count.ShouldBe(1);
        games[0].Value.Name.ShouldBe("pacman");
        games[0].Value.Year.ShouldBe("1980");
        games[0].Value.Manufacturer.ShouldBe("Namco");
    }

    [Fact]
    public async Task ParseGamesAsync_CloneOfAttribute_ParsesCorrectly()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>Test DAT</name>
                         <description>Test DAT</description>
                       </header>
                       <game name="Super Mario World (Europe)" cloneof="Super Mario World (USA)">
                         <rom name="Super Mario World (Europe).sfc" size="524288" sha1="0000000000000000000000000000000000000123"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        games[0].Value.CloneOf.ShouldBe("Super Mario World (USA)");
    }

    [Fact]
    public async Task ParseGamesAsync_RomStatus_ParsesCorrectly()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>Test DAT</name>
                         <description>Test DAT</description>
                       </header>
                       <game name="Test Game">
                         <rom name="good.rom" size="1024" sha1="0000000000000000000000000000000000000001" status="good"/>
                         <rom name="bad.rom" size="1024" sha1="0000000000000000000000000000000000000002" status="baddump"/>
                         <rom name="nodump.rom" size="0" status="nodump"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        var roms = games[0].Value.Roms.ToList(); // ToList because IEnumerable
        roms[0].Status.ShouldBe("good");
        roms[1].Status.ShouldBe("baddump");
        roms[2].Status.ShouldBe("nodump");
    }

    [Fact]
    public async Task ParseGamesAsync_HashNormalization_ConvertsToLowercase()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>Test</name>
                         <description>Test</description>
                       </header>
                       <game name="Test">
                         <rom name="test.rom" size="1024" crc="AABBCCDD" md5="AABBCCDD00112233AABBCCDD00112233" sha1="AABBCCDD00112233AABBCCDD00112233AABBCCDD"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        var rom = games[0].Value.Roms.First();
        rom.Crc.ShouldBe("aabbccdd");
        rom.Md5.ShouldBe("aabbccdd00112233aabbccdd00112233");
        rom.Sha1.ShouldBe("aabbccdd00112233aabbccdd00112233aabbccdd");
    }

    [Fact]
    public async Task ParseGamesAsync_DiskElements_ParsesCorrectly()
    {
        // Arrange
        string xml = """
                     <?xml version="1.0"?>
                     <datafile>
                       <header>
                         <name>Test DAT</name>
                         <description>Test DAT</description>
                       </header>
                       <game name="Ridge Racer (USA)">
                         <description>Ridge Racer (USA)</description>
                         <disk name="Ridge Racer (USA)" sha1="abc123def456abc123def456abc123def456abc1"/>
                       </game>
                     </datafile>
                     """;
        using var stream = CreateStream(xml);

        // Act
        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        // Assert
        games[0].Value.Disks.Count.ShouldBe(1);
        games[0].Value.Disks.First().Name.ShouldBe("Ridge Racer (USA)");
        games[0].Value.Disks.First().Sha1.ShouldBe("abc123def456abc123def456abc123def456abc1");
    }

    [Fact]
    public async Task ParseGamesAsync_InvalidHashesAndSize_PreservesNullAndZeroFallbacks()
    {
        string xml =
            """
            <datafile>
              <header><name>Test</name></header>
              <game name="Invalid values">
                <rom name="invalid.rom" size="not-a-number" crc="xyz" md5="1234" sha1="not-hex"/>
                <rom name="missing-size.rom"/>
              </game>
            </datafile>
            """;
        using var stream = CreateStream(xml);

        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        var roms = games.Single().Value.Roms.ToList();
        roms.Count.ShouldBe(2);
        roms.ShouldAllBe(rom => rom.Size == 0);
        roms.ShouldAllBe(rom => rom.Crc == null && rom.Md5 == null && rom.Sha1 == null);
    }

    [Fact]
    public async Task ParseGamesAsync_MissingGameAndRomNames_SkipsMalformedEntries()
    {
        string xml =
            """
            <datafile>
              <header><name>Test</name></header>
              <game><rom name="orphan.rom"/></game>
              <game name="Valid"><rom/><rom name="valid.rom"/></game>
            </datafile>
            """;
        using var stream = CreateStream(xml);

        var games = await _format.ParseGamesAsync(stream, CancellationToken.None).ToListAsync();

        games.Count.ShouldBe(1);
        games.Single().Value.Name.ShouldBe("Valid");
        games.Single().Value.Roms.Single().Name.ShouldBe("valid.rom");
    }

    private static MemoryStream CreateStream(string content)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }
}
