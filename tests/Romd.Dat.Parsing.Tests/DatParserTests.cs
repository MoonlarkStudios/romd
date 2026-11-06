using System.Runtime.CompilerServices;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Dat.Parsing.Models;
using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests;

public class DatParserTests
{
    private readonly DatParser _parser;

    public DatParserTests()
    {
        // Wire up the reader with the actual Logiqx format strategy
        var logiqxFormat = new LogiqxDatFormat(new NullLogger<LogiqxDatFormat>());
        _parser = new DatParser([logiqxFormat]);
    }

    [Theory]
    [InlineData(
        "<?xml version=\"1.0\"?><datafile><header><name>T</name><description>D</description></header></datafile>")]
    [InlineData("<datafile><header><name>T</name><description>D</description></header></datafile>")]
    [InlineData(
        "<!DOCTYPE datafile PUBLIC \"-//Logiqx//DTD ROM Management Datafile//EN\" \"http://www.logiqx.com/Dats/datafile.dtd\"><datafile><header><name>T</name><description>D</description></header></datafile>")]
    [InlineData("<dat><header><name>T</name><description>D</description></header></dat>")] // Alternative root element
    public async Task ReadHeaderAsync_XmlFormats_DetectsAndParsesHeader(string content)
    {
        // Arrange
        using var stream = CreateStream(content);
        // Act
        var result = await _parser.ReadHeaderAsync(stream);
        // Assert
        result.IsError.ShouldBeFalse();
        result.Value.ShouldNotBeNull();
        result.Value.Name.ShouldBe("T");
    }

    [Fact]
    public async Task ReadHeaderAsync_UnknownFormat_ReturnsError()
    {
        // Arrange
        using var stream = CreateStream("This is not a DAT file at all");
        // Act
        var result = await _parser.ReadHeaderAsync(stream);
        // Assert
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Dat.UnknownFormat");
    }

    [Fact]
    public async Task ReadHeaderAsync_NonSeekableStream_ReturnsError()
    {
        // Arrange
        await using var nonSeekableStream = new NonSeekableStream();
        // Act
        var result = await _parser.ReadHeaderAsync(nonSeekableStream);

        // Assert
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Dat.StreamNotSeekable");
    }

    [Fact]
    public async Task ReadHeaderAsync_CancellationDuringDetection_PropagatesCancellation()
    {
        using var stream = CreateStream("<datafile><header><name>T</name></header></datafile>");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            _parser.ReadHeaderAsync(stream, cancellation.Token));
    }

    [Fact]
    public async Task StreamGamesAsync_ComposesProvenanceAndWholeDatBiosAtParserLevel()
    {
        var format = new RecordingFormat();
        var parser = new DatParser([format]);
        using var headerStream = CreateStream("accepted DAT content");
        using var gamesStream = CreateStream("accepted DAT content");

        var headerResult = await parser.ReadHeaderAsync(headerStream);
        var gameResults = await parser.StreamGamesAsync(gamesStream).ToListAsync();

        headerResult.IsError.ShouldBeFalse();
        headerResult.Value.Provenance.ShouldBe(DatProvenance.Redump);
        gameResults.Single().Value.IsBios.ShouldBeTrue();
        format.ObservedPrefixes.ShouldAllBe(prefix => prefix == "accepted DAT content");
        format.HeaderPositions.ShouldAllBe(position => position == 0);
        format.GamesPosition.ShouldBe(0);
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

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingFormat : IDatFormat
    {
        public List<string> ObservedPrefixes { get; } = [];
        public List<long> HeaderPositions { get; } = [];
        public long GamesPosition { get; private set; } = -1;

        public bool CanRead(ReadOnlySpan<byte> prefix)
        {
            ObservedPrefixes.Add(System.Text.Encoding.UTF8.GetString(prefix));
            return true;
        }

        public Task<ErrorOr<ParsedHeader>> ParseHeaderAsync(Stream stream, CancellationToken ct)
        {
            HeaderPositions.Add(stream.Position);
            return Task.FromResult<ErrorOr<ParsedHeader>>(new ParsedHeader
            {
                Name = "Sony - PlayStation 2 - BIOS Images",
                Description = "BIOS",
                Author = "redump.org"
            });
        }

        public async IAsyncEnumerable<ErrorOr<ParsedGame>> ParseGamesAsync(
            Stream stream,
            [EnumeratorCancellation] CancellationToken ct)
        {
            GamesPosition = stream.Position;
            ct.ThrowIfCancellationRequested();
            yield return new ParsedGame
            {
                Name = "BIOS entry",
                NameMetadata = DatNameParser.Parse("BIOS entry"),
                IsBios = false,
                Roms = []
            };
            await Task.CompletedTask;
        }
    }
}
