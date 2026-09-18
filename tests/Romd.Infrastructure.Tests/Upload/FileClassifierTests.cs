using System.IO.Compression;
using System.Text;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Infrastructure.Upload;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Upload;

public class FileClassifierTests
{
    private readonly IArchiveExtractorResolver _archiveResolver;
    private readonly FileClassifier _sut;

    public FileClassifierTests()
    {
        _archiveResolver = Substitute.For<IArchiveExtractorResolver>();
        _archiveResolver.IsArchive(Arg.Any<string>()).Returns(false);
        _archiveResolver.IsArchive(Arg.Is<string>(s => s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))).Returns(true);
        _sut = new FileClassifier(_archiveResolver);
    }

    #region Archive Detection

    [Fact]
    public async Task ClassifyAsync_ZipByMagicBytes_ReturnsArchive()
    {
        // Arrange
        using var stream = CreateZipStream();

        // Act
        var result = await _sut.ClassifyAsync(stream, "test.bin");

        // Assert
        result.Type.ShouldBe(FileType.Archive);
        result.DetectedFormat.ShouldBe("zip");
        result.Confidence.ShouldBeGreaterThan(0.9f);
    }

    [Fact]
    public async Task ClassifyAsync_ZipByExtension_ReturnsArchiveWithLowerConfidence()
    {
        // Arrange - non-zip content but .zip extension
        using var stream = CreateTextStream("This is not a zip file");

        // Act
        var result = await _sut.ClassifyAsync(stream, "archive.zip");

        // Assert
        result.Type.ShouldBe(FileType.Archive);
        result.Confidence.ShouldBeLessThan(0.9f);
    }

    [Fact]
    public async Task ClassifyAsync_SevenZipMagicBytes_ReturnsArchive()
    {
        // Arrange - 7z magic bytes: 37 7A BC AF 27 1C
        byte[] sevenZipHeader = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x00];
        using var stream = new MemoryStream(sevenZipHeader);

        // Act
        var result = await _sut.ClassifyAsync(stream, "archive.7z");

        // Assert
        result.Type.ShouldBe(FileType.Archive);
        result.DetectedFormat.ShouldBe("7z");
    }

    [Fact]
    public async Task ClassifyAsync_RarMagicBytes_ReturnsArchive()
    {
        // Arrange - RAR magic bytes: 52 61 72 21 1A 07
        byte[] rarHeader = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00];
        using var stream = new MemoryStream(rarHeader);

        // Act
        var result = await _sut.ClassifyAsync(stream, "archive.rar");

        // Assert
        result.Type.ShouldBe(FileType.Archive);
        result.DetectedFormat.ShouldBe("rar");
    }

    [Fact]
    public async Task ClassifyAsync_GzipMagicBytes_ReturnsArchive()
    {
        // Arrange - GZIP magic bytes: 1F 8B
        byte[] gzipHeader = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00];
        using var stream = new MemoryStream(gzipHeader);

        // Act
        var result = await _sut.ClassifyAsync(stream, "archive.gz");

        // Assert
        result.Type.ShouldBe(FileType.Archive);
        result.DetectedFormat.ShouldBe("gzip");
    }

    #endregion

    #region DAT Detection

    [Fact]
    public async Task ClassifyAsync_XmlWithDatafileRoot_ReturnsDat()
    {
        // Arrange
        using var stream = CreateTextStream("<?xml version=\"1.0\"?><datafile><header></header></datafile>");

        // Act
        var result = await _sut.ClassifyAsync(stream, "catalog.dat");

        // Assert
        result.Type.ShouldBe(FileType.Dat);
        result.DetectedFormat.ShouldBe("xml/logiqx");
        result.Confidence.ShouldBeGreaterThan(0.9f);
    }

    [Fact]
    public async Task ClassifyAsync_XmlWithDatRoot_ReturnsDat()
    {
        // Arrange
        using var stream = CreateTextStream("<dat><header></header></dat>");

        // Act
        var result = await _sut.ClassifyAsync(stream, "catalog.xml");

        // Assert
        result.Type.ShouldBe(FileType.Dat);
        result.DetectedFormat.ShouldBe("xml/logiqx");
    }

    [Fact]
    public async Task ClassifyAsync_DoctypeDatafile_ReturnsDat()
    {
        // Arrange
        using var stream = CreateTextStream("<!DOCTYPE datafile PUBLIC \"-//Logiqx//DTD\"><datafile></datafile>");

        // Act
        var result = await _sut.ClassifyAsync(stream, "No-Intro.dat");

        // Assert
        result.Type.ShouldBe(FileType.Dat);
        result.DetectedFormat.ShouldBe("xml/logiqx");
    }

    [Fact]
    public async Task ClassifyAsync_XmlWithoutDatafile_ReturnsDatWithLowerConfidence()
    {
        // Arrange - generic XML that isn't a DAT
        using var stream = CreateTextStream("<?xml version=\"1.0\"?><other><stuff/></other>");

        // Act
        var result = await _sut.ClassifyAsync(stream, "config.xml");

        // Assert
        result.Type.ShouldBe(FileType.Dat);
        result.DetectedFormat.ShouldBe("xml");
        result.Confidence.ShouldBeLessThan(0.9f);
    }

    [Fact]
    public async Task ClassifyAsync_DatExtensionOnly_ReturnsDatWithLowConfidence()
    {
        // Arrange - file with .dat extension but no XML content
        using var stream = CreateTextStream("This is just text, not XML");

        // Act
        var result = await _sut.ClassifyAsync(stream, "unknown.dat");

        // Assert
        result.Type.ShouldBe(FileType.Dat);
        result.DetectedFormat.ShouldBe("extension");
        result.Confidence.ShouldBeLessThanOrEqualTo(0.5f);
    }

    [Fact]
    public async Task ClassifyAsync_XmlWithLeadingWhitespace_ReturnsDat()
    {
        // Arrange
        using var stream = CreateTextStream("  \n\t<?xml version=\"1.0\"?><datafile></datafile>");

        // Act
        var result = await _sut.ClassifyAsync(stream, "test.dat");

        // Assert
        result.Type.ShouldBe(FileType.Dat);
    }

    [Fact]
    public async Task ClassifyAsync_Utf8BomXml_ReturnsDat()
    {
        // Arrange - UTF-8 BOM + XML
        var content = Encoding.UTF8.GetPreamble().Concat(
            Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><datafile></datafile>")).ToArray();
        using var stream = new MemoryStream(content);

        // Act
        var result = await _sut.ClassifyAsync(stream, "test.dat");

        // Assert
        result.Type.ShouldBe(FileType.Dat);
    }

    #endregion

    #region ROM Detection

    [Fact]
    public async Task ClassifyAsync_BinaryFileWithExtension_ReturnsRom()
    {
        // Arrange - random binary data
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
        using var stream = new MemoryStream(bytes);

        // Act
        var result = await _sut.ClassifyAsync(stream, "game.nes");

        // Assert
        result.Type.ShouldBe(FileType.Rom);
        result.DetectedFormat.ShouldBe("extension");
    }

    [Theory]
    [InlineData("game.nes")]
    [InlineData("game.smc")]
    [InlineData("game.sfc")]
    [InlineData("game.gb")]
    [InlineData("game.gbc")]
    [InlineData("game.gba")]
    [InlineData("game.nds")]
    [InlineData("game.bin")]
    [InlineData("game.iso")]
    public async Task ClassifyAsync_CommonRomExtensions_ReturnsRom(string filename)
    {
        // Arrange
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        using var stream = new MemoryStream(bytes);

        // Act
        var result = await _sut.ClassifyAsync(stream, filename);

        // Assert
        result.Type.ShouldBe(FileType.Rom);
    }

    #endregion

    #region Unknown/Edge Cases

    [Fact]
    public async Task ClassifyAsync_NoExtension_ReturnsUnknown()
    {
        // Arrange
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        using var stream = new MemoryStream(bytes);

        // Act
        var result = await _sut.ClassifyAsync(stream, "unknownfile");

        // Assert
        result.Type.ShouldBe(FileType.Unknown);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyStream_ReturnsUnknown()
    {
        // Arrange
        using var stream = new MemoryStream();

        // Act
        var result = await _sut.ClassifyAsync(stream, "empty.bin");

        // Assert
        result.Type.ShouldBe(FileType.Unknown);
        result.Confidence.ShouldBe(0f);
    }

    [Fact]
    public async Task ClassifyAsync_ResetsStreamPosition()
    {
        // Arrange
        using var stream = CreateTextStream("<?xml version=\"1.0\"?><datafile></datafile>");
        stream.Position = 0;

        // Act
        await _sut.ClassifyAsync(stream, "test.dat");

        // Assert
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task ClassifyAsync_NonSeekableStream_Throws()
    {
        // Arrange
        using var stream = CreateTextStream("content");
        using var nonSeekable = new NonSeekableStream(stream);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            () => _sut.ClassifyAsync(nonSeekable, "test.dat"));
    }

    #endregion

    #region Archive Priority

    [Fact]
    public async Task ClassifyAsync_ZipContainingXml_ReturnsArchive()
    {
        // Arrange - ZIP file magic bytes take precedence over XML content
        using var stream = CreateZipStream();

        // Act
        var result = await _sut.ClassifyAsync(stream, "dats.zip");

        // Assert
        result.Type.ShouldBe(FileType.Archive);
    }

    #endregion

    #region Helpers

    private static MemoryStream CreateTextStream(string content)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return stream;
    }

    private static MemoryStream CreateZipStream()
    {
        var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("test.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("content");
        }
        memoryStream.Position = 0;
        return memoryStream;
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
