using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Import;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class ImportSourceEnumeratorTests : IDisposable
{
    private readonly string _allowedRoot;
    private readonly string _sourceRoot;
    private readonly string _tempRoot;

    public ImportSourceEnumeratorTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("romd-importenum-").FullName;
        _allowedRoot = Path.Combine(_tempRoot, "allowed");
        _sourceRoot = Path.Combine(_allowedRoot, "source");
        Directory.CreateDirectory(_sourceRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private ImportSourceEnumerator CreateEnumerator()
    {
        var options = Substitute.For<IRomdOptions>();
        options.AllowedImportPaths.Returns([_allowedRoot]);
        return new ImportSourceEnumerator(new PathValidator(options));
    }

    [Fact]
    public async Task Enumerate_SymlinkDirectoryLoop_TerminatesAndSkipsTheLink()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "real"));
        File.WriteAllText(Path.Combine(_sourceRoot, "real", "game.rom"), "content");
        try
        {
            // A loop: source/real/loop -> source, so naive recursion would never terminate.
            Directory.CreateSymbolicLink(Path.Combine(_sourceRoot, "real", "loop"), _sourceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Skip: symlink creation is not permitted on this platform.
        }

        var enumerator = CreateEnumerator();

        // Termination is by design (links are rejected before descent and every entry counts
        // against the cap); the timeout guards the hang this test exists to prevent.
        var result = await Task.Run(() => enumerator.Enumerate(_sourceRoot, 1_000, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(30));

        result.IsError.ShouldBeFalse();
        result.Value.Files.ShouldHaveSingleItem().RelativePath
            .ShouldBe(Path.Combine("real", "game.rom"));
        result.Value.Skipped.ShouldContain(entry =>
            entry.Path.EndsWith("loop") && entry.Reason.Contains("symbolic link"));
    }

    [Fact]
    public void Enumerate_ObservedEntriesExceedCap_ReturnsTooManyFiles()
    {
        for (int i = 0; i < 4; i++)
        {
            File.WriteAllText(Path.Combine(_sourceRoot, $"game-{i}.rom"), "content");
        }

        var result = CreateEnumerator().Enumerate(_sourceRoot, 3, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.TooManyFiles");
    }

    [Fact]
    public void Enumerate_DirectoriesCountAgainstCap_ReturnsTooManyFiles()
    {
        // The cap counts EVERY observed entry — directories included — so a tree that fans out
        // into directories (or a non-link cycle the validator cannot see) still terminates.
        for (int i = 0; i < 4; i++)
        {
            Directory.CreateDirectory(Path.Combine(_sourceRoot, $"dir-{i}"));
        }

        var result = CreateEnumerator().Enumerate(_sourceRoot, 3, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.TooManyFiles");
    }

    [Fact]
    public void Enumerate_NestedTree_PreservesRelativePathsAcrossLevels()
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "top.rom"), "top");
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "a", "b"));
        File.WriteAllText(Path.Combine(_sourceRoot, "a", "mid.rom"), "mid");
        File.WriteAllText(Path.Combine(_sourceRoot, "a", "b", "deep.rom"), "deep");

        var result = CreateEnumerator().Enumerate(_sourceRoot, 1_000, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Value.Files.Select(file => file.RelativePath)
            .ShouldBe(
                ["top.rom", Path.Combine("a", "mid.rom"), Path.Combine("a", "b", "deep.rom")],
                ignoreOrder: true);
        result.Value.Skipped.ShouldBeEmpty();
    }
}
