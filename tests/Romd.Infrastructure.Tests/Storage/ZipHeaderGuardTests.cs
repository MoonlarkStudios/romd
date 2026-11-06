using System.Buffers.Binary;
using System.IO.Compression;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ZipHeaderGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-zip-headers-").FullName;
    private readonly ZipArchiveExtractor _extractor = new();
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("count", "entry limit")]
    [InlineData("directory-size", "memory limit")]
    [InlineData("name-size", "memory limit")]
    [InlineData("offset", "bounds")]
    [InlineData("undercount", "count or size mismatch")]
    [InlineData("truncated-entry", "Truncated")]
    [InlineData("multi-volume", "Multi-volume")]
    [InlineData("trailing-directory", "bounds")]
    [InlineData("comment-record", "end record")]
    public async Task OpenAsync_UnsafeMetadata_RejectsBeforeCreatingOutput(string corruption, string message)
    {
        byte[] bytes = ZipArchiveIntegrityTests.CreateZip(CompressionLevel.NoCompression, "alpha"u8.ToArray());
        int end = bytes.Length - 22, directory = ZipArchiveIntegrityTests.DirectoryOffset(bytes);
        switch (corruption)
        {
            case "count":
                Put16(bytes, end + 8, 50_001);
                Put16(bytes, end + 10, 50_001);
                break;
            case "directory-size":
                Put32(bytes, end + 12, 16 * 1024 * 1024 + 1);
                break;
            case "name-size":
                Put16(bytes, directory + 28, 4_001);
                break;
            case "offset":
                Put32(bytes, end + 16, uint.MaxValue - 1);
                break;
            case "undercount":
                Put16(bytes, end + 8, 0);
                Put16(bytes, end + 10, 0);
                break;
            case "truncated-entry":
                Put16(bytes, directory + 30, ushort.MaxValue);
                break;
            case "multi-volume":
                Put16(bytes, end + 4, 1);
                break;
            case "trailing-directory":
                Put32(bytes, end + 12, 46);
                break;
            case "comment-record":
                Put16(bytes, end + 20, 22);
                bytes = [.. bytes, .. new byte[22]];
                Put32(bytes, end + 22, 0x06054b50);
                Put16(bytes, end + 22 + 20, 1);
                break;
        }
        using var source = new ReadCountingStream(bytes);
        string target = Path.Combine(_root, "out");

        var error = await Should.ThrowAsync<InvalidDataException>(() => _extractor.ExtractAllAsync(source, target, new Quota()));

        error.Message.ShouldContain(message);
        source.BytesRead.ShouldBeLessThan(128 * 1024);
        Directory.Exists(target).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_UnsafeMetadata_AlsoGuardsEnumerationAndSingleMember(bool singleMember)
    {
        byte[] bytes = ZipArchiveIntegrityTests.CreateZip(CompressionLevel.NoCompression, "alpha"u8.ToArray());
        Put32(bytes, bytes.Length - 10, 16 * 1024 * 1024 + 1);
        using var source = new MemoryStream(bytes);

        var error = await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            if (singleMember)
                await using (await _extractor.ExtractAsync(source, "a.rom"))
                { }
            else
                await _extractor.EnumerateAsync(source).ToListAsync();
        });

        error.Message.ShouldContain("memory limit");
    }

    [Fact]
    public async Task EnumerateAsync_TooManyActualRecordsWithSmallDeclaredCount_RejectsMismatch()
    {
        byte[] original = ZipArchiveIntegrityTests.CreateZip(CompressionLevel.NoCompression, Array.Empty<byte>());
        int directory = ZipArchiveIntegrityTests.DirectoryOffset(original);
        byte[] record = original[directory..^22];
        using var output = new MemoryStream();
        output.Write(original.AsSpan(0, directory));
        for (int index = 0; index < 50_001; index++)
            output.Write(record);
        byte[] footer = original[^22..];
        Put32(footer, 12, checked((uint)(record.Length * 50_001)));
        output.Write(footer);
        using var source = new ReadCountingStream(output.ToArray());

        var error = await Should.ThrowAsync<InvalidDataException>(async () => await _extractor.EnumerateAsync(source).ToListAsync());

        error.Message.ShouldContain("count or size mismatch");
        source.BytesRead.ShouldBeLessThan(128 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractAllAsync_OrdinaryAndZip64Directories_RetainPayload(bool zip64)
    {
        byte[] bytes = ZipArchiveIntegrityTests.CreateZip(CompressionLevel.Optimal, "alpha"u8.ToArray());
        if (zip64)
            bytes = Zip64(bytes);
        using var source = new MemoryStream(bytes);

        var output = await _extractor.ExtractAllAsync(source, Path.Combine(_root, "out"), new Quota());

        output.Single().Size.ShouldBe(5);
        File.ReadAllText(output[0].AbsolutePath).ShouldBe("alpha");
    }

    [Theory]
    [InlineData("count", "entry limit")]
    [InlineData("directory-size", "memory limit")]
    [InlineData("offset", "bounds")]
    [InlineData("record-size", "end record")]
    [InlineData("locator-offset", "outside")]
    [InlineData("multi-volume", "multi-volume")]
    [InlineData("contradictory-count", "Contradictory")]
    public async Task EnumerateAsync_UnsafeZip64Metadata_RejectsBeforeFrameworkParsing(string corruption, string message)
    {
        byte[] bytes = Zip64(ZipArchiveIntegrityTests.CreateZip(CompressionLevel.NoCompression, Array.Empty<byte>()));
        int record = bytes.Length - 98, end = bytes.Length - 22;
        switch (corruption)
        {
            case "count":
                Put64(bytes, record + 24, 50_001);
                Put64(bytes, record + 32, 50_001);
                break;
            case "directory-size":
                Put64(bytes, record + 40, 16 * 1024 * 1024 + 1);
                break;
            case "offset":
                Put64(bytes, record + 48, ulong.MaxValue);
                break;
            case "record-size":
                Put64(bytes, record + 4, ulong.MaxValue);
                break;
            case "locator-offset":
                Put64(bytes, end - 12, ulong.MaxValue);
                break;
            case "multi-volume":
                Put32(bytes, record + 16, 1);
                break;
            case "contradictory-count":
                Put16(bytes, end + 10, 2);
                break;
        }
        using var source = new ReadCountingStream(bytes);

        var error = await Should.ThrowAsync<InvalidDataException>(async () => await _extractor.EnumerateAsync(source).ToListAsync());

        error.Message.ShouldContain(message);
        source.BytesRead.ShouldBeLessThan(128 * 1024);
    }

    [Fact]
    public async Task EnumerateAsync_EmptyArchiveWithComment_IsAccepted()
    {
        byte[] bytes = ZipArchiveIntegrityTests.CreateZip(CompressionLevel.NoCompression);
        Put16(bytes, bytes.Length - 2, 3);
        using var source = new MemoryStream([.. bytes, .. "zip"u8.ToArray()]);

        (await _extractor.EnumerateAsync(source).ToListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractAllAsync_EndSignatureInNumericFooterField_MatchesFrameworkAcceptance()
    {
        byte[] bytes = ZipArchiveIntegrityTests.CreateZip(CompressionLevel.NoCompression, "alpha"u8.ToArray());
        int directory = ZipArchiveIntegrityTests.DirectoryOffset(bytes);
        byte[] footer = bytes[^22..];
        Put32(footer, 16, 0x06054b50);
        // A sparse gap positions the directory at the signature's numeric value without
        // allocating or writing a 100 MB test fixture. The original local member stays at 0.
        using var source = new FileStream(Path.Combine(_root, "numeric.zip"), FileMode.CreateNew, FileAccess.ReadWrite);
        source.Write(bytes.AsSpan(0, directory));
        source.Position = 0x06054b50;
        source.Write(bytes.AsSpan(directory, bytes.Length - 22 - directory));
        source.Write(footer);
        source.Position = 0;
        using (var framework = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
        using (var reader = new StreamReader(framework.Entries.Single().Open()))
            reader.ReadToEnd().ShouldBe("alpha");

        var result = await _extractor.ExtractAllAsync(source, Path.Combine(_root, "out"), new Quota());

        File.ReadAllText(result.Single().AbsolutePath).ShouldBe("alpha");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractAllAsync_DirectorySignatureRecord_MatchesFrameworkAcceptance(bool zip64)
    {
        byte[] signature = new byte[12];
        Put32(signature, 0, 0x05054b50);
        Put16(signature, 4, 6);
        "signed"u8.CopyTo(signature.AsSpan(6));
        byte[] bytes = WithDirectorySignature(signature);
        if (zip64)
            bytes = Zip64(bytes);
        using var source = new MemoryStream(bytes);
        using (var framework = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
        using (var reader = new StreamReader(framework.Entries.Single().Open()))
            reader.ReadToEnd().ShouldBe("alpha");

        var result = await _extractor.ExtractAllAsync(source, Path.Combine(_root, "out"), new Quota());

        File.ReadAllText(result.Single().AbsolutePath).ShouldBe("alpha");
    }

    [Theory]
    [InlineData("truncated", false)]
    [InlineData("truncated", true)]
    [InlineData("incorrect-length", false)]
    [InlineData("incorrect-length", true)]
    [InlineData("additional-record", false)]
    [InlineData("additional-record", true)]
    public async Task ExtractAllAsync_MalformedDirectorySignature_RejectsBeforeCreatingOutput(string corruption, bool zip64)
    {
        byte[] signature = new byte[6];
        Put32(signature, 0, 0x05054b50);
        signature = corruption switch
        {
            "truncated" => signature[..5],
            "additional-record" => [.. signature, .. signature],
            _ => signature
        };
        if (corruption == "incorrect-length")
            Put16(signature, 4, 1);
        byte[] bytes = WithDirectorySignature(signature);
        if (zip64)
            bytes = Zip64(bytes);
        using var source = new MemoryStream(bytes);
        string target = Path.Combine(_root, "out");

        var error = await Should.ThrowAsync<InvalidDataException>(() => _extractor.ExtractAllAsync(source, target, new Quota()));

        error.Message.ShouldContain("signature");
        Directory.Exists(target).ShouldBeFalse();
    }

    private static byte[] WithDirectorySignature(byte[] signature)
    {
        byte[] bytes = ZipArchiveIntegrityTests.CreateZip(CompressionLevel.NoCompression, "alpha"u8.ToArray());
        byte[] footer = bytes[^22..];
        Put32(footer, 12, checked(BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12)) + (uint)signature.Length));
        return [.. bytes[..^22], .. signature, .. footer];
    }

    private static byte[] Zip64(byte[] bytes)
    {
        int end = bytes.Length - 22;
        byte[] footer = bytes[^22..], record = new byte[56], locator = new byte[20];
        Put32(record, 0, 0x06064b50);
        Put64(record, 4, 44);
        Put16(record, 12, 45);
        Put16(record, 14, 45);
        Put64(record, 24, BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(8)));
        Put64(record, 32, BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(10)));
        Put64(record, 40, BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12)));
        Put64(record, 48, BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16)));
        Put32(locator, 0, 0x07064b50);
        Put64(locator, 8, (ulong)end);
        Put32(locator, 16, 1);
        Put16(footer, 8, ushort.MaxValue);
        Put16(footer, 10, ushort.MaxValue);
        Put32(footer, 12, uint.MaxValue);
        Put32(footer, 16, uint.MaxValue);
        return [.. bytes[..end], .. record, .. locator, .. footer];
    }

    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private sealed class Quota : IExtractionQuota
    {
        public bool TryChargeEntry() => true;
        public bool TryChargeBytes(long count) => true;
    }
    private sealed class ReadCountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            int count = base.Read(buffer);
            BytesRead += count;
            return count;
        }
    }
}
