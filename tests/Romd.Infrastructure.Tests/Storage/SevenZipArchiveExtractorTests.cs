using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class SevenZipArchiveExtractorTests : IDisposable
{
    private readonly SevenZipArchiveExtractor _sut = new();
    private readonly string _root = Directory.CreateTempSubdirectory("romd-7z-tests-").FullName;
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SevenZip", name);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("pack.7z", true)]
    [InlineData("pack.7Z", true)]
    [InlineData("pack.7z.001", true)] // Routed to a clean rejection, never a ROM.
    [InlineData("pack.zip", false)]
    [InlineData("pack.rar", false)]
    [InlineData("pack", false)]
    public void CanHandle_Filename_Uses7zExtension(string name, bool expected) => _sut.CanHandle(name).ShouldBe(expected);

    [Fact]
    public void SupportedExtensions_ContainsOnly7z() => _sut.SupportedExtensions.ShouldBe([".7z"]);

    [Theory]
    [InlineData("non-solid.7z")]
    [InlineData("solid.7z")]
    [InlineData("lzma.7z")]
    public async Task EnumerateAsync_CompressionVariants_ReportsDeclaredEntriesAndSizes(string fixture)
    {
        using var input = File.OpenRead(Fixture(fixture));
        var entries = await _sut.EnumerateAsync(input).ToListAsync();
        entries.Count.ShouldBe(6);
        entries.Count(e => e.IsDirectory).ShouldBe(2);
        entries.Sum(e => e.UncompressedSize).ShouldBe(15);
        entries.ShouldAllBe(e => e.CompressedSize == 0);
        entries.ShouldContain(e => e.Path == "tree/nested.rom" && e.UncompressedSize == 6);
        input.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData("non-solid.7z")]
    [InlineData("solid.7z")]
    [InlineData("lzma.7z")]
    public async Task ExtractAllAsync_CompressionVariants_WritesFilesAndChargesEveryInode(string fixture)
    {
        using var input = File.OpenRead(Fixture(fixture));
        var quota = new Quota();
        string target = Path.Combine(_root, "out");
        var entries = await _sut.ExtractAllAsync(input, target, quota);
        File.ReadAllText(Path.Combine(target, "a.rom")).ShouldBe("alpha");
        File.ReadAllText(Path.Combine(target, "b.rom")).ShouldBe("beta");
        File.ReadAllText(Path.Combine(target, "tree", "nested.rom")).ShouldBe("nested");
        new FileInfo(Path.Combine(target, "empty.rom")).Length.ShouldBe(0);
        Directory.Exists(Path.Combine(target, "emptydir")).ShouldBeTrue();
        entries.Count.ShouldBe(6);
        quota.Entries.ShouldBe(7); // root, two explicit directories, four files
        quota.Bytes.ShouldBe(15);
    }

    [Theory]
    [InlineData("implicit-parents.7z")]
    [InlineData("backslash-directory.7z")]
    public async Task ExtractAllAsync_ImplicitParents_ChargesEachOnce(string fixture)
    {
        using var input = File.OpenRead(Fixture(fixture));
        var quota = new Quota();
        await _sut.ExtractAllAsync(input, Path.Combine(_root, "out"), quota);
        quota.Entries.ShouldBe(4);
        quota.Bytes.ShouldBe(5);
    }

    [Fact]
    public async Task ExtractAllAsync_EntryLimit_StopsBeforeCreatingNextParent()
    {
        using var input = File.OpenRead(Fixture("implicit-parents.7z"));
        var quota = new Quota(maxEntries: 2);
        string target = Path.Combine(_root, "out");
        await Should.ThrowAsync<ExtractionQuotaExceededException>(() => _sut.ExtractAllAsync(input, target, quota));
        quota.Entries.ShouldBe(2);
        Directory.Exists(Path.Combine(target, "one")).ShouldBeTrue();
        Directory.Exists(Path.Combine(target, "one", "two")).ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractAllAsync_ByteLimit_RefusesCrossingChunkImmediately()
    {
        using var input = File.OpenRead(Fixture("large.7z"));
        var quota = new Quota(maxBytes: 70_000);
        string target = Path.Combine(_root, "out");
        await Should.ThrowAsync<ExtractionQuotaExceededException>(() => _sut.ExtractAllAsync(input, target, quota));
        quota.Bytes.ShouldBe(64 * 1024);
        new FileInfo(Path.Combine(target, "large.rom")).Length.ShouldBe(quota.Bytes);
        quota.LargestWrite.ShouldBeLessThanOrEqualTo(64 * 1024);
    }

    [Theory]
    [InlineData("duplicate.7z")]
    [InlineData("case-collision.7z")]
    [InlineData("parent-collision.7z")]
    [InlineData("normalized-collision.7z")]
    public async Task ExtractAllAsync_Collisions_RejectAndKeepFirstPayload(string fixture)
    {
        using var input = File.OpenRead(Fixture(fixture));
        var quota = new Quota();
        string target = Path.Combine(_root, "out");
        await Should.ThrowAsync<ArchiveEntryCollisionException>(() => _sut.ExtractAllAsync(input, target, quota));
        File.ReadAllText(Path.Combine(target, "a.rom")).ShouldBe("alpha");
        quota.Entries.ShouldBe(2);
    }

    [Fact]
    public async Task ExtractAllAsync_PreexistingFile_DoesNotOverwrite()
    {
        string target = Path.Combine(_root, "out");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.rom"), "keep");
        using var input = File.OpenRead(Fixture("copy-seed.7z"));
        await Should.ThrowAsync<ArchiveEntryCollisionException>(() => _sut.ExtractAllAsync(input, target, new Quota()));
        File.ReadAllText(Path.Combine(target, "a.rom")).ShouldBe("keep");
    }

    [Theory]
    [InlineData("encrypted.7z")]
    [InlineData("encrypted-headers.7z")]
    [InlineData("traversal.7z")]
    [InlineData("absolute.7z")]
    [InlineData("backslash.7z")]
    [InlineData("drive.7z")]
    [InlineData("nul.7z")]
    [InlineData("overlong.7z")]
    [InlineData("truncated.7z")]
    [InlineData("unsupported-coder.7z")]
    [InlineData("ppmd.7z")]
    [InlineData("symlink.7z")]
    [InlineData("split.7z.001")]
    public async Task ExtractAllAsync_UnsafeMetadata_RejectsBeforeCreatingOutput(string fixture)
    {
        using var input = File.OpenRead(Fixture(fixture));
        string target = Path.Combine(_root, "out");
        await Should.ThrowAsync<InvalidDataException>(() => _sut.ExtractAllAsync(input, target, new Quota()));
        Directory.Exists(target).ShouldBeFalse();
        input.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData("oversized-dictionary.7z")]
    [InlineData("oversized-lzma.7z")]
    [InlineData("oversized-header-dictionary.7z")]
    public async Task ExtractAllAsync_OversizedDictionary_RejectsWithMemoryLimitBeforeCreatingOutput(string fixture)
    {
        using var input = File.OpenRead(Fixture(fixture));
        var quota = new Quota();
        string target = Path.Combine(_root, "out");
        var error = await Should.ThrowAsync<InvalidDataException>(() => _sut.ExtractAllAsync(input, target, quota));
        // Some fixtures also have invalid compressed data. A decoder failure must not
        // substitute for ROMD rejecting the dictionary before allocating it.
        error.Message.ShouldBe("7z dictionary memory limit exceeded (64 MiB per folder).");
        quota.Entries.ShouldBe(0);
        quota.Bytes.ShouldBe(0);
        Directory.Exists(target).ShouldBeFalse();
        input.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task ExtractAllAsync_CorruptPayload_RejectsWithInvalidData()
    {
        using var input = File.OpenRead(Fixture("corrupt.7z"));
        await Should.ThrowAsync<InvalidDataException>(() => _sut.ExtractAllAsync(input, Path.Combine(_root, "out"), new Quota()));
    }

    [Fact]
    public async Task EnumerateAsync_DeclaredSize_IsReportedWithoutDecodingPayload()
    {
        using var input = File.OpenRead(Fixture("declared-size.7z"));
        var entries = await _sut.EnumerateAsync(input).ToListAsync();
        entries.Single().UncompressedSize.ShouldBe(1L << 40);
    }

    [Fact]
    public async Task ExtractAllAsync_AntiItem_IsSkippedWithoutQuotaCharge()
    {
        using var input = File.OpenRead(Fixture("anti.7z"));
        (await _sut.EnumerateAsync(input).ToListAsync()).Count.ShouldBe(1);
        var quota = new Quota();
        string target = Path.Combine(_root, "out");
        (await _sut.ExtractAllAsync(input, target, quota)).Count.ShouldBe(1);
        File.Exists(Path.Combine(target, "gone.rom")).ShouldBeFalse();
        quota.Entries.ShouldBe(2);
        quota.Bytes.ShouldBe(5);
    }

    [Fact]
    public async Task ExtractAsync_SolidEntry_ReturnsIndependentDiskStreamDeletedOnDispose()
    {
        using var input = File.OpenRead(Fixture("solid.7z"));
        var output = (await _sut.ExtractAsync(input, "b.rom")).ShouldBeOfType<FileStream>();
        string temp = output.Name;
        using (var text = new StreamReader(output, leaveOpen: true))
            (await text.ReadToEndAsync()).ShouldBe("beta");
        output.CanSeek.ShouldBeTrue();
        await output.DisposeAsync();
        File.Exists(temp).ShouldBeFalse();
        input.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task ExtractAsync_MissingEntry_ThrowsFileNotFound()
    {
        using var input = File.OpenRead(Fixture("solid.7z"));
        await Should.ThrowAsync<FileNotFoundException>(() => _sut.ExtractAsync(input, "missing.rom"));
    }

    [Fact]
    public async Task EnumerateAsync_WrongSignature_ThrowsInvalidData()
    {
        using var input = File.OpenRead(Fixture("7z-in-zip.zip"));
        await Should.ThrowAsync<InvalidDataException>(async () => await _sut.EnumerateAsync(input).ToListAsync());
    }

    [Fact]
    public async Task ExtractAllAsync_Cancelled_LeavesNoOutput()
    {
        using var input = File.OpenRead(Fixture("solid.7z"));
        string target = Path.Combine(_root, "out");
        await Should.ThrowAsync<OperationCanceledException>(() => _sut.ExtractAllAsync(input, target, new Quota(), new CancellationToken(true)));
        Directory.Exists(target).ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractAllAsync_CaseAliasedParent_RejectsOnAllFilesystems()
    {
        using var input = File.OpenRead(Fixture("parent-case.7z"));
        string target = Path.Combine(_root, "out");
        await Should.ThrowAsync<ArchiveEntryCollisionException>(() => _sut.ExtractAllAsync(input, target, new Quota()));
        File.ReadAllText(Path.Combine(target, "one", "a.rom")).ShouldBe("alpha");
    }

    [Fact]
    public async Task EnumerateAsync_NonSeekable_Rejects()
    {
        using var input = new NonSeekable(File.ReadAllBytes(Fixture("solid.7z")));
        await Should.ThrowAsync<ArgumentException>(async () => await _sut.EnumerateAsync(input).ToListAsync());
        await Should.ThrowAsync<ArgumentException>(() => _sut.ExtractAsync(input, "a.rom"));
        await Should.ThrowAsync<ArgumentException>(() => _sut.ExtractAllAsync(input, _root, new Quota()));
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class Quota(int maxEntries = int.MaxValue, long maxBytes = long.MaxValue) : IExtractionQuota
    {
        public int Entries { get; private set; }
        public long Bytes { get; private set; }
        public long LargestWrite { get; private set; }
        public bool TryChargeEntry() { if (Entries == maxEntries) return false; Entries++; return true; }
        public bool TryChargeBytes(long count)
        {
            LargestWrite = Math.Max(LargestWrite, count);
            if (count > maxBytes - Bytes)
                return false;
            Bytes += count;
            return true;
        }
    }
}
