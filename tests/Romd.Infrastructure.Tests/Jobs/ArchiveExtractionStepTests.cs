using System.IO.Abstractions;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ArchiveExtractionStepTests : IDisposable
{
    private readonly IDriveInfo _driveInfo = Substitute.For<IDriveInfo>();
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly string _workspace;

    public ArchiveExtractionStepTests()
    {
        _workspace = Directory.CreateTempSubdirectory("romd-extraction-").FullName;
        _driveInfo.AvailableFreeSpace.Returns(long.MaxValue);
        _fileSystem.DriveInfo.New(Arg.Any<string>()).Returns(_driveInfo);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private readonly ArchiveExtractionLimits _defaultLimits = new();

    private ArchiveExtractionStep CreateStep(
        ArchiveExtractionLimits? limits = null,
        IArchiveExtractor? additionalExtractor = null)
    {
        IArchiveExtractor[] extractors = additionalExtractor is null
            ? [new ZipArchiveExtractor()]
            : [new ZipArchiveExtractor(), additionalExtractor];
        return new ArchiveExtractionStep(
            new ArchiveExtractorResolver(extractors),
            _fileSystem,
            limits ?? _defaultLimits,
            NullLogger<ArchiveExtractionStep>.Instance);
    }

    private static void WriteZip(string path, params (string Name, byte[] Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            entryStream.Write(content);
        }
    }

    private static byte[] IncompressibleBytes(int count, int seed = 42)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task Extract_DeclaredUncompressedExceedsAvailableSpace_FailsWithNamedError()
    {
        var content = IncompressibleBytes(4096);
        WriteZip(Path.Combine(_workspace, "game.zip"), ("game.rom", content));
        // Above the extraction floor (so the outer guard passes) but too small for the archive's
        // declared uncompressed bytes once the floor is reserved.
        _driveInfo.AvailableFreeSpace.Returns(
            _defaultLimits.SpaceFloorBytes + content.Length - 1);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("game.zip");
        ex.Message.ShouldContain("uncompressed bytes");
        File.Exists(Path.Combine(_workspace, "game.zip")).ShouldBeTrue();
        File.Exists(Path.Combine(_workspace, "game", "game.rom")).ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_ExpansionRatioBomb_FailsWithNamedError()
    {
        // 1MB of zeros compresses far past the 200:1 cap.
        WriteZip(Path.Combine(_workspace, "bomb.zip"), ("zeros.rom", new byte[1 << 20]));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("bomb.zip");
        ex.Message.ShouldContain("expansion limit");
    }

    [Fact]
    public async Task Extract_NestedArchive_IsRecheckedOnItsOwnRound()
    {
        // The outer archive is innocuous (its declared payload is just the inner zip), but the
        // inner archive violates the ratio cap: the per-round re-check must catch it after the
        // first extraction round unpacks it.
        string innerPath = Path.Combine(_workspace, "inner.zip");
        WriteZip(innerPath, ("zeros.rom", new byte[1 << 20]));
        byte[] innerBytes = File.ReadAllBytes(innerPath);
        File.Delete(innerPath);
        WriteZip(Path.Combine(_workspace, "outer.zip"), ("inner.zip", innerBytes));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("inner.zip");
        ex.Message.ShouldContain("expansion limit");
        // The outer round did run: outer.zip was extracted and removed before the inner failure.
        File.Exists(Path.Combine(_workspace, "outer.zip")).ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_NestingLimit_StopsBeforeOpeningNestedArchive()
    {
        string innerPath = Path.Combine(_workspace, "inner.zip");
        WriteZip(innerPath, ("game.rom", IncompressibleBytes(128)));
        byte[] inner = await File.ReadAllBytesAsync(innerPath);
        File.Delete(innerPath);
        WriteZip(Path.Combine(_workspace, "outer.zip"), ("inner.zip", inner));
        var reported = new List<string>();
        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            CreateStep(_defaultLimits with { MaxNestingDepth = 1 }).ExtractRecursivelyAsync(
                _workspace, CancellationToken.None, (path, _) => { reported.Add(path); return Task.CompletedTask; }));
        exception.Message.ShouldContain("nesting");
        reported.ShouldBe(["outer.zip"]);
        File.Exists(Path.Combine(_workspace, "outer", "inner.zip")).ShouldBeTrue();
    }

    [Fact]
    public async Task Extract_ArchiveNameCollidesWithStagedDirectory_PreservesStagedBytes()
    {
        // Reviewer's F1 reproduction: game.zip contains foo.rom (bytes A) and the workspace also
        // staged game/foo.rom (bytes B). Extraction must never write into the staged directory —
        // otherwise the JobItem recorded at game/foo.rom describes bytes A while move-mode cleanup
        // deletes the original whose bytes B never reached storage.
        var archiveBytes = IncompressibleBytes(2048, seed: 1);
        var stagedBytes = IncompressibleBytes(4096, seed: 2);
        WriteZip(Path.Combine(_workspace, "game.zip"), ("foo.rom", archiveBytes));
        Directory.CreateDirectory(Path.Combine(_workspace, "game"));
        File.WriteAllBytes(Path.Combine(_workspace, "game", "foo.rom"), stagedBytes);

        await CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None);

        // The staged file is untouched: the JobItem at game/foo.rom binds to exactly bytes B.
        File.ReadAllBytes(Path.Combine(_workspace, "game", "foo.rom")).ShouldBe(stagedBytes);
        // The archive's copy is processed from a fresh, collision-free directory.
        File.ReadAllBytes(Path.Combine(_workspace, "game.extracted", "foo.rom")).ShouldBe(archiveBytes);
        File.Exists(Path.Combine(_workspace, "game.zip")).ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_CollisionSuffixAlsoTaken_ProbesUntilUnusedDirectory()
    {
        // Both the archive's base name (a staged extensionless FILE here) and the first suffix
        // candidate are taken: extraction probes to game.extracted-2 and touches neither.
        var archiveBytes = IncompressibleBytes(2048, seed: 1);
        var stagedFileBytes = IncompressibleBytes(1024, seed: 2);
        var stagedDirBytes = IncompressibleBytes(1024, seed: 3);
        WriteZip(Path.Combine(_workspace, "game.zip"), ("foo.rom", archiveBytes));
        File.WriteAllBytes(Path.Combine(_workspace, "game"), stagedFileBytes);
        Directory.CreateDirectory(Path.Combine(_workspace, "game.extracted"));
        File.WriteAllBytes(Path.Combine(_workspace, "game.extracted", "keep.rom"), stagedDirBytes);

        await CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None);

        File.ReadAllBytes(Path.Combine(_workspace, "game")).ShouldBe(stagedFileBytes);
        Directory.EnumerateFiles(Path.Combine(_workspace, "game.extracted"))
            .Select(Path.GetFileName).ShouldBe(["keep.rom"]);
        File.ReadAllBytes(Path.Combine(_workspace, "game.extracted-2", "foo.rom")).ShouldBe(archiveBytes);
    }

    [Fact]
    public async Task Extract_ArchiveWithinLimits_ExtractsAndRemovesArchive()
    {
        var content = IncompressibleBytes(4096);
        WriteZip(Path.Combine(_workspace, "game.zip"), ("game.rom", content));

        await CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None);

        File.Exists(Path.Combine(_workspace, "game.zip")).ShouldBeFalse();
        File.ReadAllBytes(Path.Combine(_workspace, "game", "game.rom")).ShouldBe(content);
    }

    [Fact]
    public async Task Extract_FreeSpaceBelowFloor_FailsBeforeExtracting()
    {
        WriteZip(Path.Combine(_workspace, "game.zip"), ("game.rom", IncompressibleBytes(64, seed: 7)));
        _driveInfo.AvailableFreeSpace.Returns(_defaultLimits.SpaceFloorBytes - 1);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("Insufficient disk space");
        File.Exists(Path.Combine(_workspace, "game.zip")).ShouldBeTrue();
    }

    [Fact]
    public async Task Extract_JobWideEntryBudget_FailsWhenArchiveDeclaresMoreThanRemaining()
    {
        // Reviewer's F5 reproduction: per-archive limits alone let N archives multiply the import
        // ceiling. With a job-wide budget of 10 entries, three 6-entry archives must fail at the
        // second one instead of extracting 18 files.
        foreach (string name in new[] { "a.zip", "b.zip", "c.zip" })
        {
            WriteZip(
                Path.Combine(_workspace, name),
                Enumerable.Range(0, 6).Select(i => ($"{i}.rom", IncompressibleBytes(64, seed: i))).ToArray());
        }
        var step = CreateStep(new ArchiveExtractionLimits { MaxTotalEntries = 10 });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("extraction budget");
        // Exactly one archive was extracted before the budget refused the second.
        Directory.EnumerateDirectories(_workspace).Count().ShouldBe(1);
        Directory.EnumerateFiles(_workspace, "*.zip").Count().ShouldBe(2);
    }

    [Fact]
    public async Task Extract_NestedArchive_InheritsShrunkJobWideBudget()
    {
        // The budget must carry across extraction rounds: the outer archive consumes 6 entries
        // (5 roms + inner.zip), leaving 2, so the nested archive's declared 5 entries must be
        // refused on its own round.
        string innerPath = Path.Combine(_workspace, "inner.zip");
        WriteZip(
            innerPath,
            Enumerable.Range(0, 5).Select(i => ($"n{i}.rom", IncompressibleBytes(64, seed: 10 + i))).ToArray());
        byte[] innerBytes = File.ReadAllBytes(innerPath);
        File.Delete(innerPath);
        WriteZip(
            Path.Combine(_workspace, "outer.zip"),
            Enumerable.Range(0, 5)
                .Select(i => ($"o{i}.rom", IncompressibleBytes(64, seed: 20 + i)))
                .Append(("inner.zip", innerBytes))
                .ToArray());
        var step = CreateStep(new ArchiveExtractionLimits { MaxTotalEntries = 8 });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("inner.zip");
        ex.Message.ShouldContain("extraction budget");
        // The outer round ran within budget; the nested archive was refused before extraction.
        File.Exists(Path.Combine(_workspace, "outer.zip")).ShouldBeFalse();
        File.Exists(Path.Combine(_workspace, "outer", "inner.zip")).ShouldBeTrue();
    }

    [Fact]
    public async Task Extract_StagedFilesReduceEntryBudget_FailsWhenArchiveDeclaresMoreThanRemainder()
    {
        // Reviewer's F1''' reproduction: MaxTotalEntries is a WORKSPACE ceiling, so content
        // already staged must charge it up front. Eight staged files plus the archive itself
        // leave 1 of 10 entries — an archive declaring 5 must be refused instead of nearly
        // doubling the workspace entry count past the ceiling.
        for (int i = 0; i < 8; i++)
        {
            File.WriteAllBytes(Path.Combine(_workspace, $"staged{i}.rom"), IncompressibleBytes(64, seed: 30 + i));
        }
        WriteZip(
            Path.Combine(_workspace, "game.zip"),
            Enumerable.Range(0, 5).Select(i => ($"{i}.rom", IncompressibleBytes(64, seed: 40 + i))).ToArray());
        var step = CreateStep(new ArchiveExtractionLimits { MaxTotalEntries = 10 });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("game.zip");
        ex.Message.ShouldContain("extraction budget");
        // Refused at the declared-totals gate: the archive is intact and nothing was extracted.
        File.Exists(Path.Combine(_workspace, "game.zip")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_workspace, "game")).ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_StagedContentAtCeiling_FailsOnFirstArchive()
    {
        // Staged content alone meets the ceiling (2 files + the archive = MaxTotalEntries), so
        // the entry budget opens at zero and the first archive must fail immediately.
        File.WriteAllBytes(Path.Combine(_workspace, "staged0.rom"), IncompressibleBytes(64, seed: 50));
        File.WriteAllBytes(Path.Combine(_workspace, "staged1.rom"), IncompressibleBytes(64, seed: 51));
        WriteZip(Path.Combine(_workspace, "game.zip"), ("game.rom", IncompressibleBytes(64, seed: 52)));
        var step = CreateStep(new ArchiveExtractionLimits { MaxTotalEntries = 3 });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("game.zip");
        ex.Message.ShouldContain("extraction budget");
        File.Exists(Path.Combine(_workspace, "game.zip")).ShouldBeTrue();
    }

    [Fact]
    public async Task Extract_StagedDirectoriesChargeEntryCeiling()
    {
        // Directories are staged inodes too: an empty staged directory plus the archive leave
        // 6 - 2 = 4 budget entries, so an archive declaring 5 is refused at the declared-totals
        // gate. If only files counted, extraction would start and fail mid-write, leaving a
        // partial extraction directory behind.
        Directory.CreateDirectory(Path.Combine(_workspace, "staged-dir"));
        WriteZip(
            Path.Combine(_workspace, "game.zip"),
            Enumerable.Range(0, 5).Select(i => ($"{i}.rom", IncompressibleBytes(64, seed: 60 + i))).ToArray());
        var step = CreateStep(new ArchiveExtractionLimits { MaxTotalEntries = 6 });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("game.zip");
        ex.Message.ShouldContain("extraction budget");
        Directory.Exists(Path.Combine(_workspace, "game")).ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_StagedContentPlusExtractionExactlyAtCeiling_Succeeds()
    {
        // The reduced budget is exact, not padded: 3 staged files + the archive leave 6 of 10
        // entries and the archive consumes exactly 6 (extraction directory + 5 files).
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllBytes(Path.Combine(_workspace, $"staged{i}.rom"), IncompressibleBytes(64, seed: 70 + i));
        }
        WriteZip(
            Path.Combine(_workspace, "game.zip"),
            Enumerable.Range(0, 5).Select(i => ($"{i}.rom", IncompressibleBytes(64, seed: 80 + i))).ToArray());
        var step = CreateStep(new ArchiveExtractionLimits { MaxTotalEntries = 10 });

        await step.ExtractRecursivelyAsync(_workspace, CancellationToken.None);

        File.Exists(Path.Combine(_workspace, "game.zip")).ShouldBeFalse();
        Directory.EnumerateFiles(Path.Combine(_workspace, "game")).Count().ShouldBe(5);
    }

    [Fact]
    public async Task Extract_ArchiveWithCollidingEntryPaths_FailsWithNamedCollisionError()
    {
        // Reviewer's F2''' reproduction: duplicate entry names normalize to one extracted path;
        // the archive must be rejected with the named collision error instead of the second
        // entry silently overwriting the first's bytes.
        WriteZip(
            Path.Combine(_workspace, "game.zip"),
            ("payload.rom", IncompressibleBytes(64, seed: 90)),
            ("payload.rom", IncompressibleBytes(64, seed: 91)));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("game.zip");
        ex.Message.ShouldContain("payload.rom");
        ex.Message.ShouldContain("same extracted path");
    }

    [Fact]
    public async Task Extract_ParentComponentCollision_ReportsLogicalEntryWithoutWorkspacePath()
    {
        WriteZip(
            Path.Combine(_workspace, "game.zip"),
            ("a", IncompressibleBytes(64, seed: 92)),
            ("a/b.rom", IncompressibleBytes(64, seed: 93)));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateStep().ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("a/b.rom");
        ex.Message.ShouldNotContain(_workspace);
    }

    [Fact]
    public async Task Extract_LyingExtractorOverdrawsEntryBudget_StopsAtBudgetBoundary()
    {
        // Declared totals are advisory; the budget must be charged by actual output as it is
        // created. Lying enumeration is not constructible with real zips, so a stub extractor
        // declares 1 entry but tries to write 3 files against the 2 entries remaining after the
        // staged archive charged the ceiling — extraction must stop at the boundary (one file:
        // the extraction directory consumed the other entry) instead of landing all 3 files and
        // detecting the overdraw afterwards.
        File.WriteAllBytes(Path.Combine(_workspace, "liar.lie"), IncompressibleBytes(128, seed: 5));
        var step = CreateStep(
            new ArchiveExtractionLimits { MaxTotalEntries = 3 },
            additionalExtractor: new LyingArchiveExtractor());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("liar.lie");
        ex.Message.ShouldContain("extraction budget");
        Directory.EnumerateFiles(Path.Combine(_workspace, "liar")).Count().ShouldBe(1);
    }

    [Fact]
    public async Task Extract_LyingExtractorOverdrawsByteBudget_StopsWritingAtBudgetBoundary()
    {
        // The byte budget must be enforced AS bytes are written, not after extraction completes:
        // an extractor declaring 64 bytes but writing 3 x 256 in 32-byte chunks against a
        // 100-byte budget must halt at the boundary instead of filling the volume first.
        const long byteBudget = 100;
        const int chunkBytes = 32;
        var limits = new ArchiveExtractionLimits { SpaceFloorBytes = 1_024 };
        _driveInfo.AvailableFreeSpace.Returns(limits.SpaceFloorBytes + byteBudget);
        File.WriteAllBytes(Path.Combine(_workspace, "liar.lie"), IncompressibleBytes(128, seed: 5));
        var step = CreateStep(
            limits,
            additionalExtractor: new LyingArchiveExtractor(
                declaredUncompressedBytes: 64,
                actualFileCount: 3,
                actualFileBytes: 256,
                writeChunkBytes: chunkBytes));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("liar.lie");
        ex.Message.ShouldContain("extraction budget");
        long bytesOnDisk = Directory
            .EnumerateFiles(Path.Combine(_workspace, "liar"), "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        bytesOnDisk.ShouldBeLessThanOrEqualTo(byteBudget + chunkBytes);
    }

    [Fact]
    public async Task Extract_DirectoryEntriesChargeEntryBudgetAcrossArchives_FailsWithNamedError()
    {
        // Directory entries consume inodes and must reduce the job-wide entry budget just like
        // files. With a budget of 10, two 6-directory archives must fail at the second one
        // instead of creating 12 directories.
        (string, byte[])[] directoryEntries = Enumerable.Range(0, 6)
            .Select(i => ($"d{i}/", Array.Empty<byte>()))
            .ToArray();
        WriteZip(Path.Combine(_workspace, "a.zip"), directoryEntries);
        WriteZip(Path.Combine(_workspace, "b.zip"), directoryEntries);
        var step = CreateStep(new ArchiveExtractionLimits { MaxTotalEntries = 10 });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("b.zip");
        ex.Message.ShouldContain("extraction budget");
        File.Exists(Path.Combine(_workspace, "b.zip")).ShouldBeTrue();
    }

    [Fact]
    public async Task Extract_ImplicitParentDirectories_ChargeEntryBudget()
    {
        // A file entry at a/b/c/file.rom creates three implicit parent directories plus the
        // extraction root — inodes that must charge the budget alongside the file, so a 3-entry
        // budget is exceeded before the file is written.
        WriteZip(Path.Combine(_workspace, "game.zip"), ("a/b/c/file.rom", IncompressibleBytes(64, seed: 8)));
        var step = CreateStep(new ArchiveExtractionLimits { MaxTotalEntries = 3 });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => step.ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("game.zip");
        ex.Message.ShouldContain("extraction budget");
    }

    [Fact]
    public async Task Extract_FreeSpaceShrinksBetweenArchives_ReclampsByteBudgetToCurrentSpace()
    {
        // The byte budget captured at job start must not survive another job consuming the
        // volume: free space shrinks between archive 1 and archive 2 (still above the floor), so
        // archive 2's effective budget must clamp to current free space minus the floor and
        // refuse a formerly-affordable archive.
        var limits = new ArchiveExtractionLimits { SpaceFloorBytes = 1_000 };
        var contentA = IncompressibleBytes(4096, seed: 11);
        var contentB = IncompressibleBytes(4096, seed: 12);
        WriteZip(Path.Combine(_workspace, "a.zip"), ("a.rom", contentA));
        WriteZip(Path.Combine(_workspace, "b.zip"), ("b.rom", contentB));
        _driveInfo.AvailableFreeSpace.Returns(
            1_000_000, 1_000_000, limits.SpaceFloorBytes + contentB.Length - 1);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateStep(limits).ExtractRecursivelyAsync(_workspace, CancellationToken.None));

        ex.Message.ShouldContain("b.zip");
        ex.Message.ShouldContain("extraction space budget");
        // Archive 1 extracted normally against the un-shrunk budget.
        File.ReadAllBytes(Path.Combine(_workspace, "a", "a.rom")).ShouldBe(contentA);
        File.Exists(Path.Combine(_workspace, "b.zip")).ShouldBeTrue();
    }

    [Fact]
    public async Task Extract_ArchiveAppearingAtRootMidRun_IsNotRescannedOrExtracted()
    {
        // The workspace root is scanned exactly once; each later round scans only the extraction
        // directories created by the previous round. An archive that appears at the root mid-run
        // is therefore never picked up — proving the whole-workspace rescans are gone.
        File.WriteAllBytes(Path.Combine(_workspace, "seed.plant"), IncompressibleBytes(64, seed: 6));
        var step = CreateStep(additionalExtractor: new RootPlantingArchiveExtractor(_workspace));

        await step.ExtractRecursivelyAsync(_workspace, CancellationToken.None);

        File.Exists(Path.Combine(_workspace, "planted.zip")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_workspace, "planted")).ShouldBeFalse();
    }

    /// <summary>Extractor whose enumeration under-reports the output it actually writes.</summary>
    private sealed class LyingArchiveExtractor(
        long declaredUncompressedBytes = 1,
        int actualFileCount = 3,
        int actualFileBytes = 64,
        int writeChunkBytes = 64) : IArchiveExtractor
    {
        public IReadOnlyList<string> SupportedExtensions => [".lie"];

        public bool CanHandle(string filename) =>
            filename.EndsWith(".lie", StringComparison.OrdinalIgnoreCase);

        public async IAsyncEnumerable<ArchiveEntry> EnumerateAsync(
            Stream archiveStream,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ArchiveEntry(
                "declared.rom",
                CompressedSize: 1,
                UncompressedSize: declaredUncompressedBytes,
                IsDirectory: false);
            await Task.CompletedTask;
        }

        public Task<Stream> ExtractAsync(
            Stream archiveStream, string entryPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyList<ExtractedEntry>> ExtractAllAsync(
            Stream archiveStream,
            string targetDirectory,
            IExtractionQuota quota,
            CancellationToken cancellationToken = default)
        {
            var chunk = new byte[writeChunkBytes];
            var entries = new List<ExtractedEntry>();
            for (int i = 0; i < actualFileCount; i++)
            {
                if (!quota.TryChargeEntry())
                {
                    throw new ExtractionQuotaExceededException("Entry budget exhausted.");
                }

                string path = Path.Combine(targetDirectory, $"actual{i}.rom");
                await using var output = new QuotaEnforcingStream(File.Create(path), quota);
                for (int written = 0; written < actualFileBytes; written += writeChunkBytes)
                {
                    int count = Math.Min(writeChunkBytes, actualFileBytes - written);
                    await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
                }

                entries.Add(new ExtractedEntry($"actual{i}.rom", path, Size: actualFileBytes, IsDirectory: false));
            }

            return entries;
        }
    }

    /// <summary>Extractor that plants a new zip at the workspace root while extracting.</summary>
    private sealed class RootPlantingArchiveExtractor(string workspaceRoot) : IArchiveExtractor
    {
        public IReadOnlyList<string> SupportedExtensions => [".plant"];

        public bool CanHandle(string filename) =>
            filename.EndsWith(".plant", StringComparison.OrdinalIgnoreCase);

        public async IAsyncEnumerable<ArchiveEntry> EnumerateAsync(
            Stream archiveStream,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ArchiveEntry("payload.rom", CompressedSize: 8, UncompressedSize: 8, IsDirectory: false);
            await Task.CompletedTask;
        }

        public Task<Stream> ExtractAsync(
            Stream archiveStream, string entryPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyList<ExtractedEntry>> ExtractAllAsync(
            Stream archiveStream,
            string targetDirectory,
            IExtractionQuota quota,
            CancellationToken cancellationToken = default)
        {
            quota.TryChargeEntry().ShouldBeTrue();
            string payloadPath = Path.Combine(targetDirectory, "payload.rom");
            await using (var output = new QuotaEnforcingStream(File.Create(payloadPath), quota))
            {
                await output.WriteAsync(new byte[8], cancellationToken);
            }

            WriteZip(Path.Combine(workspaceRoot, "planted.zip"), ("p.rom", new byte[8]));
            return [new ExtractedEntry("payload.rom", payloadPath, Size: 8, IsDirectory: false)];
        }
    }
}
