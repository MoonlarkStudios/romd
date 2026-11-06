using System.Buffers.Binary;
using System.IO.Compression;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ZipArchiveIntegrityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-zip-integrity-").FullName;
    private readonly ZipArchiveExtractor _extractor = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(CompressionLevel.NoCompression, false, "crc")]
    [InlineData(CompressionLevel.NoCompression, true, "crc")]
    [InlineData(CompressionLevel.Optimal, false, "crc")]
    [InlineData(CompressionLevel.Optimal, true, "crc")]
    [InlineData(CompressionLevel.NoCompression, false, "short")]
    [InlineData(CompressionLevel.NoCompression, true, "short")]
    [InlineData(CompressionLevel.Optimal, false, "short")]
    [InlineData(CompressionLevel.Optimal, true, "short")]
    [InlineData(CompressionLevel.NoCompression, false, "long")]
    [InlineData(CompressionLevel.NoCompression, true, "long")]
    [InlineData(CompressionLevel.Optimal, false, "long")]
    [InlineData(CompressionLevel.Optimal, true, "long")]
    public async Task ExtractAsync_CorruptMember_RejectsBeforeReportingSuccess(CompressionLevel compression,
        bool singleMember, string corruption)
    {
        byte[] bytes = CreateZip(compression, "alpha"u8.ToArray());
        int directory = DirectoryOffset(bytes);
        if (corruption == "crc")
        {
            bytes[directory + 16] ^= 1;
            bytes[14] ^= 1;
        }
        else
        {
            uint size = corruption == "short" ? 100u : 2u;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 24), size);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), size);
        }
        using var source = new MemoryStream(bytes);

        var error = await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            if (singleMember)
                await using (await _extractor.ExtractAsync(source, "a.rom"))
                { }
            else
                await _extractor.ExtractAllAsync(source, Path.Combine(_root, "out"), new Quota());
        });

        error.Message.ShouldContain("ZIP entry");
        source.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public async Task ExtractAllAsync_ValidMembersIncludingEmpty_WritesMeasuredBytes(CompressionLevel compression)
    {
        using var source = new MemoryStream(CreateZip(compression, "alpha"u8.ToArray(), []));
        var quota = new Quota();

        var result = await _extractor.ExtractAllAsync(source, Path.Combine(_root, "out"), quota);

        result.Select(entry => entry.Size).ShouldBe([5L, 0L]);
        File.ReadAllText(result[0].AbsolutePath).ShouldBe("alpha");
        new FileInfo(result[1].AbsolutePath).Length.ShouldBe(0);
        quota.Bytes.ShouldBe(5);
    }

    [Fact]
    public async Task ExtractAsync_ValidMember_ReturnsIndependentDiskStreamDeletedOnDisposal()
    {
        using var source = new MemoryStream(CreateZip(CompressionLevel.Optimal, "alpha"u8.ToArray()));
        var result = (await _extractor.ExtractAsync(source, "a.rom")).ShouldBeOfType<FileStream>();
        string temporary = result.Name;
        source.Dispose();

        using (var reader = new StreamReader(result, leaveOpen: true))
            (await reader.ReadToEndAsync()).ShouldBe("alpha");
        await result.DisposeAsync();

        File.Exists(temporary).ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractAllAsync_ByteQuota_RefusesCrossingChunkBeforeWriting()
    {
        byte[] payload = new byte[160_000];
        new Random(71).NextBytes(payload);
        using var source = new MemoryStream(CreateZip(CompressionLevel.NoCompression, payload));
        var quota = new Quota(70_000);
        string target = Path.Combine(_root, "out");

        await Should.ThrowAsync<ExtractionQuotaExceededException>(() => _extractor.ExtractAllAsync(source, target, quota));

        quota.Bytes.ShouldBe(64 * 1024);
        new FileInfo(Path.Combine(target, "a.rom")).Length.ShouldBe(quota.Bytes);
    }

    internal static byte[] CreateZip(CompressionLevel compression, params byte[][] payloads)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            for (int index = 0; index < payloads.Length; index++)
            {
                using var entry = archive.CreateEntry(index == 0 ? "a.rom" : $"{index}.rom", compression).Open();
                entry.Write(payloads[index]);
            }
        return output.ToArray();
    }

    internal static int DirectoryOffset(byte[] bytes) =>
        checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6)));

    private sealed class Quota(long limit = long.MaxValue) : IExtractionQuota
    {
        public long Bytes { get; private set; }
        public bool TryChargeEntry() => true;
        public bool TryChargeBytes(long count)
        {
            if (count > limit - Bytes)
                return false;
            Bytes += count;
            return true;
        }
    }
}
