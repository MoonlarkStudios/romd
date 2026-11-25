using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Formats.Logiqx;
using Romd.Dat.Models;
using Shouldly;
using Xunit;

namespace Romd.Dat.Tests.Formats.Logiqx;

public sealed class LogiqxDatWriterTests
{
    private static readonly ParsedHeader Header = new()
    {
        Name = "Custom catalog",
        Description = "Custom catalog"
    };

    [Fact]
    public async Task WriteAsync_StreamsAuthoredEntriesInOrder_LeavesNonSeekableDestinationOpen()
    {
        await using var destination = new NonSeekableDestination();
        int enumerations = 0;
        var first = Game("First", new ParsedRom { Name = "file.bin", DeclaredSize = 42, Crc = "AABBCCDD" });
        var second = Game("Second") with { CloneOf = first.Name };
        var entries = new[] { second, first }.Select(game =>
        {
            enumerations++;
            return game;
        });

        await LogiqxDatWriter.WriteAsync(destination, Header, entries.ToAsyncEnumerable());

        enumerations.ShouldBe(2);
        destination.CanWrite.ShouldBeTrue();
        using var output = new MemoryStream(destination.ToArray(), writable: false);
        var games = XDocument.Load(output).Root!.Elements("game").ToArray();
        games.Select(game => game.Attribute("name")?.Value).ShouldBe(["Second", "First"]);
        games[0].Attribute("cloneof")?.Value.ShouldBe("First");
        games[1].Element("rom")?.Attribute("crc")?.Value.ShouldBe("aabbccdd");
    }

    [Fact]
    public async Task WriteAsync_PreCancelled_LeavesDestinationUnwritten()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var destination = new MemoryStream();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            LogiqxDatWriter.WriteAsync(destination, Header, new[] { Game("Game") }.ToAsyncEnumerable(), cancellation.Token));

        destination.Length.ShouldBe(0L);
        destination.CanWrite.ShouldBeTrue();
    }

    [Fact]
    public async Task WriteAsync_CancelledWhileReadingEntries_LeavesIncompleteOutputAndAnOpenDestination()
    {
        using var cancellation = new CancellationTokenSource();
        await using var destination = new MemoryStream();

        async IAsyncEnumerable<ParsedGame> Entries([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Game("First");
            await Task.Yield();
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
        }

        await Should.ThrowAsync<OperationCanceledException>(() =>
            LogiqxDatWriter.WriteAsync(destination, Header, Entries(), cancellation.Token));

        destination.CanWrite.ShouldBeTrue();
        Encoding.UTF8.GetString(destination.ToArray()).ShouldNotContain("</datafile>");
    }

    [Theory]
    [InlineData(-1L, "nodump", null)]
    [InlineData(null, "good", null)]
    [InlineData(1L, "good", "broken")]
    public async Task WriteAsync_UnrepresentableRom_RejectsInvalidDeclaration(long? size, string status, string? crc)
    {
        var game = Game("Game", new ParsedRom { Name = "payload", DeclaredSize = size, Status = status, Crc = crc });
        await using var destination = new MemoryStream();

        await Should.ThrowAsync<InvalidDataException>(() =>
            LogiqxDatWriter.WriteAsync(destination, Header, new[] { game }.ToAsyncEnumerable()));
    }

    [Fact]
    public async Task WriteAsync_MalformedParsedNodumpSize_RejectsTheDocument()
    {
        const string input = """
            <datafile><header><name>Catalog</name></header>
              <game name="Game"><rom name="file" size="broken" status="nodump"/></game>
            </datafile>
            """;
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input), writable: false);
        var format = new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance);
        var game = (await format.ParseGamesAsync(source, default).SingleAsync()).Value;
        game.Roms.Single().Size.ShouldBe(0L);
        await using var destination = new MemoryStream();

        await Should.ThrowAsync<InvalidDataException>(() =>
            LogiqxDatWriter.WriteAsync(destination, Header, new[] { game }.ToAsyncEnumerable()));
    }

    [Theory]
    [InlineData("""<rom name="payload" size="1" crc="broken" md5="11111111111111111111111111111111"/>""")]
    [InlineData("""<disk name="payload" status="nodump" sha1="broken"/>""")]
    public async Task WriteAsync_MalformedParsedChecksum_RejectsTheDocument(string file)
    {
        string input = $"""<datafile><header><name>Catalog</name></header><game name="Game">{file}</game></datafile>""";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input), writable: false);
        var format = new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance);
        var game = (await format.ParseGamesAsync(source, default).SingleAsync()).Value;
        await using var destination = new MemoryStream();

        var error = await Should.ThrowAsync<InvalidDataException>(() =>
            LogiqxDatWriter.WriteAsync(destination, Header, new[] { game }.ToAsyncEnumerable()));

        error.Message.ShouldContain("malformed checksum declaration");
    }

    [Fact]
    public async Task WriteAsync_DuplicateEntryNames_RejectsAnAmbiguousCatalog()
    {
        await using var destination = new MemoryStream();
        var entries = new[] { Game("Duplicate"), Game("Duplicate") };

        await Should.ThrowAsync<InvalidDataException>(() =>
            LogiqxDatWriter.WriteAsync(destination, Header, entries.ToAsyncEnumerable()));
    }

    private static ParsedGame Game(string name, params ParsedRom[] roms) => new()
    {
        Name = name,
        NameMetadata = DatNameParser.Parse(name),
        Roms = roms
    };

    private sealed class NonSeekableDestination : MemoryStream
    {
        public override bool CanSeek => false;
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
