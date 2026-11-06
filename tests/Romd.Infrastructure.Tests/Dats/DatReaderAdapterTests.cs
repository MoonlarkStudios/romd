using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Dats;

public sealed class DatReaderAdapterTests
{
    private const string DatXml =
        """
        <?xml version="1.0"?>
        <datafile>
          <header>
            <name>Nintendo - Nintendo 64</name>
            <description>Nintendo 64</description>
            <author>No-Intro</author>
          </header>
          <game name="Test Game (USA) (Rev A)">
            <rom name="test.z64" size="1024" crc="AABBCCDD" md5="AABBCCDD00112233AABBCCDD00112233" sha1="AABBCCDD00112233AABBCCDD00112233AABBCCDD"/>
          </game>
        </datafile>
        """;

    private readonly DatReader _reader;

    public DatReaderAdapterTests()
    {
        var parser = new DatParser([
            new LogiqxDatFormat(new NullLogger<LogiqxDatFormat>())
        ]);
        _reader = new DatReader(parser, parser);
    }

    [Fact]
    public async Task ReadHeaderAsync_NeutralMetadata_MapsToApplicationContract()
    {
        await using var stream = CreateStream();

        var result = await _reader.ReadHeaderAsync(stream);

        result.IsError.ShouldBeFalse();
        result.Value.Name.ShouldBe("Nintendo - Nintendo 64");
        result.Value.Description.ShouldBe("Nintendo 64");
        result.Value.DatType.ShouldBe(DatType.NoIntro);
    }

    [Fact]
    public async Task StreamGamesAsync_NeutralEntry_MapsToDomainGraphAndInjectsDatFileId()
    {
        await using var stream = CreateStream();

        var results = await _reader.StreamGamesAsync(stream, 42).ToListAsync();

        results.Count.ShouldBe(1);
        results[0].IsError.ShouldBeFalse();
        var game = results[0].Value;
        game.DatFileId.ShouldBe(42);
        game.Region.ShouldBe("USA");
        game.Revision.ShouldBe("Rev A");
        game.Category.ShouldBe("Game");
        var rom = game.Roms.Single();
        rom.Crc.ShouldBe(Crc32.Parse("aabbccdd"));
        rom.Md5.ShouldBe(Md5.Parse("aabbccdd00112233aabbccdd00112233"));
        rom.Sha1.ShouldBe(Sha1.Parse("aabbccdd00112233aabbccdd00112233aabbccdd"));
    }

    private static MemoryStream CreateStream() => new(Encoding.UTF8.GetBytes(DatXml));
}
