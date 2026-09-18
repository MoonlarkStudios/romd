using System.IO.Compression;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ZipArchiveExtractorTests : IDisposable
{
    private readonly ZipArchiveExtractor _sut = new();
    private readonly string _workRoot = Directory.CreateTempSubdirectory("romd-zip-extractor-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workRoot, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Theory]
    [InlineData("test.zip", true)]
    [InlineData("test.ZIP", true)]
    [InlineData("test.Zip", true)]
    [InlineData("test.7z", false)]
    [InlineData("test.rar", false)]
    [InlineData("test.txt", false)]
    [InlineData("test", false)]
    public void CanHandle_ReturnsExpectedResult(string filename, bool expected) =>
        _sut.CanHandle(filename).ShouldBe(expected);

    [Fact]
    public void SupportedExtensions_ContainsZip() => _sut.SupportedExtensions.ShouldContain(".zip");

    [Fact]
    public async Task EnumerateAsync_ReturnsAllEntries()
    {
        // Arrange
        using var zipStream = CreateTestZip(
            ("file1.txt", "Content 1"),
            ("folder/file2.txt", "Content 2"),
            ("folder/subfolder/file3.txt", "Content 3"));

        // Act
        var entries = await _sut.EnumerateAsync(zipStream).ToListAsync();

        // Assert
        entries.Count.ShouldBe(3);
        entries.ShouldContain(e => e.Path == "file1.txt" && !e.IsDirectory);
        entries.ShouldContain(e => e.Path == "folder/file2.txt" && !e.IsDirectory);
        entries.ShouldContain(e => e.Path == "folder/subfolder/file3.txt" && !e.IsDirectory);
    }

    [Fact]
    public async Task EnumerateAsync_ReportsCorrectSizes()
    {
        // Arrange
        string content = "Hello, World!";
        using var zipStream = CreateTestZip(("test.txt", content));

        // Act
        var entries = await _sut.EnumerateAsync(zipStream).ToListAsync();

        // Assert
        var entry = entries.Single();
        entry.UncompressedSize.ShouldBe(content.Length);
    }

    [Fact]
    public async Task ExtractAsync_ExtractsFileContent()
    {
        // Arrange
        string expectedContent = "Hello, World!";
        using var zipStream = CreateTestZip(("test.txt", expectedContent));

        // Act
        await using var extractedStream = await _sut.ExtractAsync(zipStream, "test.txt");
        using var reader = new StreamReader(extractedStream);
        string actualContent = await reader.ReadToEndAsync();

        // Assert
        actualContent.ShouldBe(expectedContent);
    }

    [Fact]
    public async Task ExtractAsync_ExtractsNestedFile()
    {
        // Arrange
        string expectedContent = "Nested content";
        using var zipStream = CreateTestZip(
            ("root.txt", "Root"),
            ("folder/nested.txt", expectedContent));

        // Act
        await using var extractedStream = await _sut.ExtractAsync(zipStream, "folder/nested.txt");
        using var reader = new StreamReader(extractedStream);
        string actualContent = await reader.ReadToEndAsync();

        // Assert
        actualContent.ShouldBe(expectedContent);
    }

    [Fact]
    public async Task ExtractAsync_ThrowsForMissingEntry()
    {
        // Arrange
        using var zipStream = CreateTestZip(("test.txt", "Content"));

        // Act & Assert
        await Should.ThrowAsync<FileNotFoundException>(() => _sut.ExtractAsync(zipStream, "nonexistent.txt"));
    }

    [Fact]
    public async Task EnumerateAsync_ThrowsForNonSeekableStream()
    {
        // Arrange
        using var zipStream = CreateTestZip(("test.txt", "Content"));
        using var nonSeekableStream = new NonSeekableStream(zipStream);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(async () =>
            await _sut.EnumerateAsync(nonSeekableStream).ToListAsync());
    }

    [Fact]
    public async Task ExtractAllAsync_DuplicateEntryNames_RejectsArchiveAndPreservesFirstEntryBytes()
    {
        // Zip central directories permit duplicate names; both normalize to one target path.
        byte[] first = [1, 2, 3, 4];
        byte[] second = [9, 8, 7];
        using var zipStream = CreateBinaryZip(("payload.rom", first), ("payload.rom", second));
        string target = Path.Combine(_workRoot, "out");
        var quota = new CountingQuota();

        var ex = await Record.ExceptionAsync(() => _sut.ExtractAllAsync(zipStream, target, quota));

        // The first entry's bytes must remain intact — pre-fix the second entry overwrote them.
        File.ReadAllBytes(Path.Combine(target, "payload.rom")).ShouldBe(first);
        // Target dir + first file only — the colliding entry charged nothing.
        quota.EntryCharges.ShouldBe(2);
        ex.ShouldBeOfType<ArchiveEntryCollisionException>();
    }

    [Fact]
    public async Task ExtractAllAsync_ParentTraversalAliasCollidesWithExtractedPath_RejectsArchive()
    {
        // "a/../payload.rom" stays inside the target (zip-slip guard passes) but normalizes to
        // the same path as "payload.rom".
        byte[] first = [1, 2, 3, 4];
        byte[] second = [9, 8, 7];
        using var zipStream = CreateBinaryZip(("payload.rom", first), ("a/../payload.rom", second));
        string target = Path.Combine(_workRoot, "out");
        var quota = new CountingQuota();

        var ex = await Record.ExceptionAsync(() => _sut.ExtractAllAsync(zipStream, target, quota));

        // Target dir + first file only — pre-fix the alias charged an entry whose inode never
        // existed on disk.
        quota.EntryCharges.ShouldBe(2);
        File.ReadAllBytes(Path.Combine(target, "payload.rom")).ShouldBe(first);
        ex.ShouldBeOfType<ArchiveEntryCollisionException>();
    }

    [Fact]
    public async Task ExtractAllAsync_TargetPathAlreadyExists_ThrowsInsteadOfOverwriting()
    {
        // Final guard: outputs open with FileMode.CreateNew, so a path that somehow pre-exists
        // (fresh-extraction-directory invariant violated, or a filesystem-case collision the
        // Ordinal set cannot see) throws instead of overwriting.
        byte[] existing = [5, 5, 5];
        string target = Path.Combine(_workRoot, "out");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "payload.rom"), existing);
        using var zipStream = CreateBinaryZip(("payload.rom", [1, 2]));

        var ex = await Record.ExceptionAsync(
            () => _sut.ExtractAllAsync(zipStream, target, new CountingQuota()));

        // The pre-existing bytes must never be replaced, and the guard reports through the
        // same named contract as Ordinal-detected duplicates (platform-independent behavior).
        File.ReadAllBytes(Path.Combine(target, "payload.rom")).ShouldBe(existing);
        ex.ShouldBeOfType<ArchiveEntryCollisionException>();
    }

    [Fact]
    public async Task ExtractAllAsync_CaseOnlyCollision_UsesNamedContractOnCaseInsensitiveVolumes()
    {
        string target = Path.Combine(_workRoot, "case-out");
        using var zipStream = CreateBinaryZip(("Payload.rom", [1, 2, 3]), ("payload.rom", [9, 8]));

        var ex = await Record.ExceptionAsync(
            () => _sut.ExtractAllAsync(zipStream, target, new CountingQuota()));

        if (IsCaseInsensitiveVolume(_workRoot))
        {
            // Case-only collisions slip past the Ordinal set; the CreateNew guard must still
            // surface them as the named collision, not a generic filesystem failure.
            ex.ShouldBeOfType<ArchiveEntryCollisionException>();
            File.ReadAllBytes(Path.Combine(target, "Payload.rom")).ShouldBe(new byte[] { 1, 2, 3 });
        }
        else
        {
            // Case-sensitive volumes legitimately hold both entries.
            ex.ShouldBeNull();
            File.ReadAllBytes(Path.Combine(target, "Payload.rom")).ShouldBe(new byte[] { 1, 2, 3 });
            File.ReadAllBytes(Path.Combine(target, "payload.rom")).ShouldBe(new byte[] { 9, 8 });
        }
    }

    [Fact]
    public async Task ExtractAllAsync_FileEntryUnderFileParent_UsesNamedContractOnAllVolumes()
    {
        // Platform-independent squatter: the entry "a" is a FILE, then "a/b.rom" needs "a"
        // as a directory — parent-component collision, exact name, any volume.
        string target = Path.Combine(_workRoot, "parent-out");
        using var zipStream = CreateBinaryZip(("a", [1]), ("a/b.rom", [2, 3]));

        var ex = await Record.ExceptionAsync(
            () => _sut.ExtractAllAsync(zipStream, target, new CountingQuota()));

        var collision = ex.ShouldBeOfType<ArchiveEntryCollisionException>();
        collision.EntryPath.ShouldBe("a/b.rom");
        collision.EntryPath.ShouldNotContain(_workRoot);
        File.ReadAllBytes(Path.Combine(target, "a")).ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task ExtractAllAsync_CaseOnlyDirectoryThenFile_UsesNamedContractOnCaseInsensitiveVolumes()
    {
        string target = Path.Combine(_workRoot, "dir-then-file-out");
        using var zipStream = CreateBinaryZipWithDirectory("Payload.rom/", ("payload.rom", [9, 8]));

        var ex = await Record.ExceptionAsync(
            () => _sut.ExtractAllAsync(zipStream, target, new CountingQuota()));

        if (IsCaseInsensitiveVolume(_workRoot))
        {
            // A directory squatting on the file's target must surface as the named collision
            // (File.Exists alone is false for a directory — the original gap).
            ex.ShouldBeOfType<ArchiveEntryCollisionException>();
            Directory.Exists(Path.Combine(target, "Payload.rom")).ShouldBeTrue();
        }
        else
        {
            ex.ShouldBeNull();
            Directory.Exists(Path.Combine(target, "Payload.rom")).ShouldBeTrue();
            File.ReadAllBytes(Path.Combine(target, "payload.rom")).ShouldBe(new byte[] { 9, 8 });
        }
    }

    [Fact]
    public async Task ExtractAllAsync_CaseOnlyFileThenDirectory_UsesNamedContractOnCaseInsensitiveVolumes()
    {
        string target = Path.Combine(_workRoot, "file-then-dir-out");
        using var zipStream = CreateBinaryZipWithDirectory("payload.rom/", ("Payload.rom", [1, 2]), directoryLast: true);

        var ex = await Record.ExceptionAsync(
            () => _sut.ExtractAllAsync(zipStream, target, new CountingQuota()));

        if (IsCaseInsensitiveVolume(_workRoot))
        {
            // The inverse squatter: a FILE occupies the directory's target.
            ex.ShouldBeOfType<ArchiveEntryCollisionException>();
            File.ReadAllBytes(Path.Combine(target, "Payload.rom")).ShouldBe(new byte[] { 1, 2 });
        }
        else
        {
            ex.ShouldBeNull();
            File.ReadAllBytes(Path.Combine(target, "Payload.rom")).ShouldBe(new byte[] { 1, 2 });
            Directory.Exists(Path.Combine(target, "payload.rom")).ShouldBeTrue();
        }
    }

    private static MemoryStream CreateBinaryZipWithDirectory(
        string directoryEntry,
        (string Path, byte[] Content) fileEntry,
        bool directoryLast = false)
    {
        var memoryStream = new MemoryStream();

        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            void AddDirectory() => archive.CreateEntry(directoryEntry);
            void AddFile()
            {
                var entry = archive.CreateEntry(fileEntry.Path);
                using var stream = entry.Open();
                stream.Write(fileEntry.Content);
            }

            if (directoryLast)
            {
                AddFile();
                AddDirectory();
            }
            else
            {
                AddDirectory();
                AddFile();
            }
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private static bool IsCaseInsensitiveVolume(string directory)
    {
        string probe = Path.Combine(directory, "case-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "");
        try
        {
            return File.Exists(probe.ToUpperInvariant());
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private static MemoryStream CreateBinaryZip(params (string Path, byte[] Content)[] entries)
    {
        var memoryStream = new MemoryStream();

        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            foreach ((string path, byte[] content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private sealed class CountingQuota : IExtractionQuota
    {
        public int EntryCharges { get; private set; }

        public bool TryChargeEntry()
        {
            EntryCharges++;
            return true;
        }

        public bool TryChargeBytes(long byteCount) => true;
    }

    private static MemoryStream CreateTestZip(params (string path, string content)[] entries)
    {
        var memoryStream = new MemoryStream();

        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            foreach ((string path, string content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}
