using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Formats.Logiqx;
using Romd.Dat.Models;
using Shouldly;
using Xunit;

namespace Romd.Dat.Tests.Formats.Logiqx;

public sealed class LogiqxDatWriterRoundTripTests
{
    private const string ChildName = "Jeu Café & \"雪\" (France) (Fr) (Rev 2) [!]";
    private const string ParentName = "Parent & Périphérique";
    private const string ParentId = " \t0003\r\n ";
    private const string FirmwareName = "Firmware & co.";
    private const string SyntheticDat =
        """
        <datafile>
          <header>
            <name>MAME Café &amp; &lt;arcade&gt;</name>
            <description>Catalogue «original»</description>
            <version></version>
            <author>Équipe &amp; co.</author>
            <homepage>https://example.test/catalog?region=FR&amp;lang=fr</homepage>
            <date>2026-01-01</date>
            <email>dat@example.test</email>
            <comment>&lt;archive&gt; &amp; tests&#xD;&#xA;</comment>
          </header>
          <machine name="Jeu Café &amp; &quot;雪&quot; (France) (Fr) (Rev 2) [!]"
                   id=" child-0007 " cloneof="Parent &amp; Périphérique"
                   cloneofid=" &#x9;0003&#xD;&#xA; " romof="Firmware &amp; co." isbios="no">
            <description>Édition &lt;intégrale&gt;</description>
            <year>1998</year>
            <manufacturer>AT&amp;T</manufacturer>
            <category>Games</category>
            <category>Preproduction</category>
            <rom name="Mémoire &amp; &quot;雪&quot;.rom" size="9223372036854775807"
                 crc="AABBCCDD" md5="AABBCCDD00112233AABBCCDD00112233"
                 sha1="AABBCCDD00112233AABBCCDD00112233AABBCCDD" status="baddump" serial="É &amp; 0100"/>
            <rom name="known-zero.rom" size="0" status="nodump"/>
            <rom name="unknown.rom" status="nodump"/>
            <disk name="Disque &amp; &quot;雪&quot;" md5="AABBCCDD00112233AABBCCDD00112233"
                  sha1="AABBCCDD00112233AABBCCDD00112233AABBCCDD" status="good"/>
          </machine>
          <machine name="Parent &amp; Périphérique" id=" &#x9;0003&#xD;&#xA; ">
            <description> &#x9; </description>
          </machine>
          <machine name="Firmware &amp; co." isbios="YeS"><category>BIOS Firmware</category></machine>
          <game name="Duplicate first" id="duplicate"/>
          <game name="Duplicate second" id="duplicate"/>
          <game name="Ambiguous child" cloneofid="duplicate"/>
          <game name="Empty fields" id="" cloneof="" cloneofid="  " romof="" isbios="">
            <description/>
            <category/>
          </game>
        </datafile>
        """;

    private readonly DatParser _parser = new([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);

    [Theory]
    [InlineData(true, 21, DatProvenance.NoIntro)]
    [InlineData(false, 48, DatProvenance.Redump)]
    public async Task WriteAsync_ParsedProviderFixture_PreservesModeledHeaderAndOrderedEntries(
        bool noIntro,
        int entryCount,
        DatProvenance provenance)
    {
        // Arrange
        await using var source = noIntro ? TestResources.OpenNoIntroN64() : TestResources.OpenRedumpPs1();
        var original = await ReadAsync(source);

        // Act
        byte[] output = await WriteAsync(original.Header, original.Games);
        await using var reread = new MemoryStream(output, writable: false);
        var exported = await ReadAsync(reread);

        // Assert
        exported.Header.Provenance.ShouldBe(provenance);
        exported.Games.Count.ShouldBe(entryCount);
        AssertSameCatalog(original, exported);
    }

    [Fact]
    public async Task WriteAsync_MixedGameAndMachineInput_PreservesDeclarationsAndParentResolution()
    {
        // Arrange
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(SyntheticDat), writable: false);
        var original = await ReadAsync(source);

        // Act
        byte[] output = await WriteAsync(original.Header, original.Games);
        await using var reread = new MemoryStream(output, writable: false);
        var exported = await ReadAsync(reread);

        // Assert
        AssertSameCatalog(original, exported);
        exported.Header.Name.ShouldBe("MAME Café & <arcade>");
        exported.Header.Version.ShouldBe(string.Empty);
        exported.Header.Url.ShouldBe("https://example.test/catalog?region=FR&lang=fr");
        exported.Header.Comment.ShouldBe("<archive> & tests\r\n");

        var child = exported.Games.Single(game => game.Name == ChildName);
        child.DeclaredId.ShouldBe(" child-0007 ");
        child.CloneOf.ShouldBe(ParentName);
        child.CloneOfId.ShouldBe(ParentId);
        child.RomOf.ShouldBe(FirmwareName);
        child.DeclaredCategory.ShouldBe("Preproduction");
        child.BiosDeclaration.ShouldBe("no");
        child.IsBios.ShouldBeFalse();
        child.Roms[0].Name.ShouldBe("Mémoire & \"雪\".rom");
        child.Roms[0].Crc.ShouldBe("aabbccdd");
        child.Roms[0].Serial.ShouldBe("É & 0100");
        child.Disks.Single().Name.ShouldBe("Disque & \"雪\"");
        child.Disks.Single().Md5.ShouldBe("aabbccdd00112233aabbccdd00112233");
        child.Disks.Single().Status.ShouldBe("good");
        child.Roms[0].DeclaredSize.ShouldBe(long.MaxValue);
        child.Roms[1].DeclaredSize.ShouldBe(0L);
        child.Roms[2].DeclaredSize.ShouldBeNull();
        child.Roms[2].Size.ShouldBe(0L);

        var parent = exported.Games.Single(game => game.Name == ParentName);
        parent.DeclaredId.ShouldBe(ParentId);
        parent.CloneOf.ShouldBeNull();
        parent.Description.ShouldBe(" \t ");
        parent.Roms.ShouldBeEmpty();
        parent.Disks.ShouldBeEmpty();

        var firmware = exported.Games.Single(game => game.Name == FirmwareName);
        firmware.DeclaredCategory.ShouldBe("BIOS Firmware");
        firmware.BiosDeclaration.ShouldBe("YeS");
        firmware.IsBios.ShouldBeTrue();

        var empty = exported.Games.Single(game => game.Name == "Empty fields");
        empty.DeclaredId.ShouldBe(string.Empty);
        empty.CloneOf.ShouldBe(string.Empty);
        empty.CloneOfId.ShouldBe("  ");
        empty.RomOf.ShouldBe(string.Empty);
        empty.DeclaredCategory.ShouldBe(string.Empty);
        empty.BiosDeclaration.ShouldBe(string.Empty);
        empty.Description.ShouldBe(string.Empty);

        var resolutions = ResolveParents(exported.Games);
        resolutions[ChildName].ShouldBe(new DatParentResolution(DatParentStatus.Resolved, ParentName));
        resolutions["Ambiguous child"].ShouldBe(new DatParentResolution(DatParentStatus.Ambiguous, null));
        resolutions["Empty fields"].ShouldBe(new DatParentResolution(DatParentStatus.Missing, null));
        var originalResolutions = ResolveParents(original.Games);
        resolutions.Count.ShouldBe(originalResolutions.Count);
        foreach (var (name, expected) in originalResolutions)
        {
            resolutions[name].ShouldBe(expected);
        }

        reread.Position = 0;
        var root = XDocument.Load(reread).Root.ShouldNotBeNull();
        root.Name.ShouldBe(XName.Get("datafile"));
        root.Elements().First().Name.ShouldBe(XName.Get("header"));
        root.Elements().Skip(1).ShouldAllBe(element => element.Name == XName.Get("game"));
        var roms = root.Elements("game").First().Elements("rom").ToArray();
        roms[0].Attribute("size").ShouldNotBeNull().Value.ShouldBe("9223372036854775807");
        roms[1].Attribute("size").ShouldNotBeNull().Value.ShouldBe("0");
        roms[2].Attribute("size").ShouldBeNull();

        byte[] repeated = await WriteAsync(original.Header, original.Games);
        repeated.ShouldBe(output);
    }

    private async Task<(ParsedHeader Header, IReadOnlyList<ParsedGame> Games)> ReadAsync(Stream source)
    {
        var header = await _parser.ReadHeaderAsync(source);
        header.IsError.ShouldBeFalse();
        var games = await _parser.StreamGamesAsync(source).ToListAsync();
        games.ShouldAllBe(game => !game.IsError);
        return (header.Value, games.Select(game => game.Value).ToArray());
    }

    private static async Task<byte[]> WriteAsync(ParsedHeader header, IReadOnlyList<ParsedGame> games)
    {
        await using var destination = new MemoryStream();
        await LogiqxDatWriter.WriteAsync(destination, header, games.ToAsyncEnumerable());
        destination.CanWrite.ShouldBeTrue();
        return destination.ToArray();
    }

    private static void AssertSameCatalog(
        (ParsedHeader Header, IReadOnlyList<ParsedGame> Games) expected,
        (ParsedHeader Header, IReadOnlyList<ParsedGame> Games) actual)
    {
        actual.Header.ShouldBe(expected.Header);
        actual.Games.Count.ShouldBe(expected.Games.Count);
        foreach (var (before, after) in expected.Games.Zip(actual.Games))
        {
            // Compare entry values independently of the list references included in record equality.
            (after with { Roms = before.Roms, Disks = before.Disks }).ShouldBe(before);
            after.Roms.ToArray().ShouldBe(before.Roms.ToArray());
            after.Disks.ToArray().ShouldBe(before.Disks.ToArray());
        }
    }

    private static IReadOnlyDictionary<string, DatParentResolution> ResolveParents(IReadOnlyList<ParsedGame> games) =>
        DatParentResolver.Resolve(games
            .Select(game => new DatParentDeclaration(game.Name, game.DeclaredId, game.CloneOf, game.CloneOfId))
            .ToArray());
}
